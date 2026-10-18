using Loquinota.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Loquinota.Helpers;

namespace Loquinota;

/// <summary>
/// Application entry point. Owns the main window and global error handling.
/// </summary>
public partial class App : Application
{
    public static new App Current => (App)Application.Current;

    public MainWindow? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log.Initialize();
        AppStorage.EnsureCreated();
        Log.Info("loquinota starting");

        if (SelfTestRunner.IsRequested)
        {
            RunSelfTest();
            return;
        }

        MainWindow = new MainWindow();
        MainWindow.Activate();

        // Services initialize asynchronously so the window paints immediately.
        _ = InitializeServicesAsync();
    }

    private async void RunSelfTest()
    {
        var exitCode = 2;
        try
        {
            exitCode = await SelfTestRunner.RunAsync();
        }
        catch (Exception ex)
        {
            Log.Error("SELFTEST crashed.", ex);
        }

        Environment.Exit(exitCode);
    }

    private static async Task InitializeServicesAsync()
    {
        try
        {
            await AppServices.EnsureInitializedAsync();
            Log.Info("AppServices initialized.");
        }
        catch (Exception ex)
        {
            Log.Error("Service initialization failed.", ex);
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        e.Handled = true;
        TryShowError(e.Message);
    }

    private async void TryShowError(string message)
    {
        try
        {
            if (MainWindow?.Content is FrameworkElement root)
            {
                var dialog = new ContentDialog
                {
                    Title = "Something went wrong",
                    Content = message,
                    CloseButtonText = "Dismiss",
                    XamlRoot = root.XamlRoot
                };
                await dialog.ShowAsync();
            }
        }
        catch
        {
            // Never let the error dialog crash the error handler.
        }
    }
}
