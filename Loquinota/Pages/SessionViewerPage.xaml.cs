using Loquinota.Models;
using Loquinota.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

using Loquinota.Helpers;

namespace Loquinota.Pages;

/// <summary>
/// Session viewer/editor: loads one session by id, lets the user edit its title,
/// notes and transcript, save the changes, export the session to TXT, or delete it.
/// Shows a friendly "not found" state when the id does not resolve.
/// </summary>
public sealed partial class SessionViewerPage : Page
{
    private string? _sessionId;
    private Session? _session;
    private bool _isPopulating;
    private bool _isBusy;

    public SessionViewerPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _sessionId = e.Parameter as string;
        _ = LoadAsync();
    }

    // -------------------------------------------------------------------- loading

    private async Task LoadAsync()
    {
        SetLoading(true);
        HideStatus();
        EditorRoot.Visibility = Visibility.Collapsed;
        NotFoundCard.Visibility = Visibility.Collapsed;
        SetEditorEnabled(false);

        try
        {
            if (!await EnsureServicesAsync())
            {
                ShowNotFound("Couldn't open the session",
                    "Local storage is not available yet. Try again in a moment.");
                return;
            }

            if (string.IsNullOrWhiteSpace(_sessionId))
            {
                ShowNotFound("Session not found",
                    "No session was specified. Go back to History to pick one.");
                return;
            }

            var session = await AppServices.Sessions.GetAsync(_sessionId);
            if (session is null)
            {
                ShowNotFound("Session not found",
                    "It may have been deleted. Go back to History to pick another session.");
                return;
            }

            Populate(session);
        }
        catch (Exception ex)
        {
            Log.Error("Opening the session failed.", ex);
            ShowNotFound("Couldn't open the session", ex.Message);
            ShowError("Couldn't open the session", ex.Message);
        }
        finally
        {
            SetLoading(false);
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
            Log.Error("Services failed to initialise on the session viewer.", ex);
            ShowError("The app is still starting up",
                "Local storage could not be opened. Try again in a moment. " + ex.Message);
            return false;
        }
    }

    private void Populate(Session session)
    {
        _session = session;

        _isPopulating = true;
        try
        {
            TitleBox.Text = session.Title;
            NotesBox.Text = session.NotesText;
            TranscriptBox.Text = session.TranscriptText;
        }
        finally
        {
            _isPopulating = false;
        }

        HeaderTitleText.Text = DisplayTitle(session.Title);
        HeaderSubtitleText.Text = $"{session.SourceLabel} · {session.CreatedAtText}";

        DatePillText.Text = session.CreatedAtText;
        DurationPillText.Text = session.DurationText;
        SourcePillText.Text = session.SourceLabel;

        var modelParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(session.ModelName))
        {
            modelParts.Add($"model {session.ModelName}");
        }

        if (!string.IsNullOrWhiteSpace(session.Language) &&
            !string.Equals(session.Language, "auto", StringComparison.OrdinalIgnoreCase))
        {
            modelParts.Add(session.Language);
        }

        ModelPillText.Text = modelParts.Count > 0 ? string.Join(" · ", modelParts) : "model unknown";

        UpdateTranscriptStats();
        SaveStateText.Text = "No unsaved changes";

        EditorRoot.Visibility = Visibility.Visible;
        NotFoundCard.Visibility = Visibility.Collapsed;
        SetEditorEnabled(true);
    }

    private void ShowNotFound(string title, string message)
    {
        NotFoundTitleText.Text = title;
        NotFoundMessageText.Text = message;
        NotFoundCard.Visibility = Visibility.Visible;
        EditorRoot.Visibility = Visibility.Collapsed;
        HeaderTitleText.Text = "Session";
        HeaderSubtitleText.Text = "Not available";
        SetEditorEnabled(false);
    }

    // ---------------------------------------------------------------------- editing

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isPopulating || _session is null)
        {
            return;
        }

        HeaderTitleText.Text = DisplayTitle(TitleBox.Text);
        SaveStateText.Text = "Unsaved changes";
        UpdateTranscriptStats();
    }

    private void UpdateTranscriptStats()
    {
        var text = TranscriptBox.Text ?? "";
        var words = string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

        TranscriptStatsText.Text = words == 0 ? "Empty" : $"{words:N0} words";
    }

    private void SetEditorEnabled(bool enabled)
    {
        SaveButton.IsEnabled = enabled;
        SaveFooterButton.IsEnabled = enabled;
        ExportButton.IsEnabled = enabled;
        ExportFooterButton.IsEnabled = enabled;
        DeleteButton.IsEnabled = enabled;
    }

    private string CurrentTitle()
    {
        if (!string.IsNullOrWhiteSpace(TitleBox.Text))
        {
            return TitleBox.Text.Trim();
        }

        return _session is null
            ? "Untitled session"
            : NotesGenerator.BuildTitle(_session.SourceType, _session.CreatedAt, _session.SourceFileName);
    }

    // ------------------------------------------------------------------------- save

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Saving the session failed.", ex);
            ShowError("Couldn't save the session", ex.Message);
        }
    }

    private async Task SaveAsync()
    {
        if (_session is null || _isBusy || !await EnsureServicesAsync())
        {
            return;
        }

        _isBusy = true;
        SaveButton.IsEnabled = false;
        SaveFooterButton.IsEnabled = false;
        HideStatus();

        try
        {
            var updated = _session.Clone();
            updated.Title = CurrentTitle();
            updated.NotesText = NotesBox.Text ?? "";
            updated.TranscriptText = TranscriptBox.Text ?? "";

            await AppServices.Sessions.UpsertAsync(updated);
            _session = updated;

            _isPopulating = true;
            try
            {
                TitleBox.Text = updated.Title;
            }
            finally
            {
                _isPopulating = false;
            }

            HeaderTitleText.Text = DisplayTitle(updated.Title);
            SaveStateText.Text = $"All changes saved · {DateTime.Now:HH:mm}";
            ShowSuccess("Session saved", "Your title, notes and transcript were saved on this PC.");
        }
        finally
        {
            _isBusy = false;
            SaveButton.IsEnabled = true;
            SaveFooterButton.IsEnabled = true;
        }
    }

    // ----------------------------------------------------------------------- export

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ExportAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Exporting the session failed.", ex);
            ShowError("Couldn't export the session", ex.Message);
        }
    }

    private async Task ExportAsync()
    {
        if (_session is null || _isBusy || !await EnsureServicesAsync())
        {
            return;
        }

        var window = App.Current.MainWindow;
        if (window is null)
        {
            ShowError("Export unavailable", "The main window is not ready yet. Try again in a moment.");
            return;
        }

        _isBusy = true;
        try
        {
            // Export what is on screen, even when it has not been saved yet.
            var snapshot = _session.Clone();
            snapshot.Title = CurrentTitle();
            snapshot.NotesText = NotesBox.Text ?? "";
            snapshot.TranscriptText = TranscriptBox.Text ?? "";

            var exported = await AppServices.Export.ExportSessionAsync(snapshot, window, includeTranscript: true);
            if (exported)
            {
                ShowSuccess("Exported to TXT", "The notes and transcript were written to the file you chose.");
            }
            else
            {
                ShowInfo("Export cancelled", "No file was written.");
            }
        }
        finally
        {
            _isBusy = false;
        }
    }

    // ----------------------------------------------------------------------- delete

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await DeleteAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Deleting the session failed.", ex);
            ShowError("Couldn't delete the session", ex.Message);
        }
    }

    private async Task DeleteAsync()
    {
        if (_session is null || _isBusy || !await EnsureServicesAsync())
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Delete this session?",
            Content = "The transcript, notes and imported audio will be permanently removed from this PC. " +
                      "This cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
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

        _isBusy = true;
        try
        {
            var id = _session.Id;
            await AppServices.Sessions.DeleteAsync(id);
            _session = null;
            GoBack();
        }
        finally
        {
            _isBusy = false;
        }
    }

    // ------------------------------------------------------------------ navigation

    private void Back_Click(object sender, RoutedEventArgs e) => GoBack();

    private void BackToHistory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Frame.Navigate(typeof(HistoryPage));
        }
        catch (Exception ex)
        {
            Log.Error("Navigation back to History failed.", ex);
            ShowError("Couldn't go back", ex.Message);
        }
    }

    private void GoBack()
    {
        try
        {
            if (Frame.CanGoBack)
            {
                Frame.GoBack();
                return;
            }

            Frame.Navigate(typeof(HistoryPage));
        }
        catch (Exception ex)
        {
            Log.Error("Navigation back from the session viewer failed.", ex);
            ShowError("Couldn't go back", ex.Message);
        }
    }

    // ---------------------------------------------------------------------- chrome

    private void SetLoading(bool loading)
    {
        LoadingPanel.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
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

    private static string DisplayTitle(string? title) =>
        string.IsNullOrWhiteSpace(title) ? "Untitled session" : title.Trim();
}
