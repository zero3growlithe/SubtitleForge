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
  Column widths are draggable in the header row.
- **Live preview** of the subtitles over the video plus a clickable timeline strip
  showing every cue and the playhead.
- **Cue cleanup** on transcription: long segments are wrapped and split proportionally,
  overlaps are removed, non-speech markers (`[Music]`, `(applause)`) are dropped.
- **Export** `.srt`, `.vtt`, `.ass` — or burn into `*.subtitled.mp4` with ffmpeg
  (H.264, CRF/preset configurable, audio stream copied).
- **Open existing** `.srt` / `.vtt` to edit them with the same tools.
- **Models…** dialog: shows every model, its size on disk and whether it is downloaded,
  with per-model `Delete`, `Delete all`, and a shortcut to the models folder.
- **Multitrack timeline** under the cue strip: the video track (with preview frames every
  few percent of the clip) plus any number of audio tracks rendered as waveforms.
  Tracks can be added, renamed, recoloured (the waveform colour is derived from the
  background so it always contrasts), drag-reordered, split at the playhead and deleted —
  every one of those changes is undoable.
- **Record from the microphone** straight onto the selected audio track, starting at the
  playhead, with the waveform growing live as you speak. One press records, the next
  stops; `Ctrl+Z` restores the track exactly as it was before the take.
- **Drop media files** from Explorer onto the timeline: each file becomes its own track named
  after the file, placed at the drop position — audio files as a waveform exactly like a
  recorded take, video files as a video lane with thumbnails (undoable with `Ctrl+Z`).
- **The preview follows the tracks.** Whatever the playhead sits on is what you see and
  hear: the *topmost* video lane with a clip under the playhead is shown, every audio lane
  with a clip there is played (mixed together), and a stretch with no video clip shows a
  black frame while the audio keeps running.

### Timeline editing

- Every clip is a **block of limited width** inside its lane, so a split leaves the first
  half ending exactly at the playhead and the second half starting there.
- **Drag a clip** left/right inside its lane to slide it; clips of one lane never overlap.
  When the drop would land on top of a neighbour it snaps to that neighbour's nearest edge
  and a vertical yellow line shows the snap position. If the landing spot is taken, the
  clips to the right are pushed along by however much is needed.
- **Drag a clip onto another lane** of the same type to join that track, or **between lanes**
  (or below the last one) to give it a track of its own.
- **Drag the ruler or an empty lane** to scrub the playhead; drag a lane's **name** to
  reorder whole tracks. `Shift`+drag on a clip scrubs instead of moving it.
- **Drag the bar between the preview and the tracks** to make the video smaller and give the
  timeline more room (or the other way round).
- **Seeking repaints the preview** whether playback is running or paused — the frame under
  the playhead is shown for both video lanes and a black frame for gaps.
- Subtitles are built from **all** audio clips on the timeline and merged in playback order.
- `Ctrl+S` (or `Save project`) saves the project; `Open project…` loads one.

### Performance

- Building the timeline no longer decodes the whole movie. The thumbnail strip comes from a
  handful of **parallel keyframe seeks** (one short ffmpeg per thumbnail, seek in front of the
  input), and the waveform and the thumbnails are produced **at the same time** instead of one
  after the other.
- The preview plays a **cached 540p H.264 proxy** built in the background on first open
  (`%LocalAppData%\SubtitleForge\proxies`), which makes seeking and playback smooth even for
  4K sources. Export, burn-in and transcription always use the original full-resolution file.
  Videos that are 540p or smaller skip the proxy entirely.
- Proxies are cached **per source file** (keyed by path + size + modified time) and built one
  at a time in the background. Moving a clip, splitting it, or loading a project reuses the
  cache; only a genuinely new file is ever encoded, and never the whole timeline again.
- The **ruler shows proxy readiness** as a blue bar along its bottom edge: solid blue where a
  clip already plays from its proxy (or needs none), translucent blue while the encode is
  still filling in, nothing while it has not started. The status line reports the percentage
  while an encode runs.

### Projects

Closing the window with unsaved edits asks whether to save the project first.

`.sfproj` files are JSON. Media is only **referenced** — the video path and any imported
audio path — while microphone takes are exported next to the project as
`<project>-track<n>-clip<m>.wav` clips and referenced by name, so the project file itself
stays a few kilobytes.

### Shortcuts

- `Space` — play / pause
- `,` / `.` — step one frame back / forward (frame rate read from the file)
- `←` / `→` — seek one second; `Shift`+`←` / `Shift`+`→` — seek ten seconds
- `Del` — delete the selected track(s) or clip(s)
- `Ctrl+Shift+D` — split every selected clip at the playhead
- `Ctrl+Z` — undo the last edit (move, add, delete, split, reorder, rename, recolour, record)
- `Ctrl+S` — save the project
- double-click a track header — rename it; right-click a track — colour / split / delete

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
--self-contained false`. It produces two artifacts:

- `SubtitleForge-win-x64-cuda` — CPU **and** CUDA 12 native runtimes (~250 MB zipped).
  Use this one for GPU transcription.
- `SubtitleForge-win-x64-cpu` — CPU natives only (~20 MB zipped). Works everywhere,
  just slower; handy for testing the UI.

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
