using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Loquinota.Models;
using Loquinota.Services;
using NAudio.Wave;

namespace Loquinota.Helpers;

/// <summary>
/// Headless end-to-end smoke test, run with:  Loquinota.exe --selftest
/// It exercises the real services (Whisper, WASAPI loopback, FFmpeg, SQLite) and writes
/// a JSON report to %LOCALAPPDATA%\Loquinota\Logs\selftest-report.json.
/// Exit code 0 means every check passed (skipped checks do not fail the run).
/// </summary>
public static class SelfTestRunner
{
    private static readonly List<Check> Checks = new();
    private static readonly Stopwatch Total = new();

    public static bool IsRequested =>
        Environment.GetCommandLineArgs().Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase));

    private sealed class Check
    {
        public string Name { get; set; } = "";
        public bool Passed { get; set; }
        public bool Skipped { get; set; }
        public string Detail { get; set; } = "";
        public long ElapsedMs { get; set; }
    }

    public static async Task<int> RunAsync()
    {
        Total.Start();
        AppStorage.EnsureCreated();
        var reportPath = Path.Combine(AppStorage.LogsDirectory, "selftest-report.json");

        var speech = FindSpeechAsset();
        string? recognizedText = null;
        string? liveText = null;
        string? loopbackText = null;
        string? mp4Text = null;
        string? ffmpegPath = null;
        string? sessionId = null;

        await CheckAsync("services-init", async () =>
        {
            await AppServices.EnsureInitializedAsync();
            return (AppServices.Sessions is not null && AppServices.Audio is not null, AppStorage.RootDirectory);
        });

        var modelReady = false;
        await CheckAsync("model-ready", async () =>
        {
            // Prefer a model that is already installed, so the self-test works offline.
            var installed = AppServices.Models.GetInstalledModels();
            var modelName = installed.FirstOrDefault()?.Name ?? "tiny";
            modelReady = await AppServices.EnsureModelLoadedAsync(modelName, new Progress<ModelDownloadProgress>());
            return (modelReady && AppServices.Whisper.IsReady, $"{AppServices.Whisper.ModelName}: {AppServices.Whisper.ModelPath ?? "no model loaded"}");
        });

        await CheckAsync("wavio-roundtrip", () =>
        {
            var path = Path.Combine(AppStorage.TempDirectory, "selftest-tone.wav");
            const int rate = 16000;
            var tone = new float[rate * 2];
            for (var i = 0; i < tone.Length; i++)
            {
                tone[i] = 0.5f * MathF.Sin(2 * MathF.PI * 1000 * i / rate);
            }

            WavIO.WritePcm16(path, tone, rate);
            if (!WavIO.LooksLikeWav(path))
            {
                return Task.FromResult((false, "WAV header missing"));
            }

            var read = WavIO.ReadMono16k(path);
            var peak = read.Length == 0 ? 0 : read.Max(Math.Abs);
            var ok = Math.Abs(read.Length - tone.Length) < 100 && peak > 0.4;
            return Task.FromResult((ok, $"samples={read.Length}, peak={peak:0.000}"));
        });

        if (modelReady && speech is not null)
        {
            await CheckAsync("batch-transcribe-wav", async () =>
            {
                var result = await AppServices.Whisper.TranscribeWavAsync(speech);
                recognizedText = result.Text;
                var words = result.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                return (words >= 3, Truncate(result.Text, 160));
            });

            await CheckAsync("live-streaming-session", async () =>
            {
                var samples = WavIO.ReadMono16k(speech);
                var session = AppServices.Whisper.CreateLiveSession(new LiveTranscriptionOptions { ChunkSeconds = 3 });
                var updates = 0;
                session.TranscriptUpdated += (_, _) => Interlocked.Increment(ref updates);
                await session.StartAsync();
                for (var index = 0; index < samples.Length; index += 320)
                {
                    var count = Math.Min(320, samples.Length - index);
                    session.Push(samples[index..(index + count)]);
                    await Task.Delay(2);
                }

                liveText = await session.CompleteAsync();
                await session.DisposeAsync();
                return (updates > 0 && !string.IsNullOrWhiteSpace(liveText), $"updates={updates}, text='{Truncate(liveText, 120)}'");
            });
        }
        else
        {
            Skip("batch-transcribe-wav", speech is null ? "no speech test asset found" : "model not ready");
            Skip("live-streaming-session", speech is null ? "no speech test asset found" : "model not ready");
        }

        await CheckAsync("ffmpeg-bootstrap", async () =>
        {
            ffmpegPath = await AppServices.Mp4.EnsureFfmpegAsync();
            return (File.Exists(ffmpegPath), ffmpegPath);
        });

        if (ffmpegPath is not null && speech is not null)
        {
            var testMp4 = Path.Combine(AppStorage.TempDirectory, "selftest-speech.mp4");
            var extractedWav = Path.Combine(AppStorage.TempDirectory, "selftest-extracted.wav");
            var mp4Ok = false;

            await CheckAsync("mp4-create-test-file", () =>
            {
                if (File.Exists(testMp4) && new FileInfo(testMp4).Length > 20_000)
                {
                    return Task.FromResult((true, "reused existing test MP4"));
                }

                var args = $"-y -f lavfi -i testsrc=size=320x240:rate=15 -i \"{speech}\" -shortest " +
                           $"-c:v libx264 -pix_fmt yuv420p -c:a aac -b:a 128k \"{testMp4}\"";
                var ok = RunProcess(ffmpegPath, args, 120_000);
                return Task.FromResult((ok && File.Exists(testMp4), ok ? "MP4 created" : "ffmpeg failed"));
            });

            if (File.Exists(testMp4))
            {
                await CheckAsync("mp4-extract-audio", async () =>
                {
                    var result = await AppServices.Mp4.ExtractAudioAsync(testMp4, extractedWav, new Progress<ProcessingProgress>());
                    mp4Ok = WavIO.LooksLikeWav(extractedWav) && new FileInfo(extractedWav).Length > 8_000;
                    return (mp4Ok && result.Duration.TotalSeconds > 1, $"duration={result.Duration:mm\\:ss}, wav={new FileInfo(extractedWav).Length / 1024} KB");
                });

                if (mp4Ok && modelReady)
                {
                    await CheckAsync("mp4-transcribe-audio", async () =>
                    {
                        var result = await AppServices.Whisper.TranscribeWavAsync(extractedWav);
                        mp4Text = result.Text;
                        return (result.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 3, Truncate(result.Text, 160));
                    });
                }
                else
                {
                    Skip("mp4-transcribe-audio", "extraction or model unavailable");
                }
            }
            else
            {
                Skip("mp4-extract-audio", "test MP4 could not be created");
            }
        }
        else
        {
            Skip("mp4-create-test-file", "ffmpeg or speech asset unavailable");
            Skip("mp4-extract-audio", "ffmpeg or speech asset unavailable");
            Skip("mp4-transcribe-audio", "ffmpeg or speech asset unavailable");
        }

        if (modelReady && speech is not null)
        {
            await CheckAsync("live-loopback-caption", async () =>
            {
                var liveSession = AppServices.Whisper.CreateLiveSession(new LiveTranscriptionOptions { ChunkSeconds = 3 });
                var captions = 0;
                var maxLevel = 0.0;
                liveSession.TranscriptUpdated += (_, _) => Interlocked.Increment(ref captions);

                var capture = AppServices.Audio;
                void OnSamples(object? _, AudioSamplesEventArgs e)
                {
                    try
                    {
                        liveSession.Push(e.Samples);
                    }
                    catch
                    {
                        // The session may already be flushing.
                    }
                }

                void OnLevel(object? _, double level) => maxLevel = Math.Max(maxLevel, level);

                capture.SamplesAvailable += OnSamples;
                capture.LevelChanged += OnLevel;
                var wavOut = Path.Combine(AppStorage.TempDirectory, "selftest-loopback.wav");
                try
                {
                    await liveSession.StartAsync();
                    await capture.StartAsync(new AudioCaptureOptions
                    {
                        CaptureMicrophone = false,
                        CaptureSystemAudio = true,
                        WavOutputPath = wavOut
                    });

                    using (var reader = new AudioFileReader(speech))
                    using (var output = new WaveOutEvent { DesiredLatency = 150 })
                    {
                        output.Init(reader);
                        output.Play();
                        var deadline = DateTime.UtcNow.AddSeconds(30);
                        while (output.PlaybackState == PlaybackState.Playing && DateTime.UtcNow < deadline)
                        {
                            await Task.Delay(200);
                        }
                    }

                    await Task.Delay(3000);
                    var result = await capture.StopAsync();
                    await Task.Delay(1500);
                    loopbackText = await liveSession.CompleteAsync();
                    var ok = result.Duration.TotalSeconds > 2 && File.Exists(wavOut) && maxLevel > 0.001;
                    return (ok, $"captured={result.Duration:mm\\:ss}, maxLevel={maxLevel:P0}, captions={captions}, text='{Truncate(loopbackText, 100)}'");
                }
                finally
                {
                    capture.SamplesAvailable -= OnSamples;
                    capture.LevelChanged -= OnLevel;
                    await liveSession.DisposeAsync();
                }
            });
        }
        else
        {
            Skip("live-loopback-caption", speech is null ? "no speech test asset found" : "model not ready");
        }

        await CheckAsync("mic-capture-samples", async () =>
        {
            var wavOut = Path.Combine(AppStorage.TempDirectory, "selftest-mic.wav");
            var frames = 0;
            var capture = AppServices.Audio;
            void OnSamples(object? _, AudioSamplesEventArgs e) => Interlocked.Add(ref frames, e.Samples.Length);
            capture.SamplesAvailable += OnSamples;
            try
            {
                await capture.StartAsync(new AudioCaptureOptions
                {
                    CaptureMicrophone = true,
                    CaptureSystemAudio = false,
                    WavOutputPath = wavOut
                });
                await Task.Delay(3000);
                var result = await capture.StopAsync();
                return (frames > 16000 && result.Duration.TotalSeconds > 1, $"frames={frames}, duration={result.Duration:mm\\:ss}");
            }
            finally
            {
                capture.SamplesAvailable -= OnSamples;
            }
        });

        await CheckAsync("session-persist-and-reload", async () =>
        {
            var id = Guid.NewGuid().ToString("N");
            var session = new Session
            {
                Id = id,
                Title = "Selftest - local pipeline",
                CreatedAt = DateTimeOffset.Now,
                Duration = TimeSpan.FromSeconds(42),
                SourceType = SessionSourceType.Live,
                AudioFilePath = AppServices.Sessions.GetAudioFilePath(id),
                TranscriptText = recognizedText ?? "selftest transcript",
                NotesText = "## Overview\nSelftest note.\n\n## Key points\n- first point",
                ModelName = AppServices.Whisper.ModelName,
                Language = "auto"
            };

            await AppServices.Sessions.UpsertAsync(session);
            sessionId = id;
            var fresh = new SessionRepository(AppServices.Sessions.DatabasePath);
            await fresh.InitializeAsync();
            var reloaded = await fresh.GetAsync(id);
            var all = await fresh.GetAllAsync();
            var sessionsBeforeCleanup = all.Count;
            var ok = reloaded is not null
                     && reloaded.Title == session.Title
                     && reloaded.Duration == session.Duration
                     && all.Any(s => s.Id == id);

            // The self-test must not leave test sessions in the user's library.
            await AppServices.Sessions.DeleteAsync(id);
            var removed = await fresh.GetAsync(id) is null;
            return (ok && removed, $"reloaded={reloaded is not null}, sessionsBeforeCleanup={sessionsBeforeCleanup}, removed={removed}");
        });

        await CheckAsync("export-text", () =>
        {
            var session = new Session
            {
                Id = "export-test",
                Title = "Selftest export",
                CreatedAt = DateTimeOffset.Now,
                Duration = TimeSpan.FromSeconds(42),
                SourceType = SessionSourceType.Live,
                NotesText = "## Overview\nNote body",
                TranscriptText = recognizedText ?? "transcript body"
            };
            var text = AppServices.Export.BuildExportText(session, includeTranscript: true);
            return Task.FromResult((text.Contains("Selftest export") && text.Contains("TRANSCRIPT"), $"length={text.Length}"));
        });

        await CheckAsync("notes-generator", () =>
        {
            var notes = NotesGenerator.Generate(
                recognizedText ?? "The quick brown fox jumps over the lazy dog. This is a test.",
                SessionSourceType.Live,
                TimeSpan.FromSeconds(12));
            return Task.FromResult((notes.Contains("##") && notes.Length > 20, Truncate(notes, 140)));
        });

        var passed = Checks.Count(c => c.Passed);
        var failed = Checks.Count(c => !c.Passed && !c.Skipped);
        var skipped = Checks.Count(c => c.Skipped);
        var allPassed = failed == 0;

        var report = new
        {
            timestamp = DateTimeOffset.Now.ToString("O"),
            machine = Environment.MachineName,
            os = Environment.OSVersion.VersionString,
            allPassed,
            passed,
            failed,
            skipped,
            elapsedMs = Total.ElapsedMilliseconds,
            recognizedText,
            liveText,
            loopbackText,
            mp4Text,
            sessionId,
            model = AppServices.Whisper is { IsReady: true } ? AppServices.Whisper.ModelName : null,
            modelPath = AppServices.Whisper?.ModelPath,
            ffmpegPath,
            speechAsset = speech,
            dataFolder = AppStorage.RootDirectory,
            checks = Checks
        };

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await File.WriteAllTextAsync(reportPath, json);
        Log.Info($"SELFTEST finished: passed={passed} failed={failed} skipped={skipped}. Report: {reportPath}");
        return allPassed ? 0 : 1;
    }

    private static async Task CheckAsync(string name, Func<Task<(bool ok, string detail)>> body)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var (ok, detail) = await body();
            Checks.Add(new Check { Name = name, Passed = ok, Detail = detail, ElapsedMs = stopwatch.ElapsedMilliseconds });
        }
        catch (Exception ex)
        {
            Checks.Add(new Check { Name = name, Passed = false, Detail = ex.ToString(), ElapsedMs = stopwatch.ElapsedMilliseconds });
            Log.Error($"SELFTEST check '{name}' threw.", ex);
        }
    }

    private static void Skip(string name, string reason)
        => Checks.Add(new Check { Name = name, Skipped = true, Detail = reason });

    private static string? FindSpeechAsset()
    {
        var candidates = new List<string>();
        var custom = Environment.GetEnvironmentVariable("LOQUINOTA_TEST_ASSETS");
        if (!string.IsNullOrWhiteSpace(custom))
        {
            candidates.Add(Path.Combine(custom, "speech-en.wav"));
            candidates.Add(Path.Combine(custom, "speech.wav"));
        }

        candidates.Add(Path.Combine(AppStorage.RootDirectory, "TestAssets", "speech-en.wav"));
        candidates.Add(Path.Combine(AppStorage.RootDirectory, "TestAssets", "speech.wav"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "TestAssets", "speech-en.wav"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "TestAssets", "speech.wav"));
        candidates.Add(Path.Combine(Environment.CurrentDirectory, "TestAssets", "speech-en.wav"));
        candidates.Add(Path.Combine(Environment.CurrentDirectory, "TestAssets", "speech.wav"));

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var clean = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length <= max ? clean : clean[..max] + "...";
    }

    private static bool RunProcess(string executable, string arguments, int timeoutMs)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            Log.Warn($"Process '{Path.GetFileName(executable)}' timed out.");
            return false;
        }

        if (process.ExitCode != 0)
        {
            Log.Warn($"Process '{Path.GetFileName(executable)}' exited with {process.ExitCode}: {Truncate(output.ToString(), 300)}");
        }

        return process.ExitCode == 0;
    }
}
