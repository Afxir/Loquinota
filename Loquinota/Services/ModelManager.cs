using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Loquinota.Helpers;
using Loquinota.Models;

namespace Loquinota.Services;

/// <summary>
/// Manages local Whisper ggml model files (download, import, discovery).
///
/// Contract:
///  - Supported short names: tiny, base, small, medium.
///  - Download from https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-{name}.bin
///    with an HTTP fallback mirror; report bytes/fraction through <see cref="ModelDownloadProgress"/>.
///  - Files are stored in AppStorage.ModelsDirectory as ggml-{name}.bin.
///  - <see cref="ImportModelAsync"/> copies a user-selected .bin into the models folder.
///  - Downloads must be atomic (write .part then move) and cancellable.
/// </summary>
public sealed class ModelManager
{
    private const string PrimaryBaseUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main";
    private const string MirrorBaseUrl = "https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main";

    /// <summary>Smallest plausible ggml model file; guards against HTML/error-page downloads.</summary>
    private const long MinimumSaneModelBytes = 1L * 1024 * 1024;

    private const int CopyBufferSize = 1024 * 1024;

    private static readonly HttpClient Http = CreateHttpClient();

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Downloads =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, (string DisplayName, long ApproxSizeBytes)> Catalog =
        new Dictionary<string, (string, long)>(StringComparer.OrdinalIgnoreCase)
        {
            ["tiny"] = ("Tiny — fastest, ideal for live captions", 77_691_713L),
            ["base"] = ("Base — balanced speed and accuracy", 147_951_465L),
            ["small"] = ("Small — better accuracy, slower", 487_601_967L),
            ["medium"] = ("Medium — best accuracy, slowest", 1_533_774_188L),
        };

    public static IReadOnlyList<string> SupportedModels { get; } = new[] { "tiny", "base", "small", "medium" };

    public string ModelsDirectory => AppStorage.ModelsDirectory;

    /// <summary>All locally available model files (any *.bin/*.gguf in the models folder).</summary>
    public IReadOnlyList<ModelInfo> GetInstalledModels()
    {
        var installed = new List<ModelInfo>();
        try
        {
            var directory = ModelsDirectory;
            if (!Directory.Exists(directory))
            {
                return installed;
            }

            foreach (var path in Directory.EnumerateFiles(directory))
            {
                var extension = Path.GetExtension(path);
                if (!extension.Equals(".bin", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".gguf", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Ignore half-written downloads.
                if (path.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                long size;
                try
                {
                    size = new FileInfo(path).Length;
                }
                catch (IOException)
                {
                    continue;
                }

                if (size <= 0)
                {
                    continue;
                }

                var name = ShortNameFromFileName(Path.GetFileNameWithoutExtension(path));
                installed.Add(new ModelInfo
                {
                    Name = name,
                    DisplayName = Catalog.TryGetValue(name, out var known) ? known.DisplayName : name,
                    FilePath = path,
                    SizeBytes = size,
                    IsInstalled = true,
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not enumerate models in '{SafeModelsDirectory()}'.", ex);
        }

        return installed
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Known downloadable models with sizes and install state, for the model setup UI.</summary>
    public IReadOnlyList<ModelInfo> GetAvailableModels()
    {
        var installed = GetInstalledModels();
        var models = new List<ModelInfo>(SupportedModels.Count);

        foreach (var name in SupportedModels)
        {
            var known = Catalog[name];
            var match = installed.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
            models.Add(new ModelInfo
            {
                Name = name,
                DisplayName = known.DisplayName,
                FilePath = match?.FilePath ?? GetModelPath(name),
                SizeBytes = match?.SizeBytes ?? known.ApproxSizeBytes,
                IsInstalled = match is not null,
            });
        }

        return models;
    }

    public bool IsInstalled(string modelName)
    {
        var name = NormalizeModelName(modelName);
        if (name.Length == 0)
        {
            return false;
        }

        if (FileHasContent(Path.Combine(ModelsDirectory, $"ggml-{name}.bin")))
        {
            return true;
        }

        return GetInstalledModels().Any(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Expected/actual path of a model file: ModelsDirectory\ggml-{modelName}.bin.
    /// When a model is installed under another name (for example an imported .gguf),
    /// the actual existing file is returned so callers can load it directly.
    /// </summary>
    public string GetModelPath(string modelName)
    {
        var name = NormalizeModelName(modelName);
        if (name.Length == 0)
        {
            throw new ArgumentException("A model name is required.", nameof(modelName));
        }

        var expectedPath = Path.Combine(ModelsDirectory, $"ggml-{name}.bin");
        if (FileHasContent(expectedPath))
        {
            return expectedPath;
        }

        try
        {
            var installed = FindInstalled(name);
            if (installed is not null)
            {
                return installed.FilePath;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not resolve the path of model '{name}'.", ex);
        }

        return expectedPath;
    }

    /// <summary>Downloads the model when missing and returns its path.</summary>
    public async Task<string> EnsureModelAsync(string modelName, IProgress<ModelDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var name = NormalizeModelName(modelName);
        if (name.Length == 0)
        {
            throw new ArgumentException("A model name is required.", nameof(modelName));
        }

        if (!Catalog.ContainsKey(name))
        {
            throw new ArgumentException(
                $"Unknown model '{modelName}'. Supported models: {string.Join(", ", SupportedModels)}.",
                nameof(modelName));
        }

        if (IsInstalled(name))
        {
            var installed = FindInstalled(name);
            var existingPath = installed?.FilePath ?? GetModelPath(name);
            var existingSize = installed?.SizeBytes ?? 0;
            progress?.Report(new ModelDownloadProgress
            {
                Fraction = 1,
                BytesReceived = existingSize,
                TotalBytes = existingSize,
                Status = $"The {name} model is already installed.",
            });
            return existingPath;
        }

        // Serialize concurrent requests for the same model so the .part file cannot be clobbered.
        var gate = Downloads.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsInstalled(name))
            {
                return FindInstalled(name)?.FilePath ?? GetModelPath(name);
            }

            Directory.CreateDirectory(ModelsDirectory);
            var destination = GetModelPath(name);
            await DownloadModelAsync(name, destination, progress, cancellationToken).ConfigureAwait(false);
            return destination;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Copies a user-picked model file into the models folder and returns its path.</summary>
    public async Task<string> ImportModelAsync(string sourceFilePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceFilePath))
        {
            throw new ArgumentException("A source model file is required.", nameof(sourceFilePath));
        }

        var source = Path.GetFullPath(sourceFilePath);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException($"The selected model file does not exist: {source}", source);
        }

        var extension = Path.GetExtension(source);
        if (!extension.Equals(".bin", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Unsupported model file '{Path.GetFileName(source)}'. Choose a .bin or .gguf Whisper model (for example ggml-base.bin).");
        }

        var modelsRoot = Path.GetFullPath(ModelsDirectory);
        var sourceDirectory = Path.GetDirectoryName(source);
        if (sourceDirectory is not null &&
            string.Equals(Path.TrimEndingDirectorySeparator(sourceDirectory), Path.TrimEndingDirectorySeparator(modelsRoot), StringComparison.OrdinalIgnoreCase))
        {
            // Already in the models folder: nothing to copy.
            return source;
        }

        Directory.CreateDirectory(modelsRoot);

        var fileName = Path.GetFileName(source);
        if (!fileName.StartsWith("ggml-", StringComparison.OrdinalIgnoreCase))
        {
            fileName = "ggml-" + fileName;
        }

        var destination = Path.Combine(modelsRoot, fileName);
        var partPath = destination + ".part";

        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.Asynchronous))
            {
                await input.CopyToAsync(output, CopyBufferSize, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            var copied = new FileInfo(partPath).Length;
            if (copied <= 0)
            {
                throw new InvalidOperationException($"The selected model file '{Path.GetFileName(source)}' is empty.");
            }

            File.Move(partPath, destination, overwrite: true);
            Log.Info($"Imported Whisper model '{source}' -> '{destination}' ({copied / (1024.0 * 1024):0.0} MB).");
            return destination;
        }
        catch (OperationCanceledException)
        {
            TryDelete(partPath);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(partPath);
            if (ex is InvalidOperationException)
            {
                throw;
            }

            throw new InvalidOperationException($"Could not import the model file '{Path.GetFileName(source)}': {ex.Message}", ex);
        }
    }

    private async Task DownloadModelAsync(
        string name,
        string destination,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var partPath = destination + ".part";
        var urls = new[]
        {
            $"{PrimaryBaseUrl}/ggml-{name}.bin",
            $"{MirrorBaseUrl}/ggml-{name}.bin",
        };

        Exception? lastError = null;
        for (var attempt = 0; attempt < urls.Length; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = urls[attempt];
            var viaMirror = attempt > 0;
            try
            {
                progress?.Report(new ModelDownloadProgress
                {
                    Fraction = 0,
                    Status = viaMirror
                        ? $"Downloading the {name} model from the mirror…"
                        : $"Downloading the {name} model…",
                });

                Log.Info($"Downloading Whisper model '{name}' from {url}");
                await DownloadFileAsync(url, partPath, name, viaMirror, progress, cancellationToken).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();

                var downloadedBytes = new FileInfo(partPath).Length;
                if (downloadedBytes <= 0)
                {
                    throw new IOException("The download returned an empty file.");
                }

                // Atomic publish: only a fully downloaded file ever becomes visible to the app.
                File.Move(partPath, destination, overwrite: true);
                Log.Info($"Whisper model '{name}' saved to '{destination}' ({downloadedBytes / (1024.0 * 1024):0.0} MB).");

                progress?.Report(new ModelDownloadProgress
                {
                    Fraction = 1,
                    BytesReceived = downloadedBytes,
                    TotalBytes = downloadedBytes,
                    Status = $"The {name} model is ready.",
                });
                return;
            }
            catch (OperationCanceledException)
            {
                TryDelete(partPath);
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                TryDelete(partPath);
                Log.Warn($"Download of model '{name}' from {url} failed: {ex.Message}");
                if (attempt + 1 < urls.Length)
                {
                    progress?.Report(new ModelDownloadProgress
                    {
                        Status = "Download failed, retrying with a mirror…",
                    });
                }
            }
        }

        throw new InvalidOperationException(
            $"Could not download the {name} model. Check your internet connection and try again. " +
            (lastError is null ? "" : $"Details: {lastError.Message}"),
            lastError);
    }

    private static async Task DownloadFileAsync(
        string url,
        string partPath,
        string modelName,
        bool viaMirror,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        TryDelete(partPath);

        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? 0;
        if (totalBytes > 0 && totalBytes < MinimumSaneModelBytes)
        {
            throw new IOException($"The server returned an unexpectedly small file ({totalBytes} bytes).");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.Asynchronous);

        var buffer = new byte[256 * 1024];
        long received = 0;
        var stopwatch = Stopwatch.StartNew();
        var lastReport = Stopwatch.GetTimestamp();

        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;

            // Throttle progress events to ~8/s so the UI is not flooded.
            if (stopwatch.ElapsedMilliseconds > 0 &&
                Stopwatch.GetTimestamp() - lastReport >= Stopwatch.Frequency / 8)
            {
                lastReport = Stopwatch.GetTimestamp();
                progress?.Report(new ModelDownloadProgress
                {
                    Fraction = totalBytes > 0 ? Math.Clamp(received / (double)totalBytes, 0, 0.999) : 0,
                    BytesReceived = received,
                    TotalBytes = totalBytes,
                    Status = viaMirror
                        ? $"Downloading the {modelName} model from the mirror…"
                        : $"Downloading the {modelName} model…",
                });
            }
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);

        if (totalBytes > 0 && received != totalBytes)
        {
            throw new IOException($"The download was incomplete ({received} of {totalBytes} bytes).");
        }

        if (received < MinimumSaneModelBytes)
        {
            throw new IOException($"The download was unexpectedly small ({received} bytes).");
        }
    }

    private ModelInfo? FindInstalled(string name)
        => GetInstalledModels().FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

    private static bool FileHasContent(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length > 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string SafeModelsDirectory()
    {
        try
        {
            return ModelsDirectory;
        }
        catch
        {
            return "<unknown>";
        }
    }

    private static string NormalizeModelName(string? modelName)
    {
        var name = (modelName ?? "").Trim();
        if (name.Length == 0)
        {
            return "";
        }

        // Accept "tiny", "ggml-tiny", "ggml-tiny.bin" and full paths alike.
        name = Path.GetFileName(name);
        if (name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            name = Path.GetFileNameWithoutExtension(name);
        }

        return ShortNameFromFileName(name).Trim();
    }

    private static string ShortNameFromFileName(string fileName)
    {
        var name = fileName.Trim();
        return name.StartsWith("ggml-", StringComparison.OrdinalIgnoreCase)
            ? name[5..]
            : name;
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
            // Best effort cleanup.
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = true,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };

        var client = new HttpClient(handler)
        {
            // Model downloads are large; the caller's CancellationToken is the timeout.
            Timeout = Timeout.InfiniteTimeSpan,
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("Loquinota/1.0 (Whisper model downloader)");
        return client;
    }
}
