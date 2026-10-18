using System.Diagnostics;
using System.Globalization;
using FFMpegCore;
using FFMpegCore.Enums;
using Loquinota.Helpers;
using Loquinota.Models;

namespace Loquinota.Services;

/// <summary>
/// Extracts a 16 kHz mono PCM16 WAV from an MP4 using FFmpeg (FFMpegCore + Xabe downloader).
///
/// Contract (verified against FFMpegCore 5.5.0 / Xabe.FFmpeg.Downloader 6.1.1):
///  - Use FFMpegArguments.FromFileInput(...) (there is no Create()).
///  - Output: WithAudioCodec("pcm_s16le"), WithAudioSamplingRate(16000),
///    DisableChannel(Channel.Video), WithCustomArgument("-ac 1").
///  - Probe metadata with FFProbe.AnalyseAsync (British spelling); result is IMediaAnalysis.
///  - GlobalFFOptions.Configure must point at the ffmpeg binary folder before use.
/// </summary>
public sealed class Mp4Processor : IAsyncDisposable
{
    /// <summary>Sample rate Whisper expects; the extracted WAV is always normalised to it.</summary>
    private const int TargetSampleRate = 16000;

    private const int TargetBitsPerSample = 16;

    private const int TargetChannels = 1;

    private static readonly string[] SupportedExtensions =
    {
        ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".flv", ".mpg", ".mpeg", ".ts"
    };

    /// <summary>Set when an ffmpeg binary could be located during construction.</summary>
    public bool IsFfmpegAvailable { get; private set; }

    public string? FfmpegPath { get; private set; }

    public Mp4Processor()
    {
        try
        {
            var existing = FfmpegBootstrapper.FindExisting();
            if (existing is not null)
            {
                FfmpegPath = existing;
                IsFfmpegAvailable = true;
                ConfigureFfmpegCore(existing);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("FFmpeg discovery failed while creating Mp4Processor.", ex);
        }
    }

    /// <summary>
    /// Locates ffmpeg (AppContext.BaseDirectory\ffmpeg, then app data, then PATH), downloads it
    /// via Xabe.FFmpeg.Downloader if missing, configures FFMpegCore and returns the binary path.
    /// Must surface a clear error if the download fails; manual path is documented in README.
    /// </summary>
    public async Task<string> EnsureFfmpegAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var path = await FfmpegBootstrapper.EnsureAsync(progress, cancellationToken).ConfigureAwait(false);
        FfmpegPath = path;
        IsFfmpegAvailable = true;
        return path;
    }

    /// <summary>Returns the media duration, or null when it cannot be probed.</summary>
    public async Task<TimeSpan?> ProbeDurationAsync(string mp4Path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mp4Path))
        {
            return null;
        }

        try
        {
            if (!File.Exists(mp4Path))
            {
                Log.Warn($"Cannot probe duration: file not found '{mp4Path}'.");
                return null;
            }

            // ffprobe lives next to ffmpeg.exe; make sure FFMpegCore knows where that folder is.
            await EnsureFfmpegAsync(null, cancellationToken).ConfigureAwait(false);

            var analysis = await FFProbe
                .AnalyseAsync(mp4Path, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var duration = analysis.Duration;
            if (duration <= TimeSpan.Zero)
            {
                Log.Warn($"FFprobe reported a non-positive duration ({duration}) for '{mp4Path}'.");
                return null;
            }

            Log.Info($"Probed '{Path.GetFileName(mp4Path)}': duration {FormatDuration(duration)}" +
                     $", audio streams {analysis.AudioStreams?.Count ?? 0}");
            return duration;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not probe duration for '{mp4Path}'.", ex);
            return null;
        }
    }

    /// <summary>Extracts the audio track to <paramref name="outputWavPath"/> and reports progress.</summary>
    public async Task<Mp4ExtractionResult> ExtractAudioAsync(
        string mp4Path,
        string outputWavPath,
        IProgress<ProcessingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateInput(mp4Path);
        ValidateOutputPath(outputWavPath);
        cancellationToken.ThrowIfCancellationRequested();

        var sourceFileName = Path.GetFileName(mp4Path);
        var fullOutputPath = Path.GetFullPath(outputWavPath);
        var outputDirectory = Path.GetDirectoryName(fullOutputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        progress?.Report(new ProcessingProgress { Fraction = 0, Status = "Preparing FFmpeg..." });
        await EnsureFfmpegAsync(null, cancellationToken).ConfigureAwait(false);

        progress?.Report(new ProcessingProgress { Fraction = 0.02, Status = $"Reading media info for {sourceFileName}..." });

        var probedDuration = await ProbeDurationAsync(mp4Path, cancellationToken).ConfigureAwait(false);
        var duration = probedDuration ?? TimeSpan.Zero;

        progress?.Report(new ProcessingProgress
        {
            Fraction = 0.05,
            Status = duration > TimeSpan.Zero
                ? $"Extracting audio from {sourceFileName} ({FormatDuration(duration)})..."
                : $"Extracting audio from {sourceFileName}..."
        });

        var stopwatch = Stopwatch.StartNew();
        var lastReportedFraction = -1.0;

        void ReportExtractionProgress(double percent)
        {
            var fraction = Math.Clamp(percent / 100.0, 0.0, 1.0);

            // Throttle: ffmpeg can emit progress hundreds of times for long files.
            if (fraction < 1.0 && fraction - lastReportedFraction < 0.005)
            {
                return;
            }

            lastReportedFraction = fraction;

            TimeSpan? remaining = null;
            if (duration > TimeSpan.Zero && fraction > 0.0 && fraction < 1.0)
            {
                remaining = TimeSpan.FromTicks((long)(duration.Ticks * (1.0 - fraction)));
            }

            progress?.Report(new ProcessingProgress
            {
                Fraction = fraction,
                Status = $"Extracting audio… {fraction:P0}",
                EstimatedRemaining = remaining
            });
        }

        try
        {
            var processor = FFMpegArguments
                .FromFileInput(mp4Path, verifyExists: true)
                .OutputToFile(fullOutputPath, overwrite: true, addArguments: options => options
                    .WithAudioCodec("pcm_s16le")
                    .WithAudioSamplingRate(TargetSampleRate)
                    .DisableChannel(Channel.Video)
                    .WithCustomArgument("-ac 1"))
                .WithLogLevel(FFMpegLogLevel.Warning);

            if (duration > TimeSpan.Zero)
            {
                processor = processor.NotifyOnProgress(ReportExtractionProgress, duration);
            }

            var succeeded = await processor
                .CancellableThrough(cancellationToken)
                .ProcessAsynchronously()
                .ConfigureAwait(false);

            stopwatch.Stop();

            if (!succeeded)
            {
                throw new InvalidOperationException($"FFmpeg failed to extract audio from '{sourceFileName}'.");
            }

            if (!File.Exists(fullOutputPath))
            {
                throw new InvalidOperationException($"FFmpeg reported success but no WAV file was produced for '{sourceFileName}'.");
            }

            var fileLength = new FileInfo(fullOutputPath).Length;
            if (fileLength <= 44)
            {
                throw new InvalidOperationException($"The extracted WAV for '{sourceFileName}' is empty or invalid ({fileLength} bytes).");
            }

            if (!TryReadWavFormat(fullOutputPath, out var channels, out var sampleRate, out var bitsPerSample, out var dataBytes))
            {
                throw new InvalidOperationException($"The file produced for '{sourceFileName}' is not a readable WAV file.");
            }

            if (channels != TargetChannels || sampleRate != TargetSampleRate || bitsPerSample != TargetBitsPerSample)
            {
                throw new InvalidOperationException(
                    $"FFmpeg produced an unexpected WAV format ({sampleRate} Hz, {channels} channel(s), {bitsPerSample}-bit); " +
                    "expected 16000 Hz mono PCM16.");
            }

            if (duration <= TimeSpan.Zero && dataBytes > 0)
            {
                var bytesPerSecond = (long)TargetSampleRate * TargetChannels * (TargetBitsPerSample / 8);
                duration = TimeSpan.FromSeconds(dataBytes / (double)bytesPerSecond);
            }

            progress?.Report(new ProcessingProgress
            {
                Fraction = 1.0,
                Status = $"Audio extracted ({FormatDuration(duration)}, 16 kHz mono PCM16).",
                EstimatedRemaining = TimeSpan.Zero
            });

            Log.Info(
                $"Extracted audio from '{sourceFileName}' -> '{fullOutputPath}' " +
                $"({FormatDuration(duration)}, {fileLength / 1024} KB, 16 kHz mono PCM16, {stopwatch.ElapsedMilliseconds} ms).");

            return new Mp4ExtractionResult
            {
                WavFilePath = fullOutputPath,
                Duration = duration,
                SourceFileName = sourceFileName
            };
        }
        catch (OperationCanceledException)
        {
            TryDelete(fullOutputPath);
            progress?.Report(new ProcessingProgress { Fraction = 0, Status = "Audio extraction cancelled." });
            throw;
        }
        catch (Exception)
        {
            // Never leave a half-written WAV behind: callers must be able to retry cleanly.
            TryDelete(fullOutputPath);
            throw;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static void ValidateInput(string mp4Path)
    {
        if (string.IsNullOrWhiteSpace(mp4Path))
        {
            throw new ArgumentException("A video file path is required.", nameof(mp4Path));
        }

        if (!File.Exists(mp4Path))
        {
            throw new FileNotFoundException("The selected video file could not be found.", mp4Path);
        }

        var extension = Path.GetExtension(mp4Path);
        if (!SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"Unsupported video format '{extension}'. Supported formats: " +
                string.Join(", ", SupportedExtensions) + ".");
        }
    }

    private static void ValidateOutputPath(string outputWavPath)
    {
        if (string.IsNullOrWhiteSpace(outputWavPath))
        {
            throw new ArgumentException("An output WAV path is required.", nameof(outputWavPath));
        }

        if (!string.Equals(Path.GetExtension(outputWavPath), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The output path must be a .wav file.", nameof(outputWavPath));
        }
    }

    private static void ConfigureFfmpegCore(string ffmpegPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(ffmpegPath));
            if (!string.IsNullOrEmpty(directory))
            {
                GlobalFFOptions.Configure(new FFOptions { BinaryFolder = directory });
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not point FFMpegCore at the discovered FFmpeg folder.", ex);
        }
    }

    /// <summary>
    /// Minimal RIFF/WAVE parser used to prove the extraction really is 16 kHz mono PCM16.
    /// ffmpeg writes a LIST chunk before "data", so a fixed 44-byte header cannot be assumed.
    /// </summary>
    private static bool TryReadWavFormat(
        string wavPath,
        out int channels,
        out int sampleRate,
        out int bitsPerSample,
        out long dataBytes)
    {
        channels = 0;
        sampleRate = 0;
        bitsPerSample = 0;
        dataBytes = 0;

        try
        {
            using var stream = File.OpenRead(wavPath);
            using var reader = new BinaryReader(stream);

            if (stream.Length < 44)
            {
                return false;
            }

            if (new string(reader.ReadChars(4)) != "RIFF")
            {
                return false;
            }

            _ = reader.ReadInt32(); // RIFF chunk size (unreliable for streamed files)

            if (new string(reader.ReadChars(4)) != "WAVE")
            {
                return false;
            }

            var haveFormat = false;
            while (stream.Position + 8 <= stream.Length)
            {
                var chunkId = new string(reader.ReadChars(4));
                var chunkSize = reader.ReadInt32();
                if (chunkSize < 0)
                {
                    return false;
                }

                if (chunkId == "fmt ")
                {
                    if (chunkSize < 16)
                    {
                        return false;
                    }

                    _ = reader.ReadUInt16(); // audio format (1 = PCM, 0xFFFE = extensible)
                    channels = reader.ReadUInt16();
                    sampleRate = reader.ReadInt32();
                    _ = reader.ReadInt32(); // byte rate
                    _ = reader.ReadUInt16(); // block align
                    bitsPerSample = reader.ReadUInt16();
                    haveFormat = true;

                    var consumed = 16;
                    stream.Position += chunkSize - consumed;
                }
                else if (chunkId == "data")
                {
                    dataBytes = chunkSize;
                    if (haveFormat)
                    {
                        return channels > 0 && sampleRate > 0 && bitsPerSample > 0;
                    }

                    // Keep scanning after the payload for a trailing fmt chunk (rare but legal).
                    stream.Position += chunkSize;
                }
                else
                {
                    stream.Position += chunkSize;
                }

                if ((chunkSize & 1) == 1 && stream.Position < stream.Length)
                {
                    stream.Position += 1; // chunks are word aligned
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not parse WAV header of '{wavPath}'.", ex);
            return false;
        }
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        return duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : duration.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup only.
        }
    }
}
