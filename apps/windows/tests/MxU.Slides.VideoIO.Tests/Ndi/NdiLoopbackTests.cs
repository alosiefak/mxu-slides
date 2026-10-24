using MxU.Slides.VideoIO.Ndi;

namespace MxU.Slides.VideoIO.Tests.Ndi;

/// <summary>
/// Port of NDIKitTests/NDILoopbackTests.swift: a sender on this PC must be receivable on
/// this PC. Needs the NDI runtime (skips without it); briefly puts a test source on the network.
/// </summary>
[Collection(NdiRuntimeCollection.Name)]
public sealed class NdiLoopbackTests
{
    private static readonly int Pid = Environment.ProcessId;

    private static byte[] Blue(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 255; // blue
            pixels[i + 3] = 255; // alpha
        }
        return pixels;
    }

    /// <summary>Sends a blue frame thirty times a second until disposed.</summary>
    private sealed class Pump : IDisposable
    {
        private volatile bool pumping = true;
        private readonly Thread thread;

        public Pump(NdiSender sender, byte[] pixels, int width, int height)
        {
            thread = new Thread(() =>
            {
                while (pumping)
                {
                    sender.Send(pixels, width, height, width * 4);
                    Thread.Sleep(33);
                }
            }) { IsBackground = true };
            thread.Start();
        }

        public void Dispose()
        {
            pumping = false;
            thread.Join();
        }
    }

    [Fact]
    public void RuntimeLoadsAndReportsATable()
    {
        var library = NdiTestRuntime.Loaded();
        Assert.NotEqual(0, library.Table.SendCreate);
        Assert.NotEqual(0, library.Table.RecvCaptureV3);
    }

    [Fact]
    public void SenderIsReceivableOnThisMachine()
    {
        var library = NdiTestRuntime.Loaded();
        var sourceName = $"MxU Loopback {Pid}";
        using var sender = Assert.IsType<NdiSender>(NdiSender.Create(library, sourceName));
        using (new Pump(sender, Blue(320, 180), 320, 180))
        {
            using var receiver = Assert.IsType<NdiReceiver>(NdiReceiver.Create(library));
            Assert.NotNull(receiver.Connect(sourceName, TimeSpan.FromSeconds(15))); // loopback source never appeared in NDI discovery
            var frame = receiver.CaptureVideoFrame(TimeSpan.FromSeconds(10));
            Assert.NotNull(frame); // connected but no video frame arrived
            Assert.Equal(320, frame.Width);
            Assert.Equal(180, frame.Height);
            var offset = frame.Height / 2 * frame.BytesPerRow + frame.Width / 2 * 4;
            Assert.True(frame.Data[offset] > 200, "blue channel");
            Assert.True(frame.Data[offset + 2] < 60, "red channel");
            Assert.True(sender.Sent > 0);
        }
    }

    [Fact]
    public void InputRecoversWhenTheSourceReturns()
    {
        var library = NdiTestRuntime.Loaded();
        var sourceName = $"MxU Rehunt {Pid}";
        var pixels = Blue(320, 180);

        var sender = Assert.IsType<NdiSender>(NdiSender.Create(library, sourceName));
        var pump = new Pump(sender, pixels, 320, 180);
        using var input = new NdiInputSource(library, sourceName, quietSecondsBeforeRehunt: 2);

        bool Receiving(double seconds) => TestSupport.Eventually(() => input.State.IsReceiving && input.LatestFrame() is not null, seconds);

        Assert.True(Receiving(15), "never connected the first time");
        var first = input.LatestFrame()!;
        Assert.Equal(255, first.Pixels.Span[3]); // BGRA alpha survives

        pump.Dispose();
        sender.Dispose();
        Thread.Sleep(4000);
        input.ClearLatestFrameForTesting();

        using var returned = Assert.IsType<NdiSender>(NdiSender.Create(library, sourceName));
        using (new Pump(returned, pixels, 320, 180))
            Assert.True(Receiving(30), "input never re-hunted the returned source");
    }

    [Fact]
    public void TheProgramOutputSendsFramesAndAudioWithoutBlocking()
    {
        var library = NdiTestRuntime.Loaded();
        var sourceName = $"MxU Output {Pid}";
        using var output = new NdiOutput(sourceName, 30_000, 1_000, () => (library, null));
        output.Start();
        Assert.Equal(new NdiOutputStatus(NdiOutputState.Sending, sourceName), output.Status);
        using var receiver = Assert.IsType<NdiReceiver>(NdiReceiver.Create(library));
        var frame = TestSupport.Solid(64, 36, 255, 0, 0);
        var connected = false;
        var arrived = TestSupport.Eventually(() =>
        {
            output.WriteVideo(frame);
            output.WriteAudio(new Core.Media.AudioBuffer(48_000, 2, new float[2 * 1600], 0));
            connected = connected || receiver.Connect(sourceName, TimeSpan.FromMilliseconds(200)) is not null;
            return connected && receiver.CaptureVideoFrame(TimeSpan.FromMilliseconds(100)) is { Width: 64, Height: 36 };
        }, 20);
        Assert.True(arrived, "the program output never reached a receiver");
        Assert.True(output.FramesSent > 0);
    }
}
