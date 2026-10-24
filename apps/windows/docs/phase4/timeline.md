# Phase 4: the timeline editor

Notes for merging into `docs/HOW-IT-WORKS.md` and `docs/PORTING.md`. The first part is a
new section for HOW-IT-WORKS (it follows section 24, song timelines); then the PORTING rows,
the "not ported" notes, and the changes made outside the timeline's own files.

---

## HOW-IT-WORKS: 24b. Editing a timeline (Phase 4)

Section 24 is how a timeline *plays*. This is how you *edit* one: the Mac's timeline card,
as a window. Open a song, then **Timeline › ⋯ › Edit timeline…**. The window follows the
song open in the main window, so you can leave it open and pick another song.

### What you see

- **Lanes**, one row each: Slides, Background Media, Music always; Alerts, Overlays, Foreground
  Videos, Still Graphics, Video Input and Actions once they hold a cue (or **+ Add a layer**).
- **Track rows** for the song's stems (click, guide, keys…), folded into one bar
  ("12 stems") or one row per track (click **▸ Tracks**).
- A **ruler** along the top (click or drag it to move the playhead) and the song's
  **sections** as a coloured line under it.
- A **slide cue** is a pin with the slide's first words; a **block** (an overlay, a video,
  a music clip, a stem) spans its length. Music clips and stems show their **waveform**.
- The song's **transition** to the next service item (**Song › Transition to the Next
  Item**) is drawn as curves on the music: drag the blue dot to change how long it takes.

### What you can do

| Do this | And |
|---|---|
| **+** beside a lane | adds at the playhead: a slide, a background or video, an overlay, an alert, a video input, a music clip, an action |
| Drag a cue | moves it, on the 30 fps grid, snapping to other cues' edges and the playhead (hold **Alt** to drag freely) |
| Drag a block's right edge | changes how long it runs |
| Drag a block's left edge | trims its start (a music clip keeps its audio where it was: it reads further into the file) |
| Ctrl-click or Shift-click | picks several; drag one and they move together |
| Drag across empty lane | picks a stretch of time: **Copy** it, or **Ripple Delete** it (everything after moves up) |
| **←** **→** (Shift: a second) | nudges a frame; or type a time in the box |
| **Delete**, **Ctrl+B**, **Ctrl+C**, **Ctrl+V** | remove, split at the playhead, copy, paste at the playhead |
| **Ctrl+Z**, **Ctrl+Y** | undo and redo every edit |
| Right-click a music clip | fades (five curve shapes), crossfades where clips overlap or butt together, edge fades |
| Drag a fade's square handle / round handle | its length / how its curve bends |
| **Ctrl+wheel**, wheel, Shift+wheel | zoom, scroll along, scroll up and down |
| Space, Home | play/pause, go to the start |

### Stems (multitrack)

**+ beside Tracks › Import Audio Files…** (or **a Song's Folder…**, or drop files on the
track rows) copies the files into the library and puts each one on the track its name
matches: "EG 2.wav" goes on *Electric Guitar 2*, "Click.wav" on *Click*. Tracks are made
for names nothing matches; with tracks already set up, a dialog asks first. The files of
one download become a **track set**, so dropping the same folder again reuses them and the
stems remember their tracks. A MultiTracks.com download's `MultiTrack.als` becomes the
song's **section map** (**Song › Section Map…**): where each section is in the recording, so
every arrangement lays its audio out in its order.

**Song › Track Outputs and Interface…** says where this computer plays them: the audio
interface, how many of its outputs go to the sound board (each family, click, guide, drums,
keys…, then gets its own) and each track's **M**ute and **S**olo. Click and guide never go
to the house. This is per computer, kept in `tracks-setup.json` next to `settings.json`.

### How it's built

- **The rules are Core's**, pure functions you can test without a window:
  - `Automation/Timelines/TimelineEditing.cs`: which lanes show, what moves together, what a
    drag, nudge, delete, split, copy or paste does, fades and crossfades, snapping, new cues.
    Every edit returns a *new* list of cues, so the old one can be kept for undo.
  - `TimelineEditorLayout.cs`: the rows and cards in points, and **hit-testing** (which card
    and which edge is under the pointer). `TimelineCardDrag` is one press: it becomes a drag
    after three points, the edge grabbed picks move/stretch/trim, and the distance snaps.
  - `TimelineEditHistory.cs`: undo and redo. Before each edit the song's timelines and
    section map are copied; Undo puts the copy back. One history per song.
- **The Audio project** adds what needs it: cue labels (`Timeline/TimelineLabels.cs`), fade
  handles (`ClipFadeGeometry.cs`), the tracks' mixer (`TrackRouting.cs`), importing stems
  (`StemImport.cs`) and waveforms (`TimelineWaveformStore.cs`).
- **The window** is `Views/TimelineEditorWindow.cs` (+ `.Menus.cs`, `TimelineEditorDialogs.cs`).
  `Views/TimelineLanesView.cs` draws the lanes straight onto a `DrawingContext`, only the part
  in view, and turns the mouse into `PointerPressed/Moved/Released` on the view model; the
  playhead is a separate layer (`TimelinePlayheadLayer`) so it can move 30 times a second
  without redrawing everything under it.
- **The view model** `ViewModels/TimelineEditorViewModel.cs` (+ `.Input.cs`, `.Tracks.cs`)
  applies the rules to the library. Every edit goes through `EditTimeline`: copy for undo,
  change a copy of the timeline, save it through the `TimelinePlayer`. Edits wait while a take
  is recording (the take is writing the timeline as it plays).
- **Waveforms never block the screen.** The editor asks for a clip's waveform while drawing;
  the first time it gets nothing and the store works it out on its own thread, then the
  lanes draw again. Each file's peaks are kept on disk (`Waveforms` in the local folder).

Two things learnt while building it:

- **Starting music used to hold the slides up.** A new music engine opens the sound device on
  the thread that plays it: for a timeline that's the UI thread, and it took up to 0.8 s, so
  the slide cue just after the music fired late. Now the main mix's engine is opened in the
  background (`AudioController.Warm`) as soon as a song with music on its timeline is opened
  or played. The self-test checks the slides still fire on time with music.
- **Stems were silent on Windows** until now: the stems player had no mixer settings, so every
  track went nowhere. `TrackRouting` builds the mixer from the track list, and the tracks
  player gets it before each song.

The self-test (`Infrastructure/SelfTest.Timeline.cs`) opens the editor on the sample song,
adds a music clip and two slide cues, drags one, trims the clip, undoes and redoes, adds a
fade, plays and checks the slides fire at their new times, imports two stems, sets and drags
a transition, makes a section map and pictures every dialog (`20-…` to `31-…`).

### Things to try

1. **Drag a slide cue onto another.** It steps a frame at a time until it's clear: slide cues
   can't share a frame (`SteppingSlideCuesApart`). Then Ctrl+Z.
2. **Trim a music clip and watch its waveform.** The audio stays where it was; only where it
   starts changes (`TimelineClips.TrimmedStart`). Open the song's JSON in your *test* library:
   the cue gained an `inPoint`.
3. **Make two stems from any audio file.** Copy one file as `Click.wav` and `Keys.wav`, drop
   both on the track rows. `TimelineTracks.Plan` decides where each goes; read its tests.

---

## PORTING rows

| Swift | C# |
|---|---|
| `Sources/PresentationTimelineView.swift` (`PresentationTimelineLanes`: lanes, cards, add menus, clip menu, tracks rows, drop; `PresentationTimelineStartPopover`; `SongTransitionCurves`, `SongTransitionHandoverLine`; `TrackRowToggles`, `TrackRowResizeHandle`; `label`, `slideWords`) | `MxU.Slides.Core/Automation/Timelines/TimelineEditing.cs`, `TimelineEditorLayout.cs`; `MxU.Slides.Audio/Timeline/TimelineLabels.cs`; `MxU.Slides.Windows/ViewModels/TimelineEditorViewModel*.cs`, `Views/TimelineEditorWindow*.cs`, `Views/TimelineLanesView.cs`, `Views/TimelineEditorDialogs.cs` |
| `Sources/TimelineControls.swift` (`TimelineBlockGestures`, `TimelineBlockCard`, zoom and row-height controls, playhead layers, `TimelineEdgeAutoScroll`) | `TimelineEditorLayout.cs` (`TimelineCardDrag`), `Views/TimelineLanesView.cs`, `Views/TimelineEditorWindow.cs` |
| `Sources/TimelineClipEditing.swift` (`ClipFadesOverlay`, `ClipFadeShapeItems`, `TimelineRangeOverlay`, `TimelineClipKeyMonitor`) | `MxU.Slides.Audio/Timeline/ClipFadeGeometry.cs`, `TimelineEditing.VerbFor`, `Views/TimelineLanesView.cs`, `TimelineEditorWindow.Menus.cs` |
| `Sources/TimelineWaveforms.swift` (`WaveformStore`, `TimelineWaveform`) | `MxU.Slides.Audio/Timeline/TimelineWaveformStore.cs` (with the existing `WaveformCache`), `Views/TimelineLanesView.cs` (`DrawWaveform`) |
| `Sources/SongMapEditing.swift` (`SongMapImport`, `SongMapSheet`) | `MxU.Slides.Audio/Timeline/StemImport.cs` (`SongMapImport`), `TimelineEditorViewModel.Tracks.cs`, `Views/TimelineEditorDialogs.cs` (`SongMapWindow`) |
| `Sources/UnmatchedStemsSheet.swift` | `Views/TimelineEditorDialogs.cs` (`UnmatchedStemsWindow`) |
| `Sources/TracksSetupView.swift` (`TracksSetupGuide`), `Sources/TracksController.swift` (`routing`, `seedSends`, `familyBus`, `addChannel`, `editChannel`) | `MxU.Slides.Audio/Timeline/TrackRouting.cs`, `Views/TimelineEditorDialogs.cs` (`TrackOutputsWindow`), `MainViewModel.Timeline.cs` (`TrackRoutingFor`, `UpdateTrackChannels`) |
| `Sources/AppModel.swift` (`importTrackSet`, `importStems`, `trackChannels`, `trackSets`) | `MxU.Slides.Audio/Timeline/StemImport.cs` (`StemImporter`); track sets and the track list are library documents (`TrackSets/`, `TrackChannels/`) |
| `Sources/SongTransitionMenu.swift` (`SongTransitionMenuItems`) | `TimelineEditing.Choosing`, `Summary`, `Title`; `TimelineEditorWindow.Menus.cs` (Song menu) |
| `Sources/ActionEditors.swift` (`AddActionMenuItems`) | `Views/TimelineEditorDialogs.cs` (`TimelineActionsWindow.FillAddActions`) |

Windows-only additions to list under **Windows-only**:
- `Core/Automation/Timelines/TimelineEditHistory.cs` (the editor's own undo; the Mac uses the app's undo manager).
- `AudioController.Warm` / `AudioEngine.Prepare` (opening the sound device ahead of the music).

## "Not ported yet" — replace the Timeline editor bullet with

- **Timeline editor extras:** dragging library items onto the lanes (the main window's bins
  aren't drag sources yet: use **+** beside a lane, or drop files from Explorer onto the music
  lane or the tracks), the slide thumbnail that pops up over a slide cue (a tooltip names it),
  trackpad pinch (Ctrl+wheel zooms), MIDI Out and the other steps that need settings in the
  actions **+** menu (those come with the action editor), "Track Settings…" per track (Windows
  has no app-wide mixer: the tracks setup sets each family's output, and M/S each track), and
  the track-set list in the library (track sets are added from **+ beside Tracks › Add a Track Set**).
- **Animation timeline** (`AnimationTimelinePanel.swift`): that is the slide editor's builds
  timeline, not a song timeline; it isn't part of this editor.

## Faithful-port notes (where Windows differs on purpose)

- **Pins only move.** On the Mac, a slide or actions pin grabbed near its right edge reports a
  stretch, which `edited` then treats as a move anyway (and moves only that pin, not the
  selection). Here a pin press is always a move.
- **A trimmed clip's waveform** includes the cue's own in point, so it shows the audio it
  plays. The Mac's `TimelineWaveformSource.of` only uses the item's in point, so a trimmed or
  split clip draws the file's start.
- **A new music clip's length** is the item's out point less its in point (as the Windows ⋯
  menu already did); the Mac uses the file's whole duration.
- **Slide, media and music lanes have + menus.** The Mac adds those by dragging from the
  library; Windows has no library drag yet, so the + menus offer them.
- **Undo** covers the song's timelines and section map. A song's service transition, the
  track list and the tracks setup aren't undone (they are quick to set back from their menus).

## Changes outside the timeline's own files (all small and additive)

- `MxU.Slides.Core/Library/Documents.cs`, `Library/LibraryStore.cs`: `TrackSet` and
  `TrackChannelList` are library documents (folders `TrackSets/` and `TrackChannels/`), as
  on the Mac. Needed so stems can be made and remembered on Windows.
- `MxU.Slides.Audio/AudioEngine.cs`: `Prepare()` (opens the output now).
- `MxU.Slides.Audio/Control/AudioController.cs`: `Warm()` and a standby engine that
  `MakePlayer` uses; disposed with the controller.
- `MxU.Slides.Audio/Control/TimelineAdapters.cs`: `TimelineMusic.Prepare()`.
- `MxU.Slides.Windows/Views/MainWindow.xaml.cs`: the "Edit timeline…" entry in `TimelineMenu_Click` only.
- `MxU.Slides.Windows/Infrastructure/SelfTest.cs`: `partial`, and `await TimelineEditorCheck();` after `ProPresenterCheck`.
- `tests/MxU.Slides.Core.Tests/Automation/TimelinePlayerTests.cs`: the test host's `Music` became settable.
- `MxU.Slides.Windows/App.xaml.cs`: the single-copy lock is per data folder, and a self-test never waits on the "already running" box (the integrator's change, applied word for word so it merges cleanly).
