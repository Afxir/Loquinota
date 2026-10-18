using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Loquinota.Helpers;
using Loquinota.Models;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;

namespace Loquinota.Services;

/// <summary>
/// Wraps Whisper.net (WhisperFactory / WhisperProcessor) for fully offline speech-to-text.
///
/// Contract:
///  - <see cref="LoadModelAsync"/> loads a ggml/gguf model from disk. Safe to call multiple times;
///    disposing/replacing the previous processor is the implementation's job.
///  - <see cref="TranscribeWavAsync"/> converts the WAV to 16 kHz mono float and transcribes it,
///    reporting progress via segments processed.
///  - <see cref="CreateLiveSession"/> returns a streaming session that buffers incoming
///    16 kHz mono samples and transcribes chunks of >= 2 seconds, raising partial updates.
///  - All heavy work runs on background threads (Task.Run / async); never block the UI.
/// </summary>
public sealed class WhisperTranscriptionService : IAsyncDisposable
{
    /// <summary>Whisper expects 16 kHz mono float samples.</summary>
    public const int SampleRate = 16000;

    /// <summary>Threads handed to whisper.cpp: half the cores (keeps UI + audio threads free).</summary>
    private static readonly int WhisperThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

    /// <summary>
    /// Whisper is trained on noisy captions and emits markers such as "[BLANK_AUDIO]" or "♪"
    /// on silence/music. They are noise for our transcript UI, so they are stripped.
    /// </summary>
    private static readonly Regex NoiseMarkerRegex = new(
        @"\[\s*(?:blank[ _]?audio|blank|music|applause|silence|sound|noise|inaudible|no[ _]?speech|background[ _]?noise|crosstalk|laughter|foreign[ _]?language)\s*\]" +
        @"|\(\s*(?:silence|music|applause|laughter|inaudible)\s*\)" +
        @"|[♪♫]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RepeatedWhitespaceRegex = new(@"\s{2,}", RegexOptions.Compiled);

    /// <summary>Serializes every native whisper call: only one processing/loading operation at a time.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly object _stateLock = new();
    private readonly List<LiveTranscriptionSession> _liveSessions = new();

    private WhisperFactory? _factory;
    private bool _disposed;

    public bool IsReady { get; private set; }

    /// <summary>Full path of the loaded model file, or null.</summary>
    public string? ModelPath { get; private set; }

    /// <summary>Model short name (file name without extension), e.g. "base".</summary>
    public string ModelName { get; private set; } = "";

    public async Task LoadModelAsync(string modelPath, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(modelPath))
        {
            throw new ArgumentException("A model path is required.", nameof(modelPath));
        }

        var fullPath = Path.GetFullPath(modelPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Whisper model file not found: {fullPath}", fullPath);
        }

        var fileName = Path.GetFileName(fullPath);
        progress?.Report($"Loading speech model {fileName}…");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_factory is not null && IsReady &&
                string.Equals(ModelPath, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report($"Speech model already loaded: {ModelName}");
                return;
            }

            progress?.Report("Initialising the Whisper runtime…");
            var stopwatch = Stopwatch.StartNew();
            var factory = await Task.Run(() => WhisperFactory.FromPath(fullPath), cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            var previousFactory = _factory;
            _factory = factory;
            ModelPath = fullPath;
            ModelName = DeriveModelName(fullPath);
            IsReady = true;

            // The native model is memory-mapped/eagerly loaded; release the previous one.
            try
            {
                previousFactory?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn("Disposing the previous Whisper factory failed.", ex);
            }

            Log.Info($"Whisper model loaded: '{fullPath}' ({ModelName}) in {stopwatch.ElapsedMilliseconds} ms.");
            progress?.Report($"Speech model ready: {ModelName} (loaded in {stopwatch.ElapsedMilliseconds} ms)");
        }
        catch (OperationCanceledException)
        {
            progress?.Report("Model loading was cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            IsReady = _factory is not null;
            Log.Error($"Failed to load the Whisper model '{fullPath}'.", ex);
            progress?.Report($"Failed to load the speech model: {ex.Message}");
            throw new InvalidOperationException(
                $"Could not load the Whisper model '{fileName}'. The file may be corrupt or an unsupported model format. Details: {ex.Message}",
                ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TranscriptionResult> TranscribeWavAsync(string wavPath, IProgress<TranscriptionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(wavPath))
        {
            throw new ArgumentException("An audio file path is required.", nameof(wavPath));
        }

        var fullPath = Path.GetFullPath(wavPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Audio file not found: {fullPath}", fullPath);
        }

        EnsureReady();

        var fileName = Path.GetFileName(fullPath);
        progress?.Report(new TranscriptionProgress { Status = "Reading audio…" });

        // Decoding + resampling is CPU work: keep it off the caller's (UI) thread.
        var (samples, duration) = await Task.Run(() => ReadSamples(fullPath), cancellationToken).ConfigureAwait(false);
        if (samples.Length == 0)
        {
            throw new InvalidOperationException($"The audio file '{fileName}' does not contain any audio samples.");
        }

        progress?.Report(new TranscriptionProgress
        {
            Fraction = 0,
            Status = $"Transcribing {FormatTime(duration)} of audio…",
        });

        var segments = new List<TranscriptSegment>();
        var textBuilder = new StringBuilder();
        string? language = null;
        var stopwatch = Stopwatch.StartNew();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        WhisperProcessor? processor = null;
        try
        {
            EnsureReady();
            processor = CreateProcessor(language: null, prompt: null);

            await foreach (var segment in processor.ProcessAsync(samples, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var rawText = segment.Text ?? string.Empty;
                var cleaned = CleanText(rawText);
                language ??= segment.Language;

                if (cleaned.Length > 0)
                {
                    segments.Add(new TranscriptSegment
                    {
                        Start = segment.Start,
                        End = segment.End,
                        Text = cleaned,
                    });
                    textBuilder.Append(cleaned).Append(' ');
                }

                var fraction = duration.TotalSeconds > 0
                    ? Math.Clamp(segment.End.TotalSeconds / duration.TotalSeconds, 0, 0.99)
                    : 0;
                progress?.Report(new TranscriptionProgress
                {
                    Fraction = fraction,
                    Status = $"Transcribed {FormatTime(segment.End)} of {FormatTime(duration)}…",
                    EstimatedRemaining = EstimateRemaining(stopwatch.Elapsed, fraction),
                });
            }
        }
        finally
        {
            _gate.Release();
            if (processor is not null)
            {
                await processor.DisposeAsync().ConfigureAwait(false);
            }
        }

        stopwatch.Stop();

        var result = new TranscriptionResult
        {
            Text = CleanText(textBuilder.ToString()),
            Segments = segments,
            Language = language,
            Duration = duration,
            ModelName = ModelName,
        };

        Log.Info(
            $"Transcribed '{fileName}': {segments.Count} segment(s), {result.Text.Length} characters " +
            $"in {stopwatch.Elapsed.TotalSeconds:0.00}s using '{ModelName}'.");
        progress?.Report(new TranscriptionProgress
        {
            Fraction = 1,
            Status = $"Transcription complete ({segments.Count} segments in {stopwatch.Elapsed.TotalSeconds:0.0}s)",
            EstimatedRemaining = TimeSpan.Zero,
        });

        return result;
    }

    public LiveTranscriptionSession CreateLiveSession(LiveTranscriptionOptions? options = null)
    {
        ThrowIfDisposed();
        var session = new LiveTranscriptionSession(this, options ?? new LiveTranscriptionOptions());
        lock (_stateLock)
        {
            _liveSessions.Add(session);
        }

        return session;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Stop live sessions first: they own processors built from the factory we are about to free.
        LiveTranscriptionSession[] sessions;
        lock (_stateLock)
        {
            sessions = _liveSessions.ToArray();
            _liveSessions.Clear();
        }

        foreach (var session in sessions)
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn("Disposing a live transcription session failed.", ex);
            }
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var factory = _factory;
            _factory = null;
            IsReady = false;
            ModelPath = null;
            ModelName = "";
            try
            {
                factory?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn("Disposing the Whisper factory failed.", ex);
            }
        }
        finally
        {
            _gate.Release();
        }

        _gate.Dispose();
        Log.Info("WhisperTranscriptionService disposed.");
    }

    /// <summary>Called by a live session when it is disposed so the owner stops tracking it.</summary>
    internal void ForgetLiveSession(LiveTranscriptionSession session)
    {
        lock (_stateLock)
        {
            _liveSessions.Remove(session);
        }
    }

    /// <summary>Builds a processor on a background thread, serialized with any active transcription.</summary>
    internal async Task<WhisperProcessor> CreateProcessorAsync(string? language, string? prompt, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReady();
            return await Task.Run(() => CreateProcessor(language, prompt), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Transcribes one live audio chunk; serialized with all other whisper work.</summary>
    internal async Task<ChunkTranscription> TranscribeChunkAsync(WhisperProcessor processor, float[] samples, CancellationToken cancellationToken)
    {
        var textBuilder = new StringBuilder();
        var segments = new List<TranscriptSegment>();
        string? language = null;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReady();

            await foreach (var segment in processor.ProcessAsync(samples, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var rawText = segment.Text ?? string.Empty;
                var cleaned = CleanText(rawText);
                language ??= segment.Language;
                if (cleaned.Length > 0)
                {
                    segments.Add(new TranscriptSegment
                    {
                        Start = segment.Start,
                        End = segment.End,
                        Text = cleaned,
                    });
                    textBuilder.Append(cleaned).Append(' ');
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        return new ChunkTranscription(CleanText(textBuilder.ToString()), segments, language);
    }

    /// <summary>Creates a new Whisper processor. Caller must serialize access with other whisper calls.</summary>
    private WhisperProcessor CreateProcessor(string? language, string? prompt)
    {
        var factory = _factory ?? throw new InvalidOperationException("Load a Whisper model first.");
        var builder = factory.CreateBuilder().WithThreads(WhisperThreads);

        if (string.IsNullOrWhiteSpace(language) || string.Equals(language.Trim(), "auto", StringComparison.OrdinalIgnoreCase))
        {
            // Whisper.net defaults to English; "auto" must explicitly enable detection.
            builder = builder.WithLanguageDetection();
        }
        else
        {
            builder = builder.WithLanguage(language.Trim());
        }

        if (!string.IsNullOrWhiteSpace(prompt))
        {
            builder = builder.WithPrompt(prompt.Trim());
        }

        return builder.Build();
    }

    private void EnsureReady()
    {
        ThrowIfDisposed();
        if (!IsReady || _factory is null)
        {
            throw new InvalidOperationException("Load a Whisper model first.");
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WhisperTranscriptionService));
        }
    }

    private static string DeriveModelName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.StartsWith("ggml-", StringComparison.OrdinalIgnoreCase) ? name[5..] : name;
    }

    /// <summary>Strips Whisper's silence/music caption markers and normalises spacing.</summary>
    private static string CleanText(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var cleaned = NoiseMarkerRegex.Replace(raw, " ");
        cleaned = RepeatedWhitespaceRegex.Replace(cleaned, " ");
        return cleaned.Trim();
    }

    private static TimeSpan? EstimateRemaining(TimeSpan elapsed, double fraction)
    {
        if (fraction <= 0.02 || elapsed <= TimeSpan.Zero)
        {
            return null;
        }

        var estimate = elapsed.TotalSeconds * ((1 - fraction) / fraction);
        return estimate > 0 && estimate < TimeSpan.FromHours(10).TotalSeconds
            ? TimeSpan.FromSeconds(estimate)
            : null;
    }

    private static string FormatTime(TimeSpan value)
        => value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss")
            : value.ToString(@"m\:ss");

    /// <summary>
    /// Decodes any supported audio file (WAV via NAudio, everything else via Media Foundation)
    /// into 16 kHz mono float samples. Never assumes the source is already 16 kHz.
    /// </summary>
    private static (float[] Samples, TimeSpan Duration) ReadSamples(string path)
    {
        using var stream = OpenAudioStream(path);
        var sourceProvider = stream.ToSampleProvider();
        var sourceFormat = sourceProvider.WaveFormat;

        ISampleProvider monoProvider = sourceFormat.Channels == 1
            ? sourceProvider
            : new MonoDownmixSampleProvider(sourceProvider);

        ISampleProvider resampled = monoProvider.WaveFormat.SampleRate == SampleRate
            ? monoProvider
            : new WdlResamplingSampleProvider(monoProvider, SampleRate);

        // ~1 second read buffer at the target rate.
        var buffer = new float[SampleRate];
        var samples = new List<float>((int)Math.Min(int.MaxValue, Math.Max(SampleRate, stream.TotalTime.TotalSeconds * SampleRate)));

        int read;
        while ((read = resampled.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                samples.Add(buffer[i]);
            }
        }

        var result = samples.ToArray();
        return (result, TimeSpan.FromSeconds(result.Length / (double)SampleRate));
    }

    private static WaveStream OpenAudioStream(string path)
    {
        try
        {
            return new WaveFileReader(path);
        }
        catch (Exception wavError)
        {
            // Not a WAV we understand. Try Media Foundation (MP3/M4A/WMA/...) before giving up.
            try
            {
                return new MediaFoundationReader(path);
            }
            catch (Exception mediaError)
            {
                throw new InvalidOperationException(
                    $"Could not read the audio file '{Path.GetFileName(path)}'. " +
                    "Supported formats: WAV, MP3, M4A/AAC and WMA. " +
                    $"Details: {wavError.Message}",
                    new AggregateException(wavError, mediaError));
            }
        }
    }

    /// <summary>Downmixes any channel count to mono by averaging channels, frame-aligned.</summary>
    private sealed class MonoDownmixSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _channels;
        private float[] _scratch = Array.Empty<float>();

        public MonoDownmixSampleProvider(ISampleProvider source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _channels = source.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            if (_channels <= 1)
            {
                return _source.Read(buffer, offset, count);
            }

            var required = count * _channels;
            if (_scratch.Length < required)
            {
                _scratch = new float[required];
            }

            // Fill whole interleaved frames so channel alignment never drifts between reads.
            var filled = 0;
            while (filled < required)
            {
                var read = _source.Read(_scratch, filled, required - filled);
                if (read <= 0)
                {
                    break;
                }

                filled += read;
            }

            var frames = filled / _channels;
            var inverseChannels = 1f / _channels;
            for (var frame = 0; frame < frames; frame++)
            {
                var sum = 0f;
                var baseIndex = frame * _channels;
                for (var channel = 0; channel < _channels; channel++)
                {
                    sum += _scratch[baseIndex + channel];
                }

                buffer[offset + frame] = sum * inverseChannels;
            }

            return frames;
        }
    }
}

/// <summary>Result of transcribing a single live audio chunk.</summary>
internal sealed record ChunkTranscription(string Text, IReadOnlyList<TranscriptSegment> Segments, string? Language);
