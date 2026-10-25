using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace MxU.Slides.Production.Simulators;

/// <summary>
/// A pretend OBS on 127.0.0.1: an obs-websocket v5 server built on .NET's own WebSocket
/// (server side), so the app's hand-written client is checked against a real implementation.
/// It says Hello (with a challenge when it has a password), checks the Identify's answer
/// (closing with 4009 when it's wrong, as OBS does), then answers every request, recording it.
/// For the unit tests and the app's self-test only.
/// </summary>
public sealed class ObsSimulator : IDisposable
{
    public const string Salt = "lM1GncleQOaCu9lT1yeUZhFYnqhsLLP1G5lAGo3ixaI=";
    public const string Challenge = "+IxH4CnCiqpX1rM9scsNynZzbOe4KhDeYcTNS3PDaeY=";

    private readonly TcpListener listener;
    private readonly CancellationTokenSource stop = new();
    private readonly string? password;

    /// <param name="password">OBS's server password, or null for none.</param>
    /// <param name="port">The port to listen on (0: any free one).</param>
    public ObsSimulator(string? password = null, int port = 0)
    {
        this.password = password;
        listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        _ = AcceptLoop();
    }

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    public List<string> Scenes { get; } = ["Announcements", "Sermon", "Worship"];

    /// <summary>Requests that should fail (OBS answers result false).</summary>
    public HashSet<string> Failing { get; } = [];

    /// <summary>Every request, in order: (type, data).</summary>
    public ConcurrentQueue<(string Type, JsonObject Data)> Requests { get; } = new();

    public int Identified;
    public int RefusedPasswords;

    private async Task AcceptLoop()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stop.Token); }
            catch (Exception) { return; }
            _ = Serve(client);
        }
    }

    private async Task Serve(TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        try
        {
            // The HTTP upgrade request: read to the blank line, answer 101 with the key's accept.
            var request = new StringBuilder();
            var one = new byte[1];
            while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (await stream.ReadAsync(one, stop.Token) == 0) return;
                request.Append((char)one[0]);
            }
            var key = request.ToString().Split("\r\n").First(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))[18..].Trim();
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), stop.Token);
            using var ws = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.Zero });

            var hello = new JsonObject { ["obsWebSocketVersion"] = "5.5.0", ["rpcVersion"] = 1 };
            if (password is not null) hello["authentication"] = new JsonObject { ["challenge"] = Challenge, ["salt"] = Salt };
            await Send(ws, 0, hello);

            var identify = await Receive(ws);
            if (identify?["op"]?.GetValue<int>() != 1) return;
            if (password is not null && identify["d"]?["authentication"]?.GetValue<string>() != Answer(password))
            {
                Interlocked.Increment(ref RefusedPasswords);
                await ws.CloseAsync((WebSocketCloseStatus)4009, "Authentication failed.", stop.Token);
                return;
            }
            await Send(ws, 2, new JsonObject { ["negotiatedRpcVersion"] = 1 });
            Interlocked.Increment(ref Identified);

            while (ws.State == WebSocketState.Open && await Receive(ws) is { } message)
            {
                if (message["op"]?.GetValue<int>() != 6 || message["d"] is not JsonObject d) continue;
                var type = d["requestType"]!.GetValue<string>();
                var data = d["requestData"] as JsonObject ?? [];
                Requests.Enqueue((type, (JsonObject)data.DeepClone()));
                var ok = !Failing.Contains(type);
                await Send(ws, 7, new JsonObject
                {
                    ["requestType"] = type,
                    ["requestId"] = d["requestId"]!.GetValue<string>(),
                    ["requestStatus"] = ok
                        ? new JsonObject { ["result"] = true, ["code"] = 100 }
                        : new JsonObject { ["result"] = false, ["code"] = 600, ["comment"] = "Not in this test" },
                    ["responseData"] = ok ? Reply(type, data) : null,
                });
                if (ok && type == "SetCurrentProgramScene")
                    await Send(ws, 5, new JsonObject
                    {
                        ["eventType"] = "CurrentProgramSceneChanged", ["eventIntent"] = 4,
                        ["eventData"] = new JsonObject { ["sceneName"] = data["sceneName"]!.GetValue<string>() },
                    });
            }
        }
        catch (Exception) when (stop.IsCancellationRequested) { }
        catch (WebSocketException) { }
        catch (IOException) { }
    }

    private JsonObject? Reply(string type, JsonObject data) => type switch
    {
        "GetSceneList" => new JsonObject
        {
            ["currentProgramSceneName"] = "Worship",
            ["currentPreviewSceneName"] = "Sermon",
            // OBS lists them bottom-up: index 0 is the bottom of its list.
            ["scenes"] = new JsonArray([.. Scenes.Select((s, i) => (JsonNode)new JsonObject { ["sceneIndex"] = Scenes.Count - 1 - i, ["sceneName"] = s })]),
        },
        "GetSceneTransitionList" => new JsonObject
        {
            ["transitions"] = new JsonArray(new JsonObject { ["transitionName"] = "Fade" }, new JsonObject { ["transitionName"] = "Cut" }),
        },
        "GetSceneItemList" => new JsonObject { ["sceneItems"] = new JsonArray(new JsonObject { ["sourceName"] = "Lower third" }) },
        "GetSceneItemId" => new JsonObject { ["sceneItemId"] = 7 },
        "GetStudioModeEnabled" => new JsonObject { ["studioModeEnabled"] = true },
        "GetStreamStatus" or "GetRecordStatus" => new JsonObject { ["outputActive"] = false },
        _ => null,
    };

    /// <summary>The expected answer, worked out here independently of the app's ObsAuth.</summary>
    private static string Answer(string password)
    {
        using var sha = SHA256.Create();
        var secret = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(password + Salt)));
        return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(secret + Challenge)));
    }

    private async Task Send(WebSocket ws, int op, JsonObject d) =>
        await ws.SendAsync(Encoding.UTF8.GetBytes(new JsonObject { ["op"] = op, ["d"] = d }.ToJsonString()), WebSocketMessageType.Text, true, stop.Token);

    private async Task<JsonObject?> Receive(WebSocket ws)
    {
        var buffer = new byte[65536];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, stop.Token);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }
        return JsonNode.Parse(message.ToArray()) as JsonObject;
    }

    /// <summary>Waits for a request of a type (or the time is up).</summary>
    public (string Type, JsonObject Data)? WaitFor(string type, int milliseconds = 3000)
    {
        var until = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < until)
        {
            var found = Requests.FirstOrDefault(r => r.Type == type);
            if (found.Type is not null) return found;
            Thread.Sleep(10);
        }
        return null;
    }

    public void Dispose()
    {
        stop.Cancel();
        listener.Stop();
    }
}
