using System.Text.Json;
using Loquinota.Models;

using Loquinota.Helpers;

namespace Loquinota.Services;

/// <summary>
/// Loads/saves <see cref="AppSettings"/> as JSON at AppStorage.SettingsPath.
/// Tolerates missing or corrupt files (falls back to defaults).
/// </summary>
public sealed class AppSettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    // Static so that saves from different service instances also serialize: the replace
    // step cannot run while another writer is swapping the same file.
    private static readonly SemaphoreSlim SaveGate = new(1, 1);

    private readonly string _settingsPath;

    public AppSettingsService()
        : this(null)
    {
    }

    /// <summary>Test seam: lets callers/tests point the service at a scratch file.</summary>
    internal AppSettingsService(string? settingsPath)
        => _settingsPath = string.IsNullOrWhiteSpace(settingsPath)
            ? AppStorage.SettingsPath
            : Path.GetFullPath(settingsPath!);

    public AppSettings Current { get; private set; } = new();

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                Log.Info($"No settings file at '{_settingsPath}'; using defaults.");
                Current = new AppSettings();
                return;
            }

            await using var stream = new FileStream(
                _settingsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            AppSettings? loaded;
            try
            {
                loaded = await JsonSerializer
                    .DeserializeAsync<AppSettings>(stream, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                Log.Warn($"Settings file at '{_settingsPath}' is corrupt; using defaults.", ex);
                Current = new AppSettings();
                return;
            }

            Current = Normalize(loaded);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Unreadable file (permissions, locking): never take the app down over settings.
            Log.Warn($"Could not read settings from '{_settingsPath}'; using defaults.", ex);
            Current = new AppSettings();
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await SaveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporaryPath = $"{_settingsPath}.tmp-{Guid.NewGuid():N}";

        try
        {
            var directory = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous))
            {
                await JsonSerializer
                    .SerializeAsync(stream, Current ?? new AppSettings(), SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            // Replace in one step so readers never observe a partially written file.
            await ReplaceFileAsync(temporaryPath, _settingsPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(temporaryPath);
            throw;
        }
        catch (Exception ex)
        {
            TryDeleteFile(temporaryPath);
            Log.Error($"Could not save settings to '{_settingsPath}'.", ex);
            throw;
        }
        finally
        {
            SaveGate.Release();
        }
    }

    /// <summary>
    /// File.Move(overwrite: true) fails transiently when a concurrent reader still holds the
    /// target without FILE_SHARE_DELETE, so retry briefly instead of losing the save.
    /// </summary>
    private static async Task ReplaceFileAsync(string sourcePath, string targetPath, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(sourcePath, targetPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Retrying settings replace ({attempt}/5).", ex);
                await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static AppSettings Normalize(AppSettings? loaded)
    {
        if (loaded is null)
        {
            return new AppSettings();
        }

        if (string.IsNullOrWhiteSpace(loaded.ModelName))
        {
            loaded.ModelName = new AppSettings().ModelName;
        }

        if (string.IsNullOrWhiteSpace(loaded.Language))
        {
            loaded.Language = new AppSettings().Language;
        }

        return loaded;
    }

    private static void TryDeleteFile(string path)
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
            // Cleanup of the temp file is best effort.
        }
    }
}
