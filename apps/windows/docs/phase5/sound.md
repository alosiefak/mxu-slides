# Phase 5: sound routing and the music manager

Until now every sound the Windows app made came out of the Windows default device: the
music, because nothing told the music engine otherwise, and video sound, because WPF's video
player can't be told otherwise. A church usually wants the music on the sound desk's channels
3–4, and the video's sound maybe on the desk too, a little late so it lines up with a slow
projector. This phase makes that possible, and adds the music manager: making and editing
playlists, and looking after the songs.

## What you can do now

- **Screens & Settings › Sound** (below the video volume):
  - **Music plays on** and **Video sound on**: the system default, any audio output, or
    "No device". Video sound can also stay **Same as the music**.
  - **Audio outputs**: named places sound goes. Each is a device, a pair of its channels (or
    one channel, mono) and a **playout delay** (0–2000 ms). **Add Output…** makes one for a
    device, or one per channel of a multi-channel interface. An output whose device is
    unplugged says **Not connected**.
  - **Music fades**: fade in and out on play, pause and stop (and how long), and the room's
    crossfade between songs.
  - Everything applies at once and is saved in this PC's `settings.json` (it names this PC's
    devices, so it isn't in the library).
- **The Music tab**:
  - Click a tile to play or stop it. **Right-click a song** for Play, **Add to Playlist ›**
    (or **New Playlist…**), **Rename…**, **Replace Music File…** and **Delete**.
    **Right-click a playlist** for Play/Stop, Show Songs, Edit Playlist…, **Repeat ›**,
    **Shuffle**, **Crossfade ›**, Rename… and Delete.
  - The panel on the right lists the songs of the playlist playing now (or the one you last
    clicked), the playing one marked. Double-click a song to play the playlist from there;
    right-click it to move it or take it out.
  - Above the list: the speaker (mute all music), the **Fade** and **Crossfade** buttons
    (small popups, as on the Mac), and **Manage…**.
- **Manage…** opens the music manager: playlists and songs on the left; the chosen
  playlist's name, Repeat, Shuffle, crossfade and songs (**+ Add Song**, ↑ ↓, ✕) on the right.

## How it works

### Where sound goes: outputs, mixes and the routing table

The Mac's model is copied exactly, so a later mixer (Phase 8) only needs to add screens:

- An **output** (`src/MxU.Slides.Audio/Routing/AudioOutputInventory.cs`) is a named device,
  channel pair or mono channel, and a delay. There is always at least one ("Main Output").
- A **mix** (`Routing/AudioMixInventory.cs`) is something heard together. Its first output id
  is where it plays; `"default"` means the system default and `"none"` means no device.
  There is always the **main** mix: the music plays on it.
- **Video sound** plays on the *media mix*: the main mix unless you choose otherwise. Choosing
  an output for video sound makes a second mix called "Video Sound" and sends it there; that
  is exactly how the Mac stores the same choice, so its mixer will read it back.
- `Routing/AudioRoutingTable.cs` answers "where does this mix play?" (`RoutingForMix`): the
  output's device, its pair (or mono channel) and its delay, as an `AudioRouting`. It is a port
  of the Mac's `routing(forMix:)`, including two of its rules:
  - a pair the device hasn't got (channels 5–6 on a stereo device) falls back to 1–2;
  - **an unplugged device falls back to the system default**, and the sound moves back when
    the device returns. The table keeps the device list and re-reads it when Windows says
    something was plugged in or out, then raises `Changed`.

`settings.json` gains a `Sound` section in the Mac's own field names:

```json
"Sound": {
  "outputs": [ { "id": "6F…", "name": "Desk 3-4", "deviceUID": "{0.0.0.00000000}.{…}", "channelOffset": 2, "delayMs": 40 } ],
  "mixes": [ { "id": "main", "name": "Main", "outputIds": [ "6F…" ] } ],
  "fadeEnabled": true, "fadeSeconds": 1.5, "crossfadeSeconds": 3
}
```

### The music

`AudioController` (the Audio function) already asked "where does this playlist play?"
through `RoutingFor`, and nobody had answered. `ViewModels/MainViewModel.Sound.cs` now does
(`ConfigureMusic`): `RoutingFor` asks the routing table, and `MixLevels` says whether the mix
reaches a device ("No device" keeps the music off the speakers but in recordings of the main
mix). When the routing changes, `OnSoundRoutingChanged` saves it and calls
`ReapplyAllRouting` **on its own thread** (`MxU sound routing`): moving a bus to another device
waits for Windows to open it, which must never freeze the screen.

### Video sound

WPF's `MediaPlayer` (which `Rendering/MediaHub.cs` uses for every video) always plays sound on
the Windows default device. So for a video that should be heard:

1. `MediaHub.Video` still makes the `MediaPlayer` for the picture, but **muted**, and calls
   `TakeSound` (`Rendering/MediaHub.Sound.cs`), which hands the file to `VideoSound`.
2. `Audio/Video/VideoSound.cs` makes one `VideoSoundPlayer` per playing video, on its own
   thread (opening a file and a device takes a moment).
3. `Audio/Video/VideoSoundPlayer.cs` plays the file's audio track through our own engine:
   - a **decode thread** reads it with Media Foundation (`AudioFileDecoder`, which reads MP4
     and MOV sound as well as music files) into a two-second ring (`StemRing`);
   - an **`AudioOutputHost`** (the same one the music uses) pulls 10 ms blocks for the chosen
     device and channels; each block comes through a **varispeed** (the multitrack's), then
     the output's **delay** (`AudioDelayLine`), then the level;
   - with no device, or one unplugged, the host keeps the clock running silently, so the
     sound is still in step when a device appears.

**Keeping it with the picture.** The picture and the sound now run on two clocks, so they must
be kept together:

- 25 times a second (and at once on a pause, play, seek, restart or loop) the hub reads each
  player's `Position` and stamps it with the show clock: a `VideoClockSample`.
- Each block the sound plays, it notes which file second it was at and when that block will be
  heard: a `SoundClockReading`.
- `Audio/Video/VideoClockFollower.cs` compares the two and decides:
  - **Hold** while the picture isn't moving (paused, still opening, buffering after a seek):
    silent, with a 10 ms fade so nothing clicks;
  - **Land** when the picture starts moving or jumps: "be at second *x* at time *t*". The
    player works out where that is in the block it is playing, skips to the exact frame, or,
    when that sound isn't decoded yet, asks the decode thread to seek a little ahead
    (`SeekLead`) and starts on the right frame when the picture gets there;
  - **Steer** otherwise: play up to 0.5% faster or slower until the gap closes. Two clocks that
    drift apart (the sound card's crystal and the video's) never need a jump; the unit test
    `TwoClocksThatDriftApartStayTogetherWithoutLanding` runs ten minutes of drift.
- A loop, a restart or a seek tells the player where it jumped to, so it decodes from there
  straight away (`Discontinuity(to)`), and the sound is ready when the picture moves.

**Other speeds and trims.** At another play rate the sound is stretched without changing its
pitch (`Audio/Video/TimeStretch.cs`, WSOLA: overlapping 21 ms pieces, each nudged to where it
best continues the last), as the Mac's AVPlayer does. The rate is read from the player's
`SpeedRatio`, so whatever sets it is followed. `VideoSoundOptions` carries a cue's in and out
points; no sound is played past the out point.

**Streams and recordings** still get video sound: the program audio is captured from this
app's own output (`ProgramAudioTap`), which now includes the video sound players. The
self-test records the program audio while a video plays and checks the file has the sound.

**A file we can't read** (no audio track, or a codec only WPF's player has) is given back to
the `MediaPlayer`, which then plays it on the default device as before.

### The music manager

- The rules are Core's: `src/MxU.Slides.Core/Library/PlaylistLibrary.cs`, a port of the Mac's
  `AppModel` playlist functions (`createPlaylist`, `addToPlaylist`, `insertIntoPlaylist`,
  `handlePlaylistDrop`, `removePlaylistEntry`, `movePlaylistEntries`) and
  `Playlist.effectiveKind`. Each change is applied to a **copy** of the stored playlist and the
  copy is saved (`Update`): the music engine reads playlists on its own thread, and must never
  see one half-changed.
- `ViewModels/MainViewModel.Music.cs` wraps them for the screens, then calls
  `PlaylistDocumentChanged` so a playing playlist picks the change up at once (a new Repeat,
  a song taken out: the one playing finishes, then it moves on).
- `Views/MusicBin.xaml` is the Music tab, `Views/MusicManagerWindow.cs` the manager, and
  `Views/MusicMenus.cs` the right-click menus and the fade popups they share.
- **Replace Music File…** (`AudioImporter.ReplaceFile`) copies the new file in as an import
  does and points the song at it. The song keeps its id, so every playlist and service that
  uses it plays the new file.

### Things learnt along the way

- **WPF's video clock moves in steps of one screen refresh** (16 ms at 60 Hz). The self-test
  logs a few readings ("moved/elapsed ms: 16/16 16/16 …"). That is smooth enough to follow;
  the follower smooths it over half a second anyway.
- **Opening a video muted** also fixes an old annoyance: on a PC with no sound device, WPF
  couldn't open a video *with* sound, and the hub had to notice the failure and open it again.
- **`AudioOutputHost.Configure` waits** (up to 3 s) for Windows to switch devices, so every
  device change happens on a worker queue, never the UI thread.
- **The library stores a file once by its content.** Two identical test tones import as one
  song; the self-test gives each tone its own pitch.
- **Settings crashed as it opened.** A slider reported its first value while the window was
  still being built, before the label it updates existed. The label is now optional there.

### Self-test

`Infrastructure/SelfTest.Sound.cs` checks the music on a chosen output with its delay, "No
device" and back; mute all music; a playlist made, played from its second song, its Repeat,
Shuffle, crossfade, order, names and file changed, then deleted; a video with sound played
through our engine, in step with the picture, through a pause, a seek and an output with a
100 ms delay, and recorded; and the Sound settings. Pictures: `40-video-sound`,
`41-music-bin`, `42-music-manager`, `42b-music-manager-renamed`, `43-sound-settings`. On a PC
with no sound output the device checks are skipped; the sync checks still run.

### Things to try

1. **Hear the delay.** Give video sound its own output with a 300 ms delay and play a video of
   someone speaking: the lips move before the words. Then set it to 0.
2. **Watch it follow.** Play a video with sound and open Screens & Settings: under **Video
   sound on**, "Now:" says how far the sound is from the picture (a few milliseconds), and
   updates every second.
3. **Read the follower's tests** (`tests/MxU.Slides.Audio.Tests/VideoClockFollowerTests.cs`):
   each one is a little story of a video player and a sound card.

## Where Windows differs from the Mac

- **Video sound gets more than on the Mac.** The Mac's AVPlayer can only be given a device
  (`audioOutputDeviceUniqueID`); Windows also honours the output's channel pair or mono channel.
- **"No device" for video sound is silent everywhere.** On the Mac it turns the video volume to
  0; video sound isn't part of any mix a stream can take on either.
- **The video volume** (Settings › Sound) is now a plain gain, like AVPlayer's `volume`, not
  WPF's player's curve: 0.5 is half the level (−6 dB).
- **Lip sync is kept against WPF's clock.** A projector or TV that shows the picture late is
  matched with the output's delay, as on the Mac.
- **Reordering** a playlist's songs is ↑ ↓ (and Move Up / Move Down on right-click); the Mac
  drags. There is no undo for playlist changes yet (the Mac's `moveUndo`).
- The Mac's per-playlist **Mix** picker, mix faders, output trims and mutes, and the mixer
  itself are Phase 8. Until then every playlist plays on the main mix.
- Not here yet: a song's **Favourite** and **Tags**, and **Edit in Library** from a track.

*The PORTING.md rows for this work are in [PORTING.md](../PORTING.md); this is the walkthrough. See also [HOW-IT-WORKS.md](../HOW-IT-WORKS.md).*
