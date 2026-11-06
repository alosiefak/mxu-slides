# Operator depth (Phase 8)

Phase 5 gave the operator the panels a service needs. This phase is about running a service *well*: playing a song in the order the band is singing it tonight, seeing the whole service at once, keys you can change, a locked mode for a volunteer, a service list that copes with long services, and the small things that make the panels easier to use.

Six pieces (and a seventh: MxU's Mac updates that arrived meanwhile), each with its rules in Core (with unit tests) and its screens in the app:

| | What it is | Rules (Core) | Screens (app) |
|---|---|---|---|
| 1 | Song arrangements | `Lyrics/ArrangementEditing.cs` | `Views/Arrangements/*`, `ViewModels/MainViewModel.Arrangements.cs` |
| 2 | The continuous view | `Scene/ContinuousRows.cs` | `Views/ContinuousSlidesView.xaml`, `ViewModels/MainViewModel.Continuous.cs` |
| 3 | Volunteer (run-only) mode | `Control/VolunteerMode.cs` | `Views/Volunteer/*`, `ViewModels/MainViewModel.Volunteer.cs` |
| 4 | Keyboard shortcuts | `Control/KeyboardControl.cs` | `Views/Keys/*`, `Views/MainWindow.Keys.cs`, `ViewModels/MainViewModel.Shortcuts.cs` |
| 5 | Service list extras | `Library/RunOrderSelection.cs`, `Library/ServicePicking.cs` | `Views/ServicePicker.cs`, `Views/ServiceListExtras.cs`, `Views/ServiceMenus.cs`, `ViewModels/MainViewModel.ServiceList.cs` |
| 6 | Operator panel and board extras | `Automation/BoardDrops.cs`, `Library/FolderTree.cs` | `Views/Operator/BoardDragging.cs`, `DropLine.cs`, `PreviewShape.cs`, `LiveToolbar.cs`, `StagePanel.cs` |
| 7 | MxU's November updates (operator side) | `Control/VolunteerMode.cs` | `Views/Volunteer/VolunteerView.xaml`, `Views/Operator/ScrubMarks.cs`, `ViewModels/MainViewModel.Transport.cs` |

Everything a PC chooses for itself (its shortcuts, its volunteer PIN, which headers it has collapsed, which plan time it runs, grid or continuous) is in `settings.json` (`Infrastructure/AppSettings.Operator.cs`), never in a shared document. Arrangements are part of the song, so they *are* shared: they use the schema's existing `arrangements` and `defaultArrangementId` fields, so a Mac reads every change.

## 1. Song arrangements

### What you can do

A song's **sections** are its parts (Verse 1, Chorus, Bridge). An **arrangement** is a named order of them: "Sunday" might be Verse 1, Chorus, Verse 2, Chorus, Bridge, Chorus, Chorus. The chorus is played three times but its slides exist once, so fixing a typo fixes it everywhere.

- **The arrangement chip** in the slide grid's header (it replaces the old drop-down) says what plays here ("As written", "Sunday"). Its menu picks another, makes a **New Arrangement** (each section once, in slide order), **Duplicates**, **Renames** or **Deletes** the one picked, sets **Play by default** (what the song plays when a service row doesn't pick one), and opens **Edit Arrangements…**.
- **The pills** under the song's title are the arrangement's blocks in their section colours. Click one to jump the slides to it. With an arrangement picked you can **drag a pill** to move that block, **right-click** it to remove it, and **+** adds a section (each one listed with its first line). The pill of the block that's live has a red ring.
- **Picking live.** You can pick another arrangement while the song is on screen (the worship leader goes to the bridge early). The slide stays up, and Next and Previous carry on through the new order from the same slide: the live position moves to that slide in the new order (the same repeat of it, if it's there more than once).
- **Per service row.** Picking an arrangement for a song opened from the service saves it on that row, as before; the row's right-click menu also has **Arrangement ›**. The row's detail line names it.
- **The arrangement editor** (Edit Arrangements…) lists the song's arrangements on the left and shows the picked one's **name**, its **order as text** and its **blocks**: drag them, or use ↑ ↓ ✕, and **+ Add Section**. Type an order like `V1, C, V2, C x2, B, C` and press Enter to set all the blocks at once. If the song has a multitrack section map, sections with no audio are named under the list (the Mac's warning).

### How it works

- `ArrangementEditing` is the Mac's arrangement rules: `Add` seeds a new arrangement from the slides (each section once, in the order its slides first appear), `Delete` clears the song's default when it was the default, `Rename` trims and ignores blanks, and `MoveBlock` is the strip's drop rule ("dropped on itself, or just after itself, nothing moves"). `Duplicate` and `SetOrder` are Windows additions.
- **The order text stays in step.** `SongEditing.OrderText` now folds a run of the same section ("Chorus x2"), the way you'd type it, and `SongEditing.ParseOrder` reads it back to the same blocks (a unit test checks the round trip). The song editor's order box and the arrangement editor show the same words for the same arrangement.
- `MainViewModel.ReanchorLive` moves the live position across when the live song's arrangement changes. It counts which repeat of the live slide is up in the old order and picks the same repeat in the new one.
- Every change saves the song at once through `SavePresentation`, like any other edit.

## 2. The continuous view

### What you can do

The small switch under the slides (beside the thumbnail size slider) picks **Grid** (one presentation at a time, as before) or **Continuous**: the whole service as one long scroll. Each row is a card: a header is its name over a rule, a song shows its slides in its sections (with its arrangement's name; short sections sit side by side), a video shows its picture, music and plan rows say what they are. It behaves like the grid:

- Click a slide to take it live from its row. Next and Previous walk on through the service, and the scroll keeps the live slide in sight.
- Picking a row in the service list scrolls to its card.
- Each song's card says "Slide 3 of 12" while it's live, "12 slides" otherwise.

A song opened from the library (not from the service) shows on a card of its own.

### How it works

- `Core/Scene/ContinuousRows.cs` decides what the scroll lists (`PresentRows`: the run of show, with hidden headers kept as a bare rule, the Mac's rule) and cuts each song's arranged slides into its played sections (`Sections`). The card kinds are records (`Card.Slides`, `Card.Media`…), so the view picks a template by kind.
- `MainViewModel.Continuous.cs` turns them into card view models. It rebuilds the cards only when the rows themselves change (it compares a **signature** of the rows, their songs, arrangements and slides); a song edited in place redraws just its own card. Thumbnails are made card by card at background priority, so a long service opens at once and its pictures arrive a moment later without holding up a click.
- The view (`Views/ContinuousSlidesView.xaml`) is an `ItemsControl` with a **virtualizing** panel: only the cards on screen are made into controls, so a service of 300 slides scrolls smoothly. Scrolling to a card first asks the panel to make it (`BringIndexIntoViewPublic`), then measures where it is.
- The live marks come from the same place as the grid's: `MarkLiveTiles` (MainViewModel.Live.cs) now calls `FollowLiveInViews`.

## 3. Volunteer (run-only) mode

### What you can do

**Screens & Settings › Present › Volunteer Mode** turns it on: a **Volunteer** button appears beside Show and Edit. Pressing it switches the window to the **volunteer layout**:

- the service (pick a row, double-click to take it live, choose the service, version and plan time);
- its slides as one continuous scroll (click to take one live);
- the live preview, **Next**, **Previous** and the **clears**;
- the panels volunteers keep: **Timers** (start, pause, reset), **Alerts** (fill in the blanks, Go Live, Turn Off), **Service Tracking**, the **Stage Message**, and along the bottom **Presentations**, **Overlays**, **Media**, **Music** and **Combos** to click.
- **Presentations** is the library's songs and decks, with a search box: click one and its slides take the middle on their own, ready to take live, while the service stays as it was (click a service row to go back). This is the Mac's newer "Libraries volunteers keep": a volunteer can play from the library but not edit or reorganize it.

Nothing in it edits: no library, no slide editor, no settings, no screens on or off, no looks, no Edit mode. Keys that would edit (Ctrl+N, Ctrl+E…) say they're locked. **Panels volunteers keep** (in the same Settings section) chooses which of the panels and bins appear.

**Leave Volunteer Mode…** at the top right goes back. When the PC has a **PIN** (Settings › Present › Set a PIN…, 4 to 8 digits), leaving asks for it; a wrong one leaves volunteer mode on. Volunteer mode is remembered, so closing and reopening the app doesn't get a volunteer out of it.

### How it works

- `Core/Control/VolunteerMode.cs` has the switcher's rules (a port of the Mac's `VolunteerModeLogic`), the **run-only table** `RunOnly.Allows(action, runOnly)` (the Mac asks `runOnly` at each control; here each control asks one table, which is unit-tested), the panels volunteers keep, and the PIN.
- **The PIN is never stored.** `HashPin` stores a salted PBKDF2 hash (`pbkdf2:100000:<salt>:<hash>`), and `PinMatches` compares in constant time. A damaged stored value never matches.
- **One locked layout, not a hundred switches.** Rather than disabling every editing control in the main window one by one, `Views/Volunteer/VolunteerLayout.cs` swaps the window's content for `VolunteerView` while volunteer mode is on, and puts the usual content back, exactly as it was, when it ends. The live preview moves across (`MainViewModel.AudiencePreview`), and windows that edit (the combo, scheduler and timeline editors) close. This way an editing control can't be forgotten: if it isn't in the volunteer layout, a volunteer can't reach it.
- **Belt and braces.** The commands that change things also ask `Permit(action)` first: arrangements, the service list's bulk edits, shortcuts, settings. So do the keys (`RunsInRunOnly` on each command, as on the Mac), and the panels that appear in both layouts (Timers and Alerts hide their + menus, editors, right-click menus and dragging).

## 4. Keyboard shortcuts

### What you can do

- **Screens & Settings › Keyboard Shortcuts…** (or Alt+K) lists every command with its keys, grouped as on the Mac: Show Control, Clear Layers, Video, Create & Library, Application. Click a key chip and press the new key; **Esc** cancels and **Backspace** removes the key. ↺ puts one back to its default; **Restore Defaults…** puts them all back.
- **Filters**: Assigned, All, Customized, **Conflicts**, Unused. A key used twice where both could fire is drawn orange with "Also used by …".
- **Things that get a key when you give them one**: each look (output preset), each timer's start, pause and reset, each overlay (a toggle), each preview window, and any **action combo** (Add Combo Shortcut…). Anything without a command of its own is one combo away from a shortcut.
- **Group hot keys**: the letters that jump the live song to a section (C = Chorus). These are kept in the library's group palette, so they follow the songs.
- **The cheat sheet**: press **?** in the main window for every key at a glance. Any key closes it.

The defaults are the keys the window always had: F1–F6 clear, →, Space, Enter, PageDown and ↓ go to the next slide (PageDown is what most clickers send), Alt+→ skips builds, Ctrl+N a new song, and so on. Typing a slide number then Enter, and the group letters, are fixed.

### How it works

- `Core/Control/KeyboardControl.cs` is a port of the Mac's `KeyboardControl.swift`: a `KeyChord` (a key and its modifiers; the Mac's ⌘ is Ctrl here, ⌥ is Alt), the `KeyCommand`s with their group, **scope** and defaults, and `KeyCommandMap`, which stores only what was changed. A command missing from the map has its defaults; one stored as null has no key. `Resolve` finds what a chord runs; a command in a narrow scope (Show) wins over one that works anywhere, as on the Mac. `ConflictedKeys` finds clashes only where both can fire at once.
- **Windows' own rules**: Ctrl+A, C, V, X, Y and Z belong to every text box, and Windows keeps Alt+F4, Alt+Tab, Alt+Esc and Alt+Space, so the recorder refuses those (`ReservedReason`). While a text box has the keyboard, only chords that type nothing fire (F-keys, and ones with Ctrl or Alt: `FiresWhileTyping`), so typing a song title never clears the screen.
- `Views/Keys/KeyTokens.cs` turns WPF's `Key` into the Core's tokens. `Views/MainWindow.Keys.cs` is the only key handler: it builds a chord, handles a slide number being typed, asks the map, checks volunteer mode, and runs the command. The old hard-coded `switch` in `OnPreviewKeyDown` is gone.

## 5. Service list extras

### What you can do

- **Collapse a header**: click the chevron in front of its name (or double-click it). Its rows fold away in the list; Next still walks through them, and when a folded row goes live its group opens so you can see where the service is. The header's menu has Collapse All / Expand All.
- **Several rows at once**: Ctrl-click and Shift-click, as in Explorer. Right-click inside the choice for the **bulk menu**: "3 Items", **Transition ›** (how each song hands over to the next in a timeline: Stop, Fade, Overlap, Crossfade, and a length), **Hide**, **Remove from Service**. The service's ⋯ menu has **Set all transitions**.
- **The service picker** (it replaces the drop-down beside SERVICE) lists today's and later services first, in date order, then undated ones, twelve at a time with "Show 12 more"; **Earlier** opens the older ones, newest first. Each has its date in a column (and in the picker's tooltip: the narrow column gives the name the room).
- **Plan times.** A service imported from Planning Center can have several times (9:00 and 11:00), each leaving some rows out or running them in its own order. The picker's list then ends with **This computer runs**: All times (the default) or one of them, with how many rows it leaves out. The picker's label names the time chosen ("Sunday · 11:00"). The list, Next, auto-advance, the stage's next item, the service-tracking timers and the remote all follow it.

### How it works

- `Core/Library/RunOrderSelection.cs` ports the Mac's `ListMultiSelect` (the click rules and the Finder's rule for what a right-click works on) and `RunOrderSelection` (the row the slide area shows, and splitting a removal into this PC's rows and ones from a plan), plus `ServiceBulkEdit`. The list itself does the Ctrl/Shift clicking (WPF's Extended selection, set in `Views/ServiceListExtras.cs`), so the main window's XAML didn't change for it.
- **Collapsed rows stay in the list.** They're hidden by a style trigger (`IsCollapsedAway`), not removed, so MIDI's "select service item 3" and Next count them as before. Which headers are collapsed is per PC (`CollapsedHeaders`), like the Mac's.
- `Core/Library/ServicePicking.cs` groups the services (`Group`), formats the date column and pages the list. `ServicePlanTimes` is the picker's half of plan times; the rules themselves (`ServiceRunOrder.Visible`, `Ordered`) were ported in Phase 5. `MainViewModel.RunOfShow(service)` is the one place the app asks "which rows, in what order", so everything that walks the service follows the time.

## 6. Panel and board extras

- **Dragging on the boards.** Alerts, timers, action combos and the scheduler's triggers drag: above a row moves it there (into that row's folder if it's in one), onto a folder's name files it in the folder, and below the last row moves it to the end; folders drag too. A blue line shows where it will land. `Core/Automation/BoardDrops.cs` turns each drop into one of the board's own moves (the Mac's `handleControlBoardRowDrop` and friends), so nothing is ever lost or doubled, and the Service Tracking timers stay in their folder. `Views/Operator/BoardDragging.cs` is the one helper all four boards use; `DropLine.cs` draws the line on WPF's adorner layer, so the list doesn't move while you drag.
- **The preview keeps its target's shape.** Point the live preview at a 4:3 projector and it previews 4:3; a tall stage layout previews tall (up to 1.25 × its width, so the column stays usable). `Views/Operator/PreviewShape.cs` is an attached property set on the preview.
- **The transition chips' sliders.** Beside each room-default chip (Media, Slides) is a small slider for its length, 0.1 to 3 seconds, saved when you let go (the Mac's mini slider), with the length written after it; the chip itself now names just the kind. Longer lengths are typed in the chip's popover.
- **The Stage panel's folder submenus.** A stage screen's layout chip lists Built-in Layout, the layouts in no folder, then a submenu per folder (and folders inside folders), using `Core/Library/FolderTree.cs`, a port of the Mac's `FolderTreeLogic`.

## 7. MxU's November updates in this area

MxU released three Mac updates (their #10, #12 and #13) while this phase was being built. These are the parts that touch the operator's controls, and what Windows does with each.

- **Libraries volunteers keep** (Settings › Present on the Mac). Ported as the **Presentations** bin above. The Mac's other kinds (Overlays, Media, Audio) were already bins here, so the one setting, **Panels volunteers keep**, covers them all. A PC that saved its panels before this keeps what it chose (Presentations stays off there until it's ticked), the same rule as the Mac's `volunteerKinds(stored:)`.
- **Video marks on the transport strip.** A video can now have named marks (made in the Mac's media editor). The strip draws each one as a short coloured line on the scrub bar, and while the video plays it names the next one beside the video's name: "· Next: Chorus 0:12", in the mark's colour. `Views/Operator/ScrubMarks.cs` draws the lines over the slider (clicks go through to it); `MainViewModel.Transport.cs` works out which marks are in the pass (`MarksInPass`, the Mac's `VideoMarks.points`) and the caption.
- **"Playing a video no longer pins the main thread."** On the Mac, the strip and the auto-advance chip were redrawn by SwiftUI up to 30 times a second while a video played, which kept the main thread busy; MxU moved their ticking onto Core Animation layers. **Windows doesn't have that problem**: its strip and chip are a handful of property sets four times a second, on a Background-priority timer (`MainViewModel.ShowFlow.cs`), and WPF only redraws the text that changed. The self-test now measures it while a video plays: how many refreshes there were, how long they kept the UI thread, and how soon the UI thread answered input (the numbers are in the report, on the line after the check).
- **Nothing to port:** "one main window, never tabs" (macOS adds window tabs on its own; Windows doesn't), "Present card settings and header height redraw again" (a SwiftUI redraw fix in the Mac's continuous view; Windows' cards don't keep that state), the focus fixes ("name the front app when focus is lost": AppKit's activation, which WPF doesn't share), and the audio mixer's change (a diagnostics line, nothing an operator sees).
- **Waiting for the engine catch-up (wt\up-core), to join at integration:**
  - *Custom layers in the strip's layer picker and the Clear buttons.* The Mac now lists the screens' layer stack, custom layers included (`TransportTarget.pickerLayers`, `clear(layerID:)`). Windows lists its fixed layers until Core's `ShowState` has custom layers; then `TransportPicker()` and the Clear row read the stack instead.
  - *A mark's actions firing as the video passes it* (the Mac's `fireVideoActions`). It needs Core's `VideoMarks.Crossed`; the place for it is `RefreshTransport`, which already reads each live video four times a second.
  - `MarksInPass` is the Mac's `VideoMarks.points` (five lines, the same rule and the Mac's test cases); once Core's `VideoMarks` lands, it's swapped for `VideoMarks.Points`.
- **Not this area:** the fire feed the Mac now publishes on every fire, timer and transport press (`APIFireFeed`) is for backing up another computer (wt\up-features), as is the Backup page in Settings and the dot on its rows.

## Tests

- **Unit tests** (Core): `Lyrics/ArrangementEditingTests.cs` (ports `arrangementControlsNeedASectionedSlide` and the arrangement rules; the order text round trip), `Control/KeyboardControlTests.cs` (a port of `KeyboardControlTests.swift`, plus reserved keys and run-only), `Control/VolunteerModeTests.cs` (a port of `VolunteerModeLogicTests.swift`, plus the PIN, the panels and the run-only table), `Library/RunOrderSelectionTests.cs` (ports `ListMultiSelectTests.swift` and `RunOrderSelectionTests.swift`, plus the bulk edits), `Library/ServicePickingTests.cs` (ports `ServiceMenuLogicTests.swift`, plus grouping and plan times), `Automation/BoardDropsTests.cs`, `Library/FolderTreeTests.cs`, `Scene/ContinuousRowsTests.cs`. `Control/VolunteerModeTests.cs` also checks the Presentations bin's default and an older saved list. In the app's test project, `Views/KeyTokensTests.cs` checks the key tokens, that the defaults still do what the old hard-coded keys did, and the preview's shape; `ViewModels/TransportMarksTests.cs` ports the Mac's `VideoMarks.points` cases and checks the next-mark caption.
- **Self-test** section `OperatorDepth` (`Infrastructure/SelfTest.OperatorDepth.cs`, pictures `p8o-…`): an arrangement made, typed, moved, duplicated, picked per row and live; the song editor's order in step; the continuous view's cards, a click going live and Next following; a key rebound (the old key stops working), an overlay's own key, a clash in the Conflicts filter, keys ignored while typing, the cheat sheet; volunteer mode's layout, what it refuses, fewer panels, a wrong and a right PIN; a collapsed header and a folded row going live; two rows and the bulk menu, bulk Transition and Hide; the picker's groups; a plan time's rows and order; a board's drop line and a drop; a 4:3 preview; the slide chip's slider; the Stage panel's folders; a volunteer playing a deck from the library (`p8o-10b`); a video's marks on the strip (`p8o-22`), and the UI thread's load while it plays.

## Things to try

1. Open a song, make a new arrangement from the chip and drag the last pill to the front. Open the song editor (Edit lyrics): its order box has the new order in words.
2. Take the second chorus live, then pick "As written" from the chip. The slide stays; press → and see where it goes next.
3. Turn on Volunteer Mode with a PIN, press Volunteer, and try Ctrl+N, then leave with a wrong PIN.
4. In the shortcuts window give a combo F9 (Add Combo Shortcut…), close the window and press ? to see it on the cheat sheet. Note that changing a command's key gives it one key: Next's five defaults become just the one you pressed (the Mac's rule), and ↺ brings all five back.
5. Read `KeyCommandMap.Resolve` and `ConflictedKeys`, then make Clear All and Clear Media share a key and watch both turn orange.

## Different from the Mac

- **Volunteer mode's PIN.** The Mac retired its passcode: anyone can leave by picking Present or Edit. Windows has an optional PIN, per PC. With none set it works as the Mac does.
- **Volunteer mode is a layout of its own.** The Mac gates each control in its normal layout (its sidebar keeps only the libraries volunteers keep); Windows shows a separate locked layout (the same panels, built for running, with those libraries as bins). The Mac's saved Present layouts ("Volunteer Layout") aren't ported; Windows has one volunteer layout with a choice of panels.
- **Shortcuts.** Windows keeps its own defaults where it already had them (Ctrl+N is a new song, Ctrl+B the Bible, Ctrl+E Show/Edit, PageDown and ↓ also go to the next slide); the Mac's editor commands (Bold, Italic, Underline), its sidebar toggles and its Settings-page keys have no Windows command yet. Windows refuses the text-editing keys and the ones Windows keeps for itself. The cheat sheet (?) is Windows-only.
- **Plan times** have a picker on Windows; the Mac always runs every row in the service's order.
- **A folded row going live opens its group** on Windows; the Mac leaves it collapsed.
- **Duplicate** and **typing an order** in the arrangement editor are Windows additions; the arrangement editor is a window (the Mac's is a popover).
- The **continuous view** keeps the Mac's cards but not its sticky card headers or inline playlist editing; music rows play when fired.
- Undo for arrangement edits, service edits and board moves isn't here (the Mac has an undo journal for board moves).

## For PORTING.md

**Ported line by line**

| Swift | C# |
|---|---|
| `PresenterCore/…/KeyboardControl.swift` (`KeyChord`, `KeyCommand`, `KeyCommandMap`, `GeneratedKey`; the group hot keys were ported in Phase 2) | `MxU.Slides.Core/Control/KeyboardControl.cs` (⌘ → Ctrl, ⌥ → Alt; Windows' own commands and defaults; `ReservedReason`, `FiresWhileTyping`) |
| `Sources/KeyboardShortcuts.swift` (`KeyInController.handle`, `execute`, `dispatch`, the go-to buffer), `KeyboardSettingsPane.swift` (`KeyboardMapPane`, `KeyChordChip`, `GroupHotKeyChip`) | `MxU.Slides.Windows/Views/MainWindow.Keys.cs`, `Views/Keys/*` (`KeyTokens`, `ShortcutsWindow`, `KeyChip`), `ViewModels/MainViewModel.Shortcuts.cs` |
| `PresenterCore/…/VolunteerModeLogic.swift`, the run-only gates (`RunOnlySweepTests.swift`) | `MxU.Slides.Core/Control/VolunteerMode.cs` (`VolunteerMode`, `RunOnly`, `VolunteerPanels`) |
| `PresenterCore/…/ListMultiSelect.swift`, `Library/RunOrderSelection.swift` (not `swept`: no marquee), `AppModel.setServiceItemTransition` | `MxU.Slides.Core/Library/RunOrderSelection.cs` (`ListMultiSelect`, `RunOrderSelection`, `ServiceBulkEdit`) |
| `PresenterCore/…/Library/ServiceMenuLogic.swift`, `Sources/AppShell.swift` (`groupLocalServices`, the picker's paging and Earlier row) | `MxU.Slides.Core/Library/ServicePicking.cs`, `MxU.Slides.Windows/Views/ServicePicker.cs` |
| `PresenterCore/…/Library/FolderTreeLogic.swift`, `Sources/FolderMenus.swift` (`EntryFolderMenuItems`) | `MxU.Slides.Core/Library/FolderTree.cs`, `Views/Operator/StagePanel.cs` (`LayoutMenu`) |
| `Sources/AppModel.swift` (`addArrangement`, `updateArrangement`, `deleteArrangement`), `SlideEditorModel.swift` (`sectionName`, `arrangedSlideCount`), `ServiceContinuousView.swift` (`ArrangementStrip.dropBlock`, `baseRunSections`), `SlideSceneBuilder.showsArrangements` | `MxU.Slides.Core/Lyrics/ArrangementEditing.cs` |
| `Sources/ServiceContinuousView.swift` (`presentRows`, the card kinds, `CardSlidePosition`) | `MxU.Slides.Core/Scene/ContinuousRows.cs` |
| `Sources/Modules/ControlBoardFolders.swift` (`handleControlBoardRowDrop`, the folder block's drop, `ControlBoardEndDropStrip`) | `MxU.Slides.Core/Automation/BoardDrops.cs` |

**Re-implemented for Windows (same behaviour, different code)**

| Mac | Windows |
|---|---|
| `Sources/ArrangementPopover.swift`, `SectionPreviewPicker.swift`, `ServiceContinuousView.swift` (`ArrangementStrip`, `ArrangementPicker`) | `Views/Arrangements/*` (`ArrangementPicker`, `ArrangementStrip`, `ArrangementEditorWindow`), `ViewModels/MainViewModel.Arrangements.cs` |
| `Sources/ServiceContinuousView.swift` | `Views/ContinuousSlidesView.xaml(.cs)`, `Views/SlideViewSwitch.cs`, `ViewModels/MainViewModel.Continuous.cs` |
| `Sources/PresentLayoutController.swift` (`enterRunOnly`, `exitRunOnly`, `runOnlyModules`), `RunOnlyGate.swift`, `AppShell.swift` (`ModeSwitcher`), `SettingsView.swift` (Volunteer Mode, Tabs volunteers keep) | `Views/Volunteer/*` (`VolunteerLayout`, `VolunteerView`, `VolunteerModeButton`, `OperatorSettingsSection`, `PinDialog`), `ViewModels/MainViewModel.Volunteer.cs` |
| `Sources/ServicePlannerView.swift` (collapsing headers, the bulk menu, the row's Arrangement menu), `AppShell.swift` (Set All Transitions) | `Views/ServiceListExtras.cs`, `Views/ServiceMenus.cs`, `ViewModels/MainViewModel.ServiceList.cs` |
| `Sources/Modules/ControlBoardFolders.swift` and the modules' `draggablePayload` / `dropDestination` | `Views/Operator/BoardDragging.cs`, `DropLine.cs` (in `AlertsPanel`, `TimersPanel`, `ActionCombosWindow`, `SchedulerWindow`) |
| `Sources/LivePanel.swift` (the preview's `aspectRatio`) | `Views/Operator/PreviewShape.cs` |
| `Sources/PerformanceHUD.swift` (`TransitionChip`'s slider) | `Views/Operator/LiveToolbar.cs` (`LengthSlider`) |

**MxU's updates #10, #12 and #13 (771a319), operator side**

| Swift | C# |
|---|---|
| `SettingsView.swift` (`present.volunteerLibraries`, `VolunteerLibrariesControl`), `PresentLayoutController.swift` (`runOnlyLibraryKinds`), `LibraryPresentLogic.volunteerKinds` | `VolunteerPanel.Presentations` in `Core/Control/VolunteerMode.cs`, the Presentations bin in `Views/Volunteer/VolunteerView.xaml(.cs)`, `MainViewModel.PresentFromLibrary` |
| `MediaTransportStrip.swift` (the next-mark caption), `ScrubBar.swift` (`marks`), `MediaTransportController.swift` (`Row.marks`) | `Views/Operator/ScrubMarks.cs`, `Views/LiveShowControls.xaml`, `MainViewModel.Transport.cs` (`TransportMarks`, `TransportNextMark`, `MarksInPass`) |
| `AutoAdvanceChip.swift`, `ScrubBar.swift`, `MediaTransportStrip.swift` (`ClockLayers` ticking) | nothing to port: Windows ticks four times a second already; measured by the self-test (`MainViewModel.ShowFlowCost`) |
| `AppShell.swift` (one window, never tabs; `PresentCards`), `FocusWatchRecorder.swift`, `AudioMixerController.swift` (`feedLossSummary`) | nothing to port (macOS or SwiftUI only; diagnostics) |

**Windows-only**

- `Infrastructure/AppSettings.Operator.cs`: the key map, volunteer mode, its PIN hash and panels, grid or continuous, collapsed headers and plan times, all per PC.
- The volunteer PIN (`VolunteerMode.HashPin`, `PinMatches`); the cheat sheet (`Views/Keys/CheatSheet.cs`); the plan-time chip (`ServicePlanTimes`, `MainViewModel.RunOfShow`); `ArrangementEditing.Duplicate` and `SetOrder`; `MainViewModel.ReanchorLive`; a folded row going live opens its group (`FollowLiveInViews`); `SongEditing.OrderText` folding repeats.
- `Infrastructure/SelfTest.OperatorDepth.cs`.

**Not ported yet** (replace the matching items in the Phase 4 and Phase 5 lists)

- *Operator panel extras:* undo for alert and timer edits and board moves; the Stage panel's "Edit Layout in Library" (there's no stage layout editor). Live / Off is per screen. (Dragging, run-only mode, the folder submenus, the preview's shape and the transition sliders are done.)
- *Automation editor extras:* dropping library items onto a combo or the scheduler board, undo for board moves and trigger renames, the "Edit…" button beside a step's media or music, the capture-source preview line on scheduler rows. (Dragging on the boards and run-only mode are done.)
- *Service list:* undo for service edits; the marquee (drag a box to choose rows). (Collapsing headers, multi-select with bulk Hide, Transition and Remove, the picker's Upcoming/Earlier grouping and plan times are done.)
- *Shortcuts:* the Mac's editor commands (Bold, Italic, Underline), sidebar toggles and Settings-page keys.
- *Volunteer mode:* saved Present layouts ("Volunteer Layout"); the Local API and the browser remote aren't limited by it.
- *Continuous view:* sticky card headers, inline playlist editing, and dropping slides between cards.
- *From MxU's updates, waiting for Core (wt\up-core):* custom layers in the strip's layer picker and the Clear row (`ClearChip.swift`, `PresentCommands.swift`, `MediaTransportStrip.pickerMenu`); a video mark's actions firing (`MediaTransportController.fireVideoActions`); swapping `MarksInPass` for Core's `VideoMarks.Points`. The fire feed in `ServiceControls.swift`, `TimersController.swift`, `AudioController.swift` and `MediaTransportController.swift` (`APIFireFeed.publish`) belongs to the backup computer (wt\up-features).

*This is the walkthrough; see also [HOW-IT-WORKS.md](../HOW-IT-WORKS.md) and [PORTING.md](../PORTING.md).*
