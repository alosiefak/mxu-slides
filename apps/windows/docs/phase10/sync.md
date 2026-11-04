# Phase 10: library sync with the Mac

Until now a Mac and a Windows PC could only hand each other single presentations, as
`.mxuslides` files. Phase 10 lets them share **one library**: a folder both can reach (OneDrive,
Dropbox, iCloud for Windows, or a network share) holds the library in the Mac's own format, and
Windows keeps its library and that folder in step, both ways. Edits made on both sides at once
are merged the way the Mac merges them, with Automerge.

This walkthrough explains the Mac's library format first (you need it to follow the rest), then
the pieces Windows adds, then how to use it and how to try it with a real Mac.

## 1. Using it

**Settings › Library sync › Choose a shared folder…** Pick one of:

- the Mac's library folder or a Mac backup (a `.mxubackup` folder), in a place this PC can reach;
- an empty folder, to start a shared library that a Mac (or another PC) joins later.

Sync says what it's about to do and asks first. It then:

1. backs up both libraries (to `Library sync/backups/<date and time>` inside this PC's library
   folder: every Windows document and Windows extra, and every Mac document);
2. brings in what the Mac has and this PC hasn't, sends out what this PC has and the Mac hasn't,
   and, where both have the same document, keeps the copy changed more recently (the other stays
   in the backup);
3. from then on, follows both sides: an edit here goes out within a second, a change in the
   folder comes in within a second or two (or 30 seconds on a network share that sends no change
   notices), and the screens update. A song open on screen reopens with the Mac's change in it; a
   song being edited reopens when editing ends, with both edits merged.

The page shows the folder, the status (**Up to date · 214 documents · last synced just now**), and
a log of **what came in and went out** (↓ in, ↑ out). **Pause** stops syncing until **Resume**
(edits wait on each side, and stay paused after a restart); **Sync now** looks through everything
at once; **Turn off** stops syncing without deleting anything.

Sometimes sync stops to ask, and the page says why with the answers beside it:

- **A document this version can't read** (a newer Mac wrote something this version doesn't know,
  or a file is damaged): *Leave them out and carry on* leaves them untouched in the folder and
  doesn't bring them here; each is looked at again when it changes. Updating MxU Slides is the
  real answer.
- **Many documents disappeared at once** (at least 10, and a quarter of the library): a folder
  half-synced, a drive swapped. *Keep them* puts them back in the folder; *Delete them here too*
  moves them to the library's Trash.
- **A newer format**: a backup made by a newer Mac, or another PC that synced with a newer schema.
  Nothing to choose but updating.

If the folder goes away (an unplugged drive, OneDrive offline), the status says *Waiting for the
shared folder*, nothing is deleted, and sync carries on by itself when it's back.

## 2. The Mac's library format

Everything here comes from the Mac's Swift code: `PresenterCore/Documents` (`DocumentEntity`,
`DocumentStore`, `TypedDocument`, `EditorReplica`, `SubtreeRevert`), `PresenterCore/Library`
(`LibraryEngine*`, `SyncLedger`, `SyncScope`, `BackupBundle`) and the vendored automerge-swift
0.7.2 (`Codable/`).

**How documents are stored.** One library document (a song, a service, a theme…) is one
Automerge document, saved whole, compressed, as one file. Automerge keeps a document as a history
of *changes*; each change is a list of operations (set this key, insert into this list, delete
that) made by one *actor* (a random id the Mac picks each time it opens the document). Two copies
that each made changes can be merged: Automerge puts both histories together, and every copy
that has the same changes reads the same document, whatever order they arrived in.

The Mac turns its Swift structs into Automerge with automerge-swift's `AutomergeEncoder`:

- a struct (a "record" in `packages/schema/documents.json`) is a **map**, its fields set in the
  schema's order; an optional field that's empty isn't written at all;
- an array is a **list**;
- a string is a **string value** (never Automerge's collaborative text object, so two people
  editing the same line at once don't merge letter by letter: one whole line wins);
- an `Int` is an **Int**, a `Double` an **F64** (even 120.0), a `Bool` a boolean, and an enum its
  case's name as a string.

Before the Mac writes a document again it checks each value already there has the type it's
about to write (`cautiousWrite`), so a Windows write must use exactly these types or the Mac
couldn't save that document any more. `TypedDocument.update` rewrites the whole value (Automerge
skips puts that change nothing), and the slide grid's edits use smaller, scoped writes
(`updateSlideList`: real list inserts, deletes and moves).

Reading is `AutomergeDecoder` driving the generated Swift `Codable` code. Its rules are strict and
`Format/MacDecoder.cs` copies them exactly: a missing required field, a wrong type, or **an enum
value it doesn't know fails the whole document** (the enums are closed); keys it doesn't know are
skipped. A record's `Double` also accepts an Int, a list's doesn't.

**The folder layout** (`~/Library/Application Support/MxU Slides/Library` on the Mac):

```
presentations/<id>.automerge   one file per document, a folder per kind
services/  themes/  media/  audio/  playlists/  overlays/  …   (29 kinds: MacKinds.cs)
blobs/<sha256>.<ext>            the media files, named by their contents
fonts/   thumbnails/            library fonts, media posters
index.sqlite                    the Mac's search index and its sync ledger (rebuildable, this Mac's own)
.migrated-… .seeded-…           markers of one-time upgrades
```

A Mac backup (`Back Up Library…`, a `.mxubackup` folder) has the same document folders, `blobs`,
`fonts` and `thumbnails`, plus `manifest.json` (its `formatVersion`, now 1), `usage.json` and the
settings. **Import Backup… › Merge** merges every document in it into the Mac's own with
Automerge: that's how Windows' changes reach a Mac (section 6).

**How the Mac notices another machine's changes.** In this repository's Mac code there's no
folder watcher. When the Mac saves a document, `DocumentStore.save` first merges whatever is in
the file on disk (so another writer's changes are kept, never overwritten); when it opens one, its
cache reloads the file if its stamp (inode, size, time) moved; its tables re-read changed files
when they refresh. New documents reach its list through `rebuildIndex`, which runs when the index
is missing. The ledger types in `SyncLedger.swift` belong to a cloud sync service ("team" and
"station" areas, push and pull by sequence number, held deletes, quarantine, uploaded media); the
client that talks to that service isn't in this repository.

**How media travels.** A media file is stored once, named by the SHA-256 of its contents
(`BlobStore.swift`); a `MediaItem` document holds that hash (`fileHash`). Files are never changed,
only added, so moving them is just copying the ones the other side lacks (a backup does exactly
that, `copyMissing`).

**What the ledger records** (`SyncLedger.swift`): for each document (kind and id) the heads last
pushed (`lastPushedHeads`), the sequence numbers applied and seen (`appliedSeq`, `remoteSeq`),
whether a push is pending and when the last snapshot was taken; *held deletes* (a deletion kept
waiting, with which side and area); *quarantined* changes that wouldn't apply; which media were
uploaded. `SyncScope` says who shares each kind: the **team** (songs, services, themes, media…),
one **station** (output presets, MIDI devices, the combo and scheduler boards, stream
destinations: a computer's own set-up) or nobody (the import ledger).
`SyncReconcileLogic.afterTombstone` is the rule "a deletion loses to a later edit".

## 3. Automerge on Windows

The Mac reaches Automerge's Rust core through automerge-swift 0.7.2, which builds the `automerge`
crate **0.7.2**. Windows builds the same crate from source:

- `native/automerge-ffi` is a small Rust library of our own that wraps `automerge` 0.7.2 with a
  C interface (`src/lib.rs`, explained line by line). Its `Cargo.lock` started from
  automerge-swift 0.7.2's own lock file, so every dependency is the version the Mac's build uses.
- `tools/build-automerge.ps1` builds it into `src/MxU.Slides.Sync/Native/automerge.dll` (with
  Microsoft's C runtime linked in, so no Visual C++ redistributable is needed) and writes
  `AUTOMERGE-NOTICES.txt`: Automerge's MIT licence and every crate in the DLL. The DLL is
  committed, so building the app (and GitHub's CI) needs no Rust. Rebuild it only after changing
  the Rust code, then run the Sync tests and commit the DLL, notices and lock file together.

*Why our own wrapper rather than `automerge-c`?* automerge-c mirrors Automerge's whole API with
its own memory stack (`AMstack`, `AMresult`, `AMitem`): every value read is a separate call and a
separate thing to free, and it needs CMake to build. Library sync needs a dozen things: load,
save, fork, merge, heads, history, read the whole document, apply an edit. Our wrapper reads the
whole document in one call (a JSON tree with every value tagged by its Automerge type, so an Int
is never mistaken for an F64), and applies a whole edit in one call (a JSON list of operations,
in one transaction: all or nothing). C# makes a handful of calls and holds one kind of pointer,
through a `SafeHandle` that frees it exactly once. And the wrapper can only write what the Mac
writes: maps, lists, strings, Ints, F64s and booleans. It can't make a text object, a counter or
a null, so a bug on the C# side can't put a value into a document that the Mac would refuse.

`Automerge/AmDocument.cs` is the C# side: `Create`, `Load`, `Save`, `Fork`, `ForkAt`, `Merge`,
`Heads`, `Changes`, `Read`, `Apply`.

## 4. Reading and writing documents the Mac's way

`MxU.Slides.Sync/Format`:

- `MacSchema` reads `packages/schema/documents.json` (built into the assembly: the same file both
  apps' models come from).
- `MacKinds` lists the Mac's 29 kinds: folder names, schema records, who shares them, and the C#
  model Windows keeps each as. Sync carries the team kinds Windows keeps (16 of them). Notes,
  font files, slide-building and service-link settings aren't kept on Windows yet, so they're left
  exactly as they are in the folder.
- `MacDecoder` reads an Automerge document into JSON with the Mac's rules (above). Windows adds
  one: its models hold whole numbers in 32 bits, so a bigger one is refused rather than cut short.
- `MacEncoder` writes a new document as `TypedDocument(value)` does: every field in one change,
  with the message "create".
- `DocumentDiff` turns a Windows edit into the **smallest** Automerge edit: records are compared
  field by field, lists of records with ids (slides, objects, service items) are matched by id
  (the longest run both versions share, then real inserts and deletes), lists of values keep the
  values both share. A change to slide 3 touches only slide 3, so a Mac's change to slide 5 at
  the same time survives the merge. Keys the schema doesn't know (left by an older Mac, or added
  by a newer one) are never touched. One field is the exception, as it is on the Mac: a song's
  `arrangements` is written as a whole new list when it changes (upstream `TypedDocument.swift`,
  `wholeValueKeys`, from the "make slides from notes" release), so two computers reordering an
  arrangement at once end up with one computer's order, never a mixture with a section listed
  twice. Each arrangement that was there before goes into the new list as a copy of itself, so
  fields a newer Mac added to it are kept.
- `SchemaCheck` refuses a document that isn't exactly the schema: an unknown field, an unknown
  enum value, a missing field, NaN.
- `MacDocuments` puts it together. **Every write** is checked against the schema, made, then read
  back with the Mac's rules, and must say exactly what Windows meant, or it's thrown away and
  nothing is written. That's the rule "never write a document the Mac couldn't decode".

Windows' own changes are made under actors that start with the bytes `MXUW`, so sync can tell a
change made on Windows from one made on a Mac.

## 5. The sync engine

`MxU.Slides.Sync/Engine`. The Windows JSON library (`LibraryStore`) stays the app's working
store: everything else uses it as before.

- **Each document has a replica**: this PC's own Automerge copy, in
  `Library sync/replicas/<kind>/<id>.automerge`. It always holds everything both sides have said.
- **The ledger** (`Library sync/state.json`, `SyncLedger.cs`) remembers, per document, the
  shared file as last seen (a fingerprint, its size and time), the replica's heads, and the
  Windows file as last seen. Comparing those with what's there now says which side changed.
- **A pass** (`SyncEngine.cs`, `SyncEngine.Pass.cs`) runs on its own `WorkQueue`, never the UI
  thread. It's started by the library's `Changed` event (a Windows save, 0.7 s later, so a burst of
  saves goes as one), by the folder's change notices, and every 30 seconds.
- **For each document** (`SyncEngine.Documents.cs`):
  - *Changed on the Mac:* the shared file is merged into the replica, the replica read with the
    Mac's rules, and the result saved into the library (which raises `Changed`).
  - *Changed on Windows:* the edit is made **from the version it was edited from**. The app may
    have been holding the song as it was before a Mac change came in, and saved over it; diffing
    against the newest version would quietly undo the Mac's change. So sync remembers the last
    few versions it left in the library (with the replica's heads at each), takes the one nearest
    the edit as its starting point, makes the edit on a copy of the replica as it was then
    (`ForkAt`), and merges it back: the edit is concurrent with whatever came in since, as two
    Macs' edits are.
  - *Then* the replica goes out to the shared folder if the folder lacks anything, and into the
    library if the library does.
  - Before writing the shared file, sync checks it's still the file it read, as the Mac's
    `DocumentStore.save` merges first; if the Mac wrote it meanwhile, sync starts the document
    again and takes that change in.
  - Before saving into the library, the same: `LibraryStore.SaveIfUnchanged` (the one hook this
    phase adds to the store) saves only if the document's file is still the one sync read,
    checked and written under the store's own lock. So a change from the Mac can never overwrite
    an edit someone saved here a moment before.
  - A Windows edit taken into the replica is noted in the ledger at once, so if sending is
    interrupted (the share dropped, the app closed) it's never taken in twice (that would add a
    new slide twice).
- **Deletions.** Deleted here: the shared file goes to the sync trash (`Library sync/trash`), and
  a *tombstone* stops the Mac's old copy bringing it back (a backup made again on the Mac still
  has it); if the Mac *changed* it after, the change wins and it comes back. Deleted on the Mac:
  it counts only once it has been missing 5 seconds (cloud folders replace a file by deleting and
  re-creating it), never when its whole kind folder is gone, and many at once are held to ask.
  Then it goes to the library's Trash, unless it was edited here since (the edit wins, and it's
  put back), or only Windows ever wrote it (the Mac may not have had it yet: a backup made again
  on the Mac leaves out what it hadn't imported; it's put back).
- **The first sync** backs up both sides first (`SyncBackup.cs`), makes an empty folder a Mac
  library (a folder per kind, as the Mac's `DocumentStore` does), leaves a note
  (`.mxu-windows-sync.json`: the schema version Windows synced with; the Mac ignores it), and
  where both sides have the same document with different contents, keeps the one changed later.
- **Conflict copies.** When two computers write one file at once, OneDrive and Dropbox keep both
  ("song (MacBook's conflicted copy).automerge"). Automerge needs no such copy: its changes are
  merged into the real document and the copy goes to the sync trash.
- **Media** (`BlobMirror.cs`) is copied both ways on its own queue: any file one side lacks.
  A file arriving is checked against its name first (a cloud client may still be downloading
  it), and lands under a temporary name before it's renamed. Tiles that said FILE MISSING update.
- **Windows extras** (Windows-only transitions, production steps…) stay in `Windows extras/`
  beside the Windows library. They never go into the Mac's documents, and their fingerprints
  still make a Mac edit win.
- **Versions.** A backup whose `formatVersion` is newer than 1, or a note from a PC with a newer
  schema, stops sync before anything is written. A document this version can't read stops it to
  ask (section 1).

**A Mac newer than this build.** The shared schema grows: MxU's upstream release "make slides
from notes" (#13), with the one before it, took it from version 149 to 161. Fields added after a
type ships are always optional (a slide object's `photoSpot`, a cue's `scaleMode` and `endFade`, a
video's `marks`, a note's `mediaImports`…), and enums gain cases (`MediaScaleMode.scaleAndBlur`,
`MediaEndFade`). Sync copes with a Mac that's ahead of it like this:

- **A field it doesn't know** is skipped when the document is read (the Mac's decoder does the
  same), so it isn't in the Windows library, which couldn't hold it. But it stays in the shared
  document and in this PC's replica: every Windows edit is made *around* it, and a record Windows
  moves (a slide dragged to a new place) is re-inserted as a copy of itself with every key, as the
  Mac's `moveListElement` copies it. A value that couldn't be copied exactly stops that edit
  rather than lose it. The log notes, once per document, which fields it holds.
- **An enum case it doesn't know** can't be read without guessing, so sync stops to ask (as for any
  document it can't read): the document isn't brought in and its file is never written.
- **A kind it doesn't keep** (notes, font files, settings) is never touched.

When the Windows models are regenerated from the newer schema, the Sync project reads that same
file (it's built in), so the new fields and cases are simply known.

## 6. Trying it with a real Mac

A Mac can't be tried on this PC, so here is exactly what to do. Make a backup on the Mac first
(**Back Up Library…**) and keep it somewhere safe; Windows makes its own backups too.

**The safe way: a Mac backup as the shared folder.** It uses only what the Mac already does.

1. On the Mac, in MxU Slides: **Back Up Library…**, and save the backup into a folder both
   computers sync, for example `iCloud Drive/MxU/Library.mxubackup`, or the same in OneDrive or
   Dropbox.
2. On Windows, wait for that folder to download (iCloud for Windows, OneDrive or Dropbox), then
   **Settings › Library sync › Choose a shared folder…** and pick the `Library.mxubackup` folder.
   Windows backs up both sides, brings the Mac's library in, and puts its own documents and media
   into the backup folder.
3. Use either computer. Windows keeps the folder up to date as you work.
4. To bring Windows' changes to the Mac: **Import Backup…**, pick `Library.mxubackup`, and choose
   **Merge — keep edits from both**. Every document is merged with Automerge, so edits made on the
   Mac since are kept.
5. To send the Mac's newer changes to Windows: **Back Up Library…** again over the same
   `Library.mxubackup`. Windows sees the new files, merges them, and puts back anything of its own
   the Mac hadn't imported yet (that's why a document only Windows wrote is put back rather than
   deleted when it's missing).

What to check: a song edited on Windows shows the edit on the Mac after step 4; a song edited on
both computers between two exchanges has both edits; a picture added on Windows plays on the Mac.

**The live way (for the adventurous).** Sync also works with the Mac's own library folder, if
it's moved into a synced folder and linked back (quit MxU Slides first, then in Terminal:
`mv ~/Library/Application\ Support/MxU\ Slides/Library ~/Library/Mobile\ Documents/com~apple~CloudDocs/MxU-Library`
and `ln -s ~/Library/Mobile\ Documents/com~apple~CloudDocs/MxU-Library ~/Library/Application\ Support/MxU\ Slides/Library`).
Then choose that folder on Windows. Know the Mac's limits first (section 2): it notices Windows'
edits to a document when it next opens or saves it, but lists documents Windows *added* only once
its index is rebuilt (quit, delete `index.sqlite` in the library, start again), and a Mac with the
document open keeps its own copy if Windows deletes it. Two Macs must not use one library folder
this way at the same time (they'd share one `index.sqlite`).

## 7. Different from the Mac, and not done yet

- The Mac's own cross-computer sync (a cloud service with the ledger above) isn't in this
  repository, so Windows doesn't speak its protocol. Windows syncs through the folder, merging
  with Automerge exactly as the Mac's `DocumentStore.save` and backup **Merge** do.
- Windows writes edits as small changes (lists matched by id); the Mac's whole-value writes go
  by position. Both make documents the other reads exactly; Windows' merge more cleanly.
- Two people changing the same line of a song at once: one whole line wins, the same on every
  computer (as between Macs: the Mac doesn't use Automerge's text objects).
- A document deleted on the Mac but only ever written on Windows is put back, not deleted (the
  Mac may never have had it). Delete it on Windows instead.
- Not synced yet: the kinds Windows doesn't keep (notes, font files, slide-building settings,
  service-link rules: left as they are in the folder), the `fonts` folder, media posters
  (`thumbnails`), last-used dates (`usage.json`), and station documents (a computer's own set-up
  stays on it, as between Macs).
- Windows extras stay on each Windows PC; a second Windows PC syncing the same folder gets the
  shared documents, not this PC's extras.
- Whole numbers above 2,147,483,647 (fine on the Mac) can't be held by Windows' models; a document
  with one is refused, not cut short.

## 8. Tests

`tests/MxU.Slides.Sync.Tests` (95 tests):

- `MacFormatTests`: the DLL is Automerge 0.7.2; the embedded schema is the models' schema; the
  Mac's own fixtures (its `richDeck`, the welcome deck, every starter theme, a service) round-trip;
  a Windows document has exactly the Mac's Automerge types; a Mac document comes in; each side can
  go on editing what the other wrote (the simulator throws where the Mac's `cautiousWrite` would).
- `MacDecoderTests`: the decoder rules, ported from `AutomergeDecoderEquivalenceTests.swift`
  (null and absent optionals, missing fields, type mismatches, Int and F64, text objects, closed
  enums, legacy aliases, unknown keys, conflicts reading one winner).
- `DocumentDiffTests`, `MergeTests`: the smallest edits; edits on both sides at once (different
  slides, different fields, slides added on both sides, a Mac whole-value write against a Windows
  insert, the same field, a deletion against an edit, an edit from an older copy).
- `SyncEngineTests`, `SyncSafetyTests`: the first sync both ways, backups, adopting, newest-wins,
  edits both ways and merged, a stale editor, a Mac write in the middle of sending, an interrupted
  send not doubled after a restart, deletions both ways (with tombstones, debouncing, an edit
  winning, Windows-only documents put back, many held), a folder going offline, a document from a
  newer Mac, a newer backup format, NaN never sent, an unknown enum never written, a conflict copy
  merged, media both ways.
- `NewerMacTests`: documents as a schema-161 Mac writes them: its new fields are skipped, kept
  through Windows edits, moves and the first sync, and reported; a new enum case stops sync and is
  never written; notes are left as they are.
- `Fakes/MacSimulator.cs` plays the Mac: it writes documents the way automerge-swift's encoder
  does, written separately from the app's encoder from the Swift source.

`tests/MxU.Slides.Core.Tests/Library/LibraryStoreSyncHookTests.cs` tests the store's hook.

The self-test's **Sync** section (`Infrastructure/SelfTest.Sync.cs`, pictures `sync-01` to
`sync-06`) makes a scratch "Mac" library with documents written the Mac's way, chooses it in
Settings, checks the first sync, edits one song on both sides and shows the merged song, takes
the folder offline and back, and adds a document from a "newer Mac".

## For PORTING.md

Ported line by line (add to "Ported line by line"):

| Swift | C# |
|---|---|
| `automerge-swift/Sources/Automerge/Codable/Decoding/*` (the rules `AutomergeDecoder` applies to the schema's Codable types) | `MxU.Slides.Sync/Format/MacDecoder.cs` |
| `automerge-swift/Sources/Automerge/Codable/Encoding/*` (what `AutomergeEncoder` writes: maps, lists, string values, Int, F64, booleans, enum names) | `MxU.Slides.Sync/Format/MacEncoder.cs`; the test copy that also writes by position, `tests/MxU.Slides.Sync.Tests/Fakes/MacSimulator.cs` |
| `PresenterCore/…/Documents/DocumentEntity.swift` (`DocumentKind`, `directoryName`), `Library/SyncScope.swift` (`scope(for:)`, `entityType(for:)`) | `MxU.Slides.Sync/Format/MacKinds.cs` |
| `PresenterCore/…/Documents/DocumentStore.swift` (`url`, `save`'s merge-before-write, `ids(of:)`), `MediaImport/BlobStore.swift` (blob names) | `MxU.Slides.Sync/Engine/SharedFolder.cs`, `SyncEngine.Documents.cs` (`WriteShared`), `BlobMirror.cs` |
| `PresenterCore/…/Documents/TypedDocument.swift` (`init(value)`: one change, "create"; `updateSlideList`'s inserts, deletes and moves; upstream's `wholeValueKeys`/`encodeRootField`, not yet merged here) | `MxU.Slides.Sync/Format/MacDocuments.cs`, `DocumentDiff.cs` (`WholeValueKeys`, `WriteWhole`) |
| `PresenterCore/…/Library/SyncLedger.swift` (`hex(heads)`), `SyncScope.swift` (`SyncReconcileLogic.afterTombstone`: an edit beats a deletion) | `MxU.Slides.Sync/Automerge/AmDocument.cs` (`DocHeads`), `Engine/SyncEngine.Documents.cs` |
| `PresenterCore/…/Library/BackupBundle.swift` (`formatVersion`, the layout) | `MxU.Slides.Sync/Engine/SharedFolder.cs` (`BackupFormatVersion`) |

Re-implemented for Windows (add to "Re-implemented for Windows"):

| Mac | Windows |
|---|---|
| automerge-swift 0.7.2's prebuilt `automergeFFI.xcframework` (UniFFI) | `native/automerge-ffi` (the same `automerge` crate 0.7.2, a small C interface), built by `tools/build-automerge.ps1` into `MxU.Slides.Sync/Native/automerge.dll`; `MxU.Slides.Sync/Automerge/*` (P/Invoke with safe handles) |

Windows-only (add under "Windows-only"):

- `MxU.Slides.Sync/Engine/*`: the two-way bridge between the Windows JSON library and a Mac
  library folder (`SyncEngine`, the per-document replicas and `SyncLedger`, `SyncBackup`,
  `BlobMirror`, `WindowsLibrary`, `SyncLog`); `Format/SchemaCheck.cs`, `Format/DocumentDiff.cs`
  (id-matched list edits), `WindowsActor`.
- `Core/Library/LibraryStore.cs` → `SaveIfUnchanged`, `DeleteIfUnchanged`, `NotifyChanged`
  (library sync's save over the file it read).
- `Windows/ViewModels/MainViewModel.Sync.cs`, `Views/LibrarySyncSection.cs` (Settings › Library
  sync), `Infrastructure/AppSettings.Sync.cs`, `Infrastructure/SelfTest.Sync.cs`.

Not ported yet (replace the "Sync with the Mac's library folder" item):

- **Library sync extras:** the Mac's cloud sync protocol (its client isn't in this repository);
  the kinds Windows doesn't keep (notes, font files, slide-building settings, service-link rules:
  left as they are), the `fonts` folder, media posters and last-used dates; Windows extras
  between two Windows PCs; whole numbers above 32 bits.

*This is the walkthrough; see also [HOW-IT-WORKS.md](../HOW-IT-WORKS.md) and [PORTING.md](../PORTING.md).*
