# The service assistant (Phase 12)

An optional AI helper for the person running slides. It was built in two stages. Stage 1 made
the foundation and three features:

- **Suggest with AI** in Make Slides: the assistant reads a pastor's notes and says which lines
  are slides.
- **Clean up with AI** in the song editor: lyrics pasted from a website are sorted into Verse,
  Chorus, Bridge.
- **The scripture spotter** in the Bible window: it listens to the preacher and has each passage
  named waiting for you.

Stage 2 added the **Assistant window** (the Assistant button in the toolbar), with seven pages.
Four of them do their main job with no AI at all:

| Page | What it does | Needs the AI? |
|---|---|---|
| Ask | You type "next song"; it holds the matching action ready; Enter does it | Yes |
| Order of service | A bulletin or email becomes the rows of a service | No (*Read it*); yes for *Read with AI* |
| Slide checks | Finds slides that are hard to read; fixes two kinds safely | No; a second opinion from pictures is optional |
| Translate | A marked second-language copy of an announcements deck | Yes |
| Live captions | What is said, as a line of text on the stage or audience screens | No; translation is optional |
| Service report | What ran when, and what went over | No; "notes for next week" are optional |
| Sermon summary | A recording becomes a transcript on this PC, then a summary | No for the transcript; yes for the summary |

The Mac app has none of this. It is Windows only, and it writes nothing new into shared
documents: a note or song made with the assistant's help is an ordinary note or song.

> **Not verified against the real API.** Every test here talks to a stand-in server on this PC
> with scripted answers. No call was ever made to Anthropic: there is no key on the build PC and
> calls cost money. So the tests prove what the app sends and what it does with each kind of
> answer. They can't prove that the real model answers well, or that the real API accepts every
> field exactly as sent. The first real use needs a person watching the log
> (Settings › Assistant › What was sent and received…).

## Three rules the code keeps

1. **The AI suggests, a person decides.** Nothing the assistant makes reaches a screen without a
   click or the Enter key. A suggestion lands in a window: as rows in Make Slides, as text in the
   song editor's box (with **Put my text back**), as a reference in the Bible window's box.
2. **The show never depends on the internet.** With no key, no network or the switch off, the AI
   buttons are greyed and the app runs as before. Every job runs on the assistant's own thread.
3. **Scripture and lyrics come from the church's own files.** The assistant's answers have no
   field for verse text. Verses are read from the Bible built into the app (section 44 of
   HOW-IT-WORKS). Lyrics are the words you pasted, checked word by word.

## Trying it

1. **Settings › Assistant (AI).** Paste the church's Anthropic API key and press **Check and
   save**. The key is checked first with a call that costs nothing, then kept encrypted. The
   section then shows the key masked (`sk-ant-…mnop`), the on/off switch, the model, this month's
   use, and the log.
2. **Notes › Make Slides… › Suggest with AI.** The line beside the button guesses the cost before
   you press it. After it answers, each suggested passage is a row, exactly as with *Pick for me*:
   untick it, change its type or design, then **Make Slides**. The line then shows what the job
   really used.
3. **New Song › paste lyrics › Clean up with AI.** The box is replaced by labelled sections. The
   line beside the button lists anything left out (a website's clutter). **Put my text back**
   undoes it.
4. **Bible window (Ctrl+B) › Listen for scripture.** Pick the sound input the preacher's
   microphone is on. Each passage heard appears as a button and the newest is put in the box, with
   an amber flash. **Enter shows it.** It is never shown by itself.

5. **Assistant (toolbar) › Order of service.** Paste a bulletin and press **Read it**. Each line
   becomes a row; a song in your library gets a green "in your library", one that isn't is
   listed. **Add as a service** saves it. Nothing is saved before that.
6. **Assistant › Slide checks.** Open a presentation, press **Check the slides**. Where a row has
   a **Fix** button, the fix is safe (see below).
7. **Assistant › Service report.** Run a service (fire its rows), press Clear in the Service
   Tracking panel at the end, then open this page.
8. **Assistant › Ask.** Type `next song` and press Enter. Read the blue card. Enter again does
   it; Esc forgets it.

## How it works, piece by piece

Everything that talks to Claude or listens to speech is in a new project,
`src/MxU.Slides.Assistant`. The app's own code for it is `ViewModels/MainViewModel.Assistant.cs`
and `Views/Assistant/`.

### The foundation

**The key (`AssistantKeyVault.cs`).** Windows has a service called DPAPI that encrypts data so
only the same Windows user on the same PC can read it back. The key is kept that way in
`Assistant\key.bin` in the app's local data folder, as stream keys and the Planning Center token
are. It is never in `settings.json`, the library, a backup or a log. On the wire it travels only
in the request's `Authorization` header, which Anthropic's SDK adds; it is never part of a web
address.

**The switch (`AppSettings.Assistant.cs`).** `AssistantEnabled` is this PC's on/off switch, and
`AssistantModel` is the chosen model. `AssistantService.Unavailable` answers "may a job go at
all?" (switch on, key present). When the answer is no, `Ask` fails at once without touching the
network, and windows use the same answer to grey their buttons and say why in the tooltip.

**One job (`AssistantClient.cs`).** A job is an `AssistantRequest`: instructions that never
change, the part that does (this note, these lyrics), and a JSON Schema the answer must fit. The
client sends it with Anthropic's official C# SDK (the `Anthropic` NuGet package):

- *Adaptive thinking*: the model decides how much to think. An effort level (`medium` for jobs
  done before Sunday, `low` for the spotter) says how hard to try.
- *A fixed shape* (`output_config.format`): the API only returns JSON that fits the schema. Each
  schema lists the real values and nothing else, so an invented slide type can't come back. The
  code that reads a reply checks again anyway, because a test can't prove what the real API does.
- *Look-ups* ("tool use"): the model can ask the app to check something. The app runs the tool on
  this PC and sends the answer back, for up to six rounds. Stage 1 has one tool,
  `look_up_passage`, which says whether a reference exists in the church's Bible. It does not
  return the verse text.
- *A cache mark* on the instructions. Anthropic keeps a marked beginning of a request and charges
  a fraction for it next time, but only above a minimum length. Stage 1's instructions are short,
  so in practice nothing is cached yet. The mark is there for the later, longer jobs (the theme
  list and the library catalogue).

**When it fails (`AssistantModels.cs`).** Every failure becomes an `AssistantException` with
calm words for the operator. The SDK itself retries a request twice more when Anthropic answers
429 (busy) or 5xx (its own trouble). A whole job has a time limit, and **Cancel** stops it.

| What happened | What the operator reads |
|---|---|
| No internet | The assistant can't reach the internet. Everything else works as usual. |
| 401 or 403 | Anthropic didn't accept the API key. |
| 429 after three tries | Anthropic is busy just now. Try again in a minute. |
| 5xx after three tries | Anthropic's servers had a problem. |
| The answer failed a check | The assistant's answer couldn't be used, so nothing was changed. |

**Off the UI thread (`AssistantService.cs`).** Jobs run one at a time on a `WorkQueue` named
"Assistant" (HOW-IT-WORKS section 26), at below-normal priority. The window that asked awaits
the result and carries on drawing meanwhile.

**The log (`AssistantLog.cs`).** Every job is written to `Assistant\log.jsonl`: the words sent,
each look-up and its answer, the words received, the tokens and the cost. Settings shows it in a
window. Anything shaped like an API key (`sk-ant-…`) is blanked before it is written, in case a
key was pasted into a note.

**Prayer requests stay here (`PrayerPrivacy.cs`).** Notes often carry a list such as "Prayer
requests: Mary Okafor, surgery Tuesday". Before a note is sent, the lines of such a list are
replaced by `(private: not sent)`, and the window says how many were held back. A single line
that names someone to pray for is held back too. It errs towards holding back too much.

**Cost (`AssistantModels.cs`).** The API reports the tokens each job used. The app multiplies
them by Anthropic's list price and shows the result after every job, and a month's total in
Settings. Before a big job it shows a guess, at about one token for every three and a half
letters. These are estimates; the bill is on the church's Anthropic account.

| Model | In, per million tokens | Out |
|---|---|---|
| `claude-opus-5-5` (the default) | $4 | $20 |
| `claude-haiku-5-5` (only if you choose it) | $0.10 | $0.50 |

### Suggest with AI (`NoteSuggester.cs`)

This is built on the Make Slides port (HOW-IT-WORKS section 54), not beside it. *Pick for me*
works by marking passages in the note as "suggested"; the planner then turns marked passages
into slides. Suggest with AI does only the first half, and hands back the same thing: the note
with passages marked "suggested". From there the same planner, the same builder and the same
window run. That is why the two can't drift apart, and why what's saved is an ordinary note.

1. `Prepare` numbers the note's blocks and writes them out. Prayer-request lines are held back,
   and blocks you marked by hand are shown as taken.
2. The model answers with a list of passages: first block, last block, a kind, and for scripture
   a reference. The kinds are `title`, `scripture`, `points`, `quote` and `body`, which are the
   slide types a note's mark can already hold.
3. `Read` keeps only what is safe: block numbers inside the note, a kind from the list, no
   overlaps. Anything else in a reply is never looked at.
4. `Apply` marks the passages. A scripture passage that is **only a reference** ("John 3:16")
   gets its verse from the built-in Bible, in the translation the Bible window uses. The
   reference is read from the note by the app's own parser; the model's reference is used only
   when the note's can't be read. A scripture passage that already has words keeps the pastor's
   words.

One test gives the reply a wrong verse and a wrong reference on purpose. The slide still holds
the King James words for the reference in the note.

### Lyrics clean-up (`LyricsCleanup.cs`)

The model returns sections (a label from a fixed list, a number, the lines) and the order they
are sung in. The code turns that into the plain labelled text the lyric pipeline has always
read (`Core/Lyrics/Reflow`), so a chorus sung three times is written once and its label repeats.

**The words can't change.** `Read` cuts the pasted text and the answer into words at the
spaces, keeping capitals and punctuation. Then it walks the answer's words through the pasted
words in order. If a word of the answer isn't found, it was changed, added or moved, and the
whole reply is thrown away with a message naming the word. The model may leave words out
(a website's "Lyrics provided by…"), and those lines are listed for you to check.

### The scripture spotter (`Spotting/`)

**Listening stays on the PC.** Speech is turned into text by Windows' desktop speech recognizer
(SAPI, through Microsoft's `System.Speech` package), using the speech pack Windows already has.
Nothing is downloaded and no sound leaves the machine.

> **Different from the plan.** The plan named the newer `Windows.Media.SpeechRecognition`. Its
> free dictation is done by Microsoft's online service (the "Online speech recognition" privacy
> setting), so sermon sound would leave the PC. It also can't listen to a chosen input or to a
> file. The desktop recognizer does all three, so it was used instead.

**Its own process (`SpeechWorker.cs`, `SpeechListener.cs`).** The recognizer runs in a second
copy of the app started as `MxUSlides.exe --speech-worker`, with no window and no library. That
gives three things: Windows runs it at below-normal priority, so the show's threads get the
processor first; the app can measure exactly how much processor time recognition used; and if
the recognizer ever hangs or crashes, the show is untouched. The app sends it sound down a pipe
(mixed to one channel at 16 kHz) and reads back one line of JSON for each thing heard. If the
recognizer falls behind, old sound is dropped.

Two things learnt the hard way, both found by the self-test and neither by the first unit tests:

- *The recognizer treats a short read as the end of the sound.* A pipe hands over whatever has
  arrived, often less than was asked for. So `SoundPipe.Read` waits until it can hand over
  everything asked for.
- *The recognizer must not be made on the UI thread of a worker that then waits.* It hands its
  results to the thread that made it through that thread's message queue. In the worker, the UI
  thread was waiting for recognition to finish and never read its queue, so the processor was
  busy and nothing was ever heard. The worker now makes the recognizer on a plain thread, which
  has no queue, and results arrive directly.

**What it costs.** In the self-test, with the outputs running, recognition used 2.5 to 3.9
seconds of processor time over a 24-second sermon in two runs: 11% to 16% of one core, about 1%
of this 12-thread laptop. The strip in the Bible window shows the live figure both ways.

**Two listeners on one sound.** Free dictation is weak at chapter and verse: it wrote "john 316"
for "John three sixteen". So a second recognizer listens to the same sound with a small grammar
that knows only "book, chapter, verse". Measured here, the grammar gets the numbers right but
sometimes the wrong book, and dictation gets the book right but muddles the numbers. When both
read the same moment, `ScriptureSpotter` keeps the grammar's numbers if they agree on the book,
and dictation's book if they don't.

**Finding the reference (`SpokenScripture.cs`).** No AI is involved in this step. Spoken words
are rewritten into what the Bible window's parser reads: "chapter eight verse twenty eight"
becomes "8 verse 28", "first Corinthians" becomes "1 Corinthians", and "316" after John becomes
3:16 because John has no chapter 316. Only a book's full name followed by a number counts, since
"John", "Mark", "Acts" and "Numbers" are everyday words in a sermon. The result is checked
against the Bible.

**Only vague phrases go to Claude.** A sentence with no reference in it is sent to the assistant
only if it sounds like an allusion ("the story of the prodigal son", "as Paul wrote"). It goes
alone, with nothing else of the sermon, at most once every eight seconds, never twice, and never
if it names someone to pray for. The answer is a yes or no and a reference, which is checked
against the Bible and shown with a question mark as the assistant's guess. With the assistant
off or the internet down, the spotter simply carries on with what it hears itself.

**What you see (`Views/Assistant/SpotterStrip.cs`).** A strip at the top of the Bible window: the
Listen switch, the input, the last sentence heard, the processor use, and a button for each
passage. The newest is put in the Bible window's box unless you are typing something of your own
there. There is a flash and no chime, because a sound from this PC could come out of the hall's
speakers.

## Stage 2: the Assistant window

The window is `Views/Assistant/AssistantWindow.cs`. It only draws. The app's side of each job is
in `ViewModels/MainViewModel.AssistantTasks.cs`, and each job's thinking is one file in
`MxU.Slides.Assistant`, with no screen code in it, so it can be tested alone.

### What the assistant may look up (`LibraryFacts.cs`)

Stage 1 gave the model one look-up. Stage 2 adds three, shared by the jobs that need them:
`search_library`, `read_current_service` and `list_themes`.

Just before a job, the app takes a **snapshot** on the UI thread: the names and ids of what is in
the library, the open service's rows, and the timers. It reads them through the Local API's own
bridge (HOW-IT-WORKS section 15), so the assistant sees what a remote control sees. The look-ups
then run on the assistant's thread against the snapshot and never touch the live library.

Two things follow. The model can only name a song or lower third that a look-up returned. And
the code that reads its answer checks every id against the same snapshot, so a made-up id goes
nowhere. Names and ids leave the PC when a look-up is answered; slide words, lyrics and files
do not.

### Ask: typed requests (`TypedRequests.cs`)

The Local API has a table of routes: `fireOverlay`, `advance`, `clearAll`, `fireSlide`, and so
on. The app turns every route that *controls the show* (31 of them on this build, plus the two look-ups) into a tool the
model can call. Routes that create, change or delete documents are left out.

The important part is what a tool does when the model calls it: **nothing**. It checks the
call, keeps it as a `ReadyAction`, and tells the model "held ready, nothing has happened yet".
The window shows the action on a blue card with the model's one-sentence summary and, under it,
the exact route and inputs. Then:

- **Enter** calls the route's own handler, exactly as a remote control would, with the
  "control" permission and no more.
- **Esc** forgets it.
- A second Enter within six tenths of a second of the card appearing is ignored, so a double
  press can't run something nobody has read.
- Only one action is held for one request.

### Order of service (`ServicePlanReader.cs`)

Two readers give the same result, a `Plan`.

*Read it* needs no AI. One line is one row. A line in capitals or ending in a colon is a header.
A line that is a Bible reference is a reading. A line whose title is a song in your library (or
that starts "Song:" or "Hymn:") is a song. A start time (`10:05`) or a length (`5 min`) gives the
row its planned length.

*Read with AI* sends the text (prayer-request lines held back first) and gets rows back in a
fixed shape: a kind from `header`, `song`, `scripture`, `item`, a title, and for a reading a
reference. There is no field for lyrics or verse text.

Either way the app then does the same three things:

1. **Songs are matched to the library** with the matcher the Planning Center import already
   uses (`PlanSongMatcher`). A song that isn't there, or that the library has two of, is listed
   under "Songs to add or link". No song is ever made.
2. **A reading is named by the app's own parser** ("Jn 3:16-18" becomes "John 3:16–18"). The
   verses come from the Bible window on the day.
3. **Add as a service** saves an ordinary service: headers, rows that play your songs, and plain
   rows for everything else. A Mac opens it like any other service.

### Service report (`ServiceReport.cs`)

Service tracking (HOW-IT-WORKS section 36) already knows the moment each row is first fired, but
forgets it when the service ends. Stage 2 writes each of those moments to a small file,
`Assistant\service-runs.jsonl`, in the app's *local* data folder. It is not in the library, so
nothing new is shared with a Mac.

The report is arithmetic on that file: a row ran from its fire until the next row's fire; "over"
is how far that is past the row's planned length, once it is more than 30 seconds. Rows that
were planned and never fired are listed. Pressing **Clear** in the Service Tracking panel marks
the end. If nobody presses it, the last row has no length, and the report says so rather than
guess.

**Notes for next week** is the only AI part. It sends the report's text (row names and times)
and shows up to five notes underneath, labelled as the assistant's suggestions.

### Slide checks (`SlideChecks.cs`)

For each slide the app builds its picture twice: once as data (which text boxes, where, what
colour, what size), and once drawn small *without its words*, to measure what is behind each
box. Then plain arithmetic decides:

| Finding | How it is decided | Fix offered? |
|---|---|---|
| Hard to read | The web's contrast ratio between the text colour and the average colour behind it is under 3 (under 1.6 if the text has a shadow, outline or band) | No. Colour is the designer's choice |
| Busy background | Brightness behind the box varies a lot, and the words have no shadow, outline or band | No |
| A lot to read | More than 45 words or 8 lines in one box | No. Wording is the pastor's |
| Cut off | The words don't fit the box (the same test as the slide grid's warning badge) | **Shrink to fit**, if the box has its own text style |
| Off the slide | The box hangs over the slide's edge | **Move onto the slide**, if the box's place is the slide's own |
| Small text | Under about 36 points on a 1080-high slide | No |

The two fixes change one existing setting each and save the deck. There is no Undo entry for
them: to go back, move the box or untick shrink-to-fit in the editor.

**Ask the assistant to look too** is optional. It sends pictures of up to eight slides (those
with findings first) and shows the notes that come back as an opinion. It changes nothing.

### Translate (`SlideTranslator.cs`)

Each text box goes out numbered and comes back by number, so an answer can only fill a box that
was sent. The result is a **copy**, named "… (Spanish · AI translation: check before use)", filed
under "Translations", with the original words kept in each slide's notes for a bilingual reader
to check against.

- **Scripture is never sent.** A slide that holds a Bible passage or reference is held back
  whole and stays as it is in the copy. The Bibles built into the app are English (KJV, ASV,
  WEB), so there is no real translation to take another language's verses from. The window says
  which slides were left, before and after.
- **Songs are refused** before anything is sent. Lyrics are licensed language by language.
- A prayer request on a slide is not sent; that box stays in the original.

### Live captions (`Captions.cs`)

Captions use the same speech process as the scripture spotter. Each sentence it hears goes up as
a **live alert** (the channel the stage message uses), on the stage screens, the audience
screens or both. An alert is on screen now and is written into no document, so captions add
nothing to the shared library. If another alert is up (a nursery call), captions leave it alone.

Translation is optional: each finished sentence is sent alone and shown a second or two later.
A sentence that names someone to pray for is shown as heard and not sent. If the assistant
falls three sentences behind, or there is no internet, sentences go up as heard.

> **A judgement call to know about.** The first rule says nothing the AI makes reaches a screen
> without a keypress. Translated captions bend it: the operator's switch covers every sentence
> after it, and nobody reads each one first. The page says so in words, and suggests the stage
> screens first. Captions *as heard* involve no AI.

Captions and the spotter each start their own speech process if both are on, so the processor
cost is doubled (about a tenth of one core each, measured with a synthetic voice).

### Sermon summary (`SermonSummary.cs`, `Infrastructure/SermonSound.cs`)

1. Windows' own decoders (Media Foundation, through the NAudio package the app already uses)
   turn the recording into a temporary 16 kHz sound file.
2. The speech worker reads that file through (`MxUSlides.exe --speech-worker --wav <file>`), with
   free dictation only, and reports each sentence with its time.
3. Only then, and only if you press **Summarise with AI**, the transcript's *text* is sent.

**Quotes are checked.** The model gives each quote with the transcript line it starts on. The
app looks for the quote's words, in order, around that line. A quote that isn't there word for
word is thrown away (the window says how many were), and the time shown is the transcript's
own. Punctuation and capitals may be added; a word may not change.

Windows' recognizer is weak on real preaching. In the self-test, Windows' own synthetic voice
saying "Grace is not something you earn" was heard as "Greece is not something you learn". A
summary is only as good as the transcript under it, so both need a person's read.

## What was tested, and how

- **Stage 2: 17 more unit tests** (`StageTwoTests.cs`, 100 in all) and **23 more self-test
  checks** with pictures 151 to 163 (`SelfTest.AssistantTasks.cs`), again with the stand-in
  server. They cover: a typed request held ready, done on Enter, forgotten on Esc, and an
  invented id refused; a bulletin read with and without AI and saved as a service; the report
  made from real fires with no request sent; the checks finding and fixing the planted faults;
  the pictures really being in the second-look request and not in the log; the scripture slide
  never sent and never changed; captions heard from a synthetic voice and shown on the audience
  scene; a recording transcribed through the real decoder and worker; a false quote dropped. The full self-test passed with them: 1,073 checks, none failed or skipped.
- **83 unit tests** in `tests/MxU.Slides.Assistant.Tests`, against the stand-in server: what a
  request carries; the key in a header and never in an address or the log; each failure's words;
  retries; cancel; no network; the schemas' values; a tampered verse; tampered lyrics;
  prayer-name stripping; spoken references; the spotter's rules.
- **Real speech, twice.** Two of those tests have Windows' own voice speak a short sermon and
  play it through Windows' offline recognizer, once from a file and once down a pipe in small
  pieces. They are skipped on a PC with no English speech pack.
- **The self-test's Assistant section** (28 checks, pictures 140 to 150) drives the real windows
  with the stand-in server, and plays the spoken sermon into the real speech process at the speed
  of speech. On this PC it spotted Romans 8:28, John 3:16 and 1 Corinthians 13:4–7, sent only
  "remember the story of the prodigal son" to the stand-in assistant, and showed John 3:16 on
  Enter. The full self-test passed with it: 1,050 checks, none failed or skipped.

**What that does not cover.** A real microphone, a real preacher, a noisy hall, accents and music
under speech were not tested: the only voice was a synthetic one, which is the easiest case.
Expect a real sermon to be heard less well. The sound-card path (`AudioInputSource` into the
spotter) was not run with a device. Processor use was measured with the self-test's outputs
running, not with a real projector, stream and NDI together.

**What stage 2's tests do not cover.** Nothing was sent to the real API, so how well Claude
reads a real bulletin, picks an action, translates or summarises is unknown; so is whether the
real API accepts the picture blocks and the 33 tool definitions exactly as sent. Captions and
the transcript were tried only with Windows' synthetic voice, for about ten seconds; a
forty-minute recording, a video file (.mp4) and a real voice were not tried. The slide checks'
thresholds were set by reasoning and four planted slides, not by a room and a projector. A
background picture that has not finished loading when a slide is measured reads as black.

## Not built yet

| Piece of the plan | State |
|---|---|
| Order of service from a bulletin or email | Built in stage 2. A PDF or Word file is read as text (pictures in a scanned bulletin are not read) |
| Slide checks | Built in stage 2: local checks, two safe fixes, and an optional look at pictures |
| Translation of announcement and point slides | Built in stage 2. Scripture is left untranslated because no non-English Bible is built in |
| Live captions | Built in stage 2 as a live alert. Not built: captions burned into the stream only, a caption box in a stage layout (a new box kind can't be written into a shared layout), sharing one speech process with the spotter |
| Typed requests ("put up the giving lower third") | Built in stage 2. Not built: a keyboard shortcut for the Ask box; requests that need two actions |
| The after-service report and the sermon summary | Built in stage 2. The report has no chart and is not emailed |
| Undo for a slide check's Fix | Not built; undo it by hand in the editor |
| Whisper as a more accurate recognizer | Not built (it needs a model download, which needs the user's yes) |
| The spotter backing off when the outputs' frame timing slips | Not built; it runs at below-normal priority and shows its processor use |
| Tools for the library, themes and the current service | Built in stage 2 (`LibraryFacts.cs`) |
| Suggest with AI choosing a design or lower third per passage | Not built; the church's design map chooses, as for Pick for me |
| Opening a `.docx` or PDF straight into Suggest with AI | Already works: import the file as a note first (section 54) |
| The spotter in French, Spanish, German or Italian | Not built; the grammar and the number words are English |
| A fallback model when Claude declines a request | Not built; a decline is reported and nothing changes |

## For PORTING.md

Ported line by line: nothing. The Mac has no assistant.

Windows-only:

| C# | What |
|---|---|
| `MxU.Slides.Assistant/AssistantClient.cs`, `AssistantService.cs`, `AssistantModels.cs` | Jobs to Claude through Anthropic's C# SDK: adaptive thinking, a fixed JSON shape, tool look-ups, retries, a time limit, costs; one at a time on their own thread |
| `MxU.Slides.Assistant/AssistantKeyVault.cs`, `AssistantLog.cs`, `PrayerPrivacy.cs` | The API key under DPAPI; the log of what was sent and received; prayer-request lines held back |
| `MxU.Slides.Assistant/NoteSuggester.cs` | Suggest with AI: a note's passages marked "suggested" for the existing planner; bare references filled from the built-in Bible |
| `MxU.Slides.Assistant/LyricsCleanup.cs` | Pasted lyrics labelled and re-broken, with a word-by-word check |
| `MxU.Slides.Assistant/Spotting/*` | The scripture spotter: spoken references, the two-recognizer rule, the speech process and its pipe |
| `MxU.Slides.Assistant/Testing/FakeAnthropicServer.cs` | The stand-in for Anthropic's API that the tests and the self-test use |
| `Views/Assistant/*`, `ViewModels/MainViewModel.Assistant.cs`, `Infrastructure/AppSettings.Assistant.cs`, `Infrastructure/SelfTest.Assistant.cs` | Settings › Assistant, the log window, the Bible window's spotter strip, and the self-test section |
| `MxU.Slides.Assistant/LibraryFacts.cs` | The snapshot of library names and ids the model may look up: `search_library`, `read_current_service`, `list_themes` |
| `MxU.Slides.Assistant/TypedRequests.cs` | Typed requests: the Local API's control routes as tools that hold an action ready instead of running it |
| `MxU.Slides.Assistant/ServicePlanReader.cs` | Order of service from pasted text or a file, with and without AI; songs matched with `PlanSongMatcher` |
| `MxU.Slides.Assistant/ServiceReport.cs` | The running record of fires (`Assistant\service-runs.jsonl`, local) and the report made from it; optional notes |
| `MxU.Slides.Assistant/SlideChecks.cs` | Readability checks by arithmetic (contrast, busy background, amount of text, cut off, off the slide), two safe fixes, optional vision pass |
| `MxU.Slides.Assistant/SlideTranslator.cs` | A marked second-language copy of a deck; scripture and songs never sent |
| `MxU.Slides.Assistant/Captions.cs` | Live captions as a live alert; optional sentence-by-sentence translation |
| `MxU.Slides.Assistant/SermonSummary.cs`, `Infrastructure/SermonSound.cs` | A recording transcribed on the PC (`--speech-worker --wav`), then summary, checked quotes and post drafts |
| `Views/Assistant/AssistantWindow.cs`, `ViewModels/MainViewModel.AssistantTasks.cs`, `Infrastructure/SelfTest.AssistantTasks.cs` | The Assistant window's seven pages, the app's side of each job, and their self-test |
| `LocalApi/OpenApiDocument.cs` (`RequestSchema`, `PathParameters` made public) | A route's body shape with its reference followed, for the typed-request tools |

Not ported yet, not done, not verified (add to that list):

| What | Why |
|---|---|
| Any call to the real Anthropic API | No key on the build PC, and calls cost money; every test uses a stand-in server |
| The spotter with a real microphone and a real sermon | Only Windows' synthetic voice was used |
| How well the real model does stage 2's jobs (reading a bulletin, choosing an action, translating, summarising, judging slides from pictures) | Never run against the real API |
| Captions and the sermon transcript with a real voice, a long recording or a video file | Only a ten-second synthetic voice in a .wav was used |
| The slide checks' thresholds in a real room | Set by reasoning and four planted slides |
| Translated scripture | No non-English Bible is built in; scripture slides are left as they are |
| Whisper; a caption box in stage layouts; Undo for a slide Fix; a shortcut for the Ask box | Not built |

*See also [HOW-IT-WORKS.md](../HOW-IT-WORKS.md) sections 26, 44 and 54, and [upstream-2026-11/notes.md](../upstream-2026-11/notes.md).*
