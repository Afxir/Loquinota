using Loquinota.Models;
using Loquinota.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

using Loquinota.Helpers;

namespace Loquinota.Pages;

/// <summary>
/// MP4 import workflow: pick a video, probe its duration, run
/// FFmpeg preparation -> audio extraction -> Whisper transcription with live
/// progress (including ETA), then let the user review / edit the generated
/// title, transcript and notes before saving the session or exporting TXT.
/// </summary>
public sealed partial class ImportMp4Page : Page
{
    // Work bar ranges: FFmpeg 0 -> 4 (indeterminate), extract 4 -> 32, transcribe 32 -> 100.
    private const double ExtractStartFraction = 4;
    private const double ExtractEndFraction = 32;

    private string? _pickedFilePath;
    private string _pickedFileName = "";
    private long _pickedFileSize;
    private TimeSpan? _probedDuration;

    private string? _tempWavPath;
    private TimeSpan _audioDuration;
    private TranscriptionResult? _result;
    private string? _savedSessionId;

    private CancellationTokenSource? _importCts;
    private bool _isImporting;
    private bool _isSaving;
    private bool _isExporting;
    private bool _isDownloadingModel;
    private bool _isResetting;
    private bool _modelStatusSubscribed;
    private bool _suppressModelSelection;
    private string? _selectedModelName;

    public ImportMp4Page()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ = LoadAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        try
        {
            if (_modelStatusSubscribed)
            {
                AppServices.ModelStatusChanged -= OnModelStatusChanged;
                _modelStatusSubscribed = false;
            }
        }
        catch (Exception ex)
        {
           Log.Warn("Unsubscribing from model status changes failed.", ex);
        }

        try
        {
            _importCts?.Cancel();
        }
        catch
        {
            // Cancelling is best-effort; the page is already leaving.
        }

        // Never leave an imported-but-unsaved temp WAV behind.
        if (_savedSessionId is null)
        {
            DeleteTempWav();
        }
    }

    // -------------------------------------------------------------- page startup

    private async Task LoadAsync()
    {
        try
        {
            if (!await EnsureServicesAsync())
            {
                DownloadModelButton.IsEnabled = false;
                ModelStatusText.Text = "Local services are still starting…";
                return;
            }

            // AppServices.EnsureInitializedAsync() returns before the preloaded model has
            // finished loading, so subscribe and re-check instead of trusting the first read.
            SubscribeModelStatus();

            LoadModels();
            RefreshModelState();
        }
        catch (Exception ex)
        {
            Log.Error("Initialising the MP4 importer page failed.", ex);
            ShowError("Couldn't initialise the importer", ex.Message);
        }
    }

    private async Task<bool> EnsureServicesAsync()
    {
        if (AppServices.IsInitialized)
        {
            return true;
        }

        try
        {
            await AppServices.EnsureInitializedAsync();
            return AppServices.IsInitialized;
        }
        catch (Exception ex)
        {
            Log.Error("Services failed to initialise on the Import MP4 page.", ex);
            ShowError("The app is still starting up",
                "Local storage could not be opened. Try again in a moment. " + ex.Message);
            return false;
        }
    }

    // ---------------------------------------------------------------- model setup

    private void SubscribeModelStatus()
    {
        if (_modelStatusSubscribed)
        {
            return;
        }

        try
        {
            AppServices.ModelStatusChanged += OnModelStatusChanged;
            _modelStatusSubscribed = true;
        }
        catch (Exception ex)
        {
            Log.Warn("Subscribing to model status changes failed.", ex);
        }
    }

    private void OnModelStatusChanged(object? sender, EventArgs e)
    {
        // The event may be raised from a background load; marshal to the UI thread.
        var dispatcher = DispatcherQueue;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.TryEnqueue(RefreshModelState);
    }

    /// <summary>
    /// Fills the model picker: installed models are marked, and the selection defaults to the
    /// stored preference (or the model that is actually loaded when the stored one is missing).
    /// </summary>
    private void LoadModels()
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

        var wanted = _selectedModelName ?? PreferredModelName();
        _suppressModelSelection = true;
        try
        {
            ModelCombo.ItemsSource = list;
            ModelCombo.SelectedItem = FindModelChoice(list, wanted) ?? list[0];
            _selectedModelName = (ModelCombo.SelectedItem as ModelChoice)?.Name ?? wanted;
        }
        finally
        {
            _suppressModelSelection = false;
        }
    }

    private string PreferredModelName()
    {
        var stored = SafeSettingsModelName();

        try
        {
            if (!string.IsNullOrWhiteSpace(stored) && AppServices.Models.IsInstalled(stored))
            {
                return stored;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not check whether the stored speech model is installed.", ex);
        }

        try
        {
            if (AppServices.Whisper is { IsReady: true } && !string.IsNullOrWhiteSpace(AppServices.Whisper.ModelName))
            {
                return AppServices.Whisper.ModelName;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the loaded speech model name.", ex);
        }

        return string.IsNullOrWhiteSpace(stored) ? "base" : stored;
    }

    private string SafeSettingsModelName()
    {
        try
        {
            if (AppServices.IsInitialized && !string.IsNullOrWhiteSpace(AppServices.Settings.Current.ModelName))
            {
                return AppServices.Settings.Current.ModelName;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the stored speech model name.", ex);
        }

        return "base";
    }

    private static ModelChoice? FindModelChoice(IEnumerable<ModelChoice> choices, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return choices.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private void ModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressModelSelection || ModelCombo.SelectedItem is not ModelChoice choice)
        {
            return;
        }

        _selectedModelName = choice.Name;

        try
        {
            if (AppServices.IsInitialized)
            {
                AppServices.Settings.Current.ModelName = choice.Name;
                _ = PersistModelSelectionAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not persist the selected speech model.", ex);
        }

        RefreshModelState();
    }

    private async Task PersistModelSelectionAsync()
    {
        try
        {
            await AppServices.Settings.SaveAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not save the selected speech model.", ex);
        }
    }

    /// <summary>
    /// Re-evaluates readiness for the model that the next import will actually use and
    /// updates the banner, the model card and the import button. Called on load, when the
    /// model event fires, after downloads and after the selected model changes.
    /// </summary>
    private void RefreshModelState()
    {
        if (!AppServices.IsInitialized)
        {
            ModelInfoBar.IsOpen = false;
            ModelStatusText.Text = "Preparing local services…";
            DownloadModelButton.IsEnabled = false;
            StartImportButton.IsEnabled = false;
            return;
        }

        try
        {
            var selected = _selectedModelName ?? SafeSettingsModelName();
            var loaded = AppServices.Whisper is { IsReady: true } ? AppServices.Whisper.ModelName : null;
            var ready = loaded is not null && string.Equals(loaded, selected, StringComparison.OrdinalIgnoreCase);

            ModelInfoBar.IsOpen = !ready;

            if (ready)
            {
                ModelStatusText.Text = $"Ready · {loaded}. Everything runs locally on this PC.";
                DownloadModelButton.IsEnabled = false;
                DownloadModelLabel.Text = "Download model";
                ModelProgressPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                var installed = IsModelInstalled(selected);
                DownloadModelLabel.Text = installed ? "Load model" : "Download model";

                ModelInfoBar.Title = "Speech model not ready";
                ModelInfoBar.Message =
                    $"The “{selected}” model is required to transcribe. Download it now, or prepare " +
                    "a model on the Live Capture page — starting an import also prepares it automatically.";

                ModelStatusText.Text = loaded is null
                    ? $"Not loaded — “{selected}” is prepared on first use."
                    : $"“{loaded}” is loaded, but “{selected}” is selected. Import will switch models.";

                DownloadModelButton.IsEnabled = !_isDownloadingModel;
                ModelProgressPanel.Visibility = _isDownloadingModel ? Visibility.Visible : Visibility.Collapsed;
            }

            StartImportButton.IsEnabled = _pickedFilePath is not null && !_isImporting;
        }
        catch (Exception ex)
        {
            Log.Warn("Refreshing the speech model UI failed.", ex);
        }
    }

    private bool IsModelInstalled(string modelName)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(modelName) && AppServices.Models.IsInstalled(modelName);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not check whether a speech model is installed.", ex);
            return false;
        }
    }

    private async void DownloadModel_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await DownloadModelAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Downloading the speech model failed.", ex);
            ShowError("Model download failed", ex.Message);
        }
    }

    private async Task DownloadModelAsync()
    {
        if (_isDownloadingModel || !await EnsureServicesAsync())
        {
            return;
        }

        var modelName = _selectedModelName ?? SafeSettingsModelName();

        _isDownloadingModel = true;
        DownloadModelButton.IsEnabled = false;
        ModelProgressPanel.Visibility = Visibility.Visible;
        ModelProgressText.Text = IsModelInstalled(modelName)
            ? $"Loading the “{modelName}” model…"
            : $"Downloading the “{modelName}” model…";
        ModelDownloadBar.Value = 0;
        HideStatus();

        try
        {
            var progress = new Progress<ModelDownloadProgress>(p =>
            {
                ModelDownloadBar.Value = Math.Clamp(p.Fraction * 100, 0, 100);
                ModelProgressText.Text = string.IsNullOrWhiteSpace(p.Status)
                    ? (string.IsNullOrWhiteSpace(p.BytesText) ? "Preparing the speech model…" : p.BytesText)
                    : p.Status;
            });

            // EnsureModelLoadedAsync downloads when needed AND loads the model into Whisper,
            // so by the time it returns true the import can run without another wait.
            var ready = await AppServices.EnsureModelLoadedAsync(modelName, progress);

            if (ready)
            {
                ShowSuccess("Speech model ready",
                    string.IsNullOrWhiteSpace(AppServices.Whisper.ModelName)
                        ? "You can import an MP4 now."
                        : $"Using the “{AppServices.Whisper.ModelName}” model. Import an MP4 now.");
            }
            else
            {
                ShowError("Model download failed",
                    "Check your connection and try again, or prepare a model on the Live Capture page.");
            }
        }
        finally
        {
            _isDownloadingModel = false;
            ModelProgressPanel.Visibility = Visibility.Collapsed;
            RefreshModelState();
        }
    }

    // ------------------------------------------------------------------ file pick

    private async void ChooseFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await PickFileAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Picking an MP4 failed.", ex);
            ShowError("Couldn't open the file picker", ex.Message);
        }
    }

    private async Task PickFileAsync()
    {
        if (_isImporting || !await EnsureServicesAsync())
        {
            return;
        }

        var window = App.Current.MainWindow;
        if (window is null)
        {
            ShowError("File picker unavailable", "The main window is not ready yet. Try again in a moment.");
            return;
        }

        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.VideosLibrary,
            ViewMode = PickerViewMode.List,
        };
        picker.FileTypeFilter.Add(".mp4");

        // WinUI desktop pickers need an owner HWND or they throw at show time.
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        ClearWorkState();

        _pickedFilePath = file.Path;
        _pickedFileName = file.Name;
        _pickedFileSize = 0;
        _probedDuration = null;

        SelectedFileText.Text = file.Name;
        SelectedFileMetaText.Text = "Reading the video information…";
        StartImportButton.IsEnabled = false;
        HideStatus();

        try
        {
            var properties = await file.GetBasicPropertiesAsync();
            _pickedFileSize = (long)properties.Size;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the selected file size.", ex);
        }

        try
        {
            _probedDuration = await AppServices.Mp4.ProbeDurationAsync(_pickedFilePath);
        }
        catch (Exception ex)
        {
            // Probing needs FFmpeg, which may not be prepared yet; the import
            // still works and reports the duration after extraction.
            Log.Warn("Probing the MP4 duration failed; it will be detected during import.", ex);
        }

        var details = new List<string>();
        if (_pickedFileSize > 0)
        {
            details.Add(FormatBytes(_pickedFileSize));
        }

        details.Add(_probedDuration is { } duration && duration > TimeSpan.Zero
            ? FormatDuration(duration)
            : "duration detected during import");

        SelectedFileMetaText.Text = string.Join(" · ", details) +
                                    " — the soundtrack is converted to 16 kHz mono WAV.";

        StartImportButton.IsEnabled = true;
    }

    // -------------------------------------------------------------------- pipeline

    private async void StartImport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RunImportAsync();
        }
        catch (Exception ex)
        {
            Log.Error("MP4 import failed.", ex);
            ShowError("Import failed", ex.Message);
            ProgressCard.Visibility = Visibility.Collapsed;
        }
    }

    private async Task RunImportAsync()
    {
        if (_isImporting || _pickedFilePath is null || !await EnsureServicesAsync())
        {
            return;
        }

        _isImporting = true;
        _importCts = new CancellationTokenSource();
        var token = _importCts.Token;

        SetImportingUi(true);
        HideStatus();
        ProgressCard.Visibility = Visibility.Visible;

        try
        {
            // 0. Speech model for the selected model (downloads and loads on demand).
            var selectedModel = _selectedModelName ?? SafeSettingsModelName();
            var loadedModel = AppServices.Whisper is { IsReady: true } ? AppServices.Whisper.ModelName : null;
            var modelReadyForSelected = loadedModel is not null &&
                                        string.Equals(loadedModel, selectedModel, StringComparison.OrdinalIgnoreCase);

            if (!modelReadyForSelected)
            {
                UpdatePhase("Preparing the speech model…",
                    $"The “{selectedModel}” model is downloaded and loaded once, then reused offline.");
                SetWorkBar(0, indeterminate: true);

                var downloadProgress = new Progress<ModelDownloadProgress>(p =>
                {
                    ModelProgressPanel.Visibility = Visibility.Visible;
                    ModelDownloadBar.Value = Math.Clamp(p.Fraction * 100, 0, 100);
                    ModelProgressText.Text = string.IsNullOrWhiteSpace(p.Status) ? p.BytesText : p.Status;
                    if (!string.IsNullOrWhiteSpace(p.BytesText))
                    {
                        PhaseDetailText.Text = "Preparing the speech model · " + p.BytesText;
                    }
                });

                var modelReady = await AppServices.EnsureModelLoadedAsync(selectedModel, downloadProgress);
                ModelProgressPanel.Visibility = Visibility.Collapsed;

                if (!modelReady || AppServices.Whisper is not { IsReady: true })
                {
                    ShowError("Speech model unavailable",
                        "Download or import a Whisper model on the Live Capture page, then try the import again.");
                    RefreshModelState();
                    ProgressCard.Visibility = Visibility.Collapsed;
                    return;
                }

                RefreshModelState();
            }

            // 1. Prepare FFmpeg (first run downloads it).
            UpdatePhase("Preparing FFmpeg…",
                "FFmpeg extracts the audio track. This is prepared once per machine.");
            SetWorkBar(0, indeterminate: true);
            PhaseEtaText.Text = "";

            await AppServices.Mp4.EnsureFfmpegAsync(new Progress<string>(message =>
            {
                PhaseDetailText.Text = string.IsNullOrWhiteSpace(message)
                    ? "Preparing the audio extractor…"
                    : message;
            }), token);

            // 2. Extract the audio track.
            UpdatePhase("Extracting audio…",
                "Converting the video soundtrack to 16 kHz mono WAV.");
            SetWorkBar(ExtractStartFraction, indeterminate: false);
            PhaseEtaText.Text = "";

            Directory.CreateDirectory(AppStorage.TempDirectory);
            var tempWav = Path.Combine(AppStorage.TempDirectory, $"mp4-import-{Guid.NewGuid():N}.wav");
            _tempWavPath = tempWav;

            var extractionProgress = new Progress<ProcessingProgress>(p =>
            {
                var fraction = Math.Clamp(p.Fraction, 0, 1);
                SetWorkBar(ExtractStartFraction + (ExtractEndFraction - ExtractStartFraction) * fraction,
                    indeterminate: false);
                if (!string.IsNullOrWhiteSpace(p.Status))
                {
                    PhaseDetailText.Text = p.Status;
                }

                SetEta(p.EstimatedRemaining);
            });

            var extraction = await AppServices.Mp4.ExtractAudioAsync(
                _pickedFilePath, tempWav, extractionProgress, token);

            if (!string.IsNullOrWhiteSpace(extraction.WavFilePath))
            {
                _tempWavPath = extraction.WavFilePath;
            }

            _audioDuration = extraction.Duration > TimeSpan.Zero
                ? extraction.Duration
                : _probedDuration ?? TimeSpan.Zero;

            // 3. Transcribe with Whisper.
            UpdatePhase("Transcribing…",
                "Whisper is turning speech into text on this PC. This can take a while for long videos.");
            SetWorkBar(ExtractEndFraction, indeterminate: false);
            PhaseEtaText.Text = "";

            var transcriptionProgress = new Progress<TranscriptionProgress>(p =>
            {
                var fraction = Math.Clamp(p.Fraction, 0, 1);
                SetWorkBar(ExtractEndFraction + (100 - ExtractEndFraction) * fraction, indeterminate: false);
                if (!string.IsNullOrWhiteSpace(p.Status))
                {
                    PhaseDetailText.Text = p.Status;
                }

                SetEta(p.EstimatedRemaining);

                if (p.EstimatedRemaining is { } eta && eta > TimeSpan.Zero)
                {
                    PhaseText.Text = $"Transcribing… (estimated {FormatDuration(eta)} remaining)";
                }
            });

            var result = await AppServices.Whisper.TranscribeWavAsync(
                _tempWavPath, transcriptionProgress, token);

            _result = result;
            if (_audioDuration <= TimeSpan.Zero && result.Duration > TimeSpan.Zero)
            {
                _audioDuration = result.Duration;
            }

            // 4. Build the editable review state.
            var notes = NotesGenerator.Generate(
                result.Text,
                SessionSourceType.Mp4,
                _audioDuration,
                result.Segments,
                _pickedFileName);
            var title = NotesGenerator.BuildTitle(
                SessionSourceType.Mp4, DateTimeOffset.Now, _pickedFileName);

            _savedSessionId = null;
            TitleBox.Text = title;
            TranscriptBox.Text = result.Text;
            NotesBox.Text = notes;
            UpdateTranscriptStats();
            UpdateReviewSummary(result);

            ReviewPanel.Visibility = Visibility.Visible;
            ProgressCard.Visibility = Visibility.Collapsed;
            SetWorkBar(100, indeterminate: false);

            ShowSuccess("Transcription ready",
                "Review and edit the text below, then save the session to keep the imported audio.");
        }
        catch (OperationCanceledException)
        {
            ProgressCard.Visibility = Visibility.Collapsed;
            SetWorkBar(0, indeterminate: false);
            if (_result is null)
            {
                DeleteTempWav();
            }

            ShowInfo("Import cancelled", "The MP4 was not imported. Your file selection is still available.");
        }
        catch (Exception ex)
        {
            Log.Error("MP4 import failed.", ex);
            ProgressCard.Visibility = Visibility.Collapsed;
            SetWorkBar(0, indeterminate: false);
            if (_result is null)
            {
                DeleteTempWav();
            }

            ShowError("Import failed", ex.Message);
        }
        finally
        {
            _isImporting = false;
            try
            {
                _importCts?.Cancel();
            }
            catch
            {
                // Ignore: the source is already finished.
            }

            _importCts?.Dispose();
            _importCts = null;
            SetImportingUi(false);
        }
    }

    private void CancelImport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _importCts?.Cancel();
            PhaseDetailText.Text = "Cancelling…";
        }
        catch (Exception ex)
        {
            Log.Warn("Cancelling the import failed.", ex);
        }
    }

    // ------------------------------------------------------------------ review/save

    private void Review_TextChanged(object sender, TextChangedEventArgs e) => UpdateTranscriptStats();

    private void UpdateTranscriptStats()
    {
        var text = TranscriptBox.Text ?? "";
        var words = string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

        TranscriptStatsText.Text = words == 0 ? "Empty" : $"{words:N0} words";
    }

    private void UpdateReviewSummary(TranscriptionResult result)
    {
        var parts = new List<string>();

        if (_audioDuration > TimeSpan.Zero)
        {
            parts.Add(FormatDuration(_audioDuration));
        }

        if (!string.IsNullOrWhiteSpace(result.ModelName))
        {
            parts.Add($"model {result.ModelName}");
        }

        if (!string.IsNullOrWhiteSpace(result.Language) && !string.Equals(result.Language, "auto", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(result.Language);
        }

        if (_pickedFileSize > 0)
        {
            parts.Add(FormatBytes(_pickedFileSize));
        }

        ReviewSummaryText.Text = parts.Count == 0
            ? "Audio extracted locally. Nothing was uploaded."
            : string.Join(" · ", parts) + " — processed locally, nothing was uploaded.";
    }

    private async void SaveSession_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveSessionAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Saving the imported session failed.", ex);
            ShowError("Couldn't save the session", ex.Message);
        }
    }

    private async Task SaveSessionAsync()
    {
        if (_result is null || _isSaving || !await EnsureServicesAsync())
        {
            return;
        }

        if (_savedSessionId is null && _tempWavPath is null)
        {
            ShowError("Couldn't save the session", "The extracted audio is no longer available. Import the file again.");
            return;
        }

        _isSaving = true;
        SaveSessionButton.IsEnabled = false;
        HideStatus();

        try
        {
            var sessionId = _savedSessionId ?? Guid.NewGuid().ToString("N");
            var isFirstSave = _savedSessionId is null;

            var session = new Session
            {
                Id = sessionId,
                Title = string.IsNullOrWhiteSpace(TitleBox.Text)
                    ? NotesGenerator.BuildTitle(SessionSourceType.Mp4, DateTimeOffset.Now, _pickedFileName)
                    : TitleBox.Text.Trim(),
                CreatedAt = DateTimeOffset.Now,
                Duration = _audioDuration,
                SourceType = SessionSourceType.Mp4,
                SourceFileName = _pickedFileName,
                TranscriptText = TranscriptBox.Text ?? "",
                NotesText = NotesBox.Text ?? "",
                ModelName = !string.IsNullOrWhiteSpace(_result.ModelName)
                    ? _result.ModelName
                    : AppServices.Whisper.ModelName,
                Language = !string.IsNullOrWhiteSpace(_result.Language)
                    ? _result.Language
                    : AppServices.Settings.Current.Language,
            };

            // Re-saving keeps the original creation date and the already imported audio.
            var existing = await AppServices.Sessions.GetAsync(sessionId);
            if (existing is not null)
            {
                session.CreatedAt = existing.CreatedAt;
                if (!string.IsNullOrWhiteSpace(existing.AudioFilePath) && !isFirstSave)
                {
                    session.AudioFilePath = existing.AudioFilePath;
                }
            }

            if (string.IsNullOrWhiteSpace(session.AudioFilePath))
            {
                if (_tempWavPath is null)
                {
                    ShowError("Couldn't save the session",
                        "The extracted audio is no longer available. Import the file again.");
                    return;
                }

                var audioPath = await AppServices.Sessions.ImportAudioAsync(sessionId, _tempWavPath);
                session.AudioFilePath = string.IsNullOrWhiteSpace(audioPath)
                    ? AppServices.Sessions.GetAudioFilePath(sessionId)
                    : audioPath;

                // The audio now lives in the sessions folder; the temp copy can go.
                if (isFirstSave)
                {
                    DeleteTempWav();
                }
            }

            await AppServices.Sessions.UpsertAsync(session);

            _savedSessionId = session.Id;
            SaveSessionLabel.Text = "Save changes";
            ViewSessionButton.Visibility = Visibility.Visible;

            ShowSuccess("Session saved",
                "The MP4 session is in your History, with its audio copied into the app's sessions folder.");
        }
        finally
        {
            _isSaving = false;
            SaveSessionButton.IsEnabled = true;
        }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ExportAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Exporting the imported session failed.", ex);
            ShowError("Couldn't export the transcript", ex.Message);
        }
    }

    private async Task ExportAsync()
    {
        if (_result is null || _isExporting || !await EnsureServicesAsync())
        {
            return;
        }

        var window = App.Current.MainWindow;
        if (window is null)
        {
            ShowError("Export unavailable", "The main window is not ready yet. Try again in a moment.");
            return;
        }

        _isExporting = true;
        try
        {
            var snapshot = new Session
            {
                Id = _savedSessionId ?? Guid.NewGuid().ToString("N"),
                Title = string.IsNullOrWhiteSpace(TitleBox.Text) ? "MP4 import" : TitleBox.Text.Trim(),
                CreatedAt = DateTimeOffset.Now,
                Duration = _audioDuration,
                SourceType = SessionSourceType.Mp4,
                SourceFileName = _pickedFileName,
                TranscriptText = TranscriptBox.Text ?? "",
                NotesText = NotesBox.Text ?? "",
                ModelName = !string.IsNullOrWhiteSpace(_result.ModelName)
                    ? _result.ModelName
                    : AppServices.Whisper.ModelName,
                Language = !string.IsNullOrWhiteSpace(_result.Language)
                    ? _result.Language
                    : AppServices.Settings.Current.Language,
            };

            var exported = await AppServices.Export.ExportSessionAsync(snapshot, window, includeTranscript: true);
            if (exported)
            {
                ShowSuccess("Exported to TXT", "The transcript and notes were written to the file you chose.");
            }
            else
            {
                ShowInfo("Export cancelled", "No file was written.");
            }
        }
        finally
        {
            _isExporting = false;
        }
    }

    private void ViewSession_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_savedSessionId))
        {
            return;
        }

        try
        {
            Frame.Navigate(typeof(SessionViewerPage), _savedSessionId);
        }
        catch (Exception ex)
        {
            Log.Error("Opening the saved session failed.", ex);
            ShowError("Couldn't open the saved session", ex.Message);
        }
    }

    // --------------------------------------------------------------------- reset

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_isImporting)
            {
                _importCts?.Cancel();
                ShowInfo("Import cancelled", "Starting over once the current import has stopped.");
                return;
            }

            if (_isResetting)
            {
                return;
            }

            _isResetting = true;
            try
            {
                if (ReviewPanel.Visibility == Visibility.Visible && _savedSessionId is null)
                {
                    var dialog = new ContentDialog
                    {
                        Title = "Discard this import?",
                        Content = "The transcription has not been saved yet. Starting over removes it and the extracted audio.",
                        PrimaryButtonText = "Discard",
                        CloseButtonText = "Keep editing",
                        DefaultButton = ContentDialogButton.Close,
                        XamlRoot = XamlRoot,
                    };

                    if (Application.Current.Resources["MaterialDangerButtonStyle"] is Style dangerStyle)
                    {
                        dialog.PrimaryButtonStyle = dangerStyle;
                    }

                    if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                    {
                        return;
                    }
                }

                ClearWorkState();
                HideStatus();
            }
            finally
            {
                _isResetting = false;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Resetting the MP4 importer failed.", ex);
            ShowError("Couldn't reset the importer", ex.Message);
        }
    }

    private void ClearWorkState()
    {
        DeleteTempWav();

        _pickedFilePath = null;
        _pickedFileName = "";
        _pickedFileSize = 0;
        _probedDuration = null;
        _audioDuration = TimeSpan.Zero;
        _result = null;
        _savedSessionId = null;

        SelectedFileText.Text = "No file selected";
        SelectedFileMetaText.Text = "MP4 video — the soundtrack is converted to 16 kHz mono WAV.";
        TitleBox.Text = "";
        TranscriptBox.Text = "";
        NotesBox.Text = "";
        TranscriptStatsText.Text = "";

        ReviewPanel.Visibility = Visibility.Collapsed;
        ProgressCard.Visibility = Visibility.Collapsed;
        PhaseText.Text = "Preparing FFmpeg…";
        PhaseDetailText.Text = "";
        PhaseEtaText.Text = "";
        SetWorkBar(0, indeterminate: false);

        SaveSessionButton.IsEnabled = true;
        SaveSessionLabel.Text = "Save session";
        ViewSessionButton.Visibility = Visibility.Collapsed;
        StartImportButton.IsEnabled = false;
        CancelImportButton.Visibility = Visibility.Collapsed;
    }

    // ------------------------------------------------------------------- helpers

    private void SetImportingUi(bool importing)
    {
        ChooseFileButton.IsEnabled = !importing;
        StartImportButton.IsEnabled = !importing && _pickedFilePath is not null;
        CancelImportButton.Visibility = importing ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdatePhase(string title, string detail)
    {
        ProgressCard.Visibility = Visibility.Visible;
        PhaseText.Text = title;
        PhaseDetailText.Text = detail;
        PhaseEtaText.Text = "";
    }

    private void SetWorkBar(double value, bool indeterminate)
    {
        WorkBar.IsIndeterminate = indeterminate;
        if (indeterminate)
        {
            WorkBar.Value = 0;
            return;
        }

        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            value = 0;
        }

        WorkBar.Value = Math.Clamp(value, WorkBar.Minimum, WorkBar.Maximum);
    }

    private void SetEta(TimeSpan? remaining)
    {
        PhaseEtaText.Text = remaining is { } eta && eta > TimeSpan.Zero
            ? $"about {FormatDuration(eta)} left"
            : "";
    }

    private void DeleteTempWav()
    {
        var path = _tempWavPath;
        _tempWavPath = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            // A locked file is safe to leave for a later cleanup pass.
            Log.Warn("Could not delete the temporary WAV file.", ex);
        }
    }

    private void HideStatus() => StatusInfoBar.IsOpen = false;

    private void ShowError(string title, string message) => ShowStatus(InfoBarSeverity.Error, title, message);

    private void ShowSuccess(string title, string message) => ShowStatus(InfoBarSeverity.Success, title, message);

    private void ShowInfo(string title, string message) => ShowStatus(InfoBarSeverity.Informational, title, message);

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusInfoBar.Severity = severity;
        StatusInfoBar.Title = title;
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = true;
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return "0:00";
        }

        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes}:{duration.Seconds:00}";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "";
        }

        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
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
