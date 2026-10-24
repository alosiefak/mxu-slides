using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Library;

// Port of ServiceRunOrderTests (PresenterCoreTests/MediaMissingTests.swift), the itemKind case of
// RunOrderSelectionTests.swift and ServiceMediaReplaceTests.swift.
public class ServiceRunOrderTests
{
    private static ServiceItem Item(string id, ServiceItemKind kind) => new() { Id = id, ItemKind = kind, Name = id, RefId = $"ref-{id}" };

    private static List<string> Ids(IEnumerable<ServiceItem> items) => [.. items.Select(i => i.Id)];

    [Fact]
    public void NextFireableWalksPastHeadersAndTransport()
    {
        List<ServiceItem> items =
        [
            Item("deck", ServiceItemKind.Presentation), Item("head", ServiceItemKind.Header), Item("walkin", ServiceItemKind.Playlist),
            Item("bumper", ServiceItemKind.Media), Item("sermon", ServiceItemKind.Presentation),
        ];
        Assert.Equal("bumper", ServiceRunOrder.NextFireable(items, "deck")?.Id);
        Assert.Equal("sermon", ServiceRunOrder.NextFireable(items, "bumper")?.Id);
        Assert.Null(ServiceRunOrder.NextFireable(items, "sermon"));
        Assert.Null(ServiceRunOrder.NextFireable(items, "missing"));
    }

    [Fact]
    public void HiddenRowsLeaveTheWalkAndTheVisibleList()
    {
        var hidden = Item("hidden", ServiceItemKind.Presentation);
        hidden.HiddenInPresenter = true;
        List<ServiceItem> items = [Item("deck", ServiceItemKind.Presentation), hidden, Item("sermon", ServiceItemKind.Presentation)];
        Assert.Equal("sermon", ServiceRunOrder.NextFireable(ServiceRunOrder.Fireable(items), "deck")?.Id);
        Assert.Equal(["deck", "sermon"], Ids(ServiceRunOrder.Visible(items)));
        Assert.Equal(["hidden"], Ids(ServiceRunOrder.Hidden(items)));
    }

    [Fact]
    public void SelectedPlanTimeFiltersExcludedRowsEverywhere()
    {
        var elevenOnly = Item("eleven", ServiceItemKind.Media);
        elevenOnly.ExcludedTimeHexIds = ["nine"];
        var nineHeader = Item("nine-head", ServiceItemKind.Header);
        nineHeader.ExcludedTimeHexIds = ["eleven"];
        List<ServiceItem> items = [Item("deck", ServiceItemKind.Presentation), nineHeader, elevenOnly, Item("sermon", ServiceItemKind.Presentation)];

        Assert.Equal(["deck", "eleven", "sermon"], Ids(ServiceRunOrder.Fireable(items)));
        Assert.Equal(["deck", "sermon"], Ids(ServiceRunOrder.Fireable(items, "nine")));
        Assert.Equal(["deck", "nine-head", "sermon"], Ids(ServiceRunOrder.Visible(items, "nine")));
        Assert.Equal(["deck", "eleven", "sermon"], Ids(ServiceRunOrder.SidebarRows(items, new HashSet<string> { "nine-head" }, "eleven")));
    }

    [Fact]
    public void DivergentTimesAreTheOnesWhoseRunDiffersFromTheReference()
    {
        var opener = Item("opener", ServiceItemKind.Presentation);
        opener.MxuItemHexId = "h-opener";
        var sermon = Item("sermon", ServiceItemKind.Presentation);
        sermon.MxuItemHexId = "h-sermon";
        var baptism = Item("baptism", ServiceItemKind.Info);
        baptism.MxuItemHexId = "h-baptism";
        baptism.ExcludedTimeHexIds = ["nine", "five"];
        List<MxUPlanTime> times =
        [
            new() { HexId = "nine", Name = "9:00 am" }, new() { HexId = "eleven", Name = "11:00 am" }, new() { HexId = "five", Name = "5:00 pm" },
        ];
        List<ServiceItem> sameRows = [opener, sermon];
        List<ServiceItem> items = [opener, baptism, sermon];
        List<MxUPlanTimeOrder> orders =
        [
            new() { TimeHexId = "nine", ItemHexIds = ["h-opener", "h-sermon"] },
            new() { TimeHexId = "eleven", ItemHexIds = ["h-sermon", "h-baptism", "h-opener"] },
            new() { TimeHexId = "five", ItemHexIds = ["h-opener", "h-sermon"] },
        ];
        static List<string> Hex(IEnumerable<MxUPlanTime> found) => [.. found.Select(t => t.HexId)];

        Assert.Empty(ServiceRunOrder.DivergentTimes(sameRows, times, null, null));
        Assert.Empty(ServiceRunOrder.DivergentTimes(sameRows, times, null, "five"));
        Assert.Equal(["eleven"], Hex(ServiceRunOrder.DivergentTimes(items, times, null, null)));
        Assert.Equal(["nine", "five"], Hex(ServiceRunOrder.DivergentTimes(items, times, null, "eleven")));

        List<MxUPlanTimeOrder> reordered = [new() { TimeHexId = "five", ItemHexIds = ["h-sermon", "h-opener"] }];
        Assert.Equal(["five"], Hex(ServiceRunOrder.DivergentTimes(sameRows, times, reordered, "nine")));
        Assert.Equal(["eleven"], Hex(ServiceRunOrder.DivergentTimes(items, times, orders, "nine")));

        var hiddenBaptism = Core.Json.MxuJson.Clone(baptism);
        hiddenBaptism.HiddenInPresenter = true;
        Assert.Empty(ServiceRunOrder.DivergentTimes([opener, hiddenBaptism, sermon], times, null, null));

        Assert.Empty(ServiceRunOrder.DivergentTimes(items, [times[0]], null, null));
        Assert.Empty(ServiceRunOrder.DivergentTimes(items, times, null, "gone"));
    }

    [Fact]
    public void OrderedFollowsTheTimeSequenceAndLocalRowsFollowTheirAnchor()
    {
        var opener = Item("opener", ServiceItemKind.Presentation);
        opener.MxuItemHexId = "h-opener";
        var sermon = Item("sermon", ServiceItemKind.Presentation);
        sermon.MxuItemHexId = "h-sermon";
        var closer = Item("closer", ServiceItemKind.Media);
        closer.MxuItemHexId = "h-closer";
        var local = Item("walk-in", ServiceItemKind.Media);
        List<ServiceItem> items = [opener, local, sermon, closer];
        Assert.Equal(["opener", "walk-in", "closer", "sermon"], Ids(ServiceRunOrder.Ordered(items, ["h-opener", "h-closer", "h-sermon"])));
        Assert.Equal(["opener", "walk-in", "sermon", "closer"], Ids(ServiceRunOrder.Ordered(items, null)));

        var extra = Item("extra", ServiceItemKind.Presentation);
        extra.MxuItemHexId = "h-extra";
        Assert.Equal(["extra", "closer", "opener", "walk-in", "sermon"], Ids(ServiceRunOrder.Ordered([extra, .. items], ["h-closer", "h-opener"])));
        Assert.Equal(["closer", "opener", "walk-in", "sermon"], Ids(ServiceRunOrder.Fireable(items, order: ["h-closer", "h-opener"])));
    }

    [Fact]
    public void FireableNumbersPresentationsAndMediaOnly()
    {
        var hidden = Item("hidden", ServiceItemKind.Media);
        hidden.HiddenInPresenter = true;
        List<ServiceItem> items =
        [
            Item("head", ServiceItemKind.Header), Item("deck", ServiceItemKind.Presentation), hidden,
            Item("clip", ServiceItemKind.Media), Item("bed", ServiceItemKind.Audio), Item("sermon", ServiceItemKind.Presentation),
        ];
        Assert.Equal(["deck", "clip", "sermon"], Ids(ServiceRunOrder.Fireable(items)));
    }

    [Fact]
    public void HiddenHeaderUngroupsItsItemsInsteadOfJoiningThePreviousGroup()
    {
        var worship = Item("worship", ServiceItemKind.Header);
        worship.HiddenInPresenter = true;
        List<ServiceItem> items = [Item("pre", ServiceItemKind.Header), Item("walk-in", ServiceItemKind.Media), worship, Item("song", ServiceItemKind.Presentation)];
        Assert.Equal(["pre", "song"], Ids(ServiceRunOrder.SidebarRows(items, new HashSet<string> { "pre" })));
        Assert.Equal(["pre", "walk-in", "song"], Ids(ServiceRunOrder.SidebarRows(items, new HashSet<string>())));
    }

    [Fact]
    public void LibraryDocumentsBecomeTheirRowKinds()
    {
        Assert.Equal(ServiceItemKind.Presentation, ServiceRunOrder.ItemKindAdding(typeof(Presentation)));
        Assert.Equal(ServiceItemKind.Media, ServiceRunOrder.ItemKindAdding(typeof(MediaItem)));
        Assert.Equal(ServiceItemKind.Audio, ServiceRunOrder.ItemKindAdding(typeof(AudioItem)));
        Assert.Equal(ServiceItemKind.Playlist, ServiceRunOrder.ItemKindAdding(typeof(Playlist)));
        Assert.Null(ServiceRunOrder.ItemKindAdding(typeof(Theme)));
        Assert.Null(ServiceRunOrder.ItemKindAdding(typeof(Service)));
    }

    [Fact]
    public void ReplaceMediaRepointsOnlyThatRow()
    {
        List<ServiceItem> items =
        [
            new() { Id = "a", ItemKind = ServiceItemKind.Media, Name = "Old", RefId = "m1" },
            new() { Id = "b", ItemKind = ServiceItemKind.Media, Name = "Old", RefId = "m1" },
        ];
        ServiceRunOrder.ReplaceMedia(items, "a", "m2", "New");
        Assert.Equal("m2", items[0].RefId);
        Assert.Equal("New", items[0].Name); // the row takes the new item's name
        Assert.Equal("m1", items[1].RefId); // another row using the same media is untouched
    }

    [Fact]
    public void ReplaceMediaLeavesOtherKindsAlone()
    {
        List<ServiceItem> items = [new() { Id = "a", ItemKind = ServiceItemKind.Presentation, Name = "Deck", RefId = "p1" }];
        ServiceRunOrder.ReplaceMedia(items, "a", "m2", "New");
        Assert.Equal("p1", items[0].RefId);
    }
}
