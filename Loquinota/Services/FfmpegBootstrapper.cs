using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using FFMpegCore;
using Loquinota.Helpers;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Downloader;

namespace Loquinota.Services;

/// <summary>
/// Finds or downloads FFmpeg binaries. Implemented by the MP4 workstream.
/// Search order:
///  1. AppContext.BaseDirectory\ffmpeg\ffmpeg.exe
///  2. %LOCALAPPDATA%\Loquinota\ffmpeg\ffmpeg.exe
///  3. ffmpeg.exe on PATH
///  4. Xabe.FFmpeg.Downloader -> app data folder
///  5. Manual HTTP download fallback (gyan.dev / BtbN zip) with progress
/// </summary>
public static class FfmpegBootstrapper
{
    private const string FfmpegFileName = "ffmpeg.exe";
    private const string FfprobeFileName = "ffprobe.exe";
    private const string EssentialsZipUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

    /// <summary>Serialises downloads so concurrent callers wait for a single download.</summary>
    private static readonly SemaphoreSlim DownloadGate = new(1, 1);

    private static readonly HttpClient Http = CreateHttpClient();

    /// <summary>Last path resolved by <see cref="EnsureAsync"/> (avoids re-probing on every call).</summary>
    private static string? _resolvedPath;

    /// <summary>Returns an existing ffmpeg.exe path, or null.</summary>
    public static string? FindExisting()
    {
        // 1. Shipped next to the app (packaged or copied next to Loquinota.exe).
        var bundled = Path.Combine(AppContext.BaseDirectory, "ffmpeg", FfmpegFileName);
        if (File.Exists(bundled))
        {
            return Path.GetFullPath(bundled);
        }

        // 2. Previously downloaded into the app data folder.
        var downloaded = Path.Combine(AppStorage.FfmpegDirectory, FfmpegFileName);
        if (File.Exists(downloaded))
        {
            return Path.GetFullPath(downloaded);
        }

        // 3. Somewhere on PATH.
        return FindOnPath();
    }

    /// <summary>Ensures ffmpeg.exe exists and returns its full path.</summary>
    public static async Task<string> EnsureAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Fast path: already resolved by an earlier call.
        var cached = _resolvedPath;
        if (cached is not null && File.Exists(cached))
        {
            ConfigureFfmpegCore(cached);
            return cached;
        }

        var existing = FindExisting();
        if (existing is not null)
        {
            _resolvedPath = existing;
            ConfigureFfmpegCore(existing);
            progress?.Report($"FFmpeg found: {existing}");
            return existing;
        }

        // Slow path: download exactly once even under concurrent calls.
        await DownloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = _resolvedPath;
            if (cached is not null && File.Exists(cached))
            {
                ConfigureFfmpegCore(cached);
                return cached;
            }

            existing = FindExisting();
            if (existing is not null)
            {
                _resolvedPath = existing;
                ConfigureFfmpegCore(existing);
                progress?.Report($"FFmpeg found: {existing}");
                return existing;
            }

            Directory.CreateDirectory(AppStorage.FfmpegDirectory);

            progress?.Report("FFmpeg was not found. Downloading it now (~80–115 MB, one time only)...");

            var downloaded = await TryXabeDownloadAsync(progress, cancellationToken).ConfigureAwait(false)
                             ?? await DownloadEssentialsAsync(progress, cancellationToken).ConfigureAwait(false);

            var version = await TryGetVersionAsync(downloaded, cancellationToken).ConfigureAwait(false);
            ConfigureFfmpegCore(downloaded);
            _resolvedPath = downloaded;

            var sizeMb = new FileInfo(downloaded).Length / (1024.0 * 1024.0);
            progress?.Report($"FFmpeg ready ({sizeMb:0.0} MB): {version ?? Path.GetFileName(downloaded)}");
            Log.Info($"FFmpeg ready: {downloaded} ({version ?? "version unknown"})");
            return downloaded;
        }
        finally
        {
            DownloadGate.Release();
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true
        };

        var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Loquinota/1.0");
        return client;
    }

    private static string? FindOnPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var directory = entry.Trim().Trim('"');
                if (directory.Length == 0)
                {
                    continue;
                }

                var candidate = Path.Combine(directory, FfmpegFileName);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch
            {
                // Malformed PATH entries must never break the lookup.
            }
        }

        return null;
    }

    /// <summary>
    /// Points FFMpegCore at the folder containing ffmpeg.exe (and ffprobe.exe, when shipped together).
    /// Safe to call repeatedly.
    /// </summary>
    private static void ConfigureFfmpegCore(string ffmpegPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(ffmpegPath));
            if (string.IsNullOrEmpty(directory))
            {
                return;
            }

            GlobalFFOptions.Configure(new FFOptions { BinaryFolder = directory });
        }
        catch (Exception ex)
        {
            Log.Warn("Could not configure FFMpegCore. Binary folder was not set.", ex);
        }
    }

    /// <summary>
    /// Attempt 1: Xabe.FFmpeg.Downloader (ffbinaries) into the app data folder.
    /// Returns the ffmpeg.exe path on success, or null so the caller can fall back.
    /// </summary>
    private static async Task<string?> TryXabeDownloadAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var directory = AppStorage.FfmpegDirectory;
        try
        {
            progress?.Report("Downloading FFmpeg via Xabe.FFmpeg.Downloader...");

            var lastReportedPercent = -10;
            var xabeProgress = new SyncProgress<ProgressInfo>(info =>
            {
                // Ignore trailing flush events that report a completed 0-byte transfer.
                if (info.TotalBytes <= 1024)
                {
                    return;
                }

                var percent = (int)(info.DownloadedBytes * 100L / info.TotalBytes);
                if (percent == lastReportedPercent)
                {
                    return;
                }

                if (percent >= lastReportedPercent + 10 || percent == 100)
                {
                    lastReportedPercent = percent;
                    progress?.Report(
                        $"Downloading FFmpeg (Xabe): {percent}% ({Megabytes(info.DownloadedBytes)} / {Megabytes(info.TotalBytes)} MB)");
                }
            });

            // The downloader has no cancellation token of its own; bound the wait instead of hanging forever.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(4));

            await FFmpegDownloader
                .GetLatestVersion(FFmpegVersion.Official, directory, xabeProgress)
                .WaitAsync(timeout.Token)
                .ConfigureAwait(false);

            var found = FindInDirectory(directory, FfmpegFileName);
            if (found is null)
            {
                Log.Warn("Xabe download finished but ffmpeg.exe was not present; falling back to manual download.");
                return null;
            }

            if (FindInDirectory(directory, FfprobeFileName) is null)
            {
                Log.Warn("Xabe download did not provide ffprobe.exe; media probing may be unavailable.");
            }

            // Verify the binary actually starts (guards against truncated/blocked downloads).
            if (await TryGetVersionAsync(found, cancellationToken).ConfigureAwait(false) is null)
            {
                Log.Warn("Xabe download produced an ffmpeg.exe that could not be executed; falling back to manual download.");
                return null;
            }

            progress?.Report("FFmpeg downloaded via Xabe.");
            return found;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Warn("Xabe FFmpeg download timed out; falling back to manual download.");
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn("Xabe FFmpeg download failed; falling back to manual download.", ex);
            return null;
        }
    }

    /// <summary>
    /// Attempt 2: download the official gyan.dev essentials build and extract ffmpeg.exe / ffprobe.exe.
    /// Throws a descriptive error when the fallback also fails.
    /// </summary>
    private static async Task<string> DownloadEssentialsAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var directory = AppStorage.FfmpegDirectory;
        Directory.CreateDirectory(directory);

        var zipPath = Path.Combine(Path.GetTempPath(), $"loquinota-ffmpeg-{Guid.NewGuid():N}.zip");
        try
        {
            progress?.Report("Downloading the official FFmpeg build (~115 MB) from gyan.dev...");
            await DownloadToFileAsync(EssentialsZipUrl, zipPath, progress, cancellationToken).ConfigureAwait(false);

            progress?.Report("Download complete. Extracting ffmpeg.exe and ffprobe.exe...");
            var (ffmpeg, ffprobe) = ExtractBinaries(zipPath, directory);

            if (ffmpeg is null || !File.Exists(ffmpeg))
            {
                throw new InvalidOperationException("The FFmpeg archive did not contain ffmpeg.exe.");
            }

            if (ffprobe is null || !File.Exists(ffprobe))
            {
                Log.Warn("The FFmpeg archive did not contain ffprobe.exe; media probing will be limited.");
            }

            var sizeBytes = new FileInfo(ffmpeg).Length;
            if (sizeBytes < 1024 * 1024)
            {
                TryDelete(ffmpeg);
                throw new InvalidOperationException("The downloaded ffmpeg.exe is unexpectedly small; the download may be corrupted.");
            }

            progress?.Report($"FFmpeg extracted to {directory}.");
            return ffmpeg;
        }
        catch (OperationCanceledException)
        {
            progress?.Report("FFmpeg download cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"Manual FFmpeg download failed. Destination: {directory}", ex);
            throw new InvalidOperationException(
                "Could not download FFmpeg automatically. Check the internet connection, or install FFmpeg manually " +
                $"(place ffmpeg.exe and ffprobe.exe in '{directory}' or add them to PATH) and try again. " +
                $"Details: {ex.Message}",
                ex);
        }
        finally
        {
            TryDelete(zipPath);
        }
    }

    private static async Task DownloadToFileAsync(string url, string destinationPath, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        using var response = await Http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);

        var buffer = new byte[81920];
        long received = 0;
        var nextReport = 2L * 1024 * 1024;

        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;

            if (received >= nextReport)
            {
                nextReport = received + (2L * 1024 * 1024);
                ReportDownload(progress, received, totalBytes);
            }
        }

        ReportDownload(progress, received, totalBytes);
    }

    private static void ReportDownload(IProgress<string>? progress, long received, long total)
    {
        if (progress is null)
        {
            return;
        }

        progress.Report(total > 0
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"Downloading FFmpeg: {received * 100.0 / total:0}% ({Megabytes(received)} / {Megabytes(total)} MB)")
            : string.Create(CultureInfo.InvariantCulture, $"Downloading FFmpeg: {Megabytes(received)} MB"));
    }

    private static (string? Ffmpeg, string? Ffprobe) ExtractBinaries(string zipPath, string destinationDirectory)
    {
        string? ffmpeg = null;
        string? ffprobe = null;

        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue; // directory entry
            }

            if (!string.Equals(entry.Name, FfmpegFileName, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(entry.Name, FfprobeFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var target = Path.Combine(destinationDirectory, entry.Name);
            entry.ExtractToFile(target, overwrite: true);

            if (string.Equals(entry.Name, FfmpegFileName, StringComparison.OrdinalIgnoreCase))
            {
                ffmpeg = target;
            }
            else
            {
                ffprobe = target;
            }
        }

        return (ffmpeg, ffprobe);
    }

    private static string? FindInDirectory(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
    }

    private static async Task<string?> TryGetVersionAsync(string executablePath, CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = "-version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            // Read both pipes concurrently so a full pipe buffer can never deadlock the wait.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                return null;
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            var text = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;

            using var reader = new StringReader(text);
            var firstLine = reader.ReadLine();
            return string.IsNullOrWhiteSpace(firstLine) ? null : firstLine.Trim();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not run '{executablePath} -version'.", ex);
            return null;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort only.
        }
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
            // Best effort only.
        }
    }

    private static string Megabytes(long bytes)
        => (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>Synchronous <see cref="IProgress{T}"/> so download reports stay ordered.</summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public SyncProgress(Action<T> handler) => _handler = handler;

        public void Report(T value) => _handler(value);
    }
}
