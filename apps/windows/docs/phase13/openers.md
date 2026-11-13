# Phase 13: the opener and montage maker

Most churches open a service with a few short videos: a countdown on the screen as people find
their seats, announcements going round, a bumper for the sermon series, now and then a montage of
photos from camp. They're usually bought, or made by whoever knows a video editor. This phase
makes them inside the app. A volunteer picks a kind, picks a look, fills in a short form, watches
the live preview and presses **Make**; an ordinary MP4 lands in the Media bin.

The Mac app has nothing like it, so this is Windows-only code. The result is a plain video, so a
Mac sharing the library just sees a video (section 7).

## 1. Using it

**Media › Make an opener…** (beside *Motion background…*) opens the window.

- **Along the top**, the four kinds:
  - **Series bumper**: a title and subtitle (and a picture or logo if you like) that animate in
    and out over a motion background. 5 to 15 seconds.
  - **Countdown**: a clock running down from 1 to 10 minutes, with a line above ("Service starts
    in") and one below, ending on a "Welcome" card for five seconds if you want one.
  - **Montage**: your photos and video clips, one after another with a slow move and a dissolve,
    cut on the beats of a music track. Optional title cards first and last.
  - **Announcement loop**: cards (a title, details, perhaps a picture), each up for a few
    seconds, going round with no visible join.
- **On the left**, five **looks**: Clean, Bold, Serif, Broadcast and Minimal. Each tile is a
  still of *your* opener in that look. A look is fonts, layout and the way words move, so every
  look works over every background.
- **In the middle**, the **preview** plays what will be made, in real time. Drag the slider under
  it to look at any moment. Under that, **Background**: any of the eleven motion backgrounds in
  any palette (or leave it on the look's own).
- **On the right**, the form for the kind you picked.
- **Make** draws every frame. A bar shows the frame, the speed and the time left; **Cancel** stops
  it and nothing is added. You can close the window: it carries on, and the notice bar shows its
  progress. The video goes in the Media bin's **Openers** folder.

**Edit opener…** (right-click a tile made this way) opens the window on that opener's recipe.
**Replace “…”** makes the same item again, so everything using it changes; **Save as New** keeps
the old one. Next week's series title is one box to retype.

If something is on the screens, the window says so. Making an opener then shares the PC with the
show. It's drawn at low priority so the show comes first, but a long one is best made afterwards.

## 2. Three steps: a recipe, a plan, a picture

The code is in three layers, and only the last one touches pixels.

| Step | What it is | Where |
|---|---|---|
| **Recipe** | What the volunteer filled in: the kind, the look, the words, the files | `Core/Openers/OpenerRecipe.cs` |
| **Plan** | The recipe laid out in time: how long, where each shot falls, and *what is on screen at any moment* | `Core/Openers/OpenerPlan.cs` |
| **Picture** | One moment drawn | `MotionTemplates/Openers/OpenerRenderer.cs` |

**`OpenerPlan.FrameAt(seconds)` is a pure function.** Given a time it returns an `OpenerFrame`:
where the background's loop is, which montage shots are showing (with their opacity, zoom and
pan), which blocks of words (each line with its opacity, offset and scale), and how much black is
over the top. No pixels, no clock, nothing remembered from the frame before. That is the same idea
as the animation engine (HOW-IT-WORKS section 14) and the motion backgrounds (section 47), and it
buys the same things:

- frame *i* is drawn at *i* ÷ frame rate whether the PC is fast or slow, so a slow PC makes
  exactly the same video, just more slowly;
- the preview and the final render ask the same function, so the preview can't lie;
- the tests can check "the clock reads 0:08 at 2.5 seconds" or "the loop has no seam" without
  drawing anything.

## 3. The four kinds, in time

**Bumper.** Black, then the background fades up (half a second), the words arrive, hold, leave,
and it fades to black. Lines follow one another a quarter of the move apart.

**Countdown.** The clock is `CountdownText(remaining)`: it **rounds up**, as a kitchen timer does.
"5:00" shows for the whole first second and "0:01" for the whole last one, so the count reaches
zero exactly when the time is up. The video is as long as the count, plus five seconds when
there's a Welcome card. The figures each sit in a cell as wide as the widest figure, so the clock
doesn't shuffle sideways as "1" changes to "0".

**Announcement loop.** A loop is seamless when the frame *after the last* is the *first*. Two
things make sure:

- The change from one card to the next happens at the **end** of each card's time: the words
  leave, then the next card's arrive. So when the video ends, the first card has just fully
  arrived, which is exactly how frame 0 finds it.
- The motion background makes a **whole number of turns** over the loop's length. (For the other
  kinds it just keeps its own 20-second turn.)

The tests check both the description (`Frame(FrameCount)` equals `Frame(0)`) and the pixels (the
picture drawn for frame *N* is frame 0's, byte for byte).

**Montage.** The time from the end of the opening card to the start of the closing one is shared
among the pictures by `OpenerPlan.PlanShots`:

- every picture is used at least once; with more time than pictures (a long song, a few photos)
  they come round again in order;
- shots aim for four seconds, and are shorter when there are too many pictures for that;
- each cut moves to the **nearest beat** that keeps both its neighbours at least half the even
  length. With no music, or music with no clear beat, the cuts stay even.

The montage is as long as its music (or the length you chose, if that's shorter; ten minutes at
most). Each dissolve straddles its cut, so the picture is half changed exactly on the beat. Each
shot makes one of four gentle moves in turn (push in, drift right, pull out, drift left), never
more than 8 % of zoom, so a face near the edge of a photo isn't lost. A video clip plays from its
start and isn't zoomed. The music fades out over the last second and a half.

## 4. Finding the beat: `Core/Openers/BeatDetector.cs`

The simplest method that works on songs with drums or a strummed guitar, an "energy onset"
detector:

1. **Loudness.** Every 5 ms, the loudness of the last 20 ms is measured, on a log scale (so a hit
   in a quiet verse counts as much as one in a loud chorus).
2. **Onsets.** A beat is a sudden *rise* in loudness. Each step keeps only how much louder it is
   than the one before, less the local average. What's left spikes at each drum hit.
3. **Tempo.** If the spikes repeat every *L* steps, sliding the pattern along by *L* lines it up
   with itself. (The name for this is *autocorrelation*.) The best *L* between 60 and 180 beats a
   minute is the beat's length, and how well it lines up is the **confidence**. A pad, a held organ
   note or speech lines up with nothing: no clear beat, so the montage cuts evenly instead.
4. **Where the beats are.** A comb of teeth one beat apart is slid along to where it catches the
   most spikes. Then each beat in turn is nudged to the spike nearest to where it was expected, so
   a band that drifts a little is still followed.

Sound is fed in pieces, so a five-minute song is never in memory at once. The tests make their own
music: click tracks at 90, 100, 120, 128 and 140 beats a minute, one under hiss with quieter
off-beats, and three things with no beat (a held note that swells, plain hiss, silence).

## 5. Drawing a frame: `MotionTemplates/Openers`

It lives in the motion templates' project because that's where **Skia** already is (the graphics
library that draws the Lottie templates). No new package was added.

`OpenerRenderer.Render` draws an `OpenerFrame` bottom to top:

1. **The motion background**, by Phase 6's `MotionRenderer`. For a full-size video it's drawn at
   half size and enlarged. The backgrounds are soft by design, so nothing is lost, and it's a
   quarter of the work.
2. **A montage's pictures and clips** (`OpenerPictures`). A phone photo is far bigger than the
   screen, so each is decoded once, turned the right way up (phones store a sideways picture plus
   a note saying which way to turn it), and shrunk to just bigger than it will be drawn. A picture
   whose shape is close to the screen's is cropped to fill it. One whose shape isn't (a portrait
   photo) is shown whole over a blurred, dimmed copy of itself, so there are no black bars. Only
   the last eight are kept in memory. A clip's pictures come from `VideoClipReader`.
3. **The words.** Each line is drawn **once** into a small picture of its own, shadow and all (a
   "sprite"), and then just placed, faded, moved and scaled frame by frame. Drawing text and its
   blurred shadow thirty times a second would cost far more. The fonts are the ones the app ships
   (Phase 6), read from the library's `fonts` folder. A title too long for three lines is set
   smaller. A line with a character the look's font hasn't got goes in a Windows font that has.
4. **Black** on top, for the fades at each end.

**Bands.** Enlarging a full-screen picture smoothly is the slow part of a frame on one thread. So
a big picture is cut into twelve bands and each of the crew's threads draws one. Each band is a
canvas of its own over the same pixels (the motion templates do the same). That took a 1080p
bumper from 12 to about 27 frames a second.

## 6. Writing the video: `OpenerVideoMaker`

It follows the motion backgrounds exactly (`Broadcast/Library/MotionBackgroundMaker.cs`): the same
H.264 writer (`RenderedVideoWriter`), the same `.partial` name until the file is complete, the
same two threads (drawing frame *i* + 1 while the encoder takes frame *i*) passing three reusable
pictures, all below normal priority.

The one new thing is **sound**. `RenderedVideoWriter` can now be given a sample rate, and then
takes stereo sound with `WriteAudio`. It's encoded to AAC by the recorder's own encoder and packed
into the file beside the pictures. Each frame carries the stretch of music that plays during it,
counted in whole samples, and each AAC packet is stamped by counting packets (every one is 1,024
samples). Nothing is read from a clock, so sound and picture can't drift apart. A file with no
sound is written exactly as before.

The music is read by `MusicFileReader` (`MxU.Slides.Audio/Decoding`), a small public face on the
audio engine's own file decoder. Anything the Music tab plays can be used. It's read twice: once
for its beats, then again as the video is written.

## 7. Staying friends with the Mac

Nothing new goes into a shared document (HOW-IT-WORKS section 41).

- The video is an ordinary `MediaItem`: an H.264 MP4 in the folder "Openers". An announcement
  loop is a looping background (the bin's *Loop* kind). The other three are videos that play once.
- The **recipe** is a Windows extra: `Windows extras/opener/<media item id>.json`, where the Mac
  never looks. It's saved with a fingerprint of the video file. If the file is later replaced by
  something else, the fingerprint no longer matches and the recipe is quietly forgotten, because
  it no longer describes the video.
- A background left on "the look's own" is saved as just that (blank), so when you open the
  opener again and change its look, the background follows.
- Pictures, clips and music are named in the recipe by their full path on this PC. They aren't
  copied into the library. If one has moved when you make the opener again, the form says it can't
  be found and it's left out.

## 8. Speed on this PC

Intel Core i5-10500H (6 cores), Release build, 1920 × 1080 at 30 frames a second, with another
engineer's work running on the PC at the time:

| Kind | Frames a second | So |
|---|---|---|
| Bumper (Gradient Flow) | 25 to 29 | an 8-second bumper takes about 9 seconds |
| Countdown (Light Rays) | 20 to 23 | a 5-minute countdown takes about 7 minutes |
| Announcement loop (Gentle Waves) | 21 to 22 | a 1-minute loop takes about 1½ minutes |
| Montage with music | about 50 | a 3-minute montage takes under 2 minutes |

So an opener over a motion background takes about as long to make as it plays; the window says so
for long ones. The slower backgrounds (Starfield, Liquid, Geometric Drift) will take longer.

## Where to look

| What | File |
|---|---|
| Recipe, looks | `Core/Openers/OpenerRecipe.cs`, `OpenerLooks.cs` |
| The plan in time, the shot plan, the clock | `Core/Openers/OpenerPlan.cs`, `OpenerFrame.cs` |
| Beats | `Core/Openers/BeatDetector.cs` |
| Drawing | `MotionTemplates/Openers/OpenerRenderer.cs`, `OpenerPictures.cs` |
| The video file, filing, the recipe in Windows extras | `MotionTemplates/Openers/OpenerVideoMaker.cs` |
| Sound in a drawn video; reading a clip's pictures | `Broadcast/Media/RenderedVideoWriter.cs`, `VideoClipReader.cs` |
| Reading music | `Audio/Decoding/MusicFileReader.cs` |
| The app | `Views/OpenerWindow.cs`, `Views/MainWindow.Openers.cs` (button and tile menu), `ViewModels/MainViewModel.Openers.cs` (the job), `Rendering/OpenerPreviewer.cs` |
| Unit tests | `tests/MxU.Slides.Core.Tests/Openers/OpenerTests.cs`, `tests/MxU.Slides.MotionTemplates.Tests/OpenerVideoTests.cs`, `tests/MxU.Slides.Broadcast.Tests/RenderedSoundTests.cs`, `tests/MxU.Slides.Audio.Tests/MusicFileReaderTests.cs` |
| Self-test | `Infrastructure/SelfTest.Openers.cs`, pictures `op-…` |

## Things to try

1. **Make a countdown.** Countdown, look Bold, 5 minutes. Drag the preview's slider to the end to
   see the Welcome card. Make it, then right-click its tile › *Edit opener…* and change the look.
2. **Watch the seam.** Make an announcement loop with two cards of three seconds each and play it
   in the Media bin. Then read `AnAnnouncementLoopHasNoSeam`.
3. **Cut a montage to a song.** Add ten photos and a song with drums. The line under the preview
   says "cut on the beat (… a minute)". Try a quiet piano piece: "cut evenly".
4. **Add a look.** Add a line to `OpenerLooks.All` with other fonts from `assets/fonts`. It
   appears in the window, and the tests that run over every look check it too.
5. **Change a move.** In `OpenerPlan.Card`, make the `Rise` entrance travel further. Set
   `MXU_OPENER_PICTURES` to a folder and run `dotnet test --project tests/MxU.Slides.MotionTemplates.Tests`:
   the test `PicturesToLookAt` writes full-size frames of every kind and look there.

## Not done, and not verified

- **No real music or real photos were used**, here or in the tests: the tests make click tracks
  and coloured pictures. The beat detector has not been tried on a recording of a band. Expect it
  to do well with drums and to say "no clear beat" for quiet pieces; a song it reads at half or
  double speed still cuts on beats, just fewer or more of them.
- **The preview is silent.** You hear the montage's music only in the finished video.
- **Nobody has listened to a finished montage.** The tests check its file has a sound track of
  the right length, not how it sounds.
- **Words are set simply**: left to right, one glyph per letter, no kerning pairs. Arabic, Hebrew
  and Indic scripts will not join or order correctly.
- **HEIC photos** (an iPhone's default) can't be read by Skia; they show as missing. Export them
  as JPEG first.
- **A clip's own sound is not used**, and a clip longer than its shot is cut off, not trimmed by
  choice. A clip shorter than its shot holds its last picture.
- **4K and 60 fps** are in the recipe and the renderer but not in the window: it always makes
  1080p at 30.
- **A motion background and an opener can be made at once** if you start the background second
  (the opener's Make refuses while a background is rendering, but not the other way round). Both
  finish; both are slower.
- No free-form editor, by design: you choose among looks, you don't move things about.

## Different from the Mac

The Mac has no opener maker. Nothing here changes what the Mac reads.

## For PORTING.md

Windows-only (no Swift twin; add under "Windows only"):

| C# | What |
|---|---|
| `MxU.Slides.Core/Openers/*` (`OpenerRecipe`, `OpenerLooks`, `OpenerPlan`, `OpenerFrame`, `BeatDetector`) | The opener maker's logic: recipes, five looks, what is on screen at any moment, the montage's shot plan, the countdown clock, beat detection |
| `MxU.Slides.MotionTemplates/Openers/*` (`OpenerRenderer`, `OpenerPictures`, `OpenerVideoMaker`) | Drawing an opener's frames with Skia over a motion background; H.264 MP4 with AAC sound; filing into the library with the recipe in Windows extras (`opener`) |
| `MxU.Slides.Broadcast/Media/VideoClipReader.cs` | A clip's pictures read in order, not in real time (the picture half of `MediaTranscoder`) |
| `MxU.Slides.Audio/Decoding/MusicFileReader.cs` | A public face on `AudioFileDecoder`: a music file as plain stereo samples |
| `MxU.Slides.Windows/Views/OpenerWindow.cs`, `Views/MainWindow.Openers.cs`, `ViewModels/MainViewModel.Openers.cs`, `Rendering/OpenerPreviewer.cs` | Media › Make an opener…, Edit opener…, the live preview and the looks' stills, the job |

Changes to files already listed:

- `Broadcast/Media/RenderedVideoWriter.cs` (Windows-only, motion backgrounds row): an optional
  AAC sound track (`audioSampleRate`, `WriteAudio`). Silent videos are written as before.
- `Broadcast/Media/MediaTranscoder.cs` (ported): `OpenVideo`, `ReadVideoFormat`, `CopyPicture` and
  `VideoFormat` went from private to internal so `VideoClipReader` can share them. No behaviour
  changed.
- `Views/MainWindow.MotionBackgrounds.cs`: one line, so a media tile's menu also gets "Edit opener…".

Not ported yet / not done: nothing from the Mac (it has no twin). See "Not done, and not verified"
above for this feature's own limits.

*This is the walkthrough; see also [HOW-IT-WORKS.md](../HOW-IT-WORKS.md) and [PORTING.md](../PORTING.md).*
