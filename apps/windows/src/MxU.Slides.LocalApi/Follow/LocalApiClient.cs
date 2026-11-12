using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace MxU.Slides.LocalApi;

/// <summary>A connection to another computer's Local API WebSocket: send and receive text frames.</summary>
public interface ILocalApiClientSocket : IDisposable
{
    Task SendAsync(string text, CancellationToken cancellationToken);

    /// <summary>The next text frame; throws when the connection has closed.</summary>
    Task<string> ReceiveAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Follows another computer's Local API over its WebSocket (MxU PR #12, LocalAPIClient.swift):
/// subscribes to topics, pings every second and reads the pongs' beat to tell a computer that
/// stopped answering (lost) from one whose app froze (frozen), and reconnects with a doubling
/// delay, giving up after ten tries. It runs on the thread pool and posts what it hears to
/// <see cref="Updates"/>; the app reads them on its own thread.
/// </summary>
public sealed class LocalApiClient : IAsyncDisposable
{
    public sealed record Endpoint(string Host, int Port, string Token)
    {
        public Uri WebSocketUri => new($"ws://{(Host.Contains(':') && !Host.StartsWith('[') ? $"[{Host}]" : Host)}:{Port}/v1/ws");

        public Uri Http(string path) => new($"http://{(Host.Contains(':') && !Host.StartsWith('[') ? $"[{Host}]" : Host)}:{Port}{path}");
    }

    public sealed record Policy
    {
        public int MaxAttempts { get; init; } = 10;
        public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(500);
        public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);
        public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(1);
        public HeartbeatMonitor.Limits Heartbeat { get; init; } = new();

        /// <summary>The wait before the next try, doubling to its ceiling; null once it should give up.</summary>
        public TimeSpan? DelayAfterFailures(int failures)
        {
            if (failures < 1 || failures >= MaxAttempts) return null;
            var doubled = BaseDelay * (1L << Math.Min(failures - 1, 20));
            return doubled < MaxDelay ? doubled : MaxDelay;
        }
    }

    public enum Loss { Closed, NoAnswer, Frozen }

    public abstract record Health
    {
        public sealed record Connecting : Health;

        public sealed record Live : Health;

        public sealed record Lost(Loss Why) : Health;

        public sealed record GaveUp : Health;
    }

    public abstract record Update
    {
        public sealed record HealthChanged(Health Health) : Update;

        public sealed record Event(ApiTopic Topic, byte[] Data) : Update;
    }

    public delegate Task<ILocalApiClientSocket> Connector(Endpoint endpoint, CancellationToken cancellationToken);

    private readonly Endpoint endpoint;
    private readonly IReadOnlyList<ApiTopic> topics;
    private readonly Policy policy;
    private readonly Connector connect;
    private readonly Func<TimeSpan, CancellationToken, Task> sleep;
    private readonly Func<double> clock;
    private readonly Channel<Update> updates = Channel.CreateBounded<Update>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Lock gate = new();
    private CancellationTokenSource? running;
    private Task? runTask;
    private Health? health;
    private HeartbeatMonitor monitor;

    public LocalApiClient(
        Endpoint endpoint, IReadOnlyList<ApiTopic> topics, Policy? policy = null, Connector? connect = null,
        Func<TimeSpan, CancellationToken, Task>? sleep = null, Func<double>? clock = null)
    {
        this.endpoint = endpoint;
        this.topics = topics;
        this.policy = policy ?? new Policy();
        this.connect = connect ?? ConnectWebSocket;
        this.sleep = sleep ?? ((delay, token) => Task.Delay(delay, token));
        this.clock = clock ?? (() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency);
        monitor = new HeartbeatMonitor(this.policy.Heartbeat, this.clock());
    }

    public ChannelReader<Update> Updates => updates.Reader;

    public void Start()
    {
        lock (gate)
        {
            if (runTask is not null) return;
            running = new CancellationTokenSource();
            var token = running.Token;
            runTask = Task.Run(() => RunAsync(token), CancellationToken.None);
        }
    }

    public async Task StopAsync()
    {
        Task? task;
        lock (gate)
        {
            running?.Cancel();
            task = runTask;
            runTask = null;
        }
        if (task is not null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task RunAsync(CancellationToken cancellation)
    {
        var failures = 0;
        while (!cancellation.IsCancellationRequested)
        {
            Report(new Health.Connecting());
            var wentLive = await SessionAsync(cancellation).ConfigureAwait(false);
            if (cancellation.IsCancellationRequested) break;
            failures = wentLive ? 1 : failures + 1;
            if (policy.DelayAfterFailures(failures) is not { } delay)
            {
                Report(new Health.GaveUp());
                break;
            }
            if (health is not Health.Lost { Why: Loss.NoAnswer }) Report(new Health.Lost(Loss.Closed));
            try
            {
                await sleep(delay, cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>One connection: true once it went live (heard the hello and subscribed).</summary>
    private async Task<bool> SessionAsync(CancellationToken cancellation)
    {
        ILocalApiClientSocket socket;
        try
        {
            socket = await connect(endpoint, cancellation).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return false;
        }
        using (socket)
        {
            try
            {
                await socket.ReceiveAsync(cancellation).ConfigureAwait(false);
                await socket.SendAsync(Frame(new JsonObject
                {
                    ["topics"] = new JsonArray([.. topics.Select(t => (JsonNode)t.Raw())]), ["type"] = "subscribe",
                }), cancellation).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                return false;
            }
            monitor = new HeartbeatMonitor(policy.Heartbeat, clock());
            Report(new Health.Live());
            using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            var receive = ReceiveLoopAsync(socket, session.Token);
            var ping = PingLoopAsync(socket, session.Token);
            await Task.WhenAny(receive, ping).ConfigureAwait(false);
            await session.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(receive, ping).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException)
            {
            }
            return true;
        }
    }

    private async Task ReceiveLoopAsync(ILocalApiClientSocket socket, CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested) Handle(await socket.ReceiveAsync(cancellation).ConfigureAwait(false));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Closed: the session ends.
        }
    }

    private async Task PingLoopAsync(ILocalApiClientSocket socket, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            // Never spin without letting the session see its socket close (a sleep that returns at once, in tests).
            await Task.Yield();
            try
            {
                await sleep(policy.PingInterval, cancellation).ConfigureAwait(false);
                await socket.SendAsync(Frame(new JsonObject { ["type"] = "ping" }), cancellation).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                return;
            }
            HeartbeatMonitor.Reading reading;
            lock (gate) reading = monitor.ReadingAt(clock());
            switch (reading)
            {
                case HeartbeatMonitor.Reading.Live: Report(new Health.Live()); break;
                case HeartbeatMonitor.Reading.Frozen: Report(new Health.Lost(Loss.Frozen)); break;
                default:
                    Report(new Health.Lost(Loss.NoAnswer));
                    return;
            }
        }
    }

    private void Handle(string text)
    {
        JsonObject? inbound;
        try
        {
            inbound = JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return;
        }
        switch ((string?)inbound?["type"])
        {
            case "pong":
                ulong? beat = inbound!["beat"] is JsonValue value && value.TryGetValue<ulong>(out var b) ? b : null;
                lock (gate) monitor.Pong(beat, clock());
                break;
            case "event":
                if (ApiTopics.Parse((string?)inbound!["topic"] ?? "") is { } topic && inbound["data"] is { } data)
                    updates.Writer.TryWrite(new Update.Event(topic, ApiJson.Encoded(data)));
                break;
        }
    }

    private void Report(Health next)
    {
        lock (gate)
        {
            if (Equals(next, health)) return;
            health = next;
        }
        updates.Writer.TryWrite(new Update.HealthChanged(next));
    }

    private static string Frame(JsonObject frame) => Encoding.UTF8.GetString(ApiJson.Encoded(frame));

    /// <summary>A real WebSocket, its key in the Authorization header (as the Mac's URLSession sends it).</summary>
    public static async Task<ILocalApiClientSocket> ConnectWebSocket(Endpoint endpoint, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + endpoint.Token);
        socket.Options.KeepAliveInterval = TimeSpan.Zero;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await socket.ConnectAsync(endpoint.WebSocketUri, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        return new WebSocketClientSocket(socket);
    }

    private sealed class WebSocketClientSocket(ClientWebSocket socket) : ILocalApiClientSocket
    {
        private readonly SemaphoreSlim sending = new(1, 1);

        public async Task SendAsync(string text, CancellationToken cancellationToken)
        {
            await sending.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                sending.Release();
            }
        }

        public async Task<string> ReceiveAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[16 * 1024];
            using var message = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) throw new WebSocketException("closed");
                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage) return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            }
        }

        public void Dispose()
        {
            socket.Abort();
            socket.Dispose();
            sending.Dispose();
        }
    }
}

/// <summary>
/// Tells a computer gone quiet from one whose app froze. Pongs stop: no answer (the network or
/// the computer). Pongs still come but the beat in them stops moving: the app's main thread is
/// stuck (frozen), which a backup should take over from too. Port of HeartbeatMonitor.
/// </summary>
public struct HeartbeatMonitor
{
    public sealed record Limits(double LostAfter = 3, double FrozenAfter = 2);

    public enum Reading { Live, NoAnswer, Frozen }

    private readonly Limits limits;
    private double lastPongAt;
    private ulong? beat;
    private double beatMovedAt;

    public HeartbeatMonitor(Limits? limits, double now)
    {
        this.limits = limits ?? new Limits();
        lastPongAt = now;
        beatMovedAt = now;
        beat = null;
    }

    public void Pong(ulong? newBeat, double now)
    {
        lastPongAt = now;
        if (newBeat == beat) return;
        beat = newBeat;
        beatMovedAt = now;
    }

    public readonly Reading ReadingAt(double now)
    {
        if (now - lastPongAt > limits.LostAfter) return Reading.NoAnswer;
        if (beat is not null && lastPongAt - beatMovedAt > limits.FrozenAfter) return Reading.Frozen;
        return Reading.Live;
    }
}
