using MxU.Slides.Core.Automation;
using System.IO.Pipes;
using System.Text;
using MxU.Slides.Audio.Multitrack;

namespace MxU.Slides.Audio.Tests;

/// <summary>Port of TracksPlayerTests.swift: the app and a separate tracks player over the pipe.</summary>
public sealed class TracksPlayerTests
{
    private sealed class Box<T>(T value)
    {
        private readonly Lock gate = new();
        private T stored = value;

        public T Value
        {
            get { lock (gate) return stored; }
            set { lock (gate) stored = value; }
        }

        public void Update(Func<T, T> change)
        {
            lock (gate) stored = change(stored);
        }
    }

    private static string SilentStem() => TestAudio.Wav("tracks", 48_000, 2, 1, _ => 0);

    private static string SocketPath() => TracksPlayerWire.SocketPath(Guid.NewGuid().ToString());

    private static NamedPipeClientStream RawConnect(string path)
    {
        var client = new NamedPipeClientStream(".", path, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
        client.Connect(1000);
        return client;
    }

    [Fact]
    public void TheClientStartsThePlayerAndHearsItsStatus()
    {
        var path = SocketPath();
        var servers = new Box<List<TracksPlayerServer>>([]);
        var launches = new Box<int>(0);
        var client = new TracksPlayerClient(path, () =>
        {
            launches.Update(n => n + 1);
            var server = new TracksPlayerServer(path, new MultitrackEngine(), exit: _ => { });
            server.Start();
            servers.Update(list => [.. list, server]);
        });
        var latest = new Box<TracksPlayerStatus?>(null);
        client.OnStatus = status => latest.Value = status;
        client.Send(new TracksPlayerMessage.Routing(new MultitrackRouting([new MultitrackRouting.Channel("click")])));
        client.Send(new TracksPlayerMessage.Cue(new MultitrackSong("way-maker", [MultitrackStems.Make(SilentStem(), "click")]), 0));
        // Starting the player (a new engine and pipe) can take a few seconds on a busy machine.
        Assert.True(TestAudio.WaitFor(() => latest.Value?.SongId == "way-maker", seconds: 10));
        Assert.Equal(1, launches.Value);
        Assert.Equal(48_000, latest.Value?.SampleRate);
        client.Disconnect();
        foreach (var server in servers.Value) server.ShutDown();
    }

    [Fact]
    public void ARelaunchedSlidesFindsTheSongStillCued()
    {
        var path = SocketPath();
        var server = new TracksPlayerServer(path, new MultitrackEngine(), exit: _ => { });
        Assert.True(server.Start());
        Assert.False(new TracksPlayerServer(path, new MultitrackEngine(), exit: _ => { }).Start());
        var first = new TracksPlayerClient(path, () => { });
        first.Send(new TracksPlayerMessage.Cue(new MultitrackSong("song", [MultitrackStems.Make(SilentStem(), "c")]), 0.5));
        var firstHeard = new Box<bool>(false);
        first.OnStatus = status => { if (status.SongId == "song") firstHeard.Value = true; };
        Assert.True(TestAudio.WaitFor(() => firstHeard.Value));
        first.Disconnect();
        var launchedAgain = new Box<bool>(false);
        var second = new TracksPlayerClient(path, () => launchedAgain.Value = true);
        var heard = new Box<TracksPlayerStatus?>(null);
        second.OnStatus = status => heard.Value = status;
        second.Reattach();
        Assert.True(TestAudio.WaitFor(() => heard.Value?.SongId == "song"));
        Assert.True(Math.Abs(heard.Value!.Position() - 0.5) < 0.01);
        Assert.False(launchedAgain.Value, "must not launch a second player");
        second.Disconnect();
        server.ShutDown();
    }

    [Fact]
    public void AnIdlePlayerLeftAloneQuits()
    {
        var path = SocketPath();
        var exited = new Box<int?>(null);
        // Two seconds alone (the app uses ten minutes): long enough that a reconnect on a slow
        // CI machine isn't mistaken for the app going away, short enough for a test.
        var server = new TracksPlayerServer(path, new MultitrackEngine(), orphanLimit: 2, exit: code => exited.Value = code);
        Assert.True(server.Start());
        var client = new TracksPlayerClient(path, () => { });
        var connected = new Box<bool>(false);
        var drops = new Box<List<string>>([]);
        client.OnStatus = _ => connected.Value = true;
        client.OnDisconnect = reason => drops.Update(list => [.. list, reason]);
        client.Reattach();
        Assert.True(TestAudio.WaitFor(() => connected.Value, seconds: 10));
        Thread.Sleep(2500);
        Assert.True(drops.Value.Count == 0, $"the connection dropped: {string.Join(", ", drops.Value)}");
        Assert.Null(exited.Value);
        client.Disconnect();
        Assert.True(TestAudio.WaitFor(() => exited.Value == 0, seconds: 10));
    }

    [Fact]
    public void AnIdlePlayerOfAnotherVersionMakesWay()
    {
        var path = SocketPath();
        var exited = new Box<int?>(null);
        var server = new TracksPlayerServer(path, new MultitrackEngine(), exit: code => exited.Value = code);
        Assert.True(server.Start());
        using var raw = RawConnect(path);
        var line = TracksPlayerWire.Encode(new TracksPlayerMessage.Hello(TracksPlayerWire.Version + 1));
        raw.Write(line);
        raw.WriteByte((byte)'\n');
        raw.Flush();
        Assert.True(TestAudio.WaitFor(() => exited.Value == 0));
    }

    [Fact]
    public void AConnectionThatNeverSaysHelloCantTakeSlidesPlace()
    {
        var path = SocketPath();
        var server = new TracksPlayerServer(path, new MultitrackEngine(), exit: _ => { });
        Assert.True(server.Start());
        var launched = new Box<bool>(false);
        var client = new TracksPlayerClient(path, () => launched.Value = true);
        var statuses = new Box<int>(0);
        var reasons = new Box<List<string>>([]);
        client.OnStatus = _ => statuses.Update(n => n + 1);
        client.OnDisconnect = reason => reasons.Update(list => [.. list, reason]);
        client.Reattach();
        Assert.True(TestAudio.WaitFor(() => statuses.Value > 0));
        Assert.False(new TracksPlayerServer(path, new MultitrackEngine(), exit: _ => { }).Start());
        using (RawConnect(path)) Thread.Sleep(200);
        var before = statuses.Value;
        Assert.True(TestAudio.WaitFor(() => statuses.Value > before + 2));
        Assert.Empty(reasons.Value);
        Assert.True(client.IsConnected);
        Assert.False(launched.Value);
        client.Disconnect();
        server.ShutDown();
    }

    [Fact]
    public void ALostConnectionReattachesButAReplacedSlidesStaysOff()
    {
        var path = SocketPath();
        var server = new TracksPlayerServer(path, new MultitrackEngine(), exit: _ => { });
        Assert.True(server.Start());
        var first = new TracksPlayerClient(path, () => { });
        var firstReasons = new Box<List<string>>([]);
        var firstConnects = new Box<int>(0);
        first.OnConnect = () => firstConnects.Update(n => n + 1);
        first.OnDisconnect = reason => firstReasons.Update(list => [.. list, reason]);
        var heardPid = new Box<int?>(null);
        first.OnStatus = status => heardPid.Value = status.Pid;
        first.Reattach();
        Assert.True(TestAudio.WaitFor(() => heardPid.Value == Environment.ProcessId));

        server.ShutDown();
        var again = new TracksPlayerServer(path, new MultitrackEngine(), exit: _ => { });
        Assert.True(again.Start());
        Assert.True(TestAudio.WaitFor(() => firstConnects.Value == 2));
        Assert.Equal(["end of file"], firstReasons.Value);
        var second = new TracksPlayerClient(path, () => { });
        second.Reattach();
        Assert.True(TestAudio.WaitFor(() => firstReasons.Value.LastOrDefault() == "another Slides took over"));
        Thread.Sleep(1200);
        Assert.Equal(2, firstConnects.Value);
        Assert.True(!first.IsConnected && second.IsConnected);
        second.Disconnect();
        again.ShutDown();
    }

    [Fact]
    public void ThePlayersShareIsMappedByName()
    {
        var path = SocketPath();
        var engine = new MultitrackEngine();
        var server = new TracksPlayerServer(path, engine, exit: _ => { });
        Assert.True(server.Start());
        var name = TracksPlayerWire.ShareName(path);
        Assert.True(Encoding.UTF8.GetByteCount(name) <= 31);
        var mapped = TracksShare.Open(name);
        Assert.NotNull(mapped);
        Assert.Equal(48_000, mapped!.SampleRate);
        var ones = Enumerable.Repeat(1f, 64).ToArray();
        engine.Share!.Write(9, ones, ones, 64);
        Assert.Equal(64, mapped.WrittenFrames(9));
        server.ShutDown();
        // Windows keeps a mapping alive while anyone has it open, so the reader lets go first.
        mapped.Dispose();
        Assert.Null(TracksShare.Open(name));
    }

    [Fact]
    public void ThePlayerPreloadsWhatSlidesNames()
    {
        var path = SocketPath();
        var engine = new MultitrackEngine();
        var server = new TracksPlayerServer(path, engine, exit: _ => { });
        Assert.True(server.Start());
        var client = new TracksPlayerClient(path, () => { });
        client.Send(new TracksPlayerMessage.Preload([new MultitrackSong("next", [MultitrackStems.Make(SilentStem(), "c")])]));
        Assert.True(TestAudio.WaitFor(() => engine.PreloadedSongIds.SequenceEqual(["next"])));
        client.Disconnect();
        server.ShutDown();
    }
}
