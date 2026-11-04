# Phase 7: the editors

Until now the Windows app could *show* everything the Mac makes, but several things could only be
*made* on a Mac: themes, the look of a picture or video, stage layouts, a slide's effects. This
part of Phase 7 adds the editors for them:

1. the **theme editor** (designs, their placeholders and background, their animation, with a live
   sample), and **Apply Theme** for a deck or for some slides;
2. the **media editor** (name, trim with a scrubbable preview, speed, loop, volume, fit, zoom, crop,
   effects, Auto Advance and transition);
3. **Auto Advance** sheets for slides (one, its group, or every slide), a deck's Slide Show, and media;
4. **effects** on slide objects, theme and overlay objects, and media;
5. the **stage layout editor** for confidence monitors;
6. the overlay editor's extras: **Change** (morph) steps posed on the canvas, a custom start for an
   In, camera push settings, an **animation timeline**, and drag-and-drop to reorder steps.

The same rule as Phase 6 shaped every decision, so it comes first.

## The rule: a Mac must still open everything

The Mac's decoder rejects a whole document if it meets an enum value or field it doesn't know. So
nothing here adds one. When Windows wants something the shared format can't say, it keeps it in a
**Windows extra** (`LibraryStore.Extras`, `Core/Library/WindowsExtras.cs`) beside the document,
saved against a fingerprint of the shared part it decorates. If a Mac later changes that part, the
fingerprint no longer matches and Windows drops its extra: the Mac's edit wins. Two extras are used
here: the **media look** (below) and the **kinetic build** on a theme's placeholder (from Phase 6).

## How an editor works (all of them)

Every editor here follows one pattern, so learn it once:

- **It edits the library's own copy.** The editor doesn't make a private copy to "Save" later. It
  changes the document the library holds, redraws at once (the canvas, the picture in the list, the
  outputs if it's live), and saves a moment after the changes stop (a `DispatcherTimer` of about half
  a second: `Flush()` in each view model saves straight away). Closing the window flushes.
- **Undo has one hook.** Each editor's `Flush()` carries a comment: integration wraps it in the
  editor's own undo scope (the undo work is on another branch), so every save lands there.
- **The canvas is the slide editor's.** `SlideCanvasEditor` edits a `Slide`. A theme design *is* a
  slide, an overlay and a stage layout are not, so their editors hand it a **mirror**: a slide whose
  `Objects` list is the composition's own list. Dragging a box in the canvas moves the real object.
- **Logic lives in Core, windows only ask questions.** The rules (what a new theme contains, how a
  trim handle snaps, which slides "its group" reaches) are plain functions in `MxU.Slides.Core` with
  unit tests. The view models call them; the windows only draw, and ask "are you sure?".

## 1. The theme editor

*Library › right-click a presentation › Edit Theme…* (or *Theme…* to pick one). Files:
`ViewModels/ThemeEditorViewModel.cs`, `Views/ThemeEditorWindow.xaml(.cs)`.

Left: the themes, then the selected theme's **designs** (the Mac calls them categories: Lyrics,
Bible, Announcements…), each with a picture. Middle: the design on the canvas, with the animation
timeline above it. Right: four tabs.

- **Object**: the selected object's inspector (font, size, colour, outline, effects, mask…), the
  same panel the slide editor uses.
- **Animation**: the shared Animation tab (below).
- **Design**: its name, its group ("Lower Thirds"), its own background colour, its placeholders, and
  a **sample slide**: type some words and see a slide using this design, its builds playing.
- **Theme**: the theme-wide font, size, text and background colours.

Making and changing themes is `Core/Library/ThemeLibrary.cs`: *New* makes the Mac's starter theme
(four designs each with a "Text Placeholder"); *Duplicate* gives every design and object a **fresh
id** (so a Windows extra on one copy can't leak to the other) and keeps masks pointing at the right
copies; design names are kept unique because slides find their design *by name*; renaming a design
can rename it in the slides that use it (the editor asks).

**Kinetic text in a theme.** A worship look's words rise in one by one. On a slide that's ranged
steps (one step per word), but a theme's placeholder only holds sample words, not each slide's
words. So `Core/Library/ThemeKinetics.cs` writes *whole-text* steps into the shared theme (a Mac
fades the words in together) plus a **kinetic build** extra that tells Windows to split each slide's
words. The editor remembers the builds it touched and saves them again against the new steps when
you re-time them. Phase 6's `KineticExtras.Seed` was also fixed so the starter seeding never
overwrites a build you saved.

**Apply Theme** (`ViewModels/MainViewModel.Themes.cs`, `Core/Library/ThemeApplication.cs`,
`Core/Library/DesignActions.cs`, `Views/ThemePickerWindow.cs`). The deck's theme picker and a slide's
*Apply Theme…* use the Mac's rules: a slide keeps its design (Lyrics stays Lyrics) and swaps the old
design's slide actions for the new one's. Before a deck's new theme reaches slides that chose a
theme of their own, the app asks: **Yes** restyles them too (the Mac's way), **No** keeps theirs,
**Cancel** changes nothing. A slide can also be set to **Blank** (no theme) or **Automatic**.

## 2. The media editor

*Media bin › right-click › Edit…*. Files: `ViewModels/MediaEditorViewModel.cs`,
`Views/MediaEditorWindow.cs`, `Views/TrimScrubBar.cs`, `Core/Media/MediaTrimEdit.cs`.

The preview plays through its **own** `MediaHub` (scope `media-editor`), so scrubbing it never
touches what's on the outputs, and it plays the whole file so you can see what you're trimming.
Sound is off unless you tick *Listen*.

- **Trim**: drag the two handles on the scrub bar, or type the in and out times. The rules (a handle
  dragged near an end clears that point, an in after the out is refused…) are a line-by-line port of
  the Mac's `MediaTrimEdit.swift`. In and out points, speed and loop are shared fields: a Mac uses them.
- **Speed**: 0.25× to 4×; "Plays 0:25" tells you how long the trimmed part lasts at that speed.
- **Volume, fit, zoom, crop**: these have **no shared field**, so they are the Windows extra
  `MediaLookExtras` (`Core/Library/MediaLookExtras.cs`), saved against the file's hash. A Mac shows
  the item as it always did (filling the screen, full volume). Volume reaches the player through
  `MediaHub.GainOf` (`Rendering/MediaHub.Gain.cs`, `VideoSoundOptions.Gain`).
- **Effects** use the shared `MediaItem.Effects`, edited with the same Effects panel as everywhere.

**How the look gets on screen.** `Core/Media/MediaDressing.cs` takes the scene the outputs are about
to draw and "dresses" each full-screen media item: its effects (as the Mac's
`applyingMediaEffects` does), then its look (fit mode, a crop as a *source rectangle* and a zoomed
frame). `SceneView` learned to draw a cropped source (`PlaceCropped`), which also fixes crops that
came in from imports.

## 3. Auto Advance

`Views/AutoAdvanceWindow.cs`, `Core/Automation/AutoAdvanceEditing.cs` (port of the Mac's
`AutoAdvanceControls.swift`). One sheet, three uses:

- **a slide** (*slide grid › right-click › Auto Advance…*): on (7 seconds, as on a Mac), count from
  the slide firing or from its video ending (then the delay may be negative, down to −30), loop to
  the first slide. The Mac edits several selected slides at once; the Windows grid selects one at a
  time, so the sheet has **Apply to: this slide / its group / every slide**;
- **a deck's Slide Show** (*Library › right-click › Slide Show…*): every slide moves on by itself;
- **a media item** (*bin › right-click › Auto Advance…*).

Every change is saved at once, as on the Mac.

## 4. Effects

`Views/EffectsPanel.cs`, `ViewModels/EffectsEditorViewModel.cs`, `Core/Animation/EffectEditing.cs`.
A card per effect, with its sliders, an on/off switch, up/down, remove, and the presets
(`EffectPresetCatalog` plus your own). It appears in the slide inspector, the overlay, theme and
stage editors' inspector, and the media editor. Only the shared `EffectKind`s are offered.

Most effects are drawn by **pixel shaders**. A PC without pixel shader 3.0 (or drawing in software,
as GitHub's test machine does) can't draw them, so the panel says so in words
(`EffectEditing.SoftwareNote`) rather than letting the effect silently vanish. Blur needs no shader.
*Apply to layers below* (the Mac's adjustment layer) is kept in the document but not drawn on
Windows yet; its switch says so.

## 5. The stage layout editor

*Stage panel › the pencil beside a screen's layout* (the Mac's "Edit Layout in Library"). Files:
`ViewModels/StageLayoutEditorViewModel.cs`, `ViewModels/StageLinkViewModel.cs`,
`Views/StageLayoutEditorWindow.xaml(.cs)`, `Core/Stage/StageLayoutEditing.cs`.

- **Layouts**: new (blank or from a starter: Classic, Current + Next…), duplicate, rename, delete
  (screens showing it go back to the built-in layout).
- **Boxes** (*Add › Box*): current slide, next slide, chords, notes, clock, a timer, a
  service-tracking timer, video countdown, stage message, slide position, group name, current and
  next item, timecode, a label. Each arrives as a card (background, caption, linked text) in the first
  free spot.
- **What a box shows** is the SHOWS panel: its source, and that source's settings (which timer, how
  it reads, amber then red as it runs out; clock format; chords; lines; which video).
- **Snapping and safe margins**: boxes snap to the canvas, the middle, other boxes, and a **safe
  margin** (none, 5 %, 10 %) drawn as a dashed line; a note tells you when a box reaches past it,
  since some monitors cut the edges off. The canvas can be 16:9, 4:3 or portrait.
- **Sample words or what's live**: the canvas reads sample text (so every box shows something), or
  the real stage data, ticking each second.

**Notes without a new kind.** The shared format has no "slide notes" source. A notes box is the
existing *current slide* link with `SourceObjectName = "Notes"`. On Windows,
`ConfidenceSceneBuilder.SlideText` offers the slide's notes under that name (an object really called
Notes wins); a Mac shows an object called Notes, or nothing.

## 6. The overlay editor's extras (and the shared Animation tab)

The Animation tab is now one control, `Views/AnimationTab.xaml(.cs)` with
`ViewModels/AnimationTabViewModel.cs`, used by the overlay editor and the theme editor. Its host (the
editor) implements `IAnimationHost`: what's selected, how to redraw, how to pose.

- **Change (morph)**: pick *Change* in the gallery and the canvas switches to **posing**: you move,
  resize or restyle the object, and that becomes where the step *ends*; the object itself doesn't
  move (a faint copy shows where it rests). *Done posing* goes back. The canvas is simply given the
  objects with the pose in the object's place (`AnimationTimelineEditing.PosedObjects`).
- **Custom start**: an In can enter from where you pose it, instead of from an edge.
- **Camera push**: the program video moves into the space the object leaves: where it sits, margin,
  zoom, a blurred backdrop and how blurred.
- **Timeline** (`Views/AnimationTimelineView.cs`, port of `AnimationTimelinePanel.swift`): a row per
  object, a block per step, a column per click. Drag a block to start it later, drag its right edge to
  stretch it, its left edge to trim, a column to reorder; scrub to preview. Times snap to 1/20 s.
- **Drag and drop**: drag a step in the list onto another to move it there.

## Try it

1. Library › right-click *Amazing Grace* › **Edit Theme…**. Choose *Lyrics*, click the placeholder,
   make it bigger and yellow; on the Animation tab pick a kinetic preset and **Apply**; press ▶. Put
   a verse live: the words rise in.
2. Right-click a slide › **Apply Theme…**, pick another theme. Then change the deck's theme and
   answer the question.
3. Media bin › right-click a video › **Edit…**: drag the trim handles, choose 1.5×, crop the top.
4. Right-click a slide › **Auto Advance…**: on, *Apply to: its group*.
5. Stage panel › the pencil › **+ New › Blank**, add Current slide, Next slide, Clock and Notes,
   choose a 10 % safe margin, then *What's live now*.
6. Overlays › Edit: select a box, Animation tab › **Change**, drag it somewhere: that's where it
   ends. Look at the timeline above the canvas and drag a block.

## For PORTING.md

**Ported line by line** (Swift → C#):

- `SlideScene/MediaTrimEdit.swift` → `Core/Media/MediaTrimEdit.cs`. Tests: `MediaTrimEditTests.cs` (`WindowIsNilUntrimmed`, `WindowAndFractionsFollowThePoints`, `PointsFromABarDragTidyAndClearAtTheEnds`, `TypedInClampsClearsAndSwallowsALaterOut`, `TypedOutClampsClearsAndRefusesBeforeTheIn`, `TimecodesParseAndFormat`, `PlayRatesAreStoredAbsentAtNormalSpeed`, `ATrimmedItemPlaysForItsWindowAtItsRate`).
- `PresenterCore/Library/DesignActions.swift` → `Core/Library/DesignActions.cs`. Tests: `DesignActionsTests.cs` (`AThemeResolvesADesignByNameElseItsFirst`, `TakingADesignCopiesItsActionsWithFreshIds`, `SwitchingDesignsSwapsTheOldDesignsActions`, `ApplyingADeckThemeSwapsEachSlidesDesignActions`, `ApplyingAThemeToSomeSlidesStylesOnlyThoseSlides`, `PickingADesignPutsEverySlideOnIt`).
- `PresenterCore/Library/ConfidenceLayoutEditing.swift` (`editorMirror`, `adopt(editorMirror:)`, `isTallCanvas`) → `Core/Stage/StageLayoutEditing.cs`. Tests: the first three in `StageLayoutEditingTests.cs`.
- `Sources/AutoAdvanceControls.swift` → `Core/Automation/AutoAdvanceEditing.cs`, `Views/AutoAdvanceWindow.cs`. Tests: `AutoAdvanceEditingTests.cs`.
- `SlideEditorModel+Animations.swift` timeline and morph functions (`placeTimelineStep`, `resizeTimelineStep`, `moveTimelineSteps`, `trimTimelineStepStart`, `moveTimelineStep`, `toggleStepLink`, `duplicateAnimationStep`, `deleteTimelineColumn`, `moveTimelineColumn`, `morphBaseline`, `addMorph`, `setInCustomStart`) → `Core/Animation/AnimationTimelineEditing.cs`. Tests: `AnimationTimelineEditingTests.cs` (the Mac has none for these).
- `AnimationTimelinePanel.swift` → `Views/AnimationTimelineView.cs`.
- `MediaEditorView.swift` (the inspector, `EffectKind.addable`, the effects section) → `ViewModels/MediaEditorViewModel.cs`, `Views/MediaEditorWindow.cs`, `Core/Animation/EffectEditing.cs`; `SlideObjectInspector.swift`'s effects section and `EffectPresetsMenu` → `Views/EffectsPanel.cs`, `ViewModels/EffectsEditorViewModel.cs`. Tests: `EffectEditingTests.cs`.
- `RenderScene.applyingMediaEffects` → the effects half of `Core/Media/MediaDressing.cs`. Tests: `MediaDressingTests.cs`.
- The theme host of the slide editor, `ThemeSlides.defaultSlides`, `SlideBulkEdit.freshIDCopy`/`uniqueThemeSlideName`, AppModel's create/duplicate → `Core/Library/ThemeLibrary.cs`, `Core/Library/ThemeApplication.cs`, `ViewModels/ThemeEditorViewModel.cs`, `Views/ThemeEditorWindow.xaml`. Tests: `ThemeLibraryTests.cs`. `NewSlide.preview` → `Core/Animation/DesignPreview.cs`.
- `ConfidenceModule.swift`'s "Edit Layout in Library" → the pencil in `Views/Operator/StagePanel.cs`.

**Windows-only:**

- `Core/Library/MediaLookExtras.cs` (fit, zoom, crop, volume: a Windows extra on the file hash) and the look half of `Core/Media/MediaDressing.cs`; `SceneView.PlaceCropped`; `Rendering/MediaHub.Gain.cs`, `VideoSoundOptions.Gain`. Tests: `MediaDressingTests.cs`, `MediaCropTests.cs`.
- `Core/Library/ThemeKinetics.cs` (kinetic presets and one step per line/word on a theme placeholder become whole-text steps plus a kinetic build); `KineticExtras.Stored/Remove/Copied` and `Seed` no longer overwriting a build saved against other steps.
- `ThemeLibrary`: fresh object ids in a copied theme, unique design names, renaming a design in the slides that use it. `ThemeApplication.OwnThemes.Keep` (the question's "No").
- Auto Advance's "Apply to: its group / every slide" (the Windows grid has no multi-select yet).
- The stage layout editor's box cards (`StageLayoutEditing.MakeBox`, `FreeSpot`), safe margins, sample/live switch, canvas sizes; slide notes on stage as the current slide's "Notes" (`ConfidenceSceneBuilder.SlideText`, `LinkedText.PreviewInfo`).
- `Views/EditorMenus.cs` (the Edit…, Auto Advance…, Apply Theme…, Slide Show…, Theme… and Edit Theme… menu items); `SlideCanvasEditor.SnapTargets` and `Decorate`; `ObjectInspectorViewModel.ShowsTextSource`.
- The shared `Views/AnimationTab.xaml` / `AnimationTabViewModel` / `IAnimationHost` (the overlay and theme editors share one tab); `CompositionObjects.cs`.
- `Infrastructure/SelfTest.Editors7.cs`.

**Not ported yet:**

- The Mac's "No Theme" baking (`bakedSlide`): Windows' *Blank* sets `Unthemed` without copying the theme's look into the slide.
- Editing multi-view tile layouts (`MultiViewTiles`): they open read-only in the stage layout editor.
- *Apply to layers below* (`effectsApplyBelow`, adjustment layers) is kept but not drawn on Windows.
- A new trim or speed applies the next time a playing video is fired, not to the copy already playing.
- Bulk Auto Advance and Apply Theme from a multi-slide selection (wired through `AutoAdvanceWindow.ForSlides` and `ThemePickerWindow.ForSlides`, which take a list, once the slide grid can select several).
- In the overlay editor's line, remove "the *animation timeline* and drag-and-drop reordering are still not there".
