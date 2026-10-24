using System.Text.Json;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.VideoIO.Capture;
using MxU.Slides.VideoIO.Common;

namespace MxU.Slides.VideoIO.Tests.Capture;

/// <summary>
/// Windows: the inventory's logic (port of the Mac's VideoInputInventory behaviour) with
/// pretend devices, a pretend "is it plugged in" and a scratch file.
/// </summary>
public sealed class VideoInputInventoryTests : IDisposable
{
    private readonly TestSupport.ScratchFolder folder = new();
    private readonly FakeFactory factory = new();
    private readonly Dictionary<string, bool> plugged = [];

    private sealed class FakeFactory : IVideoInputFactory
    {
        public readonly List<(string Kind, string Source, double? Rate, FakeSource Input)> Made = [];
        public bool CamerasFail;
        public int Attempts;

        private FakeSource Make(string kind, string source, string name, double? rate = null)
        {
            Attempts++;
            if (CamerasFail && kind == "camera") throw new InvalidOperationException("the device would not open");
            var input = new FakeSource(name);
            lock (Made) Made.Add((kind, source, rate, input));
            return input;
        }

        public IVideoInputSource Camera(string deviceId, string name, double? frameRate) => Make("camera", deviceId, name, frameRate);

        public IVideoInputSource Ndi(string sourceNameContaining, string name) => Make("ndi", sourceNameContaining, name);

        public IVideoInputSource DeckLink(long persistentId, string name) => Make("decklink", persistentId.ToString(), name);

        public FakeSource Latest(string source)
        {
            lock (Made) return Made.Last(m => m.Source == source).Input;
        }
    }

    private string StorePath => Path.Combine(folder.Path, "video-inputs.json");

    private VideoInputInventory Inventory() =>
        new(StorePath, factory, id => plugged.TryGetValue(id, out var present) ? present : null, Timeout.InfiniteTimeSpan);

    public void Dispose() => folder.Dispose();

    [Fact]
    public void NothingRunsUntilWarmUp()
    {
        using var inventory = Inventory();
        var entry = inventory.Create("Pulpit cam", CaptureSourceKind.Camera, "cam-1");
        Assert.Empty(factory.Made);
        Assert.Null(inventory.Source(entry.EngineId));
        inventory.WarmUp();
        Assert.Single(factory.Made);
        Assert.NotNull(inventory.Source(entry.EngineId));
    }

    [Fact]
    public void EntriesSurviveARestartInTheMacsShape()
    {
        string id;
        using (var inventory = Inventory())
        {
            id = inventory.Create("Stage NDI", CaptureSourceKind.Ndi, "(Program)").Id;
            inventory.SetDelay(id, 3);
        }
        using var json = JsonDocument.Parse(File.ReadAllText(StorePath));
        var stored = json.RootElement[0];
        Assert.Equal("ndi", stored.GetProperty("kind").GetString());
        Assert.Equal("(Program)", stored.GetProperty("sourceId").GetString());
        Assert.Equal(3, stored.GetProperty("delayFrames").GetInt32());
        Assert.False(stored.TryGetProperty("frameRate", out _)); // unset values are left out, as Swift's encoder does
        Assert.Matches("^[0-9A-F-]{36}$", id);

        using var reloaded = Inventory();
        var entry = Assert.Single(reloaded.Entries);
        Assert.Equal(new VideoInputInventory.Entry(id, "Stage NDI", CaptureSourceKind.Ndi, "(Program)", DelayFrames: 3), entry);
    }

    [Fact]
    public void AStoredEntryWithoutAnIdGetsOneAndOneWithoutANameIsSkipped()
    {
        File.WriteAllText(StorePath, """[{"name":"Old cam","kind":"camera","sourceId":"x"},{"kind":"ndi"}]""");
        using var inventory = Inventory();
        var entry = Assert.Single(inventory.Entries);
        Assert.Equal("Old cam", entry.Name);
        Assert.NotEmpty(entry.Id);
    }

    [Fact]
    public void ReassigningStopsTheOldDeviceAndForgetsItsRate()
    {
        using var inventory = Inventory();
        inventory.WarmUp();
        var entry = inventory.Create("Cam", CaptureSourceKind.Camera, "cam-1");
        inventory.SetFrameRate(entry.Id, 50);
        var old = factory.Latest("cam-1");
        inventory.AssignDevice(entry.Id, CaptureSourceKind.Decklink, "42");
        Assert.True(old.Stopped);
        Assert.Equal("decklink", factory.Made[^1].Kind);
        Assert.Null(inventory.EntryFor(entry.Id)!.FrameRate);
    }

    [Fact]
    public void ABadDeckLinkIdIsNotedNotStarted()
    {
        using var inventory = Inventory();
        inventory.WarmUp();
        var entry = inventory.Create("Card", CaptureSourceKind.Decklink, "not-a-number");
        Assert.Empty(factory.Made);
        Assert.Null(inventory.Source(entry.EngineId));
    }

    [Fact]
    public void InputsOnOneCameraShareItsRateAndRestartTogether()
    {
        using var inventory = Inventory();
        inventory.WarmUp();
        var wide = inventory.Create("Wide", CaptureSourceKind.Camera, "cam-1");
        var tight = inventory.Create("Tight", CaptureSourceKind.Camera, "cam-1");
        var other = inventory.Create("Other", CaptureSourceKind.Camera, "cam-2");
        inventory.SetFrameRate(wide.Id, 59.94);
        Assert.Equal(59.94, inventory.EntryFor(tight.Id)!.FrameRate);
        Assert.Null(inventory.EntryFor(other.Id)!.FrameRate);
        Assert.Equal(2, factory.Made.Count(m => m.Source == "cam-1" && m.Rate == 59.94));
    }

    [Fact]
    public void TheFirstStoredRateOnADeviceWinsOnLoad()
    {
        File.WriteAllText(StorePath, """
            [{"id":"A","name":"A","kind":"camera","sourceId":"cam","frameRate":30},
             {"id":"B","name":"B","kind":"camera","sourceId":"cam","frameRate":60}]
            """);
        using var inventory = Inventory();
        Assert.All(inventory.Entries, entry => Assert.Equal(30, entry.FrameRate));
    }

    [Fact]
    public void TheDelayHoldsTheLayerBack()
    {
        using var inventory = Inventory();
        inventory.WarmUp();
        var entry = inventory.Create("Cam", CaptureSourceKind.Camera, "cam-1");
        inventory.SetDelay(entry.Id, 1);
        var source = factory.Latest("cam-1");
        source.Push(10, 1);
        Assert.Equal(10, inventory.LatestFrame(entry.EngineId)!.Pixels.Span[0]);
        source.Push(20, 2);
        Assert.Equal(10, inventory.LatestFrame(entry.EngineId)!.Pixels.Span[0]);
        source.Push(30, 3);
        Assert.Equal(20, inventory.LatestFrame(entry.EngineId)!.Pixels.Span[0]);
        Assert.Null(inventory.LatestFrame("input::item::nobody"));
    }

    [Fact]
    public void AnUnpluggedCameraIsHeldAndRestartsOnReplugAtMostEveryFiveSeconds()
    {
        using var inventory = Inventory();
        inventory.WarmUp();
        plugged["cam-1"] = true;
        var entry = inventory.Create("Cam", CaptureSourceKind.Camera, "cam-1");
        var first = factory.Latest("cam-1");

        plugged["cam-1"] = false;
        inventory.ReconcileCameraHotplug(100);
        Assert.True(first.Stopped);
        Assert.Null(inventory.Source(entry.EngineId));

        plugged["cam-1"] = true;
        inventory.ReconcileCameraHotplug(101);
        Assert.Equal(2, factory.Made.Count);

        plugged["cam-1"] = false;
        inventory.ReconcileCameraHotplug(102);
        plugged["cam-1"] = true;
        inventory.ReconcileCameraHotplug(103); // unplug cleared the throttle, so this one starts
        Assert.Equal(3, factory.Made.Count);
    }

    [Fact]
    public void AFailedStartIsRetriedNoMoreThanEveryFiveSeconds()
    {
        using var inventory = Inventory();
        plugged["cam-1"] = false;
        var entry = inventory.Create("Cam", CaptureSourceKind.Camera, "cam-1");
        inventory.WarmUp();
        Assert.Equal(0, factory.Attempts); // missing at warm-up: noted, not started

        plugged["cam-1"] = true;
        factory.CamerasFail = true;
        inventory.ReconcileCameraHotplug(10);
        Assert.Equal(1, factory.Attempts);
        inventory.ReconcileCameraHotplug(12);
        Assert.Equal(1, factory.Attempts); // too soon
        factory.CamerasFail = false;
        inventory.ReconcileCameraHotplug(15.5);
        Assert.Equal(2, factory.Attempts);
        Assert.NotNull(inventory.Source(entry.EngineId));

        inventory.Remove(entry.Id);
        Assert.True(factory.Made[0].Input.Stopped);
    }

    [Fact]
    public void ConnectedListsTheInputsActuallyDelivering()
    {
        using var inventory = Inventory();
        inventory.WarmUp();
        var cam = inventory.Create("Cam", CaptureSourceKind.Camera, "cam-1");
        var ndi = inventory.Create("NDI", CaptureSourceKind.Ndi, "(Program)");
        IReadOnlyList<string>? heard = null;
        inventory.ConnectedChanged += ids => heard = ids;

        var now = ShowState.HostClock();
        inventory.PollConnectivity(now);
        Assert.Null(heard); // nothing delivering yet, and that's no change

        factory.Latest("cam-1").Push(1, now);
        factory.Latest("(Program)").Become(VideoInputState.ReceivingFrom("PC (Program)"));
        inventory.PollConnectivity(now + 0.5);
        Assert.Equal(new[] { cam.Id, ndi.Id }.Order(StringComparer.Ordinal), heard);

        heard = null;
        inventory.PollConnectivity(now + 3); // the camera's frame is now three seconds old
        Assert.Equal(new[] { ndi.Id }, heard);
    }

    [Fact]
    public void NdiInputsRestForARuntimeSwapAndComeBack()
    {
        using var inventory = Inventory();
        inventory.WarmUp();
        var ndi = inventory.Create("NDI", CaptureSourceKind.Ndi, "(Program)");
        inventory.Create("Cam", CaptureSourceKind.Camera, "cam-1");
        var rested = inventory.RestNdiForRuntimeSwap();
        Assert.Equal(ndi.Id, Assert.Single(rested).Id);
        Assert.Null(inventory.Source(ndi.EngineId));
        inventory.RewarmAfterRuntimeSwap(rested);
        Assert.NotNull(inventory.Source(ndi.EngineId));
    }

    [Fact]
    public void EngineIdsMapBackToEntries()
    {
        using var inventory = Inventory();
        var entry = inventory.Create("Cam");
        Assert.Equal($"input::item::{entry.Id}", entry.EngineId);
        Assert.Equal(entry, inventory.EntryForEngineId(entry.EngineId));
        Assert.True(inventory.IsInventoried(entry.EngineId));
        Assert.False(inventory.IsInventoried(entry.Id));
        Assert.False(entry.IsAssigned);
    }

    [Fact]
    public void RenamingIgnoresBlankNamesAndImportsKeepTheirIds()
    {
        using var inventory = Inventory();
        var entry = inventory.Create("Cam");
        inventory.Rename(entry.Id, "   ");
        Assert.Equal("Cam", inventory.EntryFor(entry.Id)!.Name);
        inventory.Rename(entry.Id, "  Pulpit  ");
        Assert.Equal("Pulpit", inventory.EntryFor(entry.Id)!.Name);
        Assert.True(inventory.ImportEntry("FROM-MAC", "Imported", CaptureSourceKind.Ndi, "x"));
        Assert.False(inventory.ImportEntry("FROM-MAC", "Again", null, null));
        Assert.Equal(2, inventory.Entries.Count);
    }
}
