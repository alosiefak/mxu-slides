using MxU.Slides.Core.Automation;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MxU.Slides.Core.Scene;
using MxU.Slides.Core.Threading;

namespace MxU.Slides.Audio.Multitrack;

/// <summary>
/// What the app and the tracks player say to each other, one JSON object per line.
/// The shape follows the Mac's Swift enum: <c>{"cue":{"song":{…},"at":0}}</c>.
/// Port of TracksPlayerMessage.
/// </summary>
[JsonConverter(typeof(TracksPlayerMessageConverter))]
public abstract record TracksPlayerMessage
{
    public sealed record Hello(int Version) : TracksPlayerMessage;
    public sealed record Attach(string? DeviceUid) : TracksPlayerMessage;
    public sealed record Cue(MultitrackSong Song, double At) : TracksPlayerMessage;
    public sealed record Routing(MultitrackRouting Value) : TracksPlayerMessage;
    public sealed record Play(double HostTime) : TracksPlayerMessage;
    public sealed record Stop : TracksPlayerMessage;
    public sealed record Seek(double Seconds) : TracksPlayerMessage;
    public sealed record Preload(IReadOnlyList<MultitrackSong> Songs) : TracksPlayerMessage
    {
        public bool Equals(Preload? other) => other is not null && Songs.SequenceEqual(other.Songs);
        public override int GetHashCode() => Songs.Count;
    }
    public sealed record Chase(MultitrackSong? Song, string SongId, double Position, double HostTime) : TracksPlayerMessage;
    public sealed record Transition(MultitrackTransition? Value) : TracksPlayerMessage;

    /// <summary>A jump to a song (or with no song, a fade out) over <c>Seconds</c>.</summary>
    public sealed record Crossfade(MultitrackSong? Song, double At, double Seconds) : TracksPlayerMessage;
    public sealed record Quit : TracksPlayerMessage;
    public sealed record Status(TracksPlayerStatus Value) : TracksPlayerMessage;

    /// <summary>Sent to an app whose place another app just took.</summary>
    public sealed record Replaced : TracksPlayerMessage;
}

/// <summary>What the player reports ~15 times a second. Port of TracksPlayerStatus.</summary>
public sealed record TracksPlayerStatus(
    [property: JsonPropertyName("songID")] string? SongId,
    [property: JsonPropertyName("playing")] bool Playing,
    [property: JsonPropertyName("frame")] long Frame,
    [property: JsonPropertyName("hostTime")] double HostTime,
    [property: JsonPropertyName("sampleRate")] double SampleRate,
    [property: JsonPropertyName("deviceUID")] string? DeviceUid,
    [property: JsonPropertyName("deviceChannels")] int DeviceChannels,
    [property: JsonPropertyName("starvedFrames")] long StarvedFrames,
    [property: JsonPropertyName("meters")] Dictionary<string, float[]> Meters,
    [property: JsonPropertyName("error")] string? Error = null,
    [property: JsonPropertyName("channels")] IReadOnlyList<string>? Channels = null,
    [property: JsonPropertyName("pid")] int? Pid = null)
{
    /// <summary>The song position at a host-clock time (default now), extrapolated from the report.</summary>
    public double Position(double? now = null)
    {
        var seconds = SampleRate > 0 ? Frame / SampleRate : 0;
        var at = now ?? ShowState.HostClock();
        if (!Playing || HostTime <= 0 || at <= HostTime) return seconds;
        return seconds + (at - HostTime);
    }

    /// <summary>Each channel's [left rms, left peak, right rms, right peak] as readings.</summary>
    public Dictionary<string, IReadOnlyList<LevelReading>> MeterReadings() =>
        Meters.Where(m => m.Value.Length is 2 or 4).ToDictionary(m => m.Key, m => (IReadOnlyList<LevelReading>)(m.Value.Length == 4
            ? [new LevelReading(m.Value[0], m.Value[1]), new LevelReading(m.Value[2], m.Value[3])]
            : [new LevelReading(m.Value[0], m.Value[1]), new LevelReading(m.Value[0], m.Value[1])]));
}

/// <summary>Names and versions both sides agree on. Port of TracksPlayerWire.</summary>
public static class TracksPlayerWire
{
    public const int Version = 5;

    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>The player's pipe name for one app identity (a hash, so names stay short and safe).</summary>
    public static string SocketPath(string appIdentity) => $"mxu-tracks-{Fnv1a(appIdentity):x8}";

    /// <summary>The shared-memory name the player's mix shares live under.</summary>
    public static string ShareName(string socketPath) => $"Local\\mxu-trk-{Fnv1a(socketPath):x8}";

    private static uint Fnv1a(string text)
    {
        var hash = 2_166_136_261u;
        foreach (var b in Encoding.UTF8.GetBytes(text)) hash = unchecked((hash ^ b) * 16_777_619u);
        return hash;
    }

    public static byte[] Encode(TracksPlayerMessage message) => JsonSerializer.SerializeToUtf8Bytes(message, Options);

    public static TracksPlayerMessage? Decode(ReadOnlySpan<byte> line)
    {
        try { return JsonSerializer.Deserialize<TracksPlayerMessage>(line, Options); }
        catch { return null; }
    }

    /// <summary>Connects to a player's pipe, or null when no player is listening.</summary>
    internal static NamedPipeClientStream? TryConnect(string pipeName, int timeoutMilliseconds = 250)
    {
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            client.Connect(timeoutMilliseconds);
            return client;
        }
        catch
        {
            client.Dispose();
            return null;
        }
    }

    internal static NamedPipeServerStream Listen(string pipeName) =>
        new(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
}

internal sealed class TracksPlayerMessageConverter : JsonConverter<TracksPlayerMessage>
{
    public override TracksPlayerMessage? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var node = JsonNode.Parse(ref reader) as JsonObject ?? throw new JsonException("Expected an object");
        var (name, body) = node.Single();
        var payload = body as JsonObject ?? [];
        T Get<T>(string key) => payload[key] is { } value ? value.Deserialize<T>(options)! : default!;
        return name switch
        {
            "hello" => new TracksPlayerMessage.Hello(Get<int>("version")),
            "attach" => new TracksPlayerMessage.Attach(Get<string?>("deviceUID")),
            "cue" => new TracksPlayerMessage.Cue(Get<MultitrackSong>("song"), Get<double>("at")),
            "routing" => new TracksPlayerMessage.Routing(Get<MultitrackRouting>("_0")),
            "play" => new TracksPlayerMessage.Play(Get<double>("hostTime")),
            "stop" => new TracksPlayerMessage.Stop(),
            "seek" => new TracksPlayerMessage.Seek(Get<double>("seconds")),
            "preload" => new TracksPlayerMessage.Preload(Get<List<MultitrackSong>>("songs") ?? []),
            "chase" => new TracksPlayerMessage.Chase(Get<MultitrackSong?>("song"), Get<string>("songID"), Get<double>("position"), Get<double>("hostTime")),
            "transition" => new TracksPlayerMessage.Transition(Get<MultitrackTransition?>("_0")),
            "crossfade" => new TracksPlayerMessage.Crossfade(Get<MultitrackSong?>("song"), Get<double>("at"), Get<double>("seconds")),
            "quit" => new TracksPlayerMessage.Quit(),
            "status" => new TracksPlayerMessage.Status(Get<TracksPlayerStatus>("_0")),
            "replaced" => new TracksPlayerMessage.Replaced(),
            _ => throw new JsonException($"Unknown message {name}"),
        };
    }

    public override void Write(Utf8JsonWriter writer, TracksPlayerMessage value, JsonSerializerOptions options)
    {
        JsonNode? Node<T>(T item) => JsonSerializer.SerializeToNode(item, options);
        var (name, payload) = value switch
        {
            TracksPlayerMessage.Hello m => ("hello", new JsonObject { ["version"] = m.Version }),
            TracksPlayerMessage.Attach m => ("attach", new JsonObject { ["deviceUID"] = m.DeviceUid }),
            TracksPlayerMessage.Cue m => ("cue", new JsonObject { ["song"] = Node(m.Song), ["at"] = m.At }),
            TracksPlayerMessage.Routing m => ("routing", new JsonObject { ["_0"] = Node(m.Value) }),
            TracksPlayerMessage.Play m => ("play", new JsonObject { ["hostTime"] = m.HostTime }),
            TracksPlayerMessage.Stop => ("stop", new JsonObject()),
            TracksPlayerMessage.Seek m => ("seek", new JsonObject { ["seconds"] = m.Seconds }),
            TracksPlayerMessage.Preload m => ("preload", new JsonObject { ["songs"] = Node(m.Songs) }),
            TracksPlayerMessage.Chase m => ("chase", new JsonObject
            {
                ["song"] = Node(m.Song), ["songID"] = m.SongId, ["position"] = m.Position, ["hostTime"] = m.HostTime,
            }),
            TracksPlayerMessage.Transition m => ("transition", new JsonObject { ["_0"] = Node(m.Value) }),
            TracksPlayerMessage.Crossfade m => ("crossfade", new JsonObject { ["song"] = Node(m.Song), ["at"] = m.At, ["seconds"] = m.Seconds }),
            TracksPlayerMessage.Quit => ("quit", new JsonObject()),
            TracksPlayerMessage.Status m => ("status", new JsonObject { ["_0"] = Node(m.Value) }),
            TracksPlayerMessage.Replaced => ("replaced", new JsonObject()),
            _ => throw new JsonException($"Unknown message {value.GetType().Name}"),
        };
        new JsonObject { [name] = payload }.WriteTo(writer, options);
    }
}

/// <summary>
/// One end of a player connection: reads lines (on a background task) and hands each
/// decoded message to the owner's queue; writes on the queue. Closing, from either end
/// or an error, calls <c>onClose</c> once with the reason. Port of TracksPlayerConnection.
/// </summary>
internal sealed class TracksPlayerConnection
{
    private readonly PipeStream stream;
    private readonly WorkQueue queue;
    private readonly Action<TracksPlayerMessage> onMessage;
    private readonly Action<TracksPlayerConnection, string> onClose;
    private bool closed;

    public TracksPlayerConnection(PipeStream stream, WorkQueue queue, Action<TracksPlayerMessage> onMessage,
        Action<TracksPlayerConnection, string> onClose)
    {
        this.stream = stream;
        this.queue = queue;
        this.onMessage = onMessage;
        this.onClose = onClose;
        _ = Task.Run(ReadLoop);
    }

    /// <summary>The peer said hello (until then it can't take an app's place).</summary>
    public bool Greeted { get; set; }

    public void Send(TracksPlayerMessage message)
    {
        queue.Async(() =>
        {
            if (closed) return;
            try
            {
                var line = TracksPlayerWire.Encode(message);
                stream.Write(line);
                stream.WriteByte((byte)'\n');
                stream.Flush();
            }
            catch (Exception error)
            {
                Shut($"write error {error.HResult}");
            }
        });
    }

    public void Cancel() => queue.Async(() => Shut("cancelled"));

    private async Task ReadLoop()
    {
        var buffer = new byte[65_536];
        var pending = new List<byte>();
        try
        {
            while (true)
            {
                var count = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (count <= 0)
                {
                    queue.Async(() => Shut("end of file"));
                    return;
                }
                pending.AddRange(buffer.AsSpan(0, count));
                int newline;
                while ((newline = pending.IndexOf((byte)'\n')) >= 0)
                {
                    var line = pending.GetRange(0, newline).ToArray();
                    pending.RemoveRange(0, newline + 1);
                    if (TracksPlayerWire.Decode(line) is { } message) queue.Async(() => { if (!closed) onMessage(message); });
                }
            }
        }
        catch (ObjectDisposedException)
        {
            queue.Async(() => Shut("cancelled"));
        }
        catch (IOException)
        {
            // A pipe whose other end went away reads as broken rather than empty.
            queue.Async(() => Shut("end of file"));
        }
        catch (Exception error)
        {
            queue.Async(() => Shut($"read error {error.HResult}"));
        }
    }

    private void Shut(string reason)
    {
        if (closed) return;
        closed = true;
        try { stream.Dispose(); } catch { }
        onClose(this, reason);
    }
}

/// <summary>
/// The tracks player's side: listens on the pipe, takes orders from the app that said
/// hello most recently (an app that never says hello can't take over), reports status
/// ~15×/s, and keeps playing if the app goes away; left alone and idle for
/// <c>orphanLimit</c> seconds it exits. A player of another version makes way when idle.
/// Port of TracksPlayerServer.
/// </summary>
public sealed class TracksPlayerServer
{
    private readonly string path;
    private readonly MultitrackEngine engine;
    private readonly WorkQueue queue = new("MxU tracks player server");
    private readonly Action<int> exit;
    private readonly List<TracksPlayerConnection> pending = [];
    private TracksPlayerConnection? client;
    private TracksShare? share;
    private ITimer? statusTimer;
    private CancellationTokenSource? listening;
    private double? alone = ShowState.HostClock();
    private string? lastError;

    public TracksPlayerServer(string socketPath, MultitrackEngine engine, double orphanLimit = 600, Action<int>? exit = null)
    {
        path = socketPath;
        this.engine = engine;
        OrphanLimit = orphanLimit;
        this.exit = exit ?? Environment.Exit;
    }

    public double OrphanLimit { get; }

    /// <summary>Starts listening; false when another player already has the pipe (it should be used instead).</summary>
    public bool Start()
    {
        if (TracksPlayerWire.TryConnect(path, 100) is { } other)
        {
            other.Dispose();
            return false;
        }
        engine.OnHandover = () => queue.Async(SendStatus);
        if (TracksShare.Create(TracksPlayerWire.ShareName(path)) is { } made)
        {
            share = made;
            engine.SetShare(made);
        }
        // The listeners exist before Start returns, so a client connecting now finds us. Two
        // wait at once, so one is always ready while the other hands its connection over.
        listening = new CancellationTokenSource();
        var token = listening.Token;
        for (var acceptor = 0; acceptor < 2; acceptor++)
        {
            var first = TracksPlayerWire.Listen(path);
            _ = Task.Run(() => AcceptLoop(first, token));
        }
        statusTimer = ShowTimeProvider.Shared.CreateTimer(_ => queue.Async(Beat), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(66));
        return true;
    }

    public void ShutDown()
    {
        queue.Sync(() =>
        {
            statusTimer?.Dispose();
            listening?.Cancel();
            client?.Cancel();
            client = null;
            foreach (var each in pending) each.Cancel();
            pending.Clear();
            share?.Remove();
        });
    }

    private async Task AcceptLoop(NamedPipeServerStream next, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await next.WaitForConnectionAsync(token).ConfigureAwait(false);
                var connected = next;
                next = TracksPlayerWire.Listen(path);
                queue.Async(() => Accept(connected));
            }
        }
        catch
        {
            // Cancelled (shut down) or the pipe failed.
        }
        next.Dispose();
    }

    private void Accept(NamedPipeServerStream stream)
    {
        TracksPlayerConnection? made = null;
        made = new TracksPlayerConnection(stream, queue,
            message => { if (made is not null) Handle(message, made); },
            (connection, reason) =>
            {
                pending.Remove(connection);
                if (client != connection) return;
                client = null;
                alone = ShowState.HostClock();
                Trace.TraceInformation($"Slides went away ({reason}); playing on alone");
            });
        pending.Add(made);
    }

    private void Handle(TracksPlayerMessage message, TracksPlayerConnection connection)
    {
        if (message is TracksPlayerMessage.Hello hello)
        {
            if (hello.Version != TracksPlayerWire.Version && !engine.IsPlaying)
            {
                Finish(0);
                return;
            }
            pending.Remove(connection);
            if (client is { } previous && previous != connection)
            {
                previous.Send(new TracksPlayerMessage.Replaced());
                previous.Cancel();
            }
            connection.Greeted = true;
            client = connection;
            alone = null;
            SendStatus();
            return;
        }
        if (connection != client || !connection.Greeted) return;
        Handle(message);
    }

    private void Handle(TracksPlayerMessage message)
    {
        switch (message)
        {
            case TracksPlayerMessage.Attach attach:
                var device = attach.DeviceUid is { } uid ? AudioDeviceList.OutputDevices().FirstOrDefault(d => d.Uid == uid) : null;
                engine.Attach(device);
                var state = engine.OutputStatus;
                lastError = state.State is AudioOutputState.NoDevice or AudioOutputState.DeviceMissing or AudioOutputState.Failed
                    ? state.Message : null;
                break;
            case TracksPlayerMessage.Cue cue:
                engine.Cue(cue.Song, cue.At);
                break;
            case TracksPlayerMessage.Routing routing:
                engine.SetRouting(routing.Value);
                break;
            case TracksPlayerMessage.Play play:
                engine.Play(play.HostTime);
                break;
            case TracksPlayerMessage.Stop:
                engine.Stop();
                break;
            case TracksPlayerMessage.Seek seek:
                engine.Seek(seek.Seconds);
                break;
            case TracksPlayerMessage.Preload preload:
                engine.Preload(preload.Songs);
                break;
            case TracksPlayerMessage.Chase chase:
                engine.Chase(chase.Song, chase.SongId, chase.Position, chase.HostTime);
                break;
            case TracksPlayerMessage.Transition transition:
                engine.Transition(transition.Value);
                break;
            case TracksPlayerMessage.Crossfade { Song: { } song } crossfade:
                engine.Crossfade(song, crossfade.At, crossfade.Seconds);
                break;
            case TracksPlayerMessage.Crossfade crossfade:
                engine.FadeOut(crossfade.Seconds);
                break;
            case TracksPlayerMessage.Quit:
                engine.Stop();
                queue.AsyncAfter(TimeSpan.FromMilliseconds(50), () => Finish(0));
                break;
        }
    }

    private void Finish(int code)
    {
        engine.Detach();
        statusTimer?.Dispose();
        listening?.Cancel();
        share?.Remove();
        exit(code);
    }

    private void Beat()
    {
        if (alone is { } since)
        {
            // No app: let a song finish, then quit once idle for long enough.
            if (engine.IsPlaying && engine.SongLength is { } length && engine.Position() >= length) engine.Stop();
            if (!engine.IsPlaying && ShowState.HostClock() - since > OrphanLimit) Finish(0);
            return;
        }
        SendStatus();
    }

    private void SendStatus() => client?.Send(new TracksPlayerMessage.Status(Status()));

    internal TracksPlayerStatus Status()
    {
        var published = engine.Renderer.Published;
        return new TracksPlayerStatus(
            engine.SongId, published.Playing,
            published.Playing ? published.Frame : (long)Math.Round(engine.Position() * engine.SampleRate, MidpointRounding.AwayFromZero),
            published.HostTime, engine.SampleRate, engine.DeviceUid, engine.DeviceChannels, engine.StarvedFrames,
            engine.TakeMeters().ToDictionary(m => m.Key, m => new[] { m.Value.Left.Rms, m.Value.Left.Peak, m.Value.Right.Rms, m.Value.Right.Peak }),
            lastError, engine.SongChannelIds, Environment.ProcessId);
    }
}

/// <summary>
/// The app's side: sends orders, starting the player if it isn't running (and queueing
/// what was said meanwhile), hears its status, and reconnects if the connection drops,
/// unless another app took its place. Callbacks arrive on the client's own thread.
/// Port of TracksPlayerClient.
/// </summary>
public sealed class TracksPlayerClient
{
    private readonly string path;
    private readonly Action launch;
    private readonly WorkQueue queue = new("MxU tracks player client");
    private readonly List<TracksPlayerMessage> backlog = [];
    private TracksPlayerConnection? connection;
    private bool connecting;
    private bool replaced;

    /// <param name="launch">Starts a player process (see <see cref="TracksPlayerHost.Launch"/>).</param>
    public TracksPlayerClient(string socketPath, Action launch)
    {
        path = socketPath;
        this.launch = launch;
    }

    public Action<TracksPlayerStatus>? OnStatus { get; set; }

    public Action? OnConnect { get; set; }

    /// <summary>The connection dropped (with why; "another Slides took over" when replaced).</summary>
    public Action<string>? OnDisconnect { get; set; }

    public bool IsConnected => queue.Sync(() => connection is not null);

    public void Send(TracksPlayerMessage message)
    {
        queue.Async(() =>
        {
            if (connection is not null)
            {
                connection.Send(message);
            }
            else
            {
                backlog.Add(message);
                Connect();
            }
        });
    }

    /// <summary>Connects to a player that is already running (never launches one).</summary>
    public void Reattach()
    {
        queue.Async(() =>
        {
            if (connection is not null || TracksPlayerWire.TryConnect(path) is not { } stream) return;
            Adopt(stream);
        });
    }

    private void Reattach(int attempt)
    {
        queue.AsyncAfter(TimeSpan.FromMilliseconds(100), () =>
        {
            if (connection is not null || connecting) return;
            if (TracksPlayerWire.TryConnect(path) is { } stream) Adopt(stream);
            else if (attempt < 9) Reattach(attempt + 1);
        });
    }

    public void Disconnect()
    {
        queue.Sync(() =>
        {
            connection?.Cancel();
            connection = null;
        });
    }

    private void Connect()
    {
        if (connecting) return;
        if (TracksPlayerWire.TryConnect(path) is { } stream)
        {
            Adopt(stream);
            return;
        }
        connecting = true;
        try { launch(); }
        catch { }
        Retry(0);
    }

    private void Retry(int attempt)
    {
        queue.AsyncAfter(TimeSpan.FromMilliseconds(100), () =>
        {
            if (TracksPlayerWire.TryConnect(path) is { } stream)
            {
                connecting = false;
                Adopt(stream);
            }
            else if (attempt < 30)
            {
                Retry(attempt + 1);
            }
            else
            {
                connecting = false;
                backlog.Clear();
            }
        });
    }

    private void Adopt(PipeStream stream)
    {
        replaced = false;
        TracksPlayerConnection? made = null;
        made = new TracksPlayerConnection(stream, queue,
            message =>
            {
                switch (message)
                {
                    case TracksPlayerMessage.Status status:
                        OnStatus?.Invoke(status.Value);
                        break;
                    case TracksPlayerMessage.Replaced:
                        replaced = true;
                        break;
                }
            },
            (closing, reason) =>
            {
                if (connection != closing) return;
                connection = null;
                OnDisconnect?.Invoke(replaced ? "another Slides took over" : reason);
                if (!replaced) Reattach(0);
            });
        connection = made;
        made.Send(new TracksPlayerMessage.Hello(TracksPlayerWire.Version));
        OnConnect?.Invoke();
        foreach (var message in backlog) made.Send(message);
        backlog.Clear();
    }
}

/// <summary>
/// Running the tracks player as its own process, so a crash or restart of the app doesn't
/// stop the tracks mid-song. The app's executable runs it when started with
/// <see cref="Argument"/> (the WPF app checks for this in Main before showing any window).
/// </summary>
public static class TracksPlayerHost
{
    public const string Argument = "--tracks-player";

    /// <summary>Starts this executable as a headless tracks player on <paramref name="socketPath"/>.</summary>
    public static void Launch(string socketPath)
    {
        if (Environment.ProcessPath is not { } executable) return;
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(Argument);
        start.ArgumentList.Add("--socket");
        start.ArgumentList.Add(socketPath);
        Process.Start(start)?.Dispose();
    }

    /// <summary>
    /// If <paramref name="args"/> ask for the tracks player, runs it here until it decides
    /// to exit and returns true with its exit code; otherwise false (start the app normally).
    /// </summary>
    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (!args.Contains(Argument)) return false;
        var at = Array.IndexOf(args, "--socket");
        if (at < 0 || at + 1 >= args.Length) return true;
        exitCode = Run(args[at + 1]);
        return true;
    }

    public static int Run(string socketPath)
    {
        using var engine = new MultitrackEngine();
        using var exited = new ManualResetEventSlim(false);
        var code = 0;
        var server = new TracksPlayerServer(socketPath, engine, exit: c =>
        {
            code = c;
            exited.Set();
        });
        if (!server.Start()) return 0;
        exited.Wait();
        return code;
    }
}
