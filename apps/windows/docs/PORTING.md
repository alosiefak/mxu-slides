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
| `PresenterCore/…/KeyboardControl.swift` (`KeyChord`, `KeyCommand`, `KeyCommandMap`, `GeneratedKey`) | `MxU.Slides.Core/Control/KeyboardControl.cs` (⌘ → Ctrl, ⌥ → Alt; Windows' own commands and defaults; `ReservedReason`, `FiresWhileTyping`). `Views/MainWindow.Keys.cs` is the only key handler |
| `SlideScene/…/AnimationSequence.swift`, `AnimationTimelineLayout.swift`, `PlaceholderAnimations.swift`, `Paging.swift` | `MxU.Slides.Core/Animation/` (same names) |
| `RenderEngine/…/AnimationEvaluator.swift`, `AnimationTimeline.swift`, `BlockScrollRoll.swift`, `VideoPushEvaluator.swift`, `SceneTransitionEngine.swift` | `MxU.Slides.Core/Animation/` (same names; `AnimatedItem.cs` carries the Mac `RenderItem` fields the C# one lacks). `SceneTransitionEngine.cs` has an extra kind, `SceneTransitionKind.Picture`, and `AnimatedLayer.Mix` (Phase 6 transitions): a port of a change to `composite` must keep the `Picture` case and the interrupted-mix flattening at the top of `Composite`. Since MxU's autumn 2026 updates, layers are keyed by id, each runs on its own length, and a dissolve on a layer that isn't media sets `CrossfadeProgress` (the `Picture` case clears it) |
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
| `Sources/{ActionRouter,SchedulerController,TimecodeInput,PresentationTimelineController}.swift`, `PresenterCore/…/Library/{ControlBoardLogic,ScheduleMath,SchedulerBoardLogic,AutomaticTimer}.swift`, `PresenterCore/…/Timeline/{LTCDecoder,MTCDecoder,TimecodeFollow,TimecodeRecording,PresentationTimelines,TimelineClips,TimelineGeometry,TimelineTracks,SongMaps,SongTransitions}.swift`, `PresenterCore/MediaAutoAdvance.swift` | `MxU.Slides.Core/Automation/*`. **Keep the Phase 6 production hook when porting:** `ExtraSteps?.OwnerRan(…)` in SlideDidFire, MediaDidFire, Fire, RunStartupCombos and the FireCombo step, `ExtraSteps?.CancelPending()` in CancelPendingDelayedActions, and the trigger's and cue's id passed by `ISchedulerHost.Run` and `ITimelineHost.RunActions` |
| `StreamEngine/*` (sessions, RTMP via HaishinKit, HLS, health), `OutputEngine/…/Recording/*`, `PresenterCore/…/{StreamDestinations,StreamPresets}.swift`, the non-UI half of `Sources/{StreamingController,RecordingController,BroadcastStatusChip}.swift` | `MxU.Slides.Broadcast/*` |
| `Sources/StreamingView.swift`, `RecordingView.swift` | `MxU.Slides.Windows/Views/BroadcastWindow.cs`, `ViewModels/MainViewModel.Broadcast.cs` |
| `Sources/PresentationTimelineView.swift` (transport and record), `TimecodeSettingsView.swift` | `MxU.Slides.Windows/ViewModels/MainViewModel.Timeline.cs`, the timeline bar in `Views/MainWindow.xaml`, the Timecode section of `Views/SettingsWindow.xaml` |
| `OutputEngine/…/Export/OutputMirror.swift` | `MxU.Slides.Windows/Outputs/ProgramFeed.cs`, `HiddenCapture.cs` (window capture instead of Metal readback) |
| `Sources/ServiceControls.swift` (autoAdvance*, armAutoAdvance, syncAutoAdvanceArming, autoAdvancePollTick, fireAutoAdvance, fireNextServiceItem, fireServiceItem, armMediaAdvance, mediaAdvancePollTick, fireMediaAdvance, setAutoAdvancePaused, clear(function:), clear(layer:), clearAll, hasContent) | `MxU.Slides.Core/Automation/AutoAdvanceController.cs`; `ViewModels/MainViewModel.AutoAdvance.cs`, `MainViewModel.ServiceMusic.cs`, `MainViewModel.Clear.cs` |
| `Sources/AutoAdvanceChip.swift` | `Views/LiveShowControls.xaml` (the chip), `MainViewModel.AutoAdvance.cs` |
| `SlideScene/…/SlideSceneBuilder.swift` (autoAdvanceTarget, effectiveAutoAdvance) | `MxU.Slides.Core/Scene/SlideSceneBuilder.AutoAdvance.cs` |
| `SlideScene/…/ShowState.swift` (slideStepsCompletedAt, ShowFunction names, ClearAllScope, TracksClear) | `MxU.Slides.Core/Scene/ShowState.Flow.cs`, `Scene/ClearAllScope.cs` |
| `SlideScene/…/MediaTransportTarget.swift`, `MediaEngine/…/MediaTransport.swift` | `MxU.Slides.Core/Media/MediaTransport.cs`, `ShowState.Flow.cs` (TransportSeeds) |
| `Sources/MediaTransportController.swift`, `MediaTransportStrip.swift`, `ScrubBar.swift` | `ViewModels/MainViewModel.Transport.cs`, `Views/LiveShowControls.xaml(.cs)` |
| `Sources/ClearChip.swift`, `PresentCommands.swift` (the clears) | `Views/LiveShowControls.xaml(.cs)`, `ViewModels/MainViewModel.Clear.cs` |
| `SlideScene/…/ServiceTrackingTimers.swift` | `MxU.Slides.Core/Stage/ServiceTrackingTimers.cs` |
| `Sources/ServiceTimingController.swift`, `Modules/ServiceTrackingModule.swift` | `MxU.Slides.Core/Stage/ServiceTiming.cs`; `ViewModels/MainViewModel.ServiceTracking.cs`, `Views/ServiceTrackingPanel.xaml` |
| `PresenterCore/…/Library/ServiceRunOrder.swift` | `MxU.Slides.Core/Library/ServiceRunOrder.cs` |
| `PresenterCore/…/Library/ServiceVersions.swift`, `Sources/AppModel.swift` (service, runningVersion, chooseServiceVersion, create/rename/deleteServiceVersion, useMainForServiceItem, versionChangedItemIDs, claimRememberedVersion, updateService, setServiceItemHidden, setServiceItemOutputPreset, duplicate for services) | `MxU.Slides.Core/Library/ServiceVersions.cs`; `ViewModels/MainViewModel.ServiceVersions.cs`, `Infrastructure/ThisComputer.cs` |
| `Sources/AppShell.swift` (service picker row menu: Change Date, Duplicate, serviceVersionMenu, ServiceVersionAlerts) | `Views/ServiceMenus.cs`, `Views/ServiceVersionChip.cs`, `Views/ServiceDateWindow.cs` |
| `Sources/ServicePlannerView.swift` (hiddenGroupRow, hiddenRow, outputPresetMenu, the Changed badge) | `Views/HiddenServiceRows.xaml`, `Views/ServiceMenus.cs` (AddItemOptions), `Views/MainWindow.xaml` (badge) |
| `Sources/PresentGridView.swift` (AudioFireView, PlaylistFireView) | `Views/ServiceMusicCard.xaml`, `ViewModels/MainViewModel.ServiceMusic.cs` |
| `Sources/ServiceControls.swift` (restart, resolvedLoops, the restarts in land, fire(mediaItem:), fire(overlay:)) | `MxU.Slides.Core/Automation/VideoRestarts.cs`; `MainViewModel.Transport.cs` (`RestartVideo`) |
| `PresenterCore/…/Library/PerSlideTheme.swift` (`themeId(for:)`, `hasSlideThemes`, `clearSlideThemes`, override designs; `rendered(throughOverride:)`) | `MxU.Slides.Core/Library/PerSlideTheme.cs` (`rendered(throughOverride:)` is `PlaceholderAnimations.RenderedThroughOverride`); `MxU.Slides.Windows/ViewModels/MainViewModel.Rendering.cs` (`ThemeFor`) |
| `Sources/AppModel.swift` (`theme(for:in:)`), `Sources/PresentGridView.swift` (tile theme, `hasClippedText` badge) | `MainViewModel.Rendering.cs` (`ThemeFor`), `ViewModels/ItemViewModels.cs` (`HasClippedText`), the slide tile in `Views/MainWindow.xaml` |
| `MediaEngine/…/MediaEngine.swift` (`PlaybackOptions`), `MediaPlayer.swift` (trim window, loop inside the trim, rate) | `MxU.Slides.Core/Media/PlaybackOptions.cs`; `MxU.Slides.Windows/Rendering/MediaHub.Trim.cs` |
| `PresenterCore/…/MediaImport/FontActivator.swift` (`activateStored`, `register`) | `MxU.Slides.Windows/Rendering/FontMap.cs` (`ActivateStored`) |
| `PresenterCore/…/MediaImport/FontSharing.swift` (`faces(inFileAt:)`) | `MxU.Slides.Core/Library/FontFiles.cs` |
| `RenderEngine/…/PathTextLayout.swift`, `PathTextStrip.swift` (`normalizedText`, the strip) | `MxU.Slides.Core/Scene/PathTextLayout.cs`; `MxU.Slides.Windows/Rendering/PathTextRenderer.cs` |
| `RenderEngine/…/Compositor.swift` (`drawPathText`'s phase, repeat and ramp) | `PathTextLayout.TickerPhase` |
| `RenderEngine/…/ShapeRasterizer.swift` (`SVGPathParser`), `PathPresets.swift` | `MxU.Slides.Core/Scene/SvgPath.cs` (`SvgPath`, `PathPresets`) |
| `RenderEngine/…/Compositor.swift` (`BuildProjector`, `buildQuadCorners`, `stretchKeystoneVertices`) | `MxU.Slides.Core/Animation/BuildProjector.cs`; `MxU.Slides.Windows/Rendering/KeystoneEffect.cs` |
| `RenderEngine/…/TextRasterizer.swift` (`overflows`) | `MxU.Slides.Windows/Rendering/TextLayout.cs` (`OverflowsFrame`) |
| `SlideScene/…/SlideSceneBuilder.swift` (`styledText` path and ticker fields, `renderItems` shape-edge text, `fill(_:)` inputs and mirrors, `fillMediaID`, `fillLiveInputID`, `fillScreenMirrorID`, `liveInputEngineID`, `screenMirrorEngineID`, `mediaWants`, `clippedTextObjectIDs`) | `MxU.Slides.Core/Scene/SlideSceneBuilder.cs` |
| `RenderEngine/…/SceneModel.swift` (`StyledText` path, ticker and scroll fields) | `MxU.Slides.Core/Scene/SceneModel.cs` |
| `Sources/LiveInputPlaceholder.swift` | `MxU.Slides.Windows/Rendering/LiveInputPlaceholder.cs` |
| `Sources/ScreenMirrorService.swift` | `MxU.Slides.Windows/Outputs/ScreenMirrors.cs` |
| `Sources/AudioOutputInventory.swift` | `MxU.Slides.Audio/Routing/AudioOutputInventory.cs` (`AudioOutputEntry`, `AudioOutputInventory`) |
| `Sources/AudioMixInventory.swift` (entries, main, create, rename, primaryToken, setPrimary, toggleSecondary, isVirtual, remove; not folders or the legacy migration) | `MxU.Slides.Audio/Routing/AudioMixInventory.cs` |
| `Sources/AudioController.swift` (`routing(forMix:)`, `defaultRouting`, `resolveRouting`, the hardware check in `applyLevels`, `mediaMixId`/`setMediaMix`, `applyDefaultRoutingToMedia`, `reapplyAllRouting`, `deviceListChanged`, the fade and crossfade settings) | `MxU.Slides.Audio/Routing/AudioRoutingTable.cs`, `MxU.Slides.Windows/ViewModels/MainViewModel.Sound.cs` |
| `PresenterCore/…/SettingsDeviceStatus.swift` (`showsNotConnected`), `Sources/SettingsPanes.swift` (`AudioOutputWords`) | `MxU.Slides.Audio/Routing/AudioOutputInventory.cs` → `AudioOutputWords` |
| `Sources/SettingsPanes.swift` (`AudioOutputsSection`, `AudioOutputDetail`) | `MxU.Slides.Windows/Views/SoundSettingsPanel.cs` |
| `Sources/AppModel.swift` (`createPlaylist`, `addToPlaylist`, `insertIntoPlaylist`, `handlePlaylistDrop`, `removePlaylistEntry`, `movePlaylistEntries`, `updatePlaylist`), `PresenterCore/…/Documents/DocumentEntity.swift` (`Playlist.effectiveKind`) | `MxU.Slides.Core/Library/PlaylistLibrary.cs` |
| `Sources/AppModel.swift` (`replaceAudioFile`) | `MxU.Slides.Audio/Library/AudioImporter.cs` → `ReplaceFile` |
| `Sources/Modules/AudioModule.swift`, `Sources/InspectorView.swift` (`PlaylistEditor`), `Sources/LibraryView.swift` (Add to Playlist, New Playlist…, Replace Music File…, Rename, Delete) | `MxU.Slides.Windows/Views/MusicBin.xaml(.cs)`, `MusicManagerWindow.cs`, `MusicMenus.cs`, `ViewModels/MainViewModel.Music.cs` |
| `Sources/Modules/AlertsModule.swift` (the rules: tokens, timer identity, locations, layers, themes, commit) | `MxU.Slides.Core/Stage/AlertEditing.cs` |
| `Sources/AppModel.swift` (`createAlertPreset`, `updateAlertPreset`, `alertBoard`, `updateAlertBoard`) | `MxU.Slides.Core/Stage/AlertLibrary.cs` |
| `SlideScene/…/ConfidenceScene.swift` (`AlertSceneBuilder.audienceItems`), `ShowState.swift` (`liveAlert`, the alert's clock, `clearAlert`, the alert's layer) | `MxU.Slides.Core/Stage/AlertScene.cs`, `Scene/ShowState.cs` |
| `Sources/ServiceControls.swift` (`fireAlert`, `show(alert:)`, `fire(alertPreset:)`, `dismissAlert`, `syncFlashClock`, `nextSkipsBlanks`) | `MxU.Slides.Windows/ViewModels/MainViewModel.Alerts.cs`, `MainViewModel.Stage.cs` |
| `SlideScene/…/TimerBoard.swift` | `MxU.Slides.Core/Stage/TimerBoard.cs` (and `ServiceTrackingTimers.MergeInto`) |
| `Sources/TimersController.swift` (the board: `moveTimer`, `moveFolder`, `addFolder`, `renameFolder`, `removeFolder`, `setSystemTimers`) | `MxU.Slides.Core/Stage/TimerController.cs` |
| `Sources/Modules/TimersModule.swift` (the rules: quick lengths, the editor's apply, warnings, rows) | `MxU.Slides.Core/Stage/TimerPanel.cs` |
| `Sources/ConfidenceMonitorController.swift` (assignments, apply, pruning, storage) | `MxU.Slides.Core/Stage/ConfidenceScreenLayouts.cs`, `MainViewModel.Stage.cs` |
| `PresenterCore/…/Library/PreviewTargetLogic.swift`, `Sources/LivePanel.swift` (`PreviewTargets`) | `MxU.Slides.Core/Outputs/PreviewTargets.cs` |
| `Sources/Modules/OutputsModule.swift` (`OutputPresetMenu`), `Sources/OutputPresetChip.swift` | `MxU.Slides.Core/Outputs/OutputPresetMenu.cs` |
| `Sources/PerformanceHUD.swift` (`TransitionChip`), `AppShell.swift` (`transitionChips`) | `MxU.Slides.Core/Animation/TransitionChips.cs` |
| `SlideScene/…/SlideSceneBuilder.swift` (`emptyRepeatsOnce`) | `MxU.Slides.Core/Scene/SlideSceneBuilder.cs` (`EmptyRepeatsOnce`) |
| `PresenterCore/…/Library/SlidesPresentationFile.swift`, `DeckBundle.swift` | `MxU.Slides.Core/Library/SlidesPresentationFile.cs`, `DeckBundle.cs`, `SafeZip.cs` (unzipping safely, instead of `ditto`) |
| `PresenterCore/…/MediaImport/FontSharing.swift` (and the parts of `FontActivator.swift` it uses) | `MxU.Slides.Core/Library/FontSharing.cs` (`FontSharing`, `FontIndex`, `FontNames`: name tables instead of Core Text) |
| `PresenterCore/…/Library/MediaReferences.swift` | `MxU.Slides.Core/Library/MediaReferences.cs` |
| `PresenterCore/…/Library/MediaUsage.swift` (with `MediaPickCardLogic`) | `MxU.Slides.Core/Library/MediaUsage.cs` |
| `PresenterCore/…/MediaImport/MediaRelink.swift`, `Sources/AppModel.swift` (`replaceMediaFile`, `restoreMediaTombstone`, `placeholderTombstone`, `missingMediaTargets`, `locateMissingMedia`, `mediaHolding`, `mediaUses`, `mediaReferenceDocuments`) | `MxU.Slides.Core/Library/MediaRelink.cs` (`MediaRelink`, `MissingMedia`), `MediaUsage.Documents` |
| `PresenterCore/…/MediaImport/MediaImportDedupe.swift`, `Sources/AppModel.swift` (`importFiles`) | `MxU.Slides.Core/Library/MediaImportDedupe.cs`, `ViewModels/MainViewModel.MediaImport.cs` |
| `PresenterCore/…/MediaImport/BlobStore.swift` | `MxU.Slides.Core/Library/BlobStore.cs` |
| `PresenterCore/…/MediaImport/MediaImporter.swift` (files, adding items) | `MxU.Slides.Core/Library/MediaImporting.cs` |
| `PresenterCore/…/MediaImport/MediaImporter.swift` (`probeVideo`, the image probe), `MediaRelink.probe` | `MxU.Slides.Broadcast/Media/MediaImportProbe.cs`, `MediaPlayability.cs` (Media Foundation and WIC instead of AVFoundation and ImageIO) |
| `PresenterCore/…/MediaImport/TranscodeQueue.swift` | `MxU.Slides.Broadcast/Library/TranscodeQueue.cs`, `Media/MediaTranscoder.cs` (H.264 instead of HEVC) |
| `Sources/PresentationFileImport.swift`, the `.mxuslides` half of `Sources/PresentationExportPanel.swift` | `ViewModels/MainViewModel.LibraryExchange.cs`, `Views/MainWindow.Library.cs` |
| `Sources/LocateMissingMediaView.swift` | `Views/LocateMissingMediaWindow.cs`, `ViewModels/MainViewModel.MissingMedia.cs` |
| `Sources/ReplaceMediaFileFlow.swift`, `Sources/MediaImportPrompt.swift`, the delete confirmation in `Sources/LibraryView.swift` | `Views/LibraryDialogs.cs`, `ViewModels/MainViewModel.MissingMedia.cs` |
| the missing/status badges in `Sources/LibraryView.swift` and `PresentGridView.swift` | `Views/MediaBadge.cs`, `ViewModels/ItemViewModels.Library.cs` |

The Mac tests for these files are ported too, in `apps/windows/tests/` (one test project per source project). When you port a change, port its test.
| `SlideScene/…/ThemeOverride.swift` | `MxU.Slides.Core/Animation/PlaceholderAnimations.cs` → `OverrideObjects` (font traits from the name; see below). `BaseStyle` is public and reads the placeholder's font, colour and case before the theme's |
| `PresenterCore/…/ThemeSlideScope.swift` | `MxU.Slides.Core/Scene/ThemeSlideScope.cs` |
| `SlideScene/…/TransitionEdit.swift` | `MxU.Slides.Core/Animation/TransitionEdit.cs` |
| `Sources/RenderContext.swift` (`sceneTransition(from:)`, `roomDefault(for:)`, the variant engines), `Sources/ServiceControls.swift` (`nextLiveTransitions`, hand-offs, `switchPreset`, `apply()`'s variants and page turn) | `MxU.Slides.Core/Animation/LiveTransitions.cs`, `MxU.Slides.Windows/ViewModels/MainViewModel.Live.cs`, `Rendering/SceneView.cs` (`PresentLayered`). `LiveTransitions.cs` also takes the Phase 6 Windows-only kinds (`Own`, `FromWindows`) and has their stand-ins (`Fallback`, `InSoftware`); the rules themselves are still the Mac's |
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
| `PresenterCore/…/Library/ScriptureReference.swift` | `MxU.Slides.Core/Scripture/ScriptureReference.cs` (`Matches`, `IsReferenceLine`, `SplitText`: the Mac's `split` is `SplitText`, because C# can't have a method and a nested type both called `Split`) |
| `PresenterCore/…/Library/ServiceLinkLogic.swift` | `MxU.Slides.Core/Library/ServiceLinkLogic.cs` (all of it; `songKeyUpdate` is `SongKeyUpdateFor`) |
| `PresenterCore/…/Library/AnimationPresetCatalog.swift`, `EffectPresetCatalog.swift` | `MxU.Slides.Core/Animation/AnimationPresets.cs` (with `AnimationPresetApplication`) |
| `AnimationRanges` (in `TextStyleRuns.swift`), `Sources/SlideEditorModel.swift` (`generateAnimationSteps`) | `MxU.Slides.Core/Animation/AnimationRanges.cs` |
| `Sources/AppModel.swift` (`saveAnimationPreset`, `overwriteAnimationPreset`, `deleteAnimationPreset` and the effect ones) | `MxU.Slides.Core/Library/PresetBoards.cs` |
| `RenderEngine/…/TextRasterizer.swift` (balanced wrap) | `MxU.Slides.Windows/Rendering/TextLayout.cs` |

### Phase 7: editing and the editors (HOW-IT-WORKS sections 48 and 49)

| Swift | C# |
|---|---|
| `PresenterCore/…/MenuCommandLogic.swift` (`UndoMenuLogic`) | `MxU.Slides.Core/Undo/UndoMenuLogic.cs` |
| `PresenterCore/…/Library/MoveUndoJournal.swift` (the rules: 50 steps, 5 s coalescing, dead entries dropped, nothing recorded while restoring; `ListOrder.apply`, `ListRemoval.restore`) | `MxU.Slides.Core/Undo/UndoScope.cs`, `UndoHistory.cs`, `JsonMerge.cs` |
| `SlideScene/…/SlideBulkEdit.swift` (`freshIDCopy`, `duplicateSlides`, `deleteSlides`, `insertSlides(after:)`, `moveSlide(before:)`) | `MxU.Slides.Core/Editing/EditorClipboard.cs`, `Dropping.cs` (`DropRules.MoveSlides`) |
| `SlideScene/…/SlideBulkEdit.swift` (`mergeSlides`) | `MxU.Slides.Core/Lyrics/SlideBulkEdit.cs` (`MergeSlides`; the one copy) |
| `Sources/SlideEditorModel.swift` (`pasteObjects`, `duplicateSelectedObjects`, `copySlides`, `pasteSlides`, `bringForward`, `sendBackward`, `bringToFront`, `sendToBack`, `groupSelection`, `ungroupSelection`, `panelStackTopFirst`, `movePanelObject`, `applyStyleToSelection`, `toggleSelectionBold/Italic/Underline`, `mergeSlides`) | `Core/Editing/EditorClipboard.cs`, `Layers.cs`; `ViewModels/MainViewModel.Clipboard.cs`, `MainViewModel.Layers.cs`, `ObjectInspectorViewModel.TextTools.cs` |
| `PresenterCore/…/Library/SlideGridDrop.swift` | `MxU.Slides.Core/Editing/Dropping.cs` (`SlideGridDrop`, `SlideDropZones`) |
| `PresenterCore/…/Library/TextStyleRuns.swift` (with `textTransform`; `SlideObject.replaceText`; `AnimationRanges.replacing`, `reanchor`) | `MxU.Slides.Core/Editing/TextStyleRuns.cs` (`TextStyleRuns`, `ObjectText`) |
| `PresenterCore/…/Library/ChordEditing.swift` | `MxU.Slides.Core/Editing/ChordEditing.cs` |
| `Sources/PresentGridView.swift` (`SongKeyChip`; the drop in front of the first slide; `TileHelp`/`TileTipLabel` for the action badges) | `Views/SongKeyChip.cs`, `ViewModels/MainViewModel.Dropping.cs` (`InsertMediaSlides`), `Views/TileActionBadges.cs` |
| `Sources/SlideEditorView.swift` (the objects panel; Merge N Slides) | `Views/LayersPanel.cs`; `Views/MainWindow.Editing.cs` (`AddSlideEditItems`) |
| `Sources/SlideObjectInspector.swift` (typography: Balanced Wrap, Auto-Shrink, Page on Click, Keep Lines Whole, Min Size; the Selection strip with All Caps; the Photo spot toggle and Scale and Blur) | `ViewModels/ObjectInspectorViewModel.TextTools.cs`, `Views/TextFormatBar.cs`, the inspector XAML; `ObjectInspectorViewModel.PhotoSpot`, `ScaleMode` |
| `SlideScene/MediaTrimEdit.swift` | `MxU.Slides.Core/Media/MediaTrimEdit.cs` |
| `PresenterCore/Library/DesignActions.swift` (with `Presentation.applyTheme` and `adoptDesignActions`) | `MxU.Slides.Core/Library/DesignActions.cs` |
| `PresenterCore/Library/ConfidenceLayoutEditing.swift` (`editorMirror`, `adopt(editorMirror:)`, `isTallCanvas`) | `MxU.Slides.Core/Stage/StageLayoutEditing.cs` |
| `Sources/AutoAdvanceControls.swift` | `MxU.Slides.Core/Automation/AutoAdvanceEditing.cs`, `Views/AutoAdvanceWindow.cs` |
| `Sources/SlideEditorModel+Animations.swift` (timeline and morph: `placeTimelineStep`, `resizeTimelineStep`, `moveTimelineSteps`, `trimTimelineStepStart`, `moveTimelineStep`, `toggleStepLink`, `duplicateAnimationStep`, `deleteTimelineColumn`, `moveTimelineColumn`, `morphBaseline`, `addMorph`, `setInCustomStart`) | `MxU.Slides.Core/Animation/AnimationTimelineEditing.cs` |
| `Sources/AnimationTimelinePanel.swift` | `Views/AnimationTimelineView.cs` |
| `Sources/MediaEditorView.swift` (the inspector, `EffectKind.addable`, the effects section), `SlideObjectInspector.swift` (effects section, `EffectPresetsMenu`) | `ViewModels/MediaEditorViewModel.cs`, `Views/MediaEditorWindow.cs`, `MxU.Slides.Core/Animation/EffectEditing.cs`; `Views/EffectsPanel.cs`, `ViewModels/EffectsEditorViewModel.cs` |
| `RenderScene.applyingMediaEffects` | the effects half of `MxU.Slides.Core/Media/MediaDressing.cs` |
| the theme host of the slide editor, `ThemeSlides.defaultSlides`, `SlideBulkEdit.freshIDCopy`/`uniqueThemeSlideName`, AppModel's theme create and duplicate; `NewSlide.preview` | `MxU.Slides.Core/Library/ThemeLibrary.cs`, `ThemeApplication.cs`, `ViewModels/ThemeEditorViewModel.cs`, `Views/ThemeEditorWindow.xaml`; `Core/Animation/DesignPreview.cs` |
| `Sources/Modules/ConfidenceModule.swift` ("Edit Layout in Library") | the pencil in `Views/Operator/StagePanel.cs` |
| `NewSlide.swift` (`ThemeDesignDrag`), AppModel's `themeDesign(_:)`, `LibraryThemeDesigns.swift`, PresentGridView's theme design drop | `MxU.Slides.Core/Library/ThemeDesignDrag.cs`, `Views/ThemeDesignsWindow.cs`, `Views/ThemeDesignDrop.cs`, `MainViewModel.DropThemeDesign` |
| `SlideLayerFacts.swift` (`storedRoutingLayerId`, `setRoutingLayer`, `defaultLayer`), MediaCueMenu's `SpotLayer` picker, `PhotoSpots.swift` (`inPhotoSpot`), SlidePlanner's `showMedia` video end, `OutputPresetsController.customLayers` | `MxU.Slides.Core/Media/CueMediaEditing.cs` (with `CustomLayers`; the video end choices are `SlideOptions.VideoEnds`), `ViewModels/MainViewModel.SlideMedia.cs` |
| `Sources/ActionEditors.swift` (`router.layerStack`, `slideActionLabel(layers:)`) | `ActionEditing.LayerStack`, `SlideActions.LabelNames.Layers` |
| the stage inspector's Counts To / Action / Action Name / Direction / Video From rows | `ViewModels/StageLinkViewModel.cs` |

### Phase 8: the library and the operator (sections 50 and 51)

| Swift | C# |
|---|---|
| `PresenterCore/…/Library/LibraryViewOptions.swift` | `MxU.Slides.Core/Library/LibraryBrowse.cs` (`LibrarySort`, `LibrarySorts`) |
| `PresenterCore/…/ListMultiSelect.swift` | `MxU.Slides.Core/Library/RunOrderSelection.cs` (`ListMultiSelect`; one copy, used by the library and the service list) |
| `PresenterCore/…/Library/IndexSnapshot.swift` (`LibraryBrowseLogic`), `FolderTreeLogic.swift`, `TeamDrives.swift` (`folderTree`, the folder half of `folderMoveMenu`) | `MxU.Slides.Core/Library/LibraryBrowse.cs` (`LibraryFolders`, `MoveTargets`), `FolderPaths.cs`, `FolderTree.cs` (`FolderNode`; one copy) |
| `PresenterCore/…/Import/PresentationExport.swift` | `MxU.Slides.Core/Export/PresentationExport.cs` (with `PdfDocument`, `TrueTypeFont`, `LyricSheet`, `SlidesPdf`, `TextExport`) |
| `PresenterCore/…/Library/BackupBundle.swift` | `MxU.Slides.Core/Library/LibraryBackup.cs` |
| `PresenterCore/…/Library/ScheduledBackup.swift` | `MxU.Slides.Core/Library/BackupFiles.cs` (`ScheduledBackup`) |
| `PresenterCore/…/Diagnostics/DiagnosticsBundle.swift` | `MxU.Slides.Core/Diagnostics/DiagnosticsBundle.cs` |
| `PresenterCore/…/Diagnostics/PerfLineLimiter.swift` | `MxU.Slides.Core/Diagnostics/HangDetector.cs` (`PerfLineLimiter`) |
| `PresenterCore/…/Onboarding/OnboardingFlow.swift` | `MxU.Slides.Core/Library/OnboardingFlow.cs` |
| `PresenterCore/…/MediaImport/MediaRelink.swift` (`refingerprinted`) | `MxU.Slides.Core/Library/MediaRelink.cs` (`Refingerprinted`) |
| `PresenterCore/…/MediaImport/MediaImportDedupe.swift` (`unattendedUsesExisting`) | `MxU.Slides.Core/Library/MediaImportDedupe.cs`; `ImportMediaFilesAsync(unattended: true)` |
| `PresenterCore/…/MediaImport/BlobStore.swift` (edited files), `Sources/LibraryFileEdits.swift` | `MxU.Slides.Core/Library/BlobEdits.cs`, `ViewModels/MainViewModel.BlobEdits.cs` (a map of times instead of extended attributes) |
| `Sources/LibraryView.swift` (folder *Move to Folder*, search hits with *Show in Library* and the item's own menu, delete removes unused files) | `Views/LibraryMenus.cs`, `Views/LibraryPanel.xaml.cs`, `MainViewModel.LibraryBrowse.cs` (`ShowInLibrary`) |
| `Sources/KeyboardShortcuts.swift` (`KeyInController.handle`, `execute`, `dispatch`, the go-to buffer), `KeyboardSettingsPane.swift` (`KeyboardMapPane`, `KeyChordChip`, `GroupHotKeyChip`) | `Views/MainWindow.Keys.cs`, `Views/Keys/*` (`KeyTokens`, `ShortcutsWindow`, `KeyChip`), `ViewModels/MainViewModel.Shortcuts.cs` |
| `PresenterCore/…/VolunteerModeLogic.swift`, the run-only gates (`RunOnlySweepTests.swift`) | `MxU.Slides.Core/Control/VolunteerMode.cs` (`VolunteerMode`, `RunOnly`, `VolunteerPanels`) |
| `PresenterCore/…/Library/RunOrderSelection.swift` (not `swept`: no marquee), `AppModel.setServiceItemTransition` | `MxU.Slides.Core/Library/RunOrderSelection.cs` (`RunOrderSelection`, `ServiceBulkEdit`) |
| `PresenterCore/…/Library/ServiceMenuLogic.swift`, `Sources/AppShell.swift` (`groupLocalServices`, the picker's paging and Earlier row) | `MxU.Slides.Core/Library/ServicePicking.cs`, `Views/ServicePicker.cs` |
| `Sources/FolderMenus.swift` (`EntryFolderMenuItems`) | `Views/Operator/StagePanel.cs` (`LayoutMenu`) |
| `Sources/AppModel.swift` (`addArrangement`, `updateArrangement`, `deleteArrangement`), `SlideEditorModel.swift` (`sectionName`, `arrangedSlideCount`), `ServiceContinuousView.swift` (`ArrangementStrip.dropBlock`, `baseRunSections`), `SlideSceneBuilder.showsArrangements` | `MxU.Slides.Core/Lyrics/ArrangementEditing.cs` |
| `Sources/ServiceContinuousView.swift` (`presentRows`, the card kinds, `CardSlidePosition`) | `MxU.Slides.Core/Scene/ContinuousRows.cs` |
| `Sources/Modules/ControlBoardFolders.swift` (`handleControlBoardRowDrop`, the folder block's drop, `ControlBoardEndDropStrip`) | `MxU.Slides.Core/Automation/BoardDrops.cs` |
| `SettingsView.swift` (`present.volunteerLibraries`), `PresentLayoutController.swift` (`runOnlyLibraryKinds`), `LibraryPresentLogic.volunteerKinds` | `VolunteerPanel.Presentations` in `Core/Control/VolunteerMode.cs`, the Presentations bin in `Views/Volunteer/VolunteerView.xaml(.cs)`, `MainViewModel.PresentFromLibrary` |
| `MediaTransportStrip.swift` (the next-mark caption), `ScrubBar.swift` (`marks`), `MediaTransportController.swift` (`Row.marks`) | `Views/Operator/ScrubMarks.cs`, `Views/LiveShowControls.xaml`, `MainViewModel.Transport.cs` (`TransportMarks`, `TransportNextMark`) |

### Phase 10: library sync (section 52)

| Swift | C# |
|---|---|
| `automerge-swift/Sources/Automerge/Codable/Decoding/*` (the rules `AutomergeDecoder` applies) | `MxU.Slides.Sync/Format/MacDecoder.cs` |
| `automerge-swift/Sources/Automerge/Codable/Encoding/*` (what `AutomergeEncoder` writes) | `MxU.Slides.Sync/Format/MacEncoder.cs`; a second copy written separately for the tests, `tests/MxU.Slides.Sync.Tests/Fakes/MacSimulator.cs` |
| `PresenterCore/…/Documents/DocumentEntity.swift` (`DocumentKind`, `directoryName`), `Library/SyncScope.swift` (`scope(for:)`, `entityType(for:)`) | `MxU.Slides.Sync/Format/MacKinds.cs` |
| `PresenterCore/…/Documents/DocumentStore.swift` (`url`, `save`'s merge-before-write, `ids(of:)`), `MediaImport/BlobStore.swift` (blob names) | `MxU.Slides.Sync/Engine/SharedFolder.cs`, `SyncEngine.Documents.cs` (`WriteShared`), `BlobMirror.cs` |
| `PresenterCore/…/Documents/TypedDocument.swift` (`init(value)`: one change, "create"; `updateSlideList`'s inserts, deletes and moves; `wholeValueKeys`) | `MxU.Slides.Sync/Format/MacDocuments.cs`, `DocumentDiff.cs` (`WholeValueKeys`, `WriteWhole`) |
| `PresenterCore/…/Library/SyncLedger.swift` (`hex(heads)`), `SyncScope.swift` (`SyncReconcileLogic.afterTombstone`: an edit beats a deletion) | `MxU.Slides.Sync/Automerge/AmDocument.cs` (`DocHeads`), `Engine/SyncEngine.Documents.cs` |
| `PresenterCore/…/Library/BackupBundle.swift` (`formatVersion`, the layout) | `MxU.Slides.Sync/Engine/SharedFolder.cs` (`BackupFormatVersion`) |

### MxU's autumn 2026 updates (upstream #10, #12 and #13; sections 54 to 56)

| Swift | C# |
|---|---|
| `PresenterCore/Library/NoteSlideMarks.swift`, `NoteSlideSuggestions.swift`, `NoteSlideUpdate.swift`, `NoteLiveUpdates.swift`, `NoteFollowAlong.swift`, `NoteMedia.swift`, `NotePassages.swift`, `NoteReading.swift`, `NoteDeckCards.swift`, `NotePanels.swift`, `NoteSlides.swift`, `NoteSlidesMade.swift`, `NoteTextReplacement.swift`, `LocalNotes.swift` | `MxU.Slides.Core/Notes/*` (same names; `NotePassages` and `NoteDeckCards` are in `NotePanels.cs`, `LocalNotes` in `NoteReading.cs`, `NoteTextReplacement` in `NoteSlideMarks.cs`) |
| `PresenterCore/Library/SlidePlanner.swift`, `SlideDesignMap.swift`, `SlideBuildingSettingsLogic.swift` | `Core/Notes/SlidePlanner.cs`, `NoteSlideBuilder.cs`, `SlideDesignMap.cs` (with `SlideOptions.VideoEnds`), `SlideMediaFacts.cs`, `SlideBuildingSettingsLogic.cs` |
| `PresenterCore/…/Library/PhotoSpots.swift` | `MxU.Slides.Core/Library/PhotoSpots.cs` (the one copy; Make Slides calls it) |
| `Import/NoteFileReader.swift`, `PDFGlyphReader.swift`, `DocxMedia.swift`, `ZipExtract.swift` | `Core/Notes/NoteFileReader.cs`, `NoteDocumentReaders.cs` (PDF through PdfPig) |
| `PPTXImport/PPTXNoteText.swift` | `MxU.Slides.PptxImport/PptxNoteText.cs` |
| slide history | `MxU.Slides.Core/Library/SlideHistory.cs` |
| the app's notes views and Make Slides sheet | `Views/Notes/*`, `ViewModels/MainViewModel.Notes.cs`, `Views/MainWindow.Notes.cs` |
| `LocalAPI/FollowMode.swift` | `MxU.Slides.LocalApi/Follow/FollowMode.cs` |
| `LocalAPI/APIFireFeed.swift` | `MxU.Slides.LocalApi/Follow/ApiFireFeed.cs` |
| `LocalAPI/LocalAPIClient.swift` | `MxU.Slides.LocalApi/Follow/LocalApiClient.cs` |
| `LocalAPI/BonjourAdvertiser.swift`, `BonjourBrowser.swift` | `MxU.Slides.LocalApi/Follow/DnsSd.cs` (Windows' dnsapi) |
| `LocalAPI/LocalAPIServer.swift`, `APIWebSocketHandler.swift`, `APIEventBus.swift`, `WireModels.swift`, `OpenAPIDocument.swift` (follow parts) | `LocalApiServer.cs`, `ApiWebSocketSession.cs`, `ApiEventBus.cs`, `WireModels.cs`, `OpenApiDocument.cs` |
| `LocalAPIController.followRoutes` | `MxU.Slides.LocalApi/Follow/ApiRouteTable.Follow.cs` (`IFollowApi`) |
| `PresenterCore/Library/FollowLogic.swift` | `MxU.Slides.Core/Follow/FollowLogic.cs` |
| `Sources/FollowController.swift`, `FollowPairingSheet.swift`, the Backup settings | `MxU.Slides.Windows/Follow/FollowController.cs`, `Views/Follow/*` (`PairBackupWindow`, `FollowBanner`, `BackupSettingsSection`), `ViewModels/MainViewModel.Follow.cs` |
| `PresenterCore/…/Timeline/VideoMarks.swift` | `MxU.Slides.Core/Media/VideoMarks.cs` |
| `SlideScene/…/ConfidenceScene.swift` (`VideoCountdown` with marks), `LinkedText.swift` (Video Action Name) | `MxU.Slides.Core/Stage/ConfidenceInfo.cs`, `LinkedText.cs` |
| `Sources/MediaTransportController.swift` (`fireVideoActions`, countdown marks) | `ViewModels/MainViewModel.VideoMarks.cs`, `MainViewModel.Transport.cs` |
| `PresenterCore/…/Library/StarterPacks/StarterPacks.swift` (`addingLyricsDesigns`, `addingPhotoDesigns`, `shrinkingPoints`, `stayingPhotos`) | `MxU.Slides.Core/Library/StarterPacks.Repair.cs` (and the photo designs in `StarterPacks.cs`, `LibrarySeeder.cs`) |
| `SlideScene/…/SlideSceneBuilder.swift` (photo spots, `cueItem`, `scaleAndBlur`) | `MxU.Slides.Core/Scene/SlideSceneBuilder.cs` |
| `RenderEngine/…/MediaLooks.swift` | `MxU.Slides.Core/Animation/MediaLooks.cs` |
| `RenderEngine/…/SceneModel.swift` (layer ids, custom and placed layers, `endFade`, `crossfadeLeaving`, `scaleAndBlur`) | `MxU.Slides.Core/Scene/SceneModel.cs`, `Animation/AnimatedItem.cs` |
| `RenderEngine/…/Compositor.swift` (`crossfadeStacks`, `placedPictures`, `drawPlaced`, `playbackRemaining`) | `MxU.Slides.Windows/Rendering/SceneView.Layers.cs`, `SceneView.cs` (WPF containers and brushes instead of Metal passes) |
| `RenderEngine/…/AnimationEvaluator.swift`, `VideoPushEvaluator.swift`, `OutputScaling.swift` (optional layer kind) | `Animation/AnimationEvaluator.cs`, `VideoPushEvaluator.cs`, `Outputs/OutputScaling.cs` |
| `MediaEngine/…/MediaEngine.swift` (`playbackRemaining`) | `MxU.Slides.Windows/Rendering/MediaHub.cs` (`PlaybackRemaining`) |
| `SlideScene/…/ShowState.swift`, `MediaTransportTarget.swift` (custom layers, `clear(layerID:)`, seeds) | `MxU.Slides.Core/Scene/ShowState.CustomLayers.cs`, `ShowState.cs`, `ShowState.Flow.cs`, `Media/MediaTransport.cs` |
| `OutputEngine/…/ScreenMap.swift`, `OutputManager.swift` (the map, layer placements, `dominantScreen`, `seedPlacement`), `OutputLayout.swift` | `MxU.Slides.Core/Outputs/ScreenMap.cs`, `OutputConfiguration.ScreenMap.cs`, `OutputConfiguration.Feeds.cs`, `OutputLayout.cs`, `ScreenEditing.cs` |
| `Sources/OutputPresetsController.swift` (`route`: placements, custom layers) | `OutputPresets.Apply`, `ViewModels/MainViewModel.ScreenMap.cs` (`CustomLayers`: one list of every preset's layers) |
| `PresenterCore/…/Library/LyricPhrasing.swift`, `ScriptureReference.swift`, `MIDIControl.swift` (Take Over (Backup), note 36), `MediaCueRouting.swift` (`DroppedAsNewSlide`), `MediaImport/RecordingLibrary.swift` | their existing C# twins, updated |
| `StreamEngine/…/UploadWatch.swift`, `StreamSession.swift`, `StreamHealth.swift`, `StreamConfiguration.swift` | `MxU.Slides.Broadcast/Streaming/*` (`UploadWatch.cs` also holds a port of HaishinKit's `NetworkMonitor` and `AdaptiveBitRateStrategy`) |
| `RenderEngine/…/DropoutWatch.swift`, `OutputEngine/…/Export/SyncTestReader.swift` (`gridLuma`, `isBlackAbove`), `Recording/Recorder.swift` | `MxU.Slides.Core/Media/DropoutWatch.cs`, `MxU.Slides.Broadcast/Recording/SyncTestReader.cs`, `Recording/Recorder.cs` |
| `Sources/BroadcastStatusChip.swift` (network line), `StreamRecordSettings.swift` (Bitrate mode) | `MxU.Slides.Broadcast/Control/BroadcastStatus.cs`, `Views/BroadcastWindow.cs` |

Nothing to port from those updates: one main window never tabs, the Present card redraw fix, the focus fixes and the mixer's diagnostics line (macOS or SwiftUI only), and the strip's ticking moved to Core Animation (Windows already ticks four times a second).

The Mac's tests for the files in these four tables are ported with them. Each phase's walkthrough lists them by name.

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
| `PresenterCore/…/Diagnostics/ThreadStackCapture.swift` | `MxU.Slides.Windows/Infrastructure/ThreadStacks.cs` (Microsoft's ClrMD reads the stacks from a snapshot of the app; so far only the self-test's hang watchdog uses it) |
| `MediaEngine/…/MediaEngine.swift` (`setAudioOutputDevice`, `setAudioOutputDelay`, `setAudioVolume`), `MediaPlayer.swift` (`audioOutputDeviceUniqueID`), `VideoAudioTap.swift` (the playout delay): AVPlayer plays its own sound | `MxU.Slides.Audio/Video/` (`VideoSound`, `VideoSoundPlayer`, `VideoClockFollower`, `TimeStretch`), `MxU.Slides.Windows/Rendering/MediaHub.Sound.cs`: the picture player is muted and our engine plays the sound, following its clock |
| `Sources/Modules/AlertsModule.swift`, `TimersModule.swift`, `ConfidenceModule.swift`, `OutputsModule.swift`, `Sources/LivePanel.swift` (preview, screen picker, Open in Window), `OutputPresetChip.swift`, `AppShell.swift` (transition chips), `SettingsView.swift` (present.emptyRepeatsOnce, present.nextSkipsBlanks) | `MxU.Slides.Windows/Views/Operator/*` (`AlertsPanel`, `TimersPanel`, `StagePanel`, `ScreensPanel`, `LiveToolbar`, `PreviewPopOutWindow`, `PresentSettingsSection`, `OperatorKit`), `ViewModels/MainViewModel.{Alerts,Timers,Outputs,Present}.cs` |
| `Sources/ArrangementPopover.swift`, `SectionPreviewPicker.swift`, `ServiceContinuousView.swift` (`ArrangementStrip`, `ArrangementPicker`) | `Views/Arrangements/*` (`ArrangementPicker`, `ArrangementStrip`, `ArrangementEditorWindow`), `ViewModels/MainViewModel.Arrangements.cs` |
| `Sources/ServiceContinuousView.swift` | `Views/ContinuousSlidesView.xaml(.cs)`, `Views/SlideViewSwitch.cs`, `ViewModels/MainViewModel.Continuous.cs` |
| `Sources/PresentLayoutController.swift` (`enterRunOnly`, `exitRunOnly`, `runOnlyModules`), `RunOnlyGate.swift`, `AppShell.swift` (`ModeSwitcher`), `SettingsView.swift` (Volunteer Mode, Tabs volunteers keep) | `Views/Volunteer/*` (`VolunteerLayout`, `VolunteerView`, `VolunteerModeButton`, `OperatorSettingsSection`, `PinDialog`), `ViewModels/MainViewModel.Volunteer.cs` |
| `Sources/ServicePlannerView.swift` (collapsing headers, the bulk menu, the row's Arrangement menu), `AppShell.swift` (Set All Transitions) | `Views/ServiceListExtras.cs`, `Views/ServiceMenus.cs`, `ViewModels/MainViewModel.ServiceList.cs` |
| `Sources/Modules/ControlBoardFolders.swift` and the modules' `draggablePayload` / `dropDestination` | `Views/Operator/BoardDragging.cs`, `DropLine.cs` (in `AlertsPanel`, `TimersPanel`, `ActionCombosWindow`, `SchedulerWindow`) |
| `Sources/LivePanel.swift` (the preview's `aspectRatio`) | `Views/Operator/PreviewShape.cs` |
| `Sources/PerformanceHUD.swift` (`TransitionChip`'s slider) | `Views/Operator/LiveToolbar.cs` (`LengthSlider`) |
| automerge-swift 0.7.2's prebuilt `automergeFFI.xcframework` (UniFFI) | `native/automerge-ffi` (the same `automerge` crate 0.7.2 behind a small C interface), built by `tools/build-automerge.ps1` into `MxU.Slides.Sync/Native/automerge.dll`; `MxU.Slides.Sync/Automerge/*` (P/Invoke with safe handles) |
| `Sources/ScreenMapEditor.swift`, the Screen Map page of `ScreenConfigurationView.swift`, `OutputPicture.swift` | `Views/ScreenMapWindow.cs` over `MxU.Slides.Core/Outputs/ScreenMapEditing.cs` |
| `Sources/ThumbnailStore.swift` (NSCache cost limits) | `Rendering/ImageCache.cs` in `MediaHub` |
| `Sources/VideoActionsTimeline.swift` (`VideoActionsCard`'s list, `VideoActionAddMenu`), `MediaEditorView.swift` (`addMark`, `moveMark`, `removeMark`, rename, a mark's actions) | `Views/VideoActionsWindow.cs`, `Views/MainWindow.VideoMarks.cs`, `MainViewModel.SaveVideoMarks`, over `VideoMarks.Renaming` / `WithActions` / `Removing` / `Clock` / `ParseClock` (a list, not a timeline) |
| The Mac's undo (Automerge per document, plus a journal of closures) | `MxU.Slides.Core/Undo/*`, `Core/Library/LibraryStore.Undo.cs`: each document's JSON before and after is recorded at the library, and an undo merges three ways |

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
- `Infrastructure/SelfTest.{Render,Editors,Timeline,Overlays,Capture,RenderFixes,Library,Show,Operator,Sound}.cs`, and `scripts/display-demo.ps1` (the output on a real second screen, photographed).
- Phase 5 helpers the Mac doesn't need:
  - `ServiceTrackingTimers.Tracks`: publish only when something changed; the Mac republishes on every refresh.
  - `Infrastructure/ThisComputer.cs`: this PC's id and the service versions it runs; the Mac uses UserDefaults.
  - `MissingMedia.NameMatches`: same-name suggestions after a folder scan.
  - Move up / Move down / Folder › for timers and alerts: `TimerBoard.StepTimer`, `StepFolder`, `PutInFolder`, and `BoardSteps`.
  - `ConfidenceScreenLayouts.Migrated`: the old single stage layout becomes each screen's.
  - Per-screen Live / Off in `Outputs/OutputManager.cs`.
  - `Audio/Video/VideoClockFollower.cs` and `TimeStretch.cs`: the Mac's AVPlayer keeps its own sound in step.
  - `MultitrackVarispeed.Reset` and `PlaylistLibrary.MoveBy`.
- `Infrastructure/SelfTest.Trail.cs`: the self-test's report written as it goes, and its watchdog for a stuck UI thread. `AudioDeviceList.SimulatesNoDevices` (`MXU_SLIDES_AUDIO=none`): act as a PC with no sound devices, as the GitHub test machine is.
- **Phase 6** (none of these has a Swift twin, so nothing needs porting when upstream changes; HOW-IT-WORKS sections 41–47):
  - `Core/Library/WindowsExtras.cs` (`LibraryStore.Extras`): Windows-only additions kept beside the library, fingerprinted so a Mac edit wins.
  - **Looks:**
    - `Core/Library/BundledFonts.cs`, `assets/fonts/` (15 OFL fonts with `OFL.txt`, `FONT-NOTICES.txt`, `SOURCES.txt`), `Views/AboutSection.cs`. `FontMap` also finds a library face by its full name.
    - `Core/Animation/KineticText.cs`, `OverlaySteps.cs`, `PresetSamples.cs`, `Core/Library/KineticExtras.cs`.
    - `Core/Library/StarterPacks.Worship.cs` (Arena, Broadcast, Modern Serif, Minimal Sans), `StarterContent.CountdownPresentation`, `LibrarySeeder.AddWindowsStarters` (`windows-starters.txt`).
    - `Core/Library/LowerThirdGallery.cs`, `ViewModels/LowerThirdGalleryViewModel.cs`, `Views/LowerThirdGalleryWindow.cs`, `Views/StandInPictures.cs`, `ViewModels/MainViewModel.Looks.cs`.
    - `ViewModels/OverlayEditorViewModel.Presets.cs` and `Views/AnimationPresetGallery.cs` (the Mac has a menu).
  - **Transitions:**
    - `Core/Animation/WindowsTransitions.cs` (the 25 kinds: the Mac's 7 and 18 of our own, with `ToShared`), `TransitionCurves.cs`, `PictureTransitions.cs`.
    - `Core/Library/TransitionExtras.cs`.
    - `Rendering/TransitionShaders.cs`, `TransitionEffect.cs`, `SceneView.Transitions.cs`.
    - `Views/Transitions/*`, `ViewModels/MainViewModel.Transitions.cs`, `Infrastructure/AppSettings.Transitions.cs`.
  - **Bible:**
    - `Core/Scripture/` (`BibleBooks`, `PassageReference`, `Bible`, `BibleCatalog`, `AbsentVerses`, `BibleSearch`, `ScriptureSlides`), with the KJV, WEB and ASV in `Core/Scripture/Bibles/` made by `tools/bible-convert.py`.
    - `ViewModels/ScriptureViewModel.cs`, `MainViewModel.Scripture.cs`, `Views/BibleWindow.cs`, `Infrastructure/AppSettings.Scripture.cs`. `MainViewModel.Live.cs` → `Advance` asks `StepScripture` first.
  - **Planning Center:** `Core/PlanningCenter/`, `Infrastructure/PlanningCenterVault.cs`, `ViewModels/MainViewModel.PlanningCenter.cs`, `Views/PlanningCenterWindow.cs`. The plan link is a Windows extra (`planning-center`).
  - **Browser remote and stage view:**
    - `LocalApi/Remote/` (`RemoteSite`, `RemotePairing`, `IRemoteBridge`, `Web/*`), `ViewModels/MainViewModel.Remote.cs`, `Rendering/ScenePictures.cs`, `Views/QrCode.cs` (QRCoder, MIT), `Views/ConnectSettingsSection.cs`. The `/remote` routes aren't part of the Mac's contract (`/openapi.json` is unchanged).
    - `MainViewModel.ApiServer.cs` → `PublishApi` pushes once the change that raised it has finished.
  - **Production control:**
    - `MxU.Slides.Production/*` (ATEM, OBS, vMix, DMX, lighting, the production steps, the simulators).
    - `Core/Automation/Actions/IExtraSteps.cs` and `ActionRouter.ExtraSteps`.
    - `LocalApi/ApiRouteTable.Windows.cs` (`/v1/windows/production`; `RouteTableMatchesTheMac` ignores `/v1/windows/`).
    - `ViewModels/MainViewModel.Production.cs`, `Views/Production/*`.
    - Stored as Windows extras `production` and `lighting`, `production.json` beside `settings.json`, and the passwords (`ProductionSecrets`) in the local folder.
  - **Motion backgrounds:**
    - `Core/Motion/*` (`MotionRecipe`, `LoopTiming`, `LoopNoise`, `MotionPalette`, `MotionColor`, `MotionRenderer`, `RenderCrew`, `Styles/*`).
    - `Broadcast/Media/RenderedVideoWriter.cs`, `Broadcast/Library/MotionBackgroundMaker.cs`; the recipe is a Windows extra (`motion-background`).
    - `Views/MotionBackgroundWindow.cs`, `Views/MainWindow.MotionBackgrounds.cs`, `ViewModels/MainViewModel.MotionBackgrounds.cs`, `Rendering/MotionPreviewer.cs`, `Rendering/MediaHub.Reload.cs`.
    - One change to a ported file: `Recording/Mp4SinkWriter.cs` got `CreateForEncoded(…, crashSafe: false)` for a plain MP4. The recorder's default is unchanged (crash-safe fragments).
  - `Infrastructure/SelfTest.{Looks,Transitions,Scripture,Connect,Production,Backgrounds}.cs`, and `Infrastructure/HeadlessBrowser.cs`: the self-test photographs the remote's pages in the PC's own Edge or Chrome, with no window.
- **Phase 7** (sections 48 and 49):
  - **Undo:** `Infrastructure/UndoWindows.cs`, `ViewModels/MainViewModel.Undo.cs`, `Views/EditBar.cs`, and in Core `UndoStep`, `UndoTarget`, `UndoDescription` (the step names).
  - **Clipboard and paste:** `Infrastructure/AppClipboard.cs` (the formats "MxU Slides: slides" and "MxU Slides: slide objects"); `WindowsExtras.CopyOwner` (pasted things keep their extras); paste rules beyond the Mac's (plain text becomes a text box, lyrics pasted into a song become slides, a mask pasted with its matte follows it).
  - **Locks and capo:** `Core/Editing/ObjectLocks.cs` (extra kind "object locks"); `SongCapo` and `Core/Stage/ConfidenceSceneBuilder.Capo.cs` (extra kind "song capo"). One change to a ported file: `ConfidenceSceneBuilder.SlideText` takes its `DisplayKey` from `StageKey`.
  - **Find and Replace:** `Core/Editing/FindReplace.cs`, `Views/FindReplaceWindow.cs`, `ViewModels/MainViewModel.FindReplace.cs`.
  - **Chord editor:** `Views/ChordEditorWindow.cs`, `ViewModels/MainViewModel.Chords.cs` (the Mac places chords in its canvas text editor).
  - **Picking and dragging:** `ViewModels/MainViewModel.SlidePicks.cs`, `Views/EditorDragDrop.cs`, `Views/DropFeedback.cs`, `Views/PlaylistDrops.cs`, `ViewModels/MainViewModel.Dropping.cs`, `ViewModels/TimelineEditorViewModel.Drops.cs`.
  - **Media look:** `Core/Library/MediaLookExtras.cs` (fit, zoom, crop, volume: a Windows extra on the file's hash), the look half of `Core/Media/MediaDressing.cs`, `SceneView.PlaceCropped`, `Rendering/MediaHub.Gain.cs`, `VideoSoundOptions.Gain`.
  - **Themes:** `Core/Library/ThemeKinetics.cs`; `KineticExtras.Stored/Remove/Copied`; `ThemeLibrary`'s fresh object ids, unique design names and renaming a design in the slides that use it; `ThemeApplication.OwnThemes.Keep` (the question's "No").
  - **Stage layout editor:** box cards (`StageLayoutEditing.MakeBox`, `FreeSpot`), safe margins, the sample/live switch, canvas sizes; slide notes on stage as the current slide's "Notes".
  - **Shared editor pieces:** `Views/AnimationTab.xaml`, `AnimationTabViewModel`, `IAnimationHost`, `CompositionObjects.cs`, `Views/EditorMenus.cs`, `SlideCanvasEditor.SnapTargets` and `Decorate`.
  - Auto Advance's "Apply to: its group / every slide"; changing a picture slide's Scale and a video slide's end from the slide menu (the Mac sets them only when it makes slides from notes); Theme Designs as its own window.
  - `Infrastructure/SelfTest.Editing.cs`, `SelfTest.Editors7.cs`.
- **Phase 8** (sections 50 and 51):
  - **Library browsing:** `Core/Library/LibrarySearch.cs` (ranked search over every kind), `LibraryMarks.cs` (song favourites and tags as the extra `library-marks`), `LibraryDates.cs` (`usage.json`), `LibraryCatalog.cs`, `LibraryBatch.cs`; `Views/LibraryPanel.xaml`, `LibraryMenus.cs`, `MediaBinBar.cs`, `LibraryDepthDialogs.cs`.
  - **Files from Explorer:** `Core/Library/OpenRequests.cs`, `Infrastructure/FileHandoff.cs`, `Views/MainWindow.Handoff.cs`, `scripts/file-types.ps1`, `scripts/uninstall.ps1`.
  - **Diagnostics:** `Core/Diagnostics/HealthChecks.cs`, `HangDetector.cs`, `PcCheck.cs` (Check this PC, `SelfTestSandbox`), `Infrastructure/SystemInfo.cs`, `HangWatch.cs`, `ProblemReport.cs`, `PcCheckRunner.cs`, `Views/DiagnosticsWindow.cs`, `ReportProblemWindow.cs`, `HelpMenu.cs`.
  - **Media:** `Rendering/MediaHub.ReloadInPlace.cs`; the music duplicate prompt in `ViewModels/MainViewModel.MusicImport.cs`.
  - **Backups:** every-launch and weekly backups and safety copies in `BackupFiles.cs`, `AppSettings.Library.cs` (settings from a backup, applied at the next start), `Views/BackupWindow.cs`, `RestoreWindow.cs`, `LibraryCareSection.cs`. A Windows backup is a zip of JSON documents, not the Mac's Automerge store.
  - **Operator:** `Infrastructure/AppSettings.Operator.cs`; the volunteer PIN (`VolunteerMode.HashPin`, `PinMatches`); the cheat sheet (`Views/Keys/CheatSheet.cs`); the plan-time picker (`ServicePlanTimes`, `MainViewModel.RunOfShow`); `ArrangementEditing.Duplicate` and `SetOrder`; `MainViewModel.ReanchorLive`; a folded row going live opens its group; `SongEditing.OrderText` folding repeats.
  - `Infrastructure/SelfTest.LibraryDepth.cs`, `SelfTest.LibraryCare.cs`, `SelfTest.OperatorDepth.cs`.
- **Phase 10** (section 52):
  - `MxU.Slides.Sync/Engine/*`: the two-way bridge between the Windows JSON library and a Mac library folder (`SyncEngine`, the replicas and `SyncLedger`, `SyncBackup`, `BlobMirror`, `WindowsLibrary`, `SyncLog`); `Format/SchemaCheck.cs`, `Format/DocumentDiff.cs` (list edits matched by id), `WindowsActor`.
  - `Core/Library/LibraryStore.cs` → `SaveIfUnchanged`, `DeleteIfUnchanged`, `NotifyChanged`.
  - `ViewModels/MainViewModel.Sync.cs`, `Views/LibrarySyncSection.cs`, `Infrastructure/AppSettings.Sync.cs`, `Infrastructure/SelfTest.Sync.cs`.
- **Phase 11** (section 53; the Mac has no Lottie or After Effects support):
  - `MxU.Slides.Core/MotionTemplates/*` (`LottieDocument`, `LottieFiles`, `MotionFields`, `MotionTimeline`, `MotionTemplate`, `MotionTemplateStore`, `LottieBuilder`, `StarterMotionTemplates`).
  - `MxU.Slides.MotionTemplates/*` (`Lottie/`, `Export/`, `AfterEffects/`): drawing with SkiaSharp.Skottie, live playback, MP4 export, the After Effects bridge. `tests/MxU.Slides.FakeAfterEffects` stands in for After Effects.
  - `ViewModels/MainViewModel.MotionTemplates.cs`, `Rendering/MediaHub.Motion.cs`, `Rendering/MotionPreviews.cs`, `Views/MotionTemplatesWindow.cs`, `AfterEffectsWindow.cs`, `LottieGuideWindow.cs`, `MainWindow.MotionTemplates.cs`, `Infrastructure/AppSettings.MotionTemplates.cs`, `Infrastructure/SelfTest.MotionTemplates.cs`.
  - Stored as Windows extras `motion templates`, `motion template animations` and `motion template uses`.
  - Small changes to ported files: `Rendering/SceneView.cs` (a video that stands in for a live template draws the template), `Rendering/MediaHub.LiveInputs.cs` (`motion::` pictures are live pictures), `ViewModels/MainViewModel.Overlays.cs` (an overlay goes up through `ForShow`).
- **MxU's autumn 2026 updates** (sections 54 to 56), the parts with no Swift twin:
  - **Notes:** `ProImport/Rtf/RtfNoteLines.cs` (and `RtfReader`'s opt-in `weights`), `Core/Notes/NoteTextEditing.cs` (Edit Text), `Core/Notes/NoteModels.cs` (copy and value equality for the generated note classes), `Views/Notes/NoteTileBadges.cs`, `AppSettings.Notes.cs`, `Infrastructure/SelfTest.Notes.cs`.
  - **Backup computer:** `Core/Follow/FollowCopy.cs` (copying a Windows booth's JSON items to a Windows backup); hooks in ported classes (`ActionRouter.Holds`, `TimerController.Acted`, `AudioBusStatus.EntryId`); `ThisComputer.Name`'s override (`MXU_SLIDES_COMPUTER_NAME`); `Infrastructure/SelfTest.Backup.cs`.
  - **Engine:** photo designs in the worship looks (`StarterPacks.Worship.cs` → `PhotoDesigns`), the seeder's gentle repair (`LibrarySeeder.RepairGently`), `MediaLooks.Spread` (the blurred copy grown so WPF's faded rim falls outside the frame), the Screen Map saved in `settings.json` (`AppSettings.ScreenMap`), `Broadcast/Rtmp/LoopbackRtmpServer.cs`, `Infrastructure/SelfTest.UpstreamCore.cs`, `SelfTest.UpstreamStreams.cs`.
- **Phase 12, the service assistant** (sections 57 to 60; the Mac has no assistant, so nothing here needs porting when upstream changes):
  - **Jobs to Claude:** `MxU.Slides.Assistant/AssistantClient.cs`, `AssistantService.cs`, `AssistantModels.cs`: a job sent through Anthropic's C# package with a fixed JSON shape, look-ups, retries, a time limit and costs; one job at a time on its own thread.
  - **Key, log, privacy:** `AssistantKeyVault.cs` (the API key under DPAPI), `AssistantLog.cs` (what was sent and received), `PrayerPrivacy.cs` (prayer-request lines held back).
  - **Before Sunday:** `NoteSuggester.cs` (Suggest with AI: passages marked "suggested" for the existing planner), `LyricsCleanup.cs` (pasted lyrics labelled, with a word-by-word check).
  - **The scripture spotter:** `Spotting/*` (`SpokenScripture`, `ScriptureSpotter`, `SpeechWorker`, `SpeechListener`): spoken references, the two-recognizer rule, the speech process (`MxUSlides.exe --speech-worker`) and its pipe.
  - **The Assistant window's jobs:** `LibraryFacts.cs` (the snapshot the model may look things up in), `TypedRequests.cs` (Ask), `ServicePlanReader.cs` (Order of service), `SlideChecks.cs`, `SlideTranslator.cs`, `Captions.cs`, `ServiceReport.cs` (with the local file `Assistant\service-runs.jsonl`), `SermonSummary.cs` and `Infrastructure/SermonSound.cs`.
  - **The app:** `Views/Assistant/*` (`AssistantWindow`, `AssistantSettingsSection`, `SpotterStrip`), `ViewModels/MainViewModel.Assistant.cs`, `MainViewModel.AssistantTasks.cs`, `Infrastructure/AppSettings.Assistant.cs`.
  - **Tests:** `MxU.Slides.Assistant/Testing/FakeAnthropicServer.cs` (the stand-in for Anthropic's API), `tests/MxU.Slides.Assistant.Tests`, `Infrastructure/SelfTest.Assistant.cs`, `SelfTest.AssistantTasks.cs`.
  - One change to a ported file: `LocalApi/OpenApiDocument.cs` (`RequestSchema` and `PathParameters` made public, so Ask can describe each route to the model).
  - Nothing is stored in the library or in Windows extras. The key, the log and the service-run record are in the app's local data folder.
- **Phase 13, the opener and montage maker** (section 61; the Mac has no twin):
  - `MxU.Slides.Core/Openers/*` (`OpenerRecipe`, `OpenerLooks`, `OpenerPlan`, `OpenerFrame`, `BeatDetector`): recipes, the five looks, what is on screen at any moment, the montage's shot plan, the countdown clock, beat detection.
  - `MxU.Slides.MotionTemplates/Openers/*` (`OpenerRenderer`, `OpenerPictures`, `OpenerVideoMaker`): drawing the frames with Skia over a motion background; an H.264 MP4 with AAC sound; filing it into the library.
  - `Broadcast/Media/VideoClipReader.cs` (a clip's pictures read in order, not in real time) and `Audio/Decoding/MusicFileReader.cs` (a music file as plain stereo samples).
  - `Views/OpenerWindow.cs`, `Views/MainWindow.Openers.cs`, `ViewModels/MainViewModel.Openers.cs`, `Rendering/OpenerPreviewer.cs`, `Infrastructure/SelfTest.Openers.cs`.
  - The recipe is a Windows extra (`opener`).
  - Changes to files already listed: `Broadcast/Media/RenderedVideoWriter.cs` can take an AAC sound track (`audioSampleRate`, `WriteAudio`); silent videos are written as before. In the ported `Broadcast/Media/MediaTranscoder.cs`, `OpenVideo`, `ReadVideoFormat`, `CopyPicture` and `VideoFormat` went from private to internal so `VideoClipReader` can share them; no behaviour changed. `Views/MainWindow.MotionBackgrounds.cs` got one line, so a media tile's menu also offers "Edit opener…".
- **Phase 14, the key feed for a video switcher** (section 62). A Windows-only addition: the Mac makes a similar picture with an output preset (its "Broadcast Feed" template: Slide and Overlays only, always over black, the slide's backdrop kept).
  - `Core/Outputs/KeyFeed.cs` (`KeyFeedMode`, `KeyFeeds`: the layers kept, the fill, what counts as a background, which transitions become a dissolve) and `Core/Outputs/OutputConfiguration.KeyFeed.cs` (the per-screen setting, `KeyedProgram`).
  - `Infrastructure/SelfTest.KeyFeed.cs`, `tests/MxU.Slides.Core.Tests/Outputs/KeyFeedTests.cs`.
  - Saved as `keyFeed` on the screen in this PC's `settings.json`, beside the screen's look. Nothing is stored in the library or in Windows extras.
  - Changes to files already listed: `Core/Outputs/ScreenSetup.cs` (the `keyFeed` field, saved and restored), `OutputConfiguration.cs` (removing a screen forgets its key feed) and `OutputConfiguration.Feeds.cs` (`PreviewProvider` calls `KeyedProgram`; one line). `Outputs/OutputManager.cs`, `OutputWindow.cs` and `SliceView.cs` got the backdrop colour and the transition swap; `Rendering/SceneView.cs`'s `Letterbox` redraws when it changes; `Views/ScreensWindow.xaml(.cs)` got the drop-down and the KEY chip; `Views/Operator/ScreensPanel.cs` and `ScreenRow` say "KEY".

## Not ported yet, not done, not verified

### Never tested against the real thing

These work in the unit tests and the self-test, which use stand-ins. Nobody has run them with the real thing yet. Treat them as untested until someone does.

- **The backup computer with a real Mac.** Tested between two copies of the Windows app on one PC, and against the Mac's own tests for the messages. The steps to try it are in [upstream-2026-11/backup.md](upstream-2026-11/backup.md).
- **Library sync with a real Mac.** Tested against `MacSimulator` (documents written the way the Mac's code writes them) and the Mac's own fixtures. The steps are in [phase10/sync.md](phase10/sync.md), section 6.
- **The After Effects bridge with a real After Effects.** Tested with `tests/MxU.Slides.FakeAfterEffects`. The steps are in [phase11/motion.md](phase11/motion.md), section 10.
- **Planning Center with a real token.** The import (Phase 6) has only been run against made-up answers.
- **The service assistant with the real Anthropic API** (Phase 12). Every test talks to a stand-in server, `FakeAnthropicServer`. No call was ever made to Anthropic. See "Phase 12" below.
- **Anything that listens, with a real voice** (Phase 12): the scripture spotter, live captions and the sermon transcript were tried only with Windows' synthetic voice.
- **A montage with real music and photos** (Phase 13). The tests make their own click tracks and coloured pictures. See "Phase 13" below.
- **The key feed never tried on a real ATEM** (Phase 14). The pictures the app sends were checked pixel by pixel, on stand-in network outputs. No switcher has keyed them, and it was not seen on a real second HDMI display. See "Phase 14" below, and [phase14/atem-key-feed.md](phase14/atem-key-feed.md).

### Skipped on purpose

- **Phase 9 (broadcast and devices)** was deliberately skipped. Nothing from it is in this code. The older "Broadcast extras" list below still stands.

### Phases 4 to 6

- **Timeline editor extras:** the slide thumbnail that pops up over a slide cue (a tooltip names it),
  trackpad pinch (Ctrl+wheel zooms), MIDI Out and the other steps that need settings in the
  actions lane's quick **+** menu (add them with **Edit Actions…**, which is the full step editor), "Track Settings…" per track (Windows
  has no app-wide mixer: the tracks setup sets each family's output, and M/S each track), and
  the track-set list in the library (track sets are added from **+ beside Tracks › Add a Track Set**).
  The timeline editor still keeps its own undo history (`TimelineEditHistory`), separate from the library's.
- **Animation timeline** (`AnimationTimelinePanel.swift`): ported for the overlay and theme editors (`Views/AnimationTimelineView.cs`). The main window's slide editor doesn't show it.
- **Automation editor extras:** dropping library items onto a combo or the scheduler board, the "Edit…" button beside a step's media or music, and the capture-source preview line on scheduler rows. Audio-input steps show and keep their settings, but Windows offers no audio inputs to pick (the router skips those steps). Undo in the combo and scheduler windows is now the library's (section 48); it hasn't been checked edit by edit.
- **Overlay editor extras:**
  - Its canvas has no Ctrl+C and Ctrl+V yet (Duplicate works).
  - *One step per line/word* (Phase 6) splits the whole text, and the pieces follow one another instead of waiting for clicks. The slide editor has no presets gallery.
- **Mask editor:** the Mac shows it inside the screen's settings. Here it is its own window, opened from the Screens window.
- **Looks, Windows differences:** a screen can have its own look (the Mac sets looks only in output presets). There is no output preset editor on Windows yet (the Screen Map window edits a preset's placements and custom layers only), so preset looks come from imported or Mac-made presets. The Mac's grid tile "N pages" strip and look thumbnails (`PosterLookImage`, `ThumbnailStore.posterLook`) aren't ported, and neither are `SlideSceneBuilder.lookKey/withoutLayerMedia/posterLookScene` (they serve those thumbnails). `PosterLookSweepTests.swift` checks Mac UI source and has no Windows twin.
- **Bold and italic in looks** are read from the font's name (`PlaceholderAnimations.FontTraits`), not from the font file as Core Text does.
- **Echo and ghost trails** use a small (≤ 384 px) software picture for the history; effects listed before them that need pixel shaders aren't in that picture (the live item still has them). Trails on adjustment layers ("effects apply below") aren't drawn, as no adjustment-layer effects are on Windows yet.
- **Broadcast extras:** SRT (the Mac uses libsrt through SRTHaishinKit, a native library; Windows would need that binary shipped, or a complete managed SRT with its handshake, retransmission and AES encryption, so an `srt://` destination fails at once with "SRT isn't available on Windows yet" while the preset's other destinations carry on); HDR/HEVC streaming and ProRes (no Windows encoder path; HEVC/HDR destinations stream SDR H.264, ProRes presets record H.264); platform sign-in (YouTube/Facebook OAuth needs the church's own API credentials), so streams use a server URL and key. The Mac's mixer (sound inputs, video sound and the multitrack sent into mixes, per-mix stream gain, extra mixes) isn't on Windows, so a mix carries the music buses only, and the only mix is the main one. Video sound has its own output (device, channels, delay: Settings › Sound), but it isn't sent into mixes. The "Check Sync…" sheet isn't ported. From MxU's autumn 2026 stream changes: HaishinKit's frame-rate halving, HLS upload measurement, and the mixer and DeckLink heartbeat summaries.
- **Show flow extras (Phase 5):**
  - *Editing auto-advance:* done in Phase 7, except the Mac's bulk edit of several picked slides at once (the sheet has "Apply to: this slide / its group / every slide" instead).
  - *Transport:* the floating transport strip and trim handles on the strip's scrub bar (the media editor has trim handles).
  - *Service list:* the marquee (drag a box to choose rows).
  - *Clear:* tracks (stems) count as Music for the green state only while the Music tab plays.
- **Rendering differences (Phase 5):**
  - *Word spacing on ordinary text boxes:* it's drawn on path text and tickers only.
  - *Keystone with tilt or swing:* tilt and swing are still flattened to a squash, so the combination is approximate.
  - *Sharing fonts through the library* (`FontFile` documents): fonts in the `fonts` folder are used; library sync doesn't carry font files yet.
  - *A direct device reference* in a slide's live input shows only if a named input uses that device.
  - *Gapless loops:* a loop is a seek back to the in point, and the out point is checked each screen refresh (within a frame of the Mac's).
- **Sound extras (Phase 5):** a playlist's Mix picker and per-playlist mixes, mix faders, output trims and mutes (they come with the mixer); "Edit in Library" on a track.
- **Operator panel extras (Phase 5):** undo for timer edits and timer board moves (timers are saved beside the settings, not in the library, so the library's undo doesn't see them). Live / Off is per screen, not per display. The look chip switches presets; its last item opens the Screens window, as there's no output preset editor.
- **Library exchange extras (Phase 5):** transcoding to HEVC, and ProRes playback: Windows has no decoder.
- **Phase 6, not done yet:**
  - *Planning Center:*
    - downloading media attachments (they're listed as "to add");
    - OAuth sign-in (Personal Access Tokens only);
    - writing back to Planning Center;
    - people;
    - applying the plan's song key (`ServiceLinkLogic.PendingSongKey` is ported but not wired);
    - sticky link rules.
  - *Browser remote:*
    - media and overlay bins, and audio;
    - a stage layout chosen per device (the stage view has one built-in look);
    - https, without which a tablet browser won't hold its wake lock.
  - *Bible:*
    - a remote (the Local API or the stage display) can't drive it yet;
    - search stems are English-only;
    - the ASV keeps eBible.org's [brackets];
    - a saved side-by-side passage whose theme is changed later keeps the old design's positions.
  - *Production:*
    - ATEM transition settings, SuperSource, audio and tally;
    - OBS per-scene transitions and filters;
    - the other vMix functions;
    - sACN discovery;
    - a fixture library;
    - undo for production edits (not checked since the library's undo arrived in Phase 7);
    - copying production steps when a slide, combo or cue is duplicated or pasted;
    - a timeline cue with only production steps reads "No actions".
  - *Motion backgrounds:*
    - drawn on the CPU only (no graphics-card path);
    - no starter backgrounds made on first use;
    - the slowest styles (Starfield, Liquid, Geometric) haven't been sped up.
  - *Transitions:*
    - the picker's slider goes to 3 s (the Mac allows 30 s typed);
    - a `.mxuslides` file carries only the shared fallback;
    - overlays, alerts and cameras still cut, as on the Mac.

### Phase 7: editing and the editors

- **Undo:** the theme, media and stage layout editors don't open an undo scope of their own yet (each `Flush()` has a comment marking the place). The timeline editor keeps its own history.
- **`bakedSlide`** (the Mac's "No Theme" baking): Windows' *Blank* sets `Unthemed` without copying the theme's look into the slide.
- **Clipboard:** copying text with its chords inside a text box (the Mac's `ChordClipboard`) isn't wired; the chord editor's bracket box carries chords as text. Formatted text pasted from another app keeps only its words (the Mac's `PastedFormatting`).
- **Drag and drop:** slides dragged between presentations. Media dropped on a picture slide that waits for media doesn't fill its photo spot (`SlideMediaFacts.FillPlaceholder` is ported in Core but no drop calls it).
- **Text:** the Mac's canvas text editor (typing on the slide itself); the inspector's text box and the words bar do that job here. All Caps on *selected* words is saved in the style run, but only a whole box's All Caps is drawn.
- **Chords:** Numbers, Numerals and Do-Re-Mi notation are drawn, but not chosen in the chord editor.
- **Stage layouts:** multi-view tile layouts (`MultiViewTiles`) open read-only.
- **Effects:** *Apply to layers below* (`effectsApplyBelow`, adjustment layers) is kept but not drawn.
- **Media editor:** a new trim or speed applies the next time a playing video is fired, not to the copy already playing.
- **Bulk Auto Advance and Apply Theme** from several picked slides (`AutoAdvanceWindow.ForSlides` and `ThemePickerWindow.ForSlides` take a list, but the slide menu passes one slide).

### Phase 8: the library and the operator

- **Restoring a Mac backup** from the Restore window: refused with a message. (Library sync can use a Mac backup folder as its shared folder: section 52.)
- **Team drives** and their cloud search rows (`TeamDriveLogic.searchRows`, `cloudMatches`).
- The Mac's `DeleteMediaSheet` "This can't be undone" line isn't shown, since here the item and its file go to the Trash folder.
- **Notes' unattended media import:** `ImportMediaFilesAsync(…, unattended: true)` is ready, but nothing calls it with `unattended: true` yet.
- **Shortcuts:** the Mac's editor commands (Bold, Italic, Underline), sidebar toggles and Settings-page keys.
- **Volunteer mode:** saved Present layouts ("Volunteer Layout"); the Local API and the browser remote aren't limited by it.
- **Continuous view:** sticky card headers, inline playlist editing, and dropping slides between cards.
- **Motion backgrounds:** replacing one reopens it twice in a row (`MediaFilesChanged`, then `MediaHub.ReloadSilentVideos`). Harmless; a later tidy-up could drop one.

### Phase 10: library sync

- The Mac's cloud sync protocol (its client isn't in the Mac repository). Windows syncs through a folder.
- The kinds Windows sync doesn't carry: notes, font files, slide-building settings and service-link rules. They're left exactly as they are in the folder. (Notes themselves now work on Windows, section 54, but sync doesn't carry them yet.)
- The `fonts` folder, media posters (`thumbnails`) and last-used dates (`usage.json`).
- Station documents (a computer's own set-up stays on it, as between Macs).
- Windows extras between two Windows PCs.
- Whole numbers above 2,147,483,647: a document with one is refused, not cut short.

### Phase 11: motion templates

- Lottie features Skottie doesn't draw (most After Effects expressions, some effects) are drawn without them. Bodymovin's "Glyphs" text (text turned to shapes) can't be edited.
- Picture fields take pictures from files; there's no picker for pictures already in the library.
- The After Effects bridge changes text layers and colours by layer. It doesn't read or set Essential Graphics properties, and can't list compositions without `AfterFX.exe`.

### MxU's autumn 2026 updates

- **Custom layers:**
  - the transport strip's layer picker doesn't list them (`MainViewModel.TransportPicker` returns the fixed layers; their videos show with Foreground Videos);
  - the Clear row has no button for them (Clear Media does clear them, and a Clear Layer step can name one);
  - the Local API can't clear one by its id;
  - the slide grid's layer chips (`SlideLayerFacts.swift`);
  - transitions on a custom layer follow Foreground Videos' setting.
- **Make Slides:**
  - the "Scale and Blur (tall picture)" caption on a row isn't shown (the tall picture still gets Scale and Blur);
  - marking part of a paragraph, and editing rich text in place (the notes window marks and edits whole lines);
  - the Add ghost tile on the slide grid (an Add is a badge on the slide before it);
  - pasting HTML from a browser or Mail, with its pictures and video links (Paste takes plain text);
  - web pictures in notes; `.doc` and `.rtfd` files;
  - `NoteTextReplacementTests` and the `*SweepTests` (they test the Mac's own text view and Swift source).
- **Backup computer:**
  - copying items between a Mac and a PC (`FollowCopy.swift`'s `mergeCopy` and `FollowCopyTests`): a Windows backup skips a Mac booth's Automerge items;
  - *Start Stream Here* after taking over from a streaming booth;
  - the unconnected-screens warning (`Follow.unconnectedScreens`);
  - `FiresTopicSweepTests` (a sweep over the Mac's Swift source; the C# fire points are covered by the self-test).
- **Video marks:** the Mac's Video Actions *timeline* (pins you drag under a preview, nudge by a frame, a mark's colour, undo). Windows edits marks as a list in its own window. A video countdown's target (the end, the next mark, a named mark, counting up) is chosen in the stage layout editor (`StageLinkViewModel`); the slide inspector doesn't offer it.
- **Screen Map:** the shared-outputs page ("Share it / Take it over"), `networkCarryFeed`, `restoreDisplayClaim`, the full output picture (Windows shows the screen's frame, rulers and placed layers, not its live picture), thumbnail chips, and dragging or resizing one placement by hand.
- **Dissolves:** Windows fades the two stacks as containers; the Mac mixes them in a shader, so with two opaque stacks the middle is slightly darker here.
- **Media engine:** the live-input dropout note and the still-image tally; the sRGB encode pass of `OutputMirror` (Metal-only).
### Phase 12: the service assistant

Read this group before trusting the assistant on a Sunday. The walkthrough is [phase12/assistant.md](phase12/assistant.md).

Never verified:

- **Nothing in Phase 12 was verified against the real Anthropic API.** Every test uses a stand-in server on the PC (`FakeAnthropicServer`). There was no key on the build PC, and calls cost money. So nobody knows yet whether the real API accepts every request exactly as sent (the key's header, the picture blocks, the tool definitions), or how well the real model reads a bulletin, picks an action, translates, summarises or judges a slide from a picture.
- **The scripture spotter, live captions and the sermon transcript were tried only with Windows' synthetic voice**, never with a real microphone or a real preacher. A noisy hall, accents and music under speech were not tried. Captions and the transcript had about ten seconds of that voice from a `.wav` file. A long recording and a video file (`.mp4`) were not tried. The sound-card path into the spotter was not run with a device.
- **The published single-file build's speech worker was not run.** `scripts/publish.ps1` makes one `.exe`, and the app starts a second copy of itself as `--speech-worker`. That was only run from the ordinary build.
- **The slide checks' thresholds in a real room.** They were set by reasoning and four planted slides, not with a projector.
- Processor use was measured with the self-test's outputs running, not with a real projector, stream and NDI together.

Known behaviour to be aware of:

- **Translated live captions reach the screen without the operator reading each one.** The operator's switch covers every sentence after it. This bends the rule "the AI suggests, a person decides". Captions as heard use no AI.
- Windows' recognizer is weak on real speech. Even the synthetic voice's "Grace is not something you earn" was heard as "Greece is not something you learn". A transcript, and any summary made from it, needs a person's read.
- Captions and the spotter each start their own speech process if both are on, so the processor cost is doubled.
- A background picture that hasn't finished loading when a slide is checked reads as black.
- A slide check's **Fix** has no Undo entry. Undo it by hand in the editor.

Not built:

- **Whisper** as a more accurate recognizer (it needs a model download, which needs the user's yes).
- Translated scripture: no non-English Bible is built in, so scripture slides are left as they are.
- Captions burned into the stream only; a caption box in a stage layout (a new box kind can't be written into a shared layout).
- A keyboard shortcut for the Ask box; requests that need two actions.
- A chart in the service report, or emailing it.
- Reading the pictures in a scanned bulletin (a PDF or Word file is read as text).
- The spotter backing off when the outputs' frame timing slips (it runs below normal priority and shows its processor use).
- The spotter in other languages: its grammar and number words are English.
- Suggest with AI choosing a design or lower third per passage (the church's design map chooses, as for Pick for me).
- A fallback model when Claude declines a request (a decline is reported and nothing changes).

### Phase 13: the opener and montage maker

The walkthrough is [phase13/openers.md](phase13/openers.md).

- **The montage's beat detection has only seen synthesized click tracks.** The tests make their own music and coloured pictures. No real song and no real photos were used, and the detector has not been tried on a recording of a band.
- **Nobody has listened to a finished montage.** The tests check that the file has a sound track of the right length, not how it sounds.
- **Opener sources are referenced by absolute path, not copied into the library.** Pictures, clips and music are named in the recipe by their full path on this PC. If one has moved when the opener is made again, it's left out. Because they aren't in the library, a library backup doesn't hold them.
- **HEIC photos** (an iPhone's default) are unreadable: Skia can't decode them, and they show as missing. Export them as JPEG first.
- The preview is silent. You hear the music only in the finished video.
- Words are set simply: left to right, one glyph per letter, no kerning pairs. Arabic, Hebrew and Indic scripts won't join or order correctly.
- A clip's own sound isn't used. A clip longer than its shot is cut off, and a shorter one holds its last picture.
- 4K and 60 fps are in the recipe and the renderer but not in the window: it always makes 1080p at 30.
- A motion background and an opener can be made at once if the background is started second. Both finish; both are slower.
- No free-form editor, by design: you choose among looks.

### Phase 14: the key feed

The walkthrough is [phase14/atem-key-feed.md](phase14/atem-key-feed.md).

- **Never tried on a real ATEM.** The ATEM set-up steps in the walkthrough were written from how Blackmagic's keyers work in general, not from a session with the switcher.
- **Not seen on a real HDMI display.** The self-test's pictures come from stand-in NDI outputs, which draw with the same `SliceView` as a display window. The display window's own backdrop (`OutputWindow.SetBackdrop`) and the letterbox bars on a display whose shape differs from the screen's have no picture.
- **A soft edge over green keys with a fringe.** Glows, shadows and the smooth edges of letters are blended with the green, as any green-screen picture is. The app does not send a separate key signal over HDMI.
- A slide's full-screen picture counts as a background, so a photo slide shows nothing on a key feed.
- An overlay or alert moved to a media layer is hidden on a key feed.
- Masks, keystone, colour correction and edge blending are left as they are on a key feed screen, though they make no sense there. Nothing switches them off or warns.
- On a key feed, blur dissolve, film burn, the fades through a colour and the picture transitions all play as a plain dissolve.
- The key colour can't be changed (black, or pure green), and there is no blue.
