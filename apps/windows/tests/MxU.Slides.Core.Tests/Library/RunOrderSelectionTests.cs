using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Library;

// Ported from PresenterCoreTests/ListMultiSelectTests.swift and RunOrderSelectionTests.swift
// (the marquee sweep isn't: Windows' list does its own Ctrl/Shift selecting), plus the bulk
// menu's edits (AppModel.setServiceItemTransition, hide, remove).
public class RunOrderSelectionTests
{
    private static readonly List<string> Order = ["a", "b", "c", "d", "e"];

    private static ListMultiSelect.State State(string? anchor, params string[] selected) => new(new HashSet<string>(selected), anchor);

    [Fact]
    public void PlainClickReplacesAndAnchors() =>
        Assert.Equal(State("c", "c"), ListMultiSelect.Click("c", ListMultiSelect.Gesture.Replace, Order, State("a", "a", "b")));

    [Fact]
    public void ToggleAddsRemovesAndMovesTheAnchor()
    {
        var state = ListMultiSelect.Click("b", ListMultiSelect.Gesture.Toggle, Order, ListMultiSelect.State.Empty);
        Assert.Equal(State("b", "b"), state);
        state = ListMultiSelect.Click("d", ListMultiSelect.Gesture.Toggle, Order, state);
        Assert.Equal(State("d", "b", "d"), state);
        state = ListMultiSelect.Click("b", ListMultiSelect.Gesture.Toggle, Order, state);
        Assert.Equal(State("b", "d"), state); // a second Ctrl-click deselects; the anchor still follows the click
    }

    [Fact]
    public void ExtendSpansAnchorToClickBothWays()
    {
        var down = ListMultiSelect.Click("d", ListMultiSelect.Gesture.Extend, Order, State("b", "b"));
        Assert.True(down.Selected.SetEquals(["b", "c", "d"]));
        Assert.Equal("b", down.Anchor); // Shift never moves the anchor
        var up = ListMultiSelect.Click("a", ListMultiSelect.Gesture.Extend, Order, down);
        Assert.True(up.Selected.SetEquals(["a", "b", "c", "d"])); // extend adds: the earlier span stays
    }

    [Fact]
    public void ExtendWithoutAnAnchorSelectsTheClickedRow()
    {
        var state = ListMultiSelect.Click("c", ListMultiSelect.Gesture.Extend, Order, ListMultiSelect.State.Empty);
        Assert.Equal(State("c", "c"), state);
    }

    [Fact]
    public void ExtendDegradesOnStaleIds()
    {
        Assert.Equal(State("b", "b"), ListMultiSelect.Click("z", ListMultiSelect.Gesture.Extend, Order, State("b", "b")));
        Assert.True(ListMultiSelect.Click("d", ListMultiSelect.Gesture.Extend, Order, State("z", "b")).Selected.SetEquals(["b", "d"]));
    }

    [Fact]
    public void BatchFollowsTheFindersRule()
    {
        Assert.Equal(["a", "b", "d"], ListMultiSelect.Batch("d", new HashSet<string> { "b", "d", "a" }, Order)); // inside: the whole set, list order
        Assert.Equal(["c"], ListMultiSelect.Batch("c", new HashSet<string> { "b", "d" }, Order));             // outside: just the clicked row
        Assert.Equal(["c"], ListMultiSelect.Batch("c", new HashSet<string> { "c" }, Order));
    }

    private static ServiceItem Item(string id, ServiceItemKind kind = ServiceItemKind.Presentation, bool synced = false) =>
        new() { Id = id, ItemKind = kind, Name = id, RefId = kind == ServiceItemKind.Header ? "" : $"ref-{id}", MxuItemHexId = synced ? $"hex-{id}" : null };

    private static readonly List<ServiceItem> Rows = [Item("h1", ServiceItemKind.Header), Item("a"), Item("b", ServiceItemKind.Media), Item("h2", ServiceItemKind.Header), Item("c")];

    [Fact]
    public void SurfaceItemKeepsTheCurrentWhileItStaysSelected()
    {
        Assert.Equal("c", RunOrderSelection.SurfaceItem(new HashSet<string> { "a", "c" }, "c", Rows)); // growing the selection never moves the centre
        Assert.Equal("c", RunOrderSelection.SurfaceItem(new HashSet<string> { "c" }, "a", Rows));      // a plain click elsewhere does
    }

    [Fact]
    public void SurfaceItemFallsToTheFirstNonHeaderInListOrder() =>
        Assert.Equal("b", RunOrderSelection.SurfaceItem(new HashSet<string> { "c", "h1", "b" }, null, Rows));

    [Fact]
    public void SurfaceItemIgnoresHeadersAndClearsOnEmpty()
    {
        Assert.Equal("a", RunOrderSelection.SurfaceItem(new HashSet<string> { "h1" }, "a", Rows)); // a header has no surface
        Assert.Null(RunOrderSelection.SurfaceItem(new HashSet<string> { "h1" }, null, Rows));
        Assert.Null(RunOrderSelection.SurfaceItem(new HashSet<string>(), "a", Rows));
        Assert.True(RunOrderSelection.IsReselect("a", "a", ctrl: false, shift: false));
        Assert.False(RunOrderSelection.IsReselect("a", "a", ctrl: true, shift: false));
    }

    [Fact]
    public void RemovalSplitsSyncedRowsFromLocalOnesInDocumentOrder()
    {
        List<ServiceItem> items = [Item("h1", ServiceItemKind.Header, synced: true), Item("a", synced: true), Item("b"), Item("c", synced: true)];
        var removal = RunOrderSelection.RemovalOf(["c", "b", "h1", "zzz"], items);
        Assert.Equal(["h1", "b"], removal.Local.Select(i => i.Id)); // headers remove locally even when synced
        Assert.Equal(["c"], removal.Synced.Select(i => i.Id));
        Assert.Equal(["h1", "b", "c"], removal.Ids);
        Assert.Equal(3, removal.Count);
        Assert.Empty(RunOrderSelection.RemovalOf(["b"], items).Synced);
    }

    [Fact]
    public void BulkTransitionTouchesOnlyTheChosenSongs()
    {
        List<ServiceItem> items = [Item("song1"), Item("video", ServiceItemKind.Media), Item("song2"), Item("song3")];
        var crossfade = new SongTransition { Kind = "crossfade", Seconds = 5 };
        Assert.Equal(2, ServiceBulkEdit.SetTransition(items, ["song1", "video", "song3"], crossfade));
        Assert.Equal("crossfade", items[0].Transition?.Kind);
        Assert.Null(items[1].Transition);
        Assert.Null(items[2].Transition);
        // Set All Transitions: every song; each row gets its own copy.
        Assert.Equal(3, ServiceBulkEdit.SetTransition(items, null, crossfade));
        Assert.NotSame(items[0].Transition, items[2].Transition);
        Assert.Equal(3, ServiceBulkEdit.SetTransition(items, null, null));
        Assert.All(items, i => Assert.Null(i.Transition));
    }

    [Fact]
    public void BulkHideAndRemove()
    {
        List<ServiceItem> items = [Item("a"), Item("b"), Item("c")];
        Assert.Equal(2, ServiceBulkEdit.Hide(items, ["a", "c"]));
        Assert.Equal(0, ServiceBulkEdit.Hide(items, ["a"])); // already hidden
        Assert.True(items[0].HiddenInPresenter == true && items[1].HiddenInPresenter is null);
        Assert.Equal(["a", "c"], ServiceBulkEdit.Remove(items, ["c", "a", "zzz"]));
        Assert.Equal(["b"], items.Select(i => i.Id));
        Assert.Equal("3 Items", ServiceBulkEdit.BatchTitle(3));
    }
}
