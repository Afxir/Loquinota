using System.Diagnostics;
using Loquinota.Helpers;
using Loquinota.Models;
using Loquinota.Services;
using Material.Icons;
using Material.Icons.WinUI3;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace Loquinota.Pages;

/// <summary>
/// The Live Capture page: pick audio sources, record, watch the streaming transcript,
/// then review the generated notes and save everything as a session.
///
/// Threading rules used here:
///  - Audio capture raises SamplesAvailable/LevelChanged on a background thread. Samples are
///    forwarded to the live transcription session directly (non-blocking queue) and level
///    updates are coalesced onto the UI thread at ~10 Hz via DispatcherQueue.
///  - Every other event (transcript, status, errors, model changes) is marshalled with
///    DispatcherQueue.TryEnqueue before touching XAML.
///  - All async handlers are funnelled through <see cref="RunGuardedAsync"/> so an unexpected
///    exception can never escape as an unhandled async-void exception.
/// </summary>
public sealed partial class LiveCapturePage : Page
{
    private const int LevelRefreshIntervalMs = 90;
    private const int MaxProgressRefreshIntervalMs = 100;

    private readonly Stopwatch _elapsed = new();
    private readonly SemaphoreSlim _finishGate = new(1, 1);

    private DispatcherQueue? _dispatcher;
    private DispatcherQueueTimer? _uiTimer;
    private CancellationTokenSource? _pageCts;

    private LiveTranscriptionSession? _liveSession;
    private bool _servicesReady;
    private bool _loaded;
    private bool _suppressSettingsPersist;
    private bool _modelEventSubscribed;
    private bool _audioEventsSubscribed;
    private bool _isRecording;
    private bool _isBusy;
    private bool _isSaving;
    private bool _reviewVisible;
    private bool _sessionSaved;

    private string _liveTranscript = "";
    private string _pendingSessionId = "";
    private string? _wavPath;
    private TimeSpan _captureDuration;
    private Session? _savedSession;

    private int _levelRepaintQueued;
    private double _latestLevel;
    private long _lastLevelPaintTick;
    private long _lastProgressPaintTick;

    public LiveCapturePage()
    {
        InitializeComponent();
    }

    private CancellationToken PageToken => _pageCts?.Token ?? CancellationToken.None;

    // =====================================================================
    // Lifecycle
    // =====================================================================

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        try
        {
            _dispatcher = DispatcherQueue;
        }
        catch
        {
            _dispatcher = null;
        }

        _dispatcher ??= Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        try
        {
            _pageCts?.Dispose();
        }
        catch
        {
            // Ignore a double dispose.
        }

        _pageCts = new CancellationTokenSource();
        EnsureTimer();

        if (!_loaded)
        {
            _loaded = true;
            _ = RunGuardedAsync(LoadPageAsync);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        try
        {
            _uiTimer?.Stop();
            if (_uiTimer is not null)
            {
                _uiTimer.Tick -= OnUiTimerTick;
            }
            _uiTimer = null;
        }
        catch (Exception ex)
        {
            Log.Warn("Stopping the Live Capture timer failed.", ex);
        }

        try
        {
            if (_modelEventSubscribed)
            {
                AppServices.ModelStatusChanged -= OnModelStatusChanged;
                _modelEventSubscribed = false;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Unsubscribing from model status changes failed.", ex);
        }

        try
        {
            _pageCts?.Cancel();
        }
        catch (Exception ex)
        {
            Log.Warn("Cancelling pending Live Capture work failed.", ex);
        }

        UnsubscribeAudioEvents();

        bool needsTeardown = _isRecording || _liveSession is not null;
        _isRecording = false;

        if (needsTeardown)
        {
            // The page is going away; still stop the hardware and flush the transcript so the
            // audio device is released for the next page.
            _ = RunGuardedAsync(FinishCaptureOnLeaveAsync);
        }

        try
        {
            _pageCts?.Dispose();
        }
        catch
        {
            // Ignore a double dispose.
        }

        _pageCts = null;
    }

    private async Task LoadPageAsync()
    {
        InitRing.IsActive = true;
        InitRing.Visibility = Visibility.Visible;
        HeaderStatusText.Text = "Starting speech engine…";
        SetStatus("Starting the speech engine…", busy: true);

        try
        {
            await AppServices.EnsureInitializedAsync();
            _servicesReady = true;
        }
        catch (Exception ex)
        {
            Log.Error("Live Capture could not initialize the app services.", ex);
            HeaderStatusText.Text = "Speech engine unavailable.";
            ModelMissingPill.Visibility = Visibility.Visible;
            SetStatus("The speech engine could not start. See the log for details.", busy: false);
            ShowPageError("Speech engine unavailable",
                "The app services could not be started, so recording is disabled. Restart loquinota and check the log if this keeps happening.\n\n" + ex.Message);
            ApplyState();
            return;
        }
        finally
        {
            InitRing.IsActive = false;
            InitRing.Visibility = Visibility.Collapsed;
        }

        if (!_modelEventSubscribed)
        {
            AppServices.ModelStatusChanged += OnModelStatusChanged;
            _modelEventSubscribed = true;
        }

        // Populating the pickers raises selection/toggle events; make sure they do not
        // overwrite the stored preferences with the pre-load defaults.
        _suppressSettingsPersist = true;
        try
        {
            ApplySettingsDefaults();
            LoadModels();
            LoadMicrophones();
            UpdateSpeakerLabel();
        }
        finally
        {
            _suppressSettingsPersist = false;
        }

        RefreshModelUi();
        SetStatus("Ready.", busy: false);

        HeaderStatusText.Text = IsModelReady()
            ? "Everything is ready."
            : "A speech model is required for live transcription.";
    }

    private void EnsureTimer()
    {
        if (_uiTimer is not null || _dispatcher is null)
        {
            return;
        }

        try
        {
            _uiTimer = _dispatcher.CreateTimer();
            _uiTimer.Interval = TimeSpan.FromMilliseconds(200);
            _uiTimer.Tick += OnUiTimerTick;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not create the Live Capture UI timer.", ex);
            _uiTimer = null;
        }
    }

    private void OnUiTimerTick(DispatcherQueueTimer sender, object args)
    {
        try
        {
            if (!_isRecording)
            {
                return;
            }

            ElapsedText.Text = FormatTime(_elapsed.Elapsed);

            // Let the meter fall back to zero when the input goes quiet.
            if (LevelBar.Value > 0.5)
            {
                var decayed = Math.Max(0, LevelBar.Value - 3);
                LevelBar.Value = decayed;
                LevelText.Text = $"{decayed:0}%";
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Updating the elapsed timer failed.", ex);
        }
    }

    // =====================================================================
    // Init helpers (model list, devices, settings)
    // =====================================================================

    private void LoadModels(string? preferredName = null)
    {
        var list = new List<ModelChoice>();

        try
        {
            foreach (var model in AppServices.Models.GetAvailableModels())
            {
                var suffix = model.IsInstalled
                    ? " · installed"
                    : string.IsNullOrWhiteSpace(model.SizeText) ? "" : $" · {model.SizeText}";
                var display = string.IsNullOrWhiteSpace(model.DisplayName) ? model.Name : model.DisplayName;
                list.Add(new ModelChoice(model.Name, display + suffix));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not list the available speech models.", ex);
        }

        if (list.Count == 0)
        {
            foreach (var name in ModelManager.SupportedModels)
            {
                list.Add(new ModelChoice(name, name));
            }
        }

        var wanted = preferredName
                     ?? (ModelCombo.SelectedItem as ModelChoice)?.Name
                     ?? SafeSettingsModelName();

        ModelCombo.ItemsSource = list;
        ModelCombo.SelectedItem = FindModelChoice(list, wanted) ?? list[0];
    }

    private void LoadMicrophones(string? preferredDeviceId = null)
    {
        var list = new List<AudioDeviceInfo>();
        AudioDeviceInfo? defaultDevice = null;

        try
        {
            list.AddRange(AudioDeviceCatalog.GetMicrophones());
            defaultDevice = AudioDeviceCatalog.GetDefaultMicrophone();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not enumerate the microphone devices.", ex);
        }

        if (list.Count == 0)
        {
            list.Add(new AudioDeviceInfo { Id = "", Name = "Default microphone", IsDefault = true });
        }

        var wantedId = preferredDeviceId ?? SafeSettingsMicrophoneId();
        var selected = FindDevice(list, wantedId);
        selected ??= defaultDevice is null ? null : FindDevice(list, defaultDevice.Id);
        selected ??= list.FirstOrDefault(d => d.IsDefault);
        selected ??= list[0];

        MicrophoneCombo.ItemsSource = list;
        MicrophoneCombo.SelectedItem = selected;
    }

    private void UpdateSpeakerLabel()
    {
        string? speakerName = null;

        try
        {
            speakerName = AudioDeviceCatalog.GetDefaultSpeakerName();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the default speaker name.", ex);
        }

        SpeakerLabel.Text = string.IsNullOrWhiteSpace(speakerName)
            ? "Records everything playing through the default output device."
            : $"Records everything playing on “{speakerName}”.";
    }

    private void ApplySettingsDefaults()
    {
        try
        {
            var settings = AppServices.Settings.Current;
            MicrophoneToggle.IsOn = settings.CaptureMicrophoneByDefault;
            SystemAudioToggle.IsOn = settings.PreferSystemAudio;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not apply the saved capture settings.", ex);
            MicrophoneToggle.IsOn = true;
            SystemAudioToggle.IsOn = false;
        }
    }

    private void CaptureToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_servicesReady)
        {
            return;
        }

        try
        {
            ApplyState();
            if (!_suppressSettingsPersist)
            {
                _ = PersistCaptureSettingsAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Handling an audio source toggle failed.", ex);
        }
    }

    private void MicrophoneCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_servicesReady)
        {
            return;
        }

        try
        {
            if (!_suppressSettingsPersist)
            {
                _ = PersistCaptureSettingsAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Handling a microphone selection failed.", ex);
        }
    }

    private async Task PersistCaptureSettingsAsync()
    {
        try
        {
            if (!AppServices.IsInitialized || _suppressSettingsPersist)
            {
                return;
            }

            var settings = AppServices.Settings.Current;
            settings.CaptureMicrophoneByDefault = MicrophoneToggle.IsOn;
            settings.PreferSystemAudio = SystemAudioToggle.IsOn;
            settings.MicrophoneDeviceId = (MicrophoneCombo.SelectedItem as AudioDeviceInfo)?.Id;
            await AppServices.Settings.SaveAsync();
        }
        catch (Exception ex)
        {
            // Saving preferences is a nicety, not a hard failure.
            Log.Warn("Could not save the capture preferences.", ex);
        }
    }

    private void RefreshDevicesButton_Click(object sender, RoutedEventArgs e)
        => _ = RunGuardedAsync(RefreshDevicesAsync);

    private async Task RefreshDevicesAsync()
    {
        try
        {
            var currentId = (MicrophoneCombo.SelectedItem as AudioDeviceInfo)?.Id;
            LoadMicrophones(currentId);
            UpdateSpeakerLabel();
            if (!_isRecording)
            {
                SetStatus("Audio devices refreshed.", busy: false);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Refreshing the audio device list failed.", ex);
            ShowPageError("Could not refresh the audio devices", ex.Message);
        }

        await Task.CompletedTask;
    }

    // =====================================================================
    // Model setup
    // =====================================================================

    private void DownloadButton_Click(object sender, RoutedEventArgs e)
        => _ = RunGuardedAsync(PrepareModelAsync);

    private void RetryButton_Click(object sender, RoutedEventArgs e)
        => _ = RunGuardedAsync(PrepareModelAsync);

    private void ImportButton_Click(object sender, RoutedEventArgs e)
        => _ = RunGuardedAsync(ImportLocalModelAsync);

    private async Task PrepareModelAsync()
    {
        if (_isBusy)
        {
            return;
        }

        if (!_servicesReady)
        {
            ShowPageError("Speech engine not ready", "The speech engine is still starting. Try again in a moment.");
            return;
        }

        var modelName = SelectedModelName();

        _isBusy = true;
        DownloadButton.IsEnabled = false;
        DownloadProgressPanel.Visibility = Visibility.Visible;
        DownloadProgressBar.IsIndeterminate = true;
        DownloadProgressBar.Value = 0;
        DownloadStatusText.Text = $"Preparing the {modelName} model…";
        SetStatus($"Preparing the “{modelName}” speech model…", busy: true);
        ApplyState();

        try
        {
            var dispatcher = _dispatcher;
            IProgress<ModelDownloadProgress>? progress = dispatcher is null
                ? null
                : new UiProgress<ModelDownloadProgress>(dispatcher, ApplyDownloadProgress);

            var loaded = await AppServices.EnsureModelLoadedAsync(modelName, progress, PageToken);

            if (loaded)
            {
                DownloadProgressBar.IsIndeterminate = false;
                DownloadProgressBar.Value = 1;
                DownloadStatusText.Text = "Model ready.";
                SetStatus("Speech model loaded.", busy: false);

                // Refresh the list so "installed" markers update, then reload the model UI.
                LoadModels(modelName);
                RefreshModelUi();
                ScrollPageToTop();
            }
            else
            {
                DownloadProgressBar.IsIndeterminate = false;
                DownloadStatusText.Text =
                    "The model could not be loaded. Check your connection and retry, or import a local .bin/.gguf file.";
                SetStatus("Model setup failed.", busy: false);
                ShowPageError("Model setup failed",
                    "The speech model could not be downloaded or loaded. Retry, or use “Use local model…” to import a model file you already have.");
            }
        }
        catch (OperationCanceledException)
        {
            DownloadStatusText.Text = "Model setup was cancelled.";
            SetStatus("Model setup cancelled.", busy: false);
        }
        catch (Exception ex)
        {
            Log.Error("Preparing the speech model failed.", ex);
            DownloadStatusText.Text = "Model setup failed: " + ex.Message;
            SetStatus("Model setup failed.", busy: false);
            ShowPageError("Model setup failed", ex.Message);
        }
        finally
        {
            _isBusy = false;
            DownloadProgressBar.IsIndeterminate = false;
            ApplyState();
        }
    }

    private void ApplyDownloadProgress(ModelDownloadProgress progress)
    {
        try
        {
            var now = Environment.TickCount64;
            if (now - _lastProgressPaintTick < MaxProgressRefreshIntervalMs && progress.Fraction < 0.999)
            {
                return;
            }

            _lastProgressPaintTick = now;

            var fraction = double.IsNaN(progress.Fraction) ? 0 : Math.Clamp(progress.Fraction, 0, 1);
            DownloadProgressBar.IsIndeterminate = progress.TotalBytes <= 0 && fraction <= 0;
            DownloadProgressBar.Value = fraction;

            var status = progress.Status;
            var bytes = progress.BytesText;
            DownloadStatusText.Text = string.IsNullOrWhiteSpace(status)
                ? bytes
                : string.IsNullOrWhiteSpace(bytes) ? status : $"{status} · {bytes}";
        }
        catch (Exception ex)
        {
            Log.Warn("Updating the model download progress failed.", ex);
        }
    }

    private async Task ImportLocalModelAsync()
    {
        if (_isBusy)
        {
            return;
        }

        try
        {
            var window = (Application.Current as App)?.MainWindow;
            if (window is null)
            {
                ShowPageError("Cannot open the file picker", "The app window is not available yet.");
                return;
            }

            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.Downloads,
                ViewMode = PickerViewMode.List
            };
            picker.FileTypeFilter.Add(".bin");
            picker.FileTypeFilter.Add(".gguf");

            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(window));

            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            _isBusy = true;
            DownloadProgressPanel.Visibility = Visibility.Visible;
            DownloadProgressBar.IsIndeterminate = true;
            DownloadProgressBar.Value = 0;
            DownloadStatusText.Text = $"Importing {file.Name}…";
            SetStatus($"Importing “{file.Name}”…", busy: true);
            ApplyState();

            var path = await AppServices.Models.ImportModelAsync(file.Path, PageToken);
            await AppServices.Whisper.LoadModelAsync(path, cancellationToken: PageToken);

            var importedName = DeriveModelName(path);
            try
            {
                AppServices.Settings.Current.ModelName = importedName;
                await AppServices.Settings.SaveAsync();
            }
            catch (Exception ex)
            {
                Log.Warn("Could not persist the imported model name.", ex);
            }

            LoadModels(importedName);
            DownloadStatusText.Text = $"Imported {Path.GetFileName(path)}.";
            SetStatus("Local model imported and loaded.", busy: false);
            RefreshModelUi();
            ScrollPageToTop();
        }
        catch (OperationCanceledException)
        {
            SetStatus("Model import cancelled.", busy: false);
        }
        catch (Exception ex)
        {
            Log.Error("Importing a local speech model failed.", ex);
            DownloadStatusText.Text = "Import failed: " + ex.Message;
            SetStatus("Model import failed.", busy: false);
            ShowPageError("Could not import the model", ex.Message);
        }
        finally
        {
            _isBusy = false;
            DownloadProgressBar.IsIndeterminate = false;
            ApplyState();
        }
    }

    // =====================================================================
    // Recording
    // =====================================================================

    private void StartButton_Click(object sender, RoutedEventArgs e)
        => _ = RunGuardedAsync(StartCaptureAsync);

    private async Task StartCaptureAsync()
    {
        if (_isRecording || _isBusy)
        {
            return;
        }

        if (!IsModelReady())
        {
            ShowPageError("Speech model not ready",
                "Download or load a speech model before recording. The model picker is above.");
            return;
        }

        if (!AppServices.IsInitialized)
        {
            ShowPageError("Speech engine not ready", "The app services are still starting. Try again in a moment.");
            return;
        }

        var captureMicrophone = MicrophoneToggle.IsOn;
        var captureSystemAudio = SystemAudioToggle.IsOn;
        if (!captureMicrophone && !captureSystemAudio)
        {
            ShowPageError("No audio source selected",
                "Enable the microphone, system audio, or both before starting a recording.");
            return;
        }

        _isBusy = true;
        PageInfoBar.IsOpen = false;
        SetStatus("Starting capture…", busy: true);
        ApplyState();

        try
        {
            _pendingSessionId = Guid.NewGuid().ToString();
            _wavPath = SafeGetAudioPath(_pendingSessionId);
            if (!string.IsNullOrWhiteSpace(_wavPath))
            {
                var folder = Path.GetDirectoryName(_wavPath);
                if (!string.IsNullOrEmpty(folder))
                {
                    Directory.CreateDirectory(folder);
                }
            }

            _liveTranscript = "";
            _captureDuration = TimeSpan.Zero;
            _elapsed.Reset();
            ElapsedText.Text = "00:00";
            LevelBar.Value = 0;
            LevelText.Text = "0%";
            TranscriptBox.Text = "";
            TranscriptPill.Visibility = Visibility.Collapsed;

            var language = SafeSettingsLanguage();
            var options = new LiveTranscriptionOptions
            {
                Language = string.IsNullOrWhiteSpace(language) ? "auto" : language,
                ChunkSeconds = 3
            };

            _liveSession = AppServices.Whisper.CreateLiveSession(options);
            _liveSession.TranscriptUpdated += OnTranscriptUpdated;
            _liveSession.StatusChanged += OnLiveStatusChanged;
            await _liveSession.StartAsync(PageToken);

            SubscribeAudioEvents();

            var microphoneId = captureMicrophone
                ? (MicrophoneCombo.SelectedItem as AudioDeviceInfo)?.Id
                : null;
            if (string.IsNullOrWhiteSpace(microphoneId))
            {
                microphoneId = null;
            }

            var audioOptions = new AudioCaptureOptions
            {
                CaptureMicrophone = captureMicrophone,
                CaptureSystemAudio = captureSystemAudio,
                MicrophoneDeviceId = microphoneId,
                WavOutputPath = _wavPath,
                TargetSampleRate = 16000
            };

            // Mark as recording before starting the devices so the very first sample frames
            // are forwarded to the live transcription session.
            _isRecording = true;
            _elapsed.Restart();
            _uiTimer?.Start();
            ApplyState();

            await AppServices.Audio.StartAsync(audioOptions, PageToken);

            SetStatus(
                captureMicrophone && captureSystemAudio
                    ? "Recording microphone and system audio. Stop when you are done."
                    : captureSystemAudio
                        ? "Recording system audio. Stop when you are done."
                        : "Recording microphone. Stop when you are done.",
                busy: false);
            ApplyState();
        }
        catch (OperationCanceledException)
        {
            await AbortPartialStartAsync();
            SetStatus("Starting the recording was cancelled.", busy: false);
        }
        catch (Exception ex)
        {
            Log.Error("Starting a live capture failed.", ex);
            await AbortPartialStartAsync();
            SetStatus("Recording could not start.", busy: false);
            ShowPageError("Could not start recording", ex.Message);
        }
        finally
        {
            _isBusy = false;
            ApplyState();
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
        => _ = RunGuardedAsync(StopCaptureAsync);

    private async Task StopCaptureAsync()
    {
        if (!_isRecording || _isBusy)
        {
            return;
        }

        if (!await _finishGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            _isRecording = false;
            _uiTimer?.Stop();
            _elapsed.Stop();
            _captureDuration = _elapsed.Elapsed;
            SetStatus("Stopping capture and finishing the transcript…", busy: true);
            ApplyState();

            var (transcript, result) = await FinishCaptureAsync();

            if (result is not null)
            {
                if (result.Duration > TimeSpan.Zero)
                {
                    _captureDuration = result.Duration;
                }

                if (!string.IsNullOrWhiteSpace(result.WavFilePath))
                {
                    _wavPath = result.WavFilePath;
                }
            }

            if (_captureDuration <= TimeSpan.Zero)
            {
                _captureDuration = _elapsed.Elapsed;
            }

            if (string.IsNullOrWhiteSpace(transcript))
            {
                transcript = _liveTranscript;
            }

            LevelBar.Value = 0;
            LevelText.Text = "0%";
            ElapsedText.Text = FormatTime(_captureDuration);

            var title = SafeBuildTitle();
            var notes = SafeGenerateNotes(transcript);
            EnterReviewState(title, transcript, notes);

            SetStatus(string.IsNullOrWhiteSpace(transcript)
                    ? "No speech was detected — you can still save the audio with your own notes."
                    : "Recording finished. Review the transcript and notes, then save.",
                busy: false);
        }
        catch (Exception ex)
        {
            Log.Error("Finishing the live capture failed.", ex);
            SetStatus("Finishing the recording failed.", busy: false);
            ShowPageError("Could not finish the recording", ex.Message);
        }
        finally
        {
            _finishGate.Release();
            ApplyState();
        }
    }

    /// <summary>
    /// Stops the devices and flushes the live transcription session. Idempotent and safe to
    /// call from the stop button and from OnNavigatedFrom.
    /// </summary>
    private async Task<(string transcript, AudioCaptureResult? result)> FinishCaptureAsync()
    {
        UnsubscribeAudioEvents();

        AudioCaptureResult? result = null;
        try
        {
            if (AppServices.IsInitialized && AppServices.Audio.IsCapturing)
            {
                result = await AppServices.Audio.StopAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Stopping the audio capture devices failed.", ex);
        }

        var transcript = _liveTranscript;
        var session = Interlocked.Exchange(ref _liveSession, null);
        if (session is not null)
        {
            try
            {
                session.TranscriptUpdated -= OnTranscriptUpdated;
                session.StatusChanged -= OnLiveStatusChanged;
            }
            catch (Exception ex)
            {
                Log.Warn("Detaching the live transcription events failed.", ex);
            }

            try
            {
                transcript = await session.CompleteAsync();
            }
            catch (Exception ex)
            {
                Log.Error("Flushing the final transcript failed.", ex);
            }

            try
            {
                await session.DisposeAsync();
            }
            catch (Exception ex)
            {
                Log.Warn("Disposing the live transcription session failed.", ex);
            }
        }

        return (transcript ?? "", result);
    }

    private async Task FinishCaptureOnLeaveAsync()
    {
        if (!await _finishGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            await FinishCaptureAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("Tidying up the capture after leaving the page failed.", ex);
        }
        finally
        {
            _finishGate.Release();
        }
    }

    private async Task AbortPartialStartAsync()
    {
        _isRecording = false;
        _uiTimer?.Stop();
        _elapsed.Stop();
        UnsubscribeAudioEvents();

        try
        {
            if (AppServices.IsInitialized && AppServices.Audio.IsCapturing)
            {
                await AppServices.Audio.StopAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Stopping the audio devices after a failed start failed.", ex);
        }

        var session = Interlocked.Exchange(ref _liveSession, null);
        if (session is not null)
        {
            try
            {
                session.TranscriptUpdated -= OnTranscriptUpdated;
                session.StatusChanged -= OnLiveStatusChanged;
            }
            catch
            {
                // Ignore detach failures while aborting.
            }

            try
            {
                await session.DisposeAsync();
            }
            catch (Exception ex)
            {
                Log.Warn("Disposing the live transcription session after a failed start failed.", ex);
            }
        }

        LevelBar.Value = 0;
        LevelText.Text = "0%";
    }

    // =====================================================================
    // Audio events (background threads)
    // =====================================================================

    private void SubscribeAudioEvents()
    {
        if (_audioEventsSubscribed || !AppServices.IsInitialized)
        {
            return;
        }

        try
        {
            AppServices.Audio.SamplesAvailable += OnAudioSamples;
            AppServices.Audio.LevelChanged += OnAudioLevelChanged;
            AppServices.Audio.StatusChanged += OnAudioStatusChanged;
            AppServices.Audio.Error += OnAudioError;
            _audioEventsSubscribed = true;
        }
        catch (Exception ex)
        {
            Log.Warn("Subscribing to the audio capture events failed.", ex);
        }
    }

    private void UnsubscribeAudioEvents()
    {
        if (!_audioEventsSubscribed)
        {
            return;
        }

        try
        {
            AppServices.Audio.SamplesAvailable -= OnAudioSamples;
            AppServices.Audio.LevelChanged -= OnAudioLevelChanged;
            AppServices.Audio.StatusChanged -= OnAudioStatusChanged;
            AppServices.Audio.Error -= OnAudioError;
        }
        catch (Exception ex)
        {
            Log.Warn("Unsubscribing from the audio capture events failed.", ex);
        }

        _audioEventsSubscribed = false;
    }

    private void OnAudioSamples(object? sender, AudioSamplesEventArgs e)
    {
        var session = _liveSession;
        if (session is null || !_isRecording || e.Samples.Length == 0)
        {
            return;
        }

        try
        {
            // Push only queues the samples; it must never block the audio thread.
            session.Push(e.Samples);
        }
        catch (Exception ex)
        {
            Log.Warn("Forwarding audio to the live transcription session failed.", ex);
        }
    }

    private void OnAudioLevelChanged(object? sender, double level)
    {
        _latestLevel = level;

        if (Interlocked.CompareExchange(ref _levelRepaintQueued, 1, 0) != 0)
        {
            return;
        }

        var dispatcher = _dispatcher;
        if (dispatcher is null)
        {
            _levelRepaintQueued = 0;
            return;
        }

        if (!dispatcher.TryEnqueue(DispatcherQueuePriority.Normal, ApplyLevelUpdate))
        {
            _levelRepaintQueued = 0;
        }
    }

    private void ApplyLevelUpdate()
    {
        _levelRepaintQueued = 0;

        try
        {
            var now = Environment.TickCount64;
            if (now - _lastLevelPaintTick < LevelRefreshIntervalMs)
            {
                return;
            }

            _lastLevelPaintTick = now;

            var level = double.IsNaN(_latestLevel) ? 0 : Math.Clamp(_latestLevel, 0, 1);
            LevelBar.Value = level * 100;
            LevelText.Text = $"{level * 100:0}%";
        }
        catch (Exception ex)
        {
            Log.Warn("Updating the level meter failed.", ex);
        }
    }

    private void OnAudioStatusChanged(object? sender, string status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return;
        }

        var dispatcher = _dispatcher;
        dispatcher?.TryEnqueue(() =>
        {
            if (!_reviewVisible)
            {
                SetStatus(status, _isBusy);
            }
        });
    }

    private void OnAudioError(object? sender, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var dispatcher = _dispatcher;
        dispatcher?.TryEnqueue(() =>
        {
            Log.Warn("Audio capture reported an error: " + message);
            ShowPageError("Audio capture issue", message);
        });
    }

    // =====================================================================
    // Live transcript events (background threads)
    // =====================================================================

    private void OnTranscriptUpdated(object? sender, LiveTranscriptUpdate update)
    {
        if (update is null)
        {
            return;
        }

        var dispatcher = _dispatcher;
        dispatcher?.TryEnqueue(DispatcherQueuePriority.Normal, () => ApplyTranscriptUpdate(update));
    }

    private void ApplyTranscriptUpdate(LiveTranscriptUpdate update)
    {
        try
        {
            var text = update.FullTranscript ?? "";
            if (string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(update.LatestSegment))
            {
                text = update.LatestSegment;
            }

            _liveTranscript = text;
            TranscriptBox.Text = text;

            TranscriptPill.Visibility = Visibility.Visible;
            TranscriptPillText.Text = update.IsPartial ? "Partial" : "Listening…";

            if (!string.IsNullOrWhiteSpace(text))
            {
                _dispatcher?.TryEnqueue(DispatcherQueuePriority.Low, ScrollTranscriptToEnd);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Applying a live transcript update failed.", ex);
        }
    }

    private void OnLiveStatusChanged(object? sender, string status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return;
        }

        var dispatcher = _dispatcher;
        dispatcher?.TryEnqueue(() =>
        {
            if (_isRecording && !_reviewVisible)
            {
                SetStatus(status, _isBusy);
            }
        });
    }

    private void ScrollTranscriptToEnd()
    {
        try
        {
            var scrollViewer = FindDescendant<ScrollViewer>(TranscriptBox);
            if (scrollViewer is not null && scrollViewer.ScrollableHeight > 0)
            {
                scrollViewer.ChangeView(null, scrollViewer.ScrollableHeight, null, true);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Auto-scrolling the transcript failed.", ex);
        }
    }

    // =====================================================================
    // Review and save
    // =====================================================================

    private void EnterReviewState(string title, string transcript, string notes)
    {
        _reviewVisible = true;
        _sessionSaved = false;
        _savedSession = null;

        SavedInfoBar.IsOpen = false;
        ReviewTitleBox.Text = title;
        ReviewTranscriptBox.Text = transcript;
        ReviewNotesBox.Text = notes;

        DiscardButton.Visibility = Visibility.Visible;
        NewRecordingButton.Visibility = Visibility.Collapsed;
        SaveButton.IsEnabled = true;

        ApplyState();

        // Wait for the new layout, then show the review card from the top of the page and
        // only focus the title once it is on screen (avoids a surprise auto-scroll).
        var dispatcher = _dispatcher;
        if (dispatcher is not null)
        {
            dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                try
                {
                    PageScrollViewer.ChangeView(null, 0, null, true);
                    ReviewTitleBox.Focus(FocusState.Programmatic);
                }
                catch (Exception ex)
                {
                    Log.Warn("Preparing the review view failed.", ex);
                }
            });
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
        => _ = RunGuardedAsync(SaveSessionAsync);

    private async Task SaveSessionAsync()
    {
        if (_isSaving || _sessionSaved || !_reviewVisible)
        {
            return;
        }

        if (!AppServices.IsInitialized)
        {
            ShowPageError("Session store not ready", "The session database is still starting. Try again in a moment.");
            return;
        }

        _isSaving = true;
        ApplyState();

        try
        {
            var title = (ReviewTitleBox.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                title = SafeBuildTitle();
            }

            var session = new Session
            {
                Id = string.IsNullOrWhiteSpace(_pendingSessionId) ? Guid.NewGuid().ToString() : _pendingSessionId,
                Title = title,
                CreatedAt = DateTimeOffset.Now,
                Duration = _captureDuration,
                SourceType = SessionSourceType.Live,
                AudioFilePath = _wavPath ?? string.Empty,
                TranscriptText = ReviewTranscriptBox.Text ?? "",
                NotesText = ReviewNotesBox.Text ?? "",
                ModelName = SafeModelName(),
                Language = SafeSettingsLanguage()
            };

            await AppServices.Sessions.UpsertAsync(session);

            _savedSession = session;
            _pendingSessionId = session.Id;
            _sessionSaved = true;

            ReviewTitleBox.Text = title;
            SaveButton.IsEnabled = false;
            DiscardButton.Visibility = Visibility.Collapsed;
            NewRecordingButton.Visibility = Visibility.Visible;

            SavedInfoBar.Title = "Session saved";
            SavedInfoBar.Message = $"“{title}” was added to History with its audio, transcript and notes.";
            SavedInfoBar.IsOpen = true;

            SetStatus("Session saved to History.", busy: false);
        }
        catch (Exception ex)
        {
            Log.Error("Saving the live session failed.", ex);
            SetStatus("Saving the session failed.", busy: false);
            ShowPageError("Could not save the session", ex.Message);
        }
        finally
        {
            _isSaving = false;
            ApplyState();
        }
    }

    private void DiscardButton_Click(object sender, RoutedEventArgs e)
        => _ = RunGuardedAsync(DiscardRecordingAsync);

    private async Task DiscardRecordingAsync()
    {
        if (!_reviewVisible)
        {
            return;
        }

        var confirmed = await ConfirmAsync(
            "Discard this recording?",
            "The captured audio, transcript and notes will be deleted and nothing will be saved.",
            "Discard recording");

        if (!confirmed)
        {
            return;
        }

        DeletePendingAudio();
        StartNewRecording();
        SetStatus("Recording discarded.", busy: false);
    }

    private void NewRecordingButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StartNewRecording();
            SetStatus("Ready for a new recording.", busy: false);
        }
        catch (Exception ex)
        {
            Log.Warn("Resetting the Live Capture page failed.", ex);
        }
    }

    private void StartNewRecording()
    {
        _reviewVisible = false;
        _sessionSaved = false;
        _savedSession = null;
        _pendingSessionId = "";
        _wavPath = null;
        _captureDuration = TimeSpan.Zero;
        _liveTranscript = "";

        _elapsed.Reset();
        _uiTimer?.Stop();

        ElapsedText.Text = "00:00";
        LevelBar.Value = 0;
        LevelText.Text = "0%";
        TranscriptBox.Text = "";
        TranscriptPill.Visibility = Visibility.Collapsed;
        ReviewTitleBox.Text = "";
        ReviewNotesBox.Text = "";
        ReviewTranscriptBox.Text = "";
        SavedInfoBar.IsOpen = false;
        PageInfoBar.IsOpen = false;

        try
        {
            PageScrollViewer.ChangeView(null, 0, null, true);
        }
        catch (Exception ex)
        {
            Log.Warn("Scrolling the page to the top failed.", ex);
        }

        RefreshModelUi();
        ApplyState();
    }

    private void DeletePendingAudio()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_wavPath) && File.Exists(_wavPath))
            {
                File.Delete(_wavPath);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not delete the discarded audio file.", ex);
        }
    }

    private void OpenSessionButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_savedSession is null)
            {
                return;
            }

            if (!Frame.Navigate(typeof(SessionViewerPage), _savedSession.Id))
            {
                ShowPageError("Navigation failed", "The session viewer could not be opened.");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Opening the saved session failed.", ex);
            ShowPageError("Could not open the session", ex.Message);
        }
    }

    // =====================================================================
    // State / helper methods
    // =====================================================================

    private void OnModelStatusChanged(object? sender, EventArgs e)
    {
        var dispatcher = _dispatcher;
        dispatcher?.TryEnqueue(RefreshModelUi);
    }

    private void RefreshModelUi()
    {
        try
        {
            var ready = IsModelReady();

            ModelReadyPill.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
            ModelMissingPill.Visibility = !ready && _servicesReady ? Visibility.Visible : Visibility.Collapsed;

            if (ready)
            {
                var name = SafeModelName();
                ModelReadyText.Text = string.IsNullOrWhiteSpace(name) ? "Model ready" : $"Model ready · {name}";
                ModelInfoBar.IsOpen = false;
                DownloadProgressPanel.Visibility = Visibility.Collapsed;
                HeaderStatusText.Text = "Everything is ready.";
            }
            else
            {
                ModelInfoBar.IsOpen = _servicesReady && !_isRecording;
                HeaderStatusText.Text = _servicesReady
                    ? "A speech model is required for live transcription."
                    : "Starting speech engine…";
            }

            ApplyState();
        }
        catch (Exception ex)
        {
            Log.Warn("Refreshing the model status UI failed.", ex);
        }
    }

    private void ApplyState()
    {
        if (_dispatcher is null)
        {
            return;
        }

        try
        {
            var ready = IsModelReady();
            var anySource = MicrophoneToggle.IsOn || SystemAudioToggle.IsOn;
            var busy = _isBusy || _isSaving;
            var reviewing = _reviewVisible;

            SettingsCard.Visibility = reviewing ? Visibility.Collapsed : Visibility.Visible;
            RecordCard.Visibility = reviewing ? Visibility.Collapsed : Visibility.Visible;
            TranscriptCard.Visibility = reviewing ? Visibility.Collapsed : Visibility.Visible;
            ReviewCard.Visibility = reviewing ? Visibility.Visible : Visibility.Collapsed;

            StartButton.Visibility = _isRecording ? Visibility.Collapsed : Visibility.Visible;
            StopButton.Visibility = _isRecording ? Visibility.Visible : Visibility.Collapsed;
            StartButton.IsEnabled = ready && anySource && !busy && !_isRecording;
            StopButton.IsEnabled = _isRecording && !busy;

            MicrophoneToggle.IsEnabled = !_isRecording && !busy;
            MicrophoneCombo.IsEnabled = !_isRecording && !busy && MicrophoneToggle.IsOn;
            RefreshDevicesButton.IsEnabled = !_isRecording && !busy;
            SystemAudioToggle.IsEnabled = !_isRecording && !busy;

            ModelCombo.IsEnabled = !_isRecording && !busy;
            DownloadButton.IsEnabled = !_isRecording && !busy;
            RetryButton.IsEnabled = !_isRecording && !busy;
            ImportButton.IsEnabled = !_isRecording && !busy;

            CaptureHintText.Visibility = anySource || reviewing ? Visibility.Collapsed : Visibility.Visible;

            SaveButton.IsEnabled = !_isSaving && !_sessionSaved;
            ReviewTitleBox.IsEnabled = !_isSaving;
            ReviewNotesBox.IsEnabled = !_isSaving;
            ReviewTranscriptBox.IsEnabled = !_isSaving;
        }
        catch (Exception ex)
        {
            Log.Warn("Updating the Live Capture UI state failed.", ex);
        }
    }

    private void SetStatus(string message, bool busy)
    {
        try
        {
            var dispatcher = _dispatcher;
            if (dispatcher is not null && !dispatcher.HasThreadAccess)
            {
                dispatcher.TryEnqueue(() => SetStatus(message, busy));
                return;
            }

            StatusText.Text = message ?? "";
            BusyRing.IsActive = busy;
            BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Log.Warn("Updating the status line failed.", ex);
        }
    }

    /// <summary>Brings the page back to the top after a layout change (e.g. the setup bar closing).</summary>
    private void ScrollPageToTop()
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            try
            {
                PageScrollViewer.ChangeView(null, 0, null, true);
            }
            catch (Exception ex)
            {
                Log.Warn("Scrolling the page to the top failed.", ex);
            }
        });
    }

    private void ShowPageError(string title, string message)
    {
        try
        {
            var dispatcher = _dispatcher;
            if (dispatcher is not null && !dispatcher.HasThreadAccess)
            {
                dispatcher.TryEnqueue(() => ShowPageError(title, message));
                return;
            }

            PageInfoBar.Title = title;
            PageInfoBar.Message = message;
            PageInfoBar.Severity = InfoBarSeverity.Error;
            PageInfoBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            Log.Warn("Showing the error banner failed.", ex);
        }
    }

    private async Task<bool> ConfirmAsync(string title, string message, string primaryButtonText)
    {
        try
        {
            var xamlRoot = XamlRoot;
            if (xamlRoot is null)
            {
                return true;
            }

            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                PrimaryButtonText = primaryButtonText,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = xamlRoot
            };

            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary;
        }
        catch (Exception ex)
        {
            Log.Warn("Showing a confirmation dialog failed.", ex);
            return true;
        }
    }

    private async Task RunGuardedAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            // Navigating away cancels pending work; nothing to report.
        }
        catch (Exception ex)
        {
            Log.Error("A Live Capture action failed unexpectedly.", ex);
            ShowPageError("Something went wrong", ex.Message);
        }
    }

    private bool IsModelReady()
    {
        try
        {
            return _servicesReady
                   && AppServices.IsInitialized
                   && AppServices.Whisper is { IsReady: true };
        }
        catch
        {
            return false;
        }
    }

    private static string? SafeModelName()
    {
        try
        {
            return AppServices.IsInitialized ? AppServices.Whisper.ModelName : null;
        }
        catch
        {
            return null;
        }
    }

    private static string SafeSettingsModelName()
    {
        try
        {
            return AppServices.IsInitialized ? AppServices.Settings.Current.ModelName : "base";
        }
        catch
        {
            return "base";
        }
    }

    private static string? SafeSettingsLanguage()
    {
        try
        {
            return AppServices.IsInitialized ? AppServices.Settings.Current.Language : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? SafeSettingsMicrophoneId()
    {
        try
        {
            return AppServices.IsInitialized ? AppServices.Settings.Current.MicrophoneDeviceId : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? SafeGetAudioPath(string sessionId)
    {
        try
        {
            return AppServices.IsInitialized ? AppServices.Sessions.GetAudioFilePath(sessionId) : null;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not resolve the session audio path.", ex);
            return null;
        }
    }

    private string SafeGenerateNotes(string transcript)
    {
        try
        {
            return NotesGenerator.Generate(transcript ?? "", SessionSourceType.Live, _captureDuration);
        }
        catch (Exception ex)
        {
            Log.Warn("Generating notes for the recording failed.", ex);
            return string.IsNullOrWhiteSpace(transcript) ? "" : transcript;
        }
    }

    private static string SafeBuildTitle()
    {
        try
        {
            var title = NotesGenerator.BuildTitle(SessionSourceType.Live, DateTimeOffset.Now);
            if (!string.IsNullOrWhiteSpace(title))
            {
                return title;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not build a default session title.", ex);
        }

        return $"Live capture — {DateTimeOffset.Now:dd MMM yyyy HH:mm}";
    }

    private string SelectedModelName()
    {
        if (ModelCombo.SelectedItem is ModelChoice choice && !string.IsNullOrWhiteSpace(choice.Name))
        {
            return choice.Name;
        }

        if (ModelCombo.ItemsSource is IEnumerable<ModelChoice> choices)
        {
            var first = choices.FirstOrDefault();
            if (first is not null)
            {
                return first.Name;
            }
        }

        return "base";
    }

    private static ModelChoice? FindModelChoice(IEnumerable<ModelChoice> choices, string? name)
        => string.IsNullOrWhiteSpace(name)
            ? null
            : choices.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    private static AudioDeviceInfo? FindDevice(IEnumerable<AudioDeviceInfo> devices, string? id)
        => string.IsNullOrWhiteSpace(id)
            ? null
            : devices.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

    private static string DeriveModelName(string path)
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.StartsWith("ggml-", StringComparison.OrdinalIgnoreCase))
            {
                name = name.Substring("ggml-".Length);
            }

            return string.IsNullOrWhiteSpace(name) ? "base" : name;
        }
        catch
        {
            return "base";
        }
    }

    private static string FormatTime(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss")
            : value.ToString(@"mm\:ss");
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : class
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            var nested = FindDescendant<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>Small wrapper that pulls a download-progress callback onto the UI thread.</summary>
    private sealed class UiProgress<T> : IProgress<T>
    {
        private readonly DispatcherQueue _dispatcher;
        private readonly Action<T> _handler;

        public UiProgress(DispatcherQueue dispatcher, Action<T> handler)
        {
            _dispatcher = dispatcher;
            _handler = handler;
        }

        public void Report(T value)
        {
            _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                try
                {
                    _handler(value);
                }
                catch (Exception ex)
                {
                    Log.Warn("A progress update failed.", ex);
                }
            });
        }
    }

    /// <summary>Model picker entry (keeps DisplayMemberPath valid for fallback lists too).</summary>
    private sealed class ModelChoice
    {
        public ModelChoice(string name, string displayName)
        {
            Name = name;
            DisplayName = displayName;
        }

        public string Name { get; }

        public string DisplayName { get; }

        public override string ToString() => DisplayName;
    }
}
