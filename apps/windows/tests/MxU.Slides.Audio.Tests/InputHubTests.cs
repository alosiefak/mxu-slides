using MxU.Slides.Audio.Decoding;

namespace MxU.Slides.Audio.Tests;

/// <summary>Port of InputHubTests.swift and ExternalInputFeedTests.swift.</summary>
public sealed class InputHubTests
{
    private static PcmBuffer DeviceBuffer(int channels, int frames = 480, int sampleRate = 48_000)
    {
        var buffer = new PcmBuffer(sampleRate, channels, frames) { FrameLength = frames };
        for (var channel = 0; channel < channels; channel++) Array.Fill(buffer.Data[channel], (channel + 1) / 100f);
        return buffer;
    }

    private static float Value(PcmBuffer buffer, int channel) => buffer.Data[channel][0];

    [Fact]
    public void ExtractStereoPairFromMultichannel()
    {
        var output = ChannelExtraction.Extract(DeviceBuffer(8), new InputChannelSelection.StereoPair(2))!;
        Assert.Equal(2, output.ChannelCount);
        Assert.Equal(480, output.FrameLength);
        Assert.True(MathF.Abs(Value(output, 0) - 0.03f) < 0.0001);
        Assert.True(MathF.Abs(Value(output, 1) - 0.04f) < 0.0001);
    }

    [Fact]
    public void ExtractMonoDoubles()
    {
        var output = ChannelExtraction.Extract(DeviceBuffer(8), new InputChannelSelection.Mono(5))!;
        Assert.True(MathF.Abs(Value(output, 0) - 0.06f) < 0.0001);
        Assert.True(MathF.Abs(Value(output, 1) - 0.06f) < 0.0001);
    }

    [Fact]
    public void ExtractClampsOutOfRange()
    {
        var pair = ChannelExtraction.Extract(DeviceBuffer(8), new InputChannelSelection.StereoPair(10))!;
        Assert.True(MathF.Abs(Value(pair, 0) - 0.01f) < 0.0001);
        Assert.True(MathF.Abs(Value(pair, 1) - 0.02f) < 0.0001);
        var mono = ChannelExtraction.Extract(DeviceBuffer(8), new InputChannelSelection.Mono(12))!;
        Assert.True(MathF.Abs(Value(mono, 0) - 0.01f) < 0.0001);
        var negative = ChannelExtraction.Extract(DeviceBuffer(8), new InputChannelSelection.StereoPair(-2))!;
        Assert.True(MathF.Abs(Value(negative, 0) - 0.01f) < 0.0001);
    }

    [Fact]
    public void ExtractFromMonoDeviceDoubles()
    {
        var output = ChannelExtraction.Extract(DeviceBuffer(1), new InputChannelSelection.StereoPair(0))!;
        Assert.True(MathF.Abs(Value(output, 0) - 0.01f) < 0.0001);
        Assert.True(MathF.Abs(Value(output, 1) - 0.01f) < 0.0001);
    }

    [Fact]
    public void ExtractAppliesTrimAndMuteEmitsSilence()
    {
        var trimmed = ChannelExtraction.Extract(DeviceBuffer(8), new InputChannelSelection.StereoPair(2), gain: 0.5f)!;
        Assert.True(MathF.Abs(Value(trimmed, 0) - 0.015f) < 0.0001);
        var muted = ChannelExtraction.Extract(DeviceBuffer(8), new InputChannelSelection.StereoPair(2), muted: true)!;
        Assert.Equal(480, muted.FrameLength);
        Assert.Equal(0, Value(muted, 0));
        Assert.Equal(0, Value(muted, 1));
    }

    [Fact]
    public void FanoutDeliversPerTapSelections()
    {
        var fanout = new InputFanout();
        PcmBuffer? pairOut = null, monoOut = null;
        fanout.Add(new InputChannelSelection.StereoPair(2), (buffer, _) => pairOut = buffer);
        fanout.Add(new InputChannelSelection.Mono(5), (buffer, _) => monoOut = buffer);
        fanout.Push(DeviceBuffer(8), hostSeconds: 0);
        Assert.True(MathF.Abs(Value(pairOut!, 0) - 0.03f) < 0.0001);
        Assert.True(MathF.Abs(Value(pairOut!, 1) - 0.04f) < 0.0001);
        Assert.True(MathF.Abs(Value(monoOut!, 0) - 0.06f) < 0.0001);
        Assert.NotSame(pairOut, monoOut);
    }

    [Fact]
    public void UntrimmedTapIgnoresDeviceTrimAndMute()
    {
        var fanout = new InputFanout();
        PcmBuffer? heard = null, read = null;
        fanout.Add(new InputChannelSelection.Mono(1), (buffer, _) => heard = buffer);
        fanout.Add(new InputChannelSelection.Mono(1), (buffer, _) => read = buffer, trimmed: false);
        fanout.Push(DeviceBuffer(2), 0, gain: 0.5f, muted: true);
        Assert.Equal(0, Value(heard!, 0));
        Assert.True(MathF.Abs(Value(read!, 0) - 0.02f) < 0.0001);
    }

    [Fact]
    public void FanoutTapLifecycle()
    {
        var fanout = new InputFanout();
        var received = 0;
        var token = fanout.Add(InputChannelSelection.Default, (_, _) => received++);
        Assert.Equal(1, fanout.Count);
        fanout.Push(DeviceBuffer(4), 0);
        Assert.Equal(1, received);
        PcmBuffer? latest = null;
        var updateToken = fanout.Add(InputChannelSelection.Default, (buffer, _) => latest = buffer);
        fanout.Update(updateToken, new InputChannelSelection.Mono(3));
        fanout.Push(DeviceBuffer(4), 0);
        Assert.True(MathF.Abs(Value(latest!, 0) - 0.04f) < 0.0001);
        Assert.Equal(2, received);
        fanout.Remove(token);
        fanout.Remove(updateToken);
        Assert.Equal(0, fanout.Count);
        fanout.Push(DeviceBuffer(4), 0);
        Assert.Equal(2, received);
    }

    [Fact]
    public void PoolRefCountsHubs()
    {
        var pool = new AudioInputHubPool();
        var built = 0;
        pool.HubFactory = (uid, trim) =>
        {
            built++;
            return AudioInputHub.ForTests(uid, trim);
        };
        var first = pool.Acquire("wing");
        var second = pool.Acquire("wing");
        Assert.Same(first, second);
        Assert.Equal(1, built);
        Assert.Equal(1, pool.HubCount);
        pool.Acquire("interface");
        Assert.Equal(2, pool.HubCount);
        pool.Release("wing");
        Assert.Equal(2, pool.HubCount);
        pool.Release("wing");
        Assert.Equal(1, pool.HubCount);
        pool.Acquire("wing");
        Assert.Equal(3, built);
    }

    [Fact]
    public void HubConfigChangePostDoesNotBlockPostingThread()
    {
        using var hub = AudioInputHub.ForTests("test");
        hub.Lock.Enter();
        try
        {
            Assert.True(TestAudio.FinishesWithin(hub.OnDeviceConfigurationChanged), "config-change post must not wait on the hub lock");
        }
        finally
        {
            hub.Lock.Exit();
        }
    }

    [Fact]
    public void ConverterPathResamplesDeviceWide()
    {
        var source = DeviceBuffer(4, frames: 441, sampleRate: 44_100);
        var converter = new SincResampler(44_100, 48_000, 4);
        // A stream converter holds back the filter's look-ahead; two blocks show the steady state.
        AudioInputHub.ConvertDeviceBuffer(source, converter);
        var converted = AudioInputHub.ConvertDeviceBuffer(source, converter)!;
        Assert.Equal(48_000, converted.SampleRate);
        Assert.Equal(4, converted.ChannelCount);
        Assert.True(Math.Abs(converted.FrameLength - 480) < 64);
        var output = ChannelExtraction.Extract(converted, new InputChannelSelection.StereoPair(2))!;
        Assert.True(MathF.Abs(output.Data[0][output.FrameLength / 2] - 0.03f) < 0.005);
    }

    [Fact]
    public void AMissingInputDeviceSaysSo()
    {
        // Windows-specific: opening a device that isn't there is a clear error, not a crash.
        var error = Assert.Throws<AudioInputHub.CaptureException>(() => new AudioInputHub("{no-such-input}", _ => (1, false)));
        Assert.True(error.Reason is AudioInputHub.CaptureException.Kind.DeviceNotFound or AudioInputHub.CaptureException.Kind.EngineStartFailed);
    }
}

public sealed class ExternalInputFeedTests
{
    private sealed class FakeFeed : IExternalInputFeed
    {
        private readonly Lock gate = new();
        private AudioInputHub? hub;
        private int stops;

        public AudioInputHub? Hub { get { lock (gate) return hub; } }
        public int Stops { get { lock (gate) return stops; } }

        public void Start(AudioInputHub into)
        {
            lock (gate) hub = into;
        }

        public void Stop()
        {
            lock (gate) stops++;
        }
    }

    [Fact]
    public void Int32InterleavedBecomesDeinterleavedFloat()
    {
        int[] samples = [int.MaxValue, int.MinValue, 0, 1 << 30];
        var buffer = AudioInputHub.DeviceBuffer(samples, frames: 2, channels: 2);
        Assert.Equal(2, buffer?.FrameLength);
        Assert.Equal(48_000, buffer?.SampleRate);
        var left = buffer!.Data[0];
        var right = buffer.Data[1];
        Assert.True(MathF.Abs(left[0] - 1) < 0.0001);
        Assert.Equal(0, left[1]);
        Assert.Equal(-1, right[0]);
        Assert.Equal(0.5f, right[1]);
    }

    [Fact]
    public void DirectUidsRoundTripAndWindowsUidsAreNotDirect()
    {
        var uid = DeckLinkAudioUid.Uid(-42);
        Assert.Equal("decklink:-42", uid);
        Assert.Equal(-42, DeckLinkAudioUid.PersistentId(uid));
        Assert.Null(DeckLinkAudioUid.PersistentId("{0.0.1.00000000}.{6b9e2f0d-1c35-4d36-9f6b-3d0c2b5a7a11}"));
        Assert.Null(DeckLinkAudioUid.PersistentId("decklink:not-a-number"));
    }

    [Fact]
    public void AShortPacketIsRefused()
    {
        int[] samples = [1, 2, 3];
        Assert.Null(AudioInputHub.DeviceBuffer(samples, frames: 2, channels: 2));
    }

    [Fact]
    public void ExternalUidsRideTheirFeedThroughTheHub()
    {
        var pool = new AudioInputHubPool();
        var feed = new FakeFeed();
        pool.ExternalFeedProvider = uid => uid == "decklink:7" ? feed : null;
        pool.TrimProvider = _ => (0.5f, false);
        var hub = pool.Acquire("decklink:7");
        Assert.Same(hub, feed.Hub);
        float? heard = null;
        double? host = null;
        hub.AddTap(new InputChannelSelection.StereoPair(2), (buffer, seconds) =>
        {
            heard = buffer.Data[0][0];
            host = seconds;
        });
        int[] samples = [0, 0, 1 << 30, 0];
        hub.Push(AudioInputHub.DeviceBuffer(samples, frames: 1, channels: 4)!, hostSeconds: 12);
        Assert.Equal(0.25f, heard);
        Assert.Equal(12, host);
        pool.Acquire("decklink:7");
        pool.Release("decklink:7");
        Assert.Equal(0, feed.Stops);
        pool.Release("decklink:7");
        Assert.Equal(1, feed.Stops);
    }

    [Fact]
    public void ExternalInputsListBesideWindowsDevices()
    {
        var direct = new AudioInputDevice("decklink:test-list", "DeckLink Duo (1) (Direct)", 16);
        AudioDeviceList.SetExternalInputDevices([direct]);
        try
        {
            Assert.Contains(direct, AudioDeviceList.InputDevices());
            Assert.Equal(direct, AudioDeviceList.InputDevice("decklink:test-list"));
        }
        finally
        {
            AudioDeviceList.SetExternalInputDevices([]);
        }
    }
}
