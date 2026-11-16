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
- **Re-transcribe just one span.** Set `Transcribe range` in the timeline toolbar — type the
  times (the same `00:01:20.000` or plain-seconds syntax the cue editor takes), or park the
  playhead and press `Set in` / `Set out`, or take the span of the cues selected
  in the list with `From selection`. The span is drawn on the ruler, and a click on `Transcribe`
  then sends **only that audio** to Whisper: the cues inside the span are erased and replaced
  with the new transcription, while everything outside it is left exactly as it was. A cue that
  only pokes into the span is trimmed instead of thrown away, and one the span cuts in half
  becomes two, so nothing spoken outside the range is lost. The whole replacement is one undo
  step, and `Whole video` clears the range again.
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
- **Seeking selects the cues under the playhead.** Clicking or dragging either timeline — the tracks view
or the seek strip under the preview — and any other seek
selects every cue that is on screen at the new position, so the cue panel always edits exactly what the
preview draws — several cues when they overlap. Untick `Select cues at playhead` in the timeline toolbar
to keep the selection untouched while scrubbing.

**Clicking or scrubbing the timeline pauses playback first**, then moves the playhead, so
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
- **Merge tracks down / up** (`Ctrl+M` / `Ctrl+Shift+M`, or the track right-click menu) squeezes
  several selected tracks of the same kind onto one row without moving anything in time. Rows are
  taken in order — top to bottom for a downward merge, bottom to top for an upward one — and each
  clip is laid over what is already on the row: a clip that lands inside a longer one **splits it in
  two**, a clip half over it **trims** it, a clip on top of it **replaces** it. Whichever row is
  handled last is the one whose clips survive, so the direction is the choice of who wins. Source
  offsets are carried over to the cut pieces, so the picture and the sound do not shift.
- **Join clips** accepts the two halves of a split video clip as well: they keep their file in
  `ClipFile`, and until this was checked a video pair looked like two unrelated sources and the
  command stayed greyed out. When the halves sit on different rows, join says so — merge the rows
  first (`Ctrl+M`) and the two halves become joinable.
- **Drag the ruler or an empty lane** to scrub the playhead; drag a lane's **name** to
  reorder whole tracks. `Shift`+drag on a clip scrubs instead of moving it.
- **The ruler stays put.** However many tracks are stacked up, the ruler is painted again at the
  scroll offset as the tracks view scrolls, so the time scale, the cue marks, the marked
  transcribe range and the playhead are always in sight; the playhead is drawn over the ruler so
  the position stays readable while scrolled down.
- **`Alt`+wheel zooms the timeline in and out horizontally**, anchored at the pointer, so the second
  under the cursor stays under it. Zoom 1 is the whole video; the more you zoom, the fewer seconds fit
  and the finer the ruler steps become (0.5 s ticks instead of whole minutes) — that is how a clip edge
  gets placed to the tenth of a second. A horizontal scrollbar appears under the tracks while zoomed,
  **`Shift`+wheel** scrolls along the timeline without touching the row list, and while playing the view
  follows the playhead once it would leave the screen. Only a vertical scrollbar shows at zoom 1, so the
  bar never costs a row.
- **`Ctrl`+wheel changes the row height**: scrolling down makes the tracks shorter and fits more of them
  in the panel, scrolling up gives the waveforms and thumbnails more room. `Fit whole video` in the
  track right-click menu puts both back.
- **Drag the bar between the preview and the tracks** to make the video smaller and give the
  timeline more room (or the other way round).
- **Seeking repaints the preview** whether playback is running or paused — the frame under
  the playhead is shown for both video lanes and a black frame for gaps.
- Subtitles are built from **all** audio clips on the timeline. Each clip is cut into
  independent **150-second windows** (with a half-second overlap that is trimmed afterwards)
  before it reaches Whisper, because a single pass over many minutes of audio is where
  Whisper's timing drifts and starts looping; one model load covers all the windows.
- **Whisper is asked for token timestamps**, so it reports the time each word was spoken. Every
  word becomes its own cue with that exact timing, and the cue you see is a *group* of those
  words: neighbouring words are glued together while the result still fits **Max chars** and
  **Max sec**, no pause longer than 0.35 s comes between them and the previous line did not end
  on a full stop. That is what makes "two to four words per subtitle" track fast speech — the
  timings come from the words, not from dividing a phrase's duration by character count.
- **Parts** in the transcription toolbar decides how many of those windows are decoded at the
  same time (1–4). Each parallel part owns its own Whisper decoder — the model weights are
  shared, the decoder state is not — and the thread budget is divided between them, so four
  parts on four threads each is not four times faster: whisper already uses every thread it is
  given inside a single decode. It helps when the decode is bound by memory rather than
  arithmetic. CUDA always decodes one part at a time, because splitting one device between
  decoders buys nothing and needs a slice of VRAM for each. Cues from parallel windows are
  stitched back together in timeline order.
  **Max chars** is the character budget of one cue (spaces included) and **Max sec** the longest
  a cue may stay up; a cue is also cut when only one of the two is exceeded. A cue that is still
  shorter than the minimum on-screen time is held a little longer, but only into the silence that
  follows it, so it never pushes the next cue late.
  Only when whisper returns no token timings at all does the old fallback apply: the whole segment
  becomes one cue and its text is split by hand, with the pieces distributed inside the segment's
  own time range so a run of small cues cannot drift past the end of the phrase it came from.
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
- The **ruler paints cue marks** along its bottom edge — the same opaque blue bars the seek strip
  under the preview draws, accent blue for the selected cues. Proxy progress is reported in the
  background-task popup instead of on the ruler.
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
- **Pop-out animation** — what happens when the line leaves: `Fade out`, `Move out` (slide off in
  any direction), `Scale out` (shrink away, blow up, with jelly wobble), `Spin out` around X, Y or
  Z, `Dissolve` (the glyph breaks into noise-eaten pieces), or `Turn to dust` (the pieces fly off
  with a wind: direction, force, spread, piece size, piece count and piece spin). Every one of them
  can stagger per character, per word, or play on the whole cue, and `Origin` decides where a
  dissolve or a crumble starts — the same directions the gradient fills use, plus `From the centre`
  and `No order (noise only)`. The noise is a seeded lattice noise written for this, so the same
  `Seed` gives the identical pattern in the preview and in the exported video, frame for frame.
  The exit is anchored to the **end of the cue**: the first group starts before it and the last
  piece finishes exactly on it, and a stagger too long for the cue is compressed instead of
  hanging over the cut.

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
right to change the value (`Shift` = fine, `Ctrl` = coarse) or click once to type it. A whole drag is
one undo step — the history records the value from before the button went down, not one entry per
frame — and `Esc` in any text or value field gives up focus so the single-key shortcuts work again.

Every creator's live preview has a **Preview text** field of its own, so a long line, an awkward
letter pair or a word with descenders can be checked before saving; the text lives with the preview
and never touches the profile. Leave it empty and the preview falls back to `Sample Text`.

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

**No global time offset.** The offset field is gone: cue times are the times that play and export, so
nothing can silently shift the whole subtitle track. Shifting a whole track is done by selecting every
cue (`Ctrl+A` in the list) and nudging it with `Ctrl+-` / `Ctrl++`, or by moving single edges with
`Ctrl+Alt+-` / `Ctrl+Alt+Shift+-`. Any value an older settings file still carries is forced back to
zero when it is loaded.

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

All three animation creators have a **per character / word / whole cue** switch next to the
per-character toggle, so a sine wave can travel through the letters, move whole words out of
phase, or animate the entire line as one block.

Four buttons in the toolbar open one creator per kind — **Text style…**, **Idle anim…**,
**Pop-in…** and **Pop-out…**. Each one lists the saved profiles of that kind with `New`, `Delete` and
`Set as default`, edits every field with numeric inputs and colour/gradient pickers, and shows a
live preview that loops the animation so you can judge it before saving. The preview has its own
**preview size** field, and it is a *scale*, not a font-size override: the overlay multiplies the
font size and every effect width/offset/gap by it, so the preview keeps exactly the proportions of
the finished video and an outline that looks right in the editor looks right on the video. While
you have not touched the field it follows the profile's own font size.

A **multi-selection** in the cue list can be edited as one: every field in the cue editor — text,
start, end, style, idle, pop-in, pop-out, X, Y and scale — shows a dash `—` while the selected cues
disagree, and whatever you then choose or type is written to all of them. Setting the start while
several cues are selected keeps each cue's own length. While the video plays, the cue list follows
the spoken cue so the editor always shows the line that is on screen.

Profiles live in `%AppData%\SubtitleForge\profiles` (`styles.json`, `idle.json`,
`popin.json`, `popout.json`) and are plain JSON, so they can be shared or hand-edited. A `styles.json` written
before the effect stacks were merged still loads: its `Outlines` and `Shadows` arrays are folded
into the single `Effects` list on read, shadows first so the old look is preserved. Every cue uses the
default profile of its kind unless it overrides one in the Cues view, where each cue has its own
**Style / Idle / Pop-in / Pop-out** pickers (default entries marked `(default …)`) plus **X / Y / Scale**
boxes and `Reset placement` — X and Y are fractions of the frame (`0.5, 0.86` places the cue at
the bottom centre), so a cue can sit anywhere at any size. All of it — profile choices
included — is saved in the `.sfproj` project.

### Projects

Closing the window with unsaved edits asks whether to save the project first.

`.sfproj` files are JSON and carry the per-cue style/idle/pop-in/pop-out overrides plus the cue
placement. Media is only **referenced** — the video path and any imported
audio path — while microphone takes are exported next to the project as
`<project>-track<n>-clip<m>.wav` clips and referenced by name, so the project file itself
stays a few kilobytes.

### Shortcuts

- `Space` — play / pause
- `,` / `.` — step one frame back / forward (frame rate read from the file)
- `←` / `→` — seek one second; `Shift`+`←` / `Shift`+`→` — seek ten seconds
- `Del` — deletes from the view touched last: cue(s) when the cue list was last used, track(s) or
  clip(s) when the timeline was
- `Ctrl+Shift+D` — split every selected clip at the playhead
- `Ctrl+Shift+J` — join the selected neighbouring clips (also in a track's right-click menu)
- `Ctrl+M` / `Ctrl+Shift+M` — merge the selected tracks down / up onto one row
- `Ctrl+Z` — undo the last edit (move, add, delete, split, join, reorder, rename, recolour,
  record, and every cue edit: add, delete, split, merge, nudge, timing and text changes)
- `Ctrl+Y` (or `Ctrl+Shift+Z`) — redo whatever was undone last; the toolbar has a `Redo` button too
- `Ctrl+Q` — add a cue at the playhead
- `Ctrl+R` — start / stop recording; `Ctrl+T` — add an audio track
- `Ctrl+[` / `Ctrl+]` — set the selected cue's start / end to the playhead
- `Ctrl+-` / `Ctrl++` — move the selected cue(s) by 100 ms and drag the playhead along with it
- `Ctrl+Alt+-` / `Ctrl+Alt++` — move only the start of the selected cue(s) by 100 ms
- `Ctrl+Alt+Shift+-` / `Ctrl+Alt+Shift++` — move only the end of the selected cue(s) by 100 ms
- `[` / `]` — jump to the previous / next cue, selecting it and putting the playhead on its start
- `Esc` — while a text or value field has focus, leaves the field and hands focus to the window, so the
  single-key shortcuts work again (`Esc` in a numeric field first puts its previous value back)
- `Ctrl+S` — save the project; `Ctrl+Shift+S` — save as…
- double-click a track header — rename it; right-click a track — colour / split / delete

## Requirements

- Windows 11 (or Windows Server 2022+). The current whisper.cpp CPU runtime needs the
  newer C/C++ runtime shipped with these versions.
- **ffmpeg** — `ffmpeg.exe` and `ffprobe.exe`. Either put them on `PATH` or set
  `FfmpegDirectory` in `%AppData%\SubtitleForge\settings.json` (a folder or a full path).
- **.NET 8 Desktop Runtime** (the build is framework-dependent).
- Optional: **NVIDIA driver + CUDA 12 toolkit (>= 12.4.1)** for GPU transcription. The CUDA whisper
  natives ship with the build (`runtimes\cuda12\win-<arch>`), so nothing else has to be installed.
  Without a driver and the toolkit the app simply runs on the CPU.

## Typical workflow

1. `Open video…` — or, while no video is loaded, drag one onto the window: a dropped file goes
   through the very same extension filter and the very same load path as the button.
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
--self-contained false -p:EnableCuda=true`. One artifact:

- `SubtitleForge-win-x64` — the CPU whisper natives in `runtimes\win-<arch>` plus the small CUDA 12
  natives in `runtimes\cuda12\win-<arch>`, so GPU transcription works on a machine with a driver and
  the toolkit, and a machine without one still has the CPU fallback to walk on to.

The CUDA natives themselves are **not** shipped — `ggml-cuda-whisper.dll` alone is 538 MB. The
`runtimes\cuda12\win-<arch>` folder stays in the artifact, empty except for a
`PUT-CUDA-LIBRARIES-HERE.txt` that says exactly which files to copy in. An empty folder is what makes
whisper.net walk on to the CPU runtime instead of pretending it has CUDA, and dropping the files in
later turns GPU transcription on without a rebuild.

```
dotnet publish SubtitleForge/SubtitleForge.csproj -c Release -r win-x64 --self-contained false -p:EnableCuda=true -o publish
```

CI fails the build when `ggml-cuda-whisper.dll` is missing, or when it appears anywhere other than
`runtimes\cuda12\win-<arch>`: that folder is the only one whisper.net probes, and a native library in
the wrong place is skipped without a word.

### GPU transcription

Three things are needed, in this order:

1. An **NVIDIA driver** — `nvcuda.dll` in `System32`.
2. A **CUDA 12 toolkit (>= 12.4.1)** — `cudart64_12`, `cublas64_12`, `cublasLt64_12`.
3. The **CUDA whisper natives**. The build leaves the folder `runtimes\cuda12\win-<arch>` in place but
   empty (they weigh 538 MB), so copy the whole `runtimes\cuda12\win-x64` folder out of the package
   `Whisper.net.Runtime.Cuda12.Windows` 1.9.1 into it — five files:
   `ggml-cuda-whisper.dll`, `ggml-whisper.dll`, `ggml-base-whisper.dll`, `ggml-cpu-whisper.dll`,
   `whisper.dll`. The `cublas64_12` / `cudart64_12` libraries come from the toolkit and do not have to
   be copied anywhere.

Nothing has to be moved or copied by hand. whisper.net walks its runtime list and loads the first one it
can, so the CPU natives in `runtimes\win-<arch>` stay as the fallback for machines that fail check 1 or 2.

**The CUDA module has to be registered with ggml.** whisper.net opens
`runtimes\cuda12\win-x64\ggml-cuda-whisper.dll` by absolute path, which is enough for the loader to
report CUDA and for the file to sit in memory — but ggml's own backend registry only scans the
**executable folder and the current directory** for `ggml-<backend>-*.dll`. The natives live in a
subfolder, so the registry stayed CPU-only and whisper.cpp answered `whisper_backend_init_gpu: no GPU
found` while every file was present and the driver was healthy. At start-up the app therefore points
`GGML_BACKEND_PATH` at that module, which is ggml's documented way of registering an out-of-tree
backend. Nothing to configure: the first CUDA module found in `runtimes\cuda\win-x64` or
`runtimes\cuda12\win-x64` is used, and a module placed next to the exe is found by ggml on its own.

That variable is ignored by whisper builds older than the ggml that introduced it, so the module is
**also hard-linked next to `SubtitleForge.exe`** at start-up — a hard link costs no disk space, needs no
administrator rights inside the app's own folder and vanishes with the file it points at. As a last
resort the current directory is pointed at the natives folder for the moment the model is opened,
which is when ggml scans for backends. GPU diagnostics prints all three: the variable, the link and
the folder, and then reads whisper's own verdict, so a failure always names the step that did not
work.

**The backend module's file name is part of the interface.** The ggml inside whisper.net 1.9.1 does
not ask for a fixed symbol. It takes the module's own file name, strips `ggml-` and `.dll`, and calls
`ggml_backend_<that name>_reg`. whisper.net ships its natives with a `-whisper` suffix so they cannot
collide with other ggml builds in the same folder, which turns that lookup into
`ggml_backend_cuda-whisper_reg` — a symbol nobody exports. The backend is then dropped without a word:
`backends = 1` in whisper's own log, one second later `whisper_backend_init_gpu: no GPU found`. The
module really exports `ggml_backend_cuda_reg`, so at start-up the app hard-links it as **`ggml-cuda.dll`**
next to the exe and inside the CUDA natives folder, and keeps the suffixed link as well for builds that
scan for `ggml-<backend>-*`. GPU diagnostics prints, for every candidate name, the entry point ggml
would ask for and whether the module has it — that line is what tells a naming problem from a load
problem.

Two more things are reported because they are the ones with no trace in any log:

- **Does the CUDA set belong to the same release as the CPU set?** A CUDA
  `ggml-cuda-whisper.dll` from another whisper.net version is rejected by ggml on an API-version
  check, and a release build does not log the rejection — whisper simply says `no GPU found`. Both
  sets share `ggml-base-whisper.dll`, which does not depend on CUDA, so in a matching install the two
  copies are byte for byte identical. The report compares their SHA-256 and says outright when the
  folder came from another release.
- **Can the module be loaded at all?** It is loaded by absolute path and asked for
  `ggml_backend_cuda_reg`, `ggml_backend_score` and `ggml_backend_init` — a Win32 error there names
  the missing dependency (126), a version clash (127) or a 32/64-bit mismatch (193), none of which
  whisper's own log distinguishes.

The CUDA runtime libraries (`cudart64_12`, `cublas64_12`, `cublasLt64_12`) are **preloaded by absolute
path from the newest CUDA 12 toolkit** before anything asks for them by name. A machine with several
toolkits installed (v12.0 and v12.4 here) otherwise mixes them, and a `cublas64_12` that does not
export what `ggml-cuda` expects makes the backend module fail to load.

**Which card is used.** CUDA only enumerates NVIDIA adapters, so an integrated AMD or Intel GPU is not a
CUDA device and never takes index 0: a single NVIDIA card — on the PCIe bus, in an M.2 slot over Oculink,
or on a riser — *is* device 0 and needs no configuration. On a machine with more than one NVIDIA adapter
the first one is used unless `CudaDeviceIndex` in `settings.json` says otherwise; the app then sets
`CUDA_VISIBLE_DEVICES` and `CUDA_DEVICE_ORDER=PCI_BUS_ID` before the first CUDA call, so the number in
the picker means the number nvidia-smi prints. The **GPU** combo in the toolbar lists what nvidia-smi
reports and stores the choice; CUDA reads it once, when the first model is opened, so a change needs a
restart if a model was already loaded. GPU diagnostics lists every device with its memory and driver and
marks the one that will be used.

Two gates decide whether the CUDA runtime is tried at all, and both used to fail silently:

- The natives must be in `runtimes\cuda12\win-<arch>`. That is the only folder the loader probes; a
  `ggml-cuda-whisper.dll` loose in the app folder is never seen. The build puts the files there and CI
  fails if they end up anywhere else.
- `cudart64_12.dll` must be loadable **by name**, which searches the app folder, `System32` and `PATH`.
  A toolkit installed under `C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.x\bin` is on none
  of them, so the app adds that folder to the DLL search path at start-up (`SetDllDirectory`) without
  touching your environment variables.

`Use GPU` fails loudly instead of falling back in silence: the checkbox unticks itself and says which of
the three pieces is missing, `GPU diagnostics…` writes the whole chain to
`%LocalAppData%\SubtitleForge\gpu-diagnostics.txt`, and a transcription that asked for the GPU and did
not get it leaves a `GPU requested but not used:` line in `crash.log`.

### Why did it fall back to the CPU?

`GPU diagnostics…` next to the GPU checkbox runs the whole chain and shows the result: the driver
(`nvcuda.dll` plus its version), the toolkit (`nvcc.exe`, `CUDA_PATH`, installed versions), the
libraries whisper.cpp links against (`cublas64_12`, `cublasLt64_12`, `cudart64_12`, `cudnn64_9`) with
the path each one was found at or "not found anywhere", the whisper CUDA natives next to the exe,
and finally a real attempt at opening the selected model through CUDA. The report is also written to
`%LocalAppData%\SubtitleForge\gpu-diagnostics.txt`.

Opening the model is still only half of it: a CUDA build of `whisper.dll` loads perfectly well and then
runs the CPU backend when its `ggml-cuda-whisper.dll` companion is missing, mismatched or refuses to
initialise — the loader has no reason to complain, the app just gets slow. So the last section of the
report **decodes one second of synthetic silence** and reads whisper.cpp's own log for the backend it
initialised (`using CUDA0 backend` versus `no GPU found`), then asks `nvidia-smi` whether this process
holds any device memory. Those two are the only witnesses that cannot be fooled, and the same
verification runs after every transcription: the status bar reports
`GPU — CUDA 12 · CUDA0 · NVIDIA GeForce RTX 3060 · 1.8 GB on the device`, or says outright that the
CUDA build loaded but the CPU backend ran, and leaves a matching line in `crash.log`.

If it reports CUDA and the GPU still looks idle, look at the right graph: Windows Task Manager shows
CUDA work under the **Cuda** (or `Compute_0`) engine of the GPU tab, not under **3D**, and a modern card
finishes a 150-second window in well under a second, so the average sits near zero even while the work
is happening. `nvidia-smi dmon` and `nvidia-smi --query-compute-apps=pid,used_gpu_memory` show the
truth.

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
