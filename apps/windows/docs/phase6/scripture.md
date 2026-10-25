# Phase 6: the Bible tool

A sermon moves fast. The pastor says "turn with me to Romans 8", and the operator has a few
seconds to get the verse on the screen, then keep up verse by verse. This part of Phase 6 is a
Bible built into the app (the KJV, the WEB and the ASV), a window to look passages up and put
them on screen, and the code that turns a passage into ordinary slides.

The Mac app has no Bible of its own yet. Only one piece is a port: `ScriptureReference`, which
spots references inside pasted text. Everything else is Windows-only. It's built so the Mac
can still open anything it makes: a saved passage is an ordinary presentation.

## Using it

Press **Ctrl+B** (or **Bible** in the toolbar). Type into the box:

| Type | And you get |
|---|---|
| `John 3:16`, `jn3:16`, `Jn 3.16`, `John 3 v 16`, `john 3 16` | one verse |
| `Jn 3:16-18`, `1 Cor 13:4–8`, `John 3:16 to 18` | a range (hyphen or dash) |
| `John 3:35-4:2` | a range across chapters |
| `Psalm 23`, `Gen 1-2` | whole chapters |
| `Ps 23:1-3, 5`, `Rom 8:28,31-39` | pieces of a chapter |
| `John 3:16; Rom 5:8` | several passages |
| `I John 4:8`, `1st John 4:8`, `First John 4:8` | numbered books, Roman or not |
| `Jude 3` | a one-chapter book takes a bare verse |
| `John 3:16 WEB` | that translation, just this once |
| `love is patient` | a word search |

Then:

- **Enter** puts it on screen. The box keeps its text selected, so you can type the next
  reference straight away.
- **↓** in the box, **PageDown** (what a presentation clicker sends), or the main window's
  **Next** go to the next slide of the passage, and past its end to the next verse, across
  chapters and books. **↑** and **PageUp** go back.
- **Tab** takes the book the window offers while you type a name (`phil` → Philippians).
- **F1–F6** clear, exactly as in the main window.
- **Add to Service** and **Save** keep the passage as a presentation in the library.
- **Beside it: KJV** shows a second translation next to the first.
- **ⓘ** shows each translation's licence notes.

The right-hand side chooses the look: **Full screen**, **Lower third** or **Side third** (the
theme's three scripture designs), the theme, how many verses go on a slide, and whether to
show verse numbers and the reference line. The window remembers these on this PC.

## 1. The Bibles in the app

`src/MxU.Slides.Core/Scripture/Bibles/` holds three files and a list:

- `kjv.tsv.gz`, `web.tsv.gz`, `asv.tsv.gz`: one verse per line, `BOOK⇥chapter⇥verse⇥text`,
  gzip'd (about 1.3 MB each). Books use eBible.org's codes, `GEN` to `REV`; John is `JOH`
  and the Song of Songs is `SOL`.
- `bibles.json`: each translation's name, abbreviation, licence notes, the download it came
  from, and that download's SHA-256, so anyone can check the text is what eBible.org published.

They're **embedded resources** in `MxU.Slides.Core.dll` (see the `EmbeddedResource` lines in
`MxU.Slides.Core.csproj`). An embedded resource is a file baked into the DLL itself, so it
travels with the app and with the unit tests, and there's nothing to install or lose.

**Licences.** All three are public domain. Two notes travel with them, and the About window
shows them:

- The **KJV** is public domain everywhere except the UK, where printing it needs permission
  under Crown letters patent. In this text the translators' added words (printed in italics
  in a paper KJV) have lost their `[brackets]`.
- **"World English Bible" is a trademark** of eBible.org. You may use the text freely, but only
  unchanged under that name. Nothing here changes it.

### Making the files again

`apps/windows/tools/bible-convert.py` made them from eBible.org's "verse per line" (VPL)
downloads. To regenerate them, perhaps after eBible.org corrects a typo:

1. Download `eng-kjv2006_vpl.zip`, `engwebp_vpl.zip` and `eng-asv_vpl.zip` from the URLs in
   `bibles.json` into an empty folder of their own.
2. From the repository root run:

   ```powershell
   python -I apps/windows/tools/bible-convert.py <that folder> apps/windows/src/MxU.Slides.Core/Scripture/Bibles
   ```

   (`-I` keeps Python from running anything else that's sitting in the download folder.)
3. It prints each translation's verse count. Compare the new `sourceSha256` values in
   `bibles.json` with the old ones: a change means eBible.org changed its download. Run the unit
   tests (`BibleTests` checks the verse and chapter counts) and look at the diff before you commit.

The script writes the gzip files with a fixed timestamp, so re-running it on the same downloads
gives byte-identical files and git sees no change.

## 2. Reading what's typed

There are two readers, for two different jobs.

**`ScriptureReference.cs`** is a line-for-line port of the Mac's `ScriptureReference.swift`.
It **finds** references inside text: "see Matthew 5:3-12 for the list" contains
`Matthew 5:3-12`. It's one big regular expression built from the book names, and it splits
pasted scripture into verse and reference ("For God so loved the world (John 3:16)"). It
never looks anything up. Its tests are the Mac's.

**`PassageReference.cs`** **reads** a reference into book, chapters and verses. It's a small
hand-written parser rather than a regular expression, because it has to understand what the
numbers mean, not just where they are:

1. The text is cut at semicolons. Each piece starts with a book (`Rom 5:8`) or carries on in
   the last one (`John 3:16; 4:1`).
2. The book is found by `BibleBooks.Find` (`BibleBooks.cs`). Names become **keys**: lower case,
   no spaces or dots, and a leading `First`/`1st`/`I`/`1` all become `1`. So `1 Cor`, `I Cor.` and
   `1st Corinthians` are all `1cor…`. A name cut short counts when only one book starts that way
   (`Philipp` is Philippians, but `Phi` could be Philemon, so it doesn't).
3. What follows is cut into **tokens**: numbers, `:` or `.`, dashes (or the word `to`), commas.
   Any other word means it isn't a reference after all ("Psalm 23 love" is a search). Two
   numbers with only a space between are read as chapter and verse (`rom 8 28`), and a third as
   the range's end (`john 3 16 18`).
4. The tokens are read as **items**: `16`, `16-18`, `3:16`, `3:16-18`, `3:16-4:2`. The rule
   that makes `Rom 8:28, 31` work: once a chapter has been named, a bare number is a verse in it.
   Before that it's a chapter (`Psalm 23, 25`). A one-chapter book (Jude, Philemon…) takes
   a bare number as a verse.
5. A separator at the end means someone is still typing: `John 3:` reads as John 3, so the preview
   doesn't flicker while you type.

`PassageReference.Format` writes it back the printed way: `John 3:16–18` with an en dash,
`Romans 8:28, 31–39` with the book named once, `Psalm 23` but `Psalms 23–24`.

## 3. The Bible in memory

`Bible.cs` holds one translation as a jagged array, `text[book][chapter - 1][verse - 1]`. Looking
a verse up is three array indexes, with no searching. A verse the translation leaves out is a
`null` in its place.

- **Counts come from the data.** `ChapterCount` and `VersesIn` are the array lengths, so "John 3
  has 36 verses" is whatever the file says.
- **`Read(reference)`** walks each piece chapter by chapter and returns a `PassageText`: the verses
  found, in the order asked, plus the ones this translation hasn't got (`Absent`) and a calm
  `Problem` ("John has 21 chapters."). A range that runs past a chapter's end stops there
  (`John 3:30-40` is John 3:30–36), because that's what someone typing fast meant.
- **`After` and `Before`** step to the next or previous verse that exists, across chapters and
  books (Malachi 4:6 → Matthew 1:1).

### Verses left out on purpose

Some verses are in the KJV but not in the WEB or ASV, which were translated from older
manuscripts that don't have them. Acts 8:37 is the best known. That's a fact about the
translation, not a mistake, so `AbsentVerses.cs` says it that way:

> Acts 8:37 isn't in the WEB: the oldest manuscripts don't have it. The KJV has it.

It knows the sixteen such verses. It also knows one case of *renumbering*: the WEB puts the
end of Romans (16:25–27 in the KJV) at 14:24–26. Anything else just says it isn't there. The
window offers **Show it in the KJV**. A passage around a gap (`Acts 8:36-38` in the WEB) shows
the verses that are there and notes the gap, and its slide still reads "Acts 8:36–38".

## 4. Loading, off the UI thread

`BibleCatalog.cs` lists the translations (from the embedded `bibles.json`) and loads them:

- **Lazily.** Nothing is read until a translation is first asked for. Opening the Bible window
  asks for the usual one in the background (`Warm`), so it's ready before the first lookup.
- **Once.** Each translation sits behind a `Lazy<Bible>`, so two callers asking at once still
  read the file once.
- **On its own thread.** `LoadAsync` and `SearchAsync` run on a `WorkQueue` named "Bible", a
  dedicated thread. It's never the UI thread, and never .NET's shared thread pool. Show timing
  keeps clear of the pool (HOW-IT-WORKS §26), and a big search should never make a timer late.
- **At normal priority.** The show's own threads (the show clock, sound, capture) run above
  normal, so they still come first. These threads started at *below* normal. On a busy PC, the
  full self-test with other builds running alongside, the slides for 40 verses then took ten
  seconds instead of half a second: Windows gives a below-normal thread time only when nothing
  else wants it. The operator is waiting for a verse, so normal it is.

How does the answer get back to the screen? The work finishes a `TaskCompletionSource` made with
`RunContinuationsAsynchronously`. The UI code that `await`ed it resumes through WPF's
`SynchronizationContext`, which means on the UI thread, in turn with everything else. No locks
needed in the window.

## 5. Word search

`BibleSearch.cs` builds an **inverted index** the first time a translation is searched, on the
Bible thread (a few hundred milliseconds, once):

1. Every verse is cut into words (`Words`): lower case, with apostrophes dropped (`doesn’t` →
   `doesnt`) and possessives cut (`LORD’s` → `lord`).
2. Each word is reduced to a rough **stem** (`Stem`): one ending off (`-eth`, `-est`, `-ing`,
   `-ed`, `-es`, `-s`), then a final `e`. So `loved`, `loveth`, `loving`, `loves` and `love` are
   all `lov`, and a modern query meets the KJV's old forms. The stem doesn't have to be a real
   word, only the same for the query and the verse.
3. For each stem it keeps the list of verses that have it (its *postings*). For each verse it
   keeps its stems in order.

Then a search for "love is patient" goes like this:

- Each query word gets a **weight** from how rare it is: `log(1 + verses / verses with it)`. "is"
  is in thousands of verses and weighs little; "patient" is in a few and weighs a lot. Common words
  ("is", "the", "unto"…) weigh a quarter more lightly still, and never make a verse a candidate
  on their own.
- A verse scores the weights of the words it has, as a share of the total. It gets **+25** for
  having them all, **+60** for having them side by side in order (the phrase itself), or up to
  **+20** for how close together they sit. Shorter verses win ties, then Bible order.
- The **last word counts as a beginning while it's still being typed** ("love is pati" finds
  "patient"). Once you type a space after it, it has to match whole.
- The best results come back with where each telling word sits, so the window can show it in bold.

Typing fast starts many searches. Each new one cancels the last, and a result that arrives for text
you've since changed is dropped (the view model's `generation` counter).

## 6. A passage becomes slides

`ScriptureSlides.cs` turns a `PassageText` into a **`Presentation`**, the same kind of document a
song or a deck is. That's the whole compatibility story:

- **Nothing new in the document.** Each slide has two text boxes, named "Verse" and "Reference",
  with no position or style of their own. `ThemeSlideName` picks one of the theme's existing
  designs: "Verse + Reference", "Verse + Reference (Lower Third)" or "Verse + Reference (Side
  Third)" (every starter theme has all three: `Library/StarterPacks.cs`). The design's
  placeholders of the same names place and style them (HOW-IT-WORKS §4). Change the theme and
  the whole passage restyles. Open it on a Mac and it looks the same.
- **Verse numbers** are Unicode superscript digits (`¹⁶`), not a new "superscript" style. Every
  font can show them, and the Mac reads them as plain text. A no-break space keeps each number
  with its first word.
- **Sections** are the chapters ("John 3"), so the slide tiles are labelled like a song's.
- **Name and origin:** "John 3:16–18 (KJV)", origin `madeHere` with detail "Bible (KJV)". The
  Mac shows that as "Made in MxU Slides · Bible (KJV)".

A unit test serialises every layout's presentation and checks it against
`packages/schema/documents.json` field by field, the way the Mac's decoder would read it:
known fields only, every required one present, and every enum value one the Mac knows
(`SharedSchema` in `ScriptureSlidesTests.cs`).

### Splitting by length, so nothing overflows

A slide holds as many verses as fit the design's verse box at a **readable size**. That's the
design's own size, or no smaller than 85 % of it when the design shrinks text to fit. "Fit" is
measured with the outputs' own text layout: the app passes in `TextLayout.OverflowsFrame`, the
same measurer the slide grid's "text cut off" warning uses. So what fits here fits on the
projector.

The search for "how many verses fit" is careful with measurements, because each one lays the
text out properly:

1. A cheap **estimate** (half the font size per letter, lines 1.2× tall) guesses how many verses
   fit.
2. The real measurer then checks the guess and one more. Usually that's two measurements per
   slide. When the guess was off, it steps in growing strides and halves back.
3. A design that shrinks text is measured once at 85 % instead of searching sizes, which is the
   same question at a tenth of the work.

A new chapter always starts a new slide. A single verse too long for any slide (Esther 8:9) is cut
between words, at a comma or full stop when there's one near the end of the part, and each part
keeps the verse's reference. **Verses per slide** (1–4) caps the count too. Without a measurer
(the unit tests) the estimate alone decides.

The reference line on each slide names that slide's verses ("John 3:16–17 KJV"). A gap that's
only a verse the translation leaves out doesn't break the range. A gap you asked for does
("Romans 8:28, 31–32").

## 7. The window and going live

`Views/BibleWindow.cs` only draws and passes keys on. The logic is
`ViewModels/ScriptureViewModel.cs`, and the self-test drives it directly.

**Live without saving.** Enter makes the presentation and fires it through the same `Fire` every
slide uses (`MainViewModel.FireScripture`), so transitions, builds, looks, the stage display and
recordings all just work. But it isn't saved. Looking up twenty verses in a sermon shouldn't
leave twenty presentations in the library. That has one consequence: the main window's Next
can't find the live presentation in the library to move through it. So `Advance` (in
`MainViewModel.Live.cs`) has one new line, which asks the Bible tool first:

```csharp
if (StepScripture(direction)) return true; // a Bible passage: its next slide, then the next verse
```

`ScriptureViewModel.Step` plays the next slide of the passage, and past its end makes the next
verse (one verse takes a few milliseconds) and fires that. The box, the list and the preview
follow along, so the operator always sees where they are. **Add to Service** and **Save** turn
the passage into a real library presentation. The same passage saved twice is kept once.

**Two threads, so the window never stutters.**

- The **Bible** thread loads and searches (above).
- The **Bible slides** thread makes the slides: measuring, splitting and the slide pictures. WPF
  objects want a single-threaded apartment (STA) thread, so this `WorkQueue` is made STA. The
  app's `TextLayout` is safe to share between threads: its caches are locked and its brushes
  frozen. In the self-test, 40 verses take about half a second to make, and the UI thread is
  busy for about 10 ms of that.
- Enter waits for the slides of what's typed (`Ready`), so a fast typist never puts up the
  previous passage.

## Where to look

| What | File |
|---|---|
| Finding references in text (Mac port) | `Core/Scripture/ScriptureReference.cs` |
| Reading a typed reference, the books | `Core/Scripture/PassageReference.cs`, `BibleBooks.cs` |
| A translation in memory, lookups, next/previous | `Core/Scripture/Bible.cs` |
| Loading, the Bible thread | `Core/Scripture/BibleCatalog.cs` |
| Verses left out on purpose | `Core/Scripture/AbsentVerses.cs` |
| Word search | `Core/Scripture/BibleSearch.cs` |
| Passage → presentation, splitting | `Core/Scripture/ScriptureSlides.cs` |
| The data and its converter | `Core/Scripture/Bibles/`, `tools/bible-convert.py` |
| The tool's logic | `Windows/ViewModels/ScriptureViewModel.cs`, `MainViewModel.Scripture.cs` |
| The window | `Windows/Views/BibleWindow.cs` (with `BibleAboutWindow`) |
| Its settings on this PC | `Windows/Infrastructure/AppSettings.Scripture.cs` |
| Tests | `tests/MxU.Slides.Core.Tests/Scripture/` (ScriptureReference, PassageReference, Bible, BibleSearch, ScriptureSlides) |
| Self-test | `Windows/Infrastructure/SelfTest.Scripture.cs`, pictures `60-bible-…` to `66-bible-…` |

## Things to try

1. **Follow a sermon.** Open Ctrl+B, type `rom 8 28` (no colon needed: `WithSpacesRead` reads
   the space), press Enter, then PageDown five times.
2. **Find a verse you half remember.** Type `the lord is my sheph` and watch the results change as
   you finish the word. Then put `BibleSearch.Closeness` next to the ranking above.
3. **See the gap.** Pick the ASV and type `Mark 9:43-48`. Two verses are missing. Read
   `AbsentVerses.Explain`, then press *Show it in the KJV*.
4. **Make a long reading.** Type `Psalm 119`, choose *Lower third*, and look at the slide strip.
   Then change *Per slide* to 2.
5. **Take it to the Mac.** Add a passage to the service, export it as `.mxuslides`, and open it on a
   Mac. It's an ordinary deck there.

## Different from the Mac, and not done yet

- **The Mac has no Bible**, so everything but `ScriptureReference` is Windows-only. When the Mac
  gets one, its book codes and reference reading should be compared with these.
- **Two translations side by side** are shown in the window, not on the output. Slides with
  both would need text boxes placed by hand, as the designs have one verse box.
- **With the reference line off**, the lower third's reference plate (a shape in the design) stays,
  empty. Hiding it would need a different design.
- **The ASV's supplied words** keep their `[brackets]`, as eBible.org prints them (only the KJV's
  were removed, as approved).
- **No Local API or stage-display controls** for the Bible yet: a remote can't type a reference.
- **Search is English-only in its stemming.** It's tuned for these three translations.

## For PORTING.md

Rows to add when this is merged:

**Ported line by line**

| Swift | C# |
|---|---|
| `PresenterCore/…/Library/ScriptureReference.swift` | `MxU.Slides.Core/Scripture/ScriptureReference.cs` (`Matches`, `IsReferenceLine`, `SplitText`; the Mac's `split` is `SplitText`, because C# can't have a method and a nested type both called `Split`) |

**Windows-only**

- `Core/Scripture/` (`BibleBooks`, `PassageReference`, `Bible`, `BibleCatalog`, `AbsentVerses`,
  `BibleSearch`, `ScriptureSlides`) and the embedded KJV, WEB and ASV in `Core/Scripture/Bibles/`,
  made by `tools/bible-convert.py`: the Bible tool. The Mac has no Bible yet.
- `ViewModels/ScriptureViewModel.cs`, `MainViewModel.Scripture.cs`, `Views/BibleWindow.cs`,
  `Infrastructure/AppSettings.Scripture.cs`: the Bible window (Ctrl+B). `MainViewModel.Live.cs` →
  `Advance` asks `StepScripture` first, so Next moves through a live passage that isn't in the library.
- `Infrastructure/SelfTest.Scripture.cs`.

**Tests**

- `tests/MxU.Slides.Core.Tests/Scripture/ScriptureReferenceTests.cs` is the port of
  `PresenterCoreTests/ScriptureReferenceTests.swift`. The other four files there are Windows-only.

*See also [HOW-IT-WORKS.md](../HOW-IT-WORKS.md) and [PORTING.md](../PORTING.md).*
