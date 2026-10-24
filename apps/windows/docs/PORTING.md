# Porting map: Mac (Swift) → Windows (C#)

The Windows app follows the Mac app, `MxU-Engineering/mxu-slides`. Its `main` is merged into this fork every day by `.github/workflows/sync-upstream.yml`, and should also be merged before starting any work. When a merge touches one of the Swift files below, compare it with its C# twin and port the change. Then run the unit tests and the self-test.

To see what came in from upstream since the last time you ported:

```powershell
git log --stat <last-ported-commit>..upstream/main -- apps/mac packages/schema
```

Paths are relative to `apps/mac/Packages` for Swift and `apps/windows/src` for C#.

## Shared directly (no porting needed)

| Mac | Windows | How |
|---|---|---|
| `packages/schema/documents.json` → `PresenterCore/Generated/Models.swift` | `MxU.Slides.Core/Generated/Models.cs` | `node apps/windows/tools/codegen-csharp.mjs` (CI fails if you forget) |
| `apps/mac/Assets.xcassets/AppIcon.appiconset` | `MxU.Slides.Windows/Assets/AppIcon.ico` | `node apps/windows/tools/make-icon.mjs` |

## Ported line by line

| Swift | C# |
|---|---|
| `PresenterCore/…/Import/LyricTextImport.swift` | `MxU.Slides.Core/Lyrics/LyricTextImporter.cs` |
| `PresenterCore/…/Library/Reflow.swift` | `MxU.Slides.Core/Lyrics/Reflow.cs` |
| `PresenterCore/…/Library/LyricPhrasing.swift` | `MxU.Slides.Core/Lyrics/LyricPhrasing.cs` |
| `PresenterCore/…/Library/ChordMath.swift` | `MxU.Slides.Core/Lyrics/ChordMath.cs` |
| `PresenterCore/…/Import/ChordChartText.swift` | `MxU.Slides.Core/Lyrics/ChordChartText.cs` |
| `PresenterCore/…/Import/ChordProExport.swift` | `MxU.Slides.Core/Lyrics/ChordProExport.cs` |
| `SlideScene/…/SlideSceneBuilder.swift` | `MxU.Slides.Core/Scene/SlideSceneBuilder.cs`, with the animation half in `Animation/AnimatedItems.cs` |
| `SlideScene/…/ShowState.swift` | `MxU.Slides.Core/Scene/ShowState.cs` (no theme-override "looks" yet) |
| `RenderEngine/…/SceneModel.swift` | `MxU.Slides.Core/Scene/SceneModel.cs` |
| `PresenterCore/…/Documents/SlideObjectNormalization.swift` | `MxU.Slides.Core/Scene/SlideObjectNormalization.cs` |
| `PresenterCore/…/MediaCueRouting.swift` | `MxU.Slides.Core/Scene/MediaCueRouting.cs` |
| `SlideScene/…/ConfidenceScene.swift` | `MxU.Slides.Core/Stage/TimerSnapshot.cs`, `ConfidenceInfo.cs`, `ConfidenceSceneBuilder.cs`, `Timecode.cs` |
| `SlideScene/…/LinkedText.swift` | `MxU.Slides.Core/Stage/LinkedText.cs` |
| `SlideScene/…/TimerPattern.swift` | `MxU.Slides.Core/Stage/TimerPattern.cs` |
| `SlideScene/…/VisibilityRules.swift` | `MxU.Slides.Core/Stage/VisibilityRules.cs` |
| `PresenterCore/…/AlertTokens.swift` | `MxU.Slides.Core/Stage/AlertTokens.cs` |
| `PresenterCore/…/Library/ConfidenceLayoutTemplate.swift` | `MxU.Slides.Core/Stage/ConfidenceLayoutTemplate.cs` |
| `PresenterCore/…/Library/StarterPacks/StarterPacks.swift` | `MxU.Slides.Core/Library/StarterPacks.cs` (themes, designs, overlays and their animation steps) |
| `PresenterCore/…/Library/StarterPacks/WelcomeDeck.swift` | `MxU.Slides.Core/Library/StarterContent.cs` (wording adapted for Windows) |
| `PresenterCore/…/Library/GroupPaletteLogic.swift` (defaults) | `StarterContent.GroupPalette` |
| `PresenterCore/…/KeyboardControl.swift` (default keys) | `MainWindow.xaml.cs` → `OnPreviewKeyDown` |
| `SlideScene/…/AnimationSequence.swift`, `AnimationTimelineLayout.swift`, `PlaceholderAnimations.swift`, `Paging.swift` | `MxU.Slides.Core/Animation/` (same names) |
| `RenderEngine/…/AnimationEvaluator.swift`, `AnimationTimeline.swift`, `BlockScrollRoll.swift`, `VideoPushEvaluator.swift`, `SceneTransitionEngine.swift` | `MxU.Slides.Core/Animation/` (same names; `AnimatedItem.cs` carries the Mac `RenderItem` fields the C# one lacks) |
| `SlideScene/…/EditorGeometry.swift` | `MxU.Slides.Core/Scene/EditorGeometry.cs` |
| `SlideScene/…/HotKeyJump.swift` | `MxU.Slides.Core/Scene/HotKeyJump.cs` |
| `PresenterCore/…/MIDIControl.swift` | `MxU.Slides.Core/Control/MidiControl.cs` |
| `LocalAPI/Sources/LocalAPI/*` | `MxU.Slides.LocalApi/*` (Kestrel instead of Network.framework) |
| `Sources/LocalAPIBridgeAdapter.swift` | `MxU.Slides.Windows/ViewModels/MainViewModel.Api.cs` |
| `PPTXImport/Sources/PPTXImport/*` | `MxU.Slides.PptxImport/*` |
| `ProImport/Sources/ProImport/*` | `MxU.Slides.ProImport/*` |
| `OutputEngine/…/{OutputLayout,OutputSliceState,OutputTopology}.swift`, the configuration half of `OutputManager.swift`, `RenderEngine/…/{OutputAdjust,OutputMask,OutputScaling}.swift`, `Sources/RenderContext.swift` (saved setup), `OutputPresetsController.swift` | `MxU.Slides.Core/Outputs/*` |
| `RenderEngine/…/ShaderSource.swift` (`output_adjust_fragment` and the effect shaders) | `MxU.Slides.Windows/Rendering/OutputAdjustEffect.cs`, `ItemEffect.cs` (HLSL) |
| `SlideScene/…/SlideSceneBuilder.swift` `sceneEffects` | `MxU.Slides.Core/Animation/AnimatedItem.cs` → `AnimationEffect.From` |
| `NDIKit/*`, `DeckLinkKit/*`, `MediaEngine/…/{CaptureSource,CaptureFormatChoice,LiveInputDelay}.swift`, `Sources/{VideoInputInventory,DeckLinkOutputController}.swift` | `MxU.Slides.VideoIO/*` |
| `AudioEngine/*`, `PresenterCore/…/Library/{PlaylistCrossfade,MixerStripLayout}.swift`, `Timeline/{TrackChannels,TrackSets,Waveforms}.swift` | `MxU.Slides.Audio/*` |
| `Sources/{ActionRouter,SchedulerController,TimecodeInput,PresentationTimelineController}.swift`, `PresenterCore/…/Library/{ControlBoardLogic,ScheduleMath,SchedulerBoardLogic,AutomaticTimer}.swift`, `PresenterCore/…/Timeline/{LTCDecoder,MTCDecoder,TimecodeFollow,TimecodeRecording,PresentationTimelines,TimelineClips,TimelineGeometry,TimelineTracks,SongMaps,SongTransitions}.swift`, `PresenterCore/MediaAutoAdvance.swift` | `MxU.Slides.Core/Automation/*` |
| `StreamEngine/*` (sessions, RTMP via HaishinKit, HLS, health), `OutputEngine/…/Recording/*`, `PresenterCore/…/{StreamDestinations,StreamPresets}.swift`, the non-UI half of `Sources/{StreamingController,RecordingController,BroadcastStatusChip}.swift` | `MxU.Slides.Broadcast/*` |
| `Sources/StreamingView.swift`, `RecordingView.swift` | `MxU.Slides.Windows/Views/BroadcastWindow.cs`, `ViewModels/MainViewModel.Broadcast.cs` |
| `Sources/PresentationTimelineView.swift` (transport and record), `TimecodeSettingsView.swift` | `MxU.Slides.Windows/ViewModels/MainViewModel.Timeline.cs`, the timeline bar in `Views/MainWindow.xaml`, the Timecode section of `Views/SettingsWindow.xaml` |
| `OutputEngine/…/Export/OutputMirror.swift` | `MxU.Slides.Windows/Outputs/ProgramFeed.cs`, `HiddenCapture.cs` (window capture instead of Metal readback) |

The Mac tests for these files are ported too, in `apps/windows/tests/` (one test project per source project). When you port a change, port its test.

## Re-implemented for Windows (same behaviour, different code)

| Mac | Windows |
|---|---|
| `RenderEngine/TextRasterizer.swift` (Core Text) | `MxU.Slides.Windows/Rendering/TextLayout.cs` (WPF `FormattedText`) |
| `RenderEngine/Compositor.swift`, `ShapeRasterizer.swift` (Metal) | `MxU.Slides.Windows/Rendering/SceneView.cs`, `Painting.cs` (animation motion as WPF transforms, clips and opacity masks) |
| `Sources/MIDIInService.swift` (CoreMIDI) | `MxU.Slides.Windows/Infrastructure/MidiInputService.cs` (`Windows.Devices.Midi`) |
| Keychain (default API key) | DPAPI, in `MainViewModel.ApiServer.cs` |
| `NSAttributedString(rtf:)` (ProPresenter text) | `MxU.Slides.ProImport/Rtf/RtfReader.cs` |
| `ditto` (unzipping imports) | `System.IO.Compression` with safety checks, in `PptxArchive.cs` and `ImportArchive.cs` |
| `MediaEngine` (AVFoundation) | `MxU.Slides.Windows/Rendering/MediaHub.cs` (Media Foundation via `MediaPlayer`) |
| `OutputEngine` (windows, runtime), `Sources/ScreenConfigurationView.swift`, `OutputsSection.swift` | `MxU.Slides.Windows/Outputs/*`, `Views/ScreensWindow.*`, `Infrastructure/Displays.cs` |
| `PresenterCore/MediaImport/BlobStore.swift` + Automerge library | `MxU.Slides.Core/Library/LibraryStore.cs` (JSON files; same documents, different container) |
| `Sources/*.swift` (SwiftUI screens) | `MxU.Slides.Windows/Views` + `ViewModels` |
| `RenderEngine/…` font names (Core Text) | `MxU.Slides.Windows/Rendering/FontMap.cs` (maps Mac names to installed Windows fonts) |

## Windows-only

- `Lyrics/SongEditing.cs`: editing a song as text, and play orders like `V1, C x2`.
- `Infrastructure/SelfTest.cs`
- `Infrastructure/StarterBackgrounds.cs`
- `scripts/*.ps1`

## Not ported yet

- **Timeline editor:** timelines play, record takes, follow timecode and carry a song's music, but cues can't be dragged on a lane view yet, and stems can't be added on Windows (they play when the Mac made them).
- **Action combo and schedule editors:** combos and triggers run (and import from ProPresenter), but there's no editor for them on Windows yet.
- **Output mask editor** (masks imported from ProPresenter apply; drawing new ones isn't there yet).
- **Theme-override looks** (`slideThemeOverrides`, held looks and hand-offs in `ShowState.swift`) and **text paging** (the renderer doesn't supply a `TextPager` yet, so long text shrinks instead of turning pages).
- **Per-item slide transitions** (`SceneTransitionEngine` is ported and tested, but outputs still use the app-wide cut/dissolve/fade).
- **Echo and ghost trails** (they need the previous frame); every other effect is drawn on the GPU.
- **Overlay editor** (overlays can be fired and their fields typed, not redesigned).
- **ProPresenter workspace extras** not yet applied: MIDI devices, screen and look plans, timers and inputs (macros and the calendar are saved as combos and triggers).
- **Broadcast extras:** SRT, HDR/HEVC streaming, ProRes, streaming a media file ("replay"), per-screen stream canvases, audio mixes and inputs for streams, platform sign-in (YouTube/Facebook accounts); streams use a server URL and key.
- **Sync and backups:** reading the Mac's Automerge library folder directly. Today the two apps share documents, not the library folder itself.
