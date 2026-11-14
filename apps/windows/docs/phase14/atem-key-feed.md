# A key feed for a video switcher (Phase 14)

> **Not tried on a real ATEM.** Everything here was tested inside the app only: the pictures the
> app sends were checked pixel by pixel. Nobody has plugged it into an ATEM Mini Pro yet. The
> ATEM steps below come from how Blackmagic's keyers work in general. Where a menu name is not
> certain, this page says "in the keyer's settings" instead of guessing. Try it on a weekday first.

## What a key feed is

A video switcher can lay one picture over another. To do that it needs to know which parts of
the top picture are see-through. A **key feed** is a picture made for that job: the words and
graphics you want, and one flat colour everywhere else. The switcher's **keyer** makes that
colour see-through, so the camera shows there.

MxU Slides can now make any screen a key feed. That screen shows only:

- the slide's words and objects,
- overlays (lower thirds, tickers, motion templates used as overlays),
- alerts and messages that are aimed at the audience.

It does not show media backgrounds, background or foreground video, video inputs, or a slide's
own background fill or full-screen background picture. Everywhere else is the key colour, edge to
edge. Your other screens are not changed at all: the projector still shows the full slide.

## The two kinds

**Over black, for a luma key.** A luma key reads brightness. Black is see-through, white is
solid, and greys are in between. It is the simpler of the two and gives the cleanest edges on
white words. Its weakness is that it cannot tell a black background from a dark graphic: a dark
box behind a name fades away with the black.

**Over green, for a chroma key.** A chroma key reads colour. Pure green is see-through and every
other colour is solid, dark ones included. So a dark lower-third box stays. Its weaknesses are a
thin green edge around soft things (glows, shadows, the smooth edges of letters) and that
anything green in your graphics goes see-through as well.

Start with **over black**. Move to **over green** only if you need dark boxes to stay.

## Why you need two outputs from the laptop

The projector needs the whole slide, background and all. The ATEM needs the key feed. They are
different pictures, so they need different cables:

- output 1 (HDMI or USB-C to HDMI): the projector or TV, showing the ordinary Audience screen;
- output 2: into one of the ATEM's HDMI inputs, showing the key feed screen.

The laptop's own display stays as the operator's screen. In Windows, set both outputs to
**Extend** (Settings › System › Display), not Duplicate, and set the ATEM's one to 1920 × 1080.

(If the room's screen is fed *from the ATEM* instead, so the room sees camera and words together,
one output is enough.)

## Setting it up in MxU Slides

1. Plug the second output into an ATEM input, say input 4.
2. Open **Screens & Settings › More screens…**.
3. Press **+ Add screen**. Name it "ATEM". Leave **Shows** on the audience program and the size on 1920 × 1080.
4. Under **Outputs**, choose the display that is the ATEM. (**Identify displays** flashes each display's number.)
5. Set **Key feed** to **Over black (for a luma key)** or **Over green (for a chroma key)**.
6. Optional: set **Look** to a lower-third design. The stream then gets the lyrics along the
   bottom while the projector shows them full screen. Look and Key feed work together.

A key feed screen has a small **KEY** chip in the Screens list (black, or green), and the
operator's Screens panel says "KEY (black)" or "KEY (green)" beside it.

## Setting up the ATEM Mini Pro

Use **ATEM Software Control** on a computer joined to the ATEM by USB or network. The keyers are
on the **Switcher** page, in the palettes at the side.

The ATEM Mini Pro has one **upstream keyer** and one **downstream keyer**. "Downstream" means it
is added after everything else, so it stays on while you cut between cameras. That is what you
want for lyrics.

### Over black: the downstream keyer as a luma key

1. Open the **Downstream Key 1** palette.
2. Set **Fill Source** to the input the laptop is on (input 4 in the example).
3. Set **Key Source** to the same input. The keyer then uses the picture's own brightness to decide what is solid.
4. Tick **Pre Multiplied Key** (see below). If the words look thin or grey, untick it and adjust
   **Clip** and **Gain** in the keyer's settings until the black is fully gone and the words are solid.
5. Put the key on air: **DSK 1 ON AIR** in the software. To have it fade in with the next
   transition instead, use **DSK 1 TIE**.

### Over green: the upstream keyer as a chroma key

1. Open the **Upstream Key 1** palette and choose its **Chroma** tab.
2. Set **Fill Source** to the input the laptop is on.
3. Pick the green. In the keyer's settings there is a way to sample the key colour from the
   picture; sample a patch of the plain green. Then adjust the keyer's edge and spill settings
   until the green edge around the letters is gone.
4. Put the key on air with the **KEY ON** button (on the ATEM Mini's own panel, or **ON AIR** under
   the next-transition buttons in the software).

An upstream key belongs to the program you are building, so check it stays on when you cut
between cameras. If it drops, the downstream keyer with a luma key is the steadier choice.

### What "pre multiplied key" means

A soft edge is part see-through. There are two ways to store such a picture. In a
**pre multiplied** picture the colours have already been dimmed by how see-through they are: a
half see-through white is stored as mid grey. White words over black are exactly that, because
the app has already blended each soft edge with black.

With **Pre Multiplied Key** ticked, the ATEM trusts this: it adds your picture on top of the
camera and dims the camera underneath by the picture's brightness. Edges come out clean. With it
unticked, the ATEM dims your picture a second time, which can leave a dark line around letters;
**Clip** and **Gain** are then how you tidy that up.

### Why dark boxes vanish with a luma key

A luma key cannot see colour, only brightness. To it, a dark navy box is "nearly black", so it is
nearly see-through, and the camera shows through it. Three ways round it:

- use lower thirds with a bright or mid-tone plate;
- give the words a strong shadow or outline instead of a box (but a black shadow also vanishes; it only helps over a bright camera picture);
- switch the screen to **Over green** and use the chroma key.

## Things to know

- **Green in your graphics.** With the chroma key, anything close to pure green goes see-through.
- **Glows and shadows over green** end in a green fringe, because the soft part is a blend of the
  shadow and the green. The keyer's edge and spill settings help; plain words with no glow key best.
- **Transitions.** On a key feed, a slide transition that puts a colour or an effect across the
  whole picture (fade through black or white, film burn, blur dissolve, the picture wipes)
  plays as a plain dissolve of the same length. A fade through white would otherwise put a
  solid white flash over the camera. The other screens play the transition as it was set.
- **A full-screen picture on a slide** counts as a background and is left out of the key feed. So
  a photo slide shows nothing there. Send photos to the switcher another way (a full feed on
  another input) if the stream needs them.
- **An overlay on a media layer.** An overlay or alert that has been moved to a media layer (its
  Layer setting) is left out, because the key feed hides those layers.
- **Masks, keystone, colour correction and edge blending** still apply to a key feed screen as
  they are. They make no sense on one: a mask paints black, which a chroma key shows as a black
  patch, and colour correction would shift the green. Leave them off for this screen.
- **Test pictures** (the alignment grid, the sync test) show as they are, not keyed.
- **NDI and DeckLink.** The setting also works on a screen sent by NDI or DeckLink (it uses the
  same drawing code), but those can carry real see-through pixels, which is better than a key
  colour. Use the key feed for HDMI.
- **A stage screen** cannot be a key feed.
- **The HDMI signal.** A PC can send "limited range" video, where black is slightly lifted. If
  the black does not key out fully, raise **Clip** a little, or set the graphics driver to full range.

## How it works in the code

The full explanation is section 62 of [HOW-IT-WORKS.md](../HOW-IT-WORKS.md). In short:

- `Core/Outputs/KeyFeed.cs` holds the rules as small pure functions: which layers are kept, the
  fill colour, what counts as a background, and which transitions become a dissolve.
- `Core/Outputs/OutputConfiguration.KeyFeed.cs` holds the per-screen setting and applies the rules to the program.
- `Core/Outputs/ScreenSetup.cs` saves it as `keyFeed` beside the screen's look, in this PC's `settings.json`.
- `Windows/Outputs/OutputManager.cs` sets the colour behind each output and swaps the transitions.
- Tests: `tests/MxU.Slides.Core.Tests/Outputs/KeyFeedTests.cs`, and the self-test section `KeyFeed` (pictures `70-keyfeed-…`).
