using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Loquinota.Helpers;

/// <summary>
/// WAV helper used to feed Whisper with 16 kHz mono float samples.
/// Reads PCM 8/16/24/32-bit and IEEE float WAVs (mono, stereo or multi-channel),
/// averages channels down to mono and resamples to the requested rate.
/// </summary>
public static class WavIO
{
    /// <summary>
    /// Reads a WAV file and returns 16 kHz mono float samples.
    /// </summary>
    public static float[] ReadMono16k(string wavPath) => ReadMono(wavPath, 16000);

    /// <summary>Reads any supported audio file and converts it to mono float at the requested sample rate.</summary>
    public static float[] ReadMono(string wavPath, int targetSampleRate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wavPath);
        if (targetSampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetSampleRate), "The target sample rate must be positive.");
        }

        if (!File.Exists(wavPath))
        {
            throw new FileNotFoundException("The audio file was not found.", wavPath);
        }

        using var reader = new AudioFileReader(wavPath);
        ISampleProvider provider = reader;

        if (provider.WaveFormat.Channels > 1)
        {
            provider = new MonoDownmixSampleProvider(provider);
        }

        if (provider.WaveFormat.SampleRate != targetSampleRate)
        {
            provider = new WdlResamplingSampleProvider(provider, targetSampleRate);
        }

        var capacity = EstimateCapacity(reader, targetSampleRate);
        var samples = new List<float>(capacity);
        var buffer = new float[Math.Max(1024, targetSampleRate)];

        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                samples.Add(buffer[i]);
            }
        }

        return samples.ToArray();
    }

    /// <summary>Writes mono float samples as a 16-bit PCM WAV (clamped to [-1, 1]).</summary>
    public static void WritePcm16(string path, IEnumerable<float> samples, int sampleRate = 16000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(samples);
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "The sample rate must be positive.");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var writer = new WaveFileWriter(path, new WaveFormat(sampleRate, 16, 1));
        var chunk = new List<float>(4096);

        foreach (var sample in samples)
        {
            chunk.Add(float.IsNaN(sample) || float.IsInfinity(sample) ? 0f : sample);
            if (chunk.Count == 4096)
            {
                writer.WriteSamples(chunk.ToArray(), 0, chunk.Count);
                chunk.Clear();
            }
        }

        if (chunk.Count > 0)
        {
            writer.WriteSamples(chunk.ToArray(), 0, chunk.Count);
        }
    }

    /// <summary>Returns true when the file starts with a RIFF/WAVE header.</summary>
    public static bool LooksLikeWav(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length < 12)
            {
                return false;
            }

            Span<byte> header = stackalloc byte[12];
            var read = stream.Read(header);
            return read == 12
                   && header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F'
                   && header[8] == (byte)'W' && header[9] == (byte)'A' && header[10] == (byte)'V' && header[11] == (byte)'E';
        }
        catch
        {
            return false;
        }
    }

    private static int EstimateCapacity(AudioFileReader reader, int targetSampleRate)
    {
        try
        {
            var seconds = reader.TotalTime.TotalSeconds;
            if (seconds > 0 && seconds < 24 * 60 * 60)
            {
                var count = (long)(seconds * targetSampleRate);
                if (count > 0 && count < int.MaxValue - 4096)
                {
                    return (int)count;
                }
            }
        }
        catch
        {
            // Fall back to a small initial capacity.
        }

        return 0;
    }

    /// <summary>Averages an interleaved multi-channel stream into a mono stream.</summary>
    private sealed class MonoDownmixSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private float[] _buffer = Array.Empty<float>();

        public MonoDownmixSampleProvider(ISampleProvider source)
        {
            _source = source;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var channels = _source.WaveFormat.Channels;
            var needed = count * channels;
            if (_buffer.Length < needed)
            {
                _buffer = new float[needed];
            }

            var samplesRead = _source.Read(_buffer, 0, needed);
            var frames = samplesRead / channels;
            for (var frame = 0; frame < frames; frame++)
            {
                var sum = 0f;
                var baseIndex = frame * channels;
                for (var channel = 0; channel < channels; channel++)
                {
                    sum += _buffer[baseIndex + channel];
                }

                buffer[offset + frame] = sum / channels;
            }

            return frames;
        }
    }
}
