using System.Diagnostics;

namespace Loquinota.Helpers;

/// <summary>
/// Simple thread-safe file logger. Logs go to %LOCALAPPDATA%\Loquinota\Logs\loquinota.log
/// and to the debugger output. Used for diagnostics that survive crashes in Release builds.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _logFile;
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(AppStorage.LogsDirectory);
            _logFile = Path.Combine(AppStorage.LogsDirectory, "loquinota.log");
            _initialized = true;
            Info($"==== loquinota session started {DateTimeOffset.Now:O} ====");
        }
        catch
        {
            // Logging must never throw.
        }
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        if (ex is not null)
        {
            line += Environment.NewLine + ex;
        }

        Debug.WriteLine(line);

        if (!_initialized || _logFile is null)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                File.AppendAllText(_logFile, line + Environment.NewLine);
            }
        }
        catch
        {
            // Ignore logging failures.
        }
    }
}
