using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace MxU.Slides.LocalApi;

/// <summary>
/// One /v1/ws connection: hello, subscribe/unsubscribe to topics, ping, and command frames
/// that run any REST operation. The token is rechecked before every frame in or out, so a
/// revoked key's socket closes without another byte. Port of APIWebSocketHandler.
/// </summary>
internal sealed class ApiWebSocketSession(
    ApiRouter router,
    ApiEventBus events,
    ApiToken token,
    ApiTokenStore tokens,
    Func<ApiRoute, ApiRequestContext, Task<ApiHandlerResponse>> invoke)
{
    private const int MaxMessageBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(1);

    private readonly TopicSet subscriptions = new();

    private ApiScope Scope => token.Scope;

    private bool IsAuthorized => tokens.ActiveToken(token.Id)?.Scope == token.Scope;

    private sealed record Inbound(
        [property: JsonRequired] string Type,
        IReadOnlyList<string>? Topics = null,
        string? Id = null,
        string? Method = null,
        string? Path = null,
        JsonElement? Body = null);

    public async Task RunAsync(WebSocket socket, CancellationToken stopping)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var output = Channel.CreateBounded<string>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        Task pump = Task.CompletedTask, input = Task.CompletedTask;
        try
        {
            if (IsAuthorized)
            {
                var eventStream = events.Subscribe();
                Send(output.Writer, new JsonObject { ["type"] = "hello", ["scope"] = Scope.Raw() });
                pump = PumpAsync(eventStream, output.Writer, lifetime.Token);
                input = ReadAsync(socket, output.Writer, lifetime.Token);
                await foreach (var frame in output.Reader.ReadAllAsync(lifetime.Token))
                    await socket.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, lifetime.Token);
            }
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, lifetime.Token);
            await input.WaitAsync(CloseGrace, lifetime.Token);
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or TimeoutException or IOException)
        {
            // The client went away or the server is stopping.
        }
        finally
        {
            await lifetime.CancelAsync();
            await Task.WhenAll(pump, input);
        }
    }

    private async Task PumpAsync(ApiEventSubscription subscription, ChannelWriter<string> output, CancellationToken cancellation)
    {
        using var _ = subscription;
        try
        {
            await foreach (var e in subscription.Reader.ReadAllAsync(cancellation))
            {
                if (!IsAuthorized)
                {
                    output.TryComplete();
                    break;
                }
                if (!subscriptions.Contains(e.Topic)) continue;
                output.TryWrite(FrameText(new JsonObject { ["type"] = "event", ["topic"] = e.Topic.Raw() }, ("data", e.Payload)));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ReadAsync(WebSocket socket, ChannelWriter<string> output, CancellationToken cancellation)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(), cancellation);
                if (result.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxMessageBytes) break;
                if (!result.EndOfMessage) continue;
                var text = result.MessageType == WebSocketMessageType.Text
                    ? Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length)
                    : null;
                message.SetLength(0);
                if (!IsAuthorized) break;
                if (text is not null) await HandleAsync(text, output);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException)
        {
        }
        finally
        {
            output.TryComplete();
        }
    }

    private async Task HandleAsync(string text, ChannelWriter<string> output)
    {
        Inbound? inbound;
        try
        {
            inbound = JsonSerializer.Deserialize<Inbound>(text, ApiJson.Options);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException)
        {
            inbound = null;
        }
        switch (inbound?.Type)
        {
            case null:
                Send(output, new JsonObject { ["type"] = "error", ["message"] = "Unparseable frame." });
                break;
            case "ping":
                Send(output, new JsonObject { ["type"] = "pong" });
                break;
            case "subscribe" or "unsubscribe":
                var topics = (inbound.Topics ?? ApiTopics.All.Select(t => t.Raw()))
                    .Select(ApiTopics.Parse).OfType<ApiTopic>().ToList();
                if (inbound.Type == "subscribe") subscriptions.Add(topics);
                else subscriptions.Remove(topics);
                Send(output, new JsonObject
                {
                    ["type"] = "subscribed",
                    ["topics"] = new JsonArray([.. subscriptions.All.Select(t => (JsonNode)t.Raw())]),
                });
                break;
            case "command":
                await CommandAsync(inbound, output);
                break;
            default:
                Send(output, new JsonObject { ["type"] = "error", ["message"] = $"Unknown frame type '{inbound.Type}'." });
                break;
        }
    }

    private async Task CommandAsync(Inbound inbound, ChannelWriter<string> output)
    {
        var id = inbound.Id ?? "";
        void Reply(int status, byte[] data)
        {
            if (!IsAuthorized)
            {
                output.TryComplete();
                return;
            }
            output.TryWrite(FrameText(new JsonObject { ["type"] = "result", ["id"] = id, ["status"] = status }, ("data", data)));
        }

        if (inbound.Method is not { } method || inbound.Path is not { } path || router.Match(method, path) is not { } match)
        {
            Reply(404, Encode(ApiError.NotFound("No such operation.")));
            return;
        }
        if (!Scope.Allows(match.Route.Scope))
        {
            Reply(403, Encode(ApiError.Forbidden(match.Route.Scope)));
            return;
        }
        var context = new ApiRequestContext
        {
            PathParameters = match.Parameters,
            Body = inbound.Body is { } body ? Encoding.UTF8.GetBytes(body.GetRawText()) : [],
            Scope = Scope,
        };
        try
        {
            var response = await invoke(match.Route, context);
            Reply(response.Status, response.Body);
        }
        catch (ApiError e)
        {
            Reply(e.Status, Encode(e));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Reply(500, Encode(new ApiError(500, "internal", e.Message)));
        }
    }

    private static byte[] Encode(ApiError error) => ApiJson.Encode(error.Body);

    private static void Send(ChannelWriter<string> output, JsonObject envelope) => output.TryWrite(FrameText(envelope));

    /// <summary>
    /// The envelope with sorted keys, then the payload spliced in raw as the last key, so
    /// a snapshot is sent as encoded and client-supplied strings can't forge fields.
    /// </summary>
    internal static string FrameText(JsonObject envelope, (string Key, byte[] Json)? payload = null)
    {
        var text = Encoding.UTF8.GetString(ApiJson.Encoded(envelope));
        if (payload is not { } p) return text;
        var json = p.Json.Length == 0 ? "null" : Encoding.UTF8.GetString(p.Json);
        return text.Insert(text.Length - 1, (text == "{}" ? "" : ",") + $"\"{p.Key}\":" + json);
    }

    private sealed class TopicSet
    {
        private readonly Lock gate = new();
        private readonly HashSet<ApiTopic> topics = [];

        public List<ApiTopic> All
        {
            get { lock (gate) return ApiTopics.All.Where(topics.Contains).ToList(); }
        }

        public bool Contains(ApiTopic topic)
        {
            lock (gate) return topics.Contains(topic);
        }

        public void Add(IEnumerable<ApiTopic> added)
        {
            lock (gate) topics.UnionWith(added);
        }

        public void Remove(IEnumerable<ApiTopic> removed)
        {
            lock (gate) topics.ExceptWith(removed);
        }
    }
}
