# How MxU Slides for Windows works

This guide walks through the app the way the data flows: from a song on disk to light on the projector. Each section names the files involved, so you can open them alongside. Every file is short enough to read in one sitting.

## The big picture

```mermaid
flowchart LR
    Schema["packages/schema/documents.json<br/>(shared with the Mac)"] -->|tools/codegen-csharp.mjs| Models["Core/Generated/Models.cs<br/>Presentation, Slide, Theme, Service…"]
    Disk[("Library folder<br/>one JSON file per document")] <--> Store["Core/Library/LibraryStore.cs"]
    Models --- Store
    Store --> VM["Windows/ViewModels<br/>MainViewModel"]
    VM -->|"click a slide"| Show["Core/Scene/ShowState.cs<br/>what is live, which builds have played"]
    Show -->|"AnimatedScene()"| Builder["Core/Scene/SlideSceneBuilder.cs<br/>slide + theme → items + animation steps"]
    Builder --> Scene["AnimatedScene<br/>layers of boxes, text, media"]
    Scene --> Eval["Core/Animation/AnimationEvaluator.cs<br/>where is each item right now?"]
    Eval --> View["Windows/Rendering/SceneView.cs<br/>draws it with WPF, every frame while it moves"]
    View --> Out["Outputs/OutputWindow<br/>projector, stage, preview"]
    Remote["Phone / tablet / Companion"] -->|"HTTP + WebSocket"| Api["LocalApi<br/>remote control"]
    Api --> VM
```

The code is split into projects:

- **`src/MxU.Slides.Core`** holds everything that isn't Windows-specific: the document models, the library on disk, turning lyrics into slides, working out what the screen should show, the animation engine, timers and stage-display text. It has no UI code, so it's easy to unit-test (`tests/MxU.Slides.Core.Tests`). It's also the part that mirrors the Mac's Swift code most closely.
- **`src/MxU.Slides.Windows`** is the WPF app: windows, buttons, drawing, video playback, and putting outputs on displays.
- **`src/MxU.Slides.LocalApi`** is the remote-control server (section 15).
- **`src/MxU.Slides.PptxImport`** and **`src/MxU.Slides.ProImport`** read PowerPoint and ProPresenter files (section 16).
- **`src/MxU.Slides.Audio`** plays music and captures the app's own sound (section 21).
- **`src/MxU.Slides.VideoIO`** sends to and receives from NDI and DeckLink, and reads cameras (sections 20 and 22).
- **`src/MxU.Slides.Broadcast`** records the program to MP4 and streams it over RTMP/RTMPS or HLS (section 25).

Each has its own test project under `tests/`. The Core/app split is the most important design decision. The Mac app has the same shape: its `PresenterCore` and `SlideScene` packages are the "core", and its SwiftUI/Metal code is the "app".

## 1. Documents: what a song *is*

Open `src/MxU.Slides.Core/Generated/Models.cs`. Every type there (a `Presentation`, a `Slide`, a `SlideObject`, a `Theme`, a `Service`) is generated from `packages/schema/documents.json`, the same file the Mac app generates its Swift models from. **Don't edit it by hand.** Change the schema, then run:

```powershell
node apps/windows/tools/codegen-csharp.mjs
```

Because both apps come from one schema, a song saved on Windows has the same field names and values as one saved on the Mac.

A song is a `Presentation` with these parts:

- **`Sections`**: Verse 1, Chorus, …
- **`Slides`**: each one points at its section with `SectionId`. A slide holds `Objects`, usually one text object called "Lyrics".
- **`Arrangements`** (optional): orders of section ids, for example "Verse 1, Chorus, Verse 2, Chorus".
- **`ThemeId`**: the theme it's drawn with.

## 2. The library: saving safely

`src/MxU.Slides.Core/Library/LibraryStore.cs` keeps one JSON file per document under folders such as `Presentations/` and `Services/`. Media files are copied into `Blobs/` and named by their SHA-256 hash, so the same video imported twice is stored once.

The part to study is `AtomicWrite`:

1. It writes to `song.json.tmp` and flushes it to disk.
2. It uses `File.Replace` to swap the new file in, keeping the old one as `song.json.bak`.

If the power goes in the middle, you have either the old file or the new one, never half of each. `TryRead` uses the `.bak` when a file is damaged. Deleting moves files to `Trash/`, and `BackupIfDue` snapshots everything once a day.

## 3. Lyrics → slides

`src/MxU.Slides.Core/Lyrics/` is a line-for-line port of the Mac's lyric code.

- **`LyricTextImporter`** works out what kind of text you pasted (SongSelect, ChordPro, a chord chart or plain lyrics), then pulls out the title, CCLI number and key.
- **`Reflow.Parse`** walks the lines:
  - a line that matches a section name starts a section (the regex is in `Reflow.LabelPattern`);
  - a blank line ends a stanza;
  - each stanza is cut into slides of *N* lines.
- **`Reflow.Build`** turns that into `Sections`, `Slides` and, if a section repeats ("Chorus" pasted twice), an "As Pasted" arrangement.
- **`SongEditing`** (Windows-only) is what the song editor uses. It also keeps a slide's background and notes when you re-edit the words, and parses play orders like `V1, C, V2, C x2`.

The tests in `tests/MxU.Slides.Core.Tests/Lyrics/` were ported from the Mac's tests. Reading them is the quickest way to see the rules.

## 4. Themes: how a slide gets its look

A slide's text box usually has no font or position of its own. It borrows them from a **theme slide** (a "design"). Open `SlideSceneBuilder.cs`, look at `ThemeSlide`, `PlaceholderAssignments` and `ComposedStack`, and follow these steps:

1. Pick the theme slide whose name matches `slide.ThemeSlideName` (for example "Lyrics" or "Lyrics (Lower Third)").
2. Match each text object on the slide to a text **placeholder** in the design, by name first and then in order.
3. Build the stack: the design's shapes (bands, rules), with the slide's text dropped into the placeholders' places.
4. Each text property is resolved as *slide's own style → placeholder's style → theme default*. See `StyledTextFor`.

So changing a song's theme restyles every slide at once, and a slide can still override one thing. `Library/StarterPacks.cs` builds the four starter themes with the Mac's coordinates and colours.

## 5. What's live: `ShowState`

`src/MxU.Slides.Core/Scene/ShowState.cs` is a small state machine for what is on the audience screen:

- the live **slide**, with its presentation and theme;
- live **media** on each layer: background loops, still graphics, foreground videos;
- **overlays** and a **message**.

Some rules worth spotting, all straight from the Mac:

- A background set to *until replaced* stays when the next slide comes up. Foreground media is swept off by the next slide (see `Fire`).
- Each clear (F1–F6) removes one "function" (`Clear(ShowFunction)`).
- It remembers where the live slide's **builds** are: which clicks have played and whether the exit has (section 14).
- `AnimatedScene()` produces the picture of all of it, each item carrying its animation steps and clock. `Scene()` is the same picture with everything at rest, used for thumbnails, the self-test and text checks.

## 6. The picture: `RenderScene`

`Scene/SceneModel.cs` describes a frame without saying how to draw it. A frame is a canvas (1920×1080) made of layers in order: background media, stills, videos, slide, overlays, alerts. Each layer holds `RenderItem`s, and each item is a text box, a shape or a piece of media with a frame, rotation, opacity and so on.

Because this is just data, the stage display, the slide thumbnails, the audience output and the self-test all build scenes the same way and differ only in where they're drawn.

## 7. Drawing: `SceneView` and `TextLayout`

`src/MxU.Slides.Windows/Rendering/SceneView.cs` is a WPF element that draws a `RenderScene`.

- It scales the 1920×1080 canvas to fit any window, letterboxing the rest.
- Each item becomes its own `DrawingVisual`, so WPF can apply shadows, blur, rotation and skew on the GPU.
- `Present(scene, transition)` crossfades from the old frame to the new one. `Update(scene)` redraws in place without a transition, which is used every second for clocks and timers, and when a click plays a build.
- While anything moves, it asks the animation engine where every item is on each screen refresh (`OnFrame` → `Render`). It reuses each item's drawing and only changes its position, opacity, wipe or blur. It stops asking as soon as nothing is moving (`AnimationTimeline.IsAnimating`), so a still slide costs nothing.
- Text is laid out by `TextLayout.cs`, which copies the Mac's `TextRasterizer` rules:
  - insets, then word wrap;
  - a line-height multiple;
  - auto-shrink: a binary search down to the minimum size, exactly like the Mac;
  - line fills (the coloured bars behind lines) and letter tracking;
  - chords above lines.
- Media goes through `MediaHub.cs`. Images are decoded off the UI thread. Videos use one shared `MediaPlayer` per live item, so the projector and the operator preview show the same frame.

## 8. Outputs on real screens

`Outputs/OutputManager.cs` and `OutputWindow.cs` handle the displays.

- `Infrastructure/Displays.cs` asks Windows for every monitor, including friendly names like "EPSON PJ".
- Each output window is borderless and topmost, covers its monitor exactly, hides the cursor, and is marked *no-activate*, so pressing → still goes to the main window.
- When a display is plugged in or out, `SystemEvents.DisplaySettingsChanged` re-places the windows.
- With one screen, an output becomes an ordinary resizable window: `ShowAsPreviewWindow`.

## 9. The stage display

`src/MxU.Slides.Core/Stage/` is the confidence monitor.

- A **layout** (`ConfidenceLayoutTemplate.cs` has the Mac's six) is a set of text boxes with a `TextLink`: "current slide", "next slide", "clock", "timer", "video countdown", "stage message".
- Every second, `MainViewModel.BuildConfidenceInfo` gathers the live facts, and `LinkedText.ResolvedObjects` fills each box in.
- `TimerSnapshot` and `TimerController` do the timer maths: countdown, count up, count down to a time of day, plus the amber and red warnings.

## 10. The screen you click: MVVM

The WPF part uses **MVVM** (Model–View–ViewModel):

- **Views** (`Views/*.xaml`) are just layout. A button says `Command="{Binding NextCommand}"`.
- **ViewModels** (`ViewModels/`) hold the state and commands. `MainViewModel` is split by area into `.Live.cs`, `.Services.cs`, `.Media.cs`, `.Editing.cs`, `.Stage.cs` and `.Lyrics.cs`.
- `[ObservableProperty]` and `[RelayCommand]` come from CommunityToolkit.Mvvm. A source generator writes the property-changed and command boilerplate for you.

Trace one click from start to finish:

1. Clicking a slide tile calls `SlideTile_Click` in `MainWindow.xaml.cs`.
2. That runs `SlideClickedCommand` → `GoLive` → `Fire`.
3. `Fire` calls `ShowState.Fire` → `RenderOutputs`.
4. `RenderOutputs` calls `Show.AnimatedScene()` → `SceneView.Present` on the projector and the preview.

## 11. Overlays and masks (Phase 2)

An **overlay** is a free-standing set of objects: a name plate, a scripture lower third, a ticker or cinematic bars. It sits on its own layer above the slide, so it stays up while the slides change underneath. `Library/StarterPacks.cs` builds eleven per theme pack, with the Mac's coordinates and animation steps, and `ShowState.Fire(overlay)` / `DismissOverlay` put them up and take them down. Taking one down plays its Out animation first; it leaves the show once that finishes (`SweepFinishedExits`).

Several overlays use **masks**: text slides in from behind an invisible edge.

- A *matte* is a shape that other objects name in `MaskObjectId`.
- `SlideSceneBuilder.MaskWiring` works out which objects are mattes and which are masked.
- The renderer never draws a matte. It clips each masked item to its matte's shape (`SceneView.MatteGeometry`).

## 12. The canvas editor (Phase 2)

`Scene/EditorGeometry.cs` is a pure port of the Mac's editor maths:

- what's under the pointer, even when the object is rotated;
- the eight resize handles and their minimum size;
- snapping to the slide's edges, its centre and other objects within 8 units;
- marquee selection, and align and distribute.

Its tests are in `tests/.../Scene/EditorGeometryTests.cs`.

`Views/SlideCanvasEditor.cs` is the WPF control. It draws the slide with the real `SceneView`, and an overlay on top draws the selection, handles, guides and marquee and turns mouse gestures into frame changes.

A text box on a themed slide has no position of its own; it sits where its placeholder is. The first time you move it, it gets explicit `X/Y/Width/Height`, an *override*. **Reset to theme** clears them, just like the Mac. The inspector (`ViewModels/ObjectInspectorViewModel.cs`) follows the same rule: it shows the value you see, theme included, and writes any change onto the object itself.

## 13. Hot keys and MIDI (Phase 2)

- `Scene/HotKeyJump.cs` maps letters to sections. The Mac's defaults are A/S/D/F for Verses 1–4, C for Chorus and B for Bridge. `JumpStart` finds the next block of that section in the live arrangement, cycling through repeats.
- `Control/MidiControl.cs` maps MIDI note-on messages to commands with the same notes as the Mac and ProPresenter. For "select by index" commands, the velocity is the number: Trigger Slide with velocity 3 takes slide 3 live. It also parses raw MIDI bytes, including running status and SysEx.
- `Infrastructure/MidiInputService.cs` listens through Windows' own MIDI API (`Windows.Devices.Midi`). MIDI control is off until you switch it on in Settings.

## 14. Animations and builds (Phase 2)

Slides, theme designs and overlays can animate: text rising in, a plate wiping across, words appearing one by one. `src/MxU.Slides.Core/Animation/` is a line-for-line port of the Mac's engine.

- Each object has **steps** (`AnimationStep` in the schema): an In, Out, Emphasis or Morph, with an effect (fade, move, scale, wipe, blur, type, draw…) and a **trigger**: *With Previous*, *After Previous*, *On Click* or *On Dismiss*.
- `AnimationSequence.Groups` sorts the steps into an automatic group (plays when the slide fires), one group per click, and an exit group.
- A text box on a themed slide with no steps of its own borrows its placeholder's (`PlaceholderAnimations.Compose`). That's how every lyric slide gets the theme's animation.
- `ShowState` keeps the clock: when the slide fired, when each click happened, and when an exit or a clear was pressed. `SlideAnimationContext` hands those times to the scene.
- `AnimationEvaluator.Resolve(scene, now)` is a pure function: given the scene and a time, it says what to draw and how each piece is moved. That makes it easy to test (`tests/.../Animation/`) and to freeze at any instant. The self-test takes pictures 0.15, 0.35 and 0.6 s into a lower third.

What **Next** does depends on the slide, as on the Mac:

1. If the slide has click groups left, Next plays the next one and stays on the slide.
2. Then, if it has an exit group, Next plays that.
3. Otherwise Next moves to the next slide.

The starter lyric designs put the words' Out on a click, so the first Next plays the Out and the second moves on. **Alt+→** goes straight to the next slide, skipping any builds left (Option+→ on the Mac). The live label shows "build 1 of 1". Previous takes builds back.

What `SceneView` draws: moves, scales, rotation, fades, wipes with soft edges, blur, typewriter and word-by-word reveals, and line drawing. The 3-D tilt and swing are flattened into a squash. Keystone, burn, glitch and tint need GPU shaders and aren't drawn yet.

## 15. Remote control: the Local API (Phase 2)

`src/MxU.Slides.LocalApi/` is a port of the Mac's LocalAPI package. It's a small web server built into the app (ASP.NET Core's Kestrel), off until you switch it on in **Screens & Settings › Network & API**. It speaks the same REST and WebSocket protocol as the Mac, so the same remotes and scripts work with both.

- `ApiRouteTable.cs` lists every endpoint: `GET /v1/status`, `POST /v1/show/slide`, `POST /v1/show/advance`, `POST /v1/alerts/fire` and the rest. Each one needs a **key** (`Authorization: Bearer mxu_…`) with enough scope: *View* reads, *Control* runs the show, *Edit* also changes the library.
- `ApiScope.cs` stores only salted hashes of keys, in `local-api-tokens.json`. The default key's secret is kept encrypted for your Windows account with DPAPI (`MainViewModel.ApiServer.cs`), so Settings can copy it again.
- The server knows nothing about WPF. It calls the app through one interface, `IApiBridge`, which `ViewModels/MainViewModel.Api.cs` implements. The server always calls it on the UI thread, so the bridge can touch the show directly.
- When the show, timers or library change, `PublishApi` pushes a fresh snapshot to every WebSocket client subscribed to that topic (`/v1/ws?token=…`).
- `/docs` serves a page listing every endpoint, and `/openapi.json` describes them for tools.

Try it: turn the API on, copy the default key, then in PowerShell:

```powershell
$key = "mxu_paste_your_key_here"
Invoke-RestMethod http://localhost:6980/v1/status -Headers @{ Authorization = "Bearer $key" }
Invoke-RestMethod http://localhost:6980/v1/show/advance -Method Post -Headers @{ Authorization = "Bearer $key" } -ContentType application/json -Body '{"steps":1}'
```

## 16. Importing PowerPoint and ProPresenter (Phase 2)

Both importers are ports of the Mac's and save straight into the `LibraryStore`. They run on a background thread and report progress, which the notice bar shows.

- **PowerPoint** (`src/MxU.Slides.PptxImport/`): unzips the `.pptx` safely (no `..` paths, no zip bombs), reads the slide XML with its master and layout inheritance, maps shapes, text, pictures, videos and animations, and bakes textures into PNGs. Importing the same file again replaces it in place.
- **ProPresenter 7** (`src/MxU.Slides.ProImport/`): ProPresenter stores documents as *protocol buffers*. `Proto/` holds the open `.proto` descriptions of that format (MIT licensed), and the build turns them into C# classes. The importer maps presentations with their groups, arrangements and media, playlists (each becomes a service), themes, props, messages and stage layouts. Before replacing something already in your library, it asks.
- **`.pro`** is also ChordPro's extension. `MainViewModel.IsProPresenterFile` tells them apart: ProPresenter's is binary, ChordPro's is text.
- **A whole ProPresenter library**: **Screens & Settings › Import ProPresenter library…** opens at `Documents\ProPresenter`. Run it again later to pick up changes; anything you've edited here is kept.

## 17. The program feed (Phase 3)

Recording, streaming, NDI and DeckLink all need the audience picture as a stream of frames, not just a window. `Outputs/ProgramFeed.cs` makes that stream, the way the Mac's OutputMirror does.

1. When something starts listening, it opens a hidden **program window** with its own `SceneView`. It's given exactly the same scenes as the projector, so transitions, animations and videos match. Videos share the projector's player (same playback key), so they stay in step.
2. `Outputs/WindowCapture.cs` reads that window's picture through the graphics card (Windows.Graphics.Capture plus Direct3D 11, via the Vortice library) and copies it into memory.
3. A pacing thread hands the newest picture to every destination 30 times a second. It repeats the last one when nothing changed, because encoders want a steady rate.

Destinations implement one small interface, `IProgramSink` in `Core/Media/ProgramFeed.cs`: `WriteVideo(VideoFrame)` and `WriteAudio(AudioBuffer)`. They must return quickly; if one falls behind, frames are skipped and counted rather than slowing your screen.

Two things learnt the hard way, both checked by the self-test:

- **WPF's own `RenderTargetBitmap` draws on the CPU.** It managed 13 frames a second at 1080p and stalled the operator screen. Capturing a real window uses the GPU and holds 30.
- **Windows doesn't draw windows that are entirely off-screen.** So the program window sits at the top-left of the main screen, behind everything, fully transparent (alpha 0) and click-through. You never see it, but Windows keeps its picture current.

## 18. GPU shaders (Phase 3)

Some looks can only be drawn pixel by pixel on the graphics card: keystone correction, edge blending, and effects like pixelate or stained glass. WPF allows that through a `ShaderEffect` with an HLSL pixel shader.

- `Rendering/ShaderCompiler.cs` compiles the shader source when the app starts, with the compiler that's part of Windows (`d3dcompiler_47.dll`). The repo holds readable shader code and no compiled binaries.
- `Rendering/ItemEffect.cs` holds all of an object's picture effects in one shader: colour adjust, hue rotate, invert, posterize, pixelate, vignette, warp, scatter, stained glass and grain, plus the animation passes glitch, burn and tint. Each is a line-for-line port of the Mac's Metal shader in `RenderEngine/ShaderSource.swift`.
- `SceneView` gives each item one layer per effect, first effect innermost. Effects that move (warp, scatter, grain, stained glass) keep the frame loop running.
- `Rendering/OutputAdjustEffect.cs` is the per-screen correction pass: corner-pin keystone, brightness, contrast, gamma, black level, RGB levels, edge-blend ramps and masks. It's a port of `output_adjust_fragment`.
- Echo and ghost trails need the previous frame as an input, which WPF effects can't get, so they aren't drawn yet.
- On a PC without a graphics card that supports pixel shader 3.0, effects and corrections are skipped; the picture still shows.

## 19. Screens (Phase 3)

The Windows app used to have exactly two outputs, Audience and Stage. It now has the Mac's screen model, in `src/MxU.Slides.Core/Outputs/`:

- A **screen** has a name, a size (1920×1080, 4K, an odd LED-wall size…), a job (the audience program or the stage display) and a fit (show everything, or fill and trim).
- A screen has one or more **slices**: the part of its picture an output shows. One slice is the whole screen.
  - **Mirror** makes several outputs show the same thing.
  - **Grouped** splits a wide picture across side-by-side projectors.
  - **Edge blend** does the same with an overlap that ramps down so the seam disappears.
  - **LED wall** places the picture at an exact spot on a processor's input.
- Each slice is **carried** by a device id: `display:\\.\DISPLAY2`, `ndi:MxU Slides Stage`, or `decklink:<card>`.
- Each slice can be **corrected**: corner-pin keystone, rotation, brightness, contrast, gamma, black level, red/green/blue, edge-blend ramps and masks.
- `ScreenSetup` saves the whole thing in `settings.json` in the Mac's own JSON shape. On first run your old Audience and Stage display choices become two screens.

The WPF half is in `src/MxU.Slides.Windows/Outputs/`:

- `OutputManager` opens one `OutputWindow` per display that carries slices, and one `DeviceFeed` per NDI source or DeckLink card. It sends every screen its picture: the program reshaped to its size (`Config.Program`), the stage display, or the alignment grid while you line projectors up.
- A `SliceView` shows its part of the screen's picture at full resolution: the scene is drawn larger than the slice and clipped. Then the correction shader runs on it (section 18). The maths (warp matrix, blend uniforms) comes from `Core/Outputs/OutputPass.cs`, so it's tested without a graphics card.
- `Views/ScreensWindow` is where you set it all up (**Screens & Settings › More screens, NDI, DeckLink, edge blending…**). Changes apply live and save themselves.

## 20. NDI and DeckLink (Phase 3)

`src/MxU.Slides.VideoIO/` is a port of the Mac's NDIKit and DeckLinkKit.

- **NDI:** nothing is shipped with the app. `NdiLibrary` loads the NDI runtime that NDI Tools installs, through a table of functions it checks against the DLL's exports. Without NDI Tools it reports "NDI runtime not installed" and nothing else changes.
  - A screen carried by `ndi:<name>` becomes a source other NDI programs (OBS, vMix, NDI Studio Monitor) can pick. The self-test checks it appears on the network.
- **DeckLink:** the app talks to Blackmagic's Desktop Video driver through COM. The interfaces are declared by hand in `DeckLinkInterop.cs`, with the IDs from Blackmagic's headers. Frames go out as BGRA, or are converted to 10-bit-safe 8-bit YUV (BT.709) when a mode needs it. Keying uses the picture's alpha.
- **Cards:** a DeckLink card shows up in the Screens window as an output if one is installed.

## 21. Music (Phase 3)

`src/MxU.Slides.Audio/` is a port of the Mac's AudioEngine, built on NAudio (MIT) and Windows' WASAPI.

- `AudioController` is the Audio function: playlists with crossfades, single songs, fades when stopping, next/previous/seek. The **Music** tab in the bottom bin plays them, and F5 stops them.
- Each output runs on its own sound device stream. With **no** sound device (a server, or speakers unplugged) the engine keeps time anyway, so playlists still advance and streams still get sound. It recovers when the device comes back.
- `ProgramAudioTap` records *this app's* sound only, through Windows' "process loopback". That includes video sound and music, but not system sounds or other apps. It's what recordings and streams hear.
- Multitrack (stems and click tracks) is ported too: `MultitrackEngine`, and a separate tracks-player process so heavy decoding can't stall the show. The timeline UI that drives it is still to come.

## 22. Live video inputs (Phase 3)

Cameras, capture cards, NDI sources and DeckLink inputs are **inputs**: named entries ("Pulpit camera") kept in `video-inputs.json` by `VideoInputInventory`.

- Set them up with **Media › Video inputs…**. Each one then appears as a tile at the start of the Media tab; clicking it puts it on the Video Input layer, under the slides.
- Their cue ids start with `input::item::`, as on the Mac. `MediaHub.LiveInputs` copies each input's newest frame into one `WriteableBitmap` per refresh, and every view showing it draws that bitmap, so all outputs stay in step.
- `LiveInputDelay` can hold an input back a few frames so lips line up with the sound.

## 23. Show automation (Phase 3)

`src/MxU.Slides.Core/Automation/` is a port of the Mac's ActionRouter, scheduler, timecode readers and presentation timelines.

- **Slide actions:** a slide (or a media item) can carry actions that run when it goes live, such as firing a background, putting up a message, starting a timer, switching the output preset or sending MIDI. `ActionRouter.SlideDidFire` runs them, with their delays. Clear All (F1) cancels any still waiting.
- **Action combos:** a named list of actions. Run one from the **Combos** tab, with MIDI "Run Action Combo" (velocity = number), or with `POST /v1/action-combos/{id}/fire`. ProPresenter macros import as combos.
- **What actions can do:** the router never touches the show directly. It calls `IShowActions`, which `ViewModels/MainViewModel.Actions.cs` implements over the live show, timers, overlays, music, inputs and MIDI out. Anything this PC can't do yet is logged and skipped, so one missing piece doesn't stop a combo.
- **Scheduler:** schedule triggers run their actions on chosen days at a time, once on a date, or when a timer reaches a value. The scheduler's memory (`scheduler.json`) remembers what has fired, so a restart never fires anything twice. It looks again when the clock changes or the PC wakes. ProPresenter calendar events import as triggers.
- **Timecode and timelines:** see the next section.

## 24. Song timelines and timecode (Phase 3)

A **timeline** belongs to a song (one per arrangement). It is a list of cues at times in seconds: "slide 3 at 0:42", "this background at 1:10", "the music from 0:00", "run these actions at 2:05". `TimelinePlayer` (in `Core/Automation/Timelines/`, ported from the Mac's PresentationTimelineController) plays one song's timeline on its own clock, 30 ticks a second. Each tick it asks `PresentationTimelines.Crossing` what the playhead passed since the last tick and does it.

- **In the app:** the bar under the slides (**Timeline** in the song's header, or it shows by itself when the song has a timeline). ▶ plays, ⏮ goes back to the start, the slider moves the playhead.
- **Recording a take:** press **Record**, then click each slide when it should change while the song plays. `Fire` in `MainViewModel.Live.cs` calls `NoteSlideLanded`, and the player turns each click into a cue at the playhead. Press **Finish** to save it. **⋯ › Undo the last take** puts the timeline back as it was.
- **Music:** **⋯ › Play this music with the timeline** puts a song from the Music tab on the timeline's music lane, so pressing ▶ starts the music and the slides together. The music plays through the same `AudioController` as the Music tab (`TimelineMusic` in the Audio project is the adapter).
- **Where a slide cue points:** not at "slide 7" but at "the 2nd time the chorus plays, its first slide" (`TimelineSlideAnchor`). So changing the arrangement doesn't move cues onto the wrong words.
- **The app's half** is `ViewModels/MainViewModel.Timeline.cs`. It implements `ITimelineHost` (find a presentation, fire a slide, run actions, what's live) and draws the bar. A presentation opened from the library rather than a service gets an item id of `presentation:<id>`, so it can still have a timeline.
- **Stems (multitrack):** a timeline can carry stems (click, guide, pads). They play on a separate `MultitrackEngine`, made only the first time a song has stems (`LazyTracksPlayer`). The Mac's editor makes them; Windows plays them.

**Timecode in.** A playback rig (Ableton, QLab, a video playback machine) can send timecode, and the app follows it: each song whose timeline has **⋯ › Follow timecode** on, and a start time (**⋯ › Timecode start**, e.g. `01:00:00:00`), plays when the timecode reaches it.

- Choose the input in **Screens & Settings › Timecode in**: **LTC** from one channel of a sound input, or **MTC** from a MIDI input. The readout there shows the timecode and Locked / Stopped / No timecode.
- `TimecodeFollower` (Core) reads the input 30 times a second and drives the `TimelinePlayer`. The app supplies the readers (`ITimecodeInputs` in `MainViewModel.Timeline.cs`):
  - LTC: `AudioInputHub` captures the device; one channel goes to `LtcReader`, which decodes the audio waveform into frames.
  - MTC: `MidiInputService` hands every MIDI message's bytes to `MtcReader`, stamped with the time it arrived.
- The stage display can show the timecode (a Timecode text link), and `GET /v1/timecode` reports it to remotes.
- These settings belong to this PC (they name its devices), so they're kept in `settings.json`, not in the library.

## 25. Recording and streaming (Phase 3)

`src/MxU.Slides.Broadcast/` is a port of the Mac's StreamEngine and recorder. It has no WPF in it; the app gives it pictures and sound.

- **Pictures** come from the program feed (section 17): each recording or stream is one more `IProgramSink`. `FramePacer` thins the feed to the preset's frame rate, and each sink scales the picture to its own size.
- **Sound** comes from `ProgramAudioTap` (section 21). `UpdateProgramAudio` in `MainViewModel.Broadcast.cs` connects it whenever the program feed has anyone listening, so recordings hear the videos and music this app plays, and nothing else on the PC.
- **Encoding** uses Windows' own Media Foundation encoders (H.264 video, AAC sound), through hand-written COM declarations in `Media/MediaFoundationInterop.cs`. A hardware encoder (Intel Quick Sync, NVIDIA, AMD) is tried first; if it fails the rest of the run uses the software one.
- **Recordings** are MP4 files in `Videos\MxU Slides`. The file is written as `name.partial.mp4` in fragments, so a crash or power cut still leaves a file that plays. It's renamed when the recording stops, and leftovers are renamed at the next launch (`RecoverInterrupted`).
- **Streams** go out over RTMP or RTMPS (YouTube, Facebook, Restream, any RTMP server: `Rtmp/RtmpPublisher.cs`, written from scratch) or HLS. A dropped connection reconnects with back-off; the status line says "Reconnecting (try 2)". When the network can't keep up, video is dropped until the next keyframe rather than letting a delay build up.
- **Stream keys are secrets.** `StreamKeyVault` keeps them encrypted for your Windows user (DPAPI) in `%LOCALAPPDATA%\MxU Slides\StreamKeys`. They are never written into the library, so a backup or a shared library never leaks them.
- **In the app:** **Stream & Record** in the toolbar opens `Views/BroadcastWindow.cs`. A **preset** says what to do: record only, or stream to the ticked destinations (and maybe record too), at what size, frame rate and bitrate. While anything runs, the toolbar button shows "REC 12:03" or "LIVE 4:10 · REC 4:10". It turns orange if a stream is struggling and red if one failed.
- **Automation:** the slide/combo actions "Start capture" and "Stop capture" start and stop presets (`StartBroadcast` in `MainViewModel.Broadcast.cs`).
- **Quitting** while recording asks first, then waits for the files to finish before closing.

Not on Windows (yet): SRT, HDR/HEVC streaming, ProRes recording, streaming a media file, and per-screen stream canvases (every preset records the audience program).

## 26. Keeping it running

- `App.xaml.cs` catches unexpected errors, logs them (`Infrastructure/Log.cs`) and keeps going.
- **Show timing never waits for spare threads.** .NET has a shared "thread pool" that runs small background jobs and timer callbacks. If something ties it up (a big import, a slow disk, a busy PC), jobs on it run late. So anything a service depends on has its own threads, in `src/MxU.Slides.Core/Threading/`:
  - `ShowTimeProvider.Shared` is the show clock: one dedicated thread that fires the timers for timelines, delayed slide actions, the scheduler, timecode and the audio feeds.
  - A `WorkQueue` runs jobs in order on its own thread. The music engine has four (`MxU.Slides.Audio/AudioThreads.cs`): events (a song ended, so the playlist moves on), chores, stem preloading, and Windows' audio-device calls.
  - `BusyThreadPoolTests` and `BusyPoolThreadingTests` tie up every pool thread on purpose and check a song still ends on time, the next song still loads and the clock still ticks.
- A `running.marker` file shows when the last session crashed.
- `Infrastructure/SelfTest.cs` drives the real app through a service and checks what's on screen. It covers lyrics, builds, overlays, video, the Local API (over real HTTP and a WebSocket), both importers, the program feed, screens, NDI, music, live inputs, actions, timelines and recording. Run `scripts/self-test.ps1` after any change. Checks that need hardware this PC doesn't have (a graphics card, NDI Tools) report SKIP, not FAIL.

## Things to try, to learn by doing

1. **Add a section name.** Make "Spontaneous" a recognised label. In `Lyrics/Reflow.cs`, add it to the list of names just above `LabelPattern`, then add a test in `ReflowTests.cs` and run `dotnet test`.
2. **Change the default transition time.** Find `TransitionSeconds` in `Infrastructure/AppSettings.cs`.
3. **Add a keyboard shortcut.** Make B clear the slide, ProPresenter-style. Look at `OnPreviewKeyDown` in `Views/MainWindow.xaml.cs`.
4. **Make a stage layout.** Copy `CurrentAndNext` in `Stage/ConfidenceLayoutTemplate.cs`, move the boxes, and add it to `WorkspaceStarters`. It appears in the Stage menu on a new library.
5. **Script the show.** Turn on the Local API, then write a PowerShell loop that calls `POST /v1/show/advance` with `{"steps":1,"settled":true}` every 5 seconds: an instant announcement loop.
6. **Slow an animation down.** In `Library/StarterPacks.cs`, find `TextIn` and double its duration. Delete `starter-revision.txt` in your *test* library so the themes are rebuilt, then fire a lyric slide.
7. **Record a timeline.** Open a song, press **Timeline › Record**, and click through the slides while you sing it in your head. Press ▶ and watch it run itself. Then look at the cues it wrote: open the song's JSON in your *test* library and find `timelines`.
8. **Follow an upstream change.** When MxU changes a Swift file, find its C# twin in [PORTING.md](PORTING.md), compare the two, and port the change.
