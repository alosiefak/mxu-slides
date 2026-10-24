# Phase 4: the integrator's parts (to merge into HOW-IT-WORKS / PORTING)

## HOW-IT-WORKS additions

### ProPresenter: everything else a workspace holds (add to section 16)

A ProPresenter library import used to bring in presentations, media, themes, props, messages, stage layouts, playlists, macros and calendar events. It now also brings in:

- **Screens:** ProPresenter's screens become screens here (by name; a screen you already have is reused), with their role, colour and keystone corrections, slices and edge blends. `src/MxU.Slides.ProImport/ProWorkspacePlans.cs` does this; it is a port of the second half of the Mac's `WorkspaceImport.swift`. "Update unedited" never reconfigures a screen you already had; "Replace" does.
- **Looks** become **output presets** (each screen's layers, theme and mask), so switching a preset switches the look.
- **Timers** and **video inputs** join the timer list and the input inventory.
- **Stage layouts:** the first one ProPresenter assigns becomes the stage layout (this app has one stage layout for every confidence screen).
- **Group hot keys:** ProPresenter lets you change which key jumps to which group. The library now keeps a **group palette** (`Core/Library/GroupPalettes.cs`, one document, id `group-palette`, as on the Mac) and the hot keys come from it. `ProImport/ProGroupHotKeys.cs` applies imported letters under the import policy. The import ledger (`ImportLedger.json`) remembers each letter an import set, so "Update unedited" can tell your change from an untouched import.
- **MIDI devices** (below).

### MIDI devices (add to the MIDI section)

The Mac keeps a list of named MIDI devices ("Lighting desk", "Keys") in the library. Windows now does too (`MIDIDevice` documents, folder `MidiDevices`):

- Each device is on or off, In, Out or In + Out, and listens on one channel or all of them.
- On this PC each device is tied to the ports whose names match (`Core/Control/MidiDevices.cs`, a port of `MIDIDeviceResolver`), or to ports you picked (kept in `settings.json`, because port ids belong to this PC).
- MIDI control listens only to devices that are on, on their channel, once any listed device takes input. With no devices listed, it listens to every input, as before.
- A slide action's MIDI Out goes to its device's port. A device that's off, input-only or unplugged is skipped with a reason, and never sent to every port by mistake.
- Set them up in **Screens & Settings › MIDI › MIDI devices**. `GET /v1/midi/devices` lists them and `PUT …/enabled` switches one on or off.

### Recording on a PC that can't capture (add to sections 17 and 25)

Some PCs (virtual machines like the CI runner, some remote sessions) start Windows' capture but never get a picture from it. Two seconds without one, `HiddenCapture` switches to a **software copy**: WPF draws the program into a bitmap on the UI thread. It runs as often as it can without taking more than about a third of that thread's time, and later captures start on it straight away.

- Recordings, streams and NDI still work, at a lower frame rate.
- The copy can't draw pixel shader 3.0 effects, so GPU effects and output corrections are missing from those frames.
- `MXU_SLIDES_CAPTURE=software` forces it, to test it or on a PC whose capture misbehaves.

## PORTING rows

| Swift | C# |
|---|---|
| `Sources/WorkspaceImport.swift` (`run`: timers, inputs, screens, corrections, slices, masks, stage assignments, looks) | `MxU.Slides.ProImport/ProWorkspacePlans.cs`, `ViewModels/MainViewModel.Import.cs` |
| `PresenterCore/…/Library/GroupPaletteLogic.swift`, the GroupPalette part of `KeyboardControl.swift`, `ImportLedgerLogic.swift` (`applyImportedHotKeys`) | `MxU.Slides.Core/Library/GroupPalettes.cs`, `MxU.Slides.ProImport/ProGroupHotKeys.cs` |
| `PresenterCore/MIDIControl.swift` (`ResolvedMIDIDevice`, `MIDIDeviceResolver`), `Sources/MIDIDeviceInventory.swift` | `MxU.Slides.Core/Control/MidiDevices.cs`, `ViewModels/MainViewModel.MidiDevices.cs` |

## Not ported

- **Sync with the Mac's library folder** (Automerge). The Mac uses Automerge's Rust core through a prebuilt Apple-only binary. Windows needs the same core built for Windows, which means installing the Rust toolchain to build it and shipping a native DLL. That's a decision for you; until then the two apps share documents, not the library folder.
- **Masks from ProPresenter looks** wait for the path helpers the mask editor brings (`ShapeOutlineSvg`, `PathAnchors`); `ProWorkspacePlans.MasksFromPlan` is the hook.
