using MxU.Slides.Core.Media;
using MxU.Slides.VideoIO.DeckLink;

namespace MxU.Slides.VideoIO.Tests.DeckLink;

/// <summary>Port of the mode logic in DeckLinkHelper's startOutput.</summary>
public sealed class DeckLinkModeSelectionTests
{
    private static readonly DeckLinkDisplayMode P2997 = new("1080p29.97", 0x48703239, 1920, 1080, 1001, 30000, true, SupportsKeying: true);
    private static readonly DeckLinkDisplayMode P30 = new("1080p30", 0x48703330, 1920, 1080, 1000, 30000, true, SupportsKeying: false);
    private static readonly DeckLinkDisplayMode P25 = new("1080p25", 0x48703235, 1920, 1080, 1000, 25000, true, SupportsKeying: true);
    private static readonly DeckLinkDisplayMode P720 = new("720p59.94", 0x68703539, 1280, 720, 1001, 60000, true);

    private static (DeckLinkModeChoice? Choice, string? Error) Choose(
        IReadOnlyList<DeckLinkDisplayMode> modes, DeckLinkKeying keying = DeckLinkKeying.Off, uint? configured = null,
        long requested = 0, bool locked = true) =>
        DeckLinkModeSelection.Choose(modes, keying, configured, 1920, 1080, 30_000, 1_000, requested, () => locked);

    [Fact]
    public void AutoPicksTheNearestMode() => Assert.Equal(new DeckLinkModeChoice(P2997.ModeId, "1080p29.97 (Auto)"), Choose([P30, P2997, P25]).Choice);

    [Fact]
    public void TheCardsConfiguredModeWinsWhenItIsTheProgramsSize() =>
        Assert.Equal(new DeckLinkModeChoice(P25.ModeId, "1080p25 (Auto)"), Choose([P30, P2997, P25], configured: P25.ModeId).Choice);

    [Fact]
    public void AConfiguredModeOfAnotherSizeIsPassedOver() =>
        Assert.Equal(P2997.ModeId, Choose([P2997, P720], configured: P720.ModeId).Choice?.ModeId);

    [Fact]
    public void KeyingOnlyConsidersModesThatCanKey() =>
        Assert.Null(Choose([P30], DeckLinkKeying.Internal).Choice);

    [Fact]
    public void AChosenFormatIsUsedAsIs() =>
        Assert.Equal(new DeckLinkModeChoice(P720.ModeId, "720p59.94"), Choose([P2997, P720], requested: P720.ModeId).Choice);

    [Fact]
    public void NoModesAtAllMeansTheConnectorIsAnInput() =>
        Assert.StartsWith("no playback modes", Choose([]).Error, StringComparison.Ordinal);

    [Fact]
    public void NoMatchingModeAsksForAFormat() =>
        Assert.Equal("no SDI mode matches 1920×1080 at 30000/1000 fps — choose a Format to send this screen scaled", Choose([P720, P25]).Error);

    [Fact]
    public void ExternalKeyingWithoutReferenceSaysSo()
    {
        Assert.Equal("1080p29.97 (Auto) · no reference", Choose([P2997], DeckLinkKeying.External, locked: false).Choice?.WireDetail);
        Assert.Equal("1080p29.97 (Auto)", Choose([P2997], DeckLinkKeying.External, locked: true).Choice?.WireDetail);
    }
}

/// <summary>
/// Windows: the controller's recovery logic against a pretend card (port of the behaviour of
/// DeckLinkOutputController.swift). Delays are shortened so the tests run in well under a second each.
/// </summary>
public sealed class DeckLinkOutputControllerTests
{
    private sealed class FakePlayout : IDeckLinkPlayout
    {
        private readonly DeckLinkOutputTiming timing = new();
        public volatile bool Accepts = true;
        public volatile bool Disposed;
        public int Shown;

        public bool Display(VideoFrame frame)
        {
            if (!Accepts) return false;
            Interlocked.Increment(ref Shown);
            lock (timing)
            {
                timing.Latch();
                timing.TallyCompletion(Shown / 30.0);
            }
            return true;
        }

        public DeckLinkOutputTiming Timing
        {
            get { lock (timing) return timing.Copy(); }
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeBackend : IDeckLinkOutputBackend
    {
        public readonly Queue<string?> Script = new();
        public readonly List<FakePlayout> Opened = [];
        public int Opens;

        public (IDeckLinkPlayout?, string?, string) Open(DeckLinkOutputRequest request)
        {
            Interlocked.Increment(ref Opens);
            string? error;
            lock (Script) error = Script.Count > 0 ? Script.Dequeue() : null;
            if (error is not null) return (null, error, "");
            var playout = new FakePlayout();
            lock (Opened) Opened.Add(playout);
            return (playout, null, "1080p29.97 (Auto)");
        }

        public FakePlayout Last
        {
            get { lock (Opened) return Opened[^1]; }
        }
    }

    private static readonly DeckLinkOutputRequest Request = new("screen-1", "Program", 42, "DeckLink Duo 2 (1)", DeckLinkKeying.Off, 1920, 1080, 30);

    private static DeckLinkOutputController Controller(FakeBackend backend) => new(backend,
        reconnectDelay: TimeSpan.FromMilliseconds(50), stallTimeout: TimeSpan.FromMilliseconds(300),
        watchdogInterval: TimeSpan.FromMilliseconds(50), openTimeout: TimeSpan.FromSeconds(5));

    private static readonly VideoFrame Frame = TestSupport.Solid(1920, 1080, 0, 0, 0);

    [Fact]
    public void AGoodOpenSends()
    {
        var backend = new FakeBackend();
        using var controller = Controller(backend);
        controller.Start(Request);
        Assert.True(TestSupport.Eventually(() => controller.Status.State == DeckLinkOutputState.Sending));
        Assert.Equal("DeckLink Duo 2 (1)", controller.Status.Detail);
        Assert.Equal("1080p29.97 (Auto)", controller.WireDetail);
        controller.WriteVideo(Frame);
        Assert.Equal(1, backend.Last.Shown);
        Assert.Equal(1, controller.FramesDelivered);
    }

    [Fact]
    public void AHardFailureStops()
    {
        var backend = new FakeBackend();
        backend.Script.Enqueue("device does not support playback");
        using var controller = Controller(backend);
        controller.Start(Request);
        Assert.True(TestSupport.Eventually(() => controller.Status.State == DeckLinkOutputState.Failed));
        Assert.Equal("device does not support playback", controller.Status.Detail);
        Thread.Sleep(200);
        Assert.Equal(1, backend.Opens); // no retries
    }

    [Fact]
    public void AnUnpluggedCardIsRetriedAndOnlyConfirmedByADeliveredFrame()
    {
        var backend = new FakeBackend();
        backend.Script.Enqueue("DeckLink device not found (unplugged, or Desktop Video not installed)");
        backend.Script.Enqueue("DeckLink device not found (unplugged, or Desktop Video not installed)");
        using var controller = Controller(backend);
        controller.Start(Request);
        Assert.True(TestSupport.Eventually(() => controller.Status.State == DeckLinkOutputState.Interrupted));
        Assert.True(TestSupport.Eventually(() => backend.Opens >= 3 && backend.Opened.Count == 1));
        Thread.Sleep(100);
        Assert.Equal(DeckLinkOutputState.Interrupted, controller.Status.State); // open, but nothing delivered yet
        controller.WriteVideo(Frame);
        Assert.True(TestSupport.Eventually(() => controller.Status.State == DeckLinkOutputState.Sending));
    }

    [Fact]
    public void ASecondOfRefusedFramesReconnects()
    {
        var backend = new FakeBackend();
        using var controller = Controller(backend);
        controller.Start(Request);
        Assert.True(TestSupport.Eventually(() => controller.Status.State == DeckLinkOutputState.Sending));
        var first = backend.Last;
        first.Accepts = false;
        for (var i = 0; i < Request.FrameRate; i++) controller.WriteVideo(Frame);
        Assert.True(TestSupport.Eventually(() => backend.Opened.Count == 2));
        Assert.True(TestSupport.Eventually(() => first.Disposed));
        controller.WriteVideo(Frame);
        Assert.True(TestSupport.Eventually(() => controller.Status.State == DeckLinkOutputState.Sending));
        Assert.Equal(Request.FrameRate, controller.FramesRefused);
    }

    [Fact]
    public void TheWatchdogRestartsAnOutputThatStoppedDelivering()
    {
        var backend = new FakeBackend();
        using var controller = Controller(backend);
        controller.Start(Request);
        Assert.True(TestSupport.Eventually(() => controller.Status.State == DeckLinkOutputState.Sending));
        // No frames at all for longer than the stall timeout.
        Assert.True(TestSupport.Eventually(() => backend.Opened.Count >= 2, 3));
    }

    [Fact]
    public void StopClosesTheCardAndForgetsTheRequest()
    {
        var backend = new FakeBackend();
        var controller = Controller(backend);
        controller.Start(Request);
        Assert.True(TestSupport.Eventually(() => controller.Status.State == DeckLinkOutputState.Sending));
        controller.Stop();
        Assert.Equal(DeckLinkOutputStatus.Idle, controller.Status);
        Assert.Null(controller.Request);
        Assert.True(TestSupport.Eventually(() => backend.Last.Disposed));
        controller.WriteVideo(Frame); // ignored, never throws
        Thread.Sleep(400);
        Assert.Equal(1, backend.Opens); // and the watchdog is gone
    }

    [Fact]
    public void WithoutTheDriverTheRealControllerFailsWithTheInstallMessage()
    {
        if (DeckLinkRuntime.IsAvailable) Assert.Skip("Desktop Video is installed on this PC");
        using var controller = new DeckLinkOutputController();
        controller.Start(Request);
        Assert.True(TestSupport.Eventually(() => controller.Status.State == DeckLinkOutputState.Failed));
        Assert.Equal(DeckLinkRuntime.NotInstalledMessage, controller.Status.Detail);
    }

    [Theory]
    [InlineData("DeckLink device not found (unplugged, or Desktop Video not installed)", true)]
    [InlineData("video output enable failed (output may be claimed by another app)", true)]
    [InlineData("device does not support keying", false)]
    public void OnlyMissingOrBusyCardsAreTransient(string error, bool transient) =>
        Assert.Equal(transient, DeckLinkOutputController.IsTransientOpenFailure(error));
}
