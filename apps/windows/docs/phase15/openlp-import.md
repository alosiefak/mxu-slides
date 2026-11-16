# Bringing your songs across from OpenLP (Phase 15)

> **Not yet tried on a real OpenLP database.** This importer was built from OpenLP's published
> source code (which tables and columns it uses, and how it writes a song's words) and tested
> against **made-up** databases that the tests build themselves, in the layouts of OpenLP 2.0, 2.4
> and 3.x. Nobody has run it on a church's real `songs.sqlite` yet. The first real import should be
> done on a weekday, and checked as described under "What to check afterwards". The import cannot
> harm OpenLP's data (it only reads a copy), so trying it costs nothing.

## 1. Get the data off the church PC

OpenLP keeps everything in one **data folder**. The two files that matter are:

```
data\songs\songs.sqlite      every song
data\custom\custom.sqlite    every custom slide (liturgy, notices…)
```

1. **Close OpenLP.** While it is open it may be in the middle of saving, and a copy taken then can be incomplete.
2. Find the data folder. The sure way: open OpenLP, choose **Tools › Open Data Folder**, note where
   the window that opens is, then close OpenLP. If nobody ever moved it, it is
   `%APPDATA%\openlp\data` (paste that into Explorer's address bar; it usually means
   `C:\Users\<name>\AppData\Roaming\openlp\data`).
3. Copy what you need to a USB stick. Any of these works:
   - the whole `data` folder, as it is, or zipped (right-click › **Compress to ZIP file**);
   - just `songs.sqlite` (and `custom.sqlite` if you want the custom slides).

   The whole folder can be large, because it also holds pictures and videos. The importer only
   uses the two database files, so copying just those is fine.

If MxU Slides is on the **same PC** as OpenLP, skip the copying: the import can find the folder itself.

## 2. Import

Open **Screens & Settings**, and under LIBRARY press **Import from OpenLP…**. (The welcome guide's
"Bring your songs in" step has the same button.) Then choose one of:

| Button | Use it when |
|---|---|
| **Find OpenLP on this PC** | OpenLP is installed on this PC and its data folder was never moved. It looks in `%APPDATA%\openlp\data` |
| **Choose the OpenLP data folder…** | You copied the folder across, or it was moved. Pick the `data` folder (the `openlp` folder above it, or the `songs` folder inside it, work too) |
| **Choose a file…** | You have `songs.sqlite`, `custom.sqlite`, or a zip |

You can also pick a `.sqlite` or `.zip` in the ordinary **Import** dialog (Ctrl+I), or drop one on the library list.

A bar shows progress. A few hundred songs take a few seconds. Then a summary appears:

- how many songs and custom slides came in, out of how many OpenLP had;
- how many were left out because the library already had them;
- how many could not be imported;
- a **To check** list: each line names a song and says what to look at.

**Copy** puts the whole summary on the clipboard, so you can paste it into a note or an email. The same lines are written to the app's log.

**It is safe to run again.** A song the library already has is left out (see "Duplicates" below), so a second import after the choir adds five songs in OpenLP brings in just those five.

## 3. What comes across

| In OpenLP | In MxU Slides |
|---|---|
| A song's title | The song's name |
| Authors | The song's author line (all the names, joined with commas) |
| Copyright | The song's copyright, and the year if there is one in it |
| CCLI number | The song's CCLI number |
| Verses, choruses, bridges… | Sections named "Verse 1", "Chorus", "Bridge", "Pre-Chorus", "Intro", "Ending", "Other". A chorus is numbered ("Chorus 1", "Chorus 2") only when a song has more than one |
| Verse order (`v1 c1 v2 c1`) | An arrangement called **OpenLP order**, set as the song's default. A song with no verse order plays its sections as stored |
| The split mark `[---]` and OpenLP 3's `[--}{--]` | A new slide starts there |
| Formatting tags (`{r}`, `{st}`, `{it}`, a church's own `{gold}…{/gold}`) | The tags are dropped; the words stay. `{br}` becomes a new line |
| Chords (`[C]`, `[G/B]`, OpenLP 3) | Kept as chords, the way a ChordPro file's are. They are never shown as words |
| Custom slides | Ordinary presentations in a library folder called **OpenLP custom slides**, one slide per OpenLP slide |

**Slides are re-cut the way MxU Slides cuts lyrics.** OpenLP shows a whole verse on one screen if
it fits. MxU Slides puts a few lines on each slide (the "lines per slide" setting, two unless you
changed it), exactly as it does for a SongSelect or ChordPro file. So a four-line verse becomes two
slides. Every song also opens on a blank slide, as all songs here do. Custom slides are **not**
re-cut: liturgy and notices stay as typed.

**Other languages.** Yoruba letters with under-dots and tone marks (ẹ ọ ṣ, é, è) come across exactly; the tests check this letter by letter.

### Duplicates

A song counts as already in the library when it has

- the same title **and** the same CCLI number, or
- for a song with no CCLI number, the same title **and** the same first line of words.

Capital letters and extra spaces don't matter. Left-out songs are counted and listed in the summary. So if OpenLP itself held the same song twice, only one comes in, and the summary says which.

## 4. What is not brought across

- **Bibles.** MxU Slides has its own scripture, and Bible texts are licensed.
- **Images, media, presentations and themes.** Imported songs get your default look here.
- **Service files** (`.osz`). Rebuild this Sunday's order from the imported songs.
- **Song books and song numbers, topics, comments, a song's own theme, audio files linked to a song.** MxU Slides' song has no place for them.
- **The alternate title.** There is no field for one. Search finds a song by its title and its words.
- **Who wrote the words and who the music.** All the names go on one author line.
- **A custom slide's credits line.**
- **Colours, bold and italics** made with formatting tags. Songs take the look's styling.
- **"Temporary" songs** (ones OpenLP made up from a service file) are skipped on purpose.

## 5. What to check afterwards

1. Read the **To check** list in the summary. Each line is one of:
   - *could not be read (damaged XML)*: the song's row is damaged in OpenLP. Retype it with **New Song**.
   - *has no words*: OpenLP held a title with nothing under it.
   - *is not a formatting tag this importer knows, so it was kept as words*: something in braces, such as `{x}`, is on a slide. Open the song and delete it if it was a tag.
   - *its verse order names "v3", which the song does not have*: that step was left out of the order. Check the arrangement.
   - *a line that reads "Chorus" was taken as a section name*: someone typed a label inside a verse. Check that song's sections.
2. **Pick five songs you know well and compare them with OpenLP, side by side**: the words, where the slides break, and the order the sections are sung in. Include one with a bridge, one in Yoruba, and one the choir sang last Sunday.
3. Open a custom slide or two from the **OpenLP custom slides** folder.
4. Put one imported song live on the projector before Sunday.

If anything looks wrong on a real database, press **Copy** in the summary and keep the text: it is what whoever fixes the importer needs first.

## 6. How it works (for the learner)

The code is the project `src/MxU.Slides.OpenLpImport`. It has no windows in it, only logic, so tests can run it without the app. HOW-IT-WORKS section 63 walks through it; in short:

1. **`OpenLpSource`** finds the database files in whatever you chose, and makes **working copies** in a temp folder. A zip is checked first for file names that try to climb out of the folder ("zip slip"); one bad name refuses the whole zip. Your own files are only ever copied, never opened as databases, so they cannot be changed.
2. **`OpenLpDatabase`** opens a copy with SQLite and reads the rows. It first asks the file which columns it has, because OpenLP 2.0, 2.4 and 3.x differ, and uses only those.
3. **`OpenLpLyrics`** reads a song's words (a small XML document) into verses and slides: split marks, tags, plain-text old songs.
4. **`OpenLpSongMapper`** writes each song out as ChordPro text and hands it to `LyricTextImporter`, the code that already imports SongSelect and ChordPro files. That is why an OpenLP song ends up looking like any other song here. Then it turns the verse order into an arrangement.
5. **`OpenLpImporter`** runs the four steps for every song, leaves out duplicates, saves the rest, and collects the counts and warnings. One bad song becomes a line in the list; it never stops the others.

In the app, `MainViewModel.ImportOpenLpAsync` runs that on a background thread, and `Views/OpenLpImportWindow.cs` is the window.

**SQLite and the package used.** The app reads SQLite with the NuGet package `Microsoft.Data.Sqlite` 10.0.15 (MIT licence, from Microsoft). It brings `SQLitePCLRaw` 2.1.13 (Apache-2.0) and `e_sqlite3.dll`, a build of SQLite itself (public domain). The other choice was the copy of SQLite inside Windows (`winsqlite3`), which would ship nothing extra. The bundled one was chosen because it is the same SQLite on every PC, whatever the Windows version, and it is a single package reference. It adds about 2 MB. Their licence notices are in `SQLITE-NOTICES.txt`, which is placed beside the app. In the published single-file app the DLL travels inside `MxUSlides.exe` and is unpacked by .NET when the app starts, like the app's other native libraries.

## For PORTING.md

**Windows-only** (the Mac has no OpenLP import):

- `src/MxU.Slides.OpenLpImport/*` (`OpenLpSource`, `OpenLpDatabase`, `OpenLpLyrics`, `OpenLpSongMapper`, `OpenLpImporter`, `Testing/OpenLpSampleData`) and `tests/MxU.Slides.OpenLpImport.Tests`.
- `ViewModels/MainViewModel.OpenLp.cs`, `Views/OpenLpImportWindow.cs`, `Views/MainWindow.OpenLp.cs`, `Infrastructure/SelfTest.OpenLp.cs`.
- Nothing new is written into shared documents: an imported song is an ordinary `Presentation` whose origin is `lyricsFile` with the detail "OpenLP".

**Not done, not verified:**

- OpenLP import never tried on a real database.
- "Find OpenLP on this PC" looks only in `%APPDATA%\openlp\data`. A data folder moved in OpenLP's settings must be chosen by hand.
- Song books, topics, comments, alternate titles, author types, custom-slide credits and linked audio are not imported.
- A church's own formatting tag is recognised only when it appears both opened and closed in the song.
- OpenLP for macOS or Linux keeps the same files elsewhere; only a copied folder, file or zip was considered for those.
