# Phase 11: motion templates (Lottie and After Effects)

Churches design their lower thirds, scripture bars and title cards in After Effects, or buy
them from motion designers. Until now the only way to use one was to render it to a video, which
can't be see-through, can't be re-typed, and looks soft on a big screen. This part of Phase 11
plays those animations **live**, over the slides, **with their transparency**, and lets the
operator **fill them in**: the speaker's name, the verse, the church's colours.

The animations are **Lottie** files: the JSON that After Effects' Bodymovin plug-in exports (and
LottieFiles' `.lottie` packages). Lottie is a description of shapes, text, pictures and keyframes,
not pixels, so the app can draw any frame at any size and change the words before drawing. The
drawing is done by **Skottie**, the Lottie player inside Google's Skia graphics library (the one
Chrome and Android use), through the `SkiaSharp` and `SkiaSharp.Skottie` NuGet packages (MIT).

For After Effects projects that aren't Lottie-friendly, there's a **bridge**: the app has After
Effects itself render the composition (with new words and colours put in first) and imports the
video.

The Mac has no Lottie support, so all of this is Windows-only code, and nothing new goes into
the documents the Mac reads (section 5).

## 1. Using it

**Overlays › Motion templates…** opens the window.

- **The gallery** in the middle: every template, starters first. Point at one and it plays in its
  tile; click it to pick it. **Import Lottie…** (or drop `.json` / `.lottie` files on the window)
  adds your own. Right-click one of yours to rename or remove it.
- **On the right**, the picked template plays through (In, Hold, Out) over a stand-in camera shot,
  and loops. Below it:
  - **Fields**: one box per text layer (named after the layer: a layer called "Speaker Name" is the
    field "Speaker Name"), a colour per colour the design uses, and a **Choose…** per picture.
    The preview follows as you type.
  - **Plays**: *In, then hold until taken down* (the Hold part loops if it moves), *In, then stay on
    the end of the Hold*, or *In, Hold, Out once, then take itself down*. A template without
    markers loops, holds its last frame, or plays once.
  - **Use as**: a *lower third over the slides* (it goes in the Overlays bin) or a *full-screen
    background* (the Media bin).
  - **Add to library**, **Add and go live**, and **Export MP4…**.

Once added, a lower third is a tile in the Overlays bin's **Motion Templates** pack. Click it to
put it up and click it again (or F4) to take it down: it plays its Out. Its fields are beside the
bin, as an ordinary overlay's are, so **typing a new name while it's up changes it on the screen at
once**. **Edit motion template…** under its fields opens the window on that use, for its colours,
pictures and playback. A background is a tile in the Media bin; its right-click menu has *Edit
motion template…*. Each use keeps its own values: two speakers are two uses of one template.

**Export MP4…** renders the template, as filled in, to an H.264 video: 720p, 1080p or 4K at 30 or
60 fps, over black or a key colour (H.264 has no transparency), holding for as long as you say
between its In and its Out. It goes into the Media bin (as a looping background, or a video that
plays once), and/or is saved as a file for a vision mixer.

**After Effects…** opens the bridge (section 6). **How to make one…** is the short guide to
exporting Lottie from After Effects so it plays live (the same guide is in the bridge's window).

## 2. Reading a Lottie file: `Core/MotionTemplates`

A Lottie file is one JSON object: `w` and `h` (picture size), `fr` (frames a second), `ip` and `op`
(first frame and the frame after the last), `layers`, `assets` (pictures and precompositions,
which hold layers of their own), `fonts` and `markers`. `LottieDocument` reads it into a
`JsonNode` tree, which can be looked into and changed, and checks it's usable (a size, a frame
rate, some frames, no more than 64 MB), saying why not in words an operator can act on.

`LottieFiles.Read` turns what's on disk into one self-contained animation:

- a `.json` whose pictures sit in an `images` folder beside it has them put **inside** it, as
  `data:` URIs, so the library keeps one piece of JSON per template and nothing can go missing;
- a `.lottie` is a zip (`manifest.json`, the animation in `animations/` (version 1) or `a/`
  (version 2), pictures in `images/` or `i/`). Only those entries are read, straight from the zip,
  with limits on how much they unpack to, and never a path outside the package. That's the same
  care `SafeZip` takes for `.mxuslides` files.

**Fields** (`MotionFields`). Lottie has no list of editable fields (After Effects' Essential
Graphics don't survive the export), so they're worked out from what's in the file:

- **Text**: every text layer, in the main composition or inside a precomposition. Its id is where
  it is ("`text:L0`" is the first layer of the main composition, "`text:A:comp_0/L1`" the second
  layer of the precomposition `comp_0`), so a use's saved values always find their field. After
  Effects ends lines with a carriage return; the field shows ordinary new lines.
- **Colours**: every distinct colour, wherever it's used: shape fills and strokes, gradient stops,
  text fill and outline, solid layers, effects' colour settings, still or keyframed. A colour used
  in five shapes is one field, because designers build from a few brand colours and "the blue"
  should change everywhere (that's how LottieFiles' own colour editor works). The field is named
  after the layers that use it ("Plate colour", "Title & Accent colour"). Its id is the colour it
  was designed in (`color:#0F1F3A`).
- **Pictures**: every image asset, named after the layer that shows it.

`MotionFields.Apply` makes a **changed copy**; the template is never touched. Each colour is
written back in the scale its file used (0–1 in most files, 0–255 in some old ones), keeping each
place's own opacity. Two colours can even swap. A value equal to the default changes nothing, so
the same animation object comes back and nothing is redrawn.

## 3. Which frame, when: `MotionTimeline`

After Effects designers mark the parts of a template with composition markers named **In**,
**Hold** and **Out** (LottieFiles' tools use names like *intro*, *loop* and *outro*; those work too,
and so does a marker comment written as JSON, `{"name":"Out"}`). `MotionTimeline.SegmentsOf` turns
them into three ranges of frames; a marker without a length runs up to the next one.

`FrameAt(secondsUp, secondsDown)` is a **pure function**, like the slide animation engine
(HOW-IT-WORKS section 14): given how long ago the template went up and (if it has) how long ago
it was taken down, it says which frame to draw and whether it has finished.

- While it's up: the In plays, then the Hold, which **loops** (Loop), **stops on its last frame**
  (HoldLast), or plays **once** and is followed by the Out (Once).
- Taken down: it jumps **straight to the Out** from wherever it was, even part-way through the In,
  and has finished when the Out has played.
- Without markers the animation is one piece: it loops, holds its last frame, or plays once, and
  taking it down **fades** it (0.3 s).

Because frames are worked out from the clock, not counted, every screen shows the same frame and
a busy PC just shows fewer of them. A 30 fps animation shown at 60 Hz gets frames in between:
Skottie can draw any moment, not only whole frames.

`SettledFrame` is the frame that stands for the whole template in a still picture: the start of
the Hold (the design fully in), or for one without markers its last or its middle frame.
`PlayThrough` lists the frames for a video file: In, the Hold for so many seconds, then Out.

## 4. Drawing it live: `MxU.Slides.MotionTemplates`

A new project, because it brings in Skia's native library (`libSkiaSharp.dll`, which comes inside
the NuGet package), and Core stays free of anything native.

- **`LottieRenderer`** builds a Skottie animation once (fields already filled in) and draws any
  frame into a **`LottieFrame`**: premultiplied BGRA in pinned memory, which Skia draws straight
  into and WPF's `Pbgra32` reads as it is. The animation is fitted and centred, on a transparent
  background. Skottie counts frames from the in point; the app counts as the file does, so the
  renderer subtracts it.
- **Fonts.** Skia finds installed fonts by family and style on its own. For a font Windows hasn't
  got, `FontEmbedding` looks in the library's `fonts` folder (which holds the fonts the app ships,
  Phase 6) by PostScript name, the name Bodymovin writes ("Montserrat-SemiBold"), then by family
  and style, and puts the file into the animation as a `data:` URI (`fPath`), which Skottie loads.
  So the starters draw in Montserrat on a PC that never installed it.
- **`MotionPlayers`** plays the templates that are on the program. One **thread of its own** draws
  all of them (never .NET's shared pool: HOW-IT-WORKS section 26). It's paced by the screen: each
  time WPF is about to draw a picture, the app asks for the frames of the moment that picture will
  appear (`Request`), the thread draws each template for that moment into a spare picture, and
  swaps it in. The screen takes the newest with `CopyLatest`.
- **Holding still costs nothing.** Skottie keeps track of what each move to a new frame changes
  (its *invalidation controller*). A lower third holding still reports no change, so no picture is
  drawn and nothing is copied until it's taken down.
- **Only what changed is copied.** The same tracking says *where* the picture changed. Each new
  picture remembers that rectangle, and the screen copies only the part that changed since the
  picture it already has (`ChangedSince`): a lower third sweeping in touches a strip along the
  bottom, not 8 MB of 1080p. WPF then only sends that part to the graphics card.

**On the screens** (`Rendering/MediaHub.Motion.cs`). A playing template is a *picture id*,
`motion::overlay:<overlay id>`, drawn exactly like a live camera (HOW-IT-WORKS section 22): one
`WriteableBitmap` per template, updated in place on each refresh, which every view showing it
draws. So the projector, the operator's preview, the stage and the recording all show the same
frame, drawn once.

**Going up** (`ViewModels/MainViewModel.MotionTemplates.cs`). Every way of putting an overlay up
goes through `SetOverlay`, which now asks `ForShow`. For a motion template that's a **copy** of
the overlay, never saved, whose picture is the live animation instead of the still, with its steps
swapped: no In (the animation brings itself in) and an **Out that holds** for exactly as long as the
template's Out and then lets go. The show takes an overlay down only once its Outs have played
(`ShowState.SweepFinishedExits`), so that's what keeps the overlay on the screen while its Out
plays. Nothing in `ShowState` had to change.

**Coming down, any way at all**: its tile, F4, Clear All, a slide action, the Local API, the
scheduler. Rather than catch each, `SyncMotion` listens to `ShowState.Changed`: an overlay that's
leaving tells its animation to play its Out; one that has gone stops it. A template set to play
once takes itself down when it has played through.

**Full-screen templates** are videos in the library (what a Mac plays, section 5). When one is put
up as a background, `SyncMotion` starts its animation and tells the media hub that the video
**stands in** for it (`MediaHub.StandIn`): wherever a view would play that video, it draws the live
animation. Clearing the background stops it. Fields typed into a live background change it live
too.

## 5. Staying friends with the Mac

The shared format can't learn a new kind of thing: the Mac refuses a whole document that names a
kind or a field it doesn't know (HOW-IT-WORKS section 41). So:

- The **catalogue** (one entry per imported template: name, file, size, length, what it's for) and
  the **animations** themselves (by their SHA-256) are **Windows extras**:
  `Windows extras/motion templates/<id>.json` and `…/motion template animations/<hash>.json`.
- A **use** is an ordinary shared document with its Windows part beside it
  (`Windows extras/motion template uses/<document id>.json`: the template, the values, the
  playback):
  - **A lower third** is an `Overlay` in the *Motion Templates* pack holding **one picture**: a PNG
    **with alpha** of the design at its settled frame (in the media folder *Motion
    Templates/Stills*), where the animation plays, fading in over the template's In and out over
    its Out. A Mac shows a still lower third that arrives and leaves gently; Windows plays the
    animation in its place.
  - **A full-screen template** is a `MediaItem`: an H.264 MP4 rendered from it (the In, a 10-second
    hold and the Out; or one pass of a loop), set to loop. A Mac plays the video; Windows plays the
    animation live, sharp at any screen size and with its fields.
- Each use's extra is **fingerprinted** by its document (`MotionTemplateStore.Basis`): the
  overlay's objects, its build order and the files of its pictures; or the video's file. If a Mac
  moves the still, retimes it, swaps its picture or replaces the video, the fingerprint no longer
  matches and Windows shows the document **as the Mac left it**. The Mac's edit wins.
- When the values change, the still (or video) is made again a moment after the typing stops, into
  the **same** media item (`MediaRelink.ReplaceFile`), so the overlay itself doesn't change, and the
  use is re-saved with the new fingerprint. `FallbackStamp` records what the fallback was made
  from, so it's only remade when it would look different.

**Why a still for a lower third, and a video for a background?** A lower third has to be
see-through, and H.264 can't be: a video of a lower third would cover the slide with black (or
green). A PNG can, and a lower third holding its settled design is exactly what an operator wants
from it on a machine that can't animate it. A full-screen background covers the screen anyway, so
the video loses nothing, and a still background would look broken. The unit tests check the overlay
file has only fields the generated models declare (so the Mac's strict reader takes it) and
reads back through them.

## 6. The After Effects bridge: `MotionTemplates/AfterEffects`

For compositions that don't export well to Lottie (particles, 3-D, third-party effects), After
Effects can render them itself:

- **Finding it** (`AfterEffectsInstall`): `Program Files\Adobe\Adobe After Effects <version>\Support
  Files\aerender.exe` (and `AfterFX.exe` beside it), newest first. **Locate aerender.exe…** takes
  one elsewhere and remembers it in this PC's `settings.json`.
- **Rendering** (`Aerender`): Adobe's command-line renderer, `aerender -project … -comp "Name Strap"
  -output … -RStemplate "Best Settings" -OMtemplate "H.264 - Match Render Settings - 15 Mbps" -v
  ERRORS_AND_PROGRESS -close DO_NOT_SAVE_CHANGES`. Each argument is passed separately
  (`ProcessStartInfo.ArgumentList`), so Windows quotes names with spaces. aerender writes a line per
  frame (`PROGRESS:  0:00:01:14 (45): 2 Seconds`); `AerenderParser` reads the frames done from the
  number in brackets and the total from the duration and frame rate (timecodes with `;` for
  drop-frame rates, or plain frame counts when a project counts in frames), and the errors from
  lines marked ERROR. Its output is read on two threads of its own. **Cancel** kills aerender and the
  After Effects it started (the whole process tree).
- **Templates** (`ExtendScripts`, `AfterEffectsBridge`): to change words and colours, After Effects
  runs a script (`AfterFX.exe -r script.jsx`). The *list* script opens the project and reports its
  compositions, their text layers and the colours that can be changed (shape fills and strokes,
  effect colours, text fills). The *apply* script changes them and saves. The job goes into the
  script's first line as `var MXU_JOB = {…};`: JSON with every non-ASCII character written
  `\uXXXX`, so it's a valid JavaScript literal whatever encoding After Effects reads the file in.
  (ExtendScript is a 1999 JavaScript: no `JSON` object, so the script has a small JSON writer of its
  own.) The script writes its answer to a JSON file, which the bridge waits for, because `AfterFX.exe`
  may hand the script to a running After Effects and return at once.
- **Your .aep is never changed.** The bridge copies it into a work folder in the library's spool
  first, and After Effects only ever opens and saves the copy. A project remembers its footage by
  full path, so the copy still finds it. The script refuses to run if After Effects has unsaved
  changes of yours open, rather than risk them. The work folder is deleted afterwards, whatever
  happened.
- **Into the library**: the video is filed in the Media bin (folder *After Effects*) by the Phase 5
  import, which probes it: a format Windows can't play (an older After Effects' Lossless AVI, say)
  is kept but flagged, with *Transcode*.

**Tested with a stand-in.** After Effects isn't installed on the test PCs, so
`tests/MxU.Slides.FakeAfterEffects` is a small program the tests build and copy in as both
`aerender.exe` and `AfterFX.exe`, in a folder laid out like Adobe's. It prints aerender's real
progress lines (slowly, if asked, to test Cancel), fails when asked, does what the scripts' jobs
ask, and logs every command line it gets. The tests check the command lines exactly, the progress,
a failure's message, that Cancel stops it within moments and leaves nothing, that the user's
project is byte-for-byte unchanged while the copy it rendered had the changes saved in it, and the
import. The self-test drives the window with it too. **Real After Effects is for you to try**
(section 10).

## 7. Video files: `MotionTemplates/Export/LottieVideoMaker.cs`

Follows Phase 6's motion backgrounds (`Broadcast/Library/MotionBackgroundMaker.cs`): the same
H.264 writer (`RenderedVideoWriter`, Microsoft's software encoder, a plain MP4 whose header gives
the exact length), the same `.partial` name until it's complete, the same two threads at once
(drawing frame *i* + 1 while the encoder takes frame *i*) passing three reusable buffers. Each frame
is drawn at its exact moment (`MotionTimeline.PlayThrough`), put over the chosen colour and turned
into NV12 in one pass (`Nv12Over`: with premultiplied alpha, "over a colour" is one sum per
channel, then the recorder's BT.709 maths), shared out over a `RenderCrew` (threads of its own,
below normal priority).

## 8. The starter templates

Written in code as Lottie (`Core/MotionTemplates/StarterMotionTemplates.cs`, with the small
`LottieBuilder`): no downloads.

- **Name Strap**: a gold bar grows, a navy plate sweeps out from it, the name and title rise in.
  On Take Down the words drop away, the plate sweeps back and the bar goes last.
- **Scripture Bar**: a gold rule draws out from the centre, a wide bar fades up beneath it, and the
  verse and reference rise in. Long verses shrink to fit the bar.
- **Title Card**: a full-screen welcome with three soft glows drifting round an eight-second loop
  (each glow's path ends where it starts, so the loop is seamless).

Their text boxes use Skottie's "shrink to fit the box" setting (`"rs": 2`, a Skia extension other
players ignore), so a long name gets smaller rather than spilling off the plate. They use the
fonts the app ships.

## 9. Speed

Measured on this laptop (Intel Core i5-10500H, 6 cores), shared at the time with five other
engineers' builds and tests, so the numbers move by up to two times from run to run.

| Template, 1920 × 1080 | One thread | In bands (4 threads) |
|---|---|---|
| Name Strap (lower third) | 0.8–1.5 ms a frame | not needed |
| Scripture Bar (lower third) | 2.4 ms a frame | not needed |
| Title Card (full screen, three big soft glows) | 28–57 ms a frame | 12 ms a frame |

- **A lower third is easily 60 frames a second**, and once it has settled it costs nothing at all
  until it's taken down (section 4).
- **The Title Card was too slow on one thread** (the first self-test measured 21 frames a second on
  screen): soft gradients across most of a 1080p frame are what a processor draws slowest. So a
  template that takes more than 8 ms a frame, three frames running, is drawn in **bands**
  (`BandedRenderer`): four horizontal strips, each by its own copy of the animation (Skottie can only
  be used by one thread at a time) on a small crew of threads of its own, each into its own slice
  of the same picture. The pictures match a whole-frame drawing (a difference of 16 levels summed
  over all 8 million bytes). In the self-test it then drew **39 frames a second at 9.8 ms each**,
  with the screens copying 36 a second; on a quiet PC it's about 12 ms a frame. The export uses
  the same bands.
- **Copying to the screens**: 1.7–2.5 ms a picture on the UI thread for a full-screen template that
  changes everywhere; a lower third copies only the strip that moved.

So: lower thirds, the common case, play at the screen's own rate. A heavy full-screen background
plays at 30–40 frames a second on this laptop while it's shared, smoother on a quiet one; a
30 fps Lottie (as most are) loses nothing at that rate.

## 10. Trying it with real After Effects

On a PC with After Effects 2023 or later:

1. Open **Overlays › Motion templates… › After Effects…**. It should say which After Effects it found.
   If it says none, press **Locate aerender.exe…** and pick it from `…\Adobe After Effects
   <version>\Support Files`.
2. **Choose .aep…**, then **Read compositions**. After Effects starts (the first time takes a
   while), opens a copy, and the list fills in. If After Effects is open with unsaved work, it
   refuses: save or close it first.
3. Pick a composition, change a text layer or a colour, give it a name, and **Render**. The progress
   bar counts frames. The video appears in the Media bin under *After Effects*.
4. Check your `.aep` is unchanged (its modified time is the same).

Older versions of After Effects (before 2023) have no built-in H.264: set the output module to
*Lossless* and use *Transcode* on the imported tile.

For the Lottie route, follow **How to make one…**: install Bodymovin, name your text layers, add
In, Hold and Out markers, export with *Glyphs* off and *Include in json* on, and import the `.json`.

## Things learnt the hard way

- **Skottie counts frames from the in point**, not from zero. A test animation trimmed to start at
  frame 10 showed frame 30 when asked for frame 20 until the renderer subtracted it.
- **A text layer's change area is loose the moment it becomes visible.** Skottie reports a box
  covering the whole layer (even off the picture) on the first frame a text layer's opacity rises
  above zero; after that the boxes are tight. So a copy is occasionally bigger than it needs to
  be. It's still never wrong: every picture is drawn whole, and only the copying is trimmed.
- **A lower third's Out needed nothing new in the show.** The first idea was a new "motion" kind of
  item that `ShowState` would wait for. But the show already waits for an overlay's Out steps, and
  an Out step can simply hold (a delay as long as the template's Out, then a 50 ms fade). So the
  live copy carries that step, and every way of taking an overlay down plays the template's Out.
- **A full-screen template's still would have been wrong**, and a lower third's video would have
  covered the slide (section 5).
- **Progress made on the wrong thread crashed the app.** `new Progress<T>(…)` remembers the thread it
  was made on and reports there. Made inside the render's own thread, its reports went to the shared
  pool and touched a window: WPF refuses that, and the app closed. The reporters are now made on the
  UI thread before the render starts, and a job hands any stray report to the UI thread anyway.
- **A restarted animation showed stale pixels.** The screen remembers which picture it last copied
  (a version number). When the same picture id started again (a preview looping round, a lower
  third put up again), the new animation's version 1 was "older" than the screen's, and the change
  since then came out empty. A version the animation hasn't reached now means "copy it all".
- **Bins don't draw live fills.** An overlay's picture is a shape *filled* with the picture, and views
  that don't play video (bins, thumbnails) draw a placeholder for live fills, which are cameras. A
  motion template's picture is the app's own drawing, so those views show it too.
- **A raised Click doesn't run a Command.** The self-test's first click on *Motion templates…* did
  nothing: `RaiseEvent(Click)` only reaches Click handlers. It runs the button's command instead.

## Where to look

| What | File |
|---|---|
| Reading Lottie, fields, timing | `Core/MotionTemplates/LottieDocument.cs`, `LottieFiles.cs`, `MotionFields.cs`, `MotionTimeline.cs` |
| The library and the Mac's documents | `Core/MotionTemplates/MotionTemplateStore.cs`, `MotionTemplate.cs` |
| The starters | `Core/MotionTemplates/StarterMotionTemplates.cs`, `LottieBuilder.cs` |
| Drawing and playing | `MotionTemplates/Lottie/LottieRenderer.cs`, `BandedRenderer.cs`, `MotionPlayers.cs`, `LottieFonts.cs`, `LottieStills.cs` |
| Video files | `MotionTemplates/Export/LottieVideoMaker.cs`, `Nv12Over.cs` |
| After Effects | `MotionTemplates/AfterEffects/AfterEffectsInstall.cs`, `Aerender.cs`, `ExtendScripts.cs`, `AfterEffectsBridge.cs` |
| The app | `ViewModels/MainViewModel.MotionTemplates.cs`, `Rendering/MediaHub.Motion.cs`, `Rendering/MotionPreviews.cs`, `Views/MotionTemplatesWindow.cs`, `Views/AfterEffectsWindow.cs`, `Views/LottieGuideWindow.cs`, `Views/MainWindow.MotionTemplates.cs` |
| Unit tests | `tests/MxU.Slides.Core.Tests/MotionTemplates/*`, `tests/MxU.Slides.MotionTemplates.Tests/*`, the stand-in `tests/MxU.Slides.FakeAfterEffects` |
| Self-test | `Infrastructure/SelfTest.MotionTemplates.cs`, pictures `mt-…` |

## Things to try

1. **Make your own template.** In After Effects (or any Lottie tool), make a 5-second lower third
   with markers In at 0, Hold at 1 s, Out at 4 s, and a text layer called "Speaker". Export it with
   Bodymovin, import it, and type a name into the bin while it's up.
2. **Read the timeline.** Open `MotionTimelineTests.cs` and follow `TakingItDownJumpsToTheOutAndFinishesWhenItsOver`
   with `MotionTimeline.FrameAt` beside it.
3. **Watch holding cost nothing.** Put a lower third up and watch the speed line in the self-test
   report, or set a breakpoint in `MotionPlayers.Draw`: once the In is over, it returns before
   drawing.
4. **Change a starter.** In `StarterMotionTemplates.NameStrap`, change `Navy` to your church's
   colour. The starters are refreshed when they change, so the gallery shows the new one.
5. **See what a Mac gets.** Add a lower third, then open its overlay's JSON in your *test* library:
   one media object pointing at a PNG. Then look in `Windows extras/motion template uses/` for
   the file with the same id.

## Different from the Mac

The Mac has no motion templates, Lottie or After Effects bridge. Nothing here changes what the Mac
reads: the uses are ordinary overlays (with a still) and ordinary videos, and everything
Windows-only is in the extras.

## For PORTING.md

Windows-only (no Swift twin; add under "Windows only"):

| C# | What |
|---|---|
| `MxU.Slides.Core/MotionTemplates/*` (`LottieDocument`, `LottieFiles`, `MotionFields`, `MotionTimeline`, `MotionTemplate`, `MotionTemplateStore`, `LottieBuilder`, `StarterMotionTemplates`) | Motion templates: reading Lottie (.json, .lottie), fields, In/Hold/Out timing, the catalogue and uses as Windows extras with Mac-readable fallbacks, the starters |
| `MxU.Slides.MotionTemplates/*` (new project: `Lottie/`, `Export/`, `AfterEffects/`) | Drawing with SkiaSharp.Skottie, live playback on its own thread, MP4 export, the After Effects bridge (aerender, ExtendScript) |
| `MxU.Slides.Windows/ViewModels/MainViewModel.MotionTemplates.cs`, `Rendering/MediaHub.Motion.cs`, `Rendering/MotionPreviews.cs`, `Views/MotionTemplatesWindow.cs`, `Views/AfterEffectsWindow.cs`, `Views/LottieGuideWindow.cs`, `Views/MainWindow.MotionTemplates.cs`, `Infrastructure/AppSettings.MotionTemplates.cs` | Overlays › Motion templates…, live templates on the program, the bin's fields and hover preview, Edit motion template…, the After Effects window |

Small changes to ported files (no behaviour change for anything that isn't a motion template):

- `Rendering/SceneView.cs`: a video that stands in for a live template draws the template; a template's picture shows nothing (not "Waiting for…") before its first frame, and is drawn as a fill in views that don't play video (bins).
- `Rendering/MediaHub.LiveInputs.cs`: `motion::` pictures are live pictures, drawn from MediaHub.Motion.cs.
- `ViewModels/MainViewModel.Overlays.cs`: an overlay goes up (and is re-sent after an edit) through `ForShow`; a motion template's fields join the bin's.

Not ported yet / not done:

- Lottie features Skottie doesn't draw (most After Effects expressions, some effects) are drawn without them; Bodymovin's "Glyphs" text (text turned to shapes) can't be edited.
- Image fields take pictures from files (they're added to the media library); there's no picker for pictures already in the library.
- The bridge changes text layers and colours by layer; it doesn't read or set Essential Graphics properties, and can't list compositions without `AfterFX.exe`.

*This is the walkthrough; see also [HOW-IT-WORKS.md](../HOW-IT-WORKS.md) and [PORTING.md](../PORTING.md).*
