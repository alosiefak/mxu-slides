# Phase 5: show flow

This part of Phase 5 makes a service run itself where the operator wants it to, and gives the
operator the controls a Sunday needs around that: slides that move on by themselves, music rows
that play, a transport strip for the live video, finer Clear buttons, timers for the whole
service, and hidden rows, looks and versions in the service list.

Everything here is a port of the Mac app. As elsewhere, the *rules* live in
`MxU.Slides.Core` (no WPF, unit-tested), and the app only wires them to the screen.

## 1. Slides that advance by themselves

A slide, or a whole presentation, can carry an `autoAdvance` (`AutoAdvance` in `Models.cs`):

- `delaySeconds`: move on after this long;
- `loopToStart`: after the last slide, go back to the first (a "Slide Show", like a walk-in
  announcement loop: ProPresenter and PowerPoint imports write this);
- `afterPlayback`: wait for the slide's video to end first, then the delay. A *negative* delay
  fires that many seconds *before* the video ends, so the next cue is up as it finishes.

A media item can carry one too: fired from a service row, it moves the service on to the next
row when its timer runs out or its video ends.

### Where the code is

| File | What it does |
|---|---|
| `Core/Automation/AutoAdvanceController.cs` | The whole state machine, ported from the auto-advance half of the Mac's `ServiceControls.swift` (`armAutoAdvance`, `autoAdvancePollTick`, `fireAutoAdvance`, `armMediaAdvance`…). |
| `Core/Scene/SlideSceneBuilder.AutoAdvance.cs` | `EffectiveAutoAdvance` (a slide's own beats its presentation's), `AutoAdvanceTarget` (next, wrap, or nowhere), `FillMediaId`. |
| `Core/Scene/ShowState.Flow.cs` | `SlideStepsCompletedAt`: when the live slide's builds have all been pressed and played. |
| `Core/Automation/MediaAutoAdvance.cs` | The negative-delay lead (already ported, now used). |
| `ViewModels/MainViewModel.AutoAdvance.cs` | The app's side: `IAutoAdvanceHost` (what's live, the video players, firing a slide or the next row) and the chip. |

### How it flows

1. `Fire` (in `MainViewModel.Live.cs`) lands a slide, then calls `ShowFlowSlideLanded`, which
   calls `AutoAdvance.SlideLanded(slide, presentation)`.
2. The controller cancels whatever was counting, and arms the new slide's timer: a one-shot
   `ShowTimer` on the **show clock** (`ShowTimeProvider.Shared`, its own thread). The timer's
   callback only posts back to the UI thread; the UI thread does the work.
3. If the slide waits for a video, it polls the video four times a second (also on the show
   clock), and the app pokes it the moment `MediaHub.VideoEnded` fires (`OnVideoEnded`).
4. When it fires, the controller asks the host to fire the next arranged slide, or slide 1 again
   for a loop, or, on the last slide of a service row, the next row that fires by itself
   (`ServiceRunOrder.NextFireable`: presentations and media; headers and music rows are walked
   past, as on the Mac).

### What interrupts a timer (as on the Mac)

- **Any slide landing** (Next, Previous, a click, a hot key, the timer itself) cancels both timers
  and arms the new slide's own. So pressing Next early just moves on, and the new slide counts
  from its full delay.
- **Builds**: a slide with clicks never presses for you. Its timer starts only after every click
  (and the exit press, if it has one) has been pressed *and has finished playing*. Taking a build
  back with Previous stops the count again (`StepsChanged`, from `Advance` in `Live.cs`).
- **Clear**: clearing the slide (or its Outs finishing) cancels its timer. Clear All, or firing
  media from the bin, cancels a media row's timer.
- **Hold**: clicking the chip holds the advance. The time left is kept and the chip says
  "Advance held"; clicking again carries on from there. If the video ends while held, that end is
  skipped and the slide stays.
- **Video loops**: a looping video counts *one pass* from when the slide went live.
- **A video that never opens** doesn't strand a walk-in loop: after 10 seconds it counts as ended.
- **A hand-off** (a look playing its Outs before the slide lands) pauses everything until it lands.

### Things learnt

- A slide's timer outlives its slide unless something stops it, and on Windows Next can land on a
  media or music row while a slide is still on screen. Left alone, that slide's timer would fire
  later and jump the show back. So firing a media or music row cancels the slide's timer.
- `MediaPlayer` can report a frame short of the end after it says it ended. The transport reading
  remembers where each player stopped when it raised `VideoEnded`, and counts that as the end
  (`LiveTransport` in `MainViewModel.Transport.cs`), so "finished" never depends on the last frame.
- **A stopped timer can still go off.** A timer on the show clock hands its callback to the UI
  thread. If the UI thread is busy at that moment (the self-test was saving a picture), the callback
  waits in line, and the operator can press Hold, or Next, before it runs. The first self-test run
  caught exactly that: Hold was pressed just as the timer came due, and the slide moved on anyway.
  Now every timer and poll checks, when its callback finally runs, that it's still the current one
  (`After` and `Every` in `AutoAdvanceController.cs`), and the test `HoldingAsTheTimeComesWins…`
  acts it out with a UI thread that only runs callbacks when told to.
- A header row with no colour of its own was drawn in a fully transparent colour, so its name was
  invisible (imported headers often have none). It now falls back to the muted grey, as on the Mac.

## 2. Music rows play

A service row can be an **Audio** row (one song) or a **Playlist** row: ProPresenter playlists
import their walk-in cues this way. Before, they did nothing.

- Selecting a music row shows a card over the slide grid (`Views/ServiceMusicCard.xaml`, the
  Mac's AudioFireView / PlaylistFireView): the name, LIVE while it plays, its songs (click one to
  play the playlist from there), time, and Play / Stop.
- Double-clicking it, **Go live**, or arriving on it with **Next** plays it. Arriving again on
  music that's already playing leaves it playing, so Previous past the walk-in music doesn't
  restart it.
- The slide on screen stays up under the music; the next Next moves on past the music row.
- It plays through the Music tab's `AudioController` (`PlayAudio`, `PlayAudioPlaylist`); nothing
  in the audio engine changed.

Code: `ViewModels/MainViewModel.ServiceMusic.cs` (`FireRow`, `PlayServiceMusic`, the card), and the
run-order walk in `MainViewModel.Live.cs` (`Positions` now has music rows, `FirePosition` fires them).

**Different from the Mac:** the Mac's Next walks slides only and its auto-advance skips music rows;
its music rows play from their card's Play button (or Return). On Windows, Next already stopped at
media rows, so it now stops at music rows too and plays them. Auto-advance still skips them, as on
the Mac.

## 3. The live video transport strip

Under the audience preview (`Views/LiveShowControls.xaml`, the Mac's MediaTransportStrip): play/pause,
a scrub bar, time gone, total and left, back to the start, a jump to near the end (30 s before it
unless you right-click to change it), and a click on the time to type one to go to.

Which video it controls is Core's `TransportTarget` (`Core/Media/MediaTransport.cs`): you look at one
layer at a time (Foreground Videos unless you pick another in the layer menu, or pin a video); on
that layer the video that started first wins, so a later fire never steals the strip. A looping
background only shows its time left until you pick it.

`MediaTransportState` is one reading of a player (length, position, playing, looping, rate, and
when it was read); every later moment is worked out from that, so nothing asks the player 30 times
a second. The strip, the stage display's video countdown and auto-advance all read the same thing.

**What this relies on from MediaHub** (the render work in this phase makes it honour trims and play
rate):

- `MediaHub.Transport(key)` reports the *pass*: position 0 is the item's in point, and the length is
  out − in. (Before that work lands, the pass is simply the whole file.) `LiveTransport` uses those
  values as they come; only the play rate, for how fast the time left runs out, comes from the item.
- `MediaHub.Seek(key, t)` takes a time in the pass, so 0 is the in point.
- `VideoEnded(key)` means "this pass reached its end or out point". It's raised at the end of every
  loop of a looping video too, and a non-looping video has already paused when it fires.

Auto-advance only ever looks at the video that asked to be waited for: a slide's own foreground
video (or a video filling one of its shapes), or a media row's own item. `VideoEnded` only makes it
check *that* video now instead of at its next poll, so a looping background's pass ends never move
anything (`ALoopingBackgroundNeverAdvancesTheSlide`, `ASlideWaitingForItsVideoIgnoresTheBackgroundLoopsPassEnds`).

### Firing a video again

A video player is shared and kept for a moment after its video leaves the screen. Firing the same
video again within that moment used to find the player paused at its end, so nothing played. Now,
as on the Mac, firing restarts it from the start of its pass, playing (`Core/Automation/VideoRestarts.cs`,
`RestartVideo` in `MainViewModel.Transport.cs`):

- firing media restarts it, unless it's background media (a loop that carries on under the slides);
- a slide landing restarts its own non-looping videos, and its foreground video background if that
  one had played out (one still playing from the slide before carries on);
- an overlay's non-looping videos restart when it goes up (`SetOverlay` in `MainViewModel.Overlays.cs`).

The stage display's video countdown now follows the Mac: it counts down the Foreground Videos
layer's video only; a background loop never counts down there.

## 4. Clear

The Clear section (`Views/LiveShowControls.xaml`, `ViewModels/MainViewModel.Clear.cs`, the Mac's
ClearChip):

- **Clear All (F1)** clears what its checklist ticks. Right-click it for the checklist. By default
  it leaves **Inputs** up, as on the Mac, so a camera under the slides survives a panic clear; the
  button says so. The checklist is this computer's (`ClearAllSkips` in `settings.json`), stored the
  Mac's way ("inputs,audio"); the rules are `Core/Scene/ClearAllScope.cs`.
- **Each function**: Slides F2, Media F3, Overlays F4, **Music F5**, Alerts F6, **Inputs**.
- **Each layer**: "Clear a layer" opens a button per layer, top of the stack first.
- Every button is **green while what it clears has something live**.
- The Local API's clear now goes through the same code, so clearing "audio" stops the music too.

## 5. Service tracking

`Core/Stage/ServiceTiming.cs` (the Mac's ServiceTimingController) keeps time from this computer's
own fires: when each row was first fired, which row is current, and when the first one was. From
that it makes ten timers (`Core/Stage/ServiceTrackingTimers.cs`): Item, Section, Service, Video and
Timeline, each Remaining and Elapsed, against the lengths planned on the rows (`duration`).

- They're published as permanent timers (`service_tracking.item.remaining`…) through
  `TimerController.SetSystemTimers`, so a **stage layout shows them** like any timer, by id.
- A running timer counts on by itself from its anchor, so the app republishes only when something
  is news (a fire, a service change, a video paused or moved): `ServiceTrackingTimers.Tracks`.
- The **Service Tracking** panel in the live column shows the item (big, amber then red as it runs
  out, "late by…"), the section, the service and what's next, with Clear.
- They keep out of the Timers list, and are never saved to `timers.json`.
- Local timing gives up by itself when the planned service would have ended (15 minutes past the
  last item's end if that's later), on the show clock.

**Different from the Mac:** the Mac defines the Timeline faces but leaves them idle; Windows fills
them from the song timeline that's playing. Like the Mac's, they're filed in the Timers board's
read-only "Service Tracking" folder (the board came with the operator panels: [operator.md](operator.md)).

## 6. The service list

- **Hide / Unhide**: right-click a row › Hide. It leaves the run order (Next, the stage, timelines
  already skipped hidden rows) and waits under **HIDDEN (n)**, with Unhide
  (`Views/HiddenServiceRows.xaml`). Hidden rows aren't numbered. Rules: `Core/Library/ServiceRunOrder.cs`.
- **Output preset** per row: right-click › Output preset › Keep Current Look, or a preset. The
  router already honoured it for slides; media rows now pass their row too (`FireMedia(item, rowId)`).
- **Service › Duplicate** and **Change date…** (a calendar: `Views/ServiceDateWindow.cs`).
- **Versions** (`Core/Library/ServiceVersions.cs`, the Mac's ServiceVersions.swift). A version
  isn't a copy: it's a short list of differences from Main (rows it swaps, moves, hides, adds), so a
  fix to Main reaches every version. **Each computer chooses which version it runs**, and the
  choice is written into the version's `stations`, so everyone sees who runs what. Service ›
  Versions (and the chip beside the service picker, once a service has versions): Main, each
  version, New, Rename, Delete. A row the running version changed wears **Changed**; right-click ›
  Use Main's undoes it. A Mac-made versioned service runs Main here until this PC chooses.

### How versions reach the rest of the app

`SelectedService` is the service *as this computer runs it*. With no version, that's the library's
own document, exactly as before. With one, it's a copy with the version applied
(`MainViewModel.ServiceVersions.cs` → `RunningView`), so the slide grid, Next, the stage, timelines
and the Local API follow the version without knowing versions exist.

That copy must never be saved over the library's document: edits go through `UpdateService(id,
change)`, which records the change in the running version (the Mac's `updateService`), or
`SaveService(service)` for code that changed `SelectedService` in place. The service-level fields
(name, date) and the shared row fields (a colour, a transition) still reach Main, as on the Mac.

Which computer is this? `Infrastructure/ThisComputer.cs` keeps an id, made once, in the
machine-local folder (never the roaming one), and the name of the version picked last, so next
week's service runs its "11am" too.

## Testing it

- Unit tests: `AutoAdvanceControllerTests` (timers, loops, holds, builds, video ends, media rows,
  looping backgrounds, and a busy UI thread), `AutoAdvanceRulesTests`, `VideoRestartsTests`,
  `ServiceRunOrderTests`, `ServiceVersionsTests`, `ClearAllScopeTests`, `MediaTransportTests`,
  `ServiceTrackingTimersTests`, and the `SlideStepsCompletedAt` cases in `ExitSemanticsTests`.
- Self-test: `Infrastructure/SelfTest.Show.cs` builds a throwaway service and drives all of the
  above in the real app. Pictures `70-show-*` to `79-show-*`.

### Things to try

1. In your *test* library, open a presentation's JSON and add `"autoAdvance": {"delaySeconds": 5,
   "loopToStart": true}`. Fire its first slide and watch the chip count down; click it to hold.
2. Give a slide a click build and a timer: the chip only appears once you've pressed through it.
3. Make a version of a service, hide a row in it, then pick Main and back again.

*The PORTING.md rows for this work are in [PORTING.md](../PORTING.md); this is the walkthrough. See also [HOW-IT-WORKS.md](../HOW-IT-WORKS.md).*
