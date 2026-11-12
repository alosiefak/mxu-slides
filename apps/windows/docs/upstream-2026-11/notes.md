# Make slides from notes (MxU PR #13)

A pastor sends sermon notes: a Word file, a PDF, an email pasted in. Before this feature someone
retyped the title, the Bible verse, the three points and the closing quote into slides by hand.
Now the notes live in the library, and **Make Slides** reads them and builds the slides in the
church's theme. When the pastor changes the notes later, the slides it made are offered the
change.

## Trying it

1. In the library header, click **Notes** (beside Presentations).
2. **New**, **Paste** (what's on the clipboard), **Import Notes…**, or drop a file on the list:
   Word (.docx), RTF, ODT, PDF, PowerPoint (.pptx), or plain text (.txt, .md).
3. Pick lines and press **Quote**, **Point**, **Scripture**, **Title** or **More ▾** to mark them
   yourself, or leave it to Make Slides.
4. **Make Slides…**: choose how passages are picked (*Pick for me*, *Bold lines only*, *By
   structure*, or *Only what I marked*), the theme, and where the slides go (a new presentation or
   one you already have). Each passage is a row with its slide type, its design and a preview of its
   first slide. Untick a row to leave it out. **Options** holds what the church shares: one verse a
   slide, blanks for fill-in points, reveal points one at a time, and the design to use for each
   slide type.
5. **Make Slides**. The deck opens in the grid.
6. Change the notes later (**Edit Text**): a passage whose slide would change shows **Update**, a new
   passage shows **Add**, a removed one **Remove**. The same badges show on the deck's slides in the
   main window's grid (an Add sits on the slide the new one would follow). **Changes**, or a click
   on a badge, lists them; Apply puts the new words on the slide (the old ones go into the slide's
   history, so nothing is lost) or Skip leaves it.
7. **Present** opens the note beside the slides and lights the passage of whatever slide is live.

## How it works, piece by piece

The notes are a shared document (`NoteDocument`, generated from the schema the Mac uses), so a
note made here opens on a Mac and the other way round. Everything that decides anything lives in
`MxU.Slides.Core/Notes`, ported from the Mac's `PresenterCore/Library/Note*.swift` one file to one
file, with the Mac's tests beside them. The app only draws windows and saves.

**The note's body** is a list of blocks: paragraphs, headings, lists, quotes, pictures. Each block
holds *runs*: stretches of text with the same styling. A slide mark is four fields on a run: its
group (one passage can span several blocks), its slide type (title, scripture, points, quote…), the
slide it made, and whether Make Slides only *suggested* it (so the next Make Slides can suggest it
again in another mode) or a person marked it.

- `NoteReading` turns a body into plain text and back; `NoteSlideMarks` reads the marks into
  passages (`Groups`), marks, unmarks, and re-marks a note by its structure.
- `NoteSlideSuggestions` is *Pick for me*: it guesses which lines are a title, a Bible reference
  (using the Bible reader's `ScriptureReference`), a numbered list of points, a quote with an
  author, and leaves run-sheet lines ("Intro: …") alone.
- `SlidePlanner` (`DeterministicSlidePlanner`) turns passages into slides: how many slides a passage
  needs, which design of the theme each uses, where the line breaks fall. It measures text with
  the same Helvetica Bold widths the Mac uses, so both apps split a long verse at the same words.
  Its input and output are plain records (`MarkedGroup` in, `SlidePlan` out) behind
  `ISlidePlanner`, so an AI planner (Phase 12) can sit beside it.
- `NoteSlideBuilder` builds each planned slide from the theme's design (`SlideDesignMap` says which
  design each type uses; `PhotoSpots` finds a design's picture boxes for picture and video slides).
- `NoteSlidesMade` remembers in the note which passage made which slides, so later edits know
  where to go.
- `NoteLiveUpdates`, `NoteSlideUpdate` and `NoteSlideMatch` are the "Update" badges: when the notes
  change, each passage is found again (by its mark, else by its words) and compared with the slides
  it made.
- `NoteFollowAlong` lights the passage of the live slide in Present.

**Reading files** (`NoteFileReader.cs`, `NoteDocumentReaders.cs`): Word and ODT are zip files of
XML, read directly. PDF text comes from **PdfPig** (a NuGet package, Apache-2.0), which gives each
letter with its position, size and font; the Mac's PDF glyph reader is ported on top of that, so
big bold letters at the top read as a title just as on the Mac. RTF goes through the existing
ProPresenter RTF reader (`ProImport/Rtf`), which gained an opt-in flag to keep bold and italic.
PowerPoint goes through the existing `PptxImport`. The reading happens off the UI thread; only the
finished note is saved on it.

**The windows** are in `Views/Notes`: `NotesWindow` (the list and the note), `MakeSlidesWindow`,
`NoteChangesWindow` (with `NoteFollowAlongWindow`). `MainViewModel.Notes.cs` holds their
commands: import, save, Make Slides (`MakeSlides`), apply and skip updates.

**The badges on the slide grid.** Each time a note is saved, `FindNoteChanges` stores what it
proposes in the note itself (`PendingUpdates`). `MarkNoteUpdateTiles` (same file) then looks for a
note that made the open deck and has changes waiting, and sets two small properties on each slide
tile: `NoteUpdate` ("Update" or "Remove") and `NoteAdds` ("+ Add"). The tile template draws them
with `Views/Notes/NoteTileBadges.cs`, which swallows the click so a badge never fires the slide
under it. It runs again whenever the grid is rebuilt, so the badges go as changes are applied.

**What this PC remembers.** How Make Slides picked passages last time (*Pick for me*, *Bold lines
only*…) is kept in `settings.json` (`AppSettings.MakeSlidesAutoMark`), as the Mac keeps it in its
own defaults. It's a per-computer habit, so it isn't in the shared design map.

## Different from the Mac

- **Marking is whole blocks.** The Mac marks any stretch of text inside a paragraph; the Windows
  notes window marks whole lines. The marks are the same fields, so a Mac's partial marks still
  read and make the right slides here.
- **Editing text** is a plain-text editor (Edit Text) that keeps every unchanged block as it was;
  the Mac edits the rich text in place.
- **Pictures from the web** in a note aren't fetched (nothing calls out to the network); pictures
  inside a Word or PDF file are kept. RTF links are dropped. No .doc or .rtfd.
- **An Add on the slide grid** is a "+ Add" badge on the slide the new one would follow; the Mac
  draws a ghost tile of the new slide between the two. Update and Remove badges are the same.
- **Copying from a browser or Mail** (HTML on the clipboard, with its pictures and Drive video
  chips) isn't read: Paste takes the clipboard's plain text. The Mac's three tests for that
  (`NoteFileMediaTests`: a browser's copy, a paste's pictures, a Drive chip's video link) aren't
  ported, and neither are its four `NoteTextReplacementTests`, which test the Mac's own text view.
- Five tests wait for the engine catch-up's starter packs (`up-core`): they are skipped with a note
  saying so, and pass once that lands.
- Of the Mac's seven `DesignActionsTests`, the four for what Windows has are ported; the three for
  applying a theme to a whole deck or to some slides wait for `Presentation.applyTheme` in C#.

## For PORTING.md

Ported line by line (add under the Swift file's row):

| Swift | C# |
|---|---|
| `PresenterCore/Library/NoteSlideMarks.swift`, `NoteSlideSuggestions.swift`, `NoteSlideUpdate.swift`, `NoteLiveUpdates.swift`, `NoteFollowAlong.swift`, `NoteMedia.swift`, `NotePassages.swift`, `NoteReading.swift`, `NoteDeckCards.swift`, `NotePanels.swift`, `NoteSlides.swift`, `NoteSlidesMade.swift`, `NoteTextReplacement.swift`, `LocalNotes.swift` | `MxU.Slides.Core/Notes/*` (same names; `NotePassages` and `NoteDeckCards` are in `NotePanels.cs`, `LocalNotes` in `NoteReading.cs`, `NoteTextReplacement` in `NoteSlideMarks.cs`) |
| `PresenterCore/Library/SlidePlanner.swift`, `SlideDesignMap.swift`, `PhotoSpots.swift`, `SlideBuildingSettingsLogic.swift` | `Core/Notes/SlidePlanner.cs`, `NoteSlideBuilder.cs`, `SlideDesignMap.cs`, `PhotoSpots.cs`, `SlideBuildingSettingsLogic.cs` |
| `Import/NoteFileReader.swift`, `PDFGlyphReader.swift`, `DocxMedia.swift`, `ZipExtract.swift` | `Core/Notes/NoteFileReader.cs`, `NoteDocumentReaders.cs` (PDF via PdfPig) |
| `PPTXImport/PPTXNoteText.swift` | `MxU.Slides.PptxImport/PptxNoteText.cs` |
| `PresenterCore/Library/DesignActions.swift`, slide history | `Core/Library/DesignActions.cs`, `SlideHistory.cs` |
| Tests: `DesignActionsTests` (4 of 7), `MakeSlidesFixturesTests`, `NoteSlideMarksTests`, `NoteSlideSuggestionsTests`, `SlidePlannerTests`, `NoteSlideUpdateTests`, `NoteLiveUpdatesTests`, `NotePanelsTests`, `NoteFollowAlongTests`, `PictureSlidesTests`, `NoteFileReaderTests`, `PPTXNoteTextTests` | `tests/MxU.Slides.Core.Tests/Notes/*`, `tests/MxU.Slides.PptxImport.Tests/PptxNoteTextTests.cs` (the shared `packages/make-slides-fixtures/*.json` are read as-is) |
| The app's notes views and Make Slides sheet | `Views/Notes/*`, `ViewModels/MainViewModel.Notes.cs`, `Views/MainWindow.Notes.cs` |

Windows-only:

| C# | What |
|---|---|
| `ProImport/Rtf/RtfNoteLines.cs` (+ RtfReader's opt-in `weights`) | RTF notes with bold and italic, through the ProPresenter RTF reader |
| `Core/Notes/NoteTextEditing.cs` | Edit Text: a plain-text edit that keeps unchanged blocks |
| `Core/Notes/NoteModels.cs` | Copy and value equality for the generated note classes (Swift's structs compare by value) |
| `Views/Notes/NoteTileBadges.cs`, `AppSettings.Notes.cs` | The grid's Update / Remove / Add badges; the remembered way of picking passages |

Not ported yet:

| Swift | Why |
|---|---|
| Marking part of a paragraph, rich-text editing in place | The notes window marks and edits whole lines |
| The Add ghost tile on the slide grid (`NoteLiveUpdates.cells` in the grid) | An Add is a badge on the slide before it |
| Pasting HTML (a browser's or Mail's copy, with pictures and video links) | Paste takes plain text |
| Web pictures in notes; .doc and .rtfd | No network fetches; no reader for those formats |
| `Presentation.applyTheme` / `adoptDesignActions` (deck-wide design actions) and their 3 tests | Not needed by Make Slides; the per-slide part is ported |
| `NoteTextReplacementTests`, the `*SweepTests` over the Mac's Swift source | They test the Mac's own text view and source files |
| Photo-spot drawing in the new starter designs | Comes with the engine catch-up (`up-core`) |

*This is the walkthrough; see also [HOW-IT-WORKS.md](../HOW-IT-WORKS.md) and [PORTING.md](../PORTING.md).*
