using MxU.Slides.Core.Media;
using MxU.Slides.Core.Scene;
using MxU.Slides.VideoIO.Common;
using MxU.Slides.VideoIO.DeckLink;

namespace MxU.Slides.VideoIO.Tests.DeckLink;

/// <summary>Windows: a DeckLink input kept alive, against a pretend card (port of DeckLinkDirectCapture's behaviour).</summary>
public sealed class DeckLinkCaptureTests
{
    private sealed class FakeCard
    {
        public readonly Queue<string?> Script = new();
        public int Opens;
        public DeckLinkFrameHandler? Frames;
        public Action<DeckLinkInputFormat>? Formats;
        public int Closed;

        public IDisposable? Open(long id, int channels, DeckLinkFrameHandler frames, Action<DeckLinkInputFormat> formats, out string error)
        {
            Interlocked.Increment(ref Opens);
            string? failure;
            lock (Script) failure = Script.Count > 0 ? Script.Dequeue() : null;
            error = failure ?? "";
            if (failure is not null) return null;
            Frames = frames;
            Formats = formats;
            return new Closer(this);
        }

        private sealed class Closer(FakeCard card) : IDisposable
        {
            public void Dispose() => Interlocked.Increment(ref card.Closed);
        }

        /// <summary>Sends one UYVY frame of a single colour, with a couple of audio samples.</summary>
        public void Send(byte y, byte u, byte v, bool signal = true, int width = 4, int height = 2)
        {
            var video = new byte[width * 2 * height];
            for (var i = 0; i < video.Length; i += 4)
            {
                video[i] = u;
                video[i + 1] = y;
                video[i + 2] = v;
                video[i + 3] = y;
            }
            int[] audio = [int.MaxValue, int.MinValue, 0, 1 << 30];
            var arrival = new DeckLinkInputArrival
            {
                Video = signal ? video : default,
                RowBytes = width * 2,
                Width = width,
                Height = height,
                HasSignal = signal,
                HostTime = ShowState.HostClock(),
                CaptureHostTime = ShowState.HostClock(),
                Audio = audio,
                AudioFrames = 2,
                AudioChannels = 2,
            };
            Frames!(in arrival);
        }
    }

    private static DeckLinkCapture Capture(FakeCard card) =>
        new(7, "Camera 2 (Direct)", 2, card.Open, retryDelay: TimeSpan.FromMilliseconds(50));

    [Fact]
    public void FramesArriveAsBgraAndTheInputSaysReceiving()
    {
        var card = new FakeCard();
        using var capture = Capture(card);
        capture.Start();
        Assert.True(TestSupport.Eventually(() => card.Frames is not null));
        card.Send(235, 128, 128);
        var frame = capture.LatestFrame();
        Assert.NotNull(frame);
        Assert.Equal((4, 2), (frame.Width, frame.Height));
        Assert.Equal([255, 255, 255, 255], frame.Pixels[..4].ToArray());
        Assert.Equal(VideoInputState.ReceivingFrom("Camera 2 (Direct)"), capture.State);
        Assert.True(capture.IsReceiving(ShowState.HostClock()));
    }

    [Fact]
    public void EmbeddedAudioReachesTheSinksAsFloat()
    {
        var card = new FakeCard();
        using var capture = Capture(card);
        AudioBuffer? heard = null;
        float[]? samples = null;
        capture.AddAudioSink(buffer =>
        {
            heard = buffer;
            samples = buffer.Samples.ToArray();
        });
        capture.Start();
        Assert.True(TestSupport.Eventually(() => card.Frames is not null));
        card.Send(16, 128, 128);
        Assert.NotNull(heard);
        Assert.Equal(2, heard.Channels);
        Assert.Equal(48_000, heard.SampleRate);
        Assert.Equal(new[] { 1f, -1f, 0f, 0.5f }, samples!);
    }

    [Fact]
    public void NoSignalIsReportedAndDrawsNothing()
    {
        var card = new FakeCard();
        using var capture = Capture(card);
        capture.Start();
        Assert.True(TestSupport.Eventually(() => card.Frames is not null));
        card.Send(0, 0, 0, signal: false);
        Assert.Equal(VideoInputState.NoSignal, capture.State);
        Assert.Null(capture.LatestFrame());
    }

    [Fact]
    public void AMissingCardIsRetriedUntilItComesBack()
    {
        var card = new FakeCard();
        card.Script.Enqueue("DeckLink device not found (unplugged, or Desktop Video not installed)");
        card.Script.Enqueue("DeckLink device not found (unplugged, or Desktop Video not installed)");
        using var capture = Capture(card);
        capture.Start();
        Assert.True(TestSupport.Eventually(() => capture.State.Status == VideoInputStatus.Failed));
        Assert.StartsWith("DeckLink device not found", capture.State.Detail, StringComparison.Ordinal);
        Assert.True(TestSupport.Eventually(() => card.Opens == 3 && card.Frames is not null));
        card.Send(235, 128, 128);
        Assert.True(capture.State.IsReceiving);
    }

    [Fact]
    public void ASilentCardIsReopened()
    {
        var card = new FakeCard();
        using var capture = Capture(card);
        capture.Start();
        Assert.True(TestSupport.Eventually(() => card.Frames is not null));
        capture.CheckLiveness(ShowState.HostClock() + 1);
        Thread.Sleep(100);
        Assert.Equal(1, card.Opens);
        capture.CheckLiveness(ShowState.HostClock() + DirectInputLiveness.SilentSeconds + 1);
        capture.CheckLiveness(ShowState.HostClock() + DirectInputLiveness.SilentSeconds + 2); // already reopening: not twice
        Assert.True(TestSupport.Eventually(() => card.Opens == 2 && card.Closed == 1));
        Thread.Sleep(100);
        Assert.Equal(2, card.Opens);
    }

    [Fact]
    public void FramesFromAReplacedOpenAreIgnored()
    {
        var card = new FakeCard();
        using var capture = Capture(card);
        capture.Start();
        Assert.True(TestSupport.Eventually(() => card.Frames is not null));
        var stale = card.Frames!;
        capture.CheckLiveness(ShowState.HostClock() + 10);
        Assert.True(TestSupport.Eventually(() => card.Opens == 2));
        card.Frames = stale;
        card.Send(235, 128, 128);
        Assert.Null(capture.LatestFrame());
    }

    [Fact]
    public void TheHubSharesOneCapturePerCard()
    {
        var card = new FakeCard();
        var hub = new DeckLinkCaptureHub((id, name) => new DeckLinkCapture(id, name, 2, card.Open));
        var picture = hub.Acquire(7, "Cam");
        var audio = hub.Acquire(7, "Cam");
        Assert.Same(picture.Capture, audio.Capture);
        Assert.True(TestSupport.Eventually(() => card.Opens == 1));
        picture.Dispose();
        picture.Dispose(); // a second dispose of the same lease doesn't count twice
        Assert.NotNull(hub.Capture(7));
        audio.Dispose();
        Assert.Null(hub.Capture(7));
        Assert.Equal(VideoInputStatus.Stopped, audio.State.Status);
    }

    [Fact]
    public void WithoutTheDriverTheRealCaptureSaysWhyAndKeepsTrying()
    {
        if (DeckLinkRuntime.IsAvailable) Assert.Skip("Desktop Video is installed on this PC");
        using var capture = new DeckLinkCapture(long.MinValue, "Nowhere");
        capture.Start();
        Assert.True(TestSupport.Eventually(() => capture.State.Status == VideoInputStatus.Failed));
        Assert.StartsWith("DeckLink device not found", capture.State.Detail, StringComparison.Ordinal);
    }
}
