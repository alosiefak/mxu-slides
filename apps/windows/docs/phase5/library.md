# Phase 5: library exchange with the Mac, and missing media

This part of Phase 5 is about media and documents that travel: a presentation carried from the
Mac (or another PC) in a `.mxuslides` file, media whose file has gone missing, videos that won't
play on Windows, and the same file imported twice. Each is a port of the Mac's behaviour; the
rules live in Core (so they're unit-tested), the Windows-only parts (decoding, dialogs) sit
around them.

## 1. `.mxuslides`: a presentation in one file

The Mac's library is an Automerge store, which Windows can't read yet. So today the way a
song gets from the Mac to Windows (and back) is a `.mxuslides` file: **Import** (or drop the
file on the library list) to bring one in; right-click a presentation in the library ›
**Export as .mxuslides…** to make one.

The file is a zip. Its layout must match the Mac's exactly, so it lives in one place,
`src/MxU.Slides.Core/Library/SlidesPresentationFile.cs` (a port of `SlidesPresentationFile.swift`):

```
manifest.json       { "formatVersion": 1, "createdAt": "2026-10-24T09:30:00Z", "name": …, "slideCount": … }
presentation.json   { "presentation": {…}, "themes": [ … ], "media": [ … ], "audio": [ … ] }
blobs/<sha256>.png  each media file, named as in the library's Blobs folder
fonts/<sha256>.ttf  the fonts the slides name that the other computer may not have
```

The entries sit at the top of the zip: the Mac makes it with `ditto -c -k` on a folder's
*contents*, and reads it back by unzipping into a folder and opening `manifest.json` there.

**Writing** (`Write`): `SlidesPresentationFile.Bundle` collects the presentation, every theme
its slides are drawn with, every media item they show (`MediaReferences.cs`) and the songs its
"play music" actions start (`DeckBundle.cs`). They are *copies*, taken on the UI thread, so the
zip can be written on another thread while you keep editing. Media whose file isn't on this PC
is left out and named in the notice. Videos are stored, not compressed (they are already),
which saves minutes on a long one. The file is built beside the destination and moved into
place at the end, so a failed export never leaves half a file under the real name.

**Reading** (`Read`): the zip is unpacked into a scratch folder by `SafeZip.cs`, which refuses
names that climb out of the folder (`../`), links, Windows' reserved names (`CON`, `NUL`…) and
zip bombs (the unpacked size is capped against the file's size, counted on the bytes actually
inflated). The Mac's `__MACOSX/` and `._name` entries are skipped. A file from a newer app
(`formatVersion` above 1) is refused with the Mac's message rather than half-read.

**Importing** (`MakePlan`, then `ImportContents`):

1. Themes and items the library already has (by id) are left as they are.
2. An item whose *file* the library already has under another item isn't added twice. Instead
   the slides are pointed at the library's item: `Remapped` swaps the id wherever it appears in
   the presentation's JSON, so no field that holds a media id can be forgotten.
3. Anything else the bundle carries is added; an item whose file the bundle lacks is reported
   ("the file didn't include Walk-in Loop").
4. If the presentation is already here, you're asked: **Keep Both** (a copy with a new id),
   **Replace** (the library's copy is overwritten) or **Skip**.
5. Fonts are copied into the library's `fonts` folder unless this PC already has every face in
   them. Which faces this PC has is read from the font files' own name tables
   (`FontSharing.cs` → `FontNames`), because WPF doesn't expose PostScript names. Another part of
   Phase 5 makes the library's fonts usable for drawing.

**Things learnt the hard way**

- *Dates.* The Mac's JSON decoder reads dates as `2026-10-24T09:30:00Z` and refuses anything
  with fractions of a second, which is what .NET writes by default. A Windows file with
  `.1234567` in `createdAt` would be "not an MxU Slides presentation" on the Mac.
  `InternetDateConverter` writes whole seconds in UTC.
- *Slashes.* The Mac's encoder writes `/` as `\/` ("Songs\/Hymns"). It's valid JSON and .NET
  reads it fine; the test file has them on purpose.
- *Two tests guard compatibility.* `AFileLaidOutAsTheMacWritesItImports` builds a file byte for
  byte as `ditto` and the Mac's JSON encoder would (`tests/…/Library/MacArchive.cs`): a
  directory entry per folder, streamed deflate with data descriptors, "made by Unix" with file
  modes, the `UT`/`ux` extra fields, pretty JSON with `" : "` and sorted keys. .NET never writes
  zips like that. `TheFileWrittenHereHasTheMacsLayout` checks the other direction.

## 2. Missing media

Every use of a media item (a slide background, a picture on a slide, a service row, a
playlist entry, a timeline cue) points at the item's **id**, never at its file. The item points
at a file in the Blobs folder by its SHA-256. That's why one fix repairs every use at once.

**Finding files** is `BlobStore.cs` (a port of the Mac's `BlobStore.swift`). `LibraryStore.BlobPath`
now asks it. It lists the Blobs folder once and remembers the listing until the folder's
modified time moves on, so drawing a hundred tiles costs one listing, not a hundred disk checks.
A file it finds is checked on disk before it's trusted (someone may have deleted it).

*Learnt the hard way:* Windows stamps a folder from a clock that ticks every few
milliseconds. A file landing in the same tick as a listing leaves the folder's stamp
unchanged, so a cached "not here" could stick. A "not here" is therefore only believed once the
folder has been quiet for two seconds (`QuietWindow`); before that a miss lists again (at most
once per pass, and once every 50 ms). The test `AFileLandingInsideTheStampsResolutionIsFoundOnceTheGapPasses`
acts it out.

**The badges** (`ViewModels/MainViewModel.MissingMedia.cs`):

- a media tile says **FILE MISSING** (or **NEEDS TRANSCODE**, **TRANSCODING…**, **TRANSCODE FAILED**);
- a slide tile shows a warning triangle when media it shows has no file here, or was deleted;
- a service row says **File missing** (media) or **2 media files missing** (a presentation).

They're drawn by `Views/MediaBadge.cs`, from properties added to the tile view models in
`ViewModels/ItemViewModels.Library.cs`.

**Putting a file back** (`Core/Library/MediaRelink.cs`):

- **Locate missing file…** (right-click a tile) opens `Views/LocateMissingMediaWindow.cs`. It
  lists items with no file, and *deleted items still in use*. **Scan Folder…** looks through a
  folder and its subfolders for exact copies by content, so a renamed file is still found, and
  relinks them all. **Choose File…** takes any file. A file in the scanned folder with the
  original's name but different content (re-exported, re-encoded) is offered on its row as
  "Use …", never used by itself, because a wrong match would put the wrong picture up on Sunday.
- **Replace file everywhere…** lists where the item is used (`MediaUsage.cs`; a presentation
  whose *theme* uses it is listed "via" that theme), then takes the new file. If another item
  already has that file you're asked first (**Replace Anyway**), as the two would then show the
  same picture. Settings made for the old file (trim, play rate, effects…) carry over, and you're
  told which, so you can check them against the new one.
- **Deleting** media that's still used says where first. Each of those spots then shows a
  missing-media warning until it's restored or replaced.
- **Deleted items still in use.** The Mac keeps a "tombstone" of a deleted item. On Windows a
  deleted document waits in the library's `Trash` folder, so that's where tombstones come from
  (`MissingMedia.TrashedItems`). Restoring one recreates the item under its *own id*, which
  heals every slide that used it.

*Learnt the hard way:* a timeline's music clip keeps its song's id in the cue's `mediaId`,
the same field a video cue uses. Counting "ids with no media item" therefore made every song
with music on its timeline look like it had missing media. `MissingMedia.UnknownIds` leaves out
ids that are songs (and built-in sources such as `input::item::…`).

After any of these, `MediaFilesChanged` tells `MediaHub` to forget what it decoded from the old
file (`Rendering/MediaHub.Library.cs` → `ForgetFile`), then redraws tiles, slides, service rows
and outputs.

## 3. Probing media on import

`src/MxU.Slides.Broadcast/Media/MediaImportProbe.cs` looks inside every imported file with
Windows' own decoders. `LibraryStore.Prober` is set to it when the app opens the library
(`App.xaml.cs`), so the PowerPoint and ProPresenter importers get it too.

- **Pictures** are opened with Windows' imaging codecs (`Windows.Graphics.Imaging`, the ones
  Photos uses) for their size. One Windows can't decode (an iPhone HEIC photo without the HEIF
  extension, a damaged file) is skipped, with the reason, as the Mac skips an unreadable image.
- **Videos** are opened with Media Foundation: length, size, codec, whether there's video and
  sound. Then the real test: the first picture and the first sound are *decoded*. Opening a
  file isn't enough: a ProRes or HEVC file opens fine on a PC with no decoder for it, then shows
  black in the service.

`MediaPlayability.cs` turns that into the item's status, as the Mac does with its list of
codecs AVFoundation plays:

| Found | Status | Note |
|---|---|---|
| H.264, HEVC, Windows Media (VC-1), decodes | ready | |
| decodes, but another codec (Motion JPEG, MPEG-2, raw AVI, VP9…) | needs transcode | "unsupported codec: MJPG. Transcode Now makes an H.264 copy…" |
| no decoder on this PC (ProRes, HEVC without the extension…) | needs transcode | what to install, or "export it as H.264 MP4 on the Mac it came from" |
| can't be opened | needs transcode | "unreadable by Windows: …" |
| no video track | needs transcode | "no video track" (the Mac's words) |
| plays, but its sound can't be decoded | ready | "its sound (AC-3) can't be decoded on Windows, so it plays silently" (tooltip only) |

Duration and picture size go in `durationSeconds`, `pixelWidth`, `pixelHeight`, as on the Mac.
Whether a file has video and sound shapes the status, but isn't stored: the documents have no
field for it (the Mac doesn't store it either).

**Transcode now** (right-click a flagged tile) is `Broadcast/Media/MediaTranscoder.cs` and
`Broadcast/Library/TranscodeQueue.cs` (a port of `TranscodeQueue.swift`). The Mac makes HEVC with
AVFoundation; here it's **H.264 MP4 with AAC**, which Windows' own encoders make cleanly on every
PC, written by the recorder's own MP4 writer. It reads as fast as the PC decodes, not in real
time: one reader for pictures, one for sound, taking whichever is behind next so they stay
interleaved. The copy is written beside the media files and then filed in, and the item points at
it: same id, same name, same settings. A file Windows can't decode can't be transcoded either
(decoding is the first half of transcoding), so that fails with the reason.

*Learnt the hard way:* Windows' H.264 decoder won't play a picture under about 64 pixels a
side, so a transcoded copy of a tiny video was "ready" on paper and black on screen. `Fit` now
grows tiny videos to at least 96 pixels a side (and shrinks anything over 4K).

Importing used to hash and copy files on the UI thread, which froze the window for a big video.
The import (`MainViewModel.MediaImport.cs`) now does each file's disk work on a thread of its own
(`OffUiThread`, a dedicated thread rather than the shared pool, which show timing keeps clear of)
and reports progress in the notice bar.

## 4. The duplicate prompt

Before, importing a file the library already had silently reused the old item. Now (a port of
`MediaImportPrompt.swift` and `MediaImportDedupe.swift`) the import asks:

- **the same file** (same hash): "“logo.png” is already in your library. It's the same file as
  “Logo”. Use that item, or add another copy as “Logo 2”."
- **another file with the same name**: "A different file has this name in your library…", with
  both pictures side by side;
- **Use Existing** (the default) or **Add as New Copy**, numbered like Finder ("Background 2",
  then "Background 3"); with several files left, "Do the same for the rest of this import".

`MediaImportDedupe` (Core) decides; `Views/LibraryDialogs.cs` asks. Media and music never match
each other. The importers (PowerPoint, ProPresenter) still reuse the existing item by hash without
asking, as the Mac's do.

## The dialogs

`Views/LibraryDialogs.cs` holds every question above as a small `ChoiceWindow` in the app's dark
style, worded as the Mac words them. The view model asks through delegates
(`AskPresentationConflict`, `AskMediaImportConflict`, `ConfirmReplaceEverywhere`,
`ConfirmSharedFile`, `ConfirmDeleteUsedMedia`, `ChooseMediaFile`), so the logic doesn't depend
on WPF. `LibraryDialogs.Showing` lets the self-test picture each real dialog and press a button.

## Where to look

| What | File |
|---|---|
| The `.mxuslides` format | `Core/Library/SlidesPresentationFile.cs`, `DeckBundle.cs`, `SafeZip.cs`, `FontSharing.cs` |
| Which media a document uses, and where an item is used | `Core/Library/MediaReferences.cs`, `MediaUsage.cs` |
| Finding media files | `Core/Library/BlobStore.cs` (used by `LibraryStore.BlobPath`) |
| Relinking, replacing, tombstones, folder scans | `Core/Library/MediaRelink.cs` |
| Importing as new items, the probe interface | `Core/Library/MediaImporting.cs`, `MediaImportDedupe.cs` |
| Windows' decoders, transcoding | `Broadcast/Media/MediaImportProbe.cs`, `MediaPlayability.cs`, `MediaTranscoder.cs`, `Broadcast/Library/TranscodeQueue.cs` |
| The app | `ViewModels/MainViewModel.LibraryExchange.cs`, `.MediaImport.cs`, `.MissingMedia.cs`, `ItemViewModels.Library.cs`; `Views/MainWindow.Library.cs`, `LibraryDialogs.cs`, `LocateMissingMediaWindow.cs`, `MediaBadge.cs` |
| Tests | `tests/MxU.Slides.Core.Tests/Library/*` (SlidesPresentationFile, BlobStore, MediaUsage, MediaRelink, MediaImportDedupe, MediaImporting, FontSharing), `tests/MxU.Slides.Broadcast.Tests/MediaImportProbeTests.cs` |
| Self-test | `Infrastructure/SelfTest.Library.cs`, pictures `70-…` to `84-…` |

## Things to try

1. **Carry a song to the Mac.** Right-click a song › *Export as .mxuslides…*, copy the file to a
   Mac, double-click it there. Then change a word on the Mac, export, and import it back here:
   choose *Replace*.
2. **Lose a file on purpose** (in your *test* library): delete one file from `Blobs`, restart,
   and look at the media tile, the slide and the service row. Copy the file somewhere under a new
   name and use *Locate missing file… › Scan Folder…* on that folder.
3. **Import a camera's AVI**, see *NEEDS TRANSCODE*, hover for the reason, then *Transcode now*.
4. **Read `MakePlan`** with `PlanPointsAtFilesTheLibraryHasAndReportsOnesNobodyHas` beside it: it's
   the whole import decision in thirty lines.

## Different from the Mac

- **Transcoding makes H.264**, not HEVC: Windows' HEVC encoder is only on some graphics cards,
  H.264 is everywhere. The codecs counted as "plays well" are Windows' (H.264, HEVC, VC-1), not
  the Mac's (H.264, HEVC, ProRes): ProRes has no decoder on Windows.
- **Blobs aren't made read-only.** The Mac locks each media file (`0444`); on Windows a read-only
  file can't be deleted or moved by the other parts of the app that file recordings and imports,
  so they're left writable. `blobsAreReadOnlyAndTheOriginalIsNot` has no twin.
- **No team drives**, so imports land unsorted: the Mac's `.unplaced` placement, never a drive's
  "Needs Sorting" folder. `importsLandInTheDriveInTheirLibrarysFolder` has no twin.
- **Tombstones come from the Trash folder**, not a tombstone store with posters. The poster-cache
  tests (`posterSiblingOverwriteKeepsOneFilePerItem`, `aMissingPosterIsRememberedUntilOneIsWritten`,
  `tombstoneRoundTripAndSweep`) have no twins; Windows' posters live in `MediaHub`.
- **Fonts shared from Windows** are the library's own fonts and fonts installed for this user.
  Fonts in `C:\Windows\Fonts` aren't sent, because Windows mixes its own fonts (not ours to hand
  out) with ones installed for all users there, and can't tell them apart. The Mac leaves out only
  `/System/` and `/Library/Apple/`.
- **Locate also offers same-name files** after a scan (never automatically). The Mac matches by
  content only.
- **Slide tiles flag deleted media too**, not only missing files, and include the presentation's
  and section's background, since those are drawn on the slide.
- **Music imports** (the Music tab) still reuse an existing song silently; the duplicate prompt
  covers pictures and videos. The music importer belongs to the sound work.
- **A replaced video that's playing live** keeps playing the old file until it's fired again (the
  Mac reloads it in place).
- The suggested export file name also replaces the characters Windows forbids (`<>"|?*`), not
  just the Mac's `/ : \`.
- `MediaReplaceSweepTests.swift` checks the Mac's SwiftUI source text and has no Windows twin.

*The PORTING.md rows for this work are in [PORTING.md](../PORTING.md); this is the walkthrough. See also [HOW-IT-WORKS.md](../HOW-IT-WORKS.md).*
