using Loquinota.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

using Loquinota.Helpers;

namespace Loquinota;

/// <summary>
/// Material-styled shell: custom title bar, Mica backdrop and a NavigationView
/// hosting the History / Live Capture / Import MP4 pages.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = "loquinota";

        try
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not enable the custom title bar.", ex);
        }

        ConfigureWindow();

        if (BackdropHelper.TryApply(this))
        {
            // Let Mica / Acrylic show through; pages provide their own tonal surfaces.
            RootGrid.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }

        NavView.SelectedItem = HistoryNavItem;
        RootGrid.Loaded += OnRootLoaded;
    }

    private bool _startupNavigationDone;

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        if (_startupNavigationDone)
        {
            return;
        }

        _startupNavigationDone = true;
        NavigateFromStartupArguments();
    }

    /// <summary>
    /// Supports deterministic screenshots / smoke tests:
    ///   Loquinota.exe --page=history|live|import
    ///   Loquinota.exe --session=&lt;sessionId&gt;
    /// </summary>
    private void NavigateFromStartupArguments()
    {
        string? page = null;
        string? sessionId = null;

        foreach (var arg in Environment.GetCommandLineArgs())
        {
            if (arg.StartsWith("--page=", StringComparison.OrdinalIgnoreCase))
            {
                page = arg["--page=".Length..].Trim();
            }
            else if (arg.StartsWith("--session=", StringComparison.OrdinalIgnoreCase))
            {
                sessionId = arg["--session=".Length..].Trim();
            }
        }

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            NavView.SelectedItem = null;
            try
            {
                ContentFrame.Navigate(typeof(SessionViewerPage), sessionId);
            }
            catch (Exception ex)
            {
                Log.Error("Could not open the requested session.", ex);
            }

            return;
        }

        var target = (page ?? "history").ToLowerInvariant();
        var item = target switch
        {
            "live" => LiveNavItem,
            "import" => ImportNavItem,
            _ => HistoryNavItem
        };
        var pageType = target switch
        {
            "live" => typeof(LiveCapturePage),
            "import" => typeof(ImportMp4Page),
            _ => typeof(HistoryPage)
        };

        NavView.SelectedItem = item;
        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            Navigate(pageType);
        }
    }

    private void ConfigureWindow()
    {
        try
        {
            var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
            var width = Math.Min(1400, Math.Max(1000, area.WorkArea.Width - 120));
            var height = Math.Min(900, Math.Max(680, area.WorkArea.Height - 120));
            AppWindow.Resize(new SizeInt32(width, height));
            AppWindow.Move(new PointInt32(
                area.WorkArea.X + (area.WorkArea.Width - width) / 2,
                area.WorkArea.Y + (area.WorkArea.Height - height) / 2));

            var titleBar = AppWindow.TitleBar;
            titleBar.ExtendsContentIntoTitleBar = true;
            titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not configure window chrome.", ex);
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is not NavigationViewItem item || item.Tag is not string tag)
        {
            return;
        }

        Type pageType = tag switch
        {
            "live" => typeof(LiveCapturePage),
            "import" => typeof(ImportMp4Page),
            _ => typeof(HistoryPage)
        };

        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            Navigate(pageType);
        }
    }

    private void Navigate(Type pageType)
    {
        try
        {
            ContentFrame.Navigate(pageType);
        }
        catch (Exception ex)
        {
            Log.Error($"Navigation to {pageType.Name} failed.", ex);
        }
    }

    private void ContentFrame_Navigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        // Keep the navigation selection in sync when pages navigate themselves
        // (e.g. History -> Session Viewer).
        var tag = e.SourcePageType == typeof(LiveCapturePage) ? "live"
            : e.SourcePageType == typeof(ImportMp4Page) ? "import"
            : e.SourcePageType == typeof(HistoryPage) ? "history"
            : null;

        if (tag is null)
        {
            NavView.SelectedItem = null;
            return;
        }

        if (NavView.SelectedItem is NavigationViewItem current && (string?)current.Tag == tag)
        {
            return;
        }

        NavView.SelectedItem = tag switch
        {
            "live" => LiveNavItem,
            "import" => ImportNavItem,
            _ => HistoryNavItem
        };
    }
}
