# Back up another computer (MxU PR #12)

Sunday morning, the booth computer freezes in the middle of a song. With a **backup computer**
on the same network, the screens carry on: the backup has been doing everything the booth did,
fire for fire, and when the booth stops answering it takes over where the booth was. Either
computer can be a Windows PC or a Mac.

## Trying it

On the **booth** (the computer that runs the service):

1. Settings › Network & API: turn on *Let phones, tablets and other apps on this network control
   the show* (the backup talks to the booth through it). Settings › Backup has a **Turn On the
   API** button for this too.
2. Settings › Backup shows a four-digit **code**.

On the **backup**:

3. Settings › Backup › **Make This the Backup…**. The computers running MxU Slides on this
   network are listed (or type the booth's address, e.g. `192.168.1.20:6980`). Pick the booth,
   type its code, choose:
   - *While it runs*: **Show the same thing live** (the backup's screens show what the booth's
     show), or **Stay ready, show nothing** (clear screens, but it keeps reading what the booth
     shows, and puts that up the moment it takes over, videos and music where they would be now).
   - *If it stops*: **Take over after 3 seconds**, or **Wait for Take Over**.
4. **Start Backing Up**. A bar across the top says *Backing up BOOTH-PC.* with a green dot.

What happens then:

- Every fire on the booth (a slide, a build step, media, an overlay, a clear, an alert, a timer, a
  combo, an output preset, music, a video's play/pause/seek) runs on the backup too.
- The booth's library items and media files are copied to the backup (when they're both Windows:
  see below), now and every five minutes.
- While it follows, the backup's own MIDI, Scheduler and remotes wait (they'd fight the booth), and
  slide actions that drive gear the booth already drove (MIDI out, starting the stream) are held.
- If the booth stops answering for three seconds, or freezes (it answers but its app stopped
  moving), the backup takes over: the bar turns blue, and its MIDI, Companion and Scheduler answer.
  The booth, when it wakes, hears that and steps down (an orange bar: *BACKUP took over.* with
  **Take Back**).
- **Hand Back** on the backup gives the booth its say again. A booth that only dropped off the
  network (same run of the app) and comes back within a minute is handed back to by itself.
- A booth that is closed normally tells its backup it **quit**, which is no crash: nothing takes
  over.

## How it works

**The booth tells.** Each fire is published on a new Local API topic, `fires`
(`LocalApi/Follow/ApiFireFeed.cs`). A fire carries a number (`seq`), the time, its source (the
operator, MIDI, the Scheduler, a timeline…) and, if it was set off by another fire (a slide's
actions, a combo's steps), that fire's number as its `cause`. The backup replays only fires with no
cause: their effects follow from them on the backup by themselves. In the app, one line in each
place that fires (`Fire`, `Step`, the clears, `FireMedia`, `SetOverlay`, `FireAlert`,
`RunActionCombo`, `ActivateOutputPreset`, the music and video controls, the timers' new `Acted`
event) calls `Fired(...)` from `MainViewModel.Follow.cs`; a slide and a combo use
`FiredWithEffects`, which makes everything until the end of the method that fire's effect.

**The backup follows.** `Follow/FollowController.cs` keeps a WebSocket to the booth
(`LocalApi/Follow/LocalApiClient.cs`): it subscribes to `fires`, pings every second, and the pong
carries the booth's **beat**, a counter its UI thread moves four times a second. Pongs that stop
mean the booth is gone; pongs whose beat stands still mean its app froze. The client reconnects
from half a second, doubling to 30 seconds, ten times, then gives up and watches the network for
the booth to come back. Every update is read on the UI thread with `await`, so nothing blocks it.
On connecting, the backup **catches up**: it reads the booth's `/v1/status` and fires only what
differs (`FollowReplay.CatchUp`).

**Who is in charge** is a number (`FollowRole`, `Core/Follow/FollowLogic.cs`): each takeover or
hand-back counts one up, and the higher number wins, so a booth that wakes from a freeze and hears
a higher number steps down. The rules for taking over on its own (`TakesOverOnItsOwn`) and handing
back (`HandsBackOnReturn`) are ported from the Mac with its tests, as are the banners and the
Settings lines.

**Pairing** (`FollowPairing`): the booth's code is four digits; five wrong tries retire it until a
new one is made. The right code gets the backup an *Operate* key named "Backup: its name" (it shows
in Settings › Network & API › Manage keys). The backup keeps the key encrypted for this Windows user
(DPAPI, in `follow-keys`), as the Mac keeps it in the Keychain; the pairing itself is in
`follow.json` in this PC's local folder, never the shared library.

**Finding each other** (`LocalApi/Follow/DnsSd.cs`): the booth announces `_mxu-slides._tcp` on the
network with its station id and name, through Windows' own DNS-SD (`DnsServiceRegister` and
`DnsServiceBrowse` in dnsapi.dll, Windows 10 and 11). It's the same protocol as a Mac's Bonjour, so
Macs and PCs see each other. No extra package.

**Copying items** (`Core/Follow/FollowCopy.cs`): the booth lists its items with a version each
(`GET /v1/follow/files`); the backup fetches only what changed, the media files it lacks (checked by
their SHA-256), and takes away copies the booth no longer has. A Mac's items are Automerge
documents and a Windows library is JSON files, which neither can read from the other yet. So a
Windows booth offers its JSON only to a backup that asks for it (`?json=1`, which a Windows backup
sends and a Mac ignores), and a Windows backup skips a Mac's Automerge items with a note in
Settings. Copying only ever reads from the booth: its library is never changed.

**Firewall.** The first time the API turns on, Windows may ask whether MxU Slides can use the
network. Allow it on private networks: the backup connects to the booth's API port. Settings ›
Backup says so.

## Trying it with a real Mac

This has been tested between two copies of the Windows app on one PC, and against the Mac's own
tests for the messages on the wire. It has **not** been run against a real Mac yet. To try it:

1. Put the Mac and the PC on the same network (the same Wi-Fi or switch; guest networks often stop
   computers seeing each other). Both run a version of MxU Slides with MxU PR #12 or later.
2. Get the same items onto both: export the library's songs and presentations as `.mxuslides`
   files from one and import them on the other. (Items aren't copied between a Mac and a PC yet.)
3. On the one that will be the booth, turn the API on (on a PC: Settings › Network & API; the Mac
   has the same switch in its Settings) and open Settings › Backup to see its four-digit code. On
   a PC, allow MxU Slides through the Windows firewall on private networks when asked.
4. On the other, Settings › Backup › Make This the Backup…, pick the booth from the list (or type
   its address and API port, e.g. `192.168.1.20:6980`), type the code, and start.
5. Check, in this order: the bar says *Backing up …* with a green dot; a slide clicked on the booth
   shows on the backup; Clear All clears both; **Take Over** on the backup turns the booth's bar
   orange; **Hand Back** returns it. Then quit the booth normally (nothing should take over), and
   last, pull the booth's network cable (the backup should take over after three seconds).
6. Do it once each way round: a PC backing up a Mac, and a Mac backing up a PC.

If the booth isn't listed in step 4 but typing its address works, the two computers can talk but
discovery (Bonjour / DNS-SD) is being blocked, usually by the firewall or the Wi-Fi router.

## Different from the Mac

- **Items between a Mac and a PC aren't copied** (the library formats differ until Phase 10's
  sync). The show itself follows both ways; the backup needs the same items to replay them, so
  copy the library once by hand (`.mxuslides` files) when pairing a Mac with a PC.
- A Windows backup copies JSON as it is (the booth's copy wins); the Mac merges both computers'
  edits.
- *Start Stream Here* (a backup that took over from a streaming booth) isn't offered yet.
- The unconnected-screens warning (the Mac names screens with no output on the backup) isn't
  ported: Windows' screen setup is different.
- An alert typed on the booth (not one of its presets) replays here as its message; the Mac's
  bridge refuses it.

## For PORTING.md

Ported line by line:

| Swift | C# |
|---|---|
| `LocalAPI/FollowMode.swift` | `LocalApi/Follow/FollowMode.cs` |
| `LocalAPI/APIFireFeed.swift` | `LocalApi/Follow/ApiFireFeed.cs` |
| `LocalAPI/LocalAPIClient.swift` | `LocalApi/Follow/LocalApiClient.cs` |
| `LocalAPI/BonjourAdvertiser.swift`, `BonjourBrowser.swift` | `LocalApi/Follow/DnsSd.cs` (dnsapi) |
| `LocalAPI/LocalAPIServer.swift`, `APIWebSocketHandler.swift`, `APIEventBus.swift`, `WireModels.swift`, `OpenAPIDocument.swift` (follow parts) | `LocalApiServer.cs`, `ApiWebSocketSession.cs`, `ApiEventBus.cs`, `WireModels.cs`, `OpenApiDocument.cs` |
| `LocalAPIController.followRoutes` | `LocalApi/Follow/ApiRouteTable.Follow.cs` (`IFollowApi`) |
| `PresenterCore/Library/FollowLogic.swift` | `Core/Follow/FollowLogic.cs` |
| `Sources/FollowController.swift`, `FollowPairingSheet.swift`, the Backup settings | `Windows/Follow/FollowController.cs`, `Views/Follow/*`, `ViewModels/MainViewModel.Follow.cs` |
| Tests: `FollowModeTests`, `FollowPlumbingTests`, `FollowLogicTests` | `tests/MxU.Slides.LocalApi.Tests/FollowModeTests.cs`, `tests/MxU.Slides.Core.Tests/Follow/FollowLogicTests.cs` |

Windows-only:

| C# | What |
|---|---|
| `Core/Follow/FollowCopy.cs` | Copying a Windows booth's JSON items to a Windows backup (the Mac's is Automerge) |
| `ActionRouter.Holds`, `TimerController.Acted`, `AudioBusStatus.EntryId` | Hooks the follow code needs in ported classes |
| `ThisComputer.Name` override (`MXU_SLIDES_COMPUTER_NAME`) | Names the self-test's second copy |

Not ported yet:

| Swift | Why |
|---|---|
| `PresenterCore/Library/FollowCopy.swift` (`mergeCopy`, Automerge saves), `FollowCopyTests` | Needs the Automerge library (Phase 10) |
| `Follow.unconnectedScreens` | Mac screen defaults; Windows outputs differ |
| Start Stream Here | Streaming hand-off not wired yet |
| `FiresTopicSweepTests` (source sweep over the Mac app) | Swift-source test; the C# fire points are covered by the self-test |

*This is the walkthrough; see also [HOW-IT-WORKS.md](../HOW-IT-WORKS.md) and [PORTING.md](../PORTING.md).*
