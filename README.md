# Loquinota (Vibe Coded Project)

**Local AI meeting notes & transcription for Windows — live capture, MP4 import, structured notes, fully offline.**

loquinota listens to your microphone and/or your PC's system audio (WASAPI loopback), transcribes speech
locally with [Whisper.net](https://github.com/sandrocsimas/whisper.net) (no cloud, no API keys), turns the
transcript into structured notes, and keeps every session in a local SQLite library that can be exported to
`.txt` at any time.

WinUI 3 · Material Design 3 · NAudio · Whisper.net · FFmpeg · SQLite

---

## Features

| Area | What it does |
|---|---|
| **History / Dashboard** | Newest-first session library with title, date, duration, source badge and summary stats. Click a card to open the viewer. |
| **Live Capture** | Microphone and/or system-audio loopback capture, mixed to 16 kHz mono, transcribed in ~3 s chunks with a live preview. Stop → auto-generated notes → edit → save. |
| **Import MP4** | Pick an MP4, extract the audio track with FFmpeg (16 kHz mono PCM), transcribe it, generate notes, edit, save. |
| **Session Viewer / Editor** | Edit title, notes and transcript; save; export to `.txt` via the Windows save picker; delete. |
| **Persistence** | SQLite database plus per-session WAV files under `%LOCALAPPDATA%\Loquinota\`. Survives restarts. |
| **Privacy** | Everything (audio, model, transcription, notes) stays on the device. No telemetry, no network calls except the optional one-time model/FFmpeg downloads. |

---

## Requirements

- Windows 10 1809+ or Windows 11 (x64; ARM64 build also configured)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) 8.0.4xx (the repository pins 8.0.408 via `global.json`)
- Internet access **once**, to download:
  - a Whisper model (`ggml-tiny/base/small/medium`, 75 MB – 1.5 GB), and
  - FFmpeg (only for MP4 import; ~100 MB), unless you already have `ffmpeg.exe`
- No Visual Studio required: the app builds with `dotnet build`.

---

## Quick start

Run these commands from the repository root (the folder containing `global.json` and `Loquinota.sln`):

```powershell
# Debug build
dotnet build .\Loquinota\Loquinota.csproj -c Debug -p:Platform=x64

# Run
.\Loquinota\bin\x64\Debug\net8.0-windows10.0.26100.0\win-x64\Loquinota.exe

# Release build
dotnet build .\Loquinota\Loquinota.csproj -c Release -p:Platform=x64
```

The app is **unpackaged and self-contained with respect to the Windows App SDK**
(`WindowsPackageType=None`, `WindowsAppSDKSelfContained=true`), so it runs directly from `bin\` without
installing the Windows App Runtime or an MSIX package.

### First run: prepare a speech model

No model is bundled (they are large). The **Live Capture** page shows a *Speech model not ready* banner:

1. Choose a model (`tiny` is fastest, `base` is a good default, `small`/`medium` are more accurate but slower).
2. Click **Download model** — it is fetched from Hugging Face into
   `%LOCALAPPDATA%\Loquinota\Models\ggml-<name>.bin` with progress.
3. Alternatively click **Use local model…** and pick an existing `ggml-*.bin` / `*.gguf` file.

Manual download:

```
https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin
-> %LOCALAPPDATA%\Loquinota\Models\ggml-base.bin
```

### MP4 import: FFmpeg

FFmpeg is **not** bundled. On the first import the app searches, in order:

1. `<app folder>\ffmpeg\ffmpeg.exe`
2. `%LOCALAPPDATA%\Loquinota\ffmpeg\ffmpeg.exe`
3. `ffmpeg.exe` on `PATH`
4. downloads it automatically (Xabe.FFmpeg.Downloader, then a direct gyan.dev zip fallback)

Manual fallback: download `ffmpeg-release-essentials.zip` from <https://www.gyan.dev/ffmpeg/builds/>,
extract `bin\ffmpeg.exe` (and `ffprobe.exe` if present) to `%LOCALAPPDATA%\Loquinota\ffmpeg\`, and restart.

---

## Project structure

```
.
├── global.json                     # pins .NET SDK 8.0.408
├── Loquinota.sln
├── README.md
└── Loquinota
    ├── Loquinota.csproj            # WinUI 3, self-contained Windows App SDK, NuGet packages
    ├── app.manifest                # PerMonitorV2 DPI, long paths, asInvoker
    ├── Package.appxmanifest        # optional MSIX variant (mic + broadFileSystemAccess)
    ├── App.xaml / App.xaml.cs      # resource dictionaries, startup, crash handling
    ├── MainWindow.xaml / .cs       # NavigationView shell, Mica/Acrylic backdrop, custom title bar
    ├── Models
    │   ├── Session.cs
    │   ├── AudioModels.cs
    │   ├── TranscriptionModels.cs
    │   └── ProcessingModels.cs     # progress + AppSettings
    ├── Services
    │   ├── AppServices.cs          # composition root / initialization
    │   ├── AudioCaptureService.cs  # WasapiCapture + WasapiLoopbackCapture, mixing, resampling
    │   ├── AudioDeviceCatalog.cs
    │   ├── WhisperTranscriptionService.cs
    │   ├── LiveTranscriptionSession.cs
    │   ├── ModelManager.cs
    │   ├── Mp4Processor.cs
    │   ├── FfmpegBootstrapper.cs
    │   ├── NotesGenerator.cs
    │   ├── SessionRepository.cs
    │   ├── ExportService.cs
    │   └── AppSettingsService.cs
    ├── Pages
    │   ├── HistoryPage.xaml(.cs)
    │   ├── LiveCapturePage.xaml(.cs)
    │   ├── ImportMp4Page.xaml(.cs)
    │   └── SessionViewerPage.xaml(.cs)
    ├── Helpers
    │   ├── AppStorage.cs           # ApplicationData / %LOCALAPPDATA% fallback
    │   ├── WavIO.cs                # WAV decode/encode to 16 kHz mono float
    │   ├── BackdropHelper.cs       # Mica -> Acrylic -> solid fallback
    │   ├── ExceptionExtensions.cs
    │   ├── Log.cs
    │   └── SelfTestRunner.cs       # --selftest headless checks
    └── Themes
        ├── Colors.xaml             # Material 3 color roles (light/dark/high-contrast)
        ├── Styles.xaml             # Material buttons, cards, inputs, navigation, type scale
        └── MaterialIcons.xaml      # MaterialIcon control template
```

Data layout (`%LOCALAPPDATA%\Loquinota\`):

```
Loquinota\
├── loquinota.db          # SQLite sessions
├── settings.json         # selected model / language / devices
├── Sessions\{id}.wav     # 16 kHz mono PCM16 audio per session
├── Models\ggml-*.bin     # Whisper models
├── ffmpeg\ffmpeg.exe     # optional, auto-downloaded
├── Temp\                 # extraction scratch space
└── Logs\loquinota.log    # diagnostics

```

## License / third-party notices

This project uses the following open-source packages: NAudio (MIT), Whisper.net (MIT), whisper.cpp (MIT),
FFMpegCore (MIT), Xabe.FFmpeg.Downloader (MIT), Material.Icons.WinUI3 (MIT), Microsoft.Data.Sqlite (MIT),
Windows App SDK (MIT). Whisper models are distributed by OpenAI/ggerganov under their respective licenses;
FFmpeg is LGPL/GPL depending on the build you download.
