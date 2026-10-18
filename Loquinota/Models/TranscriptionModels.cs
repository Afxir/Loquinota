namespace Loquinota.Models;

/// <summary>A timestamped piece of transcript produced by Whisper.</summary>
public sealed class TranscriptSegment
{
    public TimeSpan Start { get; set; }

    public TimeSpan End { get; set; }

    public string Text { get; set; } = "";

    public string StartText => Start.TotalHours >= 1
        ? Start.ToString(@"h\:mm\:ss")
        : Start.ToString(@"m\:ss");
}

/// <summary>Full batch transcription output.</summary>
public sealed class TranscriptionResult
{
    public string Text { get; set; } = "";

    public IReadOnlyList<TranscriptSegment> Segments { get; set; } = Array.Empty<TranscriptSegment>();

    public string? Language { get; set; }

    public TimeSpan Duration { get; set; }

    public string ModelName { get; set; } = "";
}

/// <summary>Progress information for long-running transcription work.</summary>
public sealed class TranscriptionProgress
{
    public double Fraction { get; set; }

    public string Status { get; set; } = "";

    public TimeSpan? EstimatedRemaining { get; set; }
}

/// <summary>Update raised by a live (streaming) transcription session.</summary>
public sealed class LiveTranscriptUpdate : EventArgs
{
    public string FullTranscript { get; set; } = "";

    public string LatestSegment { get; set; } = "";

    public bool IsPartial { get; set; }

    public TimeSpan Elapsed { get; set; }
}

/// <summary>Tuning for live streaming transcription.</summary>
public sealed class LiveTranscriptionOptions
{
    /// <summary>"auto" lets Whisper detect the language.</summary>
    public string? Language { get; set; } = "auto";

    /// <summary>Audio chunk size fed to Whisper. Must stay >= 2 seconds for accuracy.</summary>
    public int ChunkSeconds { get; set; } = 3;

    /// <summary>Maximum audio kept in memory while a chunk is being processed.</summary>
    public int MaxBufferSeconds { get; set; } = 30;

    /// <summary>Optional initial prompt to improve continuity between chunks.</summary>
    public string? InitialPrompt { get; set; }
}

/// <summary>A transcription model available locally or downloadable.</summary>
public sealed class ModelInfo
{
    public string Name { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string FilePath { get; set; } = "";

    public long SizeBytes { get; set; }

    public bool IsInstalled { get; set; }

    public string SizeText => SizeBytes <= 0
        ? ""
        : SizeBytes >= 1024L * 1024 * 1024
            ? $"{SizeBytes / (1024.0 * 1024 * 1024):0.0} GB"
            : $"{SizeBytes / (1024.0 * 1024):0} MB";
}

/// <summary>Reports model download progress.</summary>
public sealed class ModelDownloadProgress
{
    public double Fraction { get; set; }

    public long BytesReceived { get; set; }

    public long TotalBytes { get; set; }

    public string Status { get; set; } = "";

    public string BytesText => TotalBytes > 0
        ? $"{BytesReceived / (1024.0 * 1024):0.0} / {TotalBytes / (1024.0 * 1024):0.0} MB"
        : $"{BytesReceived / (1024.0 * 1024):0.0} MB";
}
