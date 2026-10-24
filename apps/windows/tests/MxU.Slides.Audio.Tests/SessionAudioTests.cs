using MxU.Slides.Audio.Control;

namespace MxU.Slides.Audio.Tests;

/// <summary>
/// Sound for recordings and streams (Phase 4): which source a session ends up hearing and why,
/// the program summed in, the delay; the mix feeds over the music buses; the audio input
/// inventory and its on-demand device sources. No sound card needed.
/// </summary>
public sealed class SessionAudioFeedTests
{
    /// <summary>Backends that hand out a sink per source, so a test can play sound into each.</summary>
    private sealed class FakeBackends : ISessionAudioBackends
    {
        public Action<PcmBuffer, double>? Program, Mix, Input, Device;
        public bool HasMix = true, HasInput = true, HasDevice = true;
        public int Closed;
        public List<string> Opened { get; } = [];

        public IDisposable OpenProgram(Action<PcmBuffer, double> consumer)
        {
            Opened.Add("program");
            Program = consumer;
            return new Handle(this);
        }

        public IDisposable? OpenMix(string mixId, Action<PcmBuffer, double> consumer)
        {
            if (!HasMix) return null;
            Opened.Add($"mix:{mixId}");
            Mix = consumer;
            return new Handle(this);
        }

        public IDisposable? OpenInput(string inputId, Action<PcmBuffer, double> consumer, out string? problem)
        {
            problem = HasInput ? null : "unplugged";
            if (!HasInput) return null;
            Opened.Add($"input:{inputId}");
            Input = consumer;
            return new Handle(this);
        }

        public IDisposable? OpenInputDevice(string uid, Action<PcmBuffer, double> consumer, out string? problem)
        {
            problem = HasDevice ? null : "that sound device isn't connected";
            if (!HasDevice) return null;
            Opened.Add($"device:{uid}");
            Device = consumer;
            return new Handle(this);
        }

        public string? InputName(string inputId) => inputId == "desk" ? "Mixing desk" : null;

        private sealed class Handle(FakeBackends owner) : IDisposable
        {
            public void Dispose() => owner.Closed++;
        }
    }

    private static PcmBuffer Steady(float value, int frames = 480)
    {
        var buffer = new PcmBuffer(48_000, 2, frames) { FrameLength = frames };
        Array.Fill(buffer.Data[0], value);
        Array.Fill(buffer.Data[1], value);
        return buffer;
    }

    private static (List<PcmBuffer> Heard, Action<PcmBuffer, double> Consumer) Listener()
    {
        var heard = new List<PcmBuffer>();
        return (heard, (buffer, _) => heard.Add(buffer));
    }

    [Fact]
    public void ProgramAudioIsTheDefault()
    {
        var backends = new FakeBackends();
        var (heard, consumer) = Listener();
        using var feed = SessionAudioFeed.Open(new SessionAudioChoice(), backends, consumer);
        Assert.Equal("program", feed.Description);
        Assert.Null(feed.FallbackReason);
        backends.Program!(Steady(0.2f), 1);
        Assert.Equal(0.2f, Assert.Single(heard).Data[0][0]);
    }

    [Fact]
    public void AMixWinsOverAnInputAndAnInputOverADevice()
    {
        var backends = new FakeBackends();
        using (var mix = SessionAudioFeed.Open(new SessionAudioChoice(MixId: "main", InputId: "desk", InputUid: "uid"), backends, (_, _) => { }))
            Assert.Equal("mix:main", mix.Description);
        using (var input = SessionAudioFeed.Open(new SessionAudioChoice(InputId: "desk", InputUid: "uid"), backends, (_, _) => { }))
            Assert.Equal("input:desk", input.Description);
        using (var device = SessionAudioFeed.Open(new SessionAudioChoice(InputUid: "uid"), backends, (_, _) => { }))
            Assert.Equal("input-device", device.Description);
        Assert.Equal(["mix:main", "input:desk", "device:uid"], backends.Opened);
        Assert.Equal(3, backends.Closed);
    }

    [Fact]
    public void WhatIsntThereFallsBackToProgramAudioAndSaysWhy()
    {
        var backends = new FakeBackends { HasMix = false, HasInput = false, HasDevice = false };
        using var mix = SessionAudioFeed.Open(new SessionAudioChoice(MixId: "stream"), backends, (_, _) => { });
        Assert.Equal("program", mix.Description);
        Assert.Equal("mix stream not available — fell back to Program Audio", mix.FallbackReason);
        using var input = SessionAudioFeed.Open(new SessionAudioChoice(InputId: "desk", IncludeProgram: true), backends, (_, _) => { });
        Assert.Equal("input Mixing desk not available (unplugged) — fell back to Program Audio", input.FallbackReason);
        using var device = SessionAudioFeed.Open(new SessionAudioChoice(InputUid: "uid"), backends, (_, _) => { });
        Assert.Equal("input uid not available (that sound device isn't connected) — fell back to Program Audio", device.FallbackReason);
        // Like the Mac: a missing mix doesn't try the input instead.
        using var both = SessionAudioFeed.Open(new SessionAudioChoice(MixId: "stream", InputId: "desk"), new FakeBackends { HasMix = false }, (_, _) => { });
        Assert.Equal("program", both.Description);
    }

    [Fact]
    public void IncludeProgramAddsTheProgramIntoTheInputWithoutTouchingTheOriginal()
    {
        var backends = new FakeBackends();
        var (heard, consumer) = Listener();
        using var feed = SessionAudioFeed.Open(new SessionAudioChoice(InputId: "desk", IncludeProgram: true), backends, consumer);
        Assert.Equal("input:desk+program", feed.Description);
        Assert.Equal(["input:desk", "program"], backends.Opened);
        backends.Program!(Steady(0.25f), 1);
        var desk = Steady(0.5f);
        backends.Input!(desk, 1);
        Assert.Equal(0.75f, Assert.Single(heard).Data[0][0], 4);
        Assert.Equal(0.5f, desk.Data[0][0]);
        // Only what the program has delivered is added; the rest of the buffer is the desk alone.
        backends.Program!(Steady(0.25f, frames: 100), 2);
        backends.Input!(Steady(0.5f), 2);
        Assert.Equal(0.75f, heard[1].Data[1][99], 4);
        Assert.Equal(0.5f, heard[1].Data[1][100], 4);
        // Louder than full scale is held at full scale.
        backends.Program!(Steady(0.9f), 3);
        backends.Input!(Steady(0.9f), 3);
        Assert.Equal(1f, heard[2].Data[0][0]);
    }

    [Fact]
    public void TheDelayHoldsTheSoundBack()
    {
        var backends = new FakeBackends();
        var (heard, consumer) = Listener();
        using var feed = SessionAudioFeed.Open(new SessionAudioChoice(MixId: "main", DelayMilliseconds: 10), backends, consumer);
        Assert.Equal("mix:main +10ms", feed.Description);
        var first = Steady(0.5f);
        backends.Mix!(first, 1);
        backends.Mix!(Steady(0.5f), 1.01);
        // 10 ms at 48 kHz is 480 samples: the first buffer comes out silent, the second carries the first's sound.
        Assert.Equal(0f, heard[0].Data[0][479]);
        Assert.Equal(0.5f, heard[1].Data[0][0]);
        Assert.Equal(0.5f, first.Data[0][0]);
    }

    [Fact]
    public void ClosingClosesEverythingOnce()
    {
        var backends = new FakeBackends();
        var feed = SessionAudioFeed.Open(new SessionAudioChoice(MixId: "main", IncludeProgram: true), backends, (_, _) => { });
        feed.Dispose();
        feed.Dispose();
        Assert.Equal(2, backends.Closed);
    }

    [Fact]
    public void TheSummerKeepsAtMostHalfASecondOfProgram()
    {
        var summer = new ProgramAudioSummer();
        for (var i = 0; i < 60; i++) summer.PushProgram(Steady(0.1f));
        Assert.Equal(ProgramAudioSummer.MaxQueuedFrames, summer.QueuedFrames);
        summer.PushProgram(new PcmBuffer(44_100, 2, 10) { FrameLength = 10 });
        Assert.Equal(ProgramAudioSummer.MaxQueuedFrames, summer.QueuedFrames);
        var mono = new PcmBuffer(48_000, 1, 10) { FrameLength = 10 };
        summer.SumInto(mono);
        Assert.Equal(0.1f, mono.Data[0][0], 4);
    }
}

public sealed class AudioMixFeedsTests
{
    private sealed class FakeBus(string mix) : AudioMixFeeds.IMember
    {
        public Action<PcmBuffer>? Feed;
        public int Changes;

        public object Key => this;

        public string MixKey { get; set; } = mix;

        public void SetFeed(Action<PcmBuffer>? feed)
        {
            Feed = feed;
            Changes++;
        }
    }

    private static PcmBuffer Steady(float value, int frames)
    {
        var buffer = new PcmBuffer(48_000, 2, frames) { FrameLength = frames };
        Array.Fill(buffer.Data[0], value);
        Array.Fill(buffer.Data[1], value);
        return buffer;
    }

    [Fact]
    public void OnlyBusesOnAMixSomeoneListensToAreConnected()
    {
        var main = new FakeBus(AudioRouting.MainKey);
        var other = new FakeBus("foyer");
        var buses = new List<AudioMixFeeds.IMember> { main, other };
        using var feeds = new AudioMixFeeds(() => [.. buses], startsClock: false);
        Assert.Null(main.Feed);
        Assert.Null(feeds.Open("foyer", (_, _) => { }));
        var heard = new List<PcmBuffer>();
        var listener = feeds.Open(AudioRouting.MainKey, (buffer, _) => heard.Add(buffer));
        Assert.NotNull(listener);
        Assert.NotNull(main.Feed);
        Assert.Null(other.Feed);
        Assert.Equal(1, feeds.OpenMixes);

        main.Feed!(Steady(0.3f, 480));
        feeds.TickAll(10);
        Assert.Equal(0.3f, Assert.Single(heard).Data[0][0], 4);

        listener!.Dispose();
        listener.Dispose();
        Assert.Null(main.Feed);
        Assert.Equal(0, feeds.OpenMixes);
    }

    [Fact]
    public void BusesThatStartStopOrMoveArePickedUp()
    {
        var buses = new List<AudioMixFeeds.IMember>();
        using var feeds = new AudioMixFeeds(() => [.. buses], startsClock: false);
        var heard = new List<PcmBuffer>();
        using var listener = feeds.Open(AudioRouting.MainKey, (buffer, _) => heard.Add(buffer));
        var song = new FakeBus(AudioRouting.MainKey);
        buses.Add(song);
        feeds.Sync();
        Assert.NotNull(song.Feed);
        var playlist = new FakeBus(AudioRouting.MainKey);
        buses.Add(playlist);
        feeds.Sync();
        song.Feed!(Steady(0.2f, 480));
        playlist.Feed!(Steady(0.3f, 480));
        feeds.TickAll(10);
        Assert.Equal(0.5f, heard[^1].Data[0][0], 4);

        buses.Remove(song);
        feeds.Sync();
        Assert.Null(song.Feed);
        playlist.MixKey = "foyer";
        feeds.Sync();
        Assert.Null(playlist.Feed);
        feeds.Sync();
        Assert.Equal(2, playlist.Changes);
    }

    [Fact]
    public void TwoListenersShareOneMix()
    {
        var bus = new FakeBus(AudioRouting.MainKey);
        using var feeds = new AudioMixFeeds(() => [bus], startsClock: false);
        var first = new List<PcmBuffer>();
        var second = new List<PcmBuffer>();
        using var a = feeds.Open(AudioRouting.MainKey, (buffer, _) => first.Add(buffer));
        var b = feeds.Open(AudioRouting.MainKey, (buffer, _) => second.Add(buffer));
        Assert.Equal(1, bus.Changes);
        bus.Feed!(Steady(0.4f, 480));
        feeds.TickAll(10);
        Assert.Single(first);
        Assert.Single(second);
        b!.Dispose();
        Assert.NotNull(bus.Feed);
        Assert.True(AudioMixFeeds.Exists("main"));
        Assert.Equal("Main mix (music)", AudioMixFeeds.NameOf("main"));
    }
}

public sealed class AudioInputInventoryTests : IDisposable
{
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "AudioInputs-" + Guid.NewGuid().ToString("N"));

    public AudioInputInventoryTests() => Directory.CreateDirectory(scratch);

    public void Dispose()
    {
        try { Directory.Delete(scratch, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Store => Path.Combine(scratch, "audio-inputs.json");

    private static AudioInputHubPool TestPool(Dictionary<string, AudioInputHub> hubs, params string[] missing)
    {
        var pool = new AudioInputHubPool();
        pool.HubFactory = (uid, trim) =>
        {
            if (missing.Contains(uid)) throw new AudioInputHub.CaptureException(AudioInputHub.CaptureException.Kind.DeviceNotFound, $"No input device {uid}");
            return hubs[uid] = AudioInputHub.ForTests(uid, trim);
        };
        return pool;
    }

    private static PcmBuffer FourChannels(float value)
    {
        var buffer = new PcmBuffer(48_000, 4, 480) { FrameLength = 480 };
        for (var channel = 0; channel < 4; channel++) Array.Fill(buffer.Data[channel], value * (channel + 1));
        return buffer;
    }

    [Fact]
    public void InputsAreMadeOnceAndKeptForNextTime()
    {
        var inventory = new AudioInputInventory(Store);
        var changes = 0;
        inventory.EntriesChanged += () => changes++;
        var desk = inventory.Ensure("usb-desk", "Mixing desk", new InputChannelSelection.StereoPair(2));
        Assert.Same(desk, inventory.Ensure("usb-desk", "Another name", new InputChannelSelection.StereoPair(2)));
        var pulpit = inventory.Ensure("usb-desk", "Pulpit mic", new InputChannelSelection.Mono(0));
        var main = inventory.Ensure("usb-desk", "Desk main", InputChannelSelection.Default);
        Assert.Equal(3, changes);
        Assert.Equal("channels 3–4", AudioInputEntry.ChannelsLabel(desk.Channels));
        Assert.Equal("channel 1", AudioInputEntry.ChannelsLabel(pulpit.Channels));
        // Older presets name the device: its channels 1–2 are the input they meant.
        Assert.Equal(main.Id, inventory.ForUid("usb-desk")?.Id);

        var reloaded = new AudioInputInventory(Store);
        Assert.Equal(3, reloaded.Entries.Count);
        Assert.Equal(new InputChannelSelection.Mono(0), reloaded.Entry(pulpit.Id)!.Channels);
        Assert.Equal("Mixing desk", reloaded.NameFor(desk.Id));
        reloaded.Remove(desk.Id);
        Assert.Equal(2, new AudioInputInventory(Store).Entries.Count);
    }

    [Fact]
    public void ADamagedFileIsSetAsideNotFatal()
    {
        File.WriteAllText(Store, "{ not json");
        var inventory = new AudioInputInventory(Store);
        Assert.Empty(inventory.Entries);
        Assert.True(File.Exists(Store + ".damaged"));
    }

    [Fact]
    public void ASourceOpensItsDeviceForItsFirstListenerAndLetsGoAfterItsLast()
    {
        var hubs = new Dictionary<string, AudioInputHub>();
        var pool = TestPool(hubs);
        var inventory = new AudioInputInventory(Store, pool);
        var entry = inventory.Ensure("usb-desk", "Mixing desk", new InputChannelSelection.StereoPair(2));
        var source = inventory.Source(entry.Id)!;
        Assert.Same(source, inventory.Source(entry.Id));
        Assert.False(source.IsRunning);

        var heard = new List<PcmBuffer>();
        var first = source.Open((buffer, _) => heard.Add(buffer), out var problem);
        Assert.Null(problem);
        var second = source.Open((_, _) => { }, out _);
        Assert.True(source.IsRunning);
        Assert.Equal(1, pool.HubCount);

        hubs["usb-desk"].Push(FourChannels(0.1f), 5);
        var stereo = Assert.Single(heard);
        Assert.Equal(2, stereo.ChannelCount);
        Assert.Equal(0.3f, stereo.Data[0][0], 4);
        Assert.Equal(0.4f, stereo.Data[1][0], 4);

        first!.Dispose();
        Assert.True(source.IsRunning);
        second!.Dispose();
        Assert.False(source.IsRunning);
        Assert.Equal(0, pool.HubCount);
    }

    [Fact]
    public void AnInputsOwnDelayIsApplied()
    {
        var hubs = new Dictionary<string, AudioInputHub>();
        var source = new AudioInputSource("usb-desk", InputChannelSelection.Default, TestPool(hubs)) { DelayMilliseconds = 10 };
        var heard = new List<PcmBuffer>();
        using var listener = source.Open((buffer, _) => heard.Add(buffer), out _);
        hubs["usb-desk"].Push(FourChannels(0.1f), 1);
        hubs["usb-desk"].Push(FourChannels(0.1f), 1.01);
        Assert.Equal(0f, heard[0].Data[0][0]);
        Assert.Equal(0.1f, heard[1].Data[0][0], 4);
    }

    [Fact]
    public void AMissingDeviceSaysWhy()
    {
        var inventory = new AudioInputInventory(Store, TestPool([], "gone"));
        var entry = inventory.Ensure("gone", "Old interface", InputChannelSelection.Default);
        Assert.Null(inventory.Source(entry.Id)!.Open((_, _) => { }, out var problem));
        Assert.Equal("No input device gone", problem);
        Assert.Null(inventory.Source("unknown"));
        var unassigned = new AudioInputInventory(Path.Combine(scratch, "other.json"));
        Assert.Null(unassigned.Source("x"));
    }
}
