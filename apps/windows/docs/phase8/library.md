# Phase 8: the library in depth

Phase 8 turns the library from a list of songs into something a church can live in for years:
folders, sorting, a search across everything, favourites and tags, actions on many items at
once, exports, backups you can restore, files that open from Explorer, a welcome guide for a new
PC, and a Diagnostics window for the day something goes wrong. As in earlier phases, the rules
sit in Core (`src/MxU.Slides.Core`), ported from the Mac's Swift where the Mac has them, and are
unit-tested; the windows and menus around them are WPF.

A rule that runs through all of it: **nothing new is written into a shared document.** Songs,
media and services are files a Mac reads too, so a Windows-only fact (a song's tags, a backup's
schedule) goes in Windows extras or `settings.json`, never as a new field or enum value.

## 1. Browsing: folders, sorting, search, favourites and tags

**Folders.** A folder exists while something is filed in it (`Presentation.Folder`, a path like
`Worship/Lyrics`), or while it's newly made and still empty: those are kept per PC in
`settings.json` (`LibraryEmptyFolders`), as the Mac keeps "pending folders". The path maths is
`Core/Library/FolderPaths.cs`; `LibraryFolders` (in `LibraryBrowse.cs`) answers "which folders
are there", "what's directly inside this one, with how many items" and "how do they nest"
(`Tree`, for menus). Renaming, moving or deleting a folder is re-filing everything under it
(`LibraryFolders.Rename/Move/Delete` return a `FolderMove`: which items to re-file and which empty
folders to keep). Deleting a folder never deletes songs: they move up a level.

A folder's right-click menu has **Move to Folder**, as on the Mac: *Top Level* when the folder
is inside another, then the other folders as a tree. It never offers the folder itself or a
folder inside it, the one it's in now is ticked and greyed, and with nowhere to go the whole
item is greyed (`LibraryFolders.MoveTargets`).

**Sorting** (`LibrarySort`, a port of the Mac's `LibraryViewOptions`): Name, Date Modified,
Date Added, Recently Used. Each list remembers its own sort (`LibrarySorts` in settings).
"Modified" and "added" are the document file's times, read once on the library's background
thread (`LibraryDates`); "recently used" is when something last went on screen, kept in
`usage.json` beside the library (`LibraryUsage`), touched as things go live.

**Search** (`LibrarySearch.cs`) looks through songs (title, words, CCLI number, author),
presentations, media and music (names, file names) and tags, and ranks: an exact CCLI number,
then an exact title, a title starting with what you typed, all the words in the title, the
author, a tag, a file name, the words as a phrase in the lyrics, the words anywhere in the
lyrics; ties go to what was used most recently. A lyric hit shows the line it matched. The
index is built once and thrown away when the library changes.

A **search hit right-clicks like the item in its folder**, with **Show in Library** first (the
Mac's update of November 2026): Show in Library clears the search, opens the song's folder and
selects it; for a picture or video it shows it in the media bin, for music the Music tab.

**Favourites and tags** (`LibraryMarks.cs`). Media and music already have shared `favorite` and
`tags` fields, so those are used (a Mac sees them). Songs and presentations have no such field,
so theirs live in Windows extras (kind `library-marks`). Tags are cleaned (trimmed, at most 40,
the first spelling wins: "easter" then "Easter" is one tag).

## 2. Many at once

Ctrl+click and Shift+click pick several rows (`ListMultiSelect`, a port of the Mac's: Replace,
Toggle, Extend). A right-click on one of the picked rows acts on all of them: *Add 3 to Service*,
*Move 3 to Folder*, *Duplicate 3*, *Apply Theme to 3*, favourites and tags, *Export 3…* and
*Delete 3 Items*. The media bin does the same with its tiles (an outline shows what's picked; Esc
unpicks). The rules are `LibraryBatch.cs`: a duplicate is named "Amazing Grace Copy" as on the
Mac; applying a theme follows the Mac's rules; deleting songs that are in a service first says
which services (`ServicesUsing`), and deleting media that's used lists where.

## 3. Export

Right-click › *Export…* opens a small window of choices before the save dialog
(`Views/ExportWindow.cs`). The formats (`Core/Export/PresentationExport.cs`):

- **PDF**: a sheet of slides (2–4 across), a page per slide, a lyric sheet, or a chord chart.
  The PDF writer is ours (`PdfDocument.cs`, `TrueTypeFont.cs`): it embeds only the letters used
  from each font, falls back to another font for letters a font lacks (Yoruba's ẹ and ṣ), and
  adds the text layer so a PDF reader can search and copy. The lyric sheet puts chords over the
  letters they belong to (`LyricSheet.cs`) and never leaves a heading alone at the foot of a page.
- **Pictures**: one PNG or JPEG per slide, numbered with their labels ("01 Verse 1.jpg").
- **Plain text** (the words only) and **ChordPro**.

Every format is read back in the unit tests (`tests/…/Export/MiniPdf.cs` is a tiny PDF reader
just for that). The Mac has no printing either; it exports PDFs, as here.

## 4. Backups and restores

**A backup is one file** (`.mxubackup`, a zip): every library document, the Windows extras, the
library's fonts, `usage.json`, the settings that make sense on another PC, a manifest, and (if
you choose) the media files. It's written beside its destination and moved into place when
complete (`Core/Library/LibraryBackup.cs`, following the Mac's `BackupBundle` sections and
policies).

**Restoring** first reads the file and compares it with your library (*3 new, 1 different, 40
the same*), so you choose what to bring back. Before anything changes it takes a **safety copy**
of the library ("Before restore 2026-11-04 101200.mxubackup"); if that can't be written, nothing
is restored. Then, per part: *Newer wins* (the default: an item edited here since the backup is
kept), *Keep mine*, *Replace*, or *Make the library exactly the backup* (what's not in the
backup moves to the Trash folder, after a second question). Media files are checked against
their hash; a zip that would climb out of the folder or inflate to absurd sizes is refused
(`SafeZip`). Settings from a backup are staged and applied at the next start, so the running app
never changes under you.

**Nightly backups** follow the Mac's `ScheduledBackup` (ported in November 2026): one is due
once after each 3 AM, so a night the PC was off is made up the next time it runs; the files are
"MxU Slides Library 2026-11-04.mxubackup"; the newest 7 are kept and nothing else in the folder is
touched. The line under the setting says when and where ("Every night at 3 AM to Backups,
keeping the last 7. Last: Wed 03:00."), or why the last one didn't finish. A backup that includes
media waits while something is on the screens (reading gigabytes could make a video stutter).
Everything runs on the library's own background thread.

## 5. Media files as files (the Mac's November 2026 update)

- **An edited file takes its new bytes.** Media files are stored by the hash of their bytes
  (`Blobs/<sha256>.png`). Open one from there in a photo editor and save it, and its name no
  longer matches its bytes. The app watches the Blobs folder; once a changed file has been still
  for 10 seconds, it hashes it, renames it to its new hash, and every item that used it follows,
  keeping its name and settings (`MediaRelink.Refingerprinted`, `Core/Library/BlobEdits.cs`,
  `ViewModels/MainViewModel.BlobEdits.cs`). To notice an edit cheaply, the library remembers each
  file's last-write time in `blob-times.json` (the Mac writes it into the file's extended
  attributes). A file seen for the first time is noted as it is, not hashed, so a big library
  isn't read end to end at the first launch.
- **A video on screen plays a replaced file at once** (`Rendering/MediaHub.ReloadInPlace.cs`):
  Replace everywhere, Locate, a transcode or an edit reopens every player showing it on the new
  file, from the same point, and the sound our engine plays moves with it.
- **Deleting takes the file too**, when no other item uses it. The Mac removes it; here it moves
  to the library's `Trash/Blobs` folder, beside the deleted document, so a mistake can still be
  undone by hand. If the same file is imported again in the moment between, it comes straight back.
- **Music imports ask about duplicates** now, as pictures and videos do
  (`ViewModels/MainViewModel.MusicImport.cs`): the same file, or a different file under a song's
  name, asks *Use Existing* or *Add as New Copy* ("Offertory Pad 2", sharing the one stored file).
  An import nobody is watching asks nothing and reuses only the very same file
  (`MediaImportDedupe.UnattendedUsesExisting`, the Mac's rule for a note's pictures).

## 6. Opening files from Explorer

`scripts/install.ps1` now runs `scripts/file-types.ps1`, which tells Windows, **for this user
only** (`HKEY_CURRENT_USER\Software\Classes`, no administrator), that MxU Slides opens
`.mxuslides` presentations and `.mxubackup` backups, and lists it under *Open with* for
ProPresenter `.pro` and PowerPoint `.pptx` files without taking those over. `scripts/uninstall.ps1`
removes the program, its shortcuts and only the file types that still point at it (a type
another app has taken since is left alone). Both scripts take `-DryRun` to show what they would do.

Double-click a file and Windows starts `MxUSlides.exe "C:\…\song.mxuslides"`. If MxU Slides is
already running, that second copy mustn't open a second window on the same library: it sends the
file names over a **named pipe** to the copy that's running and quits (`Infrastructure/FileHandoff.cs`;
the message format is `Core/Library/OpenRequests.cs`). The pipe only accepts the same Windows
user, takes only full paths, and is capped at 200 files. The running copy comes to the front and
imports the files, or, for a backup, opens the restore window.

## 7. The welcome guide and Help

A new library opens the **welcome guide** (`Views/OnboardingWindow.cs`, steps from the Mac's
`OnboardingFlow`): what MxU Slides is, which screen is the projector and which the stage, a look
for the words, bringing in songs (import, start from scratch, or restore a backup), and where
Help is. It can be run again from Help. The **Help** button has the guide, the keys to
know, Diagnostics and Report a problem.

## 8. Diagnostics

**Diagnostics** (Help › Diagnostics…) has three pages:

- **This PC**: Windows, the graphics cards (through DXGI), memory, screens, power, sound devices
  and cameras, gathered off the UI thread (`Infrastructure/SystemInfo.cs`), and **health checks**
  (`Core/Diagnostics/HealthChecks.cs`): low disk space, on battery, a screen assigned to an
  output that isn't connected, missing media, no recent backup…
- **Logs**: the app's own log, and any hang reports.
- **Check this PC…**: runs the app's built-in self-test **in a separate copy of the app**,
  on a throwaway library (`Infrastructure/PcCheckRunner.cs`). Only that copy's environment points
  `MXU_SLIDES_DATA` and `MXU_SLIDES_LOCAL` at new folders in the temporary folder; the running
  app and your library are never touched. It refuses while anything is on an output (or music,
  a recording or a stream is running), and waits its turn on the same lock the scripts' heavy
  test runs use (`Local\MxU Slides heavy tests`). It shows progress, then a pass/fail list with the
  folder of pictures, and one line per area: screens, graphics card and transitions, capture and
  streaming, sound devices, NDI and DeckLink (`Core/Diagnostics/PcCheck.cs`).

The self-test itself now **refuses to run on a real library**: started by hand without
`MXU_SLIDES_DATA`/`MXU_SLIDES_LOCAL` pointing at a scratch folder (or with them pointing at the
real one) it writes "RESULT: FAIL (refused: not a throwaway library)" and says why
(`SelfTestSandbox` in `PcCheck.cs`, checked in `AppStartup.RefusesUnsafeSelfTest`). The script and
Check this PC set `MXU_SLIDES_SELFTEST_SANDBOX=1` to say "this one is safe".

**Hang detection** (`Infrastructure/HangWatch.cs`, rules in `Core/Diagnostics/HangDetector.cs`): a
background thread pings the UI thread four times a second. An answer later than 250 ms is a
hitch (logged at most six times a minute, the Mac's `PerfLineLimiter`); no answer for 5 seconds is
a hang, and the stacks of every thread are written to `Logs\hang-….txt` (with ClrMD, as the
Phase 6 trail does), at most every two minutes. Nothing about this waits on the UI thread, so it
can never itself stop a show.

**Report a problem** collects the log (every day's, first, as the Mac now does), hang reports,
Windows' crash reports for this app since the last launch, the system facts and health checks,
and your note, into one zip in Downloads. Secrets are taken out first (API keys, stream keys,
tokens in addresses, your Windows user name in paths: `SecretScrubber`). Nothing is sent
anywhere: you choose who to send it to.

## Where to look

| What | File |
|---|---|
| Folders, sorting, multi-select | `Core/Library/LibraryBrowse.cs`, `FolderPaths.cs`, `LibraryDates.cs` |
| Search | `Core/Library/LibrarySearch.cs`, `LibraryCatalog.cs` |
| Favourites and tags | `Core/Library/LibraryMarks.cs` |
| Batch rules | `Core/Library/LibraryBatch.cs` |
| Export | `Core/Export/*` |
| Backups | `Core/Library/LibraryBackup.cs`, `BackupFiles.cs` (with `ScheduledBackup`) |
| Edited and deleted media files | `Core/Library/BlobEdits.cs`, `MediaRelink.cs`; `ViewModels/MainViewModel.BlobEdits.cs`; `Rendering/MediaHub.ReloadInPlace.cs` |
| Files from Explorer | `Core/Library/OpenRequests.cs`, `Infrastructure/FileHandoff.cs`, `Views/MainWindow.Handoff.cs`, `scripts/file-types.ps1`, `install.ps1`, `uninstall.ps1` |
| Welcome guide | `Core/Library/OnboardingFlow.cs`, `Views/OnboardingWindow.cs`, `Views/HelpMenu.cs` |
| Diagnostics | `Core/Diagnostics/*`, `Infrastructure/SystemInfo.cs`, `HangWatch.cs`, `ProblemReport.cs`, `PcCheckRunner.cs`, `Views/DiagnosticsWindow.cs`, `ReportProblemWindow.cs` |
| The app | `ViewModels/MainViewModel.LibraryBrowse.cs`, `.LibraryBatch.cs`, `.MediaBrowse.cs`, `.Export.cs`, `.Backup.cs`, `.MusicImport.cs`; `Views/LibraryPanel.xaml(.cs)`, `LibraryMenus.cs`, `LibraryDepthDialogs.cs`, `MediaBinBar.cs`, `ExportWindow.cs`, `BackupWindow.cs`, `RestoreWindow.cs`, `LibraryCareSection.cs` |
| Tests | `tests/MxU.Slides.Core.Tests/Library/` (`LibraryBrowseTests`, `LibrarySearchTests`, `LibraryMarksTests`, `LibraryBackupTests`, `OpenRequestsTests`, `MediaFileEditsTests`), `Export/ExportTests.cs`, `Diagnostics/DiagnosticsTests.cs` |
| Self-test | `Infrastructure/SelfTest.LibraryDepth.cs`, `SelfTest.LibraryCare.cs` (section `LibraryDepth`), pictures `100-…` to `130-…` |

## Things to try

Try these in a **test** library (set `MXU_SLIDES_DATA` and `MXU_SLIDES_LOCAL` to a scratch folder).

1. Make a folder inside a folder, right-click the inner one › *Move to Folder › Top Level*.
2. Search for three words from the middle of a verse, then right-click the hit › *Show in Library*.
3. Ctrl+click three songs and *Export 3…* as a lyric sheet PDF; open it and search for a word.
4. *Back Up Now*, delete a song, open the backup from Explorer, and restore: the song comes back
   and a safety copy appears in the Backups folder.
5. Open a picture from the library's `Blobs` folder in Paint, draw on it and save: ten seconds
   later the media tile shows your change.
6. Run `pwsh apps/windows/scripts/install.ps1 -DryRun` to see every registry value it would write.
7. Read `ScheduledBackup.Due`: the whole "once after each 3 AM, catching up a missed night" rule
   is one line.

## Different from the Mac

- **Backups are zip files of the documents**, not the Mac's Automerge store, and a Mac backup is
  refused with a clear message ("This backup was made on a Mac…") until Windows reads Automerge
  (Phase 10). Restoring is *newer wins* per document rather than Automerge's merge of two
  histories. Nightly backups go to the library's own Backups folder by default (on the Mac
  they're off until a folder is chosen); every launch and weekly are Windows extras.
- **Deleted media files go to the library's Trash**, not straight off the PC.
- **A file seen for the first time is noted, not hashed** (the Mac checks everything at launch).
- **Search ranks more kinds of match** (CCLI number, author, tag, file name) and covers media and
  music; Date Added applies to every kind (the Mac's is for media).
- **Name order is Windows' own** (case-insensitive, the PC's language), where the Mac uses
  `localizedStandardCompare`.
- **Plain text export has no ChordPro tags** (that's what the ChordPro export is for). PDF slide
  sheets draw a transparent slide on the theme's colour rather than a checkerboard.
- **The export window comes before the save dialog**, so the file name and type follow the choice.
- **Songs' favourites and tags are Windows extras**: a Mac doesn't see them (its documents have
  no field for them). Media's and music's are the shared fields, so it does.
- **Hang stacks** come from ClrMD; the Mac samples with `spindump`.
- **Motion backgrounds**: replacing one now reloads it in place (keeping its position) through
  `MediaFilesChanged`, and `MediaHub.ReloadSilentVideos` then reopens it from the start of the
  loop as before. Two opens in a row; harmless, but a later tidy-up could drop one.

## For PORTING.md

**Ported line by line** (add to that table):

| Swift | C# |
|---|---|
| `PresenterCore/…/Library/LibraryViewOptions.swift` | `Core/Library/LibraryBrowse.cs` (`LibrarySort`, `LibrarySorts`); tests `LibraryBrowseTests` |
| `PresenterCore/…/ListMultiSelect.swift` | `Core/Library/LibraryBrowse.cs` (`ListMultiSelect`); tests `LibraryBrowseTests` (port of `ListMultiSelectTests`) |
| `PresenterCore/…/Library/IndexSnapshot.swift` (`LibraryBrowseLogic`), `FolderTreeLogic.swift`, `TeamDrives.swift` (`folderTree`, the folder half of `folderMoveMenu`) | `Core/Library/LibraryBrowse.cs` (`LibraryFolders`, `FolderNode`, `MoveTargets`), `FolderPaths.cs`; tests `LibraryBrowseTests` (incl. `moveToFolderShowsFoldersWithinFolders`) |
| `PresenterCore/…/Import/PresentationExport.swift` | `Core/Export/PresentationExport.cs` (+ `PdfDocument`, `TrueTypeFont`, `LyricSheet`, `SlidesPdf`, `TextExport`); tests `Export/ExportTests.cs` |
| `PresenterCore/…/Library/BackupBundle.swift` | `Core/Library/LibraryBackup.cs`; tests `LibraryBackupTests` |
| `PresenterCore/…/Library/ScheduledBackup.swift` (Nov 2026) | `Core/Library/BackupFiles.cs` (`ScheduledBackup`); tests `LibraryBackupTests` (port of `ScheduledBackupTests`) |
| `PresenterCore/…/Diagnostics/DiagnosticsBundle.swift` (incl. Nov 2026: logs first, counted zipped) | `Core/Diagnostics/DiagnosticsBundle.cs`; tests `DiagnosticsTests` |
| `PresenterCore/…/Diagnostics/PerfLineLimiter.swift` | `Core/Diagnostics/HangDetector.cs` (`PerfLineLimiter`); tests `DiagnosticsTests` |
| `PresenterCore/…/Onboarding/OnboardingFlow.swift` | `Core/Library/OnboardingFlow.cs`; tests `OpenRequestsTests` |
| `PresenterCore/…/MediaImport/MediaRelink.swift` (`refingerprinted`, Nov 2026) | `Core/Library/MediaRelink.cs` (`Refingerprinted`); test `MediaFileEditsTests` |
| `PresenterCore/…/MediaImport/MediaImportDedupe.swift` (`unattendedUsesExisting`, Nov 2026) | `Core/Library/MediaImportDedupe.cs`; test `MediaFileEditsTests`; used by `ImportMediaFilesAsync(unattended: true)` |
| `PresenterCore/…/MediaImport/BlobStore.swift` (edited files, Nov 2026) and `Sources/LibraryFileEdits.swift` | `Core/Library/BlobEdits.cs`, `ViewModels/MainViewModel.BlobEdits.cs` (a map of times instead of xattrs); tests `MediaFileEditsTests` |
| `Sources/LibraryView.swift` (Nov 2026: folder *Move to Folder*, search hits with *Show in Library* and the item's own menu, delete removes unused files) | `Views/LibraryMenus.cs`, `Views/LibraryPanel.xaml.cs`, `MainViewModel.LibraryBrowse.cs` (`ShowInLibrary`) |

**Windows-only** (add to that list):

- Library browsing extras: `LibrarySearch.cs` (ranked search over every kind), `LibraryMarks.cs` (song favourites and tags as Windows extras `library-marks`), `LibraryDates.cs` (`usage.json`), `LibraryCatalog.cs`, `LibraryBatch.cs`; `Views/LibraryPanel.xaml`, `LibraryMenus.cs`, `MediaBinBar.cs`, `LibraryDepthDialogs.cs`.
- Opening files from Explorer: `Core/Library/OpenRequests.cs`, `Infrastructure/FileHandoff.cs`, `Views/MainWindow.Handoff.cs`, `scripts/file-types.ps1`, `scripts/uninstall.ps1`.
- Diagnostics: `Core/Diagnostics/HealthChecks.cs`, `HangDetector.cs` (`HangDetector`), `PcCheck.cs` (Check this PC, `SelfTestSandbox`), `Infrastructure/SystemInfo.cs`, `HangWatch.cs`, `ProblemReport.cs`, `PcCheckRunner.cs`, `Views/DiagnosticsWindow.cs`, `ReportProblemWindow.cs`, `HelpMenu.cs`.
- Media: `Rendering/MediaHub.ReloadInPlace.cs`; music duplicate prompt `ViewModels/MainViewModel.MusicImport.cs` (`AudioImporter.Import(reuseSameFile:, knownHash:)`).
- Backups: `BackupFiles.cs` (every launch / weekly, safety copies), `AppSettings.Library.cs` (staged settings from a backup), `Views/BackupWindow.cs`, `RestoreWindow.cs`, `LibraryCareSection.cs`.
- Self-test: `Infrastructure/SelfTest.LibraryDepth.cs`, `SelfTest.LibraryCare.cs`.

**Not ported yet / not done** (add to that list):

- Restoring a **Mac backup** (Automerge): refused with a message until Phase 10.
- Team drives and their cloud search rows (`TeamDriveLogic.searchRows`, `cloudMatches`): no team drives on Windows yet.
- The Mac's `DeleteMediaSheet` wording is matched in our own dialog; its "This can't be undone" line isn't, since here the item and file go to the Trash folder.
- `PresentationExportPanel.swift`'s recording-export move and `WorkspaceImport.swift`'s Syphon changes don't apply (no such Windows features); `MediaUsage`'s public clock is an internal detail.
- Notes' unattended media import (the caller of `unattendedUsesExisting` on the Mac) belongs to the notes work; the option is ready (`ImportMediaFilesAsync(…, unattended: true)`).

*This is the walkthrough; see also [HOW-IT-WORKS.md](../HOW-IT-WORKS.md) and [PORTING.md](../PORTING.md).*
