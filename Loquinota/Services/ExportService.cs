using System.Globalization;
using System.Text;
using Loquinota.Helpers;
using Loquinota.Models;
using Windows.Storage.Pickers;

namespace Loquinota.Services;

/// <summary>
/// Exports a session to a user-chosen .txt file with FileSavePicker.
///
/// Contract:
///  - FileSavePicker must be initialised for WinUI 3 desktop with
///    WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window)).
///  - Export format: title, created date, source, duration, optional model/language, notes,
///    and (when includeTranscript) the raw transcript under a separator.
///  - Returns false when the user cancels; throws only on unexpected I/O failures.
/// </summary>
public sealed class ExportService
{
    public string BuildExportText(Session session, bool includeTranscript = true)
    {
        ArgumentNullException.ThrowIfNull(session);

        var title = string.IsNullOrWhiteSpace(session.Title) ? "Untitled session" : session.Title.Trim();
        var builder = new StringBuilder();

        builder.Append("Title: ").AppendLine(title);
        builder.Append("Created: ")
            .AppendLine(session.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));

        var source = session.SourceLabel;
        if (!string.IsNullOrWhiteSpace(session.SourceFileName))
        {
            source += $" ({session.SourceFileName!.Trim()})";
        }

        builder.Append("Source: ").AppendLine(source);
        builder.Append("Duration: ").AppendLine(session.DurationText);

        if (!string.IsNullOrWhiteSpace(session.ModelName))
        {
            builder.Append("Model: ").AppendLine(session.ModelName!.Trim());
        }

        if (!string.IsNullOrWhiteSpace(session.Language))
        {
            builder.Append("Language: ").AppendLine(session.Language!.Trim());
        }

        builder.AppendLine();
        builder.AppendLine("Notes:");
        builder.AppendLine(string.IsNullOrWhiteSpace(session.NotesText)
            ? "(no notes)"
            : session.NotesText.Trim());

        if (includeTranscript)
        {
            builder.AppendLine();
            builder.AppendLine("--- TRANSCRIPT ---");
            builder.AppendLine(string.IsNullOrWhiteSpace(session.TranscriptText)
                ? "(no transcript)"
                : session.TranscriptText.Trim());
        }

        // One trailing newline, no trailing blank lines.
        return builder.ToString().TrimEnd('\r', '\n') + Environment.NewLine;
    }

    public async Task<bool> ExportSessionAsync(
        Session session,
        Microsoft.UI.Xaml.Window window,
        bool includeTranscript = true)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(window);

        var text = BuildExportText(session, includeTranscript);

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = BuildSuggestedFileName(session),
            DefaultFileExtension = ".txt",
        };
        picker.FileTypeChoices.Add("Text document", new List<string> { ".txt" });

        // Required for WinUI 3 desktop: connect the picker to the app window's HWND.
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker,
            WinRT.Interop.WindowNative.GetWindowHandle(window));

        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return false;
        }

        try
        {
            File.WriteAllText(file.Path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Exporting session '{session.Id}' to '{file.Path}' failed.", ex);
            throw;
        }
    }

    private static string BuildSuggestedFileName(Session session)
    {
        var raw = string.IsNullOrWhiteSpace(session.Title) ? "" : session.Title.Trim();
        var invalid = Path.GetInvalidFileNameChars();

        var cleaned = new StringBuilder(raw.Length);
        foreach (var character in raw)
        {
            cleaned.Append(invalid.Contains(character) || char.IsControl(character) ? ' ' : character);
        }

        var name = string.Join(' ', cleaned.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (name.Length > 64)
        {
            name = name[..64].TrimEnd();
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = $"loquinota-session-{session.CreatedAt.ToLocalTime():yyyyMMdd-HHmm}";
        }

        return name;
    }
}
