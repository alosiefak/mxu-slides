using MxU.Slides.Core.Outputs;
using MxU.Slides.Core.Scene;
using static MxU.Slides.Core.Outputs.OutputTopology;

namespace MxU.Slides.Core.Tests.Outputs;

// Ported from OutputEngineTests/OutputTopologyTests.swift.
public class OutputTopologyTests
{
    private static readonly SceneRect Hd = new(0, 0, 1920, 1080);

    private static DisplaySnapshot Display(string id, SceneRect? frame = null) =>
        new(id, $"Display {id}", frame ?? Hd);

    private static IReadOnlySet<string> Set(params string[] ids) => ids.ToHashSet();

    [Fact]
    public void SurvivorUntouchedWhenNeighborUnplugs()
    {
        var changes = Reconcile(
            Set("A", "B"),
            new Dictionary<string, SceneRect> { ["A"] = Hd, ["B"] = new(1920, 0, 1280, 720) },
            [Display("A", Hd)]);
        Assert.Equal([new Change.Close("B")], changes);
    }

    [Fact]
    public void ReplugRestoresAssignedDisplay()
    {
        var changes = Reconcile(
            Set("A", "B"),
            new Dictionary<string, SceneRect> { ["A"] = Hd },
            [Display("A"), Display("B", new SceneRect(1920, 0, 1280, 720))]);
        Assert.Equal([new Change.Open("B")], changes);
    }

    [Fact]
    public void OriginShiftReframesWithoutRebuild()
    {
        var shifted = new SceneRect(0, 0, 1920, 1080);
        var changes = Reconcile(
            Set("A"),
            new Dictionary<string, SceneRect> { ["A"] = new(2560, 0, 1920, 1080) },
            [Display("A", shifted)]);
        Assert.Equal([new Change.Reframe("A", shifted)], changes);
    }

    [Fact]
    public void UnassignedConnectedDisplayGetsNoWindow() =>
        Assert.Empty(Reconcile(Set(), new Dictionary<string, SceneRect>(), [Display("A")]));

    [Fact]
    public void UnassignClosesWindow()
    {
        var changes = Reconcile(Set(), new Dictionary<string, SceneRect> { ["A"] = Hd }, [Display("A")]);
        Assert.Equal([new Change.Close("A")], changes);
    }

    [Fact]
    public void AssignedButDisconnectedIsPatientlyIgnored() =>
        Assert.Empty(Reconcile(Set("GONE"), new Dictionary<string, SceneRect>(), []));

    [Fact]
    public void SteadyStateIsANoOp()
    {
        var changes = Reconcile(
            Set("A", "B"),
            new Dictionary<string, SceneRect> { ["A"] = Hd, ["B"] = new(1920, 0, 1280, 720) },
            [Display("A"), Display("B", new SceneRect(1920, 0, 1280, 720))]);
        Assert.Empty(changes);
    }

    [Fact]
    public void ClosesOrderedBeforeOpens()
    {
        var changes = Reconcile(Set("B"), new Dictionary<string, SceneRect> { ["A"] = Hd }, [Display("A"), Display("B")]);
        Assert.Equal([new Change.Close("A"), new Change.Open("B")], changes);
    }
}
