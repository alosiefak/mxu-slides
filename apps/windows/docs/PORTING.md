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
| `SlideScene/…/ThemeOverride.swift` | `MxU.Slides.Core/Animation/PlaceholderAnimations.cs` → `OverrideObjects` (font traits from the name; see below) |
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
| `PresenterCore/…/Diagnostics/ThreadStackCapture.swift` | `MxU.Slides.Windows/Infrastructure/ThreadStacks.cs` (Microsoft's ClrMD reads the stacks from a snapshot of the app; so far only the self-test's hang watchdog uses it) |
| `MediaEngine/…/MediaEngine.swift` (`setAudioOutputDevice`, `setAudioOutputDelay`, `setAudioVolume`), `MediaPlayer.swift` (`audioOutputDeviceUniqueID`), `VideoAudioTap.swift` (the playout delay): AVPlayer plays its own sound | `MxU.Slides.Audio/Video/` (`VideoSound`, `VideoSoundPlayer`, `VideoClockFollower`, `TimeStretch`), `MxU.Slides.Windows/Rendering/MediaHub.Sound.cs`: the picture player is muted and our engine plays the sound, following its clock |
| `Sources/Modules/AlertsModule.swift`, `TimersModule.swift`, `ConfidenceModule.swift`, `OutputsModule.swift`, `Sources/LivePanel.swift` (preview, screen picker, Open in Window), `OutputPresetChip.swift`, `AppShell.swift` (transition chips), `SettingsView.swift` (present.emptyRepeatsOnce, present.nextSkipsBlanks) | `MxU.Slides.Windows/Views/Operator/*` (`AlertsPanel`, `TimersPanel`, `StagePanel`, `ScreensPanel`, `LiveToolbar`, `PreviewPopOutWindow`, `PresentSettingsSection`, `OperatorKit`), `ViewModels/MainViewModel.{Alerts,Timers,Outputs,Present}.cs` |

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
- **Broadcast extras:** SRT (the Mac uses libsrt through SRTHaishinKit, a native library; Windows would need that binary shipped, or a complete managed SRT with its handshake, retransmission and AES encryption, so an `srt://` destination fails at once with "SRT isn't available on Windows yet" while the preset's other destinations carry on); HDR/HEVC streaming and ProRes (no Windows encoder path; HEVC/HDR destinations stream SDR H.264, ProRes presets record H.264); platform sign-in (YouTube/Facebook OAuth needs the church's own API credentials), so streams use a server URL and key. The Mac's mixer (sound inputs, video sound and the multitrack sent into mixes, per-mix stream gain, extra mixes) isn't on Windows, so a mix carries the music buses only, and the only mix is the main one. Video sound has its own output (device, channels, delay: Settings › Sound), but it isn't sent into mixes. The "Check Sync…" sheet isn't ported.
- **Show flow extras (Phase 5):**
  - *Editing auto-advance:* the Mac's AutoAdvanceControls, the bulk menu, a deck's Slide Show setting and a media item's Auto Advance… (Phase 7). The fields are read and honoured, so imported and Mac-made timers work.
  - *Transport:* the floating transport strip and the scrub bar's trim handles.
  - *Service list:* collapsing headers, multi-select (bulk Hide, bulk Transition), the picker's Upcoming/Earlier grouping, plan times (the rules are ported in `ServiceRunOrder`, with no picker), and undo for service edits.
  - *Clear:* tracks (stems) count as Music for the green state only while the Music tab plays.
- **Rendering differences (Phase 5):**
  - *Theme picking for some slides:* the Mac's Apply Theme to selected slides, and its question before a deck's theme replaces slides' own themes. Windows draws per-slide themes, but its Theme picker changes only the deck's theme.
  - *Word spacing on ordinary text boxes:* it's drawn on path text and tickers only.
  - *Keystone with tilt or swing:* tilt and swing are still flattened to a squash, so the combination is approximate.
  - *Sharing fonts through the library* (`FontFile` documents): there's no shared library folder yet; fonts in the `fonts` folder are used.
  - *A direct device reference* in a slide's live input shows only if a named input uses that device.
  - *Gapless loops:* a loop is a seek back to the in point, and the out point is checked each screen refresh (within a frame of the Mac's).
- **Sound extras (Phase 5):** a playlist's Mix picker and per-playlist mixes, mix faders, output trims and mutes (they come with the mixer); dragging songs within a playlist (↑ ↓ and Move Up/Down instead); undo for playlist edits; song favourites and tags; "Edit in Library" on a track.
- **Operator panel extras (Phase 5):** dragging alerts and timers (Move up/down and Folder › instead); undo for alert and timer edits and board moves; volunteer (run-only) mode; the Stage panel's "Edit Layout in Library" and folder submenus (there's no stage layout editor); the main preview keeping its target's shape (pop-outs do); the transition chips' inline slider. Live / Off is per screen, not per display. The look chip switches presets; its last item opens the Screens window, as there's no output preset editor.
- **Library exchange extras (Phase 5):**
  - the export panel's other formats (PDF, slide images, plain text, ChordPro): Export makes `.mxuslides` only;
  - opening a `.mxuslides` by double-clicking it in Explorer: there's no file association, so import it or drop it on the library;
  - the duplicate prompt for music imports;
  - reloading a live video in place after its file is replaced;
  - transcoding to HEVC, and ProRes playback: Windows has no decoder.
- **Sync with the Mac's library folder** (Automerge). The Mac uses Automerge's Rust core through a prebuilt Apple-only binary. Windows needs the same core built for Windows, which means installing the Rust toolchain to build it and shipping a native DLL. The toolchain will be installed to build it from source (Phase 10). Until then the two apps share documents (`.mxuslides` files), not the library folder.
