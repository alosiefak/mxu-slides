using MxU.Slides.Audio.Control;
using MxU.Slides.Audio.Decoding;
using MxU.Slides.Audio.Library;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Audio.Tests;

/// <summary>A throwaway library folder (never the user's real one).</summary>
internal sealed class ScratchLibrary : IDisposable
{
    public ScratchLibrary()
    {
        Root = Path.Combine(Path.GetTempPath(), "mxu-audio-tests", "library-" + Guid.NewGuid().ToString("N"));
        Store = new LibraryStore(Root);
        Store.Load();
    }

    public string Root { get; }

    public LibraryStore Store { get; }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch { }
    }
}

/// <summary>Windows-only: bringing audio files into the library.</summary>
public sealed class AudioImporterTests
{
    [Fact]
    public void ImportCopiesTheFileByItsHashAndRecordsItsLength()
    {
        using var library = new ScratchLibrary();
        var importer = new AudioImporter(library.Store);
        var source = TestAudio.Tone("import", 1.5, 440);
        var item = importer.Import(source)!;
        Assert.Equal(Path.GetFileNameWithoutExtension(source), item.Name);
        Assert.Equal(Path.GetFileName(source), item.FileName);
        Assert.Equal(LibraryStore.HashFile(source), item.FileHash);
        Assert.True(Math.Abs(item.DurationSeconds!.Value - 1.5) < 0.01, $"duration {item.DurationSeconds}");
        var blob = importer.FilePath(item)!;
        Assert.StartsWith(library.Store.BlobDirectory, blob);
        Assert.Equal(item.FileHash + ".wav", Path.GetFileName(blob));
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(blob));
        var reopened = new LibraryStore(library.Root);
        reopened.Load();
        Assert.Equal(item.Id, reopened.Get<AudioItem>(item.Id)?.Id);
    }

    [Fact]
    public void TheSameFileTwiceIsStoredOnce()
    {
        using var library = new ScratchLibrary();
        var importer = new AudioImporter(library.Store);
        var source = TestAudio.Tone("twice", 0.5, 440);
        var copy = Path.Combine(Path.GetDirectoryName(source)!, "renamed-" + Path.GetFileName(source));
        File.Copy(source, copy, overwrite: true);
        var first = importer.Import(source)!;
        var second = importer.Import(copy)!;
        Assert.Equal(first.Id, second.Id);
        Assert.Single(library.Store.All<AudioItem>());
        Assert.Single(Directory.GetFiles(library.Store.BlobDirectory));
    }

    [Fact]
    public void NonAudioAndUnreadableFilesAreReported()
    {
        using var library = new ScratchLibrary();
        var importer = new AudioImporter(library.Store);
        var picture = Path.Combine(Path.GetTempPath(), "mxu-audio-tests", "not-audio.png");
        File.WriteAllBytes(picture, [1, 2, 3]);
        var broken = Path.Combine(Path.GetTempPath(), "mxu-audio-tests", $"broken-{Guid.NewGuid():N}.mp3");
        File.WriteAllBytes(broken, [1, 2, 3, 4]);
        Assert.Null(importer.Import(picture));
        var results = importer.ImportFiles([picture, broken]);
        Assert.False(results[0].Imported);
        Assert.Equal("not an audio file", results[0].SkippedReason);
        // A file Windows can't decode still comes in (like the Mac), just without a length.
        Assert.True(results[1].Imported);
        Assert.Null(results[1].Item!.DurationSeconds);
    }

    [Fact]
    public void AFolderImportsItsAudioInNameOrder()
    {
        using var library = new ScratchLibrary();
        var folder = Path.Combine(Path.GetTempPath(), "mxu-audio-tests", "folder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.Copy(TestAudio.Tone("b", 0.2, 330), Path.Combine(folder, "02 Second.wav"));
        File.Copy(TestAudio.Tone("a", 0.2, 440), Path.Combine(folder, "01 First.wav"));
        var results = new AudioImporter(library.Store).ImportFiles([folder]);
        Assert.Equal(["01 First", "02 Second"], results.Select(r => r.Item!.Name));
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public void AMissingBlobResolvesToNothing()
    {
        using var library = new ScratchLibrary();
        var item = new AudioItem { Id = "x", Name = "Gone", FileHash = "deadbeef", FileName = "gone.mp3" };
        Assert.Null(AudioImporter.FilePath(library.Store, item));
    }

    [Fact]
    public void TheWaveformCacheBuildsOnceAndReadsBack()
    {
        using var library = new ScratchLibrary();
        var source = TestAudio.Wav("wave", 48_000, 1, 0.5, i => i < 12_000 ? 0.5f : -0.25f);
        var cache = new WaveformCache(Path.Combine(library.Root, "Waveforms"));
        var wave = cache.Get("abc", source)!;
        Assert.Equal(50, wave.Count);
        Assert.Equal(0.5f, wave.Maxs[0]);
        Assert.Equal(-0.25f, wave.Mins[49]);
        Assert.True(File.Exists(Path.Combine(library.Root, "Waveforms", "abc.peaks")));
        Assert.Equal(wave, new WaveformCache(Path.Combine(library.Root, "Waveforms")).Get("abc", "missing.wav"));
    }
}

/// <summary>Windows-only: nothing throws, and time keeps moving, without an audio device.</summary>
public sealed class NoDeviceTests
{
    private static readonly AudioOutputDevice Unplugged = new("{0.0.0.00000000}.{00000000-0000-0000-0000-000000000000}", "Unplugged Interface", 8);

    [Fact]
    public void AMissingDeviceIsAStatusNotAnError()
    {
        using var engine = new AudioEngine { MasterVolume = 0 };
        engine.SetOutputDevice(Unplugged, channelOffset: 2);
        engine.Play(TestAudio.Tone("unplugged", 3, 440));
        Assert.True(engine.IsPlaying);
        Assert.Equal(AudioOutputState.DeviceMissing, engine.OutputStatus.State);
        Assert.Contains("disconnected", engine.OutputStatus.Message);
        Assert.True(TestAudio.WaitFor(() => engine.Position?.Elapsed > 0.3, 5), "the track keeps time on the clock");
        engine.Stop();
    }

    [Fact]
    public void TheDefaultDeviceOrNoneEitherWayPlays()
    {
        using var engine = new AudioEngine { MasterVolume = 0 };
        var ended = false;
        engine.TrackPlayedToEnd += () => ended = true;
        engine.Play(TestAudio.Tone("whatever", 0.3, 440));
        var state = engine.OutputStatus.State;
        Assert.True(state is AudioOutputState.Playing or AudioOutputState.NoDevice or AudioOutputState.Failed, $"state {state}");
        Assert.True(TestAudio.WaitFor(() => ended, 5), "the track reaches its end with or without a device");
        if (AudioDeviceList.DefaultOutputDevice() is null) Assert.Equal(AudioOutputState.NoDevice, state);
    }

    [Fact]
    public void ARecoveredDeviceIsFoundAgainByTheWatcher()
    {
        var device = TestAudio.RequireOutputDevice();
        using var engine = new AudioEngine { MasterVolume = 0 };
        engine.SetOutputDevice(Unplugged);
        engine.Play(TestAudio.Tone("recover", 5, 440));
        Assert.Equal(AudioOutputState.DeviceMissing, engine.OutputStatus.State);
        // The device "comes back": point at one that exists and tell the engine Windows changed.
        engine.SetOutputDevice(device);
        engine.OnDeviceConfigurationChanged();
        Assert.True(TestAudio.WaitFor(() => engine.OutputStatus.IsAudible, 5), engine.OutputStatus.Message);
        Assert.True(engine.IsPlaying, "playback carried on through the switch");
        engine.Stop();
    }

    [Fact]
    public void DeviceListingNeverThrows()
    {
        var outputs = AudioDeviceList.OutputDevices();
        var inputs = AudioDeviceList.InputDevices();
        Assert.All(outputs, d => Assert.True(d.ChannelCount > 0 && d.SampleRate > 0));
        Assert.All(inputs, d => Assert.False(string.IsNullOrEmpty(d.Uid)));
        using var token = AudioDeviceList.ListenForChanges(() => { });
    }

    [Fact]
    public void AnUnreadableFileIsTheOnlyThingPlayThrows()
    {
        using var engine = new AudioEngine();
        engine.EnableManualRenderingForTests();
        var error = Assert.Throws<AudioEngineException>(() => engine.Play(Path.Combine(Path.GetTempPath(), "no-such-file.mp3")));
        Assert.Equal(AudioEngineException.Kind.Unreadable, error.Reason);
        var tone = TestAudio.Tone("empty-trim", 1, 440);
        Assert.Equal(AudioEngineException.Kind.EmptySchedule, Assert.Throws<AudioEngineException>(() => engine.Play(tone, trimStart: 0.5, trimEnd: 0.5)).Reason);
        Assert.False(engine.IsPlaying);
    }
}

/// <summary>Windows-only: the pieces that stand in for AVAudioConverter and the output unit's channel map.</summary>
public sealed class ConversionTests
{
    [Fact]
    public void TheChannelMapPutsThePairWhereAsked()
    {
        Assert.Equal([0, 1], ChannelMap.Make(2, 0, mono: false));
        Assert.Equal([-1, -1, 0, 1, -1, -1, -1, -1], ChannelMap.Make(8, 2, mono: false));
        Assert.Equal([-1, -1, -1, -1, -1, 0, -1, -1], ChannelMap.Make(8, 5, mono: true));
        Assert.Equal([0, 1, -1, -1], ChannelMap.Make(4, 3, mono: false));
        Assert.Equal([0, 1], ChannelMap.Make(1, 0, mono: false));
    }

    [Fact]
    public void TheResamplerHasNoDelayAndUnityGain()
    {
        var resampler = new SincResampler(44_100, 48_000, 1);
        var input = new float[44_100];
        input[22_050] = 1;
        var output = new[] { new float[48_100] };
        var written = resampler.Process([input], 0, input.Length, output, 0);
        written += resampler.Flush(output, written);
        Assert.True(Math.Abs(written - 48_000) <= 1, $"{written} frames");
        Assert.Equal(24_000, TestAudio.PeakIndex(output[0].Take(written).ToList()));
        var dc = new SincResampler(48_000, 44_100, 1);
        var ones = new[] { Enumerable.Repeat(0.5f, 4800).ToArray() };
        var down = new[] { new float[4800] };
        var made = dc.Process(ones, 0, 4800, down, 0);
        Assert.True(down[0].Skip(100).Take(made - 200).All(s => MathF.Abs(s - 0.5f) < 1e-3));
    }

    [Fact]
    public void TheDecoderReadsAWindowOfTheFileAtTheEngineRate()
    {
        var path = TestAudio.Wav("decode", 44_100, 2, 2, i => i / 88_200f, right: _ => -0.5f);
        using var decoder = AudioFileDecoder.Open(path, 48_000);
        Assert.Equal(44_100, decoder.SourceRate);
        Assert.Equal(2, decoder.SourceChannels);
        Assert.True(Math.Abs(decoder.Duration - 2) < 1e-6);
        decoder.Seek(44_100, 66_150);
        var left = new float[48_000];
        var right = new float[48_000];
        var got = decoder.Read(left, right, 0, left.Length);
        Assert.True(Math.Abs(got - 24_000) <= 2, $"{got} frames for half a second");
        Assert.True(MathF.Abs(left[0] - 0.5f) < 0.002, $"starts at the window ({left[0]})");
        Assert.True(MathF.Abs(right[1000] + 0.5f) < 1e-3);
    }

    [Fact]
    public void MonoAndSurroundFoldToStereo()
    {
        var left = new float[1];
        var right = new float[1];
        AudioFileDecoder.Fold([0.3f], 1, 1, left, right);
        Assert.Equal((0.3f, 0.3f), (left[0], right[0]));
        AudioFileDecoder.Fold([0.1f, 0.2f, 0.4f, 0.9f, 0.2f, 0.3f], 1, 6, left, right);
        Assert.True(MathF.Abs(left[0] - (0.1f + 0.4f * 0.70710677f + 0.2f * 0.70710677f)) < 1e-6, "LFE left out");
        Assert.True(MathF.Abs(right[0] - (0.2f + 0.4f * 0.70710677f + 0.3f * 0.70710677f)) < 1e-6);
    }
}

/// <summary>Windows: the Audio function's controller (playlists with crossfades, transport, bus status).</summary>
public sealed class AudioControllerTests
{
    private sealed class FakeLibrary : IAudioLibrary
    {
        public readonly Dictionary<string, Playlist> Playlists = [];
        public readonly Dictionary<string, AudioItem> Items = [];
        public readonly Dictionary<string, string> Paths = [];

        public Playlist? Playlist(string id) => Playlists.GetValueOrDefault(id);

        public AudioItem? AudioItem(string id) => Items.GetValueOrDefault(id);

        public string? FilePath(AudioItem item) => Paths.GetValueOrDefault(item.Id);

        public AudioItem Add(string id, string path, double? inPoint = null, double? outPoint = null)
        {
            var item = new AudioItem { Id = id, Name = id.ToUpperInvariant(), FileHash = id, FileName = id + ".wav", InPoint = inPoint, OutPoint = outPoint };
            Items[id] = item;
            Paths[id] = path;
            return item;
        }

        public Playlist List(string id, PlaybackMode mode, double? crossfade, params string[] items)
        {
            var playlist = new Playlist
            {
                Id = id, Name = id, PlaybackMode = mode, CrossfadeSeconds = crossfade,
                Entries = items.Select((item, index) => new PlaylistEntry { Id = $"e{index}", RefKind = PlaylistRefKind.Audio, RefId = item }).ToList(),
            };
            Playlists[id] = playlist;
            return playlist;
        }
    }

    private static (AudioController Controller, List<AudioEngine> Engines) Offline(FakeLibrary library)
    {
        var engines = new List<AudioEngine>();
        var controller = new AudioController(library, engineFactory: key =>
        {
            var engine = new AudioEngine(key);
            engine.EnableManualRenderingForTests();
            engines.Add(engine);
            return engine;
        }, startsPolling: false)
        { FadeEnabled = false };
        return (controller, engines);
    }

    [Fact]
    public void APlaylistPlaysThroughItsTracksAndStopsAtTheEnd()
    {
        var library = new FakeLibrary();
        library.Add("a", TestAudio.Tone("ctl-a", 0.3, 440));
        library.Add("b", TestAudio.Tone("ctl-b", 0.3, 550));
        var playlist = library.List("p", PlaybackMode.PlayAll, 0, "a", "b");
        var (controller, engines) = Offline(library);
        using (controller)
        {
            Assert.Empty(controller.Play(playlist));
            var bus = controller.Status().Single();
            Assert.Equal(new AudioBusStatus("p", null, "A", true, 0, bus.Duration), bus);
            Assert.True(Math.Abs(bus.Duration!.Value - 0.3) < 0.01);
            engines[0].RenderOfflineForTests(16_000);
            Assert.True(TestAudio.WaitFor(() => controller.Status().Single().TrackName == "B"), "the next track follows the natural end");
            engines[0].RenderOfflineForTests(16_000);
            Assert.True(TestAudio.WaitFor(() => !controller.Status().Single().Playing), "the pass ends");
            Assert.Null(controller.Status().Single().TrackName);
        }
    }

    [Fact]
    public void ThePollStartsTheNextTracksCrossfadeBeforeTheEnd()
    {
        var library = new FakeLibrary();
        library.Add("a", TestAudio.Tone("xf-a", 4, 440));
        library.Add("b", TestAudio.Tone("xf-b", 4, 550));
        var playlist = library.List("p", PlaybackMode.PlayAll, 2, "a", "b");
        var (controller, engines) = Offline(library);
        using (controller)
        {
            controller.Play(playlist);
            engines[0].RenderOfflineForTests(48_000);
            controller.PollNow();
            Assert.Equal("A", controller.Status().Single().TrackName);
            engines[0].RenderOfflineForTests(60_000);
            controller.PollNow();
            Assert.Equal("B", controller.Status().Single().TrackName);
            var crossfade = engines[0].RenderOfflineForTests(96_000);
            Assert.Equal(0, OfflineRenderTests.Discontinuities(crossfade));
        }
    }

    [Fact]
    public void TransportButtonsMoveThroughTheList()
    {
        var library = new FakeLibrary();
        foreach (var id in new[] { "a", "b", "c" }) library.Add(id, TestAudio.Tone($"tr-{id}", 6, 440));
        var playlist = library.List("p", PlaybackMode.LoopPlaylist, 0, "a", "b", "c");
        var (controller, engines) = Offline(library);
        using (controller)
        {
            Assert.Throws<InvalidOperationException>(() => controller.Transport(AudioTransportAction.Next));
            controller.Play(playlist);
            controller.Transport(AudioTransportAction.Next);
            Assert.Equal("B", controller.Status()[0].TrackName);
            controller.Transport(AudioTransportAction.Seek, 4);
            engines[0].RenderOfflineForTests(4_800);
            controller.PollNow();
            Assert.True(controller.Status()[0].Position >= 4);
            controller.Transport(AudioTransportAction.Previous);
            Assert.Equal("B", controller.Status()[0].TrackName);
            Assert.True(controller.Status()[0].Position < 0.5, "more than 3 s in, previous restarts the track");
            controller.Transport(AudioTransportAction.Previous);
            Assert.Equal("A", controller.Status()[0].TrackName);
            controller.Transport(AudioTransportAction.PlayPause);
            Assert.False(controller.Status()[0].Playing);
            controller.Transport(AudioTransportAction.PlayPause);
            Assert.True(controller.Status()[0].Playing);
            Assert.Throws<InvalidOperationException>(() => controller.Transport(AudioTransportAction.Seek));
            Assert.Equal(AudioTransportAction.PlayPause, AudioController.ParseTransportAction("playPause"));
            Assert.Null(AudioController.ParseTransportAction("rewind"));
        }
    }

    [Fact]
    public void OneBusPerMixAndStopsReportWhatTheyStopped()
    {
        var library = new FakeLibrary();
        var item = library.Add("a", TestAudio.Tone("bus-a", 3, 440));
        library.Add("b", TestAudio.Tone("bus-b", 3, 550));
        var playlist = library.List("p", PlaybackMode.PlayAll, 0, "b");
        var (controller, _) = Offline(library);
        using (controller)
        {
            controller.Play(item);
            var replaced = controller.Play(playlist);
            Assert.Equal([(null, "a")], replaced);
            Assert.Single(controller.Players);
            Assert.Equal(("p", (string?)null), controller.StopPlaylist("p"));
            Assert.Null(controller.StopPlaylist("p"));
            Assert.Empty(controller.Status());
        }
    }

    [Fact]
    public void AMissingTrackIsSkipped()
    {
        var library = new FakeLibrary();
        library.Items["gone"] = new AudioItem { Id = "gone", Name = "GONE", FileHash = "x", FileName = "x.wav" };
        library.Add("b", TestAudio.Tone("skip-b", 2, 550));
        var playlist = library.List("p", PlaybackMode.PlayAll, 0, "gone", "b");
        var (controller, _) = Offline(library);
        using (controller)
        {
            controller.Play(playlist);
            Assert.Equal("B", controller.Status().Single().TrackName);
        }
    }

    [Fact]
    public void TheMasterAndMixLevelsReachTheBus()
    {
        var library = new FakeLibrary();
        var item = library.Add("a", TestAudio.Wav("levels", 48_000, 1, 1, _ => 0.5f));
        var (controller, engines) = Offline(library);
        using (controller)
        {
            controller.MixLevels = _ => new AudioMixLevel(0.5f, false, true);
            controller.MasterVolume = 0.5f;
            controller.Play(item);
            var output = engines[0].RenderOfflineForTests(4_800);
            Assert.True(MathF.Abs(output.Data[0][2_000] - 0.125f) < 1e-4, $"{output.Data[0][2_000]}");
            controller.IsMuted = true;
            Assert.True(engines[0].IsMuted);
        }
    }
}
