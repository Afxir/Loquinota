namespace Loquinota.Helpers;

/// <summary>
/// Central place for the app's local data locations.
/// Uses the WinRT ApplicationData folder when the app has an identity (packaged),
/// and gracefully falls back to %LOCALAPPDATA%\Loquinota for unpackaged desktop runs.
/// </summary>
public static class AppStorage
{
    private static readonly Lazy<string> LazyRoot = new(ResolveRoot);

    public static string RootDirectory => LazyRoot.Value;

    public static string SessionsDirectory => Path.Combine(RootDirectory, "Sessions");

    public static string ModelsDirectory => Path.Combine(RootDirectory, "Models");

    public static string FfmpegDirectory => Path.Combine(RootDirectory, "ffmpeg");

    public static string LogsDirectory => Path.Combine(RootDirectory, "Logs");

    public static string TempDirectory => Path.Combine(RootDirectory, "Temp");

    public static string DatabasePath => Path.Combine(RootDirectory, "loquinota.db");

    public static string SettingsPath => Path.Combine(RootDirectory, "settings.json");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(SessionsDirectory);
        Directory.CreateDirectory(ModelsDirectory);
        Directory.CreateDirectory(FfmpegDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(TempDirectory);
    }

    private static string ResolveRoot()
    {
        // Packaged apps (and some unpackaged configurations) expose ApplicationData.
        try
        {
            var local = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
            if (!string.IsNullOrWhiteSpace(local))
            {
                return Path.Combine(local, "Loquinota");
            }
        }
        catch
        {
            // Unpackaged apps without package identity throw here: fall through.
        }

        var fallback = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(fallback, "Loquinota");
    }
}
