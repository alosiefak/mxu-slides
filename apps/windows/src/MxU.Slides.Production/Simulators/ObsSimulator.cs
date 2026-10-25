using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace MxU.Slides.Production.Simulators;

/// <summary>
/// A pretend OBS on 127.0.0.1: an obs-websocket v5 server. It says Hello (with a challenge
/// when it has a password), checks the Identify's answer (closing with 4009 when it's wrong,
/// as OBS does), then answers every request, recording it.
/// <para>
/// It runs on threads of its own with blocking sockets, like the app's devices, so a busy
/// thread pool in the app can't make it slow (the self-test runs it inside the app). Its
/// WebSocket framing is the server's half, written separately from the app's client
/// (MiniWebSocket): it insists that what the client sends is masked, as RFC 6455 requires. The
/// unit tests also check the client against .NET's own WebSocket server.
/// </para>
/// For the unit tests and the app's self-test only.
/// </summary>
public sealed class ObsSimulator : IDisposable
{
    public const string Salt = "lM1GncleQOaCu9lT1yeUZhFYnqhsLLP1G5lAGo3ixaI=";
    public const string Challenge = "+IxH4CnCiqpX1rM9scsNynZzbOe4KhDeYcTNS3PDaeY=";

    private readonly TcpListener listener;
    private readonly string? password;
    private readonly ConcurrentDictionary<TcpClient, byte> connections = new();
    private volatile bool stopped;

    /// <param name="password">OBS's server password, or null for none.</param>
    /// <param name="port">The port to listen on (0: any free one).</param>
    public ObsSimulator(string? password = null, int port = 0)
    {
        this.password = password;
        listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        new Thread(AcceptLoop) { IsBackground = true, Name = "OBS simulator" }.Start();
    }

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    public List<string> Scenes { get; } = ["Announcements", "Sermon", "Worship"];

    /// <summary>Requests that should fail (OBS answers result false).</summary>
    public HashSet<string> Failing { get; } = [];

    /// <summary>Every request, in order: (type, data).</summary>
    public ConcurrentQueue<(string Type, JsonObject Data)> Requests { get; } = new();

    public int Identified;
    public int RefusedPasswords;

    /// <summary>
    /// OBS has frozen: it still signs clients in (obs-websocket's own thread does that) but
    /// takes requests without ever answering them, as when OBS's main thread is stuck.
    /// </summary>
    public volatile bool Hang;

    private void AcceptLoop()
    {
        while (!stopped)
        {
            TcpClient client;
            try { client = listener.AcceptTcpClient(); }
            catch (Exception) { return; }
            connections[client] = 0;
            new Thread(() => Serve(client)) { IsBackground = true, Name = "OBS simulator connection" }.Start();
        }
    }

    private void Serve(TcpClient client)
    {
        using var connection = client;
        try
        {
            var stream = client.GetStream();
            // The HTTP upgrade request: read to the blank line, answer 101 with the key's accept.
            var request = new StringBuilder();
            while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                var next = stream.ReadByte();
                if (next < 0) return;
                request.Append((char)next);
            }
            var key = request.ToString().Split("\r\n").First(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))[18..].Trim();
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            stream.Write(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"));

            var hello = new JsonObject { ["obsWebSocketVersion"] = "5.5.0", ["rpcVersion"] = 1 };
            if (password is not null) hello["authentication"] = new JsonObject { ["challenge"] = Challenge, ["salt"] = Salt };
            Send(stream, 0, hello);

            var identify = Receive(stream);
            if (identify?["op"]?.GetValue<int>() != 1) return;
            if (password is not null && identify["d"]?["authentication"]?.GetValue<string>() != Answer(password))
            {
                Interlocked.Increment(ref RefusedPasswords);
                // A close frame: code 4009, "Authentication failed."
                var reason = Encoding.UTF8.GetBytes("Authentication failed.");
                var payload = new byte[2 + reason.Length];
                BinaryPrimitives.WriteUInt16BigEndian(payload, 4009);
                reason.CopyTo(payload, 2);
                Frame(stream, 0x8, payload);
                return;
            }
            Send(stream, 2, new JsonObject { ["negotiatedRpcVersion"] = 1 });
            Interlocked.Increment(ref Identified);

            while (!stopped && Receive(stream) is { } message)
            {
                if (message["op"]?.GetValue<int>() != 6 || message["d"] is not JsonObject d) continue;
                var type = d["requestType"]!.GetValue<string>();
                var data = d["requestData"] as JsonObject ?? [];
                Requests.Enqueue((type, (JsonObject)data.DeepClone()));
                if (Hang) continue;
                var ok = !Failing.Contains(type);
                Send(stream, 7, new JsonObject
                {
                    ["requestType"] = type,
                    ["requestId"] = d["requestId"]!.GetValue<string>(),
                    ["requestStatus"] = ok
                        ? new JsonObject { ["result"] = true, ["code"] = 100 }
                        : new JsonObject { ["result"] = false, ["code"] = 600, ["comment"] = "Not in this test" },
                    ["responseData"] = ok ? Reply(type) : null,
                });
                if (ok && type == "SetCurrentProgramScene")
                    Send(stream, 5, new JsonObject
                    {
                        ["eventType"] = "CurrentProgramSceneChanged", ["eventIntent"] = 4,
                        ["eventData"] = new JsonObject { ["sceneName"] = data["sceneName"]!.GetValue<string>() },
                    });
            }
        }
        catch (IOException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
        finally
        {
            connections.TryRemove(connection, out _);
        }
    }

    private JsonObject? Reply(string type) => type switch
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

    private static void Send(NetworkStream stream, int op, JsonObject d) =>
        Frame(stream, 0x1, Encoding.UTF8.GetBytes(new JsonObject { ["op"] = op, ["d"] = d }.ToJsonString()));

    /// <summary>One frame from the server: FIN and the opcode, the length, the payload (servers don't mask).</summary>
    private static void Frame(NetworkStream stream, byte opcode, byte[] payload)
    {
        using var frame = new MemoryStream();
        frame.WriteByte((byte)(0x80 | opcode));
        if (payload.Length < 126)
        {
            frame.WriteByte((byte)payload.Length);
        }
        else if (payload.Length <= ushort.MaxValue)
        {
            frame.WriteByte(126);
            frame.WriteByte((byte)(payload.Length >> 8));
            frame.WriteByte((byte)payload.Length);
        }
        else
        {
            frame.WriteByte(127);
            var length = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(length, (ulong)payload.Length);
            frame.Write(length);
        }
        frame.Write(payload);
        stream.Write(frame.ToArray());
    }

    /// <summary>The next text message from the client (null when it closes). Fragments are joined; client frames must be masked.</summary>
    private static JsonObject? Receive(NetworkStream stream)
    {
        using var message = new MemoryStream();
        while (true)
        {
            var head = new byte[2];
            stream.ReadExactly(head);
            var opcode = head[0] & 0x0F;
            if ((head[1] & 0x80) == 0) throw new IOException("A client frame must be masked");
            long length = head[1] & 0x7F;
            if (length == 126)
            {
                var more = new byte[2];
                stream.ReadExactly(more);
                length = BinaryPrimitives.ReadUInt16BigEndian(more);
            }
            else if (length == 127)
            {
                var more = new byte[8];
                stream.ReadExactly(more);
                length = (long)BinaryPrimitives.ReadUInt64BigEndian(more);
            }
            var mask = new byte[4];
            stream.ReadExactly(mask);
            var payload = new byte[length];
            stream.ReadExactly(payload);
            for (var i = 0; i < payload.Length; i++) payload[i] ^= mask[i % 4];
            if (opcode == 0x8) return null;
            if (opcode is 0x9 or 0xA) continue;
            message.Write(payload);
            if ((head[0] & 0x80) != 0) return JsonNode.Parse(message.ToArray()) as JsonObject;
        }
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

    /// <summary>OBS quits: it stops listening and drops everyone connected.</summary>
    public void Dispose()
    {
        stopped = true;
        listener.Stop();
        foreach (var client in connections.Keys) client.Dispose();
    }
}
