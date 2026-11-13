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
- **Live preview** of the subtitles over the video, with the cue positions drawn straight
  into the seek bar below it (blue marks = cue start/length) plus the playhead.
- **Cue cleanup** on transcription: long segments are wrapped and split proportionally,
  overlaps are removed, non-speech markers (`[Music]`, `(applause)`) are dropped.
- **One `Export…` button.** Its menu offers the subtitle formats (`.srt` / `.vtt` / `.ass`)
  and `As MP4…`, which opens a small dialog with the toggles **Include video**,
  **Include audio**, **Include subtitles (as a file)** and **Burn in subtitles**, plus a
  **video resolution** (same as source, 4K, 1440p, 1080p, 720p, 480p) and an **audio quality**
  (same as source, or 320 / 256 / 192 / 128 kbps). Without video the burn-in renders onto a
  **solid colour backdrop** (chroma green by default) so the result keys out cleanly in another
  program; audio and the sidecar can be combined freely.

  **Burn-in is rendered, not typeset:** every frame is drawn with the same control the preview uses
  and piped into ffmpeg as an alpha overlay, so the export matches the preview exactly — profile
  font, gradient fills, the whole effect stack (including `Composition` targets) and the idle /
  pop-in animations, which a plain `.ass` file could never carry. Frames without a cue are skipped
  without drawing and the drawing runs on its own STA thread, so a long export leaves the window
  responsive.

  **Audio is copied, not re-encoded,** whenever it does not have to be: "same as source" passes the
  AAC stream through untouched, and a codec MP4 cannot hold (Opus, Vorbis, PCM, …) is transcoded at
  320 kbps instead of failing the export.
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
  recorded take, video files as a video lane with thumbnails **plus an audio lane directly
  below it** carrying that clip's own audio (undoable with `Ctrl+Z`).
- **The preview follows the tracks.** Whatever the playhead sits on is what you see and
  hear: the *topmost* video lane with a clip under the playhead is shown, every audio lane
  with a clip there is played (mixed together), and a stretch with no video clip shows a
  black frame while the audio keeps running.
- **Sound comes only from the audio lanes** — the preview itself is always muted, so a video
  clip is heard through its audio lane (and never twice).
- **Clicking or scrubbing the timeline pauses playback first**, then moves the playhead, so
  the preview does not run away from where you clicked.

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
- Subtitles are built from **all** audio clips on the timeline. Each clip is cut into
  independent **150-second windows** (with a half-second overlap that is trimmed afterwards)
  before it reaches Whisper, because a single pass over many minutes of audio is where
  Whisper's timing drifts and starts looping; one model load covers all the windows.
- `Ctrl+S` (or `Save project`) saves the project; `Open project…` loads one.

### Performance

- Building the timeline no longer decodes the whole movie. The thumbnail strip comes from a
  handful of **parallel keyframe seeks** (one short ffmpeg per thumbnail, seek in front of the
  input), and the waveform and the thumbnails are produced **at the same time** instead of one
  after the other.
- The preview plays a **cached 540p H.264 proxy** built in the background on first open
  (`%LocalAppData%\SubtitleForge\proxies`), which makes seeking and playback smooth even for
  4K sources. Export, burn-in and transcription always use the original full-resolution file.
  **Full HD and smaller sources are never proxied** — they are cheap to seek directly, so
  the original file is played as-is and only bigger-than-1080p sources get a proxy.
- Proxy encodes use the **GPU** (`h264_nvenc`) whenever the ffmpeg build and driver provide
  it, and silently fall back to `libx264` on the CPU if the hardware encode fails. The audio
  lanes play from a cached **audio-only AAC file** per source, so hearing the timeline never
  means decoding a 4K video stream.
- Proxies are cached **per source file** (keyed by path + size + modified time) and built one
  at a time in the background. Moving a clip, splitting it, or loading a project reuses the
  cache; only a genuinely new file is ever encoded, and never the whole timeline again.
- The **ruler shows proxy readiness** as a blue bar along its bottom edge: solid blue where a
  clip already plays from its proxy (or needs none), translucent blue while the encode is
  still filling in, nothing while it has not started.
- **Click the bar in the lower-left corner** to open a popup listing every background job
  (proxy encodes, the audio cache, transcription, exports) with its live percentage; the same
  bar shows how many tasks are running.

### Subtitle appearance profiles

The subtitle overlay is rendered as per-character geometry, so every glyph can be animated on
its own. Three kinds of named profile drive it:

- **Style** — font family, bold/italic, size (pixels on a 1080-tall canvas), fill colour
  (solid or a two-stop gradient with a direction), how many words to show at a time (`0` = the
  whole cue, `1` = one word at a time), and an optional highlight colour for the word currently
  being spoken.
- **Style, spacing** — character spacing and word spacing (in design pixels, negative values
  tighten the text), so a title can be tracked out without touching the font.
- **Idle animation** — shake, sine drift on X and Y (amplitude as a multiple of the font size,
  frequency in Hz, phase), pulsing scale, wiggle rotation and a spin around the vertical axis
  (faked by squashing the width). Each channel has its own on/off switch, and the profile can
  animate the whole text or every character with a stagger - which is what turns the sine into
  a wave travelling through the letters.
- **Pop-in animation** — slide in from any side with a damped spring, scale in with jelly
  wobble (separate X/Y amplitude, frequency and falloff), or fade in. Either the whole text
  enters at once or the characters enter one by one with an adjustable delay.

Outlines and shadows share **one effect stack**: a single list, a single `Add effect` button, and
a **Type** dropdown (`Outline` / `Shadow`) in the properties that shows only the fields that matter
for that type — thickness, offsets, colour and gradient for both, plus `Opacity` for shadows and
`Inside the glyph` / `Fill the glyph` for outlines. The list order **is** the paint order (first
entry sits closest to the fill), and entries can be reordered with the `▲` / `▼` buttons under the
list or with `Ctrl+Up` / `Ctrl+Down`.

Every effect carries a **target**:

- `Font` paints the effect per glyph, underneath that glyph's fill.
- `Composition` combines all glyphs into **one silhouette** first and paints the effect around
  that, so a ring wraps the whole line instead of every letter, and a composition shadow or fill
  covers the block as a single shape instead of per-character copies.

An outline can be drawn **inside** the glyph (either the `Inside` switch or a negative thickness)
and can **fill** the glyph with its colour as a base layer, which is how a solid silhouette under a
translucent gradient is made.

**Burned-in exports are rendered, not typeset.** `As MP4…` with *Burn in subtitles* draws every
frame with the very same control the preview uses and pipes the raw frames into ffmpeg as an alpha
overlay, so the export matches the preview exactly: the profile's font, gradient fills, the whole
effect stack (including `Composition` targets) and the idle / pop-in animations. The frames are
drawn on a separate STA thread so a long export never freezes the window, frames without a cue are
skipped without drawing, and the picture is scaled to the resolution picked in the export dialog with
the subtitles rendered at that same resolution.

Colours are picked with a real colour picker — a saturation/value square, hue and alpha ramps,
preset swatches and an optional hex field (`#RRGGBB` or `#AARRGGBB`) — so the swatches in the
editor are the only thing you click. Every numeric field is scrubbable Unity-style: drag left or
right to change the value (`Shift` = fine, `Ctrl` = coarse) or click once to type it.

Every creator has a **Duplicate** button next to `New` / `Delete`, and switching profiles inside a
creator saves the edits of the profile you were just in, so the picker doubles as a commit. `Enter`
in any field submits it — the value is validated and the field gives up focus instead of triggering
the window's Save button — and `Esc` puts the previous value back.

**The cue list colours cue times, and weights overrides.**

- **Red** — the cue overlaps another cue. Times are checked as a whole, so a cue nested inside a
  longer one flags both, which is what a doubled-up subtitle looks like on screen.
- **Light blue** — the cue ends exactly where the next one starts (or within a tenth of a
  millisecond). That is a clean handover, not a problem, so it is marked but not warned about.
- **Bold with a `*` after the number** — the cue carries its own style, idle or pop-in profile. The
  colour is left to the timing state; weight and the star carry the "not the defaults" meaning, so
  one row never has to say two things in one channel.

Hovering a row draws an outline instead of a background, which used to paint over the selection
colour and make selected rows look unselected.

**Stacked cues in the preview.** When several cues cover the same moment the preview draws all of
them, stacked exactly the way the burn-in export does, so nothing that ends up in the video is
invisible while editing. `Show overlapping cues` in the track toolbar turns that off and falls back
to one cue — the selected one when it is on screen. The preference lives in `settings.json`.

**Cue placement is edited as an offset.** The `X` / `Y` fields under the cue editor show how far the
cue sits from the default subtitle position in percent of the frame — `0` is the default, `-12` is
twelve percent to the left (or up). `Scale` starts at `1`, which means the size from the text style
profile. Multi-selection shows a `—` for any field the selected cues disagree on.

**Global subtitle time offset.** `Time offset` in the cue panel shifts every cue by that many
seconds (negative = earlier) when the preview plays it, on the cue strip, and in every export — SRT,
VTT, ASS and the burned-in video. The times listed on the cues stay untouched, so the transcription
timing is never destroyed by trying an offset, and `= playhead`, `+ New cue` and the arrow nudges
keep landing where you see them.

The **splitter sizes and the maximised state are remembered**: the height of the multitrack timeline,
the width of the cue panel and whether the window was maximised come back on the next launch (they
live in `settings.json` together with the global offset).

**Preview audio is not re-encoded when it does not have to be.** The audio-only cache the lanes play
is now built with `-c:a copy` whenever the source audio is AAC (phones, cameras, screen recorders,
most MP4/MOV files), so what you hear is bit-for-bit the source; other codecs are transcoded at
320 kbps instead of 192 kbps. The cache key was bumped, so old quieter-quality files are rebuilt
automatically. A lane whose file Media Foundation refuses is switched off after a few failed
re-syncs instead of being re-seeked on every tick — that retry loop used to be audible as a
stuttering, robotic loop.

In the main window a click outside a text field drops keyboard focus, so the transport and editing
shortcuts work again without having to focus another field first.

Both animation creators have a **per character / word / whole cue** switch next to the
per-character toggle, so a sine wave can travel through the letters, move whole words out of
phase, or animate the entire line as one block.

Three buttons in the toolbar open one creator per kind — **Text style…**, **Idle anim…** and
**Pop-in…**. Each one lists the saved profiles of that kind with `New`, `Delete` and
`Set as default`, edits every field with numeric inputs and colour/gradient pickers, and shows a
live preview that loops the animation so you can judge it before saving. The preview has its own
**preview size** field, and it is a *scale*, not a font-size override: the overlay multiplies the
font size and every effect width/offset/gap by it, so the preview keeps exactly the proportions of
the finished video and an outline that looks right in the editor looks right on the video. While
you have not touched the field it follows the profile's own font size.

A **multi-selection** in the cue list can be edited as one: every field in the cue editor — text,
start, end, style, idle, pop-in, X, Y and scale — shows a dash `—` while the selected cues
disagree, and whatever you then choose or type is written to all of them. Setting the start while
several cues are selected keeps each cue's own length. While the video plays, the cue list follows
the spoken cue so the editor always shows the line that is on screen.

Profiles live in `%AppData%\SubtitleForge\profiles` (`styles.json`, `idle.json`,
`popin.json`) and are plain JSON, so they can be shared or hand-edited. A `styles.json` written
before the effect stacks were merged still loads: its `Outlines` and `Shadows` arrays are folded
into the single `Effects` list on read, shadows first so the old look is preserved. Every cue uses the
default profile of its kind unless it overrides one in the Cues view, where each cue has its own
**Style / Idle / Pop-in** pickers (default entries marked `(default …)`) plus **X / Y / Scale**
boxes and `Reset placement` — X and Y are fractions of the frame (`0.5, 0.86` places the cue at
the bottom centre), so a cue can sit anywhere at any size. All of it — profile choices
included — is saved in the `.sfproj` project.

### Projects

Closing the window with unsaved edits asks whether to save the project first.

`.sfproj` files are JSON and carry the per-cue style/idle/pop-in overrides plus the cue
placement. Media is only **referenced** — the video path and any imported
audio path — while microphone takes are exported next to the project as
`<project>-track<n>-clip<m>.wav` clips and referenced by name, so the project file itself
stays a few kilobytes.

### Shortcuts

- `Space` — play / pause
- `,` / `.` — step one frame back / forward (frame rate read from the file)
- `←` / `→` — seek one second; `Shift`+`←` / `Shift`+`→` — seek ten seconds
- `Del` — delete the selected track(s), clip(s) or cue(s)
- `Ctrl+Shift+D` — split every selected clip at the playhead
- `Ctrl+Shift+J` — join the selected neighbouring clips (also in a track's right-click menu)
- `Ctrl+Z` — undo the last edit (move, add, delete, split, join, reorder, rename, recolour,
  record, and every cue edit: add, delete, split, merge, nudge, timing and text changes)
- `Ctrl+Y` (or `Ctrl+Shift+Z`) — redo whatever was undone last; the toolbar has a `Redo` button too
- `Ctrl+Q` — add a cue at the playhead
- `Ctrl+R` — start / stop recording; `Ctrl+T` — add an audio track
- `Ctrl+[` / `Ctrl+]` — set the selected cue's start / end to the playhead
- `Ctrl+-` / `Ctrl++` — move the selected cue(s) by 100 ms and drag the playhead along with it
- `Ctrl+S` — save the project; `Ctrl+Shift+S` — save as…
- double-click a track header — rename it; right-click a track — colour / split / delete

## Requirements

- Windows 11 (or Windows Server 2022+). The current whisper.cpp CPU runtime needs the
  newer C/C++ runtime shipped with these versions.
- **ffmpeg** — `ffmpeg.exe` and `ffprobe.exe`. Either put them on `PATH` or set
  `FfmpegDirectory` in `%AppData%\SubtitleForge\settings.json` (a folder or a full path).
- **.NET 8 Desktop Runtime** (the build is framework-dependent).
- Optional: **NVIDIA driver + CUDA 12 toolkit (>= 12.4.1)** *and* the Whisper.net CUDA 12 runtime
  next to the executable (see Build → GPU transcription without a CUDA build) for GPU
  transcription. Without all three the app simply runs on the CPU.

## Typical workflow

1. `Open video…`
2. Pick a model — `small` is a good default, `large-v3-turbo` is the best quality/speed
   trade-off on a GPU, `base` is for quick rough drafts.
3. Pick the language (or leave `auto (detect)`), press `Transcribe`.
4. Fix the cues: click a cue, edit the text, use `= playhead` for exact starts/ends,
   `-100 ms` / `+100 ms` to re-sync a block, `Split` / `Merge ↓` to fix boundaries.
5. `Export…` — pick a subtitle format, or `As MP4…` for video/audio with the subtitles
   burned in or written alongside. An MP4 without video is a solid-colour backdrop with the
   subtitles burned in, so it has its own **frame rate** choice (24 / 25 / 30 / 50 / 60 fps).

First transcription with a chosen model also downloads it to
`%LocalAppData%\SubtitleForge\models` (base ≈ 142 MB, small ≈ 466 MB, medium ≈ 1.5 GB,
large-v3-turbo ≈ 1.6 GB). Progress is shown in the status bar.

## Where things live

- Settings: `%AppData%\SubtitleForge\settings.json`
- Models: `%LocalAppData%\SubtitleForge\models`
- Crash reports: `%LocalAppData%\SubtitleForge\crash.log` — every entry starts with the
  build sha, which is also shown in the window title.
- Burn-in diagnostics: `%LocalAppData%\SubtitleForge\burn-in-command.txt`,
  `burn-in-diagnostics.txt` and `burn-in-first-frame.png` — the exact ffmpeg command, the frame
  size, and the first frame that had a cue in it, so a burn-in that comes out empty can be
  inspected instead of guessed at.

## Build

Nothing is built locally; GitHub Actions is the build authority
(`.github/workflows/build.yml`): `windows-latest`, .NET 8, `dotnet publish -r win-x64
--self-contained false -p:EnableCuda=false`. One artifact:

- `SubtitleForge-win-x64` — CPU whisper natives only (~3.3 MB zipped). Works everywhere.

```
dotnet publish SubtitleForge/SubtitleForge.csproj -c Release -r win-x64 --self-contained false -p:EnableCuda=false -o publish
```

### GPU transcription without a CUDA build

The CUDA whisper natives are a ~560 MB package and the CUDA toolkit cannot stand in for them, so
they are not shipped. A CPU build turns into a GPU build by dropping the package's native folder
next to `SubtitleForge.exe`:

1. Download `https://www.nuget.org/api/v2/package/Whisper.net.Runtime.Cuda12.Windows/1.9.1`
   (it is a plain zip).
2. Extract its `runtimes` folder so the layout is
   `<app>\runtimes\cuda12\win-x64\ggml-cuda-whisper.dll`.
3. Install the NVIDIA driver and a CUDA 12 toolkit (>= 12.4.1).
4. Restart the app. Ticking `Use GPU` now works; the checkbox reports what is missing instead of
   silently falling back when one of the three pieces is absent.

### Why did it fall back to the CPU?

`GPU diagnostics…` next to the GPU checkbox runs the whole chain and shows the result: the driver
(`nvcuda.dll` plus its version), the toolkit (`nvcc.exe`, `CUDA_PATH`, installed versions), the
libraries whisper.cpp links against (`cublas64_12`, `cublasLt64_12`, `cudart64_12`, `cudnn64_9`) with
the path each one was found at or "not found anywhere", the whisper CUDA natives next to the exe,
and finally a real attempt at opening the selected model through CUDA — the only check that cannot
lie. The report is also written to `%LocalAppData%\SubtitleForge\gpu-diagnostics.txt`.

Every transcription that asks for the GPU and does not get it now leaves a line in
`crash.log` starting with `GPU requested but not used:` or `GPU requested but CUDA failed to
initialise:`, and the status bar shows the same reason while it runs.

Building the CUDA variant from source instead is a `-p:EnableCuda=true` publish, which pulls the
same package as a `PackageReference`.

## Known limitations

- The preview uses WPF `MediaElement` (Media Foundation), so exotic codecs may not
  preview. Transcription and burn-in go through ffmpeg and work regardless.
- Whisper reads the whole extracted 16 kHz mono track into memory: roughly 230 MB of
  samples per hour of audio.
- Burn-in (and any export that is not a plain remux) re-encodes the video, so it is
  CPU/GPU-bound and much slower than exporting a sidecar `.srt`. The subtitle frames are drawn
  with the preview's own painter into a `DrawingVisual` rather than a laid-out control, because a
  detached WPF visual has no `RenderSize` and silently drew nothing.
- Long exports are cancellable from either the toolbar `Cancel` or the button next to the export in
  the background-task list; a partial file is deleted. The finished export plays the system
  notification sound and opens the folder with the file selected.
- Cue splitting is proportional to text length; it does not do word-level forced
  alignment, so a very long single Whisper segment may still need manual `Split`.

## Licence

MIT.
