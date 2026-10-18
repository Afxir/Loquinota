using System.Collections.ObjectModel;
using Loquinota.Models;
using Loquinota.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

using Loquinota.Helpers;

namespace Loquinota.Pages;

/// <summary>
/// History dashboard: summary cards, a newest-first list of saved sessions and
/// navigation into the session viewer. Reloads every time the page is navigated to
/// so returning from the viewer (or after a live capture / MP4 import) shows fresh data.
/// </summary>
public sealed partial class HistoryPage : Page
{
    private readonly ObservableCollection<HistoryItem> _items = new();
    private bool _isLoading;
    private bool _isNavigating;

    public HistoryPage()
    {
        InitializeComponent();
        SessionsList.ItemsSource = _items;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _isNavigating = false;
        _ = LoadSessionsAsync();
    }

    // ------------------------------------------------------------------ loading

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await LoadSessionsAsync();
        }
        catch (Exception ex)
        {
            ShowError("Couldn't refresh your history", ex.Message);
        }
    }

    private async Task LoadSessionsAsync()
    {
        if (_isLoading)
        {
            return;
        }

        _isLoading = true;
        SetLoading(true);
        HideStatus();

        try
        {
            if (!await EnsureServicesAsync())
            {
                UpdateEmptyState();
                return;
            }

            var sessions = await AppServices.Sessions.GetAllAsync();

            _items.Clear();
            foreach (var session in sessions.OrderByDescending(s => s.CreatedAt))
            {
                _items.Add(HistoryItem.FromSession(session));
            }

            UpdateSummary(sessions);
            UpdateEmptyState();
        }
        catch (Exception ex)
        {
            ShowError("Couldn't load your history", ex.Message);
            UpdateEmptyState();
        }
        finally
        {
            SetLoading(false);
            _isLoading = false;
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
            Log.Error("Services failed to initialise on the History page.", ex);
            ShowError("The app is still starting up",
                "Local storage could not be opened. Try Refresh in a moment. " + ex.Message);
            return false;
        }
    }

    // ------------------------------------------------------------------- summary

    private void UpdateSummary(IReadOnlyList<Session> sessions)
    {
        var totalDuration = TimeSpan.Zero;
        var live = 0;
        var mp4 = 0;

        foreach (var session in sessions)
        {
            totalDuration += session.Duration;
            if (session.SourceType == SessionSourceType.Mp4)
            {
                mp4++;
            }
            else
            {
                live++;
            }
        }

        TotalSessionsText.Text = sessions.Count.ToString("N0");
        TotalDurationText.Text = FormatDuration(totalDuration);
        LiveVsMp4Text.Text = sessions.Count == 0 ? "—" : $"{live} / {mp4}";
    }

    private void UpdateEmptyState()
    {
        EmptyStatePanel.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ navigation

    private void SessionsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is HistoryItem item)
        {
            OpenSession(item);
        }
    }

    // Backstop for Enter so keyboard users can open a session even when the
    // ListViewItem template swallows the key.
    private void SessionsList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        try
        {
            if (FocusManager.GetFocusedElement(XamlRoot) is ListViewItem { Content: HistoryItem item })
            {
                e.Handled = true;
                OpenSession(item);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not resolve the focused history row.", ex);
        }
    }

    private void OpenSession(HistoryItem item)
    {
        if (_isNavigating || string.IsNullOrWhiteSpace(item.SessionId))
        {
            return;
        }

        _isNavigating = true;
        try
        {
            Frame.Navigate(typeof(SessionViewerPage), item.SessionId);
        }
        catch (Exception ex)
        {
            _isNavigating = false;
            Log.Error("Opening the session viewer failed.", ex);
            ShowError("Couldn't open that session", ex.Message);
        }
    }

    private void StartLiveCapture_Click(object sender, RoutedEventArgs e) => NavigateTo(typeof(LiveCapturePage));

    private void ImportMp4_Click(object sender, RoutedEventArgs e) => NavigateTo(typeof(ImportMp4Page));

    private void NavigateTo(Type pageType)
    {
        try
        {
            Frame.Navigate(pageType);
        }
        catch (Exception ex)
        {
            Log.Error($"Navigation to {pageType.Name} from History failed.", ex);
            ShowError("Couldn't open that page", ex.Message);
        }
    }

    // ---------------------------------------------------------------------- chrome

    private void SetLoading(bool loading)
    {
        LoadingPanel.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.IsEnabled = !loading;
    }

    private void HideStatus() => StatusInfoBar.IsOpen = false;

    private void ShowError(string title, string message)
    {
        StatusInfoBar.Severity = InfoBarSeverity.Error;
        StatusInfoBar.Title = title;
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = true;
    }

    // ------------------------------------------------------------------- helpers

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
}

/// <summary>
/// Display model for one row of the history list. Keeps XAML bindings simple and
/// pre-formats every string so the page never needs value converters.
/// </summary>
public sealed class HistoryItem
{
    public string SessionId { get; private init; } = "";

    public string Title { get; private init; } = "";

    public string DateText { get; private init; } = "";

    public string DurationText { get; private init; } = "";

    public string SourceLabel { get; private init; } = "";

    public string WordsText { get; private init; } = "";

    public Visibility WordsVisibility { get; private init; }

    public Visibility LiveVisibility { get; private init; }

    public Visibility Mp4Visibility { get; private init; }

    public static HistoryItem FromSession(Session session)
    {
        var isMp4 = session.SourceType == SessionSourceType.Mp4;
        var words = session.WordCount;

        return new HistoryItem
        {
            SessionId = session.Id,
            Title = string.IsNullOrWhiteSpace(session.Title) ? "Untitled session" : session.Title.Trim(),
            DateText = session.CreatedAt.ToLocalTime().ToString("ddd, d MMM yyyy · HH:mm"),
            DurationText = session.DurationText,
            SourceLabel = isMp4 ? "MP4 import" : "Live capture",
            WordsText = words > 0 ? $"{words:N0} words" : "",
            WordsVisibility = words > 0 ? Visibility.Visible : Visibility.Collapsed,
            LiveVisibility = isMp4 ? Visibility.Collapsed : Visibility.Visible,
            Mp4Visibility = isMp4 ? Visibility.Visible : Visibility.Collapsed,
        };
    }
}
