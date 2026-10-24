# Phase 4: broadcast extras (to merge into HOW-IT-WORKS and PORTING)

Branch `phase4/broadcast`. This file holds the text for the main docs; the coordinator merges it.

## For HOW-IT-WORKS.md

### Replace the last line of section 25 ("Not on Windows (yet): SRT, …")

Not on Windows (yet): SRT, HDR/HEVC streaming, ProRes recording, and signing in to YouTube or Facebook. Streams use a server URL and key.

### New section: 27. What a preset records and streams (Phase 4)

A preset now says three things: what the **picture** is, what the **sound** is, and where **recordings** are filed. You set them in **Stream & Record**.

**The picture**

- **The audience program** (the default): what the audience sees, from the program feed (section 17).
- **One screen**: the stage display, a lower-thirds-only screen, an LED wall. `Outputs/ScreenFeeds.cs` makes a hidden copy of that screen, exactly like the program feed does for the audience: a `SceneView` in a hidden window, captured by `HiddenCapture`.
  - It works out what the screen shows the same way `OutputManager` does, from `OutputManager.Config`: the program reshaped to the screen's size, with that screen's layer routing, or the stage display, or the alignment grid while you line projectors up.
  - It gets every new scene at the same moment the real outputs do. `RenderOutputs` and `RenderStage` hand their scenes to `ScreenFeeds.Show` right after they send them to the outputs, so transitions, builds and videos match the real screen.
  - A screen feed only exists while something records or streams that screen. Two destinations wanting the same screen at the same size share one.
- **60 frames a second**: the program feed runs at 30. A 60 fps preset gets the program from a screen feed running at 60 instead.
- **A media file ("replay")**: a stream preset can play out a video from the library, or **the newest recording** in a folder. Use this to stream a pre-recorded service. `Broadcast/Media/MediaFileSource.cs` decodes the file with Windows' own decoders (the Media Foundation source reader), and two threads hand its pictures and sound to the stream in real time. The stream ends by itself when the video does, and the toolbar counts down ("REPLAY 41:10"). A replay isn't recorded again.
  - `Broadcast/Library/StreamSources.cs` works out which file a preset means, and why it can't play if it can't: "no finished recording in “Recordings”", "newest recording … is 3 days old — over the 24h limit", "“Intro” isn't ready to play". The window shows this line under the preset before you go live.

The code that picks the picture is `CaptureSources` (in `Broadcast/Control/`). The app fills it in with the program feed, `ScreenFeeds` and the sound opener described next. The controllers just ask it for "the pictures of screen X at this size and rate". The tests give it fakes.

**The sound**

Each recording or stream opens its own sound, chosen by its preset. `Audio/SessionAudioFeed.cs` is a port of the Mac's `SessionAudioFeed`.

- **Program audio** (the default): everything this app plays, from `ProgramAudioTap` (section 21).
- **The main mix**: just the Music tab (playlists, songs, timeline music), with no video sound. `Audio/AudioMixFeeds.cs` takes each music bus's sound after its fader and adds them up on a steady 10 ms clock (`AudioMixFeed`). This works even on a PC with no sound card.
- **A sound input**: for example, the mixing desk plugged into a USB sound interface. Pick the device, then which channels (1–2, 3–4, or one channel in mono). `Audio/AudioInputInventory.cs` remembers these inputs in `audio-inputs.json` beside the settings, because they name this PC's devices. The device is opened only while a recording or stream uses it, so Windows' "microphone in use" light isn't on all service.
- **Add the program audio too**: mixes the program into a mix or input (`ProgramAudioSummer`).
- **Delay the sound** (0–2000 ms): holds the sound back so lips line up with a late picture (`AudioDelayLine`).
- If the chosen mix or input can't be opened (unplugged, or blocked in Windows Settings › Privacy › Microphone), the session falls back to program audio. The app shows a notice saying why.

The program feed no longer carries sound itself (`UpdateProgramAudio` is now empty): every session opens exactly the sound it asked for.

**Recordings in the media library**

As on the Mac, a finished recording is filed into the media library, so it appears in the **Media** tab:

1. While recording, the file is written to the library's `spool` folder (on the same disk, so filing it is just a rename).
2. When it's finished, `Broadcast/Library/RecordingLibrary.Adopt` fingerprints it, saves a `MediaItem` (folder "Recordings" unless the preset says otherwise, with when and by which preset it was recorded) and moves the file into the library's Blobs folder. This runs on its own thread (`BroadcastController.FileRecordingsIntoLibrary`), so the screen never waits for a long recording.
3. **Also copy to** puts a copy in an ordinary folder too, such as Videos\MxU Slides, OneDrive or Dropbox.
4. If the app stops before a recording is filed, the file is still in the spool. The next launch files it. The item is saved before the file moves, so this never makes two items.

**Something learnt the hard way:** in a COM interface, .NET sends a `byte[]` as a SAFEARRAY unless told otherwise. Media Foundation expects a plain pointer. So reading a blob overwrote memory and crashed the app, and the MP4 writer had been giving Windows a garbage H.264 header; recordings only played because the decoder found the real one inside the video. Every blob in `MediaFoundationInterop.cs` is now marked `LPArray`, and `MediaFoundationBlobTests` checks a blob round-trips byte for byte.

### Section 26 ("Keeping it running"): add to the self-test sentence

…both importers, the program feed, screens, NDI, music, live inputs, actions, timelines, recording, recording the stage screen, recording the Music tab's sound, 60 fps, recordings filed into the Media tab, and replaying the newest recording.

## For PORTING.md

### Add to "Ported line by line"

| Swift | C# |
|---|---|
| `Sources/StreamingController.swift`, `RecordingController.swift` (per-screen mirrors, the file source, the session audio feed) | `MxU.Slides.Broadcast/Control/StreamingController.cs`, `RecordingController.cs`, `CaptureSources.cs` |
| `OutputEngine/…/Export/MediaFileSource.swift` | `MxU.Slides.Broadcast/Media/MediaFileSource.cs` (Media Foundation source reader instead of AVAssetReader) |
| `PresenterCore/…/MediaImport/RecordingLibrary.swift` | `MxU.Slides.Broadcast/Library/RecordingLibrary.cs` |
| `Sources/ServiceControls.swift` (libraryAdoption, resolveStreamSource, resolveSourceItem, fileSource, captureSourcePreview, the row detail's summaries) | `MxU.Slides.Broadcast/Control/BroadcastController.cs` (`FileRecordingsIntoLibrary`, `CapturePreview`), `Library/StreamSources.cs` |
| `Sources/ProgramAudioSource.swift` (`SessionAudioFeed`, `ProgramAudioSummer`) | `MxU.Slides.Audio/SessionAudioFeed.cs`; the backends are in `MxU.Slides.Windows/ViewModels/MainViewModel.Broadcast.cs` |
| `Sources/AudioInputInventory.swift` (`Entry`, `AudioInputSource`) | `MxU.Slides.Audio/AudioInputInventory.cs` |
| `Sources/AudioMixerController.swift` (feed hubs: addFeedConsumer, removeFeedConsumer, syncMixFeeds), `AudioMixInventory.swift` (the main mix) | `MxU.Slides.Audio/AudioMixFeeds.cs` |
| `Sources/AudioSourceMenuItems.swift` | the Sound picker in `Views/BroadcastWindow.cs`, `MainViewModel.AudioSourceTitle` |
| `Sources/StreamRecordPane.swift` (preset detail: Source, Recording), `StreamRecordSettings.swift` (sizes, frame rates) | `MxU.Slides.Windows/Views/BroadcastWindow.cs` |
| `OutputEngine/…/Export/OutputMirror.swift` with `previewProvider(for:)` (one screen) | `MxU.Slides.Windows/Outputs/ScreenFeeds.cs` |

Tests ported: `RecordingLibraryTests.swift` → `RecordingLibraryTests.cs`; `MediaFileSourceTests.swift` → `MediaFileSourceTests.cs`. New: `CaptureTests.cs`, `StreamSourcesTests` (in `RecordingLibraryTests.cs`), `MediaFoundationBlobTests.cs`, and `SessionAudioTests.cs` in the Audio tests.

### Replace the "Broadcast extras" line in "Not ported yet"

- **Broadcast extras:** SRT (the Mac uses libsrt through SRTHaishinKit, a native library; Windows would need that binary shipped, or a complete managed SRT with its handshake, retransmission and AES encryption, so an `srt://` destination fails at once with "SRT isn't available on Windows yet" while the preset's other destinations carry on); HDR/HEVC streaming and ProRes (no Windows encoder path; HEVC/HDR destinations stream SDR H.264, ProRes presets record H.264); platform sign-in (YouTube/Facebook OAuth needs the church's own API credentials), so streams use a server URL and key. The Mac's mixer (sound inputs, video sound and the multitrack sent into mixes, per-mix stream gain, extra mixes) isn't on Windows, so a mix carries the music buses only, and the only mix is the main one. The "Check Sync…" sheet isn't ported.

## Files changed outside my ownership (small, additive)

- `src/MxU.Slides.Windows/ViewModels/MainViewModel.Live.cs`: one line in `RenderOutputs`, after the outputs get their scene: `ScreenFeeds?.Show(OutputRole.Audience, audience, refreshOnly ? null : transition);`
- `src/MxU.Slides.Windows/ViewModels/MainViewModel.Stage.cs`: one line at the end of `RenderStage`: `ScreenFeeds?.Show(OutputRole.Stage, scene, refreshOnly ? null : SceneTransition.Cut);`
- `src/MxU.Slides.Windows/Infrastructure/SelfTest.cs`: `partial`; `await BroadcastExtrasCheck();` after `await ProPresenterCheck();`; in `RecordingCheck`, `var file = await RecordedFileAsync(session);` (the file now moves into the library when it's finished).
- `OutputManager.cs`: **not changed**. `ScreenFeeds` uses only `OutputManager.Config`.

## Integration to-dos

- If the render agent changes how `RenderOutputs` or `RenderStage` hand scenes to the outputs (per-item transitions, looks), keep the two `ScreenFeeds?.Show(...)` lines next to them. If the outputs' per-screen scene logic in `OutputManager.SceneFor` changes (for example slide-theme looks), mirror it in `ScreenFeeds.SceneFor`, or give `OutputManager` a public `SceneFor(Guid)` and have `ScreenFeeds` call it.
- `UpdateProgramAudio()` (called from `MainViewModel.cs`) is now empty and could be removed with its call.
- Recordings now go into the library by default (as on the Mac), not Videos\MxU Slides. A preset's **Also copy to** has a one-click "Videos\MxU Slides" button. Mention this in the README's recording paragraph.
