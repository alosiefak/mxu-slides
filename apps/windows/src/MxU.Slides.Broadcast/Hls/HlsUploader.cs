using System.Net.Http.Headers;
using System.Text;
using System.Threading.Channels;
using MxU.Slides.Broadcast.Streaming;

namespace MxU.Slides.Broadcast.Hls;

/// <summary>
/// Delivers HLS segments to the ingest over HTTP PUT, in order, one at a time: the segment,
/// then the re-rendered playlist. Failures retry with back-off; after two failed tries it moves
/// to the backup ingest for good; after four it gives up (the session reconnects). A 401
/// (the ingest URL expired) or 400/403/405 is fatal. Holds at most <see cref="QueueDepth"/>
/// segments: if the network can't keep up, new ones are dropped rather than piling up.
/// Port of HLSUploader.swift (an actor there; here one <see cref="RunAsync"/> loop owns the state).
/// </summary>
internal sealed class HlsUploader
{
    public sealed record Item(string Name, byte[] Data, double Duration);

    public const int QueueDepth = 30;

    private readonly string primary;
    private readonly string? backup;
    private string activeTemplate;
    private readonly HttpClient client;
    private readonly HlsPlaylist playlist;
    private readonly HlsContinuity continuity;
    private readonly Channel<Item> items = Channel.CreateBounded<Item>(
        new BoundedChannelOptions(QueueDepth) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly Dictionary<string, int> lastStatusByKind = [];
    private bool loggedHost;

    private static readonly string? DumpDirectory = Environment.GetEnvironmentVariable("MXU_HLS_DUMP");

    /// <param name="handler">Tests pass a stub; normally a fresh handler with a 10 s timeout and one connection.</param>
    public HlsUploader(string primary, string? backup, HlsContinuity continuity, HttpMessageHandler? handler = null)
    {
        this.primary = primary;
        this.backup = backup;
        activeTemplate = primary;
        this.continuity = continuity;
        playlist = new HlsPlaylist(continuity.ResumeSequence);
        client = handler is null
            ? new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = 1, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
            : new HttpClient(handler, disposeHandler: false);
        client.Timeout = TimeSpan.FromSeconds(10);
    }

    /// <summary>How the retry loop waits; tests make it instant.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Sleep { get; set; } = Task.Delay;

    public void Enqueue(Item item)
    {
        if (!items.Writer.TryWrite(item))
            BroadcastLog.Warning("hls-upload", $"upload queue full: dropped {item.Name}");
    }

    public void FinishEnqueuing() => items.Writer.TryComplete();

    /// <summary>Uploads until <see cref="FinishEnqueuing"/> and the queue is empty. Throws <see cref="StreamSessionException"/>.</summary>
    public async Task RunAsync(CancellationToken cancellation)
    {
        try
        {
            if (DumpDirectory is { } dump) Directory.CreateDirectory(dump);
            await foreach (var item in items.Reader.ReadAllAsync(cancellation))
                await DeliverAsync(item, cancellation);
        }
        finally
        {
            client.Dispose();
        }
    }

    private async Task DeliverAsync(Item segment, CancellationToken cancellation)
    {
        if (DumpDirectory is { } dump) TryWrite(Path.Combine(dump, segment.Name), segment.Data);
        await PutAsync(segment.Name, segment.Data, "video/mp2t", cancellation);
        playlist.Add(segment.Name, segment.Duration);
        continuity.RecordSequence(playlist.NextSequence);
        var rendered = Encoding.UTF8.GetBytes(playlist.Render());
        if (DumpDirectory is { } dir) TryWrite(Path.Combine(dir, "playlist.m3u8"), rendered);
        await PutAsync("playlist.m3u8", rendered, "application/vnd.apple.mpegurl", cancellation);
    }

    private static void TryWrite(string path, byte[] data)
    {
        try { File.WriteAllBytes(path, data); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async Task PutAsync(string name, byte[] body, string contentType, CancellationToken cancellation)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                await PutOnceAsync(activeTemplate, name, body, contentType, cancellation);
                return;
            }
            catch (StreamSessionException error) when (!error.IsFatal)
            {
                attempt++;
                if (attempt == 2 && backup is not null && activeTemplate == primary)
                {
                    BroadcastLog.Warning("hls-upload", $"hls ingest failing over to backup after {attempt} attempts");
                    activeTemplate = backup;
                }
                else if (attempt >= 4)
                {
                    throw StreamSessionException.TransportClosed($"hls ingest unreachable after {attempt} attempts", error);
                }
                var delay = Math.Min(4.0, Math.Pow(2.0, attempt - 1) * 0.5);
                await Sleep(TimeSpan.FromSeconds(delay), cancellation);
            }
        }
    }

    private async Task PutOnceAsync(string template, string name, byte[] body, string contentType, CancellationToken cancellation)
    {
        var url = UploadUrl(template, name) ?? throw StreamSessionException.Fatal("bad HLS ingest URL template");
        if (!loggedHost)
        {
            loggedHost = true;
            BroadcastLog.Info("hls-upload", $"hls ingest host: {url.Host}");
        }
        int status;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            using var response = await client.SendAsync(request, cancellation);
            status = (int)response.StatusCode;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            throw StreamSessionException.TransportClosed($"hls upload failed: {e.Message}", e);
        }
        var kind = name.EndsWith(".ts", StringComparison.Ordinal) ? "segment" : "playlist";
        if (!lastStatusByKind.TryGetValue(kind, out var last) || last != status)
        {
            lastStatusByKind[kind] = status;
            BroadcastLog.Info("hls-upload", $"hls {kind} status -> {status} ({name})");
        }
        switch (status)
        {
            case >= 200 and <= 299:
                return;
            case 401:
                throw StreamSessionException.Fatal("hls ingest rejected credentials (401) — the ingest URL expired");
            case 400 or 403 or 405:
                throw StreamSessionException.Fatal($"hls ingest rejected the upload ({status})");
            default:
                throw StreamSessionException.TransportClosed($"hls ingest returned {status}");
        }
    }

    /// <summary>
    /// Completes an ingest template with a file name: YouTube's ends in "file=", so the name is
    /// appended; otherwise "file=name" is added as a query parameter.
    /// </summary>
    public static Uri? UploadUrl(string template, string filename)
    {
        string text;
        if (template.EndsWith('=')) text = template + filename;
        else if (template.Contains('?')) text = template + "&file=" + filename;
        else text = template + "?file=" + filename;
        return Uri.TryCreate(text, UriKind.Absolute, out var url) ? url : null;
    }
}
