# MxU Slides for Windows

A Windows version of MxU Slides: lyrics, slides, media backgrounds, a stage display, timers and messages for church services. It reads and writes the same document format as the Mac app (generated from `packages/schema/documents.json`), and it ports the Mac app's show logic from Swift to C# so it behaves the same way.

It's written in C# on .NET 10 with WPF.

## Run it

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) (already installed on this laptop) and Node 24 (only if you change the schema).

```powershell
cd apps\windows
dotnet run --project src\MxU.Slides.Windows
```

First launch creates your library at `%APPDATA%\MxU Slides\Library` with:

- four starter themes;
- a welcome deck;
- *Amazing Grace* (public domain);
- four gradient backgrounds;
- a "Getting Started" service.

## Install it like a normal app

```powershell
pwsh apps\windows\scripts\publish.ps1    # builds artifacts\MxU Slides\MxUSlides.exe (self-contained, no .NET needed)
pwsh apps\windows\scripts\install.ps1    # copies it to %LOCALAPPDATA%\Programs and adds Start menu + desktop shortcuts
```

Re-run both after pulling changes to update. Your library is stored separately and is never touched by installing.

## Test it

```powershell
dotnet test --solution apps\windows\MxUSlides.slnx      # 1,900+ unit tests: lyrics, animation, timers, the API, both importers
pwsh apps\windows\scripts\self-test.ps1                 # drives the real app through a service, saves pictures, checks the output
pwsh apps\windows\scripts\display-demo.ps1              # shows the output on your second screen (projector, TV) and photographs it
```

The self-test uses a throwaway library. Its report and a picture of every step are written to `%TEMP%\MxU Slides self-test`. Run it on any new computer before a Sunday.

The display demo also uses a throwaway library. With a projector or TV connected as an extended screen (Windows key + P, Extend), it puts the audience output there and steps through a song, a background and an alert. It saves what the screen showed to `%TEMP%\MxU Slides display demo`. It only takes a picture while MxU Slides covers that screen, so nothing else of yours is captured.

GitHub Actions (`.github/workflows/windows.yml`) runs all of this on every push, including the daily merge from MxU's repository.

## Using it

| To… | Do this |
|---|---|
| Put a slide on screen | Click it (Show mode) |
| Next / previous slide | → Space Enter / ← (carries on into the next service item). A slide with builds takes a press per build first, as on the Mac; **Alt+→** skips them |
| Clear | F1 everything · F2 slide · F3 media · F4 overlays · F6 message |
| Add a song | **New Song**, paste or type lyrics with section names (Verse 1, Chorus…) |
| Import | **Import** (Ctrl+I): PowerPoint `.pptx`, ProPresenter 7 (`.pro`, `.probundle`, `.proPlaylist`, `.protheme`), SongSelect `.txt`, ChordPro (`.cho`, `.chopro`, `.pro`) or plain text. Imports go into a "PowerPoint Import" or "ProPresenter Import" folder |
| Move from ProPresenter | **Screens & Settings › Import ProPresenter library…**: presentations, media, themes, props, messages, stage layouts and playlists. Run it again to pick up changes; your edits here are kept |
| Change a song's order | **Edit lyrics** › *Play order*, e.g. `V1, C, V2, C, B, C x2` |
| Jump to a section of the live song | Group keys like ProPresenter: **A S D F** = Verse 1–4, **C** = Chorus (**X** Chorus 2), **B** Bridge, **P** Pre-Chorus, **T** Tag, **I** Intro, **E** Ending, **O** Outro. Press again for the next repeat |
| Jump to slide *n* | Type the number, press Enter (Esc cancels) |
| Edit a slide's words | **Edit** mode (Ctrl+E), click the slide, type below the canvas |
| Design a slide | **Edit** mode: drag objects on the canvas (snaps to the centre, edges and other objects; hold Alt to stop snapping), drag handles to resize, arrows nudge (Shift ×10), Ctrl+D duplicates, Delete removes. **Add** text, boxes, circles and media; the inspector sets font, size, colour, alignment, spacing, outline, shadow, fill and corners. **Reset to theme** undoes overrides |
| Lower thirds and tickers | Bottom bin › **Overlays**: pick a pack, click an overlay to put it up or take it down (F4 clears all). They animate in and out. Type the name, title or verse into its fields; it updates live |
| Backgrounds | Click media in the bin to show it behind the slides; right-click for "use as background for the open song" |
| Message to the stage/room | Type in **Message**, tick Stage/Audience, press Enter |
| Timers | ▶/❚❚ and ↺ in **Timers**; double-click a name to set `Sermon, 35` or `Service starts, 10:30` |
| Phone, tablet or Companion remote | **Screens & Settings › Network & API**: turn it on, press **Copy default key**, then connect to the address shown (e.g. `http://192.168.1.20:6980`). **Open API docs** lists every command. Allow MxU Slides through the Windows firewall on Private networks when asked |
| MIDI control | **Screens & Settings** › MIDI: turn it on, pick a channel. The note map matches ProPresenter's (Next Slide = C-1 / note 12, Clear All = C-2 / note 0, Trigger Slide = G-1 with velocity = slide number…) |
| Choose screens | **Screens & Settings**: pick the projector for Audience and the confidence monitor for Stage. **More screens, NDI, DeckLink, edge blending…** sets up any number of screens, projector blends, LED walls and NDI or DeckLink outputs |
| Music | Bottom bin › **Music**: **+ Import music**, click a song or playlist to play it; F5 stops it |
| Cameras and NDI sources | **Media › Video inputs…**: name an input and pick a camera, NDI source or DeckLink input. Its tile in the Media tab puts it under the slides |
| Action combos | Bottom bin › **Combos**: click one to run its actions (ProPresenter macros import as combos) |
| Slides that change by themselves | Open a song › **Timeline** › **Record**, click each slide as the song plays, **Finish**. ▶ plays it back. **⋯** adds the song's music or follows timecode |
| Timecode | **Screens & Settings › Timecode in**: LTC from a sound input or MTC from MIDI |
| A different look on each screen | **Screens & Settings › More screens…**, pick a screen, then **Look**: e.g. the stream shows the lyric as a lower third while the projector shows it full screen |
| Design overlays | Bottom bin › **Overlays › Edit…**: make lower thirds and plates, their fields, masks and animations |
| Black out part of a screen | **Screens & Settings › More screens…** › **Masks…**: draw over a pillar or a speaker stack |
| Action combos and slide actions | **Combos › Edit combos…**; right-click a slide › **Actions** |
| Things that happen by themselves | **Combos › Scheduler…**: on chosen days at a time, once on a date, or when a timer reaches a value |
| Edit a song's timeline | **Timeline › ⋯ › Edit timeline…**: drag cues, trim and fade the music, add stems |
| MIDI devices | **Screens & Settings › MIDI › MIDI devices**: name the devices the show talks to |
| Record or stream | **Stream & Record** in the toolbar: pick a preset and press **Start recording** or **Go live**. Add YouTube, Facebook or any RTMP server with its stream key. A preset can record or stream one screen (the stage, a lower-thirds screen), stream a video ("replay"), and take its sound from the Music tab or a sound input. Recordings appear in the **Media** tab; **Also copy to** keeps a copy in a folder such as Videos\MxU Slides |

**On a laptop with one screen**, click **Audience** or **Stage** in the toolbar to open that output as a normal window. It shows exactly what the projector would. When you plug in a projector, choose it under **Screens & Settings**. The output then goes full screen there, stays on top, hides the mouse and never steals the keyboard.

### Built to keep running during a service

- Every edit is saved within about half a second.
- Each save is atomic and keeps the previous version as `.bak`.
- A damaged file is restored from its `.bak` and the bad copy is set aside in `Quarantine`.
- Deleting moves things to `Trash`; nothing is ever permanently removed.
- The library is backed up once a day into `Backups` (last 14 days are kept).
- If one slide item fails to draw, it's skipped and logged rather than blanking the screen.
- If the app closes unexpectedly, it reopens your service and offers to put the last slide back up.
- Sleep and the screensaver are blocked while an output is showing.
- If a projector is unplugged mid-service, its output is hidden. When the projector comes back, the output returns to it.
- Logs are in `%LOCALAPPDATA%\MxU Slides\Logs`.

## What's here, and what's next

Phase 1 covers:

- the library, songs and decks;
- lyric import with SongSelect, ChordPro and chords;
- arrangements and services that run straight through;
- themes with designs (full slide, lower third, side third);
- live output with dissolve or fade transitions;
- image and video backgrounds;
- the stage display with the Mac's five layouts;
- timers, clock and messages;
- multi-display output and the single-screen preview.

Phase 2 adds:

- slide, theme and overlay animations and builds (Next plays them, as on the Mac);
- drag-to-edit objects on the canvas, with snapping and an inspector;
- overlays (lower thirds, scripture, tickers) with masks;
- PowerPoint and ProPresenter 7 import, including a whole ProPresenter library;
- the Local API for phone, tablet and Companion remotes;
- MIDI control and ProPresenter-style group hot keys.

Phase 3 adds:

- any number of screens: mirrors, grouped projectors, edge blending, LED walls, keystone and colour correction;
- NDI and DeckLink outputs, and cameras, NDI and DeckLink as live inputs;
- music playlists, and multitrack stems on timelines;
- recording to MP4 and streaming over RTMP/RTMPS or HLS;
- song timelines (record a take, play it back), timecode in (LTC/MTC);
- slide actions, action combos, the scheduler and MIDI out;
- GPU effects (colour, hue, pixelate, vignette, warp, glitch, burn, tint…).

Phase 4 adds:

- looks: each screen can restyle the live slide through its own theme;
- long lyrics that turn pages, transitions per slide and per layer, echo and ghost trails;
- editors for action combos, slide actions, the scheduler, overlays, output masks and song timelines (with stems);
- the rest of a ProPresenter workspace: screens, looks, masks, timers, video inputs, MIDI devices and group hot keys;
- show timing that never waits for spare threads, and recording on PCs whose screen capture doesn't work.

Phase 5 (Sunday essentials) adds:

- slides that advance by themselves (timers, walk-in loops, "next when the video ends"), music rows that play, live video controls, Clear options and service tracking timers;
- hidden service rows, a look per row, duplicating services, and 9am/11am service versions;
- Mac and ProPresenter documents drawn faithfully: per-slide themes, video trims and speed, the library's fonts, tickers and text on a path, cameras inside slides, keystone, a "text doesn't fit" warning;
- music and video sound on the speakers you choose (with delay), and a music manager for playlists;
- saved alerts, timer folders and warnings, a look switcher, each screen Live or Off, a stage layout per stage screen, and pop-out previews;
- `.mxuslides` files to move presentations to and from the Mac, missing-media badges with Locate and Replace Everywhere, and checks that media will play when it's imported;
- recordings, streams and NDI bigger than the screen keep their full picture.

Phase 6 (motion and production) goes beyond the Mac, and everything it makes still opens on a Mac:

- 15 bundled fonts, a gallery of 27 animated lower thirds, four worship lyric looks with lines that build in, countdowns, and animation presets with moving previews;
- motion backgrounds made in the app: eleven styles of seamlessly looping video (aurora, light rays, bokeh, smoke…), coloured your way, rendered to MP4;
- eighteen new transitions drawn by the graphics card (push, zooms, iris, wipes, cube, flip, light leak, glitch, ripple…);
- a Bible window (KJV, WEB and ASV): type a reference, press Enter, step verse by verse;
- Planning Center Services plans imported as services, a browser remote for any phone, and a stage view for any screen with a browser;
- ATEM, OBS, vMix and DMX lighting (Art-Net, sACN) steps on slides, combos, schedules and timelines.

Next: Phase 7, slide editor parity. After that, the rest of "Not ported yet" in [docs/PORTING.md](docs/PORTING.md).

See [docs/HOW-IT-WORKS.md](docs/HOW-IT-WORKS.md) to learn the code, and [docs/PORTING.md](docs/PORTING.md) for which Swift file each C# file comes from.

## Licence

MxU Slides is source-available under the PolyForm Shield License 1.0.0. See [LICENSE.md](../../LICENSE.md). Required Notice: Copyright MxU (https://getmxu.com)
