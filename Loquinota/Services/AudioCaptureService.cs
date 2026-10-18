using System.Diagnostics;
using Loquinota.Helpers;
using Loquinota.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Loquinota.Services;

/// <summary>
/// Captures microphone (WasapiCapture) and/or system audio (WasapiLoopbackCapture),
/// mixes them, converts to 16 kHz mono float PCM and raises ~20 ms frames.
///
/// Contract:
///  - StartAsync begins capture. At least one source must be enabled.
///  - SamplesAvailable fires on a background thread with 16 kHz mono samples.
///  - If options.WavOutputPath is set, a 16 kHz mono PCM16 WAV is written incrementally.
///  - StopAsync flushes the WAV, stops devices and returns the captured duration.
///  - Device default changes mid-session must be handled gracefully (restart or report).
///  - Never block the UI thread; all work happens on background threads.
/// </summary>
public sealed class AudioCaptureService : IAsyncDisposable
{
    private const int DefaultTargetSampleRate = 16000;

    /// <summary>20 ms at 16 kHz — the frame size raised through <see cref="SamplesAvailable"/>.</summary>
    private const int FrameSamples = 320;

    private const int MaxRestartAttempts = 3;
    private const int PullTickMilliseconds = 4;

    private static readonly TimeSpan SourceBufferDuration = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LevelUpdateInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan DefaultRenderPollInterval = TimeSpan.FromMilliseconds(2000);

    // IEEE float sub-format GUID as used by WAVE_FORMAT_EXTENSIBLE.
    private static readonly Guid IeeeFloatSubFormatGuid = new("00000003-0000-0010-8000-00AA00389B71");

    private readonly object _sync = new();
    private readonly object _stopGate = new();
    private readonly object _restartGate = new();

    private CancellationTokenSource? _cts;
    private Task? _pullTask;
    private Task? _devicePollTask;
    private Stopwatch? _clock;
    private CaptureSource? _mic;
    private CaptureSource? _loopback;
    private WaveFileWriter? _wavWriter;
    private string? _wavPath;
    private string? _requestedMicrophoneId;
    private int _targetSampleRate = DefaultTargetSampleRate;
    private volatile bool _capturingFlag;
    private volatile bool _microphoneStarted;
    private volatile bool _systemAudioStarted;
    private int _starting;
    private int _expectedStops;
    private bool _disposed;
    private AudioCaptureResult? _lastResult;

    // State only touched by the pull thread (and by StopCore after the pull thread is joined).
    private float[] _mixScratch = Array.Empty<float>();
    private float[] _microphoneScratch = Array.Empty<float>();
    private float[] _systemScratch = Array.Empty<float>();
    private readonly float[] _pendingFrame = new float[FrameSamples];
    private int _pendingCount;
    private long _producedSamples;
    private long _writtenSamples;
    private float _level;
    private long _lastLevelTimestamp;
    private byte[] _pcmBytes = new byte[FrameSamples * 2];
    private bool _wavWriteFailed;
    private bool _fellBehindReported;

    public bool IsCapturing { get; private set; }

    /// <summary>Raised with mixed 16 kHz mono float frames (typically 20 ms = 320 samples).</summary>
    public event EventHandler<AudioSamplesEventArgs>? SamplesAvailable;

    /// <summary>Raised with the current mixed level, 0.0 .. 1.0.</summary>
    public event EventHandler<double>? LevelChanged;

    /// <summary>Human-readable capture status, e.g. "Capturing microphone + system audio".</summary>
    public event EventHandler<string>? StatusChanged;

    /// <summary>Raised when a capture source fails but the service tries to continue.</summary>
    public event EventHandler<string>? Error;

    public async Task StartAsync(AudioCaptureOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(AudioCaptureService));
        }

        if (_capturingFlag || IsCapturing)
        {
            throw new InvalidOperationException("Audio capture is already running.");
        }

        if (Interlocked.CompareExchange(ref _starting, 1, 0) != 0)
        {
            throw new InvalidOperationException("Audio capture is already starting.");
        }

        try
        {
            await Task.Run(() => StartCore(options, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _starting, 0);
        }
    }

    public Task<AudioCaptureResult> StopAsync()
    {
        if (!_capturingFlag && _cts == null)
        {
            return Task.FromResult(_lastResult ?? new AudioCaptureResult());
        }

        return Task.Run(() =>
        {
            lock (_stopGate)
            {
                return StopCore();
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (_capturingFlag || _cts != null)
            {
                await StopAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _disposed = true;
        }
    }

    // ------------------------------------------------------------------ start / stop

    private void StartCore(AudioCaptureOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _targetSampleRate = options.TargetSampleRate > 0 ? options.TargetSampleRate : DefaultTargetSampleRate;
        _requestedMicrophoneId = options.MicrophoneDeviceId;
        _microphoneStarted = false;
        _systemAudioStarted = false;
        _lastResult = null;

        // Reset the pull-thread state (nothing else is running at this point).
        _pendingCount = 0;
        _producedSamples = 0;
        _writtenSamples = 0;
        _level = 0f;
        _lastLevelTimestamp = 0;
        _wavWriteFailed = false;
        _fellBehindReported = false;

        IsCapturing = true;
        _capturingFlag = true;

        var cts = new CancellationTokenSource();
        CaptureSource? microphone = null;
        CaptureSource? systemAudio = null;
        WaveFileWriter? writer = null;
        string? wavPath = null;
        string? microphoneError = null;
        string? systemAudioError = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(options.WavOutputPath))
            {
                wavPath = Path.GetFullPath(options.WavOutputPath);
                var directory = Path.GetDirectoryName(wavPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                writer = new WaveFileWriter(wavPath, new WaveFormat(_targetSampleRate, 16, 1));
            }

            if (options.CaptureMicrophone)
            {
                microphone = CreateMicrophoneSource(out microphoneError);
            }

            if (options.CaptureSystemAudio)
            {
                systemAudio = CreateLoopbackSource(out systemAudioError);
            }

            if (microphone == null && systemAudio == null)
            {
                var details = string.Join(
                    " ",
                    new[] { microphoneError, systemAudioError }.Where(e => !string.IsNullOrWhiteSpace(e)));
                throw new InvalidOperationException(
                    "No audio source is available. Enable a microphone or system audio capture. " +
                    (string.IsNullOrWhiteSpace(details)
                        ? "Make sure at least one active capture device and one active playback device exist."
                        : details));
            }

            var clock = Stopwatch.StartNew();
            var assigned = false;
            lock (_sync)
            {
                // If StopAsync ran while the devices were opening it cleared _capturingFlag; do not
                // resurrect a capture the caller already stopped.
                if (_capturingFlag)
                {
                    _cts = cts;
                    _clock = clock;
                    _mic = microphone;
                    _loopback = systemAudio;
                    _wavWriter = writer;
                    _wavPath = wavPath;
                    _pullTask = Task.Run(() => PullLoop(cts.Token));
                    _devicePollTask = systemAudio != null
                        ? Task.Run(() => PollDefaultRenderDevice(cts.Token))
                        : null;
                    assigned = true;
                }
            }

            if (!assigned)
            {
                // Concurrent StopAsync won the race: roll back and leave the service stopped.
                try
                {
                    microphone?.Dispose();
                    systemAudio?.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Warn("Disposing sources from an aborted capture start failed.", ex);
                }

                try
                {
                    writer?.Flush();
                    writer?.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Warn("Disposing the WAV writer from an aborted capture start failed.", ex);
                }

                if (wavPath != null)
                {
                    try
                    {
                        File.Delete(wavPath);
                    }
                    catch
                    {
                        // Best effort only.
                    }
                }

                cts.Dispose();
                microphone = null;
                systemAudio = null;
                return;
            }

            writer = null; // ownership transferred to the service

            if (microphone == null && options.CaptureMicrophone)
            {
                RaiseError("Microphone capture is unavailable: " + (microphoneError ?? "unknown error"));
            }

            if (systemAudio == null && options.CaptureSystemAudio)
            {
                RaiseError("System audio capture is unavailable: " + (systemAudioError ?? "unknown error"));
            }

            RaiseStatus(BuildStatusText(microphone != null, systemAudio != null));

            Log.Info(
                $"Audio capture started: microphone={microphone?.Name ?? "off"}, " +
                $"systemAudio={systemAudio?.Name ?? "off"}, targetRate={_targetSampleRate}, wav={wavPath ?? "off"}.");
        }
        catch
        {
            IsCapturing = false;
            _capturingFlag = false;

            try
            {
                microphone?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn("Disposing the microphone source failed during start rollback.", ex);
            }

            try
            {
                systemAudio?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn("Disposing the system audio source failed during start rollback.", ex);
            }

            try
            {
                writer?.Flush();
                writer?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn("Disposing the WAV writer failed during start rollback.", ex);
            }

            if (wavPath != null)
            {
                try
                {
                    File.Delete(wavPath);
                }
                catch
                {
                    // Best effort only.
                }
            }

            cts.Dispose();
            throw;
        }
    }

    private AudioCaptureResult StopCore()
    {
        CancellationTokenSource? cts;
        CaptureSource? microphone;
        CaptureSource? systemAudio;
        Task? pullTask;
        Task? pollTask;
        Stopwatch? clock;
        WaveFileWriter? writer;
        string? wavPath;
        int targetRate;

        lock (_sync)
        {
            if (!_capturingFlag && _cts == null)
            {
                return _lastResult ?? new AudioCaptureResult();
            }

            _capturingFlag = false;
            IsCapturing = false;
            cts = _cts;
            clock = _clock;
            wavPath = _wavPath;
            targetRate = _targetSampleRate;
        }

        clock?.Stop();

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed; nothing to cancel.
        }

        // Let any in-flight automatic restart reach a decision before we take ownership of the sources.
        lock (_restartGate)
        {
        }

        // Join the pull loop BEFORE detaching the writer/sources so no frame can be counted after the
        // WAV writer is released (that would make SampleCount and the WAV sample count disagree).
        lock (_sync)
        {
            pullTask = _pullTask;
        }

        if (pullTask != null)
        {
            try
            {
                pullTask.Wait(TimeSpan.FromSeconds(3));
            }
            catch (AggregateException ex)
            {
                Log.Warn("The audio pull loop ended with an error.", ex.Flatten());
            }
        }

        lock (_sync)
        {
            microphone = _mic;
            systemAudio = _loopback;
            writer = _wavWriter;
            pollTask = _devicePollTask;
            _mic = null;
            _loopback = null;
            _pullTask = null;
            _devicePollTask = null;
            _cts = null;
            _clock = null;
            _wavWriter = null;
            _wavPath = null;
        }

        // Drain the audio that is due between the last pull tick and the stop instant.
        try
        {
            FinalDrain(microphone, systemAudio, clock, writer);
        }
        catch (Exception ex)
        {
            Log.Error("Draining the final audio samples failed.", ex);
            RaiseError("Draining the final audio samples failed: " + ex.Message);
        }

        StopSourceQuietly(microphone);
        StopSourceQuietly(systemAudio);

        if (writer != null)
        {
            try
            {
                writer.Flush();
                writer.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error("Finalising the capture WAV file failed.", ex);
                RaiseError("Finalising the WAV file failed: " + ex.Message);
                _wavWriteFailed = true;
            }
        }

        try
        {
            pollTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException ex)
        {
            Log.Warn("The default render device poll ended with an error.", ex.Flatten());
        }

        try
        {
            microphone?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Disposing the microphone failed.", ex);
        }

        try
        {
            systemAudio?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Disposing the system audio source failed.", ex);
        }

        try
        {
            cts?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Disposing the capture cancellation source failed.", ex);
        }

        var samples = _writtenSamples;
        var result = new AudioCaptureResult
        {
            WavFilePath = _wavWriteFailed ? null : wavPath,
            SampleCount = samples,
            Duration = TimeSpan.FromSeconds((double)samples / targetRate),
            CapturedMicrophone = _microphoneStarted,
            CapturedSystemAudio = _systemAudioStarted,
        };

        _lastResult = result;
        _pendingCount = 0;
        _producedSamples = 0;

        RaiseStatus(
            $"Capture stopped. {samples:N0} samples ({result.Duration.TotalSeconds:F2} s at {targetRate} Hz mono).");

        Log.Info(
            $"Audio capture stopped: samples={samples}, duration={result.Duration}, " +
            $"mic={result.CapturedMicrophone}, system={result.CapturedSystemAudio}, wav={result.WavFilePath ?? "off"}.");

        return result;
    }

    // ------------------------------------------------------------------ pull / mix

    private void PullLoop(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var clock = _clock;
                if (clock == null)
                {
                    return;
                }

                var elapsedSamples = (long)(clock.Elapsed.TotalSeconds * _targetSampleRate);
                var due = elapsedSamples - _producedSamples;
                if (due > 0)
                {
                    if (due > _targetSampleRate)
                    {
                        // The machine was suspended or the loop stalled for over a second: drop the backlog
                        // instead of emitting a huge burst of stale audio.
                        if (!_fellBehindReported)
                        {
                            _fellBehindReported = true;
                            RaiseError("Audio capture fell behind and dropped buffered audio to stay in real time.");
                        }

                        _producedSamples = elapsedSamples - FrameSamples;
                        due = FrameSamples;
                    }

                    CaptureSource? microphone;
                    CaptureSource? systemAudio;
                    lock (_sync)
                    {
                        microphone = _mic;
                        systemAudio = _loopback;
                    }

                    if (microphone == null && systemAudio == null)
                    {
                        return;
                    }

                    EnsureScratch(ref _mixScratch, (int)due);
                    MixInto(_mixScratch, (int)due, microphone, systemAudio);
                    _producedSamples += due;
                    AppendToPending(_mixScratch, (int)due);

                    // Do not sleep here: the next iteration consumes whatever is left, then idles below.
                    continue;
                }

                if (token.WaitHandle.WaitOne(PullTickMilliseconds))
                {
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("The audio pull loop failed.", ex);
            RaiseError("Audio capture stopped unexpectedly: " + ex.Message);
        }
    }

    private void MixInto(float[] destination, int count, CaptureSource? microphone, CaptureSource? systemAudio)
    {
        Array.Clear(destination, 0, count);

        // Keep headroom when both sources are live so the mix does not clip constantly.
        var bothSources = microphone != null && systemAudio != null;
        var gain = bothSources ? 0.7f : 1.0f;

        if (microphone != null)
        {
            EnsureScratch(ref _microphoneScratch, count);
            var read = ReadSource(microphone, _microphoneScratch, count);
            for (var i = 0; i < read; i++)
            {
                destination[i] += _microphoneScratch[i] * gain;
            }
        }

        if (systemAudio != null)
        {
            EnsureScratch(ref _systemScratch, count);
            var read = ReadSource(systemAudio, _systemScratch, count);
            for (var i = 0; i < read; i++)
            {
                destination[i] += _systemScratch[i] * gain;
            }
        }

        for (var i = 0; i < count; i++)
        {
            destination[i] = Math.Clamp(destination[i], -1f, 1f);
        }
    }

    private int ReadSource(CaptureSource source, float[] buffer, int count)
    {
        try
        {
            return source.Output.Read(buffer, 0, count);
        }
        catch (Exception ex)
        {
            if (Interlocked.Increment(ref source.ReadErrors) == 1)
            {
                RaiseError($"Reading from {source.Name} failed: {ex.Message}");
            }

            return 0;
        }
    }

    private void AppendToPending(float[] data, int count)
    {
        var offset = 0;
        while (offset < count)
        {
            var take = Math.Min(FrameSamples - _pendingCount, count - offset);
            Array.Copy(data, offset, _pendingFrame, _pendingCount, take);
            _pendingCount += take;
            offset += take;

            if (_pendingCount == FrameSamples)
            {
                EmitFrame(_pendingFrame, FrameSamples);
                _pendingCount = 0;
            }
        }
    }

    private void EmitFrame(float[] frame, int count)
    {
        double sumSquares = 0;
        for (var i = 0; i < count; i++)
        {
            var sample = frame[i];
            sumSquares += (double)sample * sample;
        }

        var rms = Math.Sqrt(sumSquares / count);
        _level = (float)((_level * 0.55) + (rms * 0.45));

        var now = Stopwatch.GetTimestamp();
        if (_lastLevelTimestamp == 0 ||
            Stopwatch.GetElapsedTime(_lastLevelTimestamp, now) >= LevelUpdateInterval)
        {
            _lastLevelTimestamp = now;
            RaiseLevel(Math.Clamp(_level, 0.0, 1.0));
        }

        var samples = new float[count];
        Array.Copy(frame, samples, count);
        RaiseSamples(samples, _targetSampleRate);

        WriteWavFrame(frame, count, _wavWriter);
        _writtenSamples += count;
    }

    private void WriteWavFrame(float[] frame, int count, WaveFileWriter? writer)
    {
        if (writer == null || _wavWriteFailed)
        {
            return;
        }

        var byteCount = count * 2;
        if (_pcmBytes.Length < byteCount)
        {
            _pcmBytes = new byte[byteCount];
        }

        for (var i = 0; i < count; i++)
        {
            var clamp = Math.Clamp(frame[i], -1f, 1f);
            var value = (short)Math.Round(clamp * 32767.0, MidpointRounding.AwayFromZero);
            _pcmBytes[i * 2] = (byte)(value & 0xFF);
            _pcmBytes[(i * 2) + 1] = (byte)((value >> 8) & 0xFF);
        }

        try
        {
            writer.Write(_pcmBytes, 0, byteCount);
        }
        catch (Exception ex)
        {
            _wavWriteFailed = true;
            Log.Error("Writing to the capture WAV file failed.", ex);
            RaiseError("Writing to the WAV file failed; audio capture continues without a file. " + ex.Message);

            try
            {
                writer.Dispose();
            }
            catch
            {
                // Ignored: the writer is already broken.
            }

            lock (_sync)
            {
                if (ReferenceEquals(_wavWriter, writer))
                {
                    _wavWriter = null;
                }
            }
        }
    }

    private void FinalDrain(
        CaptureSource? microphone,
        CaptureSource? systemAudio,
        Stopwatch? clock,
        WaveFileWriter? writer)
    {
        if (clock == null)
        {
            return;
        }

        var elapsedSamples = (long)(clock.Elapsed.TotalSeconds * _targetSampleRate);
        var due = elapsedSamples - _producedSamples;
        if (due > _targetSampleRate)
        {
            due = _targetSampleRate;
        }

        if (due > 0)
        {
            EnsureScratch(ref _mixScratch, (int)due);
            MixInto(_mixScratch, (int)due, microphone, systemAudio);
            _producedSamples += due;

            var offset = 0;
            while (offset < due)
            {
                var take = Math.Min(FrameSamples - _pendingCount, (int)due - offset);
                Array.Copy(_mixScratch, offset, _pendingFrame, _pendingCount, take);
                _pendingCount += take;
                offset += take;

                if (_pendingCount == FrameSamples)
                {
                    WriteWavFrame(_pendingFrame, FrameSamples, writer);
                    _writtenSamples += FrameSamples;
                    _pendingCount = 0;
                }
            }
        }

        if (_pendingCount > 0)
        {
            WriteWavFrame(_pendingFrame, _pendingCount, writer);
            _writtenSamples += _pendingCount;
            _pendingCount = 0;
        }
    }

    // ------------------------------------------------------------------ sources

    private CaptureSource? CreateMicrophoneSource(out string? error)
    {
        error = null;
        MMDevice? device = null;
        WasapiCapture? capture = null;

        try
        {
            using var enumerator = new MMDeviceEnumerator();

            if (!string.IsNullOrWhiteSpace(_requestedMicrophoneId))
            {
                try
                {
                    device = enumerator.GetDevice(_requestedMicrophoneId);
                }
                catch (Exception ex)
                {
                    Log.Warn(
                        $"Microphone '{_requestedMicrophoneId}' could not be opened; falling back to the default device.",
                        ex);
                    device = null;
                }
            }

            if (device == null)
            {
                if (enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Communications))
                {
                    device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                }
                else if (enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia))
                {
                    device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                }
            }

            if (device == null)
            {
                error = "No active microphone endpoint was found.";
                return null;
            }

            if (device.State != DeviceState.Active)
            {
                error = "The selected microphone endpoint is not active.";
                device.Dispose();
                return null;
            }

            var name = SafeFriendlyName(device, "Microphone");
            capture = new WasapiCapture(device);
            var source = BuildSource(capture, device, isLoopback: false, name);
            device = null;
            capture = null; // ownership transferred to the source
            StartSource(source);
            _microphoneStarted = true;
            return source;
        }
        catch (Exception ex)
        {
            Log.Error("Starting microphone capture failed.", ex);
            error = "Microphone capture could not start: " + ex.Message;

            try
            {
                capture?.Dispose();
            }
            catch (Exception disposeEx)
            {
                Log.Warn("Disposing the failed microphone capture failed.", disposeEx);
            }

            try
            {
                device?.Dispose();
            }
            catch (Exception disposeEx)
            {
                Log.Warn("Disposing the failed microphone endpoint failed.", disposeEx);
            }

            return null;
        }
    }

    private CaptureSource? CreateLoopbackSource(out string? error)
    {
        error = null;
        MMDevice? device = null;
        WasapiLoopbackCapture? capture = null;

        try
        {
            using var enumerator = new MMDeviceEnumerator();

            // WasapiLoopbackCapture captures the default render endpoint (multimedia role, matching NAudio).
            if (enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
            {
                device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            else if (enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Communications))
            {
                device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications);
            }

            if (device == null)
            {
                error = "No active playback endpoint was found for system audio capture.";
                return null;
            }

            var name = SafeFriendlyName(device, "System audio");
            capture = new WasapiLoopbackCapture(device);
            var source = BuildSource(capture, device, isLoopback: true, name);
            device = null;
            capture = null; // ownership transferred to the source
            StartSource(source);
            _systemAudioStarted = true;
            return source;
        }
        catch (Exception ex)
        {
            Log.Error("Starting system audio loopback capture failed.", ex);
            error = "System audio capture could not start: " + ex.Message;

            try
            {
                capture?.Dispose();
            }
            catch (Exception disposeEx)
            {
                Log.Warn("Disposing the failed loopback capture failed.", disposeEx);
            }

            try
            {
                device?.Dispose();
            }
            catch (Exception disposeEx)
            {
                Log.Warn("Disposing the failed playback endpoint failed.", disposeEx);
            }

            return null;
        }
    }

    private CaptureSource BuildSource(IWaveIn capture, MMDevice? device, bool isLoopback, string name)
    {
        var deviceFormat = capture.WaveFormat
                           ?? throw new InvalidOperationException("The capture device did not report a wave format.");
        var standardFormat = MakeStandardWaveFormat(deviceFormat);

        var buffer = new BufferedWaveProvider(standardFormat, SourceBufferDuration)
        {
            ReadFully = false,
            DiscardOnBufferOverflow = true,
        };

        ISampleProvider provider = CreateSampleProvider(buffer, standardFormat);

        if (standardFormat.Channels > 1)
        {
            provider = new AverageChannelsSampleProvider(provider);
        }

        if (standardFormat.SampleRate != _targetSampleRate)
        {
            provider = new WdlResamplingSampleProvider(provider, _targetSampleRate);
        }

        return new CaptureSource(
            capture,
            device,
            buffer,
            provider,
            name,
            device?.ID,
            isLoopback,
            standardFormat);
    }

    private void StartSource(CaptureSource source)
    {
        source.Capture.DataAvailable += (_, e) => OnDataAvailable(source, e);
        source.Capture.RecordingStopped += (_, e) => OnRecordingStopped(source, e);

        source.Capture.StartRecording();
        source.Started = true;

        if (source.IsLoopback)
        {
            _systemAudioStarted = true;
        }
        else
        {
            _microphoneStarted = true;
        }
    }

    private void OnDataAvailable(CaptureSource source, WaveInEventArgs e)
    {
        try
        {
            if (e.BytesRecorded <= 0)
            {
                return;
            }

            // AddSamples copies into the ring buffer; it never blocks on the audio thread.
            source.Buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            Interlocked.Add(ref source.BytesReceived, e.BytesRecorded);
        }
        catch (Exception ex)
        {
            if (Interlocked.Increment(ref source.ReadErrors) == 1)
            {
                Log.Error($"Buffering audio from {source.Name} failed.", ex);
                RaiseError($"Buffering audio from {source.Name} failed: {ex.Message}");
            }
        }
    }

    private void OnRecordingStopped(CaptureSource source, StoppedEventArgs e)
    {
        try
        {
            if (Volatile.Read(ref _expectedStops) > 0)
            {
                return; // Expected, intentional stop.
            }

            if (!_capturingFlag)
            {
                return;
            }

            lock (_sync)
            {
                if (!ReferenceEquals(_mic, source) && !ReferenceEquals(_loopback, source))
                {
                    return; // Source was already replaced.
                }
            }

            var reason = e.Exception != null ? e.Exception.Message : "the device stopped unexpectedly";
            RaiseError($"{source.Name} capture stopped: {reason}.");
            RaiseStatus($"{source.Name} capture interrupted; attempting to restart...");
            ScheduleRestart(source);
        }
        catch (Exception ex)
        {
            Log.Error("Handling a capture stop event failed.", ex);
        }
    }

    private void ScheduleRestart(CaptureSource source)
    {
        if (!_capturingFlag)
        {
            return;
        }

        if (Interlocked.Increment(ref source.RestartsScheduled) > MaxRestartAttempts)
        {
            GiveUpOnSource(source);
            return;
        }

        ThreadPool.QueueUserWorkItem(_ => RestartSource(source));
    }

    private void RestartSource(CaptureSource failed)
    {
        try
        {
            var cts = _cts;
            var attempt = failed.RestartsScheduled;
            var delayMilliseconds = (int)Math.Min(400 * Math.Pow(2, Math.Max(0, attempt - 1)), 5000);
            if (cts != null && cts.Token.WaitHandle.WaitOne(delayMilliseconds))
            {
                return;
            }

            lock (_restartGate)
            {
                if (!_capturingFlag)
                {
                    return;
                }

                lock (_sync)
                {
                    if (!ReferenceEquals(failed.IsLoopback ? _loopback : _mic, failed))
                    {
                        return;
                    }
                }

                CaptureSource? replacement = null;
                string? error = null;
                try
                {
                    replacement = failed.IsLoopback
                        ? CreateLoopbackSource(out error)
                        : CreateMicrophoneSource(out error);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    Log.Error("Recreating an audio capture source failed.", ex);
                }

                if (replacement == null)
                {
                    RaiseError($"{failed.Name} could not be restarted: {error ?? "unknown error"}.");
                    if (_capturingFlag)
                    {
                        ScheduleRestart(failed);
                    }

                    return;
                }

                var assigned = false;
                lock (_sync)
                {
                    if (_capturingFlag)
                    {
                        if (failed.IsLoopback)
                        {
                            _loopback = replacement;
                        }
                        else
                        {
                            _mic = replacement;
                        }

                        assigned = true;
                    }
                }

                if (!assigned)
                {
                    try
                    {
                        replacement.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("Disposing a redundant capture source failed.", ex);
                    }

                    return;
                }

                StopSourceQuietly(failed);
                try
                {
                    failed.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Warn("Disposing a failed capture source failed.", ex);
                }

                RaiseStatus($"{replacement.Name} capture restarted.");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Restarting audio capture failed.", ex);
            RaiseError("Restarting audio capture failed: " + ex.Message);
        }
    }

    private void GiveUpOnSource(CaptureSource source)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_mic, source))
            {
                _mic = null;
            }

            if (ReferenceEquals(_loopback, source))
            {
                _loopback = null;
            }
        }

        StopSourceQuietly(source);

        try
        {
            source.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Disposing a failed capture source failed.", ex);
        }

        RaiseError($"{source.Name} capture could not be restarted and was disabled.");

        bool anySourceLeft;
        lock (_sync)
        {
            anySourceLeft = _mic != null || _loopback != null;
        }

        if (!anySourceLeft)
        {
            RaiseStatus("All audio sources stopped; capture ended.");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    StopAsync().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Log.Warn("Stopping after all audio sources failed.", ex);
                }
            });
        }
    }

    private void StopSourceQuietly(CaptureSource? source)
    {
        if (source == null)
        {
            return;
        }

        Interlocked.Increment(ref _expectedStops);
        try
        {
            try
            {
                if (source.Started)
                {
                    source.Capture.StopRecording();
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Stopping {source.Name} failed.", ex);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _expectedStops);
        }
    }

    // ------------------------------------------------------------------ loopback default device tracking

    private void PollDefaultRenderDevice(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (token.WaitHandle.WaitOne(DefaultRenderPollInterval))
            {
                return;
            }

            if (!_capturingFlag)
            {
                return;
            }

            try
            {
                string? defaultId;
                using (var enumerator = new MMDeviceEnumerator())
                {
                    if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) &&
                        !enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Communications))
                    {
                        continue;
                    }

                    var role = enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                        ? Role.Multimedia
                        : Role.Communications;
                    using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, role);
                    defaultId = device.ID;
                }

                CaptureSource? current;
                lock (_sync)
                {
                    current = _loopback;
                }

                if (current == null || string.Equals(current.DeviceId, defaultId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                RestartLoopbackForDeviceChange(current);
            }
            catch (Exception ex)
            {
                Log.Warn("Polling the default playback endpoint failed.", ex);
            }
        }
    }

    private void RestartLoopbackForDeviceChange(CaptureSource current)
    {
        lock (_restartGate)
        {
            if (!_capturingFlag)
            {
                return;
            }

            lock (_sync)
            {
                if (!ReferenceEquals(_loopback, current))
                {
                    return; // Already replaced by another path.
                }
            }

            CaptureSource? replacement = null;
            string? error = null;
            try
            {
                replacement = CreateLoopbackSource(out error);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Log.Error("Restarting loopback capture after a device change failed.", ex);
            }

            if (replacement == null)
            {
                RaiseError("The default playback device changed but system audio capture could not be restarted: " +
                           (error ?? "unknown error"));
                return;
            }

            var assigned = false;
            lock (_sync)
            {
                if (_capturingFlag)
                {
                    _loopback = replacement;
                    assigned = true;
                }
            }

            if (!assigned)
            {
                try
                {
                    replacement.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Warn("Disposing a redundant loopback source failed.", ex);
                }

                return;
            }

            StopSourceQuietly(current);
            try
            {
                current.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn("Disposing the previous loopback source failed.", ex);
            }

            RaiseStatus($"Default playback device changed; system audio capture restarted ({replacement.Name}).");
        }
    }

    // ------------------------------------------------------------------ helpers

    private static ISampleProvider CreateSampleProvider(IWaveProvider source, WaveFormat format)
    {
        switch (format.Encoding)
        {
            case WaveFormatEncoding.IeeeFloat:
                return format.BitsPerSample switch
                {
                    64 => new WaveToSampleProvider64(source),
                    32 => new WaveToSampleProvider(source),
                    _ => throw new NotSupportedException(
                        $"Unsupported IEEE float capture format: {format.BitsPerSample} bits."),
                };
            case WaveFormatEncoding.Pcm:
                return format.BitsPerSample switch
                {
                    8 => new Pcm8BitToSampleProvider(source),
                    16 => new Pcm16BitToSampleProvider(source),
                    24 => new Pcm24BitToSampleProvider(source),
                    32 => new Pcm32BitToSampleProvider(source),
                    _ => throw new NotSupportedException(
                        $"Unsupported PCM capture format: {format.BitsPerSample} bits."),
                };
            default:
                throw new NotSupportedException($"Unsupported capture encoding: {format.Encoding} ({format}).");
        }
    }

    /// <summary>
    /// WASAPI mix formats are usually WAVE_FORMAT_EXTENSIBLE. The NAudio sample-provider helpers inspect
    /// <see cref="WaveFormat.Encoding"/>, which is <see cref="WaveFormatEncoding.Extensible"/> for those, so
    /// map to the equivalent plain format first (the raw sample layout is unchanged).
    /// </summary>
    private static WaveFormat MakeStandardWaveFormat(WaveFormat format)
    {
        try
        {
            if (format.Encoding != WaveFormatEncoding.Extensible)
            {
                return format;
            }

            if (format is WaveFormatExtensible extensible &&
                extensible.SubFormat == IeeeFloatSubFormatGuid &&
                format.BitsPerSample == 32)
            {
                return WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels);
            }

            if (format.BitsPerSample is 8 or 16 or 24 or 32)
            {
                return new WaveFormat(format.SampleRate, format.BitsPerSample, format.Channels);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Normalising the capture wave format failed; using the device format as-is.", ex);
        }

        return format;
    }

    private static string SafeFriendlyName(MMDevice device, string fallback)
    {
        try
        {
            var name = device.FriendlyName;
            return string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();
        }
        catch (Exception ex)
        {
            Log.Warn("Reading an audio endpoint name failed.", ex);
            return fallback;
        }
    }

    private static void EnsureScratch(ref float[] buffer, int size)
    {
        if (buffer.Length < size)
        {
            buffer = new float[size];
        }
    }

    private string BuildStatusText(bool hasMicrophone, bool hasSystemAudio) => (hasMicrophone, hasSystemAudio) switch
    {
        (true, true) => "Capturing microphone + system audio (16 kHz mono).",
        (true, false) => "Capturing microphone (16 kHz mono).",
        (false, true) => "Capturing system audio (16 kHz mono).",
        _ => "Capture stopped.",
    };

    private void RaiseSamples(float[] samples, int sampleRate)
    {
        var handler = SamplesAvailable;
        if (handler == null)
        {
            return;
        }

        try
        {
            handler(this, new AudioSamplesEventArgs { Samples = samples, SampleRate = sampleRate });
        }
        catch (Exception ex)
        {
            Log.Warn("A SamplesAvailable subscriber threw.", ex);
        }
    }

    private void RaiseLevel(double level)
    {
        var handler = LevelChanged;
        if (handler == null)
        {
            return;
        }

        try
        {
            handler(this, level);
        }
        catch (Exception ex)
        {
            Log.Warn("A LevelChanged subscriber threw.", ex);
        }
    }

    private void RaiseStatus(string message)
    {
        var handler = StatusChanged;
        if (handler == null)
        {
            return;
        }

        try
        {
            handler(this, message);
        }
        catch (Exception ex)
        {
            Log.Warn("A StatusChanged subscriber threw.", ex);
        }
    }

    private void RaiseError(string message)
    {
        Log.Warn("Audio capture error: " + message);

        var handler = Error;
        if (handler == null)
        {
            return;
        }

        try
        {
            handler(this, message);
        }
        catch (Exception ex)
        {
            Log.Warn("An Error subscriber threw.", ex);
        }
    }

    // ------------------------------------------------------------------ nested types

    /// <summary>An <see cref="ISampleProvider"/> that mixes any channel count down to mono.</summary>
    private sealed class AverageChannelsSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _channels;
        private float[] _interleaved = Array.Empty<float>();

        public AverageChannelsSampleProvider(ISampleProvider source)
        {
            _source = source;
            _channels = Math.Max(1, source.WaveFormat.Channels);
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            if (_channels == 1)
            {
                return _source.Read(buffer, offset, count);
            }

            var needed = count * _channels;
            if (_interleaved.Length < needed)
            {
                _interleaved = new float[needed];
            }

            var read = _source.Read(_interleaved, 0, needed);
            var frames = read / _channels;
            for (var frame = 0; frame < frames; frame++)
            {
                var baseIndex = frame * _channels;
                var sum = 0f;
                for (var channel = 0; channel < _channels; channel++)
                {
                    sum += _interleaved[baseIndex + channel];
                }

                buffer[offset + frame] = sum / _channels;
            }

            return frames;
        }
    }

    /// <summary>One capture device plus its buffering and 16 kHz mono conversion chain.</summary>
    private sealed class CaptureSource : IDisposable
    {
        private int _disposed;

        public CaptureSource(
            IWaveIn capture,
            MMDevice? device,
            BufferedWaveProvider buffer,
            ISampleProvider output,
            string name,
            string? deviceId,
            bool isLoopback,
            WaveFormat format)
        {
            Capture = capture;
            Device = device;
            Buffer = buffer;
            Output = output;
            Name = name;
            DeviceId = deviceId;
            IsLoopback = isLoopback;
            Format = format;
        }

        public IWaveIn Capture { get; }

        public MMDevice? Device { get; }

        public BufferedWaveProvider Buffer { get; }

        public ISampleProvider Output { get; }

        public string Name { get; }

        public string? DeviceId { get; }

        public bool IsLoopback { get; }

        public WaveFormat Format { get; }

        public bool Started { get; set; }

        public long BytesReceived;

        public int RestartsScheduled;

        public int ReadErrors;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                Capture.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn($"Disposing the {Name} capture failed.", ex);
            }

            try
            {
                Device?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn($"Disposing the {Name} endpoint failed.", ex);
            }
        }
    }
}
