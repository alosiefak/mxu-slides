# Phase 4: the automation editors

Notes to merge into `HOW-IT-WORKS.md` and `PORTING.md`.

## HOW-IT-WORKS: new section, right after "23. Show automation"

### 23a. Editing show automation (Phase 4)

Section 23 explains how actions, combos and schedule triggers *run*. Phase 4 adds the windows that *make and change* them. There are three, and they share one piece:

- **The action list** (`Views/ActionListEditor.cs`) edits a list of steps. Each step is a card: its kind at the top ("FIRE MEDIA"), its settings, and a Delay box. ↑ ↓ move it, ✕ removes it, **+ Add action** opens the Add Action menu (Clear ›, Alert ›, Timer › Start › Sermon …). The same control sits in all three windows, because a combo, a slide, a media item and a trigger all hold the same kind of list.
- **Action Combos** (`Views/ActionCombosWindow.cs`): every combo on the left, in folders and in your order; the selected combo on the right with its name, colour, icon, "Run when MxU Slides starts" and its steps. Ctrl+click or Shift+click picks several to colour, move or delete at once.
- **Slide Actions** (`Views/SlideActionsWindow.cs`): the steps a slide runs each time it goes live, or a media item runs each time it's fired.
- **Scheduler** (`Views/SchedulerWindow.cs`): every trigger, in folders, with when it fires next ("Sun 09:00"), an On/Paused chip, and the selected trigger's conditions and steps. Right-click a trigger to skip its next fire, pause it until a moment, or give it an end date. **History…** shows every run on this PC and one-time triggers that have finished.

**Where the rules live.** The windows only draw things. What each kind of step lets you set, its defaults and its limits (MIDI channel 1–16, a delay of 0–3600 seconds in tenths), what the Add Action menu holds, how a trigger's summary reads, what the On/Paused chip does: all of that is in Core, ported from the Mac, with unit tests:

- `Core/Automation/Actions/ActionEditing.cs`: each step kind's settings (`Fields`), what its pickers list (`Entries`), the Add Action menu (`AddMenu`) and reading a typed delay. The window turns each setting into a control: a picker becomes a drop-down (or, for media, songs and music, a search window: `Views/LibraryPickerWindow.cs`), a number becomes a box with − and +.
- `Core/Automation/Scheduling/ScheduleEditing.cs` and `ScheduleConditionFormat.cs`: new conditions (Sundays at 9:00; an hour from now; the first timer at 0:00), the summaries, what a row shows on its right, the chip, and reading typed dates and times ("25/10/2026", "tomorrow", "9:30 pm").
- `Core/Automation/AutomationLibrary.cs`: making, changing, duplicating and deleting combos and triggers, keeping their **boards** (the folders and order) in step. It always changes a *copy* of a document and saves the copy, so nobody else holding the old one (the scheduler, for instance) sees a half-made change. On the Mac documents are Swift values, which gives the same safety for free.
- `Core/Automation/BoardSteps.cs` (Windows-only): Move up / Move down and the Folder picker. The Mac reorders by dragging.

**Saving.** Every change saves straight away, as on the Mac: there is no Save button, only Done. Text boxes (names, delays, times) save when you press Enter or click elsewhere, not on every key, so a half-typed name never fires anything. Esc puts the old text back.

**Keeping up.** Each window listens to `LibraryStore.Changed`. Its own saves just redraw the list; a change from somewhere else (an import, the Local API) redraws everything. The Scheduler window also listens to `Scheduler.Changed`, which fires after each sweep, so the "next" times move on by themselves. Saving a trigger makes the scheduler sweep again (`MainViewModel.Scheduler.cs`), which is how a new trigger's next time appears a moment after you make it.

**Try it.** Make a combo with two steps, an alert and then an overlay with a 2-second delay, and press Run: the message comes up at once, the overlay two seconds later. Then open `ActionCombos/` in your *test* library and find the combo's JSON: the delay is stored as `"delaySeconds": 2`, and a zero delay isn't stored at all.

The self-test (`Infrastructure/SelfTest.Editors.cs`) drives all three windows: it makes a two-step combo and runs it, gives a slide a background and a timer and fires it, and makes a trigger and checks its next time is listed. Pictures: `30-editor-combos-window`, `30b`/`30c` (the Add Action menu and a submenu), `31-editor-slide-actions-window`, `32-editor-scheduler-window`.

## PORTING: rows to add to "Ported line by line"

| Swift | C# |
|---|---|
| `Sources/ActionEditors.swift` (ActionParameterFields, AddActionMenuItems, ActionsMenu: the rules), `Sources/LiveInputCatalog.swift` | `MxU.Slides.Core/Automation/Actions/ActionEditing.cs` |
| `Sources/SchedulerView.swift` (ScheduleConditionFormat, conditionSummary, the row, chip, condition and sheet logic) | `MxU.Slides.Core/Automation/Scheduling/ScheduleConditionFormat.cs`, `ScheduleEditing.cs` |
| `Sources/AppModel.swift` (createActionCombo, comboBoard, updateComboBoard, createScheduleTrigger, duplicateScheduleTrigger, duplicateScheduleFolder, deleteScheduleTrigger, schedulerBoard, updateSchedulerBoard) | `MxU.Slides.Core/Automation/AutomationLibrary.cs` |

Tests for these: `ActionEditingTests.cs`, `ScheduleEditingTests.cs`, `AutomationLibraryTests.cs` (the Mac checks these views with source sweeps, `SlideActionsMenuSweepTests` and `ScopedGridEditSweepTests.comboAndTriggerFieldsCommitOnAPause`; the C# tests check the same rules directly).

Rows to add to "Re-implemented for Windows":

| Mac | Windows |
|---|---|
| `Sources/ActionEditors.swift` (ActionListEditor, SlideActionsSheet), `LibraryPickerSheet` | `MxU.Slides.Windows/Views/ActionListEditor.cs`, `SlideActionsWindow.cs`, `LibraryPickerWindow.cs`, `EditorKit.cs`, `EditorStyles.xaml` |
| `Sources/Modules/ActionCombosModule.swift`, `Modules/ControlBoardFolders.swift` | `MxU.Slides.Windows/Views/ActionCombosWindow.cs` (a window, not a sidebar module) |
| `Sources/SchedulerView.swift` (the SwiftUI views, SnoozeSheet, EndDateSheet, SchedulerHistorySheet) | `MxU.Slides.Windows/Views/SchedulerWindow.cs`, `SchedulerDialogs.cs` |
| (the editors' app glue in AppModel / ServiceControls) | `MxU.Slides.Windows/ViewModels/MainViewModel.ActionEditors.cs` |

Add to "Windows-only":

- `Core/Automation/BoardSteps.cs` (Move up / Move down, the Folder picker) and the typed date and time parsing in `ScheduleEditing.cs` (the Mac uses date pickers). `ScheduleEditing.NextFireLine` (the trigger editor's "Fires next … Won't fire now: the Scheduler is off").
- `Infrastructure/SelfTest.Editors.cs`.

Remove from "Not ported yet": the "Action combo and schedule editors" bullet. Add:

- **Automation editor extras:** dragging on the combo and scheduler boards (Windows has Move up/down and a Folder picker), dropping library items onto a combo or the scheduler board, undo for board moves and trigger renames, the "Edit…" button beside a step's media or music, run-only mode, and the capture-source preview line on scheduler rows. Audio-input steps show and keep their settings, but Windows offers no audio inputs to pick (the router skips those steps).

## Where Windows differs from the Mac, on purpose

- **Adding a step.** The Mac's Add Action opens a small sheet to confirm the new step. Windows adds it straight to the list with its target already filled in (Alert › Nursery adds "Fire Alert: Nursery"), because the list shows every setting in place. Fire Media… and Music File… open the search window at once. ✕ takes a step back out.
- **Deleting a combo or a trigger asks first** (it goes to the library's Trash). The Mac deletes at once and offers undo; Windows has no undo for these yet.
- **A weekly time that can't be read** shows "invalid time" in the summary; the Mac shows the current time.
- **Day buttons** read Sun, Mon … (the region's short day names) rather than S M T W T F S.
- **Weekday numbering, stored formats, defaults and limits are exactly the Mac's**, so a library made on one app edits correctly on the other.

## Integration to-dos for the main window

`Views/MainWindow.xaml` isn't touched; these are the calls to wire up (all on the UI thread, `this` being the main window):

| Where | Call |
|---|---|
| A button in the Combos tab header (e.g. "Edit combos…", beside the MIDI hint) | `ActionCombosWindow.Open(this, model);` |
| A toolbar button or menu item "Scheduler" | `SchedulerWindow.Open(this, model);` |
| "Slide Actions…" for the selected slide (edit mode, or the slide's menu) | `if (model.SelectedSlide is { } tile) SlideActionsWindow.Edit(this, model, tile.Slide, model.CurrentPresentation);` |
| A slide tile's right-click menu (build it in `ContextMenuOpening`, so the count is fresh) | `menu.Items.Add(SlideActionsWindow.ActionsMenu(this, model, tile.Slide, model.CurrentPresentation));` |
| A media tile's right-click menu, "Actions…" | `SlideActionsWindow.EditMedia(this, model, mediaTile.Item);` |
| A service item's right-click menu, "Actions…" | `SlideActionsWindow.EditServiceItem(this, model, row.Item);` (a media item's actions; for a presentation it says to pick a slide) |

`Open` brings an already-open window to the front instead of opening a second. `Edit`, `EditMedia` and `EditServiceItem` wait until the window is closed.

Nice to have, later: the Mac shows up to three action icons on a slide tile (`SlideActions.IconName(action)` gives the icon name, `EditorKit.Glyph(name)` the Segoe character), and the Combos tab could follow the board's folders and order (today it lists combos by name, which is also the order MIDI "Run Action Combo" numbers them).

## Files

New: `Core/Automation/Actions/ActionEditing.cs`, `Core/Automation/Scheduling/ScheduleConditionFormat.cs`, `ScheduleEditing.cs`, `Core/Automation/AutomationLibrary.cs`, `BoardSteps.cs`; `Windows/Views/ActionListEditor.cs`, `ActionCombosWindow.cs`, `SlideActionsWindow.cs`, `SchedulerWindow.cs`, `SchedulerDialogs.cs`, `LibraryPickerWindow.cs`, `EditorKit.cs`, `EditorStyles.xaml`; `Windows/ViewModels/MainViewModel.ActionEditors.cs`; `Windows/Infrastructure/SelfTest.Editors.cs`; tests `ActionEditingTests.cs`, `ScheduleEditingTests.cs`, `AutomationLibraryTests.cs`, `BoardStepsTests.cs`.

Changed outside the editors' own files: `Windows/Infrastructure/SelfTest.cs` (the class is now `partial`, and `await EditorsCheck();` runs after `await ProPresenterCheck();`).
