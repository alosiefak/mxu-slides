# Phase 5: rendering correctness

These were places where Windows drew a Mac or ProPresenter document differently from the Mac.
Each one is now drawn the Mac's way. This note explains, for someone learning the code, what
changed, which files to open, and what was learnt on the way.

The self-test checks all of it (`Infrastructure/SelfTest.RenderFixes.cs`, pictures `50-…` to `56-…`).

## 1. Each slide's own theme, and Blank slides

**What was wrong.** A presentation names one theme, but on the Mac a single slide can follow a
different theme (a scripture slide in a lyric deck) or none at all (a *Blank* slide). Windows
always used the presentation's theme, so mixed decks came out in one look.

**How it works now.**

- `Core/Library/PerSlideTheme.cs` is a port of `PerSlideTheme.swift`. The rule is three lines in
  `ThemeIdFor(presentation, slide)`:
  1. a Blank slide (`unthemed: true`) has no theme, whatever else it says;
  2. else the slide's own `themeId`, if it has one (an empty one counts as none);
  3. else the presentation's `themeId`.
- `MainViewModel.ThemeFor(slide, presentation)` (in `ViewModels/MainViewModel.Rendering.cs`)
  looks that id up in the library. **Every place that draws a slide now asks it**:
  - going live (`Fire` in `MainViewModel.Live.cs`), so the outputs, the stage display and the
    program feed get the right theme;
  - the slide grid (`RebuildSlides` in `MainViewModel.cs`, and the tile refresh while you type in
    `MainViewModel.Editing.cs`);
  - the editor's canvas, its inspector and the Design picker (`EditedSlideTheme`);
  - the lyrics editor's preview.
- A Blank slide's theme is `null`, so `SlideSceneBuilder` draws its objects with the built-in
  defaults on black, exactly as the Mac does with no theme.

The Local API on Windows has no slide-picture endpoints, so there was nothing to change there; its
looks are output presets, which already go through `ShowState`.

**Tests:** `tests/…/Library/PerSlideThemeTests.cs` (the Mac's three tests, plus a mixed deck built
into scenes).

## 2. A video's trim and play rate

**What was wrong.** A media item can say "start at 0:05, stop at 0:42, play at half speed"
(`inPoint`, `outPoint`, `playRate`). ProPresenter imports set them. Windows played every video
whole at normal speed.

**How it works now.**

- `Core/Media/PlaybackOptions.cs` is a port of the Mac's `PlaybackOptions`: `Resolved(duration)`
  clamps the points into the file and the speed into 0.25–4, and plays the whole file when the
  window is shorter than a frame or the points are the wrong way round.
- `Rendering/MediaHub.Trim.cs` is the WPF half, a separate file so the video code in
  `MediaHub.cs` stays readable. It:
  - jumps to the in point and sets `MediaPlayer.SpeedRatio` when the player opens
    (`StartPass`);
  - ends a pass at the out point. WPF's `MediaPlayer` has no "stop here", so while a trimmed video
    plays its position is checked on every screen refresh (`CompositionTarget.Rendering`, on the UI
    thread, where a `MediaPlayer` must be used; never the shared thread pool);
  - at the end of a pass (`PassEnded`), a looping video goes back to its **in point**, so it loops
    inside the trim; any other video pauses there;
  - raises `VideoEnded` **once per pass**, for looping and non-looping videos alike;
  - makes `Transport` and `Seek` work in the trimmed window, as the Mac reports it (position from
    the in point, length of the trim), so the stage display's video countdown counts down the trim.
- `MediaHub.cs` itself only gained three hooks (`BeginPass`, `StartPass`, `PassEnded`) and the
  window maths in `Transport`, `Seek` and `Restart`. The app hands it each video's options with
  `Media.TrimOf = TrimFor` (one line in `MainViewModel`'s constructor).

**Tests:** `tests/…/Media/PlaybackOptionsTests.cs` (the Mac's, plus the "pass is over" rule). The
self-test trims its test video to 1.2 s at double speed and checks the reported window, a single
end, and that a looping copy loops inside the trim.

## 3. Fonts saved in the library

**What was wrong.** When you import a PowerPoint deck, its embedded fonts are saved into the
library's `fonts` folder (`PptxImport/FontActivator.cs`, as on the Mac). But drawing only knew the
fonts installed in Windows, so those fonts were never used.

**How it works now.**

- `Core/Library/FontFiles.cs` reads what's in a font file without Windows: each face's family,
  style and **PostScript name** ("Gotham-Bold", which is what slides store) from its `name` table,
  and its weight, width and slant from its `OS/2` table. That is what Core Text resolves a font name
  by on the Mac (`FontSharing.faces(inFileAt:)`).
- `Rendering/FontMap.ActivateStored(libraryRoot)` is the Mac's `FontActivator.activateStored`. For
  each file in `fonts` it asks WPF for the file's own font families (`Fonts.GetFontFamilies(file)`),
  which loads the font **privately, for this app only: nothing is installed into Windows**. Then:
  - a PostScript name maps to exactly that face (family + weight + style + stretch);
  - a family name maps to a `FontFamily` over the whole folder, so "Gotham" finds every Gotham face;
  - the font pickers list the library's families beside the installed ones.
- It runs at launch (`App.xaml.cs`) and after a PowerPoint import that stored fonts
  (`MainViewModel.ActivateLibraryFonts`, which also redraws the grid and the outputs).
- Text measurements were cached by font name, so loading new fonts clears those caches
  (`TextLayout.ForgetMeasurements`, `PathTextRenderer.ForgetLayouts`): text that had a stand-in font
  is measured again in its real one.
- The PowerPoint importer no longer reports a font as "not installed" when an earlier import already
  saved it into the library.

**Tests:** `tests/…/Library/FontFilesTests.cs` (a hand-built font, and Windows' own Arial), and
`tests/MxU.Slides.PptxImport.Tests/FontActivatorTests.cs` (the Mac's FontActivator tests). The
self-test copies Georgia, renames the copy "MxUText" so Windows doesn't know it, puts it in the
library and checks text in "MxUText" draws exactly like Georgia (`54-library-font*.png`).

## 4. Tickers, and text on a path or a shape's edge

**What was wrong.** On the Mac, text can run along a path (an arc, a ring, a line, a shape's
outline) and can flow along it: that's how tickers work. Windows dropped those fields, so ticker
designs made on the Mac showed as still, wrapped text.

**How it works now.** Follow one ticker from document to screen:

1. **The scene.** `StyledText` (`Core/Scene/SceneModel.cs`) now carries the Mac's fields: `PathData`,
   `TickerSpeed`, `PathReversed`, `PathOffset`, `TickerRepeat`, `TickerLeftToRight`,
   `TickerStream`, `TickerGap`, `TickerSeparator`, `WordSpacing`, `TickerRamp` (and `Scroll`, see
   section 7). `SlideSceneBuilder.StyledTextFor` fills them from the object's style, falling back to
   its placeholder's, as on the Mac. For a shape whose text sits **on its edge**, `RenderItems`
   turns the shape's outline into the path (`ShapeOutlineSvg`), reversed when the text is inside the
   edge so it reads upright.
2. **The maths** (`Core/Scene/PathTextLayout.cs`, a port of `PathTextLayout.swift`):
   - `SvgPath.Parse` reads the path data (a port of the Mac's `SVGPathParser`);
   - `Flatten` cuts it into short straight pieces, with the distance along it at each point and a
     smoothed direction, so letters turn smoothly round a curve;
   - the text is laid out once as a single-line **strip**; `MakeRun` cuts the strip into one tile
     per character cluster;
   - `TickerPhase` says how far the ticker has travelled: speed × time since it went up, negative
     for right-to-left; a repeat count stops it after that many passes, and a ramp eases each pass;
   - `Place` puts every tile on the path at that phase: a closed path (a ring) wraps round; an open
     path scrolls the text in at one end and out at the other, trimming the letter that's half off
     the end; a *stream* is a train of copies with a gap and a separator between them.
3. **The drawing** (`Rendering/PathTextRenderer.cs`). The Mac draws tiles of one rasterised strip
   on the GPU. WPF draws each cluster's own little vector drawing, moved to its place and turned to
   the path's direction there, clipped where a tile was trimmed. Strips and flattened paths are
   cached, so a moving ticker only re-places tiles each frame.
4. **Frames.** `SceneView` (via `PathTextPose`) works out the phase for the frame. A moving ticker's
   phase is part of the item's content key, so its drawing is rebuilt every frame while it's up;
   `AnimationTimeline.IsAnimating` keeps the frames coming. Live views move tickers; **thumbnails
   and the editor show them at rest** (aligned along the path), unless the scene is frozen at an
   instant (scrubbing, the self-test's pictures).

Two Mac rules worth knowing: text on a **closed** path always starts where the path starts (the top
of a ring) whatever its alignment, and a path's text never block-scrolls or pages.

**Tests:** `tests/…/Scene/PathTextTests.cs`: the Mac's PathTextTests (flattening, seams, culling,
marquee loops, alignment, trims, tangents, streams, offsets, separators), the ticker phase
(repeat limit, direction, ramp), and the scene builder carrying the fields. Pictures:
`52-tickers-1s/2s.png` (frozen a second apart), `52-tickers-program.png` (the real program feed).

## 5. Live inputs and screen mirrors inside slide objects

**What was wrong.** A slide object can show a camera (a picture-in-picture of the preacher) or a
mirror of one of the app's own screens. `SlideSceneBuilder` returned "no fill" for these.

**How it works now.**

- `SlideSceneBuilder.Fill` turns a media fill into the Mac's engine ids: `screen::<screen id>` for a
  mirror, `input::item::<id>` for a named input, `input::camera::<device>` / `input::ndi::<name>` for
  an older direct device reference. A mirror wins over an input, which wins over a library file. The
  Mac's helpers (`FillMediaId`, `FillLiveInputId`, `FillScreenMirrorId`, `LiveInputEngineId`,
  `MediaWants`) are ported with their tests.
- `SceneView.DrawLivePicture` draws the input's newest frame inside the shape. Live views (outputs,
  the operator's preview, the program feed) show the picture once it arrives (nothing until then, as
  on the Mac). **Thumbnails and the editor show the Mac's placeholder**
  (`Rendering/LiveInputPlaceholder.cs`: stripes, a border, and a camera, NDI or screen glyph),
  because a slide grid full of live cameras would be both busy and expensive.
- **Screen mirrors** (`Outputs/ScreenMirrors.cs`) are the Mac's `ScreenMirrorService`: a hidden copy
  of that screen (a `ScreenFeeds` feed, the same thing recordings of one screen use) whose newest
  picture is drawn like a camera. A mirror starts the first time something draws it and stops ten
  seconds after nothing does. Like the Mac's, it mirrors **this app's** screens (what that screen's
  outputs show), not the Windows desktop. Windows.Graphics.Capture *could* capture a monitor, but
  that isn't what the Mac's `screen::` means, so it isn't used for this.
- An older document's direct device reference shows through the named input set up with that
  device (`NamedInputFor`).

**Learnt the hard way:** a live picture used to stop updating after ten seconds on screen.
`MediaHub` copied an input's frames only while something had *asked* for its picture in the last ten
seconds, but a view only asks when it builds the item's drawing; after that WPF just redraws the
same bitmap. Views now hold a live picture (`MediaHub.RetainLive`/`ReleaseLive`) for as long as they
draw it, the way they already held video players. This also fixes the Video Input layer.

**Tests:** `tests/…/Scene/LiveInputFillTests.cs` (the Mac's). Pictures: `55-live-input-placeholder*`
and `55-screen-mirror-live.png` (the stage screen inside a slide on the audience preview).

## 6. Keystone on objects

**What was wrong.** An object's keystone (its top or bottom edge drawn narrower or wider) wasn't
drawn. WPF's 2-D transforms can only make parallelograms, never a trapezoid.

**How it works now.**

- `Core/Animation/BuildProjector.cs` is a port of the Mac's build projector: where a point of the
  item lands once keystone, skew, tilt, swing and perspective are applied, plus `QuadCorners` and the
  stretch-keystone strips, with the Mac's tests.
- `Rendering/KeystoneEffect.cs` is a small pixel shader. For each output pixel it works out which
  point of the untouched item belongs there:
  - **perspective** keystone (the Mac's default): the trapezoid is a picture tipped back, so the
    mapping is a homography (the same maths as a screen's corner pin, `OutputWarp`) and the narrow
    end is foreshortened;
  - **stretch** keystone: rows stay evenly spaced, each squeezed or widened, as the Mac's 32 strips.
- In `SceneView`, a keystoned item gets a keystone pass between its pose and its effects (the Mac
  warps after effects). It uses the trail passes' trick: the item lives in a `Source` that isn't
  in the visual tree, and a `Face` rectangle painted with it (a `VisualBrush` over a known region)
  carries the shader, so the shader's coordinates line up with the item.

**Learnt the hard way:** WPF draws pixel shaders in *software* (thumbnails, `RenderTargetBitmap`
pictures, PCs that can't capture on the graphics card) only for **ps_2_0** shaders. The other effects
are ps_3_0 and vanish there. The keystone shader is ps_2_0 (`ShaderCompiler.Compile` now takes a
target), so keystone shows everywhere.

**Differences from the Mac:** tilt and swing are still flattened to a squash (as before), and the
keystone is applied in the item's own frame before its 2-D transform, so a keystoned object that is
*also* tilted or swung is an approximation. Keystone alone, with skew, flip, rotation, moves and
scales, matches the Mac.

**Tests:** `tests/…/Animation/BuildProjectorTests.cs`. Pictures: `53-keystone.png` (software) and
`53-keystone-program.png` (graphics card).

## 7. "Text doesn't fit" warning

**What was wrong.** `TextLayout` worked out whether text overflowed its box, but nothing used it.

**How it works now.**

- `SlideSceneBuilder.ClippedTextObjectIds` (Core) is the Mac's `clippedTextObjectIDs`: the slide's
  own text objects whose words don't fit. Text that shows all its words its own way (on a path, a
  ticker, a block scroll) never counts, and nor does the design's own text. To know about block
  scrolls from the scene alone, `StyledText` now carries `Scroll` too, as the Mac's does.
- `TextLayout.OverflowsFrame` is the Mac's `TextRasterizer.overflows`: shrink as far as the text
  allows, then check the height (a box too narrow for one word wraps; that isn't "clipped").
- Each slide tile (`SlideTileViewModel.HasClippedText`) measures its slide just after the grid draws
  (at background priority, so opening a long song stays quick), and the tile shows the Mac's orange
  triangle with its tooltip (one column in the tile template in `MainWindow.xaml`).
- To make the warning true, **overflowing block text is now cut off at its box's edge**, as the Mac
  draws it, instead of spilling over whatever is around the box. Text that fits is drawn exactly as
  before (the clip only applies when the layout overflows).

**Tests:** `tests/…/Scene/ClippedTextTests.cs` (the Mac's, with a stand-in measure); the self-test
checks the real measure with the Mac's TextEngineTests cases and the badge (`51-clipped-text-badge.png`).

## Other things learnt

- **Line separators in source.** Writing `'
'` in a C# file through some tools turns it into the
  real character, which the compiler then reads as a line break. Use `(char)0x2028`.
- **A video re-fired soon after it ended** reuses the cached player (kept 1.5 s after the last view
  lets go, plus any media dissolve), which is paused at its end. This was already so before Phase 5;
  it's noted for whoever works on media cues.

*The PORTING.md rows for this work are in [PORTING.md](../PORTING.md); this is the walkthrough. See also [HOW-IT-WORKS.md](../HOW-IT-WORKS.md).*
