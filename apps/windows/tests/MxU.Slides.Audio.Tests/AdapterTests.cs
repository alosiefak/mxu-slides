using MxU.Slides.Audio.Control;
using MxU.Slides.Audio.Multitrack;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Audio.Tests;

/// <summary>Windows: the adapters that plug the engines into Core's TimelinePlayer (ITimelineMusic, ITracksPlayer).</summary>
public sealed class AdapterTests
{
    private sealed class OneItemLibrary(AudioItem item, string path) : IAudioLibrary
    {
        public Playlist? Playlist(string id) => null;

        public AudioItem? AudioItem(string id) => id == item.Id ? item : null;

        public string? FilePath(AudioItem asked) => asked.Id == item.Id ? path : null;
    }

    [Fact]
    public void TimelineMusicPlaysSeeksAndStopsClips()
    {
        var item = new AudioItem { Id = "song", Name = "Song", FileHash = "h", FileName = "song.wav" };
        var path = TestAudio.Wav("adapter-music", 48_000, 2, 6, _ => 0.25f);
        AudioEngine? engine = null;
        using var audio = new AudioController(new OneItemLibrary(item, path), engineFactory: key =>
        {
            engine = new AudioEngine(key);
            engine.EnableManualRenderingForTests();
            return engine;
        }, startsPolling: false) { FadeEnabled = false };
        var music = new TimelineMusic(audio);
        var played = new List<string>();
        music.Played += (clip, _) => played.Add(clip.Id);
        Assert.Null(music.ClipPlayer);
        music.Play(new TimelineMusicClip("clip-1", item, 1, 5, 0.5, ClipEnvelopes.Make(), false, null));
        Assert.Equal(["clip-1"], played);
        var state = music.Player("song")!;
        Assert.Equal(("song", "clip-1", true), (state.SingleItemId, state.ClipId, state.IsPlaying));
        Assert.True(Math.Abs(state.Elapsed - 0.5) < 0.01, $"starts half a second into the clip ({state.Elapsed})");
        Assert.Equal("clip-1", music.ClipPlayer?.ClipId);
        music.Seek("song", 2);
        engine!.RenderOfflineForTests(480);
        audio.PollNow();
        Assert.True(Math.Abs(music.Player("song")!.Elapsed - 2) < 0.05);
        (string?, string?)? stopped = null;
        music.Stopped += cue => stopped = cue;
        music.Stop("song", 0);
        Assert.Equal((null, "song"), stopped);
        Assert.Null(music.Player("song"));
    }

    [Fact]
    public void TheLocalTracksPlayerAnswersTheTimeline()
    {
        var path = TestAudio.Wav("adapter-local", 48_000, 1, 3, _ => 0.5f);
        var song = new MultitrackSong("item|deck|", [MultitrackStems.Make(path, "keys")]);
        using var engine = new MultitrackEngine(48_000, 1);
        using var local = new LocalTracksPlayer(engine);
        ITracksPlayer tracks = local;
        local.SetRouting(new MultitrackRouting([new("keys", sends: new() { ["m"] = 1 })], [new("m", outputs: [new([0])])]));
        Assert.Null(tracks.LatestSongId);
        tracks.Play(song, 1);
        local.AfterPending(() => { });
        Assert.Equal(song.Id, tracks.LatestSongId);
        Assert.Null(tracks.Position(song.Id));
        var output = TestAudio.Render(engine, 0.2)[0];
        Assert.True(MathF.Abs(output[^1] - 0.5f) < 1e-5);
        Assert.True(Math.Abs(tracks.Position(song.Id)!.Value - 1.2) < 0.02);
        Assert.Null(tracks.Position("another song"));
        Assert.False(tracks.Reattaching);
        tracks.Stop();
        local.AfterPending(() => { });
        TestAudio.Render(engine, 0.05);
        Assert.Null(tracks.Position(song.Id));
    }

    [Fact]
    public void TheRemoteTracksPlayerDrivesAPlayerOverThePipe()
    {
        var path = TracksPlayerWire.SocketPath(Guid.NewGuid().ToString());
        var engine = new MultitrackEngine();
        var server = new TracksPlayerServer(path, engine, exit: _ => { });
        Assert.True(server.Start());
        using var tracks = new RemoteTracksPlayer(path, launch: () => { });
        var routing = new MultitrackRouting([new("click", sends: new() { ["m"] = 1 })], [new("m", outputs: [new([0])])]);
        tracks.SetRouting(routing);
        var song = new MultitrackSong("item|deck|", [MultitrackStems.Make(TestAudio.Wav("adapter-remote", 48_000, 2, 2, _ => 0), "click")]);
        tracks.CueAhead([song]);
        Assert.True(TestAudio.WaitFor(() => engine.PreloadedSongIds.Contains(song.Id)), "preloads reach the player");
        tracks.Play(song, 0.5);
        Assert.True(TestAudio.WaitFor(() => tracks.LatestSongId == song.Id), "the player reports the song");
        Assert.True(TestAudio.WaitFor(() => engine.SongChannelIds.SequenceEqual(["click"])));
        Assert.True(tracks.IsConnected);
        Assert.False(tracks.Reattaching);
        tracks.Stop();
        server.ShutDown();
        Assert.True(TestAudio.WaitFor(() => !tracks.IsConnected));
        Assert.True(tracks.Reattaching, "just lost: the timeline should wait, not fall back");
        engine.Dispose();
    }
}
