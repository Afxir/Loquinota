namespace Loquinota.Models;

/// <summary>Progress for MP4 extraction / long batch operations.</summary>
public sealed class ProcessingProgress
{
    public double Fraction { get; set; }

    public string Status { get; set; } = "";

    public TimeSpan? EstimatedRemaining { get; set; }
}

/// <summary>Output of the FFmpeg audio-extraction step.</summary>
public sealed class Mp4ExtractionResult
{
    public string WavFilePath { get; set; } = "";

    public TimeSpan Duration { get; set; }

    public string SourceFileName { get; set; } = "";
}

/// <summary>User preferences persisted as JSON under the app data folder.</summary>
public sealed class AppSettings
{
    /// <summary>Whisper model short name: tiny, base, small, medium.</summary>
    public string ModelName { get; set; } = "base";

    /// <summary>"auto" or a language code such as "en", "pl".</summary>
    public string Language { get; set; } = "auto";

    public string? MicrophoneDeviceId { get; set; }

    public bool PreferSystemAudio { get; set; } = true;

    public bool CaptureMicrophoneByDefault { get; set; } = true;

    public bool ShowPartialTranscripts { get; set; } = true;
}
