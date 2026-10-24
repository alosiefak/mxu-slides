# Phase 4: the render pipeline (looks, paging, transitions, trails)

This file holds the pieces to merge into `HOW-IT-WORKS.md` and `PORTING.md`. Section numbers are suggestions.

---

## For HOW-IT-WORKS.md

### 27. Looks: one slide, a different design on each screen (Phase 4)

A church often wants the projector to show the lyric full screen while the livestream shows the same words as a lower third over the camera. ProPresenter calls this *Looks*. Here a screen's look is a theme it shows the live slide through, while every other screen shows the slide as designed.

- **Setting one:** **Screens & Settings › More screens…**, pick the screen, then **Look**. Choose a theme, and in the second box which of its designs to use: all of them, one folder (*Only its "Lower Thirds" designs*), or one design (*Just "Lyrics (Lower Third)"*). The choice is saved with the screen in `settings.json` (`slideThemeId`, `slideThemeFolder`, `slideThemeSlideId`: the same names the Mac uses for this in output presets).
- **Output presets can switch looks too**, as on the Mac. A preset's *slide theme* for a screen beats the screen's own look while the preset is active. Switching presets (a slide action, a combo, the Local API) changes looks live. The active preset is now applied at launch, as on the Mac.
- **How a look restyles a slide:** `Scene/ShowState.cs` → `AddSlide`. The slide's text loses its own font, size and position and takes the look's placeholder instead, keeping only the emphasis the look allows (bold words stay bold, a bigger word stays bigger in proportion: `PlaceholderAnimations.OverrideObjects`). A slide can also choose which design it uses in a given theme (`OverrideDesigns`).
- **One scene per look:** `RenderOutputs` in `ViewModels/MainViewModel.Live.cs` builds the audience scene once as designed and once per look some screen shows (`Show.AnimatedScene(slideThemeOverride: look)`). `OutputManager.SceneFor` gives each screen the one for its look. The program feed (recording, streaming) and the operator preview show the slide as designed.
- **Builds count across looks:** every look shares the slide's clock. If only the stream's lower third has a click-off Out, the first Next plays it on the stream and the second moves on, everywhere. `ShowState.SlideClickCount` takes the most clicks any look needs.
- **Holding a look:** going from verse 1 to verse 2 on the same lower-third design, the stream doesn't rebuild the plate; it keeps it up and fades the new words in (`HoldsLook`, `HoldFade`).
- **Hand-offs:** when the next slide makes a look go away (its preset drops it, the slide needs another design, or the slide is blank), that look plays its Outs first, then the slide lands everywhere (`ShowState.Handoff`, `MainViewModel.Fire`). The wait is timed on the show clock, and pressing anything else meanwhile cuts straight through.

### 28. Long lyrics turn pages (Phase 4)

A lower third has room for two lines. Put a long verse on it and the text would shrink to nothing, so designs mark their text box *page on click*: text that doesn't fit becomes pages, and Next turns to the next page before the slide moves on.

- `Rendering/TextLayout.cs` → `Pages` measures it, the way the Mac's `TextRasterizer.pages` does: shrink as far as the minimum size allows, then cut into runs of whole lines that fit. Text on a path, a ticker and a block scroll never page.
- `Core/Animation/Paging.cs` turns the pages into click steps: the text box shows page 1 and each further page is a *morph* step that swaps in the next page's text, placed after the build-in and before the design's Out.
- `ShowState.Pager` is set to `TextLayout.Pager` the first time the outputs are drawn. Without it nothing pages, which is how the unit tests can count clicks without fonts.
- Pages turn the way slides change (Settings › Transition): a cut, or a fade of the same length (`ShowState.PageTurn`).
- The stage display's "build 2 of 4" counts page turns, because they are clicks.

### 29. Each layer changes its own way (Phase 4)

Until now every change on the outputs used one app-wide cut or dissolve. Now each layer changes on its own, decided exactly as the Mac decides it (`Core/Animation/LiveTransitions.cs`):

- A slide comes in with **its own transition**, else **its presentation's**, else the **room default** (Settings › Transition).
- A background a slide brings uses the **media item's own transition**, else the presentation's. Media fired on its own uses its own transition, and so does media cleared off. Otherwise media dissolves over 0.7 s (Settings › Transition set to Cut makes media cut too).
- Overlays, alerts and live cameras cut: overlays play their own In and Out animations.
- Only layers whose items actually change transition. A clock ticking, a build playing or a page turning never fades.

The transitions are Cut, Dissolve, Fade Black/White/Colour, Blur Dissolve and Film Burn (the Mac has no push or wipe). `Core/Animation/SceneTransitionEngine.cs` composites them: leaving items get `::leaving` ids and fade, blur or burn out while arriving ones come in, and a fade puts a coloured plate over the layer. `SceneView.PresentLayered` runs one engine per view and draws its result every frame until the transition lands. A video fading out keeps its player (`PlaybackId`), and players nothing draws any more are let go every frame (`ReleaseUnusedPlayers`).

### 30. Echo and ghost trails (Phase 4)

These two effects need what the item looked like a moment ago, which a WPF shader can't read. So:

1. Each frame, `Rendering/TrailStore.cs` takes a small picture of the item (at most 384 pixels across) with `RenderTargetBitmap`.
2. `Core/Animation/EffectTrail.cs` keeps the history and works out the *trail*: Echo fades the old picture (to a tenth after the effect's fade time), Ghost Trails also pushes it about with slow noise. It's plain arithmetic on pixels, so it has unit tests.
3. `Rendering/TrailEffect.cs`, a small shader, combines the live picture with the trail: the brighter of the two for Echo, a mix for Ghost Trails.
4. Without pixel shader 3.0 the trail is drawn under the item instead. It never stops the frame: a failure shows the item without its trail.

The projector, its preview and the program feed share one trail, so the work is done once a frame. As on the Mac, the trail follows the item: moving an item doesn't smear it, but anything changing inside it (a video, a colour, words swapping) leaves a trail. Trails only run on live views; thumbnails and the editor show the item as it is.

### Self-test

`Infrastructure/SelfTest.Render.cs` checks all four: two stand-in network screens show the same slide through a lower-third and a side-third look; a long lyric shows page 1 on the lower third and Next turns to page 2; a verse with its own Fade White passes through white on the program feed; and Echo and Ghost Trails show in the program feed's pictures (`20-*` to `23-*`).

### Lines to change in the existing sections

- **§7 Drawing:** "`Present(scene, transition)` crossfades from the old frame to the new one" → audience views now change layer by layer (§29); a whole-view cut, dissolve or fade-through is still used by the stage display.
- **§14 Animations:** add "page turns (§28) are morph steps, so they count as clicks".
- **§18 GPU shaders:** replace "Echo and ghost trails need the previous frame as an input, which WPF effects can't get, so they aren't drawn yet" with a pointer to §30.
- **§19 Screens:** add "a screen can show the live slide through a look (§27)".
- **Things to try:** "Give the stream a lower third: add a screen, set its Look to a starter theme and *Just "Lyrics (Lower Third)"*, open its Preview window, and fire a lyric. Then fire a long verse and press Next." (A folder scope picks each slide's design by name inside the folder, so a "Lyrics" slide needs a folder design called "Lyrics"; the starter folders call it "Lyrics (Lower Third)".)

---

## For PORTING.md

Replace the `ShowState.swift` row with:

| Swift | C# |
|---|---|
| `SlideScene/…/ShowState.swift` | `MxU.Slides.Core/Scene/ShowState.cs` (looks, held looks, hand-offs, look switches and dismissals, paging, page turns) |

Add:

| Swift | C# |
|---|---|
| `SlideScene/…/ThemeOverride.swift` | `MxU.Slides.Core/Animation/PlaceholderAnimations.cs` → `OverrideObjects` (font traits from the name; see below) |
| `PresenterCore/…/Library/PerSlideTheme.swift` (`rendered(throughOverride:)`) | `PlaceholderAnimations.RenderedThroughOverride` |
| `PresenterCore/…/ThemeSlideScope.swift` | `MxU.Slides.Core/Scene/ThemeSlideScope.cs` |
| `SlideScene/…/TransitionEdit.swift` | `MxU.Slides.Core/Animation/TransitionEdit.cs` |
| `Sources/RenderContext.swift` (`sceneTransition(from:)`, `roomDefault(for:)`, the variant engines), `Sources/ServiceControls.swift` (`nextLiveTransitions`, hand-offs, `switchPreset`, `apply()`'s variants and page turn) | `MxU.Slides.Core/Animation/LiveTransitions.cs`, `MxU.Slides.Windows/ViewModels/MainViewModel.Live.cs`, `Rendering/SceneView.cs` (`PresentLayered`) |
| `RenderEngine/…/TextRasterizer.swift` (`pages(for:sceneFrame:)`) | `MxU.Slides.Windows/Rendering/TextLayout.cs` → `Pages` |
| `RenderEngine/…/Compositor.swift` (effect history), `ShaderSource.swift` (`compositor_echo`, `compositor_ghost_trails`) | `MxU.Slides.Core/Animation/EffectTrail.cs`, `MxU.Slides.Windows/Rendering/TrailStore.cs`, `TrailEffect.cs` |
| `Sources/OutputPresetsController.swift` (slide-theme routing), `Sources/ScreenConfigurationView.swift` (`ThemeLookPicker`) | `MxU.Slides.Core/Outputs/OutputConfiguration.Looks.cs`, `OutputPresets.cs`; `MxU.Slides.Windows/Views/ScreensWindow` (Look) |

Tests ported: `PerOutputLookTests.swift`, `ThemeOverrideTests.swift`, the ShowState cases of `PlaceholderAnimationsTests.swift` and `PagingTests.swift`, `TransitionEditTests.swift` → `tests/MxU.Slides.Core.Tests/Scene/{ShowStateLookTests,ThemeOverrideTests,ThemeSlideScopeTests}.cs`, `Animation/{PagingTests,TransitionEditTests,LiveTransitionsTests,EffectTrailTests}.cs`, `Outputs/ScreenLookTests.cs`.

## For "Not ported yet" in PORTING.md

Remove the theme-override looks, text paging, per-item transitions and echo/ghost-trail lines, and add:

- **Looks, Windows differences:** a screen can have its own look (the Mac sets looks only in output presets). There is no output preset editor on Windows yet, so preset looks come from imported or Mac-made presets. The Mac's grid tile "N pages" strip and look thumbnails (`PosterLookImage`, `ThumbnailStore.posterLook`) aren't ported, and neither are `SlideSceneBuilder.lookKey/withoutLayerMedia/posterLookScene` (they serve those thumbnails). `PosterLookSweepTests.swift` checks Mac UI source and has no Windows twin.
- **Bold and italic in looks** are read from the font's name (`PlaceholderAnimations.FontTraits`), not from the font file as Core Text does.
- **Thumbnails** show page 1 only when given the pager: `SlideSceneBuilder.Scene(…, pager: TextLayout.Pager)` (see the integration notes).
- **Transitions:** Windows has one room-default setting (Settings › Transition) where the Mac has separate slide and media defaults; slides follow it, media dissolves 0.7 s unless it is Cut. Themes have no transition of their own in the shared schema, on either app.
- **Echo and ghost trails** use a small (≤ 384 px) software picture for the history; effects listed before them that need pixel shaders aren't in that picture (the live item still has them). Trails on adjustment layers ("effects apply below") aren't drawn, as no adjustment-layer effects are on Windows yet.
