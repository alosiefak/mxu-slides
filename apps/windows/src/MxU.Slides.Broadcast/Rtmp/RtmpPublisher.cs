using System.Net.Security;
using System.Net.Sockets;
using System.Threading.Channels;
using MxU.Slides.Broadcast.Hls.Vendored;
using MxU.Slides.Broadcast.Media;
using MxU.Slides.Broadcast.Streaming;

namespace MxU.Slides.Broadcast.Rtmp;

/// <summary>An onStatus event from the server, e.g. code "NetStream.Publish.Start", level "status".</summary>
internal sealed record RtmpStatus(string Code, string Level, string Description);

internal sealed class RtmpPublishOptions
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>A write that makes no progress this long means the network is gone.</summary>
    public TimeSpan WriteStallTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>The chunk size we send with once connected (HaishinKit's default, 8 KB).</summary>
    public int ChunkSize { get; init; } = 8 * 1024;

    /// <summary>
    /// How much may wait to be sent before video is dropped (until the next keyframe) rather
    /// than queued: about three seconds of the stream, so a slow uplink loses frames instead of
    /// building up delay and memory.
    /// </summary>
    public long MaxQueuedBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>Tests trust their own self-signed certificate; null means normal Windows validation.</summary>
    public RemoteCertificateValidationCallback? CertificateValidation { get; init; }
}

/// <summary>
/// Publishes one live stream to an RTMP or RTMPS server. <see cref="ConnectAsync"/> opens the
/// TCP (and TLS) connection, does the handshake, and sends the same command sequence as
/// HaishinKit and OBS: connect, releaseStream, FCPublish, createStream, publish ("live"); it
/// returns once the server says NetStream.Publish.Start. Then <see cref="SendVideo"/>,
/// <see cref="SendAudio"/> and <see cref="SendMetadata"/> queue FLV-tag messages, sending the
/// AVC and AAC sequence headers before the first picture and frame. A writer task sends the
/// queue; a reader task answers pings, acknowledges what the server sent once it reaches the
/// server's window, applies its chunk size, and watches for status codes that end the
/// connection. <see cref="Completion"/> fails (with a transport-closed
/// <see cref="StreamSessionException"/>) when the connection is lost. Send methods are
/// thread-safe. Port of the parts of HaishinKit's RTMPConnection and RTMPStream the Mac uses.
/// </summary>
internal sealed class RtmpPublisher : IAsyncDisposable
{
    private readonly record struct Outgoing(int ChunkStreamId, RtmpMessage Message, int? ChunkSizeAfter = null);

    private readonly RtmpUrl url;
    private readonly string streamKey;
    private readonly RtmpPublishOptions options;
    private readonly Socket socket;
    private Stream stream = Stream.Null;
    private readonly RtmpChunkWriter chunkWriter = new();
    private RtmpChunkReader? chunkReader;
    private readonly Channel<Outgoing> outgoing = Channel.CreateUnbounded<Outgoing>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource stopping = new();
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock gate = new();
    private readonly Dictionary<int, TaskCompletionSource<List<object?>>> transactions = [];
    private TaskCompletionSource<RtmpStatus>? publishStatus;
    private int nextTransaction = 1;
    private long bytesQueued;
    private long bytesSent;
    private uint serverWindow = 2_500_000;
    private long acknowledged;
    private uint lastWindowSent;
    private Task? writer;
    private Task? reader;
    private bool waitingForKeyframe;
    private AvcParameterSets? sentParameterSets;
    private AudioSpecificConfig? sentAudioConfig;
    private int disposed;

    private RtmpPublisher(RtmpUrl url, string streamKey, RtmpPublishOptions options)
    {
        this.url = url;
        this.streamKey = streamKey;
        this.options = options;
        socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true, SendBufferSize = 512 * 1024 };
    }

    /// <summary>The message stream id the server gave our stream (from createStream).</summary>
    public uint StreamId { get; private set; }

    /// <summary>Fails when the connection ends for any reason other than <see cref="CloseAsync"/>.</summary>
    public Task Completion => completion.Task;

    public long BytesSent => Interlocked.Read(ref bytesSent);

    public long BytesQueued => Interlocked.Read(ref bytesQueued);

    public RtmpStatus? LastStatus { get; private set; }

    public static async Task<RtmpPublisher> ConnectAsync(RtmpUrl url, string streamKey, RtmpPublishOptions? options, CancellationToken cancellation)
    {
        var publisher = new RtmpPublisher(url, streamKey, options ?? new RtmpPublishOptions());
        try
        {
            await publisher.OpenAsync(cancellation);
            return publisher;
        }
        catch
        {
            await publisher.DisposeAsync();
            throw;
        }
    }

    private async Task OpenAsync(CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation, stopping.Token);
        timeout.CancelAfter(options.ConnectTimeout);
        try
        {
            await socket.ConnectAsync(url.Host, url.Port, timeout.Token);
            Stream network = new NetworkStream(socket, ownsSocket: false);
            if (url.Secure)
            {
                var tls = new SslStream(network, leaveInnerStreamOpen: false);
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = url.Host,
                    RemoteCertificateValidationCallback = options.CertificateValidation,
                }, timeout.Token);
                network = tls;
            }
            stream = network;
            await RtmpHandshake.ClientAsync(stream, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw StreamSessionException.TransportClosed($"couldn't reach {url.Host}:{url.Port} (timed out)");
        }
        catch (Exception e) when (e is SocketException or IOException or System.Security.Authentication.AuthenticationException or InvalidDataException)
        {
            throw StreamSessionException.TransportClosed($"couldn't connect to {url.Host}:{url.Port}: {e.Message}", e);
        }

        chunkReader = new RtmpChunkReader(stream);
        writer = Task.Run(WriteLoopAsync, CancellationToken.None);
        reader = Task.Run(ReadLoopAsync, CancellationToken.None);

        var connect = new Amf0Object
        {
            ["app"] = url.App,
            ["flashVer"] = "FMLE/3.0 (compatible; FMSc/1.0)",
            ["swfUrl"] = null,
            ["tcUrl"] = url.TcUrl,
            ["fpad"] = false,
            ["capabilities"] = 239,
            ["audioCodecs"] = 0x0400,
            ["videoCodecs"] = 0x0080,
            ["videoFunction"] = 1,
            ["pageUrl"] = null,
            ["objectEncoding"] = 0,
        };
        var connected = await CallAsync("connect", connect, [], cancellation);
        var connectInfo = connected.Count > 3 ? connected[3] as Amf0Object : null;
        BroadcastLog.Info("rtmp", $"connected to {url.Host}: {connectInfo?.GetValueOrDefault("code") ?? "ok"}");

        // Once connected, send in big chunks (HaishinKit does this on the connect result).
        Enqueue(RtmpChunkStreamId.Control, RtmpMessage.SetChunkSize(options.ChunkSize), chunkSizeAfter: options.ChunkSize);

        // The FMLE sequence: release any stale publisher of this name, announce it, make a stream.
        _ = CallAsync("releaseStream", null, [streamKey], CancellationToken.None).ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        _ = CallAsync("FCPublish", null, [streamKey], CancellationToken.None).ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        var created = await CallAsync("createStream", null, [], cancellation);
        StreamId = created.Count > 3 && created[3] is double id ? (uint)id : 1;

        var published = new TaskCompletionSource<RtmpStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate) publishStatus = published;
        Enqueue(RtmpChunkStreamId.Command, RtmpMessage.Command(StreamId, "publish", 0, null, streamKey, "live"));
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellation, stopping.Token);
        wait.CancelAfter(options.RequestTimeout);
        RtmpStatus status;
        try
        {
            status = await published.Task.WaitAsync(wait.Token);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw Completion.IsFaulted
                ? Unwrap(Completion.Exception)
                : StreamSessionException.TransportClosed("server didn't answer publish");
        }
        BroadcastLog.Info("rtmp", $"publish: {status.Code}");
    }

    private static Exception Unwrap(AggregateException? e) => e?.InnerException ?? (Exception?)e ?? new InvalidOperationException();

    /// <summary>Sends a NetConnection command and waits for its _result; an _error throws.</summary>
    private async Task<List<object?>> CallAsync(string name, object? commandObject, object?[] arguments, CancellationToken cancellation)
    {
        var source = new TaskCompletionSource<List<object?>>(TaskCreationOptions.RunContinuationsAsynchronously);
        int transaction;
        lock (gate)
        {
            transaction = nextTransaction++;
            transactions[transaction] = source;
        }
        Enqueue(RtmpChunkStreamId.Command, RtmpMessage.Command(0, [name, transaction, commandObject, .. arguments]));
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellation, stopping.Token);
        wait.CancelAfter(options.RequestTimeout);
        try
        {
            return await source.Task.WaitAsync(wait.Token);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            if (Completion.IsFaulted) throw Unwrap(Completion.Exception);
            throw StreamSessionException.TransportClosed($"server didn't answer {name}");
        }
        finally
        {
            lock (gate) transactions.Remove(transaction);
        }
    }

    private void Enqueue(int chunkStreamId, RtmpMessage message, int? chunkSizeAfter = null)
    {
        Interlocked.Add(ref bytesQueued, message.Payload.Length);
        if (!outgoing.Writer.TryWrite(new Outgoing(chunkStreamId, message, chunkSizeAfter)))
            Interlocked.Add(ref bytesQueued, -message.Payload.Length);
    }

    /// <summary>
    /// Queues one picture at <paramref name="timestampMs"/> (milliseconds since the stream began).
    /// Returns false when it was dropped because the connection is behind; after a drop, nothing
    /// is sent until the next keyframe so the picture never breaks up.
    /// </summary>
    public bool SendVideo(EncodedVideoFrame frame, uint timestampMs)
    {
        if (Completion.IsCompleted) return false;
        var queued = BytesQueued;
        if (waitingForKeyframe)
        {
            if (!frame.IsKeyframe || queued > options.MaxQueuedBytes / 2) return false;
            waitingForKeyframe = false;
        }
        else if (queued > options.MaxQueuedBytes)
        {
            waitingForKeyframe = true;
            BroadcastLog.Warning("rtmp", $"{url.Host}: network behind by {queued / 1024} KB, dropping video until the next keyframe");
            return false;
        }
        if (!frame.ParameterSets.Equals(sentParameterSets))
        {
            sentParameterSets = frame.ParameterSets;
            Enqueue(RtmpChunkStreamId.Video, new RtmpMessage(RtmpMessageType.Video, StreamId, timestampMs, FlvTags.AvcSequenceHeader(frame.ParameterSets)));
        }
        Enqueue(RtmpChunkStreamId.Video, new RtmpMessage(RtmpMessageType.Video, StreamId, timestampMs, FlvTags.AvcPicture(frame)));
        return true;
    }

    /// <summary>Queues one AAC frame. Audio is only dropped when the connection is badly behind.</summary>
    public bool SendAudio(EncodedAudioFrame frame, uint timestampMs)
    {
        if (Completion.IsCompleted || BytesQueued > options.MaxQueuedBytes * 2) return false;
        if (frame.Config != sentAudioConfig)
        {
            sentAudioConfig = frame.Config;
            Enqueue(RtmpChunkStreamId.Audio, new RtmpMessage(RtmpMessageType.Audio, StreamId, timestampMs, FlvTags.AacSequenceHeader(frame.Config)));
        }
        Enqueue(RtmpChunkStreamId.Audio, new RtmpMessage(RtmpMessageType.Audio, StreamId, timestampMs, FlvTags.AacRaw(frame.Data)));
        return true;
    }

    public void SendMetadata(byte[] body) =>
        Enqueue(RtmpChunkStreamId.Data, new RtmpMessage(RtmpMessageType.DataAmf0, StreamId, 0, body));

    private async Task WriteLoopAsync()
    {
        var buffer = new MemoryStream(256 * 1024);
        try
        {
            var reader = outgoing.Reader;
            while (await reader.WaitToReadAsync(stopping.Token))
            {
                buffer.SetLength(0);
                long payload = 0;
                // Batch whatever is waiting (up to ~256 KB) into one network write.
                while (buffer.Length < 256 * 1024 && reader.TryRead(out var item))
                {
                    chunkWriter.Append(buffer, item.ChunkStreamId, item.Message);
                    payload += item.Message.Payload.Length;
                    if (item.ChunkSizeAfter is { } size)
                    {
                        // A new chunk size applies to the chunks after this message, so send now.
                        await FlushAsync(buffer);
                        buffer.SetLength(0);
                        chunkWriter.ChunkSize = size;
                    }
                }
                await FlushAsync(buffer);
                Interlocked.Add(ref bytesQueued, -payload);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            Fail(e is StreamSessionException ? e : StreamSessionException.TransportClosed($"send failed: {e.Message}", e));
        }
    }

    private async Task FlushAsync(MemoryStream buffer)
    {
        if (buffer.Length == 0) return;
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
        stall.CancelAfter(options.WriteStallTimeout);
        try
        {
            await stream.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), stall.Token);
            await stream.FlushAsync(stall.Token);
        }
        catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
        {
            throw StreamSessionException.TransportClosed("network stalled (nothing sent for 15 seconds)");
        }
        Interlocked.Add(ref bytesSent, buffer.Length);
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                var message = await chunkReader!.ReadMessageAsync(stopping.Token);
                Handle(message);
                if (chunkReader.BytesRead - acknowledged >= serverWindow)
                {
                    acknowledged = chunkReader.BytesRead;
                    Enqueue(RtmpChunkStreamId.Control, RtmpMessage.Acknowledgement((uint)acknowledged));
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
        catch (EndOfStreamException)
        {
            Fail(StreamSessionException.TransportClosed("server closed the connection"));
        }
        catch (Exception e)
        {
            Fail(e is StreamSessionException ? e : StreamSessionException.TransportClosed($"connection lost: {e.Message}", e));
        }
    }

    private void Handle(RtmpMessage message)
    {
        switch (message.TypeId)
        {
            case RtmpMessageType.SetChunkSize:
                chunkReader!.ChunkSize = (int)Math.Clamp(message.ReadUInt32() & 0x7FFFFFFF, 1, 16 * 1024 * 1024);
                break;
            case RtmpMessageType.WindowAcknowledgementSize:
                serverWindow = Math.Max(1, message.ReadUInt32());
                // HaishinKit answers with the same window, which some servers wait for.
                if (lastWindowSent != serverWindow)
                {
                    lastWindowSent = serverWindow;
                    Enqueue(RtmpChunkStreamId.Control, RtmpMessage.WindowAcknowledgementSize(serverWindow));
                }
                break;
            case RtmpMessageType.SetPeerBandwidth:
                var size = message.ReadUInt32();
                if (size != 0 && size != lastWindowSent)
                {
                    lastWindowSent = size;
                    Enqueue(RtmpChunkStreamId.Control, RtmpMessage.WindowAcknowledgementSize(size));
                }
                break;
            case RtmpMessageType.UserControl when message.Payload.Length >= 6:
                var eventType = (ushort)(message.Payload[0] << 8 | message.Payload[1]);
                if (eventType == 6) Enqueue(RtmpChunkStreamId.Control, RtmpMessage.UserControl(7, message.ReadUInt32(2)));
                break;
            case RtmpMessageType.CommandAmf0:
                HandleCommand(message);
                break;
            case RtmpMessageType.CommandAmf3 when message.Payload.Length > 1:
                HandleCommand(message with { Payload = message.Payload[1..] });
                break;
        }
    }

    private void HandleCommand(RtmpMessage message)
    {
        List<object?> values;
        try
        {
            values = Amf0.DecodeAll(message.Payload);
        }
        catch (Exception e) when (e is FormatException or EndOfStreamException)
        {
            BroadcastLog.Warning("rtmp", $"unreadable command from server: {e.Message}");
            return;
        }
        if (values.Count == 0 || values[0] is not string name) return;
        var transaction = values.Count > 1 && values[1] is double t ? (int)t : 0;
        switch (name)
        {
            case "_result" or "_error":
                TaskCompletionSource<List<object?>>? waiter;
                lock (gate) transactions.TryGetValue(transaction, out waiter);
                if (waiter is null) break;
                if (name == "_result") waiter.TrySetResult(values);
                else
                {
                    var info = StatusOf(values);
                    waiter.TrySetException(StreamSessionException.TransportClosed(
                        $"server refused: {info?.Code ?? "error"}{(string.IsNullOrEmpty(info?.Description) ? "" : " — " + info.Description)}"));
                }
                break;
            case "onStatus":
                if (StatusOf(values) is { } status) OnStatus(status);
                break;
            case "close":
                Fail(StreamSessionException.TransportClosed("server closed the stream"));
                break;
        }
    }

    private static RtmpStatus? StatusOf(List<object?> values)
    {
        var info = values.Skip(2).OfType<Amf0Object>().LastOrDefault();
        if (info is null) return null;
        return new RtmpStatus(
            info.GetValueOrDefault("code") as string ?? "",
            info.GetValueOrDefault("level") as string ?? "",
            info.GetValueOrDefault("description") as string ?? "");
    }

    private void OnStatus(RtmpStatus status)
    {
        LastStatus = status;
        TaskCompletionSource<RtmpStatus>? waiter;
        lock (gate) waiter = publishStatus;
        if (status.Code == "NetStream.Publish.Start") waiter?.TrySetResult(status);
        if (EndsTransport(status))
        {
            var reason = string.IsNullOrEmpty(status.Description) ? status.Code : $"{status.Code} — {status.Description}";
            var error = StreamSessionException.TransportClosed(reason);
            waiter?.TrySetException(error);
            Fail(error);
        }
    }

    /// <summary>The Mac's rule: any "error" level, or the connection being closed, timed out or shut down.</summary>
    public static bool EndsTransport(RtmpStatus status) =>
        status.Level == "error"
        || status.Code is "NetConnection.Connect.Closed" or "NetConnection.Connect.IdleTimeOut" or "NetConnection.Connect.AppShutdown";

    private void Fail(Exception error)
    {
        if (completion.TrySetException(error))
            BroadcastLog.Warning("rtmp", $"{url.Host}: {error.Message}");
        lock (gate)
        {
            foreach (var waiter in transactions.Values) waiter.TrySetException(error);
            publishStatus?.TrySetException(error);
        }
        // The reader can notice the closed socket after DisposeAsync has already let go of these.
        try { stopping.Cancel(); } catch (ObjectDisposedException) { }
        try { socket.Close(); } catch { }
    }

    /// <summary>
    /// Ends the stream politely: FCUnpublish and deleteStream (as HaishinKit's deleteStream
    /// does), a moment for them to be sent, then the socket is closed.
    /// </summary>
    public async Task CloseAsync()
    {
        if (!Completion.IsCompleted && writer is not null)
        {
            Enqueue(RtmpChunkStreamId.Command, RtmpMessage.Command(0, "FCUnpublish", NextTransaction(), null, streamKey));
            Enqueue(RtmpChunkStreamId.Command, RtmpMessage.Command(0, "deleteStream", NextTransaction(), null, (double)StreamId));
            outgoing.Writer.TryComplete();
            await Task.WhenAny(writer, Task.Delay(TimeSpan.FromSeconds(2)));
        }
        completion.TrySetResult();
        await DisposeAsync();
    }

    private int NextTransaction()
    {
        lock (gate) return nextTransaction++;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1) return;
        completion.TrySetResult();
        stopping.Cancel();
        outgoing.Writer.TryComplete();
        try { socket.Shutdown(SocketShutdown.Both); } catch { }
        try { socket.Close(); } catch { }
        try { await stream.DisposeAsync(); } catch { }
        foreach (var task in new[] { writer, reader })
        {
            if (task is null) continue;
            try { await task.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
        socket.Dispose();
        stopping.Dispose();
    }
}
