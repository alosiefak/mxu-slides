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
| `SlideScene/…/ShowState.swift` | `MxU.Slides.Core/Scene/ShowState.cs` (looks, held looks, hand-offs, look switches and dismissals, paging, page turns) |
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
| `SlideScene/…/ThemeOverride.swift` | `MxU.Slides.Core/Animation/PlaceholderAnimations.cs` → `OverrideObjects` (font traits from the name; see below) |
| `PresenterCore/…/Library/PerSlideTheme.swift` (`rendered(throughOverride:)`) | `PlaceholderAnimations.RenderedThroughOverride` |
| `PresenterCore/…/ThemeSlideScope.swift` | `MxU.Slides.Core/Scene/ThemeSlideScope.cs` |
| `SlideScene/…/TransitionEdit.swift` | `MxU.Slides.Core/Animation/TransitionEdit.cs` |
| `Sources/RenderContext.swift` (`sceneTransition(from:)`, `roomDefault(for:)`, the variant engines), `Sources/ServiceControls.swift` (`nextLiveTransitions`, hand-offs, `switchPreset`, `apply()`'s variants and page turn) | `MxU.Slides.Core/Animation/LiveTransitions.cs`, `MxU.Slides.Windows/ViewModels/MainViewModel.Live.cs`, `Rendering/SceneView.cs` (`PresentLayered`) |
| `RenderEngine/…/TextRasterizer.swift` (`pages(for:sceneFrame:)`) | `MxU.Slides.Windows/Rendering/TextLayout.cs` → `Pages` |
| `RenderEngine/…/Compositor.swift` (effect history), `ShaderSource.swift` (`compositor_echo`, `compositor_ghost_trails`) | `MxU.Slides.Core/Animation/EffectTrail.cs`, `MxU.Slides.Windows/Rendering/TrailStore.cs`, `TrailEffect.cs` |
| `Sources/OutputPresetsController.swift` (slide-theme routing), `Sources/ScreenConfigurationView.swift` (`ThemeLookPicker`) | `MxU.Slides.Core/Outputs/OutputConfiguration.Looks.cs`, `OutputPresets.cs`; `MxU.Slides.Windows/Views/ScreensWindow` (Look) |
| `Sources/ActionEditors.swift` (ActionParameterFields, AddActionMenuItems, ActionsMenu: the rules), `Sources/LiveInputCatalog.swift` | `MxU.Slides.Core/Automation/Actions/ActionEditing.cs` |
| `Sources/SchedulerView.swift` (ScheduleConditionFormat, conditionSummary, the row, chip, condition and sheet logic) | `MxU.Slides.Core/Automation/Scheduling/ScheduleConditionFormat.cs`, `ScheduleEditing.cs` |
| `Sources/AppModel.swift` (createActionCombo, comboBoard, updateComboBoard, createScheduleTrigger, duplicateScheduleTrigger, duplicateScheduleFolder, deleteScheduleTrigger, schedulerBoard, updateSchedulerBoard) | `MxU.Slides.Core/Automation/AutomationLibrary.cs` |
| `SlideScene/…/PathAnchors.swift` | `MxU.Slides.Core/Scene/PathAnchors.cs` (`PathAnchor`, `PathAnchorCodec.Parse/Encode/EncodeAbsolute`) |
| `SlideScene/…/ShapeOutlineSVG.swift` | `MxU.Slides.Core/Scene/ShapeOutlineSvg.cs` (`ShapeOutlineSvg.PathData`) |
| `PresenterCore/…/Library/TeamDrives.swift` (`isUnder`, `childCounts`, `rebased`, `renamed`) | `MxU.Slides.Core/Library/FolderPaths.cs` |
| `Sources/AppModel.swift` (overlays: `createEntity`, `duplicate`, `rename`, `renameFolder`, `moveToFolder`) | `MxU.Slides.Core/Library/OverlayLibrary.cs` |
| `Sources/SlideEditorModel+Animations.swift` (step editing, sentences, timing labels, speeds), the names in `AnimationInspector.swift` | `MxU.Slides.Core/Animation/AnimationEditing.cs` |
| `Sources/SlideObjectInspector.swift` (mask section: `maskCandidates`, `isMatte`, `maskObjectBinding`) | `MxU.Slides.Core/Scene/ObjectMasks.cs` |
| `Sources/MaskEditor.swift` (`MaskPenNSView` and the library edits in `OutputMaskEditor`) | `MxU.Slides.Core/Outputs/OutputMaskEditing.cs` (`OutputMaskPen`, `OutputMaskLibrary`) |
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
| `Sources/WorkspaceImport.swift` (`run`: timers, inputs, screens, corrections, slices, masks, stage assignments, looks) | `MxU.Slides.ProImport/ProWorkspacePlans.cs`, `ViewModels/MainViewModel.Import.cs` |
| `PresenterCore/…/Library/GroupPaletteLogic.swift`, the GroupPalette part of `KeyboardControl.swift`, `ImportLedgerLogic.swift` (`applyImportedHotKeys`) | `MxU.Slides.Core/Library/GroupPalettes.cs`, `MxU.Slides.ProImport/ProGroupHotKeys.cs` |
| `PresenterCore/MIDIControl.swift` (`ResolvedMIDIDevice`, `MIDIDeviceResolver`), `Sources/MIDIDeviceInventory.swift` | `MxU.Slides.Core/Control/MidiDevices.cs`, `ViewModels/MainViewModel.MidiDevices.cs` |
| `Sources/StreamingController.swift`, `RecordingController.swift` (per-screen mirrors, the file source, the session audio feed) | `MxU.Slides.Broadcast/Control/StreamingController.cs`, `RecordingController.cs`, `CaptureSources.cs` |
| `OutputEngine/…/Export/MediaFileSource.swift` | `MxU.Slides.Broadcast/Media/MediaFileSource.cs` (Media Foundation source reader instead of AVAssetReader) |
| `PresenterCore/…/MediaImport/RecordingLibrary.swift` | `MxU.Slides.Broadcast/Library/RecordingLibrary.cs` |
| `Sources/ServiceControls.swift` (libraryAdoption, resolveStreamSource, resolveSourceItem, fileSource, captureSourcePreview, the row detail's summaries) | `MxU.Slides.Broadcast/Control/BroadcastController.cs` (`FileRecordingsIntoLibrary`, `CapturePreview`), `Library/StreamSources.cs` |
| `Sources/ProgramAudioSource.swift` (`SessionAudioFeed`, `ProgramAudioSummer`) | `MxU.Slides.Audio/SessionAudioFeed.cs`; the backends are in `MxU.Slides.Windows/ViewModels/MainViewModel.Broadcast.cs` |
| `Sources/AudioInputInventory.swift` (`Entry`, `AudioInputSource`) | `MxU.Slides.Audio/AudioInputInventory.cs` |
| `Sources/AudioMixerController.swift` (feed hubs: addFeedConsumer, removeFeedConsumer, syncMixFeeds), `AudioMixInventory.swift` (the main mix) | `MxU.Slides.Audio/AudioMixFeeds.cs` |
| `Sources/AudioSourceMenuItems.swift` | the Sound picker in `Views/BroadcastWindow.cs`, `MainViewModel.AudioSourceTitle` |
| `Sources/StreamRecordPane.swift` (preset detail: Source, Recording), `StreamRecordSettings.swift` (sizes, frame rates) | `MxU.Slides.Windows/Views/BroadcastWindow.cs` |
| `OutputEngine/…/Export/OutputMirror.swift` with `previewProvider(for:)` (one screen) | `MxU.Slides.Windows/Outputs/ScreenFeeds.cs` |

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
| `Sources/ActionEditors.swift` (ActionListEditor, SlideActionsSheet), `LibraryPickerSheet` | `MxU.Slides.Windows/Views/ActionListEditor.cs`, `SlideActionsWindow.cs`, `LibraryPickerWindow.cs`, `EditorKit.cs`, `EditorStyles.xaml` |
| `Sources/Modules/ActionCombosModule.swift`, `Modules/ControlBoardFolders.swift` | `MxU.Slides.Windows/Views/ActionCombosWindow.cs` (a window, not a sidebar module) |
| `Sources/SchedulerView.swift` (the SwiftUI views, SnoozeSheet, EndDateSheet, SchedulerHistorySheet) | `MxU.Slides.Windows/Views/SchedulerWindow.cs`, `SchedulerDialogs.cs` |
| (the editors' app glue in AppModel / ServiceControls) | `MxU.Slides.Windows/ViewModels/MainViewModel.ActionEditors.cs` |
| `Sources/SlideEditorView.swift` / `SlideEditorModel.swift` with an overlay host, `Modules/OverlaysModule.swift` (Layer menu, Edit in Library) | `MxU.Slides.Windows/Views/OverlayEditorWindow.*`, `ViewModels/OverlayEditorViewModel*.cs`; `MxU.Slides.Core/Animation/OverlayPreview.cs` (still and animated pictures) |
| `Sources/AnimationInspector.swift` (gallery, sequence list, step strip and editor) | `ViewModels/OverlayEditorViewModel.Animation.cs`, `ViewModels/AnimationStepViewModel.cs` |
| `Sources/SlideObjectInspector.swift` (as a reusable panel) | `Views/ObjectInspectorPanel.xaml` over `ViewModels/ObjectInspectorViewModel.cs` |
| `Sources/MaskEditor.swift` (SwiftUI and AppKit) | `Views/MaskEditorWindow.cs` |

## Windows-only

- `Lyrics/SongEditing.cs`: editing a song as text, and play orders like `V1, C x2`.
- `Infrastructure/SelfTest.cs`
- `Infrastructure/StarterBackgrounds.cs`
- `scripts/*.ps1`

- `Core/Automation/BoardSteps.cs` (Move up / Move down, the Folder picker) and the typed dates and times in `ScheduleEditing.cs` (the Mac uses date pickers); `ScheduleEditing.NextFireLine`.
- `Core/Automation/Timelines/TimelineEditHistory.cs` (the timeline editor's own undo; the Mac uses the app's undo manager), and `AudioController.Warm` / `AudioEngine.Prepare` (opening the sound device before the music starts).
- `Core/Threading/` (`WorkQueue`, `ShowTimeProvider`) and `MxU.Slides.Audio/AudioThreads.cs`: show timing off .NET's shared thread pool.
- The software copy in `Outputs/HiddenCapture.cs` for PCs where Windows' capture delivers nothing, and `Core/Outputs/CaptureTiles.cs` (a capture bigger than the main screen is drawn in screen-sized pieces).
- `Broadcast/Media/ReplayPacer.cs`: a replay keeps showing pictures on a PC that decodes slower than the file plays (the Mac only drops late pictures).
- `Infrastructure/SelfTest.{Render,Editors,Timeline,Overlays}.cs`.

## Not ported yet

- **Timeline editor extras:** dragging library items onto the lanes (the main window's bins
  aren't drag sources yet: use **+** beside a lane, or drop files from Explorer onto the music
  lane or the tracks), the slide thumbnail that pops up over a slide cue (a tooltip names it),
  trackpad pinch (Ctrl+wheel zooms), MIDI Out and the other steps that need settings in the
  actions lane's quick **+** menu (add them with **Edit Actions…**, which is the full step editor), "Track Settings…" per track (Windows
  has no app-wide mixer: the tracks setup sets each family's output, and M/S each track), and
  the track-set list in the library (track sets are added from **+ beside Tracks › Add a Track Set**).
- **Animation timeline** (`AnimationTimelinePanel.swift`): that is the slide editor's builds
  timeline, not a song timeline; it isn't part of this editor.
- **Automation editor extras:** dragging on the combo and scheduler boards (Windows has Move up/down and a Folder picker), dropping library items onto a combo or the scheduler board, undo for board moves and trigger renames, the "Edit…" button beside a step's media or music, run-only mode, and the capture-source preview line on scheduler rows. Audio-input steps show and keep their settings, but Windows offers no audio inputs to pick (the router skips those steps).
- **Overlay editor extras:**
  - *Morph ("Change") steps* can't be made yet, because their end state is posed on the Mac's canvas (`AnimateCanvasOverlay.swift`). Existing ones play, re-time and delete. *Where it enters from* (a custom start pose) is the same.
  - The *animation timeline* (`AnimationTimelinePanel.swift`), *animation presets*, and *one step per line/word* for text aren't there. Steps reorder with Earlier/Later and Move to, not drag and drop.
  - *Camera push* can be turned on and its mode chosen; its alignment, margin, zoom and blur settings aren't shown.
  - *Undo* is one step back: "Undo my changes to this overlay" restores it as it was when the editor first showed it.
  - The *Overlays module's* pinned/recent views, grid/list switch and search.
- **Mask editor:** the Mac shows it inside the screen's settings. Here it is its own window, opened from the Screens window.
- **Looks, Windows differences:** a screen can have its own look (the Mac sets looks only in output presets). There is no output preset editor on Windows yet, so preset looks come from imported or Mac-made presets. The Mac's grid tile "N pages" strip and look thumbnails (`PosterLookImage`, `ThumbnailStore.posterLook`) aren't ported, and neither are `SlideSceneBuilder.lookKey/withoutLayerMedia/posterLookScene` (they serve those thumbnails). `PosterLookSweepTests.swift` checks Mac UI source and has no Windows twin.
- **Bold and italic in looks** are read from the font's name (`PlaceholderAnimations.FontTraits`), not from the font file as Core Text does.
- **Echo and ghost trails** use a small (≤ 384 px) software picture for the history; effects listed before them that need pixel shaders aren't in that picture (the live item still has them). Trails on adjustment layers ("effects apply below") aren't drawn, as no adjustment-layer effects are on Windows yet.
- **Broadcast extras:** SRT (the Mac uses libsrt through SRTHaishinKit, a native library; Windows would need that binary shipped, or a complete managed SRT with its handshake, retransmission and AES encryption, so an `srt://` destination fails at once with "SRT isn't available on Windows yet" while the preset's other destinations carry on); HDR/HEVC streaming and ProRes (no Windows encoder path; HEVC/HDR destinations stream SDR H.264, ProRes presets record H.264); platform sign-in (YouTube/Facebook OAuth needs the church's own API credentials), so streams use a server URL and key. The Mac's mixer (sound inputs, video sound and the multitrack sent into mixes, per-mix stream gain, extra mixes) isn't on Windows, so a mix carries the music buses only, and the only mix is the main one. The "Check Sync…" sheet isn't ported.
- **Sync with the Mac's library folder** (Automerge). The Mac uses Automerge's Rust core through a prebuilt Apple-only binary. Windows needs the same core built for Windows, which means installing the Rust toolchain to build it and shipping a native DLL. That needs a decision about installing the toolchain and shipping the DLL; until then the two apps share documents, not the library folder.
