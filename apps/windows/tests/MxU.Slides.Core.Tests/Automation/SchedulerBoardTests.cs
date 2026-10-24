using System.Text.Json.Nodes;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Automation;

// Ported from PresenterCoreTests/SchedulerTests.swift.
public class SchedulerBoardTests
{
    [Fact]
    public void ScheduleTriggerRoundTripsThroughJson()
    {
        var trigger = new ScheduleTrigger
        {
            Id = "tr1", Name = "Sunday pre-service",
            Conditions =
            [
                new() { Id = "c1", Kind = ScheduleConditionKind.Weekly, Days = [1], TimeOfDay = "08:45" },
                new() { Id = "c2", Kind = ScheduleConditionKind.OneTime, Date = "2026-07-20T18:00" },
                new() { Id = "c3", Kind = ScheduleConditionKind.TimerReaches, TimerId = "t1", TimerSeconds = 0 },
            ],
            Actions =
            [
                Make.Action("a1", SlideActionKind.SwitchOutputPreset, a => a.PresetId = "preset-1"),
                Make.Action("a2", SlideActionKind.CaptureStart, a => a.CapturePresetId = "capture-1"),
                Make.Action("a3", SlideActionKind.CaptureStop),
                Make.Action("a4", SlideActionKind.FirePresentation, a => { a.PresentationId = "p1"; a.SlideIndex = 2; }),
            ],
            Enabled = false, Notes = "Booth Mac only",
        };
        var decoded = MxuJson.Deserialize<ScheduleTrigger>(MxuJson.Serialize(trigger));
        Assert.True(Make.Same(trigger, decoded));
    }

    [Fact]
    public void ScheduleTriggerOptionalsStayAbsentWhenNil()
    {
        var trigger = new ScheduleTrigger { Id = "tr1", Name = "Bare" };
        var text = MxuJson.Serialize(trigger);
        var json = JsonNode.Parse(text)!.AsObject();
        Assert.False(json.ContainsKey("enabled"));
        Assert.False(json.ContainsKey("notes"));
        Assert.False(json.ContainsKey("archived"));
        Assert.Null(MxuJson.Deserialize<ScheduleTrigger>(text).Enabled);
    }

    private static SchedulerBoard MakeBoard(params string[] triggers)
    {
        var board = new SchedulerBoard { Id = SchedulerBoardLogic.WellKnownId };
        board.Reconcile(triggers);
        return board;
    }

    [Fact]
    public void BoardReconcileAppendsUnknownAndPrunesGone()
    {
        var board = MakeBoard("a", "b");
        Assert.Equal(["a", "b"], board.AllTriggerIds());

        var folderId = board.AddFolder("Sundays");
        board.MoveTriggerIntoFolder("b", folderId);
        board.Reconcile(["a", "c"]);
        Assert.Equal(["a", "c"], board.AllTriggerIds().Order());
        Assert.Empty(board.Folder(folderId)!.TriggerIds);
        Assert.NotNull(board.Folder(folderId));
    }

    [Fact]
    public void BoardTriggerAppearsExactlyOnceThroughMoves()
    {
        var board = MakeBoard("a", "b", "c");
        var folderId = board.AddFolder("F");

        board.MoveTriggerIntoFolder("a", folderId);
        Assert.Single(board.AllTriggerIds(), "a");
        Assert.Equal(folderId, board.FolderContaining("a")?.Id);

        board.MoveTrigger("b", "a");
        Assert.Equal(folderId, board.FolderContaining("b")?.Id);
        Assert.Equal(["b", "a"], board.Folder(folderId)!.TriggerIds);

        board.MoveTrigger("a", "c");
        Assert.Null(board.FolderContaining("a"));
        Assert.Equal(["a", "c"], board.Nodes.Take(2));
        Assert.Equal(["a", "c", "b"], board.AllTriggerIds());
        Assert.Single(board.AllTriggerIds(), "a");
    }

    [Fact]
    public void BoardDissolveReturnsMembersInPlace()
    {
        var board = MakeBoard("a", "b", "c");
        var folderId = board.AddFolder("F");
        board.MoveTriggerIntoFolder("b", folderId);
        board.MoveFolder(folderId, "c");

        board.RemoveFolder(folderId);
        Assert.Equal(["a", "b", "c"], board.AllTriggerIds());
        Assert.Empty(board.Folders);
        Assert.Equal(["a", "b", "c"], board.Nodes);
    }

    [Fact]
    public void BoardFolderEnabledGatesItsMembers()
    {
        var board = MakeBoard("a", "b");
        var folderId = board.AddFolder("F");
        board.MoveTriggerIntoFolder("a", folderId);

        Assert.True(board.FolderEnabled("a"));
        Assert.True(board.FolderEnabled("b"));

        board.SetFolderEnabled(folderId, false);
        Assert.False(board.FolderEnabled("a"));
        Assert.True(board.FolderEnabled("b"));

        board.SetFolderEnabled(folderId, true);
        Assert.Null(board.Folder(folderId)!.Enabled);
        Assert.True(board.FolderEnabled("a"));
    }

    [Fact]
    public void BoardPlaceTriggerLandsBesideItsSibling()
    {
        var board = MakeBoard("a", "b");

        board.PlaceTrigger("a2", "a");
        Assert.Equal(["a", "a2", "b"], board.AllTriggerIds());

        var folderId = board.AddFolder("F");
        board.MoveTriggerIntoFolder("b", folderId);
        board.PlaceTrigger("b2", "b");
        Assert.Equal(["b", "b2"], board.Folder(folderId)!.TriggerIds);

        board.PlaceTrigger("a", "b");
        Assert.Single(board.AllTriggerIds(), "a");
        Assert.Null(board.FolderContaining("a"));
    }

    [Fact]
    public void BoardDuplicateFolderCopiesShapeAfterOriginal()
    {
        var board = MakeBoard("a", "b");
        var folderId = board.AddFolder("Sundays");
        board.MoveTriggerIntoFolder("a", folderId);
        board.SetFolderEnabled(folderId, false);
        board.MoveFolder(folderId, "b");

        var copyId = board.DuplicateFolder(folderId, ["a2"]);
        Assert.NotNull(copyId);
        var copy = board.Folder(copyId)!;
        Assert.Equal("Sundays Copy", copy.Name);
        Assert.False(copy.Enabled);
        Assert.Equal(["a2"], copy.TriggerIds);
        Assert.Equal([folderId, copyId, "b"], board.Nodes);

        var secondId = board.DuplicateFolder(folderId, ["a"]);
        Assert.NotNull(secondId);
        Assert.Empty(board.Folder(secondId)!.TriggerIds);
        Assert.Single(board.AllTriggerIds(), "a");

        Assert.Null(board.DuplicateFolder("ghost", []));
    }

    [Fact]
    public void BoardRoundTripsThroughJson()
    {
        var board = MakeBoard("a", "b");
        var folderId = board.AddFolder("Sundays");
        board.MoveTriggerIntoFolder("a", folderId);
        board.SetFolderEnabled(folderId, false);

        var decoded = MxuJson.Deserialize<SchedulerBoard>(MxuJson.Serialize(board));
        Assert.True(Make.Same(board, decoded));
        Assert.False(decoded.FolderEnabled("a"));
    }

    // C#: the enabled flag is stored as the Mac stores it (on = absent).
    [Fact]
    public void TriggerEnabledIsStoredAsAbsentWhenOn()
    {
        var trigger = new ScheduleTrigger { Id = "t", Name = "t" };
        ScheduleTriggers.SetEnabled(trigger, false);
        Assert.False(trigger.Enabled);
        ScheduleTriggers.SetEnabled(trigger, true);
        Assert.Null(trigger.Enabled);
    }
}
