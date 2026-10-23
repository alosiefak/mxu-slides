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
| `SlideScene/…/SlideSceneBuilder.swift` | `MxU.Slides.Core/Scene/SlideSceneBuilder.cs` (no animation, paging or masks yet) |
| `SlideScene/…/ShowState.swift` | `MxU.Slides.Core/Scene/ShowState.cs` (no animation exits yet) |
| `RenderEngine/…/SceneModel.swift` | `MxU.Slides.Core/Scene/SceneModel.cs` |
| `PresenterCore/…/Documents/SlideObjectNormalization.swift` | `MxU.Slides.Core/Scene/SlideObjectNormalization.cs` |
| `PresenterCore/…/MediaCueRouting.swift` | `MxU.Slides.Core/Scene/MediaCueRouting.cs` |
| `SlideScene/…/ConfidenceScene.swift` | `MxU.Slides.Core/Stage/TimerSnapshot.cs`, `ConfidenceInfo.cs`, `ConfidenceSceneBuilder.cs`, `Timecode.cs` |
| `SlideScene/…/LinkedText.swift` | `MxU.Slides.Core/Stage/LinkedText.cs` |
| `SlideScene/…/TimerPattern.swift` | `MxU.Slides.Core/Stage/TimerPattern.cs` |
| `SlideScene/…/VisibilityRules.swift` | `MxU.Slides.Core/Stage/VisibilityRules.cs` |
| `PresenterCore/…/AlertTokens.swift` | `MxU.Slides.Core/Stage/AlertTokens.cs` |
| `PresenterCore/…/Library/ConfidenceLayoutTemplate.swift` | `MxU.Slides.Core/Stage/ConfidenceLayoutTemplate.cs` |
| `PresenterCore/…/Library/StarterPacks/StarterPacks.swift` | `MxU.Slides.Core/Library/StarterPacks.cs` (layouts and colours, no animation steps) |
| `PresenterCore/…/Library/StarterPacks/WelcomeDeck.swift` | `MxU.Slides.Core/Library/StarterContent.cs` (wording adapted for Windows) |
| `PresenterCore/…/Library/GroupPaletteLogic.swift` (defaults) | `StarterContent.GroupPalette` |
| `PresenterCore/…/KeyboardControl.swift` (default keys) | `MainWindow.xaml.cs` → `OnPreviewKeyDown` |

The Mac tests for these files are ported too, in `apps/windows/tests/MxU.Slides.Core.Tests/{Lyrics,Stage}`. When you port a change, port its test.

## Re-implemented for Windows (same behaviour, different code)

| Mac | Windows |
|---|---|
| `RenderEngine/TextRasterizer.swift` (Core Text) | `MxU.Slides.Windows/Rendering/TextLayout.cs` (WPF `FormattedText`) |
| `RenderEngine/Compositor.swift`, `ShapeRasterizer.swift` (Metal) | `MxU.Slides.Windows/Rendering/SceneView.cs`, `Painting.cs` |
| `MediaEngine` (AVFoundation) | `MxU.Slides.Windows/Rendering/MediaHub.cs` (Media Foundation via `MediaPlayer`) |
| `OutputEngine`, `Sources/ScreenConfigurationView.swift` | `MxU.Slides.Windows/Outputs/*`, `Infrastructure/Displays.cs` |
| `PresenterCore/MediaImport/BlobStore.swift` + Automerge library | `MxU.Slides.Core/Library/LibraryStore.cs` (JSON files; same documents, different container) |
| `Sources/*.swift` (SwiftUI screens) | `MxU.Slides.Windows/Views` + `ViewModels` |
| `RenderEngine/…` font names (Core Text) | `MxU.Slides.Windows/Rendering/FontMap.cs` (maps Mac names to installed Windows fonts) |

## Windows-only

- `Lyrics/SongEditing.cs`: editing a song as text, and play orders like `V1, C x2`.
- `Infrastructure/SelfTest.cs`
- `Infrastructure/StarterBackgrounds.cs`
- `scripts/*.ps1`

## Not ported yet

- **Animations and builds:** `AnimationSequence`, `AnimationEvaluator`, `PlaceholderAnimations`, `Paging`.
- **Overlays editor, props, masks, effects** beyond blur.
- **Import:** `ProImport` (ProPresenter), `PPTXImport` (PowerPoint).
- **Remote control:** `LocalAPI`.
- **Audio:** `AudioEngine` (multitrack, audio playlists).
- **Video outputs and streaming:** `NDIKit`, `DeckLinkKit`, `StreamEngine`, recording.
- **Sync and backups:** reading the Mac's Automerge library folder directly. Today the two apps share documents, not the library folder itself.
