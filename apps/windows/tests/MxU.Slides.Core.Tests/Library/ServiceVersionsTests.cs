using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Library;

// Port of PresenterCoreTests/ServiceVersionsTests.swift. Not ported: the Automerge merge test
// (Windows has no Automerge) and the sweep test that reads the Mac's AppModel source; the app's
// routing of edits into the running version is checked by the self-test instead.
public class ServiceVersionsTests
{
    private static ServiceItem Row(string id, string reference = "", string? hex = null) =>
        new() { Id = id, ItemKind = ServiceItemKind.Presentation, Name = id, RefId = reference, MxuItemHexId = hex };

    private static Service MainService() => new()
    {
        Id = "plan-p", Name = "Sunday", ServiceDate = "2026-10-04",
        Items = [Row("a", "deck-a", "ha"), Row("b", "deck-b", "hb"), Row("c", "deck-c", "hc"), Row("d", "deck-d", "hd")],
    };

    private static string North(Service service) => ServiceVersions.Create("North Campus", service, "north");

    /// <summary>Edits the service as North sees it; returns what North now shows (the new Main is written back).</summary>
    private static Service EditNorth(ref Service service, Action<Service> mutate)
    {
        service = ServiceVersions.Edit(service, "north", mutate);
        return ServiceVersions.Apply(service.Versions![0], service);
    }

    private static void Move(List<ServiceItem> items, int from, int toOffset)
    {
        // Swift's move(fromOffsets: [from], toOffset:): the offset counts positions before the move.
        var item = items[from];
        items.RemoveAt(from);
        items.Insert(toOffset > from ? toOffset - 1 : toOffset, item);
    }

    [Fact]
    public void ANewVersionRunsMain()
    {
        var service = MainService();
        North(service);
        Assert.True(MxuJson.StructurallyEqual(MainService().Items, ServiceVersions.Apply(service.Versions![0], service).Items));
    }

    [Fact]
    public void EditsWhileRunningAVersionLandInItAndLeaveMainAlone()
    {
        var service = MainService();
        North(service);
        var seen = EditNorth(ref service, run =>
        {
            run.Items[1].RefId = "deck-north";
            run.Items[2].ArrangementId = "acoustic";
            run.Items.RemoveAll(i => i.Id == "d");
            run.Items.Insert(1, Row("x", "deck-x"));
            Move(run.Items, 0, 4);
        });
        Assert.Equal(["x", "b", "c", "a", "d"], seen.Items.Select(i => i.Id));
        Assert.Equal(["deck-x", "deck-north", "deck-c", "deck-a", "deck-d"], seen.Items.Select(i => i.RefId));
        Assert.Equal("acoustic", seen.Items[2].ArrangementId);
        Assert.True(seen.Items[4].HiddenInPresenter); // a removed Main row is skipped, so Unhide brings it back
        Assert.True(MxuJson.StructurallyEqual(MainService().Items, service.Items)); // Main is untouched
    }

    [Fact]
    public void SharedRowFieldsAndServiceFieldsStillReachMain()
    {
        var service = MainService();
        North(service);
        EditNorth(ref service, run =>
        {
            run.Name = "Sunday AM";
            run.Items[0].ColorHex = "#FF0000FF";
            run.Items[0].RefId = "deck-north";
        });
        Assert.Equal("Sunday AM", service.Name);
        Assert.Equal("#FF0000FF", service.Items[0].ColorHex);
        Assert.Equal("deck-a", service.Items[0].RefId);
    }

    [Fact]
    public void AVersionKeepsItsChangesWhenMainChangesAndFollowsMainsNewAndGoneRows()
    {
        var service = MainService();
        North(service);
        EditNorth(ref service, run => run.Items[1].RefId = "deck-north");

        service.Items[1].ArrangementId = "full-band";
        service.Items[1].RefId = "deck-b2";
        service.Items.RemoveAll(i => i.Id == "c");
        service.Items.Insert(2, Row("e", "deck-e"));
        var seen = ServiceVersions.Apply(service.Versions![0], service);
        Assert.Equal(["a", "b", "e", "d"], seen.Items.Select(i => i.Id));
        Assert.Equal("deck-north", seen.Items[1].RefId); // the version's swap wins
        Assert.Equal("full-band", seen.Items[1].ArrangementId); // fields it did not change follow Main
    }

    [Fact]
    public void MovesSurviveASecondEditAndUseMainDropsARowsChanges()
    {
        var service = MainService();
        North(service);
        EditNorth(ref service, run => Move(run.Items, 3, 0));
        var seen = EditNorth(ref service, run => run.Items[2].RefId = "deck-north");
        Assert.Equal(["d", "a", "b", "c"], seen.Items.Select(i => i.Id));
        Assert.Equal(new HashSet<string> { "d", "b" }, ServiceVersions.ChangedItemIds(service.Versions![0]));
        ServiceVersions.UseMain("b", "north", service);
        Assert.Equal("deck-b", ServiceVersions.Apply(service.Versions![0], service).Items[2].RefId);
    }

    [Fact]
    public void AComputerRunsTheVersionNamingItElseTheOneItPickedLastElseMain()
    {
        var service = MainService();
        North(service);
        ServiceVersions.Create("Chapel", service, "chapel");
        Assert.Null(ServiceVersions.Running(service, "booth", null));
        Assert.Equal("north", ServiceVersions.Running(service, "laptop", "north campus")?.Id);
        ServiceVersions.Choose("chapel", service, new ServiceVersionStation { Id = "laptop", Name = "Laptop" });
        Assert.Equal("chapel", ServiceVersions.Running(service, "laptop", "North Campus")?.Id);
        Assert.Equal(["Laptop"], service.Versions![1].Stations!.Select(s => s.Name)); // every computer sees who runs it
        ServiceVersions.Choose(null, service, new ServiceVersionStation { Id = "laptop", Name = "Laptop" });
        Assert.All(service.Versions, v => Assert.Null(v.Stations));
    }

    [Fact]
    public void EveryVersionsRowsAreKeptInAllItems()
    {
        // The Mac's sync-guard test: nothing a version runs is tidied away as unused.
        var service = MainService();
        North(service);
        EditNorth(ref service, run =>
        {
            run.Items.Add(new ServiceItem { Id = "m", ItemKind = ServiceItemKind.Media, Name = "Loop", RefId = "media-north" });
            run.Items[0].RefId = "deck-north";
        });
        var references = ServiceVersions.AllItems(service).Select(i => i.RefId).ToHashSet();
        Assert.Superset(new HashSet<string> { "deck-a", "deck-north", "media-north" }, references);
        Assert.Equal(["media-north"], ServiceVersions.AllItems(service).Where(i => i.ItemKind == ServiceItemKind.Media).Select(i => i.RefId).Distinct());
    }

    [Fact]
    public void EditingAVersionThatIsGoneChangesNothing()
    {
        var service = MainService();
        var after = ServiceVersions.Edit(service, "missing", run => run.Items.Clear());
        Assert.Same(service, after);
        Assert.Equal(4, after.Items.Count);
    }

    [Fact]
    public void HidingARowInAVersionIsRecordedAsAChangeAndShownHidden()
    {
        var service = MainService();
        North(service);
        var seen = EditNorth(ref service, run => run.Items[2].HiddenInPresenter = true);
        Assert.True(seen.Items[2].HiddenInPresenter);
        Assert.Null(service.Items[2].HiddenInPresenter); // Main still runs it
        Assert.Equal(new HashSet<string> { "c" }, ServiceVersions.ChangedItemIds(service.Versions![0]));
    }
}
