using System.Net;
using System.Net.Sockets;

namespace MxU.Slides.Broadcast.Rtmp;

/// <summary>
/// A tiny RTMP server on 127.0.0.1 that takes a stream and throws it away, for checking the
/// app's streaming without the internet (the self-test streams to it). It answers a publisher
/// the way nginx-rtmp does (handshake, connect, createStream, publish), counts the pictures it
/// receives, and can play a bad network: <see cref="ReadLimitBytesPerSecond"/> is a slow
/// upload, <see cref="Paused"/> a stalled one (it stops reading, so the publisher's bytes stop
/// leaving). Windows-only: the Mac's tests use a real server on port 1935.
/// </summary>
public sealed class LoopbackRtmpServer : IAsyncDisposable
{
    private const int SliceMilliseconds = 50;

    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stopping = new();
    private readonly Task accepting;
    private readonly List<TcpClient> clients = [];
    private readonly Lock gate = new();
    private int connections;
    private int videoMessages;

    /// <param name="receiveBufferSize">A small, fixed receive buffer, so a stall is felt within a few kilobytes.</param>
    public LoopbackRtmpServer(int receiveBufferSize = 8192)
    {
        listener.Server.ReceiveBufferSize = receiveBufferSize;
        listener.Start();
        accepting = Task.Run(AcceptAsync);
    }

    /// <summary>The server URL for a stream destination (any stream key will do).</summary>
    public string Url => $"rtmp://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/live";

    /// <summary>While true the server stops reading: the publisher's upload stalls.</summary>
    public bool Paused { get; set; }

    /// <summary>Reads at most this many bytes a second (a slow upload); null reads as fast as it comes.</summary>
    public int? ReadLimitBytesPerSecond { get; set; }

    /// <summary>Publishers that have connected so far (a reconnect is a new one).</summary>
    public int Connections => Volatile.Read(ref connections);

    /// <summary>Pictures received, over every connection.</summary>
    public int VideoMessages => Volatile.Read(ref videoMessages);

    private async Task AcceptAsync()
    {
        while (!stopping.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stopping.Token); }
            catch { return; }
            lock (gate) clients.Add(client);
            Interlocked.Increment(ref connections);
            _ = Task.Run(() => ServeAsync(client, stopping.Token));
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken cancellation)
    {
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                await RtmpHandshake.ServerAsync(stream, cancellation);
                var reader = new RtmpChunkReader(new SlowReads(stream, this));
                var writer = new RtmpChunkWriter();
                async Task Send(int chunkStream, RtmpMessage message)
                {
                    writer.Write(stream, chunkStream, message);
                    await stream.FlushAsync(cancellation);
                }
                while (!cancellation.IsCancellationRequested)
                {
                    var message = await reader.ReadMessageAsync(cancellation);
                    switch (message.TypeId)
                    {
                        case RtmpMessageType.SetChunkSize:
                            reader.ChunkSize = (int)Math.Clamp(message.ReadUInt32() & 0x7FFFFFFF, 1, 16 * 1024 * 1024);
                            break;
                        case RtmpMessageType.Video:
                            Interlocked.Increment(ref videoMessages);
                            break;
                        case RtmpMessageType.CommandAmf0:
                            await AnswerAsync(Amf0.DecodeAll(message.Payload), Send);
                            break;
                    }
                }
            }
        }
        catch
        {
            // The publisher went away, or the server is stopping.
        }
    }

    private static async Task AnswerAsync(List<object?> values, Func<int, RtmpMessage, Task> send)
    {
        if (values.Count == 0 || values[0] is not string name) return;
        var transaction = values.Count > 1 && values[1] is double t ? t : 0;
        switch (name)
        {
            case "connect":
                await send(2, RtmpMessage.WindowAcknowledgementSize(5_000_000));
                await send(2, RtmpMessage.SetPeerBandwidth(5_000_000, 2));
                await send(3, RtmpMessage.Command(0, "_result", transaction,
                    new Amf0Object { ["fmsVer"] = "FMS/3,0,1,123", ["capabilities"] = 31 },
                    new Amf0Object { ["level"] = "status", ["code"] = "NetConnection.Connect.Success", ["description"] = "Connection succeeded." }));
                break;
            case "releaseStream" or "FCPublish":
                await send(3, RtmpMessage.Command(0, "_result", transaction, null, Amf0.Undefined));
                break;
            case "createStream":
                await send(3, RtmpMessage.Command(0, "_result", transaction, null, 1.0));
                break;
            case "publish":
                await send(5, RtmpMessage.Command(1, "onStatus", 0, null,
                    new Amf0Object { ["level"] = "status", ["code"] = "NetStream.Publish.Start", ["description"] = "Publishing live." }));
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        listener.Stop();
        lock (gate)
            foreach (var client in clients)
                try { client.Close(); } catch { }
        try { await accepting; } catch { }
        stopping.Dispose();
    }

    /// <summary>The server's side of a slow or stalled network: reads wait while paused, and take a slice of the limit every 50 ms.</summary>
    private sealed class SlowReads(Stream inner, LoopbackRtmpServer server) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
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

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
