using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MxU.Slides.Broadcast.Rtmp;

namespace MxU.Slides.Broadcast.Tests;

/// <summary>
/// An in-process RTMP server on 127.0.0.1 that answers a publisher the way nginx-rtmp or
/// YouTube does — handshake, window/bandwidth/chunk-size control messages, _result for connect
/// and createStream, onStatus NetStream.Publish.Start — and records every message it receives
/// so tests can check what a real server would have seen.
/// </summary>
internal sealed class FakeRtmpServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly X509Certificate2? certificate;
    private readonly CancellationTokenSource stopping = new();
    private readonly Task accepting;
    private readonly Lock gate = new();
    private readonly List<FakeRtmpConnection> connections = [];
    private readonly SemaphoreSlim arrived = new(0);

    public FakeRtmpServer(X509Certificate2? certificate = null, int? receiveBufferSize = null)
    {
        this.certificate = certificate;
        // A small, fixed receive buffer (accepted sockets inherit it) means a server that stops
        // reading is felt by the publisher within a few kilobytes, as over a congested uplink.
        if (receiveBufferSize is { } size) listener.Server.ReceiveBufferSize = size;
        listener.Start();
        accepting = Task.Run(AcceptAsync);
    }

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    public string Url(string app = "live") => $"{(certificate is null ? "rtmp" : "rtmps")}://127.0.0.1:{Port}/{app}";

    /// <summary>Answer publish with NetStream.Publish.BadName (as a server does for a wrong key).</summary>
    public bool RejectPublish { get; set; }

    /// <summary>Drop the connection after this many video messages.</summary>
    public int? DropAfterVideoMessages { get; set; }

    /// <summary>The acknowledgement window the server asks for (the client must ack each time it has read this much).</summary>
    public uint Window { get; set; } = 5_000_000;

    /// <summary>The server's own chunk size (sent before the connect result).</summary>
    public int ServerChunkSize { get; set; } = 4096;

    /// <summary>While true, the server stops reading: the publisher's upload stalls.</summary>
    public bool Paused { get; set; }

    /// <summary>Reads at most this many bytes a second (a slow uplink); null reads as fast as it comes.</summary>
    public int? ReadLimitBytesPerSecond { get; set; }

    public IReadOnlyList<FakeRtmpConnection> Connections
    {
        get { lock (gate) return [.. connections]; }
    }

    public async Task<FakeRtmpConnection> NextConnectionAsync(int index, CancellationToken cancellation)
    {
        while (true)
        {
            lock (gate)
                if (connections.Count > index) return connections[index];
            await arrived.WaitAsync(TimeSpan.FromMilliseconds(100), cancellation);
        }
    }

    private async Task AcceptAsync()
    {
        while (!stopping.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stopping.Token); }
            catch { return; }
            var connection = new FakeRtmpConnection(this, client);
            lock (gate) connections.Add(connection);
            arrived.Release();
            _ = Task.Run(() => connection.RunAsync(certificate, stopping.Token));
        }
    }

    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        listener.Stop();
        foreach (var connection in Connections) connection.Close();
        try { await accepting; } catch { }
    }

    /// <summary>A self-signed certificate for 127.0.0.1, re-imported so Windows' TLS can use its private key.</summary>
    public static X509Certificate2 SelfSignedCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var created = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
    }
}

/// <summary>One publisher's connection to the fake server, and everything it sent.</summary>
internal sealed class FakeRtmpConnection(FakeRtmpServer server, TcpClient client)
{
    private readonly Lock gate = new();
    private readonly List<RtmpMessage> messages = [];
    private readonly List<List<object?>> commands = [];

    public byte[]? C1 { get; private set; }

    public bool HandshakeVerified { get; private set; }

    public int ClientChunkSize { get; private set; } = 128;

    public IReadOnlyList<RtmpMessage> Messages
    {
        get { lock (gate) return [.. messages]; }
    }

    /// <summary>Every AMF0 command (and data message) decoded, in arrival order.</summary>
    public IReadOnlyList<List<object?>> Commands
    {
        get { lock (gate) return [.. commands]; }
    }

    public IReadOnlyList<RtmpMessage> Video => Messages.Where(m => m.TypeId == RtmpMessageType.Video).ToList();

    public IReadOnlyList<RtmpMessage> Audio => Messages.Where(m => m.TypeId == RtmpMessageType.Audio).ToList();

    public bool Closed { get; private set; }

    public void Close()
    {
        Closed = true;
        try { client.Close(); } catch { }
    }

    public async Task WaitUntilAsync(Func<FakeRtmpConnection, bool> condition, TimeSpan timeout, CancellationToken cancellation)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition(this))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("the fake RTMP server never saw what the test waited for");
            await Task.Delay(20, cancellation);
        }
    }

    public async Task RunAsync(X509Certificate2? certificate, CancellationToken cancellation)
    {
        try
        {
            Stream stream = client.GetStream();
            if (certificate is not null)
            {
                var tls = new SslStream(stream, leaveInnerStreamOpen: false);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, cancellation);
                stream = tls;
            }
            C1 = await RtmpHandshake.ServerAsync(stream, cancellation);
            HandshakeVerified = true;
            var reader = new RtmpChunkReader(new ThrottledReads(stream, server));
            var writer = new RtmpChunkWriter();
            async Task Send(int chunkStream, RtmpMessage message)
            {
                writer.Write(stream, chunkStream, message);
                await stream.FlushAsync(cancellation);
            }

            var videoCount = 0;
            while (!cancellation.IsCancellationRequested)
            {
                var message = await reader.ReadMessageAsync(cancellation);
                lock (gate) messages.Add(message);
                switch (message.TypeId)
                {
                    case RtmpMessageType.SetChunkSize:
                        reader.ChunkSize = (int)message.ReadUInt32();
                        ClientChunkSize = reader.ChunkSize;
                        break;
                    case RtmpMessageType.Video:
                        videoCount++;
                        if (server.DropAfterVideoMessages is { } limit && videoCount >= limit)
                        {
                            Close();
                            return;
                        }
                        break;
                    case RtmpMessageType.DataAmf0:
                        lock (gate) commands.Add(Amf0.DecodeAll(message.Payload));
                        break;
                    case RtmpMessageType.CommandAmf0:
                        var values = Amf0.DecodeAll(message.Payload);
                        lock (gate) commands.Add(values);
                        await AnswerAsync(values, Send, size => writer.ChunkSize = size);
                        break;
                }
            }
        }
        catch
        {
            Close();
        }
    }

    private async Task AnswerAsync(List<object?> values, Func<int, RtmpMessage, Task> send, Action<int> setChunkSize)
    {
        var name = values[0] as string;
        var transaction = values.Count > 1 && values[1] is double t ? t : 0;
        switch (name)
        {
            case "connect":
                await send(2, RtmpMessage.WindowAcknowledgementSize(server.Window));
                await send(2, RtmpMessage.SetPeerBandwidth(server.Window, 2));
                await send(2, RtmpMessage.SetChunkSize(server.ServerChunkSize));
                setChunkSize(server.ServerChunkSize);
                await send(2, RtmpMessage.UserControl(6, 0x1234_5678));
                await send(3, RtmpMessage.Command(0, "_result", transaction,
                    new Amf0Object { ["fmsVer"] = "FMS/3,0,1,123", ["capabilities"] = 31 },
                    new Amf0Object { ["level"] = "status", ["code"] = "NetConnection.Connect.Success", ["description"] = "Connection succeeded.", ["objectEncoding"] = 0 }));
                // A long padded message, written with the server's large chunk size.
                await send(3, RtmpMessage.Command(0, "onBWDone", 0, null, new string('x', 9000)));
                break;
            case "releaseStream" or "FCPublish":
                await send(3, RtmpMessage.Command(0, "_result", transaction, null, Amf0.Undefined));
                break;
            case "createStream":
                await send(3, RtmpMessage.Command(0, "_result", transaction, null, 1.0));
                break;
            case "publish":
                await send(2, RtmpMessage.UserControl(0, 1));
                await send(5, RtmpMessage.Command(1, "onStatus", 0, null, server.RejectPublish
                    ? new Amf0Object { ["level"] = "error", ["code"] = "NetStream.Publish.BadName", ["description"] = "Stream key is invalid" }
                    : new Amf0Object { ["level"] = "status", ["code"] = "NetStream.Publish.Start", ["description"] = "Publishing live." }));
                break;
        }
    }
}

/// <summary>
/// The server's side of a slow or stalled network: reads wait while the server is paused, and
/// take at most a slice of its read limit every 50 ms. Writes go straight through.
/// </summary>
internal sealed class ThrottledReads(Stream inner, FakeRtmpServer server) : Stream
{
    private const int SliceMilliseconds = 50;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (server.Paused) await Task.Delay(SliceMilliseconds, cancellationToken);
        if (server.ReadLimitBytesPerSecond is not { } limit) return await inner.ReadAsync(buffer, cancellationToken);
        await Task.Delay(SliceMilliseconds, cancellationToken);
        var slice = Math.Max(1, limit * SliceMilliseconds / 1000);
        return await inner.ReadAsync(buffer[..Math.Min(buffer.Length, slice)], cancellationToken);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        inner.WriteAsync(buffer, cancellationToken);

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
