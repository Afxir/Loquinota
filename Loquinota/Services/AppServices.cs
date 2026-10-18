using Loquinota.Models;

using Loquinota.Helpers;

namespace Loquinota.Services;

/// <summary>
/// Composition root for the app's services. Pages use <see cref="EnsureInitializedAsync"/>
/// then the static service properties. Initialization is idempotent and never blocks
/// the UI thread (all I/O is async).
/// </summary>
public static class AppServices
{
    private static Task? _initializeTask;

    public static bool IsInitialized { get; private set; }

    public static AppSettingsService Settings { get; private set; } = null!;

    public static SessionRepository Sessions { get; private set; } = null!;

    public static AudioCaptureService Audio { get; private set; } = null!;

    public static ModelManager Models { get; private set; } = null!;

    public static WhisperTranscriptionService Whisper { get; private set; } = null!;

    public static Mp4Processor Mp4 { get; private set; } = null!;

    public static ExportService Export { get; private set; } = null!;

    /// <summary>Raised when the model readiness or selected model changes.</summary>
    public static event EventHandler? ModelStatusChanged;

    public static Task EnsureInitializedAsync() => _initializeTask ??= InitializeCoreAsync();

    private static async Task InitializeCoreAsync()
    {
        try
        {
            Settings = new AppSettingsService();
            await Settings.LoadAsync();

            Sessions = new SessionRepository();
            await Sessions.InitializeAsync();

            Models = new ModelManager();
            Whisper = new WhisperTranscriptionService();
            Audio = new AudioCaptureService();
            Mp4 = new Mp4Processor();
            Export = new ExportService();

            // Load an already-installed model before signaling readiness so every page
            // observes a consistent "model ready" state on first render.
            await TryLoadInstalledModelAsync();

            IsInitialized = true;
            Log.Info($"Services ready. Data folder: {AppStorage.RootDirectory}");
            ModelStatusChanged?.Invoke(null, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Error("AppServices initialization failed.", ex);
            // Keep IsInitialized false so pages can show a setup error instead of crashing.
            throw;
        }
    }

    private static async Task TryLoadInstalledModelAsync()
    {
        try
        {
            var wanted = Settings.Current.ModelName;
            var installed = Models.GetInstalledModels();
            if (installed.Count == 0)
            {
                return;
            }

            var model = installed.FirstOrDefault(m => string.Equals(m.Name, wanted, StringComparison.OrdinalIgnoreCase))
                        ?? installed[0];

            await Whisper.LoadModelAsync(model.FilePath);

            // Keep settings in sync when we fall back to whatever model is installed.
            if (!string.Equals(Settings.Current.ModelName, model.Name, StringComparison.OrdinalIgnoreCase))
            {
                Settings.Current.ModelName = model.Name;
                await Settings.SaveAsync();
            }

            ModelStatusChanged?.Invoke(null, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not preload a Whisper model.", ex);
        }
    }

    /// <summary>
    /// Ensures a model is downloaded (when needed) and loaded into Whisper.
    /// Returns false with a log entry on failure; callers show a user-friendly status.
    /// </summary>
    public static async Task<bool> EnsureModelLoadedAsync(
        string? modelName = null,
        IProgress<ModelDownloadProgress>? downloadProgress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureInitializedAsync();
            modelName ??= Settings.Current.ModelName;

            string modelPath = Models.IsInstalled(modelName)
                ? Models.GetModelPath(modelName)
                : await Models.EnsureModelAsync(modelName, downloadProgress, cancellationToken);

            if (!string.Equals(Whisper.ModelPath, modelPath, StringComparison.OrdinalIgnoreCase) || !Whisper.IsReady)
            {
                await Whisper.LoadModelAsync(modelPath, cancellationToken: cancellationToken);
            }

            Settings.Current.ModelName = modelName;
            await Settings.SaveAsync(cancellationToken);
            ModelStatusChanged?.Invoke(null, EventArgs.Empty);
            return Whisper.IsReady;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error("Preparing the Whisper model failed.", ex);
            return false;
        }
    }

    public static string ModelStatusText =>
        Whisper is { IsReady: true }
            ? $"Speech model ready · {Whisper.ModelName}"
            : "Speech model not loaded";
}
