using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Automation;

// C#: the combo, trigger and board methods of the Mac's AppModel (createActionCombo,
// comboBoard, updateComboBoard, createScheduleTrigger, duplicateScheduleTrigger,
// duplicateScheduleFolder, deleteScheduleTrigger, schedulerBoard), on a real library in a
// throwaway folder.
public sealed class AutomationLibraryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mxu-automation-tests-" + Guid.NewGuid().ToString("N"));
    private readonly LibraryStore store;
    private readonly AutomationLibrary library;

    public AutomationLibraryTests()
    {
        store = new LibraryStore(root);
        store.Load();
        library = new AutomationLibrary(store);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
    }

    private LibraryStore Reopened()
    {
        var again = new LibraryStore(root);
        again.Load();
        return again;
    }

    [Fact]
    public void ANewComboNeedsANameAndStartsEmpty()
    {
        Assert.Null(library.CreateActionCombo("  \n "));
        var id = library.CreateActionCombo("  Walk-in  ", () => "c1");
        Assert.Equal("c1", id);
        var combo = Reopened().Get<ActionCombo>("c1")!;
        Assert.Equal(("Walk-in", 0), (combo.Name, combo.Actions.Count));
    }

    [Fact]
    public void TheComboBoardListsEveryComboWithoutSavingUntilItChanges()
    {
        library.CreateActionCombo("Walk-out", () => "b");
        library.CreateActionCombo("Announcements", () => "a");
        Assert.Equal(["a", "b"], library.ComboBoard().Nodes);
        Assert.Null(store.Get<ControlBoard>(ControlBoardLogic.ComboBoardId));

        library.UpdateComboBoard(board =>
        {
            var folder = board.AddFolder("Sunday", "f");
            board.MoveItemIntoFolder("b", folder);
        });
        var saved = Reopened().Get<ControlBoard>(ControlBoardLogic.ComboBoardId)!;
        Assert.Equal(["a", "f"], saved.Nodes);
        Assert.Equal(["b"], saved.Folders.Single().ItemIds);

        library.CreateActionCombo("Offering", () => "c");
        Assert.Equal(["a", "b", "c"], library.ComboBoard().AllItemIds());
    }

    [Fact]
    public void EditingAComboSavesACopyAndLeavesTheOldOneAlone()
    {
        library.CreateActionCombo("Walk-in", () => "c");
        var before = store.Get<ActionCombo>("c")!;
        Assert.True(library.UpdateActionCombo("c", combo => combo.Actions.Add(Make.Action("a", SlideActionKind.ClearAll))));
        Assert.Empty(before.Actions);
        Assert.Single(store.Get<ActionCombo>("c")!.Actions);
        Assert.Single(Reopened().Get<ActionCombo>("c")!.Actions);
        Assert.False(library.UpdateActionCombo("missing", _ => { }));
    }

    [Fact]
    public void DeletingAComboTakesItOffTheBoardAndItsFolder()
    {
        library.CreateActionCombo("A", () => "a");
        library.CreateActionCombo("B", () => "b");
        library.UpdateComboBoard(board => board.MoveItemIntoFolder("a", board.AddFolder("F", "f")));
        library.DeleteActionCombo("a");
        Assert.Null(store.Get<ActionCombo>("a"));
        var board = store.Get<ControlBoard>(ControlBoardLogic.ComboBoardId)!;
        Assert.Empty(board.Folders.Single().ItemIds);
        Assert.Equal(["b", "f"], board.Nodes);
        Assert.True(Directory.EnumerateFiles(Path.Combine(root, "Trash"), "*.json", SearchOption.AllDirectories).Any(), "a delete goes to the Trash");
    }

    [Fact]
    public void DuplicateAndPasteMakeNewCombosWithNewStepIds()
    {
        library.CreateActionCombo("Walk-in", () => "c");
        library.UpdateActionCombo("c", combo =>
        {
            combo.Actions.Add(Make.Action("s1", SlideActionKind.FireMedia, a => a.MediaId = "m"));
            combo.ColorHex = "#FF5F57FF";
            combo.RunOnStartup = true;
        });
        var copyId = library.DuplicateActionCombo("c", Make.Ids("d"))!;
        var copy = store.Get<ActionCombo>(copyId)!;
        Assert.Equal(("Walk-in Copy", "#FF5F57FF", (bool?)null), (copy.Name, copy.ColorHex, copy.RunOnStartup));
        Assert.NotEqual("s1", copy.Actions.Single().Id);
        Assert.Null(library.DuplicateActionCombo("missing"));

        var pastedId = library.PasteActionCombo(store.Get<ActionCombo>("c")!)!;
        Assert.Equal("Walk-in", store.Get<ActionCombo>(pastedId)!.Name);
        Assert.NotEqual("c", pastedId);
        Assert.Equal(3, store.All<ActionCombo>().Count);
    }

    [Fact]
    public void ANewTriggerGoesOnTheSchedulerBoard()
    {
        Assert.Null(library.CreateScheduleTrigger(" "));
        var id = library.CreateScheduleTrigger("Countdown loop", [ScheduleEditing.NewWeekly(() => "w")], newId: () => "t1")!;
        Assert.Equal("t1", id);
        Assert.Equal(["t1"], Reopened().Get<SchedulerBoard>(SchedulerBoardLogic.WellKnownId)!.Nodes);
        Assert.Single(store.Get<ScheduleTrigger>("t1")!.Conditions);
        Assert.Null(store.Get<ScheduleTrigger>("t1")!.Enabled);
    }

    [Fact]
    public void ADuplicateIsOffAndLandsBesideItsOriginal()
    {
        library.CreateScheduleTrigger("A", newId: () => "a");
        library.CreateScheduleTrigger("B", newId: () => "b");
        library.UpdateSchedulerBoard(board =>
        {
            var folder = board.AddFolder("Sunday", "f");
            board.MoveTriggerIntoFolder("a", folder);
            board.MoveTriggerIntoFolder("b", folder);
        });
        library.UpdateScheduleTrigger("a", t => t.Archived = true);
        var copyId = library.DuplicateScheduleTrigger("a", newId: () => "a2")!;
        var copy = store.Get<ScheduleTrigger>(copyId)!;
        Assert.Equal(("A Copy", (bool?)false, (bool?)null), (copy.Name, copy.Enabled, copy.Archived));
        Assert.Equal(["a", "a2", "b"], library.SchedulerBoard().Folders.Single().TriggerIds);
        Assert.Equal(["f"], library.SchedulerBoard().Nodes);
    }

    [Fact]
    public void DuplicatingAFolderCopiesItsTriggersIntoANewFolderAfterIt()
    {
        library.CreateScheduleTrigger("A", newId: () => "a");
        library.CreateScheduleTrigger("Z", newId: () => "z");
        library.UpdateSchedulerBoard(board =>
        {
            board.MoveTriggerIntoFolder("a", board.AddFolder("Sunday", "f"));
            board.MoveFolder("f", beforeNode: "z");
            board.SetFolderEnabled("f", false);
        });
        var ids = Make.Ids("n");
        var folderCopy = library.DuplicateScheduleFolder("f", ids)!;
        var board = library.SchedulerBoard();
        Assert.Equal(["f", folderCopy, "z"], board.Nodes);
        var copied = board.Folder(folderCopy)!;
        Assert.Equal(("Sunday Copy", (bool?)false), (copied.Name, copied.Enabled));
        var member = store.Get<ScheduleTrigger>(copied.TriggerIds.Single())!;
        Assert.Equal(("A Copy", (bool?)false), (member.Name, member.Enabled));
        Assert.Equal(["a"], board.Folder("f")!.TriggerIds);
        Assert.Null(library.DuplicateScheduleFolder("nope"));
    }

    [Fact]
    public void DeletingATriggerTakesItOffTheBoard()
    {
        library.CreateScheduleTrigger("A", newId: () => "a");
        library.CreateScheduleTrigger("B", newId: () => "b");
        library.UpdateSchedulerBoard(board => board.MoveTriggerIntoFolder("a", board.AddFolder("F", "f")));
        library.DeleteScheduleTrigger("a");
        var board = Reopened().Get<SchedulerBoard>(SchedulerBoardLogic.WellKnownId)!;
        Assert.Empty(board.Folders.Single().TriggerIds);
        Assert.Equal(["b", "f"], board.Nodes.Order(StringComparer.Ordinal));
        Assert.False(library.UpdateScheduleTrigger("a", _ => { }));
    }

    [Fact]
    public void EditingATriggerLeavesTheCopyTheSchedulerReadAlone()
    {
        library.CreateScheduleTrigger("A", [ScheduleEditing.NewWeekly(() => "w")], newId: () => "a");
        var held = store.Get<ScheduleTrigger>("a")!;
        library.UpdateScheduleTrigger("a", t => ScheduleEditing.ToggleDay(t.Conditions[0], 4));
        Assert.Equal([1], held.Conditions[0].Days!);
        Assert.Equal([1, 4], store.Get<ScheduleTrigger>("a")!.Conditions[0].Days!);
    }
}
