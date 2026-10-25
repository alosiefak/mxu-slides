# Phase 6: new transitions

The Mac app has seven transitions: Cut, Dissolve, Fade Black, Fade White, Fade Colour, Blur
Dissolve and Film Burn. Phase 6 adds eighteen more on Windows, drawn by the graphics card:

| Group | Kinds | Settings |
|---|---|---|
| Move | Push, Slide Over, Slide Reveal, Whip Pan | direction |
| Zoom | Zoom In, Zoom Out, Zoom Through, Cross Zoom | |
| Wipe | Iris, Diamond, Clock, Soft Wipe, Luma Wipe | softness (Soft Wipe also a direction) |
| 3-D | Cube, Flip | direction |
| Effects | Light Leak, Glitch, Ripple | colour (Light Leak) |

You can choose one in four places: a slide (right-click its tile › **Transition**), a whole
presentation (the same submenu, or right-click it in the library list), a media item (right-click
its tile), and the two room defaults (the live header's transition chips, or Settings ›
Transitions). Each place shows every kind as a small tile that plays itself.

This note explains how it works, for someone learning the code. The self-test checks all of it
(`Infrastructure/SelfTest.Transitions.cs`, pictures `60-…` to `62-…`).

## 1. The problem: the Mac must still read our documents

Slides, presentations and media items live in the library, which the Mac shares. Their
`transition` field is a `TransitionKind`, a closed list of seven names. A Mac that meets a name it
doesn't know (say `"push"`) refuses **the whole document**. So a Windows-only kind can never be
written into a shared document.

The way round it has two halves:

1. **The document keeps something the Mac can play.** Choosing Push Left, 0.8 s writes the
   *nearest* Mac kind at the same length into the document: Dissolve, 0.8 s
   (`WindowsTransitions.ToShared`, in `Core/Animation/WindowsTransitions.cs`). Light Leak's nearest is
   Film Burn, the blurred zooms' and the whip's is Blur Dissolve; everything else's is Dissolve.
2. **The Windows kind is kept beside the library**, in `Windows extras/slide transitions/<slide
   id>.json` (and `presentation transitions`, `media transitions`), where the Mac never looks.
   `Core/Library/TransitionExtras.cs` does this, on top of Phase 6's `WindowsExtras` store.

Each extra keeps a **fingerprint** of the shared value it was saved with: a short hash of the
Dissolve's JSON. When the outputs ask for a slide's transition, the extra counts only while the
document still holds that same Dissolve. If a Mac has since changed the slide's transition (to a
Fade Black, or just to a different length, or back to none), the fingerprints differ and the
**Mac's choice wins**. A slide with no extra simply plays its shared kind.

Choosing one of the Mac's own seven kinds (or the default) deletes the extra: the document then
says everything.

Three edits give slides new ids, so the extras follow them: duplicating a slide or a presentation
(`TransitionExtras.Copied`), and saving a song from the lyrics editor, which rebuilds its slides
(`TransitionExtras.CarryOver`, matching new slides to old ones in order by their shared transition,
the same one SongEditing carried across).

The room defaults are different: they are this PC's settings (`settings.json`), never shared, so
they hold the new kinds directly. `OutputTransition` (`Infrastructure/AppSettings.cs`) gained every
kind, and `AppSettings.Transitions.cs` adds each default's direction, softness and colour.

## 2. A transition is two pictures and some numbers

The existing transitions work item by item: in a Dissolve, each leaving item fades out and each
arriving one fades in (`Core/Animation/SceneTransitionEngine.cs`, HOW-IT-WORKS section 29). That
can't make a push: a push moves the *whole picture*.

So the new kinds work on pictures. While one plays on a layer:

1. **The engine** (`SceneTransitionEngine.Composite`, the `Picture` case) gives the layer both whole
   pictures as items: everything it showed before, renamed `…::leaving`, then everything it shows
   now. It also puts a `LayerMix` on the layer: which transition, how far through (0–1) and how long
   it has run.
2. **The view** (`Rendering/SceneView.Transitions.cs`) sees the `LayerMix` and, instead of drawing
   both pictures, puts each into a container of its own that isn't on the screen. In their place it
   draws one rectangle the size of the canvas carrying a `TransitionEffect`, a WPF pixel shader. The
   shader reads the two pictures through brushes (the same trick the Echo and keystone passes use)
   and works out every pixel of the result.
3. **The numbers** come from `Core/Animation/PictureTransitions.cs`, recomputed every frame.

The view never knows what a push *is*. All the geometry is in Core, in plain C#, where it can be
unit tested; the shaders just follow instructions.

One trap (the self-test checks it, `61-push-off-to-nothing-middle.png`): a picture can be empty (a slide pushed off to a clear screen, or
the first slide pushed onto one). WPF draws nothing for a brush of nothing, and skips an effect on
nothing, so the slide just vanished. Each picture now has a clear rectangle the size of the canvas
at the bottom (`MixPass` in `SceneView.Transitions.cs`), so it always covers the canvas.

If Next is pressed again before a push lands, the old picture is the half-pushed one: the new
`LayerMix` carries the old one as `Inner`, frozen where it was caught, and the view draws a mix
inside a mix. If one of the Mac's kinds catches a picture transition, the two pictures are
cross-faded where they had got to (`SceneTransitionEngine.Flattened`).

Each layer still changes its own way, as on the Mac. A new verse over the same background pushes
only the words; a slide that brings a new background pushes both layers, in step, so they look like
one picture.

### Easing

The engine's clock runs straight: half the time gone is 0.5. A push that moved at one speed would
start with a jolt and stop dead. `Core/Animation/TransitionCurves.cs` gives each kind a curve:
pushes, slides, zooms and the cube start gently, are quickest in the middle and settle
(`InOutCubic`); a whip pan waits, then goes very fast (`InOutQuint`); wipes and the ripple use the
gentlest S-curve (`InOutSine`). Blurs, glows and the glitch are strongest in the middle (`Bell`).
`Speed` is how fast the eased progress is changing, and a whip pan's streak is that long.

### Moves, zooms and the 3-D kinds: flat pictures in space

Push, Slide Over, Slide Reveal, Zoom In and Out, Cube and Flip are all "two flat pictures placed
on the screen". `PictureTransitions.Planes` says where each picture's four corners land:

- **Push**: the two pictures side by side, sliding along together.
- **Slide Over**: the new picture slides over; the old one drifts a little the same way and dims,
  for depth. **Slide Reveal** is the reverse.
- **Zoom In**: the old picture grows past you and fades, uncovering the new one, which settles
  back into place. Neither is ever smaller than the screen, so no edge shows.
- **Cube**: the old picture is the front of a box and the new one its side. The box turns a
  quarter-turn about its own middle (half its width behind the screen) and backs away a little at
  the half-way point, so its nearest edge never grows past the screen. The corners are turned in
  3-D and then seen through a camera (`Project`: further away is smaller, towards the middle). A
  side facing away from the camera is hidden: its corners then run the other way round.
- **Flip**: a card turning over about its middle, the old picture on its front, the new on its back.

Four corners in, four corners out: that's a **homography**, the same maths as a screen's corner pin
(`Core/Outputs/OutputAdjust.cs`, `OutputWarp.CenteredHomography`). It tells the shader, for each
pixel of the screen, which point of the picture belongs there. A homography handles a plain slide
and a perspective cube with the same few sums.

The picture on top hides what's under its whole area, not just its opaque parts. A slide layer is
mostly transparent round its words; without this, the old words would show through the new slide
as it slid over them.

### The other kinds

- **Wipes** (`Wipe` shader): every pixel gets a "reveal time", 0 first and 1 last, from a pattern:
  distance from the middle (Iris), the same in a diamond (Diamond), the angle round the middle
  (Clock), the distance along the direction (Soft Wipe), or the old picture's brightness (Luma
  Wipe). A pixel shows the new picture once the edge has passed its time. The edge starts a little
  before 0 and ends a little after 1, so the soft edge has fully cleared at both ends
  (`WipeThreshold`).
- **Zoom Through and Cross Zoom** (`Radial`): the pictures are streaked towards a centre, eight
  samples along each streak.
- **Whip Pan** (`Whip`): a push streaked along its direction by its speed.
- **Light Leak** (`Light`): a cross-fade with a band of warm light sweeping across. If the background
  and the words both leak, only the lowest layer lights the whole screen (`LayerMix.LitBelow`), or
  the light would be added twice and white everything out.
- **Glitch**: rows jump, colours split and blocks switch to the new picture in a scattered order,
  jittering 24 times a second.
- **Ripple**: rings of waves bend both pictures, and a soft ring spreading from the middle carries
  the new one out.

## 3. The shaders, and PCs without a graphics card

The seven shaders are in `Rendering/TransitionShaders.cs`, as readable HLSL that the compiler in
Windows turns into code when the app starts (`Rendering/ShaderCompiler.cs`, HOW-IT-WORKS section 18).

They are all **ps_2_0**, the oldest and smallest kind, on purpose. WPF can run ps_2_0 shaders
without a graphics card, in software. So the new transitions also draw in thumbnails, in the
self-test's pictures, and in the program feed's **software copy** (HOW-IT-WORKS section 17: a PC that
can't capture on the graphics card copies the program in software, which skips ps_3_0 effects).
Recordings and streams from such a PC show the real transition, not a cut.

ps_2_0 allows 64 sums and 32 texture reads per pixel, which shaped the shaders:

- Blurs take eight samples per picture. Eight copies would show as steps, so each pixel slides its
  samples by its own share of the gap (a fixed noise pattern, "dither"), which the eye reads as a
  smooth streak.
- WPF reads a picture's edge colour for any point outside it, so each shader masks what's off its
  pictures itself. A point is inside exactly when t − t² ≥ 0, one comparison instead of two.
- Pictures are premultiplied (colour already times alpha), so mixing them is plain arithmetic and a
  transparent part of a layer stays transparent.

On a PC that can't run pixel shaders at all, a Windows-only transition plays its nearest Mac kind,
at the same length (`SceneView.Drawable`, `LiveTransitions.Fallback`). `MXU_SLIDES_TRANSITIONS=simple`
makes any PC behave that way, to test it or on a graphics card that draws them badly.

## 4. Smooth from the first frame

- **Compiling the shaders** takes tens of milliseconds each. That would stall the first push of a
  service, so the first SceneView made (a thumbnail, at start-up) sets them compiling on a thread of
  their own (`TransitionShaders.WarmUp`), along with one run of the maths for every kind (so .NET has
  compiled that code too). `ShaderCompiler` now compiles outside its lock, so this never holds up a
  shader the screen needs.
- **Each graphics card** makes its own copy of a shader the first time it draws it, and an output
  may be on a different card from the operator's screen (this laptop has an Intel UHD and a GTX
  1650). So the first time a live view changes layer by layer, it draws every transition shader once
  on an all-but-transparent speck under its own picture, for three frames (`WarmGraphicsCard`).
- **Each frame** costs one shader pass per changing layer. The two pictures' brushes keep what they
  drew (`CachingHint.Cache`) and only redraw when something in them changes (a video frame, a build),
  so a transition between still slides costs little more than the shader itself.

The self-test times every frame the screen draws during a 1.6 s push on the program feed and
reports it (`61-…`): see the report's "Push on the program feed" line.

## 5. Choosing them

`Views/Transitions/`:

- `TransitionPicker.cs`: the tiles, grouped, with the length and (when the kind has them) the
  direction arrows, softness and colour underneath. Tiles with a direction or a softness play with
  the ones chosen, so you can see what each would look like.
- `TransitionPreview.cs`: one playing tile. It pushes the two sample slides (`TransitionSamples.cs`)
  to a real `SceneView` in turn, so a preview is exactly what the outputs do.
- `TransitionPickerWindow.cs`: **Transition…** for a slide, a presentation or a media item: a large
  preview, the picker, and what a Mac will play instead. Every change is saved at once, as Slide
  Actions are.
- `TransitionMenus.cs`: puts the Transition items into the three right-click menus. Rather than each
  menu listing it, WPF lets one handler hear every context menu opening (a *class handler*), and the
  element the menu opened on says what it's for. So the transition menus all live in one file.
- `RoomTransitionPopover.cs` and `TransitionSettingsSection.cs`: the room defaults, from the live
  header's chips and from Settings.

The rules for which transition wins are the Mac's and haven't changed (HOW-IT-WORKS section 29):
a slide's own, else its presentation's, else the room default; media its own, else the
presentation's, else the media room default. `LiveTransitions.For` now takes the Windows kinds that
go with each (`LiveCues.SlideWindows`, `PresentationWindows`, the `mediaWindows` lookup and
`RoomTransitions.Of`).

## 6. Tests

- `tests/…/Animation/WindowsTransitionsTests.cs`: the catalogue; a Windows kind is stored as its
  nearest Mac kind at the same length, and the shared JSON only ever names one of the Mac's seven.
- `tests/…/Animation/PictureTransitionsTests.cs`: the curves; where each picture is (each direction
  of a push, the cube's sides, the flip's faces, nothing ever looming past the screen); the
  homographies; the wipes start and end clean; blur, glow and glitch strengths.
- `tests/…/Animation/PictureTransitionEngineTests.cs`: the engine hands over both whole pictures; a
  transition caught mid-way is drawn as it was; a Mac kind catching one cross-fades where it had got;
  the light leak lights once; which transition wins for slides, presentations, media and the room.
- `tests/…/Library/TransitionExtrasTests.cs`: the fallback in the document; a Mac edit wins; a
  missing extra means the shared kind; duplicates and edited lyrics keep theirs.
- `tests/MxU.Slides.Windows.Tests/Rendering/TransitionShaderTests.cs` (a new test project): every
  shader compiles as ps_2_0 with the compiler in Windows.
- The self-test: all eighteen kinds pictured half-way (`60-00-all-transitions-half-way.png` shows
  them together), a push on the real program feed with its frame timing, the Mac-readable document,
  a Mac edit winning, the no-shader fallback, and the pickers.

## What isn't done

- A transition can only be typed up to 3 s in the picker (the slider); the Mac allows 30 s typed.
- Moving a presentation to another PC with a `.mxuslides` file carries the shared transition only:
  the Windows kind isn't in the file (a Mac must be able to read it), so the other PC plays the
  Dissolve until it's chosen again.
- Overlays, alerts and live cameras still cut, as on the Mac (overlays play their own In and Out
  animations). The engine and the view would mix those layers too, if something chose a transition
  for them.

## For PORTING.md

Windows only: none of these has a Swift twin, so nothing needs porting when upstream changes. Rows
for the "Windows only" part of the map:

| C# | What it is |
|---|---|
| `MxU.Slides.Core/Animation/WindowsTransitions.cs` | The 25 kinds the Windows app plays (the Mac's 7 and 18 of its own), and `ToShared`: the nearest Mac kind a shared document keeps |
| `MxU.Slides.Core/Animation/TransitionCurves.cs`, `PictureTransitions.cs` | Easing, and each Windows-only kind's geometry as shader constants |
| `MxU.Slides.Core/Library/TransitionExtras.cs` | Slides', presentations' and media items' Windows-only transitions beside the library (`WindowsExtras`), fingerprinted so a Mac edit wins |
| `MxU.Slides.Windows/Rendering/TransitionShaders.cs`, `TransitionEffect.cs`, `SceneView.Transitions.cs` | The ps_2_0 shaders, their effect, and the view's mix passes and warm-up |
| `MxU.Slides.Windows/Views/Transitions/*` | The picker, its previews, the transition window, the menus, the room-default popover and Settings section |
| `MxU.Slides.Windows/ViewModels/MainViewModel.Transitions.cs`, `Infrastructure/AppSettings.Transitions.cs` | Saving the choices; the room defaults as any kind |

And two notes for rows that already exist:

- `SceneTransitionEngine.swift` → `SceneTransitionEngine.cs`: the C# has an extra kind,
  `SceneTransitionKind.Picture`, and `AnimatedLayer.Mix`; a port of a Swift change to `composite`
  must keep the `Picture` case and the interrupted-mix flattening at the top of `Composite`.
- `RenderContext.swift` (`sceneTransition`, `roomDefault`) and `ServiceControls.swift`
  (`nextLiveTransitions`) → `LiveTransitions.cs`: the C# also takes the Windows-only kinds (`Own`,
  `FromWindows`); the rules themselves are still the Mac's.

*This is the walkthrough; see also [HOW-IT-WORKS.md](../HOW-IT-WORKS.md) sections 17, 18 and 29.*
