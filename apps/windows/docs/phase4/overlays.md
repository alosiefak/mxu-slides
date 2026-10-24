# Phase 4: the overlay editor and the output mask editor

Notes for merging into `HOW-IT-WORKS.md` and `PORTING.md`.

## For HOW-IT-WORKS.md

### 27. Designing overlays (Phase 4)

Section 11 explains what an overlay is: a lower third, a scripture plate, a ticker, sitting on its own layer above the slides. Until now you could only put the starter ones up and type into their fields. The **overlay editor** lets you make your own and change any of them.

Open it with **Edit…** in the Overlays bin. It has three parts:

- **Left: the overlays, by pack.** A *pack* is just the first part of an overlay's folder: "Clean Geometric" in "Clean Geometric/Lower Thirds" (the rest, "Lower Thirds", is its *category*). So a pack exists while something is filed in it, and renaming a pack re-files every overlay in it. You can make, duplicate, rename and delete overlays and packs here.
- **Middle: the canvas.** It is the slide editor's canvas (`Views/SlideCanvasEditor.cs`, section 12), so moving, resizing, snapping, the arrow keys, Delete and Ctrl+D all work the same way. Below it, ▶ plays the overlay going up, a short hold, then coming down; the slider lets you stop it at any moment. **Go live** puts it on the program for real.
- **Right: three tabs.**
  - *Object*: the slide editor's inspector (font, colour, size, position), plus the label, what a text box shows (typed text, or the clock, the current slide…), and its **mask**.
  - *Animation*: add an effect to the selected objects, and see every step in the order it plays.
  - *Overlay*: its name, pack, category and layer, and its **fields**: the text boxes the operator types into in the bin.

**How editing works.** The editor changes the overlay itself: the library's own copy, the same one the bin and the program use. The Mac edits an overlay in its slide editor as a "one-slide composition"; Windows does the same trick. `OverlayEditorViewModel` makes a stand-in `Slide` (the *mirror*) whose object list *is* the overlay's object list, and gives that to the canvas. When the canvas moves an object, it is moving the overlay's object.

The canvas doesn't draw the mirror the way it draws a slide. `SlideCanvasEditor.SceneBuilder` lets the overlay editor supply the picture: `OverlayPreview.Still` (in `Core/Animation/OverlayPreview.cs`) draws the overlay exactly as the overlay layer does. It also adds pale blue "ghosts" of the *mattes*, because a matte is never drawn on an output and would otherwise be invisible while you design.

After every change, `MainViewModel.OverlayChanged` does three things:

1. It redraws the overlay's thumbnail in the bin.
2. If the overlay is up, it calls `ShowState.OverlayEdited`, which swaps the edited overlay in without replaying its entrance. A typo fixed while a lower third is up is fixed on the screen straight away.
3. It saves the file 600 ms after the changes stop, as the slide editor does.

**Masks inside an overlay.** An object can be *masked* by another (section 11): "Mask with" names the matte, and "Keep" chooses inside or cut out. `Core/Scene/ObjectMasks.cs` holds the rules from the Mac's inspector. For example, it won't offer a matte whose own mask leads back to this object, because that would be a loop.

**Animations.** Each object carries its own steps, and the overlay's `AnimationOrder` says what order they play in (section 14). `Core/Animation/AnimationEditing.cs` is the non-UI half of the Mac's animation editor: add a step, swap its effect, re-time, reorder or remove it, and the words for each ("Name · Slides in from bottom", "After · 0.5s").

The list shows the steps grouped by when they run (`AnimationSequence.Groups`):

- **When it goes up**: everything that plays as soon as it is fired.
- **When it's taken down**: the exit, which plays when you take it down or press F4.

An overlay never gets a click. So an In step left waiting for a click would never play, and the editor flags it. That is also why the editor's **Bring in** effects go into "when it goes up" (the Mac starts them on a click).

**The preview** is built the same way as the program. `OverlayPreview.PlayThrough` makes the overlay's items with `AnimationSequence.PreviewContext` as their clock, just as `ShowState` makes a live overlay's items with the time it was fired. So what you preview is what the audience will see. Dragging the slider freezes that clock at one moment.

**Starter overlays** are the ones the app ships (their ids start with `pack.`). The app puts them back when they're missing and refreshes them when its starter packs change (`LibrarySeeder.RefreshStarterPacks`). So the editor says so, and offers **Make my copy**.

### 28. Output masks (Phase 4)

An **output mask** blacks out part of a screen: the pillar in front of the projection, the stage wings, or everything except a window in an LED wall. Section 19's screens already applied masks imported from ProPresenter. The **mask editor** (`Views/MaskEditorWindow.cs`) is where you draw your own. The Screens window opens it for a screen.

- A mask is a shape stored as SVG path data in the screen's *unit square*: 0 to 1 across and down, whatever the screen's size. So "M 0.25 0 L 0.75 0 L 0.75 1 L 0.25 1 Z" is the middle half.
- **Draw a mask**: click to place points. Drag while placing one to pull out a curve. Click the first point again, double-click, or press Enter to close the shape; Esc gives up.
- **Edit points**: drag a square point to move it, or a round handle to bend the curve. Right-click a point to remove it.
- **+ Rectangle** drops in a rectangle to drag into place.
- Each mask can **black out what's inside** or **show only what's inside**. **Feather** softens its edge. **Always on** means it's part of the room; turn it off and an output preset switches it on when needed. On a screen with several outputs (edge blend, mirrors), **On** picks which output it darkens.

The pen's rules are in Core, so they are tested without a window. `Core/Outputs/OutputMaskEditing.cs` holds `OutputMaskPen` (what each click, drag and key does) and `OutputMaskLibrary` (adding, changing and removing masks). `Core/Scene/PathAnchors.cs` holds `PathAnchorCodec`, which reads and writes the path data as a list of points with Bézier handles.

Every change goes to `OutputConfiguration.SetMasks`, which saves the setup. The mask raster is rebuilt and the outputs' correction shader multiplies the picture by it (section 18), so the real screens change as you edit.

The editor draws over the screen's live picture (`OutputManager.Picture`), or over the alignment grid. It darkens the parts the mask will black out, using the same coverage raster the outputs use.

### Things to try

- **Make a lower third from nothing.** Open Overlays › Edit…, press + New, add a Box and a Text. Select the box, pick *Slide* under Bring in; select the text, pick *Fade*, then set its Plays to *After previous*. Press ▶. Then Go live, and type a new name in the bin while it's up.
- **Hide a pillar.** In Screens, open a screen's masks, press + Rectangle, and drag its corners over where the pillar is. Turn up Feather so the edge isn't harsh.
- **Read the pen.** Open `OutputMaskPen.Press` and follow what a click does while drawing, then while editing. The tests in `OutputMaskEditingTests.cs` do exactly what a user does with the mouse.

## For PORTING.md

### Ported line by line (new rows)

| Swift | C# |
|---|---|
| `SlideScene/…/PathAnchors.swift` | `MxU.Slides.Core/Scene/PathAnchors.cs` (`PathAnchor`, `PathAnchorCodec.Parse/Encode/EncodeAbsolute`) |
| `SlideScene/…/ShapeOutlineSVG.swift` | `MxU.Slides.Core/Scene/ShapeOutlineSvg.cs` (`ShapeOutlineSvg.PathData`) |
| `PresenterCore/…/Library/TeamDrives.swift` (`isUnder`, `childCounts`, `rebased`, `renamed`) | `MxU.Slides.Core/Library/FolderPaths.cs` |
| `Sources/AppModel.swift` (overlays: `createEntity`, `duplicate`, `rename`, `renameFolder`, `moveToFolder`) | `MxU.Slides.Core/Library/OverlayLibrary.cs` |
| `Sources/SlideEditorModel+Animations.swift` (step editing, sentences, timing labels, speeds), the names in `AnimationInspector.swift` | `MxU.Slides.Core/Animation/AnimationEditing.cs` |
| `Sources/SlideObjectInspector.swift` (mask section: `maskCandidates`, `isMatte`, `maskObjectBinding`) | `MxU.Slides.Core/Scene/ObjectMasks.cs` |
| `Sources/MaskEditor.swift` (`MaskPenNSView` and the library edits in `OutputMaskEditor`) | `MxU.Slides.Core/Outputs/OutputMaskEditing.cs` (`OutputMaskPen`, `OutputMaskLibrary`) |

Their tests: `PathAnchorTests.swift` → `Scene/PathAnchorTests.cs`; the `ShapeOutlineSVG` cases of `SlideSceneBuilderTests.swift` → `Scene/ShapeOutlineSvgTests.cs`; the folder cases of `TeamDrivesTests.swift` → `Library/OverlayLibraryTests.cs` (`FolderPathsTests`); the scene half of `ObjectMaskTests.swift` → `Scene/ObjectMasksTests.cs`, its renderer half → the self-test (`SelfTest.Overlays.cs`, `ObjectMaskRenderCheck`). `OutputMaskTests.swift` and `OutputMaskRoutingTests.swift` were already ported in Phase 3. The Mac has no tests for the app-target code (`MaskEditor.swift`, the animation editor), so `OutputMaskEditingTests`, `AnimationEditingTests`, `OverlayLibraryTests` and `OverlayPreviewTests` are new.

### Re-implemented for Windows (new rows)

| Mac | Windows |
|---|---|
| `Sources/SlideEditorView.swift` / `SlideEditorModel.swift` with an overlay host, `Modules/OverlaysModule.swift` (Layer menu, Edit in Library) | `MxU.Slides.Windows/Views/OverlayEditorWindow.*`, `ViewModels/OverlayEditorViewModel*.cs`; `MxU.Slides.Core/Animation/OverlayPreview.cs` (still and animated pictures) |
| `Sources/AnimationInspector.swift` (gallery, sequence list, step strip and editor) | `ViewModels/OverlayEditorViewModel.Animation.cs`, `ViewModels/AnimationStepViewModel.cs` |
| `Sources/SlideObjectInspector.swift` (as a reusable panel) | `Views/ObjectInspectorPanel.xaml` over `ViewModels/ObjectInspectorViewModel.cs` |
| `Sources/MaskEditor.swift` (SwiftUI and AppKit) | `Views/MaskEditorWindow.cs` |

### Not ported yet (replace the two Phase 3 lines)

Remove "Output mask editor" and "Overlay editor" from the list. Add:

- **Overlay editor extras:**
  - *Morph ("Change") steps* can't be made yet, because their end state is posed on the Mac's canvas (`AnimateCanvasOverlay.swift`). Existing ones play, re-time and delete. *Where it enters from* (a custom start pose) is the same.
  - The *animation timeline* (`AnimationTimelinePanel.swift`), *animation presets*, and *one step per line/word* for text aren't there. Steps reorder with Earlier/Later and Move to, not drag and drop.
  - *Camera push* can be turned on and its mode chosen; its alignment, margin, zoom and blur settings aren't shown.
  - *Undo* is one step back: "Undo my changes to this overlay" restores it as it was when the editor first showed it.
  - The *Overlays module's* pinned/recent views, grid/list switch and search.
- **Mask editor:** the Mac shows it inside the screen's settings. Here it is its own window, opened from the Screens window.

### Deliberate differences

- **Bring in effects go into "when it goes up".** On the Mac, a new In step starts on a click, which an overlay never gets, so the object would never appear. The editor also flags any In left waiting for a click.
- **Duplicated objects get new step ids.** Their steps play right after the originals'. (The slide editor's Duplicate copies the step ids too, which makes two objects share one step id.)
- **The mask editor adds a few things the Mac doesn't have:** + Rectangle, removing a point (right-click), choosing which output a mask darkens, a darkened preview of the result, the alignment grid as a backdrop, and clicking a mask to pick it.

### For the renderer

The self-test's `ObjectMaskRenderCheck` found one difference from the Mac's `ObjectMaskTests`. An item masked "in" by a matte that isn't drawn is shown *unclipped* by `SceneView`; the Mac shows nothing. Scenes built from documents drop masks that name a missing object (`MaskWiring`), so this only shows when the matte object is *hidden*. The self-test reports it as a NOTE, not a FAIL.
