# Phase 6: looks, lower thirds and kinetic text

This part of Phase 6 is about how things *look* on a Sunday: fonts worth using, lower thirds
that a broadcast team would be happy to put on the stream, worship lyric looks, countdowns
before the service, and words that arrive with some life (word by word, line by line, out of a
blur). Almost everything here is *content*: themes, overlays and animation steps, built from
types the shared document format already has. The code mostly builds that content, and the
windows around it let you browse it, fill it in and fire it.

One rule shaped every decision, so it comes first.

## The rule: a Mac must still open everything

The library is shared with the Mac app (through `.mxuslides` files today, folder sync later).
The Mac's decoder is strict: a document that holds one enum value it doesn't know (a new
`StepAnimation`, a new `EffectKind`) is rejected *whole*. So nothing in this work adds a value
or a field to the shared format. Instead:

- **New looks are built from old parts.** A word that rises in is an ordinary *Move* step with a
  *fade*, aimed at one word with the `ranges` field the Mac already has (its own "One Step per
  Word"). A glass panel is an ordinary gradient fill with an ordinary *Grain* effect. A countdown is
  an ordinary text box whose `textLink` points at a timer.
- **Windows-only extras live beside the document,** in `LibraryStore.Extras`
  (`Core/Library/WindowsExtras.cs`), never in it. The one used here is the kinetic build on the
  worship looks' lyrics (below). Each extra is saved against a *fingerprint* of the shared part it
  decorates, so if someone later changes that part on a Mac, the fingerprint no longer matches and
  Windows quietly drops its extra: the Mac's change wins.

## 1. Bundled fonts

The app ships 15 font files under the SIL Open Font License 1.1: Poppins (7 weights), Montserrat
(5), Bebas Neue, and DM Serif Display (regular and italic). They're in `apps/windows/assets/fonts`
with `SOURCES.txt` (where each came from), `OFL.txt` (the licence) and `FONT-NOTICES.txt`.

**Shipping them.** The app's project file copies them into a `Fonts` folder beside
`MxUSlides.exe` (`publish.ps1` and `install.ps1` carry that folder along).

**Every library gets them.** At launch, `BundledFonts.AddTo` (`Core/Library/BundledFonts.cs`)
copies each one into the library's `fonts` folder, named by its SHA-256 like every other font
there, unless it's already there. Why copy them at all, rather than load them from the app's
folder? Because that folder is what the rest of the app already reads:

- `Rendering/FontMap.ActivateStored` loads the library's fonts for drawing *privately* (they're
  never installed into Windows);
- a `.mxuslides` export carries the fonts its slides name from that folder, so a Mac that opens a
  deck made with the new looks gets the same fonts and draws it the same.

**The licence notice.** The OFL lets software bundle fonts as long as each font's copyright notice
and the licence go along, where people can read them. `BundledFonts.Credits` reads each family's
copyright string out of the font file's own *name table* (so it can't drift from the files), and
`BundledFonts.Notice` puts those and the OFL text together: that is `FONT-NOTICES.txt`, and a unit
test checks the shipped file still matches the fonts. In the app: **Settings › About** lists the four families,
each drawn in its own font, with its copyright; **Font licences…** shows the full notice.

**The right weight.** A font name like "Montserrat Black" has to land on the Black *file*, not on
Montserrat Regular made bold by Windows (a "simulated" bold, thicker and blurrier). `FontMap` now
knows each library face by its full name ("Montserrat Black", "Poppins SemiBold") as well as its
PostScript name ("Montserrat-Black"); before, "Montserrat Black" fell all the way back to Segoe
UI. The self-test resolves every face both ways and checks the weight, the style and that no
simulation is applied (picture `60-looks-fonts`).

## 2. Animation presets

### The Mac's catalogs

`Core/Animation/AnimationPresets.cs` ports the Mac's `AnimationPresetCatalog.swift` (Settle,
Float, Slide, Bar Wipe, Line Draw, Pill Grow, Bracket Pop, Blur Resolve, Burn, Glitch, Type,
Roll, Crawl) and `EffectPresetCatalog.swift` (Frost, Static, Heat Haze, Long Trails…), with
`AnimationPresetApplication`, which puts a preset on objects exactly as the Mac does: replace
their steps or add after them, and each object after the first starts a beat later (a
*stagger*). Presets you save go on the `AnimationPresetBoard` / `EffectPresetBoard` documents
(`Core/Library/PresetBoards.cs`) with the Mac's ids, so once libraries are shared, a preset saved
on either computer shows on both.

### Kinetic text (Windows' own presets)

`Core/Animation/KineticText.cs` adds presets that cut a text into pieces: *word by word: rise*,
*line by line: rise*, *soft blur in*, *typewriter*, *scale pop*, *wipe reveal*, *glitch in*,
*burn in*, *light sweep*, *wave*, *cinematic focus* and more (17 in all). A preset is a small
recipe (`KineticPreset`): the In steps each piece plays, how to cut the text (whole, lines or
words) and the *stagger* between pieces.

`KineticText.Expand` turns a recipe into steps. For "Pastor Ade Johnson", word by word:

```
Move+fade  on "Pastor"   (starts when the overlay goes up)
Move+fade  on "Ade"      (With Previous, 0.08 s later)
Move+fade  on "Johnson"  (With Previous, 0.08 s later)
```

Each is an ordinary step with a `ranges` entry naming its word, which is why a Mac plays it
the same. One subtlety: "With Previous" counts from the step just before in the play order. If a
piece has two steps (a fade, then a colour sweep), the next piece's lead would count from the
sweep, not from the fade; `Expand` subtracts that offset so the pieces stay exactly a stagger
apart.

Another: **each piece gets one In.** Two In steps on the same words would play together, but
the Mac's editor flags the second one "already shown". So where a look wants more (a pop, then a
settle), the second step is an *Emphasis* (Pulse, Color), which is allowed after an In.

**Words typed later.** A range names characters (line 0, column 7, length 3). If the operator
types a new name into a lower third's field, the old ranges point at the wrong letters: the new
name would appear half at once. `KineticText.Reflow` notices a word-by-word build that no longer
matches its words and makes it again over the new ones (same steps, same stagger, old ids kept
where they can be). `MainViewModel.OverlayChanged` runs it after every edit, so a new name still
rises in word by word. (The Mac re-anchors ranges as you type in its editor; this is the Windows
equivalent for fields.)

### One step per line / per word

The Mac's **Generate › One Step per Line / per Word** gives each line or word of a text its own
step (`AnimationRanges.Generate`, ported from `TextStyleRuns.swift` and
`SlideEditorModel.generateAnimationSteps`). The Mac splits the *selected* text; Windows' overlay
editor has no text selection yet, so it splits the whole text. The Mac makes each piece wait for a
click; an overlay never gets a click, so Windows chains them (each starts a moment after the one
before) and files them into "when it goes up" (`OverlaySteps.File`).

### In the overlay editor

The Animation tab (`ViewModels/OverlayEditorViewModel.Presets.cs`) now has:

- **a presets gallery** (`Views/AnimationPresetGallery.cs`): three shelves (*Kinetic text*, the
  Mac's *Recommended* set, and *Mine*), each a grid of tiles. Point at a tile and it plays its
  preset on a small sample lower third (`Core/Animation/PresetSamples.cs`), filed exactly as it
  would be on your overlay, so what the tile shows is what you'll get. Click to pick it; **Apply**
  replaces the selected objects' steps, **Add** keeps theirs; double-click applies straight away.
  **Save as preset…** saves the selected object's steps to *Mine* (the shared board);
- **One step per Line / Word**;
- **Look effects**: the Mac's effect presets (frost, static, heat haze, trails) on the selected
  objects.

The Mac shows its presets as a menu; the tiles with moving previews are Windows' own.

## 3. Worship lyric looks

Four new starter themes, built in `Core/Library/StarterPacks.Worship.cs`:

| Look | Feel | Fonts | Motion |
| --- | --- | --- | --- |
| **Arena** | big, bold, capitals, amber accent | Montserrat Black, Bebas Neue | lines rise in with weight |
| **Broadcast** | built for the stream's lower third | Poppins | a clean rise |
| **Modern Serif** | refined, editorial, gold | DM Serif Display, Montserrat | a slow focus pull |
| **Minimal Sans** | quiet, left-aligned | Poppins | a gentle rise |

Each has the same designs the Mac's four starter themes have (Lyrics, Lyrics Lower Third and
Side Third, Verse + Reference with its Lower and Side Thirds, Name + Title, Point and Points,
Quote, Section Title, Call to Action and their thirds), plus a **Countdown** design, and the same
eleven starter overlays. A unit test runs the Mac's `starterPacksAreConsistentDocuments` over all
eight packs.

**How they reach a library.** The starter-pack system already restores a missing starter theme
at launch (`LibrarySeeder.RefreshStarterPacks`) and only *refreshes* existing ones when the
starter *revision* number goes up. A brand-new pack is simply missing everywhere, so every library,
new or old, gains the four looks the first time it opens, without the revision changing, and
nothing anyone edited in the existing themes is touched.

**Kinetic lyrics.** The shared theme gives the lyrics a whole-text rise, which the Mac plays as
it is. On Windows, the seeder also saves a *kinetic build* for each lyric placeholder as a Windows
extra (`Core/Library/KineticExtras.cs`): "lines rise in one after another, 0.2 s apart".
`PlaceholderAnimations.Compose`, which copies a theme's steps onto each slide's own words, asks
`KineticText.BuildFor` for that build and, when there is one, plays it over the slide's words
(`KineticText.Donate`) in place of the whole-text rise. The build is saved against the
placeholder's steps' fingerprint: change the lyrics' animation on a Mac and Windows follows the
Mac's choice instead.

A design is made for a slide of a few lines. If someone pastes a whole hymn verse onto one slide,
sixteen lines a fifth of a second apart would take over four seconds to arrive, while the room
is already singing. So `KineticText.StaggerFor` squeezes the pieces after the fourth closer
together: a build never spreads wider than its first four pieces would (or 0.8 s).

**Long lyrics still fit.** Text in the looks shrinks to fit its box. Where it can turn the page
(the lower and side thirds), it stops at three quarters of its size and pages instead of turning
tiny. The full-screen Lyrics can't page on the projector, so it shrinks as far as it must, as the
Mac's starter themes do.

**Balanced lines.** The looks turn on a text style's *balanced wrap*. `Rendering/TextLayout.cs`
now honours it as the Mac's `TextRasterizer` does: each paragraph wraps at the narrowest width
that keeps its number of lines, so a lyric never leaves one word alone on its last line.

## 4. Countdowns

- Every worship look has a **Countdown** design: "Service starts in", big Bebas Neue digits (its
  figures are all the same width, so a ticking clock never shifts sideways) linked to a timer,
  and a welcome line.
- A **Pre-Service Countdown** deck (`StarterContent.CountdownPresentation`) has one slide per look.
  It is added once per library (`windows-starters.txt` remembers it, so deleting it sticks).
- The lower-thirds gallery has countdown overlays too (below): a glass corner card, a full-screen
  serif one, and "Starting soon" designs with the minutes left.

The digits are an ordinary `TextLink` with a timer source: start a countdown in the Timers panel
and they count down on every screen.

## 5. The lower-thirds gallery

`Core/Library/LowerThirdGallery.cs` holds 27 designs in four looks:

- **Clean**: a quiet dark plate, a thin accent, Poppins;
- **Broadcast**: solid plates, a coloured accent bar, Montserrat in capitals;
- **Glass**: see-through frosted panels with a bright top edge;
- **Editorial**: words straight on the picture over a soft shadow, DM Serif Display and gold rules.

They cover a speaker's name and title, scripture, a sermon point, song credits with the CCLI
line (title, writers, CCLI song number, licence number), social handles, giving, announcements and
events, prayer, a baptism or celebration, a welcome, LIVE bugs and starting soon / countdowns.

Each design is an ordinary `Overlay`: shapes, text boxes, masks and steps, nothing new. They're
written with a small *composer* that adds objects bottom to top, each with its In and Out steps,
and works out the play order: plates first, then the words (rising out of a mask, or coming into
focus), the small print a beat after; on the way out the words go first and the plates follow.
Some headlines build word by word (kinetic text, above). The motion uses the whole range of step
kinds, sparingly: wipes with a soft edge, scale, blur and fade, *Draw* (the social icons' rings and
the celebration card's edge draw themselves round), *Type* (a handle typed out), *Glitch* (the
LIVE clock's time flickers on), *Burn* (the welcome title), *Pulse* (the LIVE dot) and *Color* (a
warm light passing over a baptism name), with masks for words that rise out of an edge.
Everything sits inside the EBU graphics-safe area on the 1920 × 1080 canvas that every screen
size scales from, so at 4K the same design is drawn at the same place, twice as sharp (picture
`64-gallery-live-4k-crop`).

**The gallery window** (`Views/LowerThirdGalleryWindow.cs`, state in
`ViewModels/LowerThirdGalleryViewModel.cs`) opens from **Gallery…** in the Overlays bin:

- tiles of every design, filtered by what it's for and by look; point at one and it plays;
- the picked design playing on a loop, over a stand-in camera shot (or a worship background, for
  full-screen designs), or over what's on the program right now;
- its **fields** to fill in. Until you add it, the design is a *draft*: typing changes only the
  preview;
- **Add to library** saves a copy into a pack ("Lower Thirds" unless you pick or make another),
  filed under its category, so it appears in the Overlays bin. From then on the fields edit *that*
  overlay, the same one the bin and the program use, so a typo fixed while it's up is fixed on
  screen;
- **Go live / Take down**;
- for song credits, **Fill from the song** takes the title, writers and CCLI number from the song
  that's up or open (the church's licence number isn't in a song, so it stays as typed);
- for countdowns, a timer picker and **Start a 5-minute countdown**.

The stand-in pictures are drawn in code (`Views/StandInPictures.cs`), so the app needs no image
files for them; the self-test uses the same ones.

A small fix came with it: when a new pack appeared, the Overlays bin's pack picker lost its
place and jumped to the theme's pack (`MainViewModel.RefreshOverlays` now keeps the selection
while it rebuilds the list).

## How it's checked

**Unit tests** (in `tests/MxU.Slides.Core.Tests`):

- `Library/BundledFontsTests.cs`: the 15 fonts ship; a library gets each once, under its hash;
  a deleted one comes back; each face is the weight and style its name says; the credits come
  from the name tables; the shipped notice matches.
- `Animation/AnimationPresetTests.cs`: the Mac's `recommendedCatalogIsWellFormed` and
  `applyReplacesAddAppendsAndFollowersStagger`, plus the effect catalog.
- `Library/PresetBoardsTests.cs`: the Mac's `animationPresetBoardIsASingletonDocumentKind` and
  `defaultInOutSurviveReopenAndMerge`, effect presets, and a kinetic extra ignored once its theme
  changes elsewhere.
- `Animation/AnimationRangesTests.cs`: the Mac's word splitting, characters counted as the Mac
  counts them, and generating one step per piece.
- `Animation/KineticTextTests.cs`: every kinetic preset is built from shared kinds and is
  warning-free on an overlay; the stagger; reflowing after new words are typed; donating a build
  to a slide; a long slide's lines squeezing closer.
- `Animation/PresetSamplesTests.cs`: every preset plays on the gallery's sample without waiting
  for a click, and leaves the catalog alone.
- `Library/StarterPackTests.cs`: the Mac's consistency check over all eight packs; the worship
  looks use only the bundled fonts, have a timer-linked countdown and ask for kinetic lyrics; a
  long lyric still fits the projector while thirds page; an existing library gains the looks and
  keeps its edits.
- `Library/LowerThirdGalleryTests.cs`: every category and look is covered; every design is a
  well-formed overlay inside the safe area, made only of the shared format and the bundled fonts;
  adding, song credits, countdown timers, a new name typed word by word.

**The self-test** (`Infrastructure/SelfTest.Looks.cs`, pictures 60 to 65) resolves every font,
shows the credits, draws every worship-look design over a stand-in camera shot or worship
background, plays the kinetic lyrics part-way in, ticks the countdown deck, draws every gallery
design part-way in, settled and on its way out, then drives the gallery window (pick, fill in, add,
go live, fix the name while it's up, take down, song credits, a countdown) and the overlay editor's
presets (kinetic, the Mac's, one step per word, saving a preset, look effects).

Pictures that show the designs best: `61-look-Arena-Lyrics`, `61-look-ModernSerif-Lyrics`,
`61-look-Arena-SectionTitle`, `61-look-Arena-Countdown`, `61-look-Broadcast-LyricsLowerThird`,
`61-look-Arena-kinetic-0.50s`, `63-gallery-Speaker-Broadcast-Speaker`,
`63-gallery-Speaker-Editorial-Speaker`, `63-gallery-Scripture-Editorial-Scripture`,
`63-gallery-Announcement-Glass-Announcement`, `63-gallery-Welcome-Editorial-WelcomeTitle`,
`63-gallery-Countdown-Editorial-Countdown`, `64-gallery-window-speaker`, `64-gallery-live-program`
and `65-overlay-editor-presets`.

## Things to try

1. Open **Settings**, scroll to **About**: each font's name is drawn in that font.
2. In the Overlays bin, press **Gallery…**. Point at a few tiles. Pick *Speaker · Broadcast*,
   type your own name, **Add and go live**, then change a letter while it's up.
3. Pick *Song Credits · Clean*, open *Amazing Grace* in the library and press **Fill from the
   song**.
4. Open the Overlay editor on the speaker you added, Animation tab: point at the kinetic tiles,
   pick *Word by word: blur in*, select the name and **Apply**, then press ▶.
5. Open *Amazing Grace* with the **Arena** look and put a verse up: the lines rise in one after
   another. Do the same with **Modern Serif**.
6. Start a countdown in the Timers panel and put up a slide of the **Pre-Service Countdown** deck.

## For PORTING.md

**Ported line by line** (Swift → C#):

- `Library/AnimationPresetCatalog.swift`, `EffectPresetCatalog.swift` → `Core/Animation/AnimationPresets.cs` (with `AnimationPresetApplication`). Tests: `AnimationPresetTests.cs` (`recommendedCatalogIsWellFormed`, `applyReplacesAddAppendsAndFollowersStagger`).
- `AnimationRanges` (in `TextStyleRuns.swift`) and `SlideEditorModel.generateAnimationSteps` → `Core/Animation/AnimationRanges.cs`. Tests: `AnimationRangesTests.cs` (`buildRangesByWordGeneratorSplitsInsideTheSelection`).
- The preset boards in `AppModel` (`saveAnimationPreset`, `overwriteAnimationPreset`, `deleteAnimationPreset`, and the effect ones) → `Core/Library/PresetBoards.cs`. Tests: `PresetBoardsTests.cs` (`animationPresetBoardIsASingletonDocumentKind`, `defaultInOutSurviveReopenAndMerge`).
- `TextRasterizer`'s balanced wrap → `Rendering/TextLayout.cs`.

**Windows-only:**

- `Core/Library/BundledFonts.cs`, `assets/fonts/` (15 OFL fonts, `OFL.txt`, `FONT-NOTICES.txt`, `SOURCES.txt`), `Views/AboutSection.cs` (Settings › About and Font licences). `FontMap` also resolves a library face by its full name.
- `Core/Animation/KineticText.cs` (`KineticPresets`, `Expand`, `Reflow`, `Donate`), `Core/Library/KineticExtras.cs` (kinetic builds on theme placeholders, a Windows extra with a fingerprint basis), `Core/Animation/OverlaySteps.cs`, `Core/Animation/PresetSamples.cs`.
- `Core/Library/StarterPacks.Worship.cs` (the Arena, Broadcast, Modern Serif and Minimal Sans looks), `StarterContent.CountdownPresentation` and `LibrarySeeder.AddWindowsStarters` (`windows-starters.txt`).
- `Core/Library/LowerThirdGallery.cs` (27 designs), `ViewModels/LowerThirdGalleryViewModel.cs`, `Views/LowerThirdGalleryWindow.cs`, `Views/StandInPictures.cs`, `ViewModels/MainViewModel.Looks.cs`.
- `ViewModels/OverlayEditorViewModel.Presets.cs` and `Views/AnimationPresetGallery.cs` (the presets gallery with moving previews; the Mac has a menu).
- `Infrastructure/SelfTest.Looks.cs`.

**Not ported yet:** in the overlay editor's line, replace "*animation presets*, and *one step per
line/word* for text aren't there" with: "*One step per line/word* splits the whole text (Windows'
editor has no text selection) and chains the pieces instead of waiting for clicks." The *animation
timeline* and drag-and-drop reordering are still not there. The slide editor has no presets
gallery yet (Phase 7).
