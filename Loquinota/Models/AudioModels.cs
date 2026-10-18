namespace Loquinota.Models;

/// <summary>A WASAPI capture or render endpoint.</summary>
public sealed class AudioDeviceInfo
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public bool IsDefault { get; set; }

    public override string ToString() => Name;
}

/// <summary>Options for a live capture run.</summary>
public sealed class AudioCaptureOptions
{
    public bool CaptureMicrophone { get; set; } = true;

    public bool CaptureSystemAudio { get; set; }

    /// <summary>MMDevice id of the microphone; null uses the default communications device.</summary>
    public string? MicrophoneDeviceId { get; set; }

    /// <summary>When set, the mixed 16 kHz mono audio is written to this WAV path.</summary>
    public string? WavOutputPath { get; set; }

    /// <summary>Whisper input rate. Keep at 16000.</summary>
    public int TargetSampleRate { get; set; } = 16000;
}

/// <summary>Result of a completed live capture.</summary>
public sealed class AudioCaptureResult
{
    public string? WavFilePath { get; set; }

    public TimeSpan Duration { get; set; }

    public long SampleCount { get; set; }

    public bool CapturedMicrophone { get; set; }

    public bool CapturedSystemAudio { get; set; }
}

/// <summary>A 16 kHz mono float frame raised by the audio capture service.</summary>
public sealed class AudioSamplesEventArgs : EventArgs
{
    public float[] Samples { get; init; } = Array.Empty<float>();

    public int SampleRate { get; init; } = 16000;
}
