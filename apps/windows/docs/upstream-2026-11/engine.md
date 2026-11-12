# MxU's autumn 2026 releases: the engine catch-up

MxU released three Mac versions in October and November 2026 (upstream PRs #10, #12 and #13). This is the port of their **engine** changes to Windows: the parts of `apps/mac/Packages` that have a C# twin, plus the Windows UI each needed. The shared schema was already up to date (`Core/Generated/Models.cs`, schema 161), so Windows could already *read* Mac documents that use the new features. This port makes Windows *do* them.

No new enum value or field was written into a shared document. Every new thing Windows saves into the library (a preset's placements and custom layers, a video's marks) is a field the Mac's schema already has.

## 1. Video marks ("Video Actions")

A video can now carry **marks**: named points on its timeline (`MediaItem.marks`), each with a list of actions.

- `Core/Media/VideoMarks.cs` holds the rules. A mark's time is in the *pass* the operator sees (0 is the trim's in point), so `Points` moves marks by the in point and drops the ones outside the trim.
- **Firing.** `Crossed` decides which marks playback passed between two readings. The rule is "only what the time explains": if the video moved further than the elapsed time × play rate allows, it was a jump (a seek, a typed time), and nothing fires. A video that loops fires its marks again each pass, and one that starts at its top fires a mark at 0.
- `ViewModels/MainViewModel.VideoMarks.cs` reads every live video four times a second (the transport's refresh) and once more just after the next mark is due, on the show clock (`ShowTimeProvider`), not the thread pool. A crossed mark runs its actions through the action router.
- **Countdowns.** `VideoCountdown` (`Core/Stage/ConfidenceInfo.cs`) now carries the marks. A Video Countdown text box can count to the end (as before), to the **next mark**, or to a **named mark**, and can **count up** from the mark before. Counting up never turns amber.
- A new text source, **Video Action Name**, shows the coming mark's name ("Band Walk-On").

- **Making marks.** Right-click a video in the Media bin › **Video Actions…** (`Views/VideoActionsWindow.cs`). Type a time (or press **Playhead** while the video is live), then **+ Add video action**: a *Mark Only*, or a mark that runs an action and is named after it. Each mark's row lets you type a new time to move it, rename it, edit its **Actions…** and remove it. Every change is saved at once into `MediaItem.marks`, the field the Mac writes, so the Mac sees the same marks. The rules are in Core: `Adding` and `Moving` keep time order and stay inside the file, `Renaming` refuses a blank name, and `Clock` / `ParseClock` write and read a time like `1:02.5`.

The Mac edits marks on a timeline under the video's preview. Windows has no media editor yet, so its window is a list; the timeline with draggable pins, and a mark's colour, aren't ported. Choosing a countdown's target (the end, the next mark, a named mark, counting up) is in the Mac's text inspector, which belongs to the slide editor's port: Windows shows such a text box correctly, but can't make one yet.

## 2. Picture and video slides

- **Photo spots.** A theme design can mark shapes as photo spots (`SlideObject.photoSpot`). A slide made from the design carries one photo per spot; `SlideSceneBuilder.ComposedStack` lays the photos over the spots **in reading order** (left to right along a row, then down), each wearing its spot's corners, border and shadow (`Core/Library/PhotoSpots.cs`).
- **Starter designs.** Every starter theme now has the Mac's eight photo designs (Photo, Photo + Text, 2 Photos, 3 Photos, and lower- and side-third ones). Windows' own worship looks get them too, in their colours, so Notes' Make Slides finds one in any starter theme. A library from the last starter revision is repaired gently (`StarterPacks.Repair.cs`): the designs are added, nothing you changed is touched. The Points design now shrinks long points, but not below 64 pt.
- **Scale and Blur** (`Core/Animation/MediaLooks.cs`). A tall phone photo on a wide screen shows whole, over a blurred, slightly zoomed copy of itself that fills the screen, so there are no black bars. `MediaLooks.Expanding` turns it into two ordinary items just before each frame is drawn (the blurred fill, then the fitted picture), so nothing else in the renderer needed to change. In a photo spot, the blurred copy is clipped to the spot's outline with a matte. One deliberate difference from the Mac: WPF's blur fades a picture out towards its own edges, which showed as a dark rim inside the frame, so on Windows the blurred copy's frame is grown by the blur wherever something clips it (`MediaLooks.Spread`: all four sides under a spot's matte, and any side that reaches the canvas edge). The faded rim then falls outside what's seen.
- **Video end fades.** A video slide can fade over its last second, **to black** (a black cover rising over it) or **to clear** (the video itself fading, so what's under it shows). `SceneView` asks the player how long is left (`MediaHub.PlaybackRemaining`; a looping video never ends, so it never fades) and redraws every frame while a fade can happen.

## 3. Dissolves: each layer as one picture

Before, a dissolve faded every item on its own: as an opaque backdrop faded out, what it covered showed through the fading words. Now (`Core/Animation/SceneTransitionEngine.cs`) a dissolve on a layer of words, overlays or alerts keeps the whole old stack and the whole new stack, and marks the layer with `CrossfadeProgress`. `Rendering/SceneView.Layers.cs` draws each stack into its own container and fades the containers (WPF fades a container as one picture). Media layers keep the old way: the old picture holds under the new one.

Each layer also runs on **its own length** now: a quick background fade lands while the words are still dissolving. When a new slide interrupts a dissolve, `Flattened` turns the two stacks back into ordinary fades where they had got to.

The Mac mixes the two pictures in a shader; Windows fades the two containers. They match exactly when one side is empty (the usual case: words leaving, words arriving); when both are opaque the middle is a little darker on Windows.

## 4. The Screen Map

The Screen Map draws the room as one big picture with each screen where it really sits (`Core/Outputs/ScreenMap.cs`). It belongs to this PC (its screens are this PC's), so it's saved in `settings.json`, as the Mac keeps it in its own defaults.

- **Placements.** An output preset can place any layer on the map (`OutputPreset.placements`): "the slide across both side screens", "the logo in the corner of the LED wall". `ScreenMapResolver.Resolve` turns each placement into frames in one screen's own units; a frame can run past the screen's edge, because the screen shows only its part.
- **Drawing.** For a screen on the map, `OutputConfiguration.Placed` gives each placed layer a `PlacedLayer`: the room's (unscaled) picture of that layer and the frames it lands in. `SceneView.Layers.cs` draws the layer once into an unseen container and paints a brush of it into each frame, fitted or filling. A placed layer shows wherever it's placed, whatever the preset's routing; a screen it misses doesn't show it; a screen off the map keeps the routing as before. A placed Slide layer wears the look of the screen it mostly lands on, so a lyric across a wall reads the same on every piece. (Windows' screens draw the *animated* program, `Program(feed, AnimatedScene, looks)`; `OutputManager` hands it the looks' scenes for this. The Mac-shaped `PreviewProvider` is kept for the ported tests, but the app doesn't draw from it.)
- **Custom layers.** A preset can add layers of its own (`OutputPreset.layers`), each above a layer it names. Scenes now hold layers by id (`RenderLayer.Id`; `Kind` is null for a custom layer). Media fired onto a custom layer (a cue's `layerId`, or a media tile's **Play on Layer** menu) plays there; a new slide sweeps foreground media off it, and Clear Media clears it (`ShowState.CustomLayers.cs`).
- **The window.** Screens › **Screen Map…** (`Views/ScreenMapWindow.cs`): drag screens to where they sit (they snap to 10 pixels), place the preset's layers on every screen or across them (each **fitted** whole inside its frames, or **filling** them), add and remove custom layers, and **New LED Wall…**, which adds a screen of the wall's own pixel size seeded 1:1 into its processor's frame (HD, or 4K for a bigger wall) and seats it on the map. The selected screen's picture shows with rulers and its share of each placed layer, and **Test card** puts the alignment grid on it. The rules live in `Core/Outputs/ScreenMapEditing.cs`.
- **LED walls are no longer a layout.** As on the Mac, a placement in a processor's frame is where an output lands, not a shape: `OutputLayoutKind.LedWall` is gone, a placement doesn't change the layout, tiles are named "Slice N", and `SeedPlacement` puts a joining output under the ones already sharing a frame.

## 5. Streams and recordings

(Ported by a helper in a separate worktree, then picked into this branch.)

A church's upload is often the weakest link of a live stream. MxU's releases taught the Mac's streams two things, and Windows now does both.

- **A stalled upload reconnects by itself.** Every RTMP stream is measured once a second by an upload watch (`Broadcast/Streaming/UploadWatch.cs`). `RtmpPublisher` counts the bytes it hands to the network in 16 KB slices, so a slow upload shows bytes trickling out. After 10 silent seconds, or more than 20 seconds of stream waiting, the session drops the connection on purpose and the supervisor reconnects; the reason is kept as `LastDrop`.
- **Adaptive bitrate.** A stream is **Adaptive** (the default) or **Constant**, kept in the shared preset (`adaptBitrate`, `minBitrateKbps`). The Stream & Record window has a **Bitrate mode** row. In Adaptive mode a port of HaishinKit's network monitor and strategy (the Mac gets them from the library) lowers the encoder's bitrate to what really leaves the PC, never below the preset's lowest (2 Mbps unless chosen), and climbs back after 15 good seconds. The encoder takes the new rate between pictures (`H264Encoder.SetBitrate`).
- **Live network status.** The status line reads like "Network slow · sending 2.41 Mbps · quality lowered to 2.6 of 6.0 Mbps". A platform that says the broadcast has ended turns the chip red.
- **A/V sync and blackout diagnostics.** The heartbeats log the picture's seconds beside the sound's. A recording notes when the picture went black above the lower third, with the time in the file (`recording.black`), using `Core/Media/DropoutWatch.cs` and `Broadcast/Recording/SyncTestReader.cs`, on the recorder's own thread.
- `Broadcast/Rtmp/LoopbackRtmpServer.cs` is a tiny RTMP server on 127.0.0.1 that can act like a slow or stalled network, for the self-test.

## 6. Smaller things

- **Grid pictures age out** (`Rendering/ImageCache.cs`). Decoded pictures and video posters were kept for the whole session; a big library grew without end. They're now kept up to a memory budget (384 MB, posters a quarter of that), and the least recently used go first.
- **Lyric phrasing.** A short line that opens a phrase ("In desperation") keeps the chart's break, and a slide pays a little for ending on a line that leads on.
- **Scripture.** More translations are recognised (WEB, ASV, RSV, BSB…), a reference followed by a spaced dash leads its verse, and `ScriptureReference.Verses` cuts a passage at the verse numbers its reference names.
- **MIDI.** A *Take Over (Backup)* command on note 36 (the backup computer's feature wires it).
- **Merge Slides** (`Core/Lyrics/SlideBulkEdit.cs`, Core only): selected slides join into the first, their lyric boxes as new lines, chords and style runs moved down with them.
- **Theme placeholders.** `PlaceholderAnimations.BaseStyle` is public and reads the placeholder's font, colour and case before the theme's, for the inspector.
- `MediaCueRouting.DroppedAsNewSlide(id, kind)`; `RecordingLibrary.IsRecording` is gone, as on the Mac.

## Files ported

| Swift (`apps/mac/Packages`) | C# (`apps/windows/src`) |
|---|---|
| `PresenterCore/…/Timeline/VideoMarks.swift` | `MxU.Slides.Core/Media/VideoMarks.cs` |
| `SlideScene/…/ConfidenceScene.swift` (`VideoCountdown`) | `MxU.Slides.Core/Stage/ConfidenceInfo.cs` |
| `SlideScene/…/LinkedText.swift` | `MxU.Slides.Core/Stage/LinkedText.cs` |
| `PresenterCore/…/Library/PhotoSpots.swift` | `MxU.Slides.Core/Library/PhotoSpots.cs` |
| `PresenterCore/…/Library/StarterPacks/StarterPacks.swift` | `MxU.Slides.Core/Library/StarterPacks.cs`, `StarterPacks.Repair.cs`, `StarterPacks.Worship.cs`, `LibrarySeeder.cs` |
| `SlideScene/…/SlideSceneBuilder.swift` (photo spots, `cueItem`, `scaleAndBlur`) | `MxU.Slides.Core/Scene/SlideSceneBuilder.cs` |
| `RenderEngine/…/MediaLooks.swift` | `MxU.Slides.Core/Animation/MediaLooks.cs` |
| `RenderEngine/…/SceneModel.swift` (layer ids, custom and placed layers, `endFade`, `crossfadeLeaving`, `scaleAndBlur`) | `MxU.Slides.Core/Scene/SceneModel.cs`, `Animation/AnimatedItem.cs` |
| `RenderEngine/…/SceneTransitionEngine.swift` | `MxU.Slides.Core/Animation/SceneTransitionEngine.cs` |
| `RenderEngine/…/Compositor.swift` (crossfade stacks, placed pictures, `playbackRemaining`) | `MxU.Slides.Windows/Rendering/SceneView.Layers.cs`, `SceneView.cs` |
| `RenderEngine/…/AnimationEvaluator.swift`, `VideoPushEvaluator.swift`, `OutputScaling.swift` (optional layer kind) | `Animation/AnimationEvaluator.cs`, `VideoPushEvaluator.cs`, `Outputs/OutputScaling.cs` |
| `MediaEngine/…/MediaEngine.swift` (`playbackRemaining`) | `MxU.Slides.Windows/Rendering/MediaHub.cs` |
| `SlideScene/…/ShowState.swift`, `MediaTransportTarget.swift` (custom layers, `clear(layerID:)`, seeds) | `MxU.Slides.Core/Scene/ShowState.CustomLayers.cs`, `ShowState.cs`, `ShowState.Flow.cs`, `Media/MediaTransport.cs` |
| `OutputEngine/…/ScreenMap.swift`, `OutputManager.swift` (map, placements, dominant screen, `seedPlacement`) | `MxU.Slides.Core/Outputs/ScreenMap.cs`, `OutputConfiguration.ScreenMap.cs`, `OutputConfiguration.Feeds.cs`, `OutputLayout.cs` |
| `OutputEngine/…/OutputLayout.swift` | `MxU.Slides.Core/Outputs/OutputLayout.cs`, `ScreenEditing.cs` |
| `SlideScene/…/ThemeOverride.swift` (`BaseStyle`, a run's case) | `MxU.Slides.Core/Animation/PlaceholderAnimations.cs` |
| `SlideScene/…/SlideBulkEdit.swift` (`mergeSlides`) | `MxU.Slides.Core/Lyrics/SlideBulkEdit.cs` |
| `PresenterCore/…/Library/LyricPhrasing.swift` | `MxU.Slides.Core/Lyrics/LyricPhrasing.cs` |
| `PresenterCore/…/Library/ScriptureReference.swift` | `MxU.Slides.Core/Scripture/ScriptureReference.cs` |
| `PresenterCore/…/MIDIControl.swift` | `MxU.Slides.Core/Control/MidiControl.cs` |
| `PresenterCore/…/MediaCueRouting.swift` | `MxU.Slides.Core/Scene/MediaCueRouting.cs` |
| `PresenterCore/…/MediaImport/RecordingLibrary.swift` | `MxU.Slides.Broadcast/Library/RecordingLibrary.cs` |
| `StreamEngine/*`, `RenderEngine/…/DropoutWatch.swift`, `OutputEngine/…/Export/SyncTestReader.swift`, `Recording/Recorder.swift` | see section 5 |
| `Sources/OutputPresetsController.swift` (placements, custom layers) | `MxU.Slides.Core/Outputs/OutputPresets.cs`, `ViewModels/MainViewModel.ScreenMap.cs` |
| `Sources/ScreenMapEditor.swift`, `ScreenConfigurationView.swift` (Screen Map page), `OutputPicture.swift` (simplified) | `Views/ScreenMapWindow.cs`, `MxU.Slides.Core/Outputs/ScreenMapEditing.cs` |
| `Sources/MediaTransportController.swift` (`fireVideoActions`, countdown marks) | `ViewModels/MainViewModel.VideoMarks.cs`, `MainViewModel.Transport.cs` |
| `Sources/ThumbnailStore.swift` (cost-limited caches) | `MxU.Slides.Windows/Rendering/ImageCache.cs` |

The Mac's tests came along: `VideoMarksTests`, the new `LinkedTextTests`, `PhotoSpotsTests` (whole, the Make Slides cases joined in from the notes port) and `PhotoSpotSceneTests`, `MediaLooksTests`, the new `SceneTransitionEngineTests`, `ScreenMapTests`, `LayerPlacementRoutingTests`, the new `OutputLayoutTests`, `ThemeOverrideTests.baseStyleReadsThePlaceholderBeforeTheTheme`, `SlideBulkEditTests.testMergeSlides…`, `LyricPhrasingTests`, `ScriptureReferenceTests`, `MediaCueRoutingTests`, and the stream tests (section 5). Windows adds `CustomLayerTests`, `ScreenMapEditingTests`, `ImageCacheTests` and the starter-repair tests.

The self-test's **UpstreamCore** section checks photo spots and Scale and Blur (`upstream-photo-spots`, `upstream-scale-and-blur`), a dissolve mixing stacks (`upstream-dissolve-stacks`), the Screen Map across two screens (`upstream-screen-map-left`, `-right`, `upstream-screen-map-window`), video marks made in the Video Actions window (`upstream-video-actions-window`), firing, and the end fade (`upstream-video-marks`, `upstream-end-fade-before`, `upstream-end-fade-black`), and the streams (`upstream-streams-*`).

## For PORTING.md

**Ported line by line** (new rows)

| Swift | C# |
|---|---|
| `PresenterCore/…/Timeline/VideoMarks.swift` | `MxU.Slides.Core/Media/VideoMarks.cs` |
| `PresenterCore/…/Library/PhotoSpots.swift` | `MxU.Slides.Core/Library/PhotoSpots.cs` |
| `PresenterCore/…/Library/StarterPacks/StarterPacks.swift` (`addingLyricsDesigns`, `addingPhotoDesigns`, `shrinkingPoints`, `stayingPhotos`) | `MxU.Slides.Core/Library/StarterPacks.Repair.cs` |
| `RenderEngine/…/MediaLooks.swift` | `MxU.Slides.Core/Animation/MediaLooks.cs` |
| `RenderEngine/…/Compositor.swift` (`crossfadeStacks`, `placedPictures`, `drawPlaced`) | `MxU.Slides.Windows/Rendering/SceneView.Layers.cs` (WPF containers and brushes instead of Metal passes) |
| `SlideScene/…/ShowState.swift` (custom layers) | `MxU.Slides.Core/Scene/ShowState.CustomLayers.cs` |
| `OutputEngine/…/ScreenMap.swift`, `OutputManager.swift` (screen map, layer placements, `dominantScreen`, `seedPlacement`) | `MxU.Slides.Core/Outputs/ScreenMap.cs`, `OutputConfiguration.ScreenMap.cs`, `OutputLayout.cs` |
| `SlideScene/…/SlideBulkEdit.swift` (`mergeSlides`) | `MxU.Slides.Core/Lyrics/SlideBulkEdit.cs` |
| `Sources/MediaTransportController.swift` (`fireVideoActions`) | `MxU.Slides.Windows/ViewModels/MainViewModel.VideoMarks.cs` |
| `Sources/OutputPresetsController.swift` (`route`: placements, custom layers) | `OutputPresets.Apply`, `ViewModels/MainViewModel.ScreenMap.cs` |
| plus the stream rows in section 5's source doc: `StreamEngine/…/UploadWatch.swift` → `Broadcast/Streaming/UploadWatch.cs`; `StreamSession.swift`, `StreamHealth.swift`, `StreamConfiguration.swift` → `Broadcast/Streaming/*`; `RenderEngine/…/DropoutWatch.swift` → `Core/Media/DropoutWatch.cs`; `OutputEngine/…/Export/SyncTestReader.swift` (`gridLuma`, `isBlackAbove`) → `Broadcast/Recording/SyncTestReader.cs`; `Recording/Recorder.swift` → `Recording/Recorder.cs`; `Sources/BroadcastStatusChip.swift` (network line) → `Control/BroadcastStatus.cs`; `Sources/StreamRecordSettings.swift` (Bitrate mode) → `Views/BroadcastWindow.cs` | |

Update the existing `SlideScene/…/ThemeOverride.swift` row: `BaseStyle` is now public (`PlaceholderAnimations.BaseStyle`), with colour and case. Update the `SceneTransitionEngine.swift` row: layers are keyed by id, each runs on its own length, and a non-media dissolve sets `CrossfadeProgress` (keep the Phase 6 `Picture` case, which now also clears `CrossfadeProgress`).

**Re-implemented for Windows**

| Mac | Windows |
|---|---|
| `Sources/ScreenMapEditor.swift`, the Screen Map page of `ScreenConfigurationView.swift`, `OutputPicture.swift` | `Views/ScreenMapWindow.cs` over `Core/Outputs/ScreenMapEditing.cs` |
| `Sources/ThumbnailStore.swift` (NSCache cost limits) | `Rendering/ImageCache.cs` in `MediaHub` |
| `Sources/VideoActionsTimeline.swift` (`VideoActionsCard`'s list, `VideoActionAddMenu`), `MediaEditorView.swift` (`addMark`, `moveMark`, `removeMark`, rename, a mark's actions) | `Views/VideoActionsWindow.cs`, `Views/MainWindow.VideoMarks.cs`, `MainViewModel.SaveVideoMarks`, over `VideoMarks.Renaming` / `WithActions` / `Removing` / `Clock` / `ParseClock` |

**Windows-only**

- Photo designs in the worship looks (`StarterPacks.Worship.cs` → `PhotoDesigns`), and the starter seeder's gentle repair from revision 2 (`LibrarySeeder.RepairGently`).
- `ScreenMap` saved in `settings.json` (`AppSettings.ScreenMap`).
- `Infrastructure/SelfTest.UpstreamCore.cs`, `SelfTest.UpstreamStreams.cs`, `Broadcast/Rtmp/LoopbackRtmpServer.cs`; HaishinKit's `NetworkMonitor` and `AdaptiveBitRateStrategy` in `UploadWatch.cs`.

**Not ported yet**

- *Video marks:* the Mac's Video Actions *timeline* (`VideoActionsTimeline.swift` in `MediaEditorView.swift`: pins you drag under a preview, nudge by a frame, a mark's colour, undo); Windows edits marks as a list (`Views/VideoActionsWindow.cs`). Marks on the transport strip's scrub bar (`ScrubBar.swift`). The text inspector's countdown target and count-up controls (`SlideObjectInspector.swift`; the slide editor's port).
- *Custom layers:* the transport strip's layer picker doesn't list them (their videos show with Foreground Videos); the Clear Layer action's picker doesn't offer them (a Mac-made step that names one does run here: `IShowActions.ClearCustomLayer`); the Local API's clear by custom-layer id; the slide grid's layer chips (`SlideLayerFacts.swift`); transitions on a custom layer follow Foreground Videos' setting.
- *Screen Map:* the shared-outputs page ("Share it / Take it over"), `networkCarryFeed`, `restoreDisplayClaim`, the full output picture (Windows shows the screen's frame, rulers and placed layers, not its live picture), thumbnail chips with their hover, and dragging or resizing one placement by hand (the window places a layer with its starting points, **Each screen** or **Across**, and a **Fit / Fill** button).
- *Dissolves:* Windows fades the two stacks as containers; the Mac mixes them in a shader, so with two opaque stacks the middle is slightly darker here.
- *Media engine:* the live-input dropout note and the still-image tally; the sRGB encode pass of `OutputMirror` (Metal-only).
- *All Caps per selection* (`TextStyleRun.textTransform` in the scene and text layout): the slide editor's port.
- The stream items listed in section 5's notes: HaishinKit's frame-rate halving, HLS upload measurement, the mixer and DeckLink heartbeat summaries, "Check Sync…".
