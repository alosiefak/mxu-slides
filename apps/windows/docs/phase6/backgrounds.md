# Phase 6: motion backgrounds

A church background behind the lyrics is usually a video that loops: slow light, drifting
smoke, soft colour. Buying them is expensive and they rarely match the season or the room.
This part of Phase 6 makes them inside the app. You pick a style, colour it, choose how fast and
how strong it is, and **Render**: every frame is drawn on this PC and written into an ordinary
H.264 MP4 in the media library, set to loop, which the Mac plays like any other video.

The Mac app has nothing like it, so this is Windows-only code. The shared documents never learn
anything new (see "Staying friends with the Mac" below).

## 1. Using it

**Media › Motion background…** (the button beside *Video inputs…*) opens the window:

- **Styles** on the left: eleven, each a small live preview. Click one to choose it.
- **Preview** in the middle: a bigger live preview at real speed, with sample lyrics over it so
  you can judge whether white text will read. Untick *Show sample lyrics* to see it bare.
- **Colours**: fifteen designed palettes (including the church seasons: Advent, Christmas, Lent,
  Easter, Pentecost), or your own two to four colours. Until you pick colours, each style comes
  in the palette it was designed for.
- **Motion**: *Speed* (¼× to 2×), *Intensity* (how bright and strong) and *Variation*:
  *Shuffle* moves everything somewhere else for a fresh take on the same look.
- **Video**: length (10–30 seconds), 1080p or 4K, 30 or 60 frames a second, and a name.

**Render** draws every frame. A progress bar shows the frame, the speed and the time left. You
can close the window: the render carries on and its progress shows in the notice bar. **Cancel**
stops it and nothing is added. The finished video is in the Media bin, in the folder "Motion
Backgrounds", marked *Loop*.

**Edit motion background…** (right-click a tile made this way) reopens the window on that
background's recipe. **Replace “…”** gives the existing item the new video: same item, so every
song and service using it changes, and a copy playing on the screens switches straight away.
**Save as New** keeps the old one and adds another.

## 2. A recipe, and staying friends with the Mac

Everything that makes a background is in a small record, `MotionRecipe`
(`Core/Motion/MotionRecipe.cs`): the style's id, the palette and its colours, speed, intensity,
seconds, height, frame rate, variation and name. The same recipe always makes the same video,
frame for frame, on any PC.

The recipe is Windows-only, so it can't go in the shared documents: the Mac refuses a whole
document with a field or value it doesn't know. It goes in the library's **Windows extras**
(`Core/Library/WindowsExtras.cs`), in `Windows extras/motion-background/<media item id>.json`,
where the Mac never looks. It's saved with a fingerprint of the item's file (`basis` = its
`fileHash`). If the file is later replaced by something else (on a Mac, say), the fingerprint no
longer matches and the recipe is quietly forgotten, because it no longer describes the video.

The video itself is an ordinary `MediaItem`: `mediaKind` video, `classification` background,
`loops` true, `folder` "Motion Backgrounds", with its length and size from the Phase 5 import
probe. Nothing new is written into it.

## 3. How one frame is drawn

`Core/Motion/MotionRenderer.cs` draws frame *i* of a recipe. A style
(`Core/Motion/Styles/*.cs`) is pure maths: given a moment in the loop, it says what colour each
point is, and lists the dots of light (sprites) floating over it. The renderer does the rest:

1. **The smooth part.** Soft styles (smoke, gradients) are asked on a grid 2–4× coarser than the
   picture (`FieldDivisor`) and the grid is enlarged with a **cubic B-spline**, the smoothest way
   to enlarge: 4–16× less work, and no visible difference because nothing in them is sharp.
   Styles with fine lines (waves, hexagons) are worked out at every pixel.
2. **Sprites**: stars, dust and out-of-focus lights are added at full sharpness, as light (they
   add up where they overlap). A dot smaller than a pixel or two would flicker as it moves across
   pixels, so it's drawn a little bigger and dimmer, with the same total light.
3. **Finishing**: the corners darkened a little (a vignette keeps the eye in the middle and the
   text readable), very bright light rolled off towards white instead of clipping, then film grain.
4. **Pixels**: BGRA for the previews, or straight into **NV12**, the layout the H.264 encoder
   takes, worked out from full-precision colour so there's only one rounding. A tiny random
   **dither** goes in before rounding: a slow dark gradient across 1920 pixels has only a couple
   of dozen shades to use and would show as stripes without it.

Everything is in **picture units**: y runs 0 to 1 down the picture and x from 0 to the aspect
ratio. So a recipe looks the same at 1080p and 4K, just sharper, and the tiny gallery previews
look like the real thing.

Steps 2–4 run in bands of 16 rows, so even 4K needs only small scratch buffers.

## 4. Why the loop is seamless

A loop is seamless when the frame after the last one is the first one. Two ideas make sure:

- **Time is a circle, not a line.** `LoopClock` turns "how far through the loop" (0 to 1) into a
  point going round a circle. Noise is sampled in four dimensions: two for the picture and two for
  that point (`LoopNoise.Loop`). As the point goes round, the picture changes smoothly, and when it
  comes back to the start, so does the picture. The circle's radius sets how much it changes: a
  bigger circle is faster flow. `MotionScene.Turn` picks the radius from the speed and the length,
  so the same speed looks the same in a 10- or a 30-second loop.
- **Anything else repeats a whole number of times.** A speck of dust lives a few seconds, then
  starts again somewhere new; its life is chosen to fit a whole number of times into the loop,
  and where it starts depends only on which life it's on (`LoopClock.Rounds`). A hexagon grid
  looks the same after drifting exactly one cell, so it drifts exactly one cell per loop. Waves
  roll exactly once across. Smoke that rises for ever uses the **flow-map trick**
  (`LoopNoise.Drift`): two copies of the same smoke, half a drift apart, rise and take turns,
  each fading out before it jumps back.

Frames are **drawn, not recorded**. Frame *i* is drawn at loop position *i* ÷ *frames* and
stamped *i* ÷ *frame rate* seconds into the file (`LoopTiming`), worked out with whole numbers so
nothing drifts. A slow PC makes exactly the same video as a fast one, just more slowly. Film grain
and dither are seeded by the frame number (wrapped round the loop), so they repeat too.

The tests check this for every style: drawing frame *N* gives frame 0's pixels exactly, and the
step from the last frame to the first is no bigger than any other step.

## 5. The styles

| Style | How it works |
|---|---|
| Gradient Flow | Six coloured points wander on closed paths; every pixel blends their colours (in OKLab) by nearness, after being nudged by noise so the borders curve. |
| Aurora | Two curtains hang from wavy lines: a bright foot, light fading upward broken into rays by tall thin noise, folds that lean as they rise. Faint stars behind. |
| Light Rays | Noise around the circle of directions from a light above the picture: neighbouring directions brighten together, which makes shafts. Haze, a bloom at the top, dust glinting where a beam crosses it. |
| Bokeh | Three depths of soft discs with brighter rims, as a lens draws lights it isn't focused on; nearer ones bigger, fainter and drifting further. |
| Floating Dust | Specks living a few seconds each, drifting up with a wobble, lit more near a soft light; a few big blurred ones in front. |
| Smoke & Fog | Domain-warped noise (noise looked up where more noise pushed it) rising by the flow-map trick, lit from below. |
| Gentle Waves | Ribbons of fine lines strung between two wavy edges; where the edges cross, the ribbon seems to twist like silk. Each pixel only looks at the few lines near it. |
| Geometric Drift | Three layers of hexagon lines, each drifting one cell per loop, lit by a wandering soft light; some cells glow. |
| Starfield | Nebula gas and dust lanes drifting by the flow-map trick; stars at different depths drifting past (parallax), fading in and out. |
| Film Wash | Broad pools of colour, a warm light leak along an edge, a faint flicker (waves that go round a whole number of times per loop) and heavy grain. |
| Liquid | Domain warping twice over (Inigo Quilez's technique): swirls like ink folding through oil, with soft glossy folds. |

## 6. Colour

- Light is added up in **linear light** (twice the light is twice the number), which is how glows
  and lens blur really mix. Only the finished picture is turned into sRGB.
- Colours are blended in **OKLab** (`MotionColor`), where equal steps look equally different.
- A blend **keeps its colour** (`MotionColor.LerpLab`). In OKLab a colour's distance from grey is
  how colourful it is (its *chroma*). A straight line between two hues far apart, purple and gold
  say, passes close to grey half-way, and that reads as mud. So the blend takes its lightness and
  hue from the straight line but its chroma from the two colours' own (their average, half-way):
  purple to gold goes through a clear rose. Colours of one hue blend just as before; two exactly
  opposite colours still meet in grey (the boost is capped, so it can't invent a strong colour
  that neither has). Gradient Flow does the same for its average of six colours (`KeepChroma`).
- A palette is four colours, deep to highlight (`MotionPalette.Stops`). A preset keeps its
  designed order (in "Northern Lights" the green is the aurora's foot though it looks lighter than
  the violet above it). Your own colours are sorted dark to light and spread to four, and the
  darkest is deepened if it's too pale to put white text on (same hue, just darker).

## 7. Threads

- `RenderCrew` (`Core/Motion/RenderCrew.cs`) is a small team of threads of its own that share
  out the rows: never the shared .NET thread pool (which show timing keeps clear of, see
  HOW-IT-WORKS section 26), and at **below-normal priority**, so rendering during a service never
  makes the screens stutter. Each part of the picture is written by exactly one thread, so the
  result doesn't depend on how the work was split.
- A render (`Broadcast/Library/MotionBackgroundMaker.cs`) runs two things at once: the crew draws
  frame *i* + 1 while an encoder thread compresses frame *i*. They pass three reusable buffers
  back and forth (a 4K frame is 12 MB), so memory stays flat however long the loop.
- The window's previews (`Rendering/MotionPreviewer.cs`) have one thread and a small crew. About
  30 times a second it draws the big preview (the gallery every third time) and hands each picture
  to the UI thread. A picture isn't drawn again until the UI has taken the last one, so a busy UI
  sees fewer frames, never a pile-up. While a render runs, the gallery stops and the preview drops
  to four frames a second.
- `MainViewModel.MotionBackgrounds.cs` runs one job at a time (it uses every core), on a thread of
  its own, and files the result on another.

## 8. Encoding and filing

- `Broadcast/Media/RenderedVideoWriter.cs` uses the recorder's own H.264 encoder and MP4 writer,
  with two differences. It always uses Microsoft's **software** encoder: a graphics-card encoder
  that's slow to ask for a picture makes the recorder drop one, and a loop can't lose a frame (the
  software encoder takes each picture before it returns, and makes the same file on every PC). And
  it writes a **plain MP4**, not the recorder's crash-safe fragmented one (see below).
- The bitrate is generous, about 0.2 bits per pixel per frame (12 Mbit/s at 1080p30, capped at
  60 for 4K60), because smooth gradients are what video compression handles worst.
- The file is written as `name.partial.mp4` in `<library>/spool/motion/` and only renamed when
  it's complete, so a cancelled or crashed render never leaves a broken file that looks finished.
  (Not in `spool` itself: the recorder sweeps that for its own unfinished recordings.)
- `MotionBackgroundMaker.FileIntoLibrary` imports it with the Phase 5 code
  (`MediaImporting.AddMedia`, probed by `MediaImportProbe`), or for **Replace** gives the existing
  item the new file (`MediaRelink.ReplaceFile`), then saves the recipe. After a Replace,
  `MediaHub.ReloadSilentVideos` (`Rendering/MediaHub.Reload.cs`) reopens any player showing it on
  the new file. Elsewhere a replaced video keeps playing its old file until it's fired again (as
  Phase 5 does), but here you asked for the new look on the screens.

## Things learnt the hard way

- **A fragmented MP4 said the loop was 9 seconds long.** Every one of the 300 frames of a 10-second
  loop was in the file, but its header left out the last one-second fragment. A player that loops
  by the stated length would cut the last second and break the seam. Renders don't need crash safety (they're only used once finished), so the writer makes a
  plain MP4, whose header states the exact length: `Mp4SinkWriter.CreateForEncoded` got a
  `crashSafe` option (default true, so the recorder is unchanged). The test checks the file says
  exactly 10.0 seconds.
- **Two different patterns taking turns look like morphing, not drifting.** The first drifting
  smoke and nebula cross-faded between two unrelated patterns; in a strip of frames one second
  apart they visibly melted into new shapes. The flow-map trick needs two copies of the *same*
  pattern, half a drift apart, mixed plainly (they're nearly the same, so no contrast fix is needed).
- **Fine detail churns faster.** In layered noise each finer layer also travels further round the
  circle of time, so with five layers the finest changes 16× faster than the big shapes. Smoke,
  liquid and the nebula looked frantic at "speed 1" until their circles were made smaller. Look at
  frames a second apart, not just single frames, to judge speed.
- **Noise is calmer near its grid.** A small circle of time that sits on one of the noise's grid
  planes gives less contrast than one between them (a spread of 0.24 against 0.4). The styles were
  tuned by eye with their own lanes, so it's documented rather than changed.
- **A crew of just the caller** has no helper threads to wake, and `SemaphoreSlim.Release(0)`
  throws: disposing one crashed until a test with no helpers caught it.
- **Blending in OKLab still went grey.** Liquid in "Royal" (purple and gold) had big lilac-grey
  washes, and Gradient Flow looked dusty: a straight line between far-apart hues loses most of
  its colour half-way. Keeping the chroma (section 6) fixed both without touching the styles'
  maths; palettes of one hue (Midnight, Ember) look as they did.
- **A preview can't predict a render's time.** At 480 × 270 the work per frame that doesn't depend
  on size dominates, so timing the preview overestimated a 1080p render about four-fold. The window
  says how many times bigger a job than 1080p30 the choice is instead, and the real time left
  shows once rendering starts.

## Speed on this PC

Intel Core i5-10500H (6 cores, 12 threads), Release build, the real pipeline
(`MotionBackgroundMaker.Render`: drawing at below-normal priority, encoding alongside), each
style in its own palette at the default speed and intensity. The PC was shared with five other
engineers' builds and self-tests, so each run says what share of the PC's processors the render
actually got; the 1080p runs had about 80 % of it (nearly quiet). A render's speed is roughly
"processors free ÷ processor-seconds a frame", so the last column works the 4K60 speed out for
the same 80 %.

| Style | 1080p30 fps | 20 s at 1080p30 (600 frames) | 4K60 fps measured (share of PC) | 4K60 fps at 80 % | 20 s at 4K60 (1,200 frames) |
|---|---|---|---|---|---|
| Gradient Flow | 22 | 27 s | 5.4 (82 %) | 5.4 | 3.7 min |
| Aurora | 22 | 27 s | 4.7 (74 %) | 4.9 | 4.1 min |
| Light Rays | 14 | 43 s | 1.7 (42 %) | 3.1 | 6.5 min |
| Bokeh | 29 | 20 s | 4.4 (50 %) | 6.9 | 2.9 min |
| Floating Dust | 28 | 22 s | 4.1 (53 %) | 6.0 | 3.3 min |
| Smoke & Fog | 17 | 36 s | 3.1 (63 %) | 3.8 | 5.3 min |
| Gentle Waves | 15 | 41 s | 2.8 (66 %) | 3.2 | 6.3 min |
| Geometric Drift | 8.9 | 67 s | 0.9 (35 %) | 1.9 | 10 min |
| Starfield | 6.5 | 93 s | 1.2 (64 %) | 1.5 | 13 min |
| Film Wash | 26 | 23 s | 5.9 (78 %) | 5.8 | 3.4 min |
| Liquid | 7.3 | 83 s | 1.5 (72 %) | 1.6 | 12 min |

So a 1080p30 background takes from 20 seconds to a minute and a half. 4K60 is eight times the
pixels a second of video: 3 to 13 minutes for 20 seconds, up to about 20 minutes for a 30-second
Starfield or Liquid. It can run while you get on with something else (close the window, and the
notice bar shows its progress). Film Wash shows the floor: finishing, film grain and the H.264
encoder alone cost about 0.36 processor-seconds a frame at 1080p and 1.6 at 4K. Where the time
goes in the slow styles: Starfield and Liquid stack many layers of four-dimensional noise at every
point of their field, and Geometric Drift works out three hexagon layers at every pixel.

## Where to look

| What | File |
|---|---|
| Recipe, timing, palettes, colour maths | `Core/Motion/MotionRecipe.cs`, `LoopTiming.cs`, `MotionPalette.cs`, `MotionColor.cs` |
| Noise and the circle of time | `Core/Motion/LoopNoise.cs` |
| Drawing a frame, the threads | `Core/Motion/MotionRenderer.cs`, `MotionStyle.cs`, `RenderCrew.cs` |
| The styles | `Core/Motion/MotionStyles.cs`, `Core/Motion/Styles/*.cs` |
| Encoding and filing | `Broadcast/Media/RenderedVideoWriter.cs`, `Broadcast/Library/MotionBackgroundMaker.cs` |
| The app | `Views/MotionBackgroundWindow.cs`, `Views/MainWindow.MotionBackgrounds.cs` (button and tile menu), `ViewModels/MainViewModel.MotionBackgrounds.cs` (the job), `Rendering/MotionPreviewer.cs`, `Rendering/MediaHub.Reload.cs` |
| Unit tests | `tests/MxU.Slides.Core.Tests/Motion/*` (seamless loops, determinism, palettes and colour blends, NV12, timing, noise, recipes, the crew), `tests/MxU.Slides.Broadcast.Tests/MotionBackgroundTests.cs` (a playable MP4 with every frame, exact time stamps and length, what's in the file matches what was drawn, filing, Replace, Cancel, 4K60) |
| Self-test | `Infrastructure/SelfTest.Backgrounds.cs`, pictures `bg-…` (`bg-01-all-styles` is every style on one sheet, `bg-02-<style>-…` each at full size, `bg-21-audience-screen` a render live behind a lyric) |

## Things to try

1. **Make an Advent background.** Gradient Flow, palette Advent, intensity 40 %, 20 seconds.
   Render, then right-click a song › *Use as background for the open song*.
2. **Watch the seam.** Make a 10-second loop and play it in the Media bin. Look at the moment it
   jumps back to the start: nothing happens. Then read `EveryStyleLoopsSeamlessly`.
3. **Change a style.** In `Styles/BokehStyle.cs`, change the near layer's count (8) to 20 and its
   brightness down. Run `dotnet test --project tests/MxU.Slides.Core.Tests` (the loop tests still
   pass: a sprite on a closed path loops whatever its numbers) and look at the gallery.
4. **Add a palette.** Add a line to `MotionPalette.Presets`, darkest colour first. It appears in
   the window. Keep the first colour very dark so lyrics read.
5. **Write a twelfth style.** Copy `FilmWashStyle.cs`, give it a new `Id`, add it to
   `MotionStyles.All`. The tests run over every style, so they check your new one loops seamlessly,
   draws the same on any number of threads and takes its palette.

## Different from the Mac

The Mac has no motion background maker. Its twins are only the things this builds on: the media
library and the recorder's MP4 writing. Nothing here changes what the Mac reads: the videos are
ordinary H.264 MP4 media items, and the recipes are in the Windows extras folder.

## For PORTING.md

Windows-only (no Swift twin; add under "Windows only"):

| C# | What |
|---|---|
| `MxU.Slides.Core/Motion/*` (`MotionRecipe`, `LoopTiming`, `LoopNoise`, `MotionPalette`, `MotionColor`, `MotionRenderer`, `MotionStyle`, `MotionStyles`, `RenderCrew`, `Styles/*`) | Motion backgrounds: deterministic, seamlessly looping frames drawn from a recipe |
| `MxU.Slides.Broadcast/Media/RenderedVideoWriter.cs`, `Broadcast/Library/MotionBackgroundMaker.cs` | Encoding a drawn loop to H.264 MP4 and filing it as a looping background; the recipe in Windows extras (`motion-background`) |
| `MxU.Slides.Windows/Views/MotionBackgroundWindow.cs`, `Views/MainWindow.MotionBackgrounds.cs`, `ViewModels/MainViewModel.MotionBackgrounds.cs`, `Rendering/MotionPreviewer.cs`, `Rendering/MediaHub.Reload.cs` | Media › Motion background…, Edit motion background…, live previews, reloading a replaced background on the screens |

One change to a ported file: `Recording/Mp4SinkWriter.cs` (`OutputEngine/…/Recording/*` row) got
`CreateForEncoded(…, crashSafe: false)`, a plain MP4 for files only used once finished. The
recorder's behaviour is unchanged (crash-safe fragments by default), so recording ports are
unaffected.

*This is the walkthrough; see also [HOW-IT-WORKS.md](../HOW-IT-WORKS.md) and [PORTING.md](../PORTING.md).*
