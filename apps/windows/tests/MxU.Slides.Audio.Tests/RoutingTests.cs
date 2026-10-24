using System.Text.Json;
using MxU.Slides.Audio.Routing;

namespace MxU.Slides.Audio.Tests;

/// <summary>
/// The outputs, the mixes and where sound plays: AudioOutputInventory.swift, the routing half
/// of AudioMixInventory.swift and AudioController.swift (routing(forMix:), the hardware check,
/// the media mix), and SettingsDeviceStatus.swift. The Mac tests only cover the last; the rest
/// are new tests of the ported rules.
/// </summary>
public class RoutingTests
{
    private static readonly AudioOutputDevice Speakers = new("speakers-uid", "Speakers", 2);
    private static readonly AudioOutputDevice Focusrite = new("focusrite-uid", "Focusrite USB", 4);

    private static AudioOutputInventory Outputs(params AudioOutputDevice[] devices) => new(null, () => devices);

    // MARK: Outputs

    [Fact]
    public void AFirstRunHasOneMainOutputOnTheSystemDefault()
    {
        var outputs = Outputs();
        var only = Assert.Single(outputs.Entries);
        Assert.Equal("Main Output", only.Name);
        Assert.Null(only.DeviceUid);
        Assert.Equal(0, only.ChannelOffset);
    }

    [Fact]
    public void NewOutputsAreNumberedPastTheNamesTaken()
    {
        var outputs = Outputs();
        Assert.Equal("Output 2", outputs.Create().Name);
        outputs.Create("Output 4");
        Assert.Equal("Output 5", outputs.Create().Name);
        Assert.Equal("Foldback", outputs.Create("Foldback").Name);
    }

    [Fact]
    public void EnsureNamesAnOutputAfterItsDeviceAndPairAndFindsItAgain()
    {
        var outputs = Outputs(Focusrite);
        var made = outputs.Ensure(Focusrite.Uid, 2);
        Assert.Equal("Focusrite USB 3-4", made.Name);
        Assert.Same(made, outputs.Ensure(Focusrite.Uid, 2));
        Assert.Equal("Output", outputs.Ensure("unplugged", 0).Name);
        Assert.Equal("Main Output", outputs.Ensure(null, 0).Name);
        Assert.Equal(3, outputs.Entries.Count);
    }

    [Fact]
    public void AnOutputForEveryChannelSkipsChannelsAlreadyMade()
    {
        var outputs = Outputs(Focusrite);
        outputs.EnsureMono(Focusrite, 1);
        outputs.AddOutputPerChannel(Focusrite);
        var monos = outputs.Entries.Where(e => e.DeviceUid == Focusrite.Uid).ToList();
        Assert.Equal(new[] { "Focusrite USB 2", "Focusrite USB 1", "Focusrite USB 3", "Focusrite USB 4" }, monos.Select(e => e.Name));
        Assert.Equal(new[] { 1, 0, 2, 3 }, monos.Select(e => e.MonoChannel!.Value));
        Assert.Same(monos[0], outputs.EnsureMono(Focusrite, 1));
        Assert.Equal("Focusrite USB 3-4", outputs.EnsurePair(Focusrite, 2).Name);
    }

    [Fact]
    public void AssigningADeviceSetsThePairOrTheMonoChannel()
    {
        var outputs = Outputs(Focusrite);
        var id = outputs.Entries[0].Id;
        outputs.AssignDevice(id, Focusrite.Uid, 2);
        Assert.Equal((Focusrite.Uid, 2, (int?)null), (outputs.Entry(id)!.DeviceUid, outputs.Entry(id)!.ChannelOffset, outputs.Entry(id)!.MonoChannel));
        outputs.AssignDevice(id, Focusrite.Uid, -4, monoChannel: 3);
        Assert.Equal((0, (int?)3), (outputs.Entry(id)!.ChannelOffset, outputs.Entry(id)!.MonoChannel));
        outputs.AssignDevice(id, AudioOutputInventory.NoDeviceUid, 0);
        Assert.True(outputs.IsDeviceless(id));
    }

    [Fact]
    public void TheDelayIsClampedAndZeroIsStoredAsNone()
    {
        var outputs = Outputs();
        var id = outputs.Entries[0].Id;
        outputs.SetDelay(id, 5000);
        Assert.Equal(2000, outputs.Entry(id)!.DelayMs);
        outputs.SetDelay(id, -3);
        Assert.Null(outputs.Entry(id)!.DelayMs);
        outputs.SetDelay(id, 40);
        Assert.Equal(40, outputs.Entry(id)!.DelayMs);
    }

    [Fact]
    public void RenamingTrimsAndIgnoresABlankName()
    {
        var outputs = Outputs();
        var id = outputs.Entries[0].Id;
        outputs.Rename(id, "  House  ");
        Assert.Equal("House", outputs.Entry(id)!.Name);
        outputs.Rename(id, "   ");
        Assert.Equal("House", outputs.Entry(id)!.Name);
    }

    [Fact]
    public void TheLastOutputCantBeRemoved()
    {
        var outputs = Outputs();
        var changes = 0;
        outputs.Changed += () => changes++;
        var second = outputs.Create();
        outputs.Remove(second.Id);
        outputs.Remove(outputs.Entries[0].Id);
        Assert.Single(outputs.Entries);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void ShortNamesAndChannelWords()
    {
        Assert.Equal("Out 3", new AudioOutputEntry { Name = "Focusrite 3", MonoChannel = 2 }.ShortName);
        Assert.Equal("Out 3–4", new AudioOutputEntry { Name = "Focusrite 3-4", ChannelOffset = 2 }.ShortName);
        Assert.Equal("Foldback", new AudioOutputEntry { Name = "Foldback", ChannelOffset = 2 }.ShortName);
        Assert.Equal("3", AudioOutputInventory.ChannelWords(new AudioOutputEntry { MonoChannel = 2 }));
        Assert.Equal("5-6", AudioOutputInventory.ChannelWords(new AudioOutputEntry { ChannelOffset = 4 }));
    }

    [Fact]
    public void OutputsSaveInTheMacsShape()
    {
        var entry = new AudioOutputEntry { Id = "A", Name = "Foldback", DeviceUid = "uid", ChannelOffset = 2, DelayMs = 20 };
        var json = JsonSerializer.Serialize(entry);
        Assert.Equal("""{"id":"A","name":"Foldback","deviceUID":"uid","channelOffset":2,"delayMs":20}""", json);
        // The Mac decodes a missing offset as 0 and a missing id as a fresh one.
        var mac = JsonSerializer.Deserialize<AudioOutputEntry>("""{"name":"Main Output","monoChannel":1}""")!;
        Assert.Equal((0, (int?)1, (string?)null), (mac.ChannelOffset, mac.MonoChannel, mac.DeviceUid));
        Assert.NotEmpty(mac.Id);
    }

    // MARK: Words (SettingsDeviceStatusTests.swift and AudioOutputWords)

    [Fact]
    public void AssignedAndMissingIsNotConnected()
    {
        Assert.True(AudioOutputWords.ShowsNotConnected("uid-1", found: false));
        Assert.False(AudioOutputWords.ShowsNotConnected("uid-1", found: true));
    }

    [Fact]
    public void NothingAssignedIsNeverMissing()
    {
        Assert.False(AudioOutputWords.ShowsNotConnected(null, found: false));
        Assert.False(AudioOutputWords.ShowsNotConnected("", found: false));
        Assert.False(AudioOutputWords.ShowsNotConnected(new AudioOutputEntry { DeviceUid = AudioOutputInventory.NoDeviceUid }, []));
        Assert.False(AudioOutputWords.ShowsNotConnected(new AudioOutputEntry { DeviceUid = "gone" }, devices: null));
        Assert.True(AudioOutputWords.ShowsNotConnected(new AudioOutputEntry { DeviceUid = "gone" }, [Speakers]));
    }

    [Fact]
    public void SubtitlesSayWhereAnOutputPlays()
    {
        IReadOnlyList<AudioOutputDevice> devices = [Speakers, Focusrite];
        Assert.Equal("System default", AudioOutputWords.Subtitle(new AudioOutputEntry(), devices));
        Assert.Equal("No device", AudioOutputWords.Subtitle(new AudioOutputEntry { DeviceUid = AudioOutputInventory.NoDeviceUid }, devices));
        Assert.Equal("Speakers · 20 ms delay", AudioOutputWords.Subtitle(new AudioOutputEntry { DeviceUid = Speakers.Uid, DelayMs = 20 }, devices));
        Assert.Equal("Focusrite USB 3-4", AudioOutputWords.Subtitle(new AudioOutputEntry { DeviceUid = Focusrite.Uid, ChannelOffset = 2 }, devices));
        Assert.Equal("Channels 3", AudioOutputWords.Subtitle(new AudioOutputEntry { DeviceUid = "gone", MonoChannel = 2 }, devices));
    }

    // MARK: Mixes

    [Fact]
    public void ThereIsAlwaysAMainMix()
    {
        var mixes = new AudioMixInventory([new AudioMixEntry { Id = "x", Name = "Stray" }]);
        var main = Assert.Single(mixes.Entries);
        Assert.True(main.IsMain);
        Assert.Equal(AudioMixInventory.DefaultOutputId, mixes.PrimaryToken(AudioMixInventory.MainId));
        Assert.Equal("Mix 1", mixes.Create().Name);
        mixes.Remove(AudioMixInventory.MainId);
        Assert.Contains(mixes.Entries, m => m.IsMain);
    }

    [Fact]
    public void SettingWhereAMixPlaysKeepsItsSends()
    {
        var mixes = new AudioMixInventory();
        var id = AudioMixInventory.MainId;
        mixes.SetPrimary(id, "out-a");
        mixes.ToggleSecondary(id, "out-b");
        Assert.Equal(new[] { "out-a", "out-b" }, mixes.Entry(id)!.OutputIds);
        mixes.SetPrimary(id, "out-b");
        Assert.Equal(new[] { "out-b" }, mixes.Entry(id)!.OutputIds);
        mixes.ToggleSecondary(id, "out-c");
        mixes.SetPrimary(id, AudioMixInventory.DefaultOutputId);
        Assert.Equal(new[] { AudioMixInventory.DefaultOutputId, "out-c" }, mixes.Entry(id)!.OutputIds);
        mixes.ToggleSecondary(id, "out-c");
        Assert.Empty(mixes.Entry(id)!.OutputIds);
        mixes.SetPrimary(id, AudioMixInventory.NoOutputId);
        Assert.Equal(new[] { AudioMixInventory.NoOutputId }, mixes.Entry(id)!.OutputIds);
        Assert.True(AudioMixInventory.IsVirtual(mixes.Entry(id)!.OutputIds));
        // A send on a mix that plays nowhere puts it back on the default, plus the send.
        mixes.ToggleSecondary(id, "out-d");
        Assert.Equal(new[] { AudioMixInventory.DefaultOutputId, "out-d" }, mixes.Entry(id)!.OutputIds);
    }

    [Fact]
    public void MixesSaveInTheMacsShape()
    {
        var json = JsonSerializer.Serialize(new AudioMixEntry { Id = "main", Name = "Main", OutputIds = ["A"] });
        Assert.Equal("""{"id":"main","name":"Main","outputIds":["A"]}""", json);
        var mac = JsonSerializer.Deserialize<AudioMixEntry>("""{"id":"m2","name":"Lobby","folderId":"f"}""")!;
        Assert.Empty(mac.OutputIds);
        Assert.Equal("f", mac.FolderId);
    }

    // MARK: Where sound plays

    private static AudioRoutingTable Table(AudioOutputInventory outputs, AudioMixInventory? mixes = null, params AudioOutputDevice[] devices) =>
        new(outputs, mixes ?? new AudioMixInventory(), null, () => devices, watchDevices: false);

    [Fact]
    public void WithNothingChosenMusicPlaysOnTheDefaultDevice()
    {
        using var table = Table(Outputs(), null, Speakers);
        var routing = table.MusicRouting("any playlist");
        Assert.Null(routing.Device);
        Assert.Equal((0, "main", 0, false), (routing.ChannelOffset, routing.Key, routing.DelayMs, routing.Mono));
        Assert.True(table.HardwareLive(AudioMixInventory.MainId));
        Assert.Equal(routing, table.MediaRouting());
    }

    [Fact]
    public void TheMusicFollowsTheMainMixsOutputDevicePairAndDelay()
    {
        var outputs = Outputs(Focusrite);
        var foldback = outputs.EnsurePair(Focusrite, 2);
        outputs.SetDelay(foldback.Id, 35);
        using var table = Table(outputs, null, Speakers, Focusrite);
        table.SetMusicOutput(foldback.Id);
        var routing = table.MusicRouting(null);
        Assert.Equal(Focusrite, routing.Device);
        Assert.Equal((2, 35, false), (routing.ChannelOffset, routing.DelayMs, routing.Mono));
        Assert.Equal(foldback.Id, table.MusicOutputToken);
    }

    [Fact]
    public void APairOrChannelTheDeviceHasntGotFallsBackToTheFirstPair()
    {
        var outputs = Outputs(Focusrite);
        var odd = outputs.Create();
        outputs.AssignDevice(odd.Id, Focusrite.Uid, 3);
        var mono = outputs.Create();
        outputs.AssignDevice(mono.Id, Speakers.Uid, 0, monoChannel: 1);
        using var table = Table(outputs, null, Speakers, Focusrite);
        table.SetMusicOutput(odd.Id);
        Assert.Equal(0, table.MusicRouting(null).ChannelOffset);
        table.SetMusicOutput(mono.Id);
        Assert.Equal((1, true), (table.MusicRouting(null).ChannelOffset, table.MusicRouting(null).Mono));
        outputs.AssignDevice(mono.Id, Speakers.Uid, 0, monoChannel: 7);
        Assert.Equal((0, false), (table.MusicRouting(null).ChannelOffset, table.MusicRouting(null).Mono));
    }

    [Fact]
    public void AnUnpluggedDeviceFallsBackToTheDefaultLikeTheMac()
    {
        var outputs = Outputs(Focusrite);
        var foldback = outputs.EnsurePair(Focusrite, 2);
        AudioOutputDevice[] plugged = [Focusrite];
        using var table = new AudioRoutingTable(outputs, new AudioMixInventory(), null, () => plugged, watchDevices: false);
        table.SetMusicOutput(foldback.Id);
        Assert.Equal(Focusrite, table.MusicRouting(null).Device);
        var changes = 0;
        table.Changed += () => changes++;
        plugged = [];
        table.RefreshDevices();
        Assert.Null(table.MusicRouting(null).Device);
        Assert.Equal(0, table.MusicRouting(null).ChannelOffset);
        Assert.Equal(1, changes);
        plugged = [Focusrite];
        table.RefreshDevices();
        Assert.Equal((Focusrite, 2), (table.MusicRouting(null).Device, table.MusicRouting(null).ChannelOffset));
    }

    [Fact]
    public void NoDeviceAndNoOutputKeepTheSoundOffTheSpeakers()
    {
        var outputs = Outputs();
        var silent = outputs.Create("Streams only");
        outputs.AssignDevice(silent.Id, AudioOutputInventory.NoDeviceUid, 0);
        using var table = Table(outputs);
        table.SetMusicOutput(silent.Id);
        Assert.False(table.HardwareLive(AudioMixInventory.MainId));
        table.SetMusicOutput(AudioMixInventory.NoOutputId);
        Assert.False(table.HardwareLive(AudioMixInventory.MainId));
        table.SetMusicOutput(AudioMixInventory.DefaultOutputId);
        Assert.True(table.HardwareLive(AudioMixInventory.MainId));
    }

    [Fact]
    public void VideoSoundCanHaveItsOwnOutputThroughAMediaMix()
    {
        var outputs = Outputs(Focusrite);
        var foldback = outputs.EnsurePair(Focusrite, 2);
        var mixes = new AudioMixInventory();
        using var table = Table(outputs, mixes, Focusrite);
        Assert.Null(table.VideoOutputToken);
        table.SetVideoOutput(foldback.Id);
        var videoMix = mixes.Entry(table.MediaMixId)!;
        Assert.Equal(AudioRoutingTable.VideoMixName, videoMix.Name);
        Assert.Equal(foldback.Id, table.VideoOutputToken);
        Assert.Equal((Focusrite, 2), (table.MediaRouting().Device, table.MediaRouting().ChannelOffset));
        Assert.Null(table.MusicRouting(null).Device);

        // Back to following the music, then out again: the same mix is used, not a second one.
        table.SetVideoOutput(null);
        Assert.Equal(AudioMixInventory.MainId, table.MediaMixId);
        table.SetVideoOutput(AudioMixInventory.NoOutputId);
        Assert.Equal(videoMix.Id, table.MediaMixId);
        Assert.Equal(2, mixes.Entries.Count);
        Assert.False(table.HardwareLive(table.MediaMixId));
    }

    [Fact]
    public void AMediaMixThatIsGoneMeansTheMainMix()
    {
        using var table = new AudioRoutingTable(Outputs(), new AudioMixInventory(), "deleted-mix", () => [], watchDevices: false);
        Assert.Equal(AudioMixInventory.MainId, table.MediaMixId);
    }
}
