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
dotnet test --solution apps\windows\MxUSlides.slnx      # 200+ unit tests: lyrics, timers, stage text, songs
pwsh apps\windows\scripts\self-test.ps1                 # drives the real app through a service, saves pictures, checks the output
```

The self-test uses a throwaway library. Its report and a picture of every step are written to `%TEMP%\MxU Slides self-test`. Run it on any new computer before a Sunday.

GitHub Actions (`.github/workflows/windows.yml`) runs all of this on every push, including the daily merge from MxU's repository.

## Using it

| To… | Do this |
|---|---|
| Put a slide on screen | Click it (Show mode) |
| Next / previous slide | → Space Enter / ← (carries on into the next service item) |
| Clear | F1 everything · F2 slide · F3 media · F4 overlays · F6 message |
| Add a song | **New Song**, paste or type lyrics with section names (Verse 1, Chorus…) |
| Import lyric files | **Import Lyrics**: SongSelect `.txt`, ChordPro (`.cho`, `.chopro`, `.pro`) or plain text |
| Change a song's order | **Edit lyrics** › *Play order*, e.g. `V1, C, V2, C, B, C x2` |
| Edit a slide's words | **Edit** mode (Ctrl+E), click the slide, type below the grid |
| Backgrounds | Click media in the bin to show it behind the slides; right-click for "use as background for the open song" |
| Message to the stage/room | Type in **Message**, tick Stage/Audience, press Enter |
| Timers | ▶/❚❚ and ↺ in **Timers**; double-click a name to set `Sermon, 35` or `Service starts, 10:30` |
| Choose screens | **Screens & Settings**: pick the projector for Audience and the confidence monitor for Stage |

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

Phase 1 (this) covers:

- the library, songs and decks;
- lyric import with SongSelect, ChordPro and chords;
- arrangements and services that run straight through;
- themes with designs (full slide, lower third, side third);
- live output with dissolve or fade transitions;
- image and video backgrounds;
- the stage display with the Mac's five layouts;
- timers, clock and messages;
- multi-display output and the single-screen preview.

Still to come:

- **Phase 2:** slide animations and builds, drag-to-edit objects on the canvas, overlays and props, ProPresenter and PowerPoint import, the Local API and remote, MIDI.
- **Phase 3:** NDI, DeckLink, recording and streaming, multitrack audio.

See [docs/HOW-IT-WORKS.md](docs/HOW-IT-WORKS.md) to learn the code, and [docs/PORTING.md](docs/PORTING.md) for which Swift file each C# file comes from.

## Licence

MxU Slides is source-available under the PolyForm Shield License 1.0.0. See [LICENSE.md](../../LICENSE.md). Required Notice: Copyright MxU (https://getmxu.com)
