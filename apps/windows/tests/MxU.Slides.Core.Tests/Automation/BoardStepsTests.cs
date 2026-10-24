using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Automation;

// Windows-only: the Move Up / Move Down buttons and the Folder picker of the combo and
// scheduler editors. Every move must keep each item on the board exactly once.
public class BoardStepsTests
{
    /// <summary>Top level: x, folder f (a, b), y.</summary>
    private static ControlBoard ComboBoard() => new()
    {
        Id = ControlBoardLogic.ComboBoardId,
        Nodes = ["x", "f", "y"],
        Folders = [new ControlFolder { Id = "f", Name = "Sunday", ItemIds = ["a", "b"] }],
    };

    private static SchedulerBoard TriggerBoard() => new()
    {
        Id = SchedulerBoardLogic.WellKnownId,
        Nodes = ["x", "f", "y"],
        Folders = [new ScheduleFolder { Id = "f", Name = "Sunday", TriggerIds = ["a", "b"] }],
    };

    [Fact]
    public void AComboStepsWithinItsFolderAndStopsAtTheEnds()
    {
        var board = ComboBoard();
        Assert.True(board.StepItem("b", -1));
        Assert.Equal(["b", "a"], board.Folders[0].ItemIds);
        Assert.False(board.StepItem("b", -1));
        Assert.False(board.StepItem("a", 1));
        Assert.Equal(["x", "f", "y"], board.Nodes);
    }

    [Fact]
    public void AtTheTopLevelAFolderCountsAsOnePlace()
    {
        var board = ComboBoard();
        Assert.True(board.StepItem("x", 1));
        Assert.Equal(["f", "x", "y"], board.Nodes);
        Assert.True(board.StepFolder("f", 1));
        Assert.Equal(["x", "f", "y"], board.Nodes);
        Assert.False(board.StepFolder("x", 1));
        Assert.False(board.StepFolder("nope", 1));
        Assert.False(board.StepItem("y", 1));
        Assert.Equal(["x", "a", "b", "y"], board.AllItemIds());
    }

    [Fact]
    public void TheFolderPickerMovesAComboInOrOutToTheEnd()
    {
        var board = ComboBoard();
        board.PutInFolder("x", "f");
        Assert.Equal(["a", "b", "x"], board.Folders[0].ItemIds);
        Assert.Equal(["f", "y"], board.Nodes);
        board.PutInFolder("a", null);
        Assert.Equal(["f", "y", "a"], board.Nodes);
        board.PutInFolder("y", null);
        Assert.Equal(["f", "y", "a"], board.Nodes);
        board.PutInFolder("b", "f");
        Assert.Equal(["b", "x"], board.Folders[0].ItemIds);
        Assert.Equal(4, board.AllItemIds().Distinct().Count());
    }

    [Fact]
    public void TriggersStepAndMoveTheSameWay()
    {
        var board = TriggerBoard();
        Assert.True(board.StepTrigger("a", 1));
        Assert.Equal(["b", "a"], board.Folders[0].TriggerIds);
        Assert.True(board.StepTrigger("y", -1));
        Assert.Equal(["x", "y", "f"], board.Nodes);
        Assert.True(board.StepFolder("f", -1));
        Assert.Equal(["x", "f", "y"], board.Nodes);
        board.PutInFolder("x", "f");
        Assert.Equal(["b", "a", "x"], board.Folders[0].TriggerIds);
        board.PutInFolder("b", null);
        Assert.Equal(["f", "y", "b"], board.Nodes);
        Assert.Equal(["a", "x", "y", "b"], board.AllTriggerIds());
    }
}
