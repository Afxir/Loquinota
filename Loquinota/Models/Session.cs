namespace Loquinota.Models;

/// <summary>Where a session's audio came from.</summary>
public enum SessionSourceType
{
    Live = 0,
    Mp4 = 1
}

/// <summary>
/// A persisted transcription session. Stored in SQLite by <c>SessionRepository</c>;
/// audio lives next to the database as Sessions/{Id}.wav.
/// </summary>
public sealed class Session
{
    public string Id { get; set; } = "";

    public string Title { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public TimeSpan Duration { get; set; }

    public SessionSourceType SourceType { get; set; }

    public string AudioFilePath { get; set; } = "";

    public string TranscriptText { get; set; } = "";

    public string NotesText { get; set; } = "";

    /// <summary>Original MP4 file name for imports, or null for live captures.</summary>
    public string? SourceFileName { get; set; }

    /// <summary>Whisper model used, e.g. "base".</summary>
    public string? ModelName { get; set; }

    /// <summary>Detected or requested language code.</summary>
    public string? Language { get; set; }

    public string DurationText => Duration.TotalHours >= 1
        ? Duration.ToString(@"h\:mm\:ss")
        : Duration.ToString(@"m\:ss");

    public string CreatedAtText => CreatedAt.LocalDateTime.ToString("g");

    public string SourceLabel => SourceType == SessionSourceType.Mp4 ? "MP4 import" : "Live capture";

    public int WordCount
    {
        get
        {
            var text = string.IsNullOrWhiteSpace(TranscriptText) ? NotesText : TranscriptText;
            return string.IsNullOrWhiteSpace(text)
                ? 0
                : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        }
    }

    public Session Clone() => (Session)MemberwiseClone();
}
