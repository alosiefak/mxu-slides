# The service assistant (Phase 12, stage 1)

An optional AI helper for the person running slides. This stage builds the foundation and three
features:

- **Suggest with AI** in Make Slides: the assistant reads a pastor's notes and says which lines
  are slides.
- **Clean up with AI** in the song editor: lyrics pasted from a website are sorted into Verse,
  Chorus, Bridge.
- **The scripture spotter** in the Bible window: it listens to the preacher and has each passage
  named waiting for you.

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

## What was tested, and how

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

## Not built yet

| Piece of the plan | State |
|---|---|
| Order of service from a bulletin or email | Not built |
| Slide checks from pictures of slides (vision) | Not built |
| Translation of announcement and point slides | Not built |
| Live captions | Not built |
| Typed requests ("put up the giving lower third") | Not built |
| The after-service report and the sermon summary | Not built |
| Whisper as a more accurate recognizer | Not built (it needs a model download, which needs the user's yes) |
| The spotter backing off when the outputs' frame timing slips | Not built; it runs at below-normal priority and shows its processor use |
| Tools for the library, themes and the current service | Not built; stage 1's jobs don't need them |
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

Not ported yet, not done, not verified (add to that list):

| What | Why |
|---|---|
| Any call to the real Anthropic API | No key on the build PC, and calls cost money; every test uses a stand-in server |
| The spotter with a real microphone and a real sermon | Only Windows' synthetic voice was used |
| The rest of Phase 12 (order of service, slide checks, translation, captions, typed requests, the report and summary) | Stage 2 |

*See also [HOW-IT-WORKS.md](../HOW-IT-WORKS.md) sections 26, 44 and 54, and [upstream-2026-11/notes.md](../upstream-2026-11/notes.md).*
