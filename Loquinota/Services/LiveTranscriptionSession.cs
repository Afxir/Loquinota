using System.Diagnostics;
using System.Threading.Channels;
using Loquinota.Helpers;
using Loquinota.Models;
using Whisper.net;

namespace Loquinota.Services;

/// <summary>
/// Streaming transcription session for live capture.
///
/// Contract:
///  - <see cref="Push"/> is called from the audio thread with 16 kHz mono frames; it must be
///    non-blocking (queue the samples, do not transcribe synchronously).
///  - The session accumulates audio into chunks of <see cref="LiveTranscriptionOptions.ChunkSeconds"/>
///    (default 3 s, minimum 2 s) and processes them one at a time on a background task.
///  - After each chunk, <see cref="TranscriptUpdated"/> fires with the growing full transcript
///    and the latest segment. UI subscribers marshal to their own dispatcher.
///  - <see cref="CompleteAsync"/> flushes remaining audio, waits for the last chunk and returns
///    the final transcript.
///  - Serializes Whisper calls: only one transcription at a time.
/// </summary>
public sealed class LiveTranscriptionSession : IAsyncDisposable
{
    internal const int SampleRate = WhisperTranscriptionService.SampleRate;

    /// <summary>Whisper needs at least two seconds of audio to be reliable.</summary>
    private const int MinimumChunkSeconds = 2;

    private const int MaximumChunkSeconds = 30;

    /// <summary>Trailing audio shorter than this is not worth a Whisper pass.</summary>
    private const double MinimumTailSeconds = 0.5;

    private readonly Channel<float[]> _frames = Channel.CreateUnbounded<float[]>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
    });

    private readonly object _sync = new();
    private readonly List<string> _chunkTexts = new();
    private readonly CancellationTokenSource _lifetimeCts = new();

    private CancellationTokenSource? _linkedCts;
    private Task? _pump;
    private WhisperProcessor? _processor;
    private volatile bool _started;
    private volatile bool _completing;
    private volatile bool _disposed;
    private long _samplesPushed;
    private long _samplesTranscribed;
    private int _chunkCount;

    internal LiveTranscriptionSession(WhisperTranscriptionService owner, LiveTranscriptionOptions options)
    {
        Owner = owner;
        Options = options ?? new LiveTranscriptionOptions();

        var requestedChunkSeconds = Options.ChunkSeconds > 0 ? Options.ChunkSeconds : 3;
        ChunkSeconds = Math.Clamp(requestedChunkSeconds, MinimumChunkSeconds, MaximumChunkSeconds);
        MaxBufferSeconds = Math.Max(Options.MaxBufferSeconds > 0 ? Options.MaxBufferSeconds : 30, ChunkSeconds * 2);
    }

    internal WhisperTranscriptionService Owner { get; }

    public LiveTranscriptionOptions Options { get; }

    public bool IsActive { get; private set; }

    public string Transcript { get; private set; } = "";

    /// <summary>Effective (clamped) chunk length used for audio accumulation.</summary>
    private int ChunkSeconds { get; }

    /// <summary>Cap on audio held in memory while chunks are processed.</summary>
    private int MaxBufferSeconds { get; }

    public event EventHandler<LiveTranscriptUpdate>? TranscriptUpdated;

    public event EventHandler<string>? StatusChanged;

    /// <summary>Starts the background processing pump.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_started)
        {
            return Task.CompletedTask;
        }

        // Fails fast with a friendly message when no model is loaded.
        if (!Owner.IsReady)
        {
            throw new InvalidOperationException("Load a Whisper model first.");
        }

        _started = true;
        IsActive = true;

        _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCts.Token);
        var token = _linkedCts.Token;
        token.Register(static state => ((ChannelWriter<float[]>)state!).TryComplete(), _frames.Writer);

        _pump = Task.Run(() => PumpAsync(token));
        RaiseStatus("Live transcription started. Listening…");
        Log.Info($"Live transcription started (chunk {ChunkSeconds}s, language '{Options.Language ?? "auto"}').");
        return Task.CompletedTask;
    }

    /// <summary>Queues 16 kHz mono samples. Must never block.</summary>
    public void Push(float[] samples16k)
    {
        if (samples16k is null || samples16k.Length == 0 || _disposed || _completing || !_started)
        {
            return;
        }

        // Copy so a re-used capture buffer can never be mutated while we transcribe it.
        var copy = new float[samples16k.Length];
        Array.Copy(samples16k, copy, samples16k.Length);

        if (_frames.Writer.TryWrite(copy))
        {
            Interlocked.Add(ref _samplesPushed, copy.Length);
        }
    }

    /// <summary>Stops accepting audio, flushes the tail and returns the full transcript.</summary>
    public async Task<string> CompleteAsync()
    {
        ThrowIfDisposed();

        var transcript = GetTranscript();
        if (!_started)
        {
            return transcript;
        }

        _completing = true;
        _frames.Writer.TryComplete();

        var pump = _pump;
        if (pump is not null)
        {
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancelled sessions simply stop where they are.
            }
        }

        _started = false;
        IsActive = false;
        RaiseStatus("Live transcription complete.");

        transcript = GetTranscript();
        Log.Info($"Live transcription completed: {transcript.Length} characters from {_chunkCount} chunk(s).");
        return transcript;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _completing = true;

        try
        {
            _lifetimeCts.Cancel();
        }
        catch (Exception ex)
        {
            Log.Warn("Cancelling the live transcription session failed.", ex);
        }

        _frames.Writer.TryComplete();

        var pump = _pump;
        if (pump is not null)
        {
            // Bound the wait: a native Whisper pass can only observe cancellation between segments.
            var stopped = await Task.WhenAny(pump, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
            if (stopped != pump)
            {
                Log.Warn("Live transcription pump did not stop within 10 s; processor left to the GC.");
            }
        }

        if (pump is null || pump.IsCompleted)
        {
            if (_processor is not null)
            {
                try
                {
                    await _processor.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Warn("Disposing the live transcription processor failed.", ex);
                }

                _processor = null;
            }
        }

        _linkedCts?.Dispose();
        _lifetimeCts.Dispose();
        _started = false;
        IsActive = false;
        Owner.ForgetLiveSession(this);
        Log.Info("Live transcription session disposed.");
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        var chunkSamples = ChunkSeconds * SampleRate;
        var maxBufferSamples = MaxBufferSeconds * SampleRate;
        var pending = new List<float>(chunkSamples * 2);

        try
        {
            while (await _frames.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (_frames.Reader.TryRead(out var frame))
                {
                    pending.AddRange(frame);

                    while (pending.Count >= chunkSamples)
                    {
                        var chunk = new float[chunkSamples];
                        pending.CopyTo(0, chunk, 0, chunkSamples);
                        pending.RemoveRange(0, chunkSamples);
                        await ProcessChunkAsync(chunk, isFinal: false, cancellationToken).ConfigureAwait(false);
                    }

                    if (pending.Count > maxBufferSamples)
                    {
                        var dropped = pending.Count - maxBufferSamples;
                        pending.RemoveRange(0, dropped);
                        Log.Warn($"Live transcription is falling behind; dropped {dropped / (double)SampleRate:0.00}s of audio.");
                        RaiseStatus("Falling behind — dropped some audio. Try a faster model.");
                    }
                }
            }

            // The channel was completed: flush the remaining tail so the final words are not lost.
            var tailSamples = pending.Count;
            if (tailSamples >= (int)(MinimumTailSeconds * SampleRate))
            {
                var tail = pending.ToArray();
                pending.Clear();
                await ProcessChunkAsync(tail, isFinal: true, cancellationToken).ConfigureAwait(false);
            }
            else if (tailSamples > 0)
            {
                Log.Info($"Live transcription: discarded {tailSamples / (double)SampleRate:0.00}s trailing tail (shorter than {MinimumTailSeconds:0.0}s).");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Log.Info("Live transcription cancelled.");
        }
        catch (Exception ex)
        {
            Log.Error("The live transcription pump failed.", ex);
            RaiseStatus($"Live transcription stopped: {ex.Message}");
        }
        finally
        {
            IsActive = false;
        }
    }

    private async Task ProcessChunkAsync(float[] chunk, bool isFinal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var processor = await GetProcessorAsync(cancellationToken).ConfigureAwait(false);
        if (processor is null)
        {
            return;
        }

        var chunkNumber = Interlocked.Increment(ref _chunkCount);
        var processedSeconds = Interlocked.Read(ref _samplesTranscribed) / (double)SampleRate;
        RaiseStatus($"Transcribing chunk {chunkNumber} (from {FormatTime(processedSeconds)})…");

        var stopwatch = Stopwatch.StartNew();
        ChunkTranscription result;
        try
        {
            result = await Owner.TranscribeChunkAsync(processor, chunk, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"Live transcription of chunk {chunkNumber} failed.", ex);
            RaiseStatus($"Transcription error: {ex.Message}");
            return;
        }

        stopwatch.Stop();
        var chunkSeconds = chunk.Length / (double)SampleRate;
        Interlocked.Add(ref _samplesTranscribed, chunk.Length);

        var latest = result.Text.Trim();
        string full;
        lock (_sync)
        {
            if (latest.Length > 0)
            {
                _chunkTexts.Add(latest);
            }

            full = string.Join(" ", _chunkTexts).Trim();
            Transcript = full;
        }

        Log.Info(
            $"Live chunk {chunkNumber}: {chunkSeconds:0.0}s audio transcribed in {stopwatch.ElapsedMilliseconds} ms " +
            $"-> \"{latest}\"");

        RaiseStatus(latest.Length > 0
            ? $"Heard {latest.Length} characters ({chunkNumber} chunk(s) processed)."
            : "No speech detected in the last chunk.");

        RaiseTranscript(new LiveTranscriptUpdate
        {
            FullTranscript = full,
            LatestSegment = latest,
            IsPartial = !isFinal,
            Elapsed = TimeSpan.FromSeconds(Interlocked.Read(ref _samplesTranscribed) / (double)SampleRate),
        });
    }

    private async Task<WhisperProcessor?> GetProcessorAsync(CancellationToken cancellationToken)
    {
        if (_processor is not null)
        {
            return _processor;
        }

        if (!Owner.IsReady)
        {
            RaiseStatus("Speech model not loaded.");
            return null;
        }

        // Building a processor touches the native factory: serialize it with active transcriptions.
        _processor = await Owner.CreateProcessorAsync(Options.Language, Options.InitialPrompt, cancellationToken).ConfigureAwait(false);
        return _processor;
    }

    private string GetTranscript()
    {
        lock (_sync)
        {
            return Transcript;
        }
    }

    private void RaiseTranscript(LiveTranscriptUpdate update)
    {
        try
        {
            TranscriptUpdated?.Invoke(this, update);
        }
        catch (Exception ex)
        {
            // A misbehaving UI subscriber must not kill the transcription pump.
            Log.Error("A TranscriptUpdated subscriber threw.", ex);
        }
    }

    private void RaiseStatus(string message)
    {
        try
        {
            StatusChanged?.Invoke(this, message);
        }
        catch (Exception ex)
        {
            Log.Error("A StatusChanged subscriber threw.", ex);
        }
    }

    private static string FormatTime(double seconds)
        => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"m\:ss");

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(LiveTranscriptionSession));
        }
    }
}
