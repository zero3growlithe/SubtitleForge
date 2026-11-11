# SubtitleForge

A Windows subtitle workstation: open a video, let a local Whisper model listen to it,
then edit, re-time and export the subtitles — or burn them straight into a new MP4.

Everything runs on your machine. No cloud, no API keys, no uploads.

![icon](SubtitleForge/Assets/app.ico)

## What it does

- **Transcribe** any video/audio file with whisper.cpp through [Whisper.net](https://github.com/sandrohanea/whisper.net)
  (models downloaded once, then reused offline).
- **GPU first**: ships both the CUDA 12 and the CPU runtime and picks CUDA automatically
  when an NVIDIA driver is present. If CUDA cannot initialise (missing cuBLAS / cuDNN),
  it falls back to CPU instead of failing — the status bar always names the runtime that
  actually ran.
- **Cue list editing**: edit text, set start/end from the playhead, nudge ±100 ms,
  split, merge, add, delete; double-click a cue to jump to it in the preview.
- **Live preview** of the subtitles over the video plus a clickable timeline strip
  showing every cue and the playhead.
- **Cue cleanup** on transcription: long segments are wrapped and split proportionally,
  overlaps are removed, non-speech markers (`[Music]`, `(applause)`) are dropped.
- **Export** `.srt`, `.vtt`, `.ass` — or burn into `*.subtitled.mp4` with ffmpeg
  (H.264, CRF/preset configurable, audio stream copied).
- **Open existing** `.srt` / `.vtt` to edit them with the same tools.

## Requirements

- Windows 11 (or Windows Server 2022+). The current whisper.cpp CPU runtime needs the
  newer C/C++ runtime shipped with these versions.
- **ffmpeg** — `ffmpeg.exe` and `ffprobe.exe`. Either put them on `PATH` or set
  `FfmpegDirectory` in `%AppData%\SubtitleForge\settings.json` (a folder or a full path).
- **.NET 8 Desktop Runtime** (the build is framework-dependent).
- Optional: **NVIDIA driver + CUDA 12 toolkit (>= 12.4.1)** for GPU transcription.
  Without it the app simply runs on the CPU.

## Typical workflow

1. `Open video…`
2. Pick a model — `small` is a good default, `large-v3-turbo` is the best quality/speed
   trade-off on a GPU, `base` is for quick rough drafts.
3. Pick the language (or leave `auto (detect)`), press `Transcribe`.
4. Fix the cues: click a cue, edit the text, use `= playhead` for exact starts/ends,
   `-100 ms` / `+100 ms` to re-sync a block, `Split` / `Merge ↓` to fix boundaries.
5. `Save .srt` (or `.vtt` / `.ass`), or `Burn into MP4…`.

First transcription with a chosen model also downloads it to
`%LocalAppData%\SubtitleForge\models` (base ≈ 142 MB, small ≈ 466 MB, medium ≈ 1.5 GB,
large-v3-turbo ≈ 1.6 GB). Progress is shown in the status bar.

## Where things live

- Settings: `%AppData%\SubtitleForge\settings.json`
- Models: `%LocalAppData%\SubtitleForge\models`
- Crash reports: `%LocalAppData%\SubtitleForge\crash.log` — every entry starts with the
  build sha, which is also shown in the window title.

## Build

Nothing is built locally; GitHub Actions is the build authority
(`.github/workflows/build.yml`): `windows-latest`, .NET 8, `dotnet publish -r win-x64
--self-contained false`, zipped artifact `SubtitleForge-win-x64`.

```
dotnet publish SubtitleForge/SubtitleForge.csproj -c Release -r win-x64 --self-contained false -o publish
```

## Known limitations

- The preview uses WPF `MediaElement` (Media Foundation), so exotic codecs may not
  preview. Transcription and burn-in go through ffmpeg and work regardless.
- Whisper reads the whole extracted 16 kHz mono track into memory: roughly 230 MB of
  samples per hour of audio.
- Burn-in re-encodes the video, so it is CPU/GPU-bound and much slower than exporting
  a sidecar `.srt`.
- Cue splitting is proportional to text length; it does not do word-level forced
  alignment, so a very long single Whisper segment may still need manual `Split`.

## Licence

MIT.
