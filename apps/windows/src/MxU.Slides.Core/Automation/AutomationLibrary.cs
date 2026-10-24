using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Automation;

/// <summary>
/// Making, changing and deleting action combos and schedule triggers in the library, and
/// keeping their boards (the folders and order the editors show) in step. Port of the combo,
/// trigger and board methods of the Mac's AppModel (createActionCombo, updateComboBoard,
/// duplicateScheduleTrigger …).
/// <para>
/// Every change is made to a copy of the document, which is then saved in its place. Swift's
/// documents are values, so an edit on the Mac never touches a copy someone else is holding;
/// copying first gives the same safety here (the scheduler, for one, keeps the triggers it read).
/// </para>
/// </summary>
public sealed class AutomationLibrary(LibraryStore store)
{
    public LibraryStore Store { get; } = store;

    // MARK: Action combos

    /// <summary>Every combo id in the library's order (by name), as the board reconciles against.</summary>
    private List<string> ComboIds() => [.. Store.All<ActionCombo>().Select(c => c.Id)];

    /// <summary>
    /// The combo board: the stored folders and order, with combos that have gone removed and
    /// new ones added at the end. Reading it never saves anything.
    /// </summary>
    public ControlBoard ComboBoard()
    {
        var board = Store.Get<ControlBoard>(ControlBoardLogic.ComboBoardId) is { } stored
            ? MxuJson.Clone(stored)
            : new ControlBoard { Id = ControlBoardLogic.ComboBoardId };
        board.Reconcile(ComboIds());
        return board;
    }

    /// <summary>Changes the combo board (reconciled first, so every combo is on it) and saves it.</summary>
    public void UpdateComboBoard(Action<ControlBoard> mutate)
    {
        var board = ComboBoard();
        mutate(board);
        Store.Save(board);
    }

    /// <summary>A new, empty combo; null when the name trims to nothing.</summary>
    public string? CreateActionCombo(string name, Func<string>? newId = null)
    {
        var trimmed = name.TrimmingWhitespacesAndNewlines();
        if (trimmed.Length == 0) return null;
        var id = (newId ?? LibraryStore.NewId)();
        Store.Save(new ActionCombo { Id = id, Name = trimmed, Actions = [] });
        return id;
    }

    /// <summary>Changes a combo (a copy of it) and saves it; false when there's no such combo.</summary>
    public bool UpdateActionCombo(string id, Action<ActionCombo> mutate)
    {
        if (Store.Get<ActionCombo>(id) is not { } stored) return false;
        var combo = MxuJson.Clone(stored);
        mutate(combo);
        Store.Save(combo);
        return true;
    }

    /// <summary>Deletes a combo (to the library's Trash) and takes it off the board.</summary>
    public void DeleteActionCombo(string id)
    {
        if (Store.Get<ActionCombo>(id) is null) return;
        Store.Delete<ActionCombo>(id);
        if (Store.Get<ControlBoard>(ControlBoardLogic.ComboBoardId) is not null) UpdateComboBoard(_ => { });
    }

    /// <summary>Duplicate: " Copy" on the name, every step with a new id, the same colour and icon.</summary>
    public string? DuplicateActionCombo(string id, Func<string>? newId = null)
    {
        if (Store.Get<ActionCombo>(id) is not { } combo) return null;
        var copy = ActionCombos.Duplicate(combo, (newId ?? LibraryStore.NewId)());
        Store.Save(copy);
        return copy.Id;
    }

    /// <summary>Paste: a new combo like the copied one (same name, new step ids, same look).</summary>
    public string? PasteActionCombo(ActionCombo copied, Func<string>? newId = null)
    {
        if (copied.Name.TrimmingWhitespacesAndNewlines().Length == 0) return null;
        var pasted = ActionCombos.Pasted(copied, (newId ?? LibraryStore.NewId)());
        Store.Save(pasted);
        return pasted.Id;
    }

    // MARK: Schedule triggers

    /// <summary>Every trigger id, sorted (the Mac reconciles the board against its sorted ids).</summary>
    private List<string> TriggerIds() => [.. Store.All<ScheduleTrigger>().Select(t => t.Id).Order(StringComparer.Ordinal)];

    /// <summary>The scheduler board, reconciled with the triggers in the library. Reading it never saves anything.</summary>
    public SchedulerBoard SchedulerBoard()
    {
        var board = Store.Get<SchedulerBoard>(SchedulerBoardLogic.WellKnownId) is { } stored
            ? MxuJson.Clone(stored)
            : new SchedulerBoard { Id = SchedulerBoardLogic.WellKnownId };
        board.Reconcile(TriggerIds());
        return board;
    }

    /// <summary>Changes the scheduler board (reconciled first) and saves it.</summary>
    public void UpdateSchedulerBoard(Action<SchedulerBoard> mutate)
    {
        var board = SchedulerBoard();
        mutate(board);
        Store.Save(board);
    }

    /// <summary>A new trigger (on at once, though it does nothing until it has a condition); null when the name trims to nothing.</summary>
    public string? CreateScheduleTrigger(
        string name, List<ScheduleCondition>? conditions = null, List<SlideAction>? actions = null, Func<string>? newId = null)
    {
        var trimmed = name.TrimmingWhitespacesAndNewlines();
        if (trimmed.Length == 0) return null;
        var id = (newId ?? LibraryStore.NewId)();
        Store.Save(new ScheduleTrigger { Id = id, Name = trimmed, Conditions = conditions ?? [], Actions = actions ?? [] });
        UpdateSchedulerBoard(_ => { });
        return id;
    }

    /// <summary>Changes a trigger (a copy of it) and saves it; false when there's no such trigger.</summary>
    public bool UpdateScheduleTrigger(string id, Action<ScheduleTrigger> mutate)
    {
        if (Store.Get<ScheduleTrigger>(id) is not { } stored) return false;
        var trigger = MxuJson.Clone(stored);
        mutate(trigger);
        Store.Save(trigger);
        return true;
    }

    /// <summary>
    /// Duplicate: " Copy" on the name, switched off (so a copy never fires twice by surprise),
    /// not archived, placed right after the original in the same folder.
    /// </summary>
    public string? DuplicateScheduleTrigger(string id, bool placeBesideOriginal = true, Func<string>? newId = null)
    {
        if (Store.Get<ScheduleTrigger>(id) is not { } stored) return null;
        var copy = MxuJson.Clone(stored);
        copy.Id = (newId ?? LibraryStore.NewId)();
        copy.Name += " Copy";
        copy.Enabled = false;
        copy.Archived = null;
        Store.Save(copy);
        if (placeBesideOriginal)
        {
            // Placed before reconciling, so the copy lands beside its original rather than at the end.
            var board = Store.Get<SchedulerBoard>(SchedulerBoardLogic.WellKnownId) is { } saved
                ? MxuJson.Clone(saved)
                : new SchedulerBoard { Id = SchedulerBoardLogic.WellKnownId };
            board.Reconcile([.. TriggerIds().Where(t => t != copy.Id)]);
            board.PlaceTrigger(copy.Id, afterSibling: id);
            board.Reconcile(TriggerIds());
            Store.Save(board);
        }
        return copy.Id;
    }

    /// <summary>Duplicate Folder: a copy of every trigger in it, in a new folder right after it.</summary>
    public string? DuplicateScheduleFolder(string folderId, Func<string>? newId = null)
    {
        var board = SchedulerBoard();
        if (board.Folder(folderId) is not { } folder) return null;
        var copies = folder.TriggerIds.Select(id => DuplicateScheduleTrigger(id, placeBesideOriginal: false, newId)).OfType<string>().ToList();
        var copyId = (newId ?? LibraryStore.NewId)();
        var fresh = Store.Get<SchedulerBoard>(SchedulerBoardLogic.WellKnownId) is { } saved
            ? MxuJson.Clone(saved)
            : new SchedulerBoard { Id = SchedulerBoardLogic.WellKnownId };
        // Reconciled without the copies first, so the folder copy can take them (Reconcile would put them at the end).
        fresh.Reconcile([.. TriggerIds().Where(t => !copies.Contains(t))]);
        var made = fresh.DuplicateFolder(folderId, copies, copyId);
        fresh.Reconcile(TriggerIds());
        Store.Save(fresh);
        return made;
    }

    /// <summary>Deletes a trigger (to the library's Trash) and takes it off the board.</summary>
    public void DeleteScheduleTrigger(string id)
    {
        if (Store.Get<ScheduleTrigger>(id) is null) return;
        Store.Delete<ScheduleTrigger>(id);
        UpdateSchedulerBoard(_ => { });
    }
}
