using System.Windows.Threading;
using MxU.Slides.Broadcast.Control;
using MxU.Slides.Core.Media;
using MxU.Slides.Core.Outputs;
using MxU.Slides.Windows.Infrastructure;

namespace MxU.Slides.Windows.Outputs;

/// <summary>
/// Screen mirrors: a slide object whose fill is <c>screen::&lt;screen id&gt;</c> shows what one of this
/// app's screens shows (the stage display inside a slide, the stream's lower-third screen on the
/// projector...). Like the Mac's ScreenMirrorService, a mirror is a hidden copy of that screen, here
/// a <see cref="ScreenFeeds"/> feed, whose newest picture is drawn like a live camera.
/// <list type="bullet">
/// <item>A mirror starts the first time something draws it and stops once nothing has for ten seconds,
/// so a mirror only costs anything while it's on screen.</item>
/// <item>A screen that isn't in this PC's setup (a Mac display id) has no picture: the slide shows the
/// mirror's placeholder in thumbnails and nothing on the outputs.</item>
/// </list>
/// Pictures arrive on the feed's capture thread; <see cref="LatestFrame"/> is read on the UI thread.
/// </summary>
public sealed class ScreenMirrors : IDisposable
{
    private const string Prefix = "screen::";
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(10);

    private readonly Func<ScreenFeeds?> feeds;
    private readonly Func<OutputConfiguration?> config;
    private readonly Dispatcher dispatcher;
    private readonly Dictionary<string, Mirror> mirrors = new(StringComparer.Ordinal);
    private readonly DispatcherTimer sweeper;

    /// <param name="feeds">The app's screen feeds (made on first use).</param>
    /// <param name="config">The screen setup, for each screen's size.</param>
    public ScreenMirrors(Func<ScreenFeeds?> feeds, Func<OutputConfiguration?> config)
    {
        this.feeds = feeds;
        this.config = config;
        dispatcher = Dispatcher.CurrentDispatcher;
        sweeper = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => Sweep(), dispatcher);
    }

    public static bool IsMirror(string engineId) => engineId.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>How many mirrors are running (each a hidden screen feed being captured).</summary>
    public int RunningCount => mirrors.Values.Count(m => m.Source is not null);

    /// <summary>
    /// The newest picture of the mirrored screen, or null while it starts (or if it can't). Asking
    /// keeps the mirror running; the first ask starts it, just after this frame (starting a feed draws).
    /// </summary>
    public VideoFrame? LatestFrame(string engineId)
    {
        if (!IsMirror(engineId)) return null;
        if (!mirrors.TryGetValue(engineId, out var mirror))
        {
            mirrors[engineId] = mirror = new Mirror(engineId[Prefix.Length..]);
            dispatcher.BeginInvoke(DispatcherPriority.Background, () => Start(mirror));
            if (!sweeper.IsEnabled) sweeper.Start();
        }
        mirror.LastAsked = DateTime.UtcNow;
        return mirror.Latest;
    }

    private void Start(Mirror mirror)
    {
        if (mirror.Source is not null || mirror.Failed || !mirrors.ContainsValue(mirror)) return;
        var size = config()?.Feed(mirror.ScreenId).CanvasSize;
        if (size is not { } canvas || feeds() is not { } screenFeeds)
        {
            mirror.Failed = true;
            Log.Info($"Screen mirror: no screen {mirror.ScreenId} on this PC");
            return;
        }
        // A picture in a slide doesn't need full size: at most 1280 across keeps the copy cheap.
        var scale = Math.Min(1, 1280 / Math.Max(1, canvas.Width));
        var source = screenFeeds.Source(mirror.ScreenId, (int)(canvas.Width * scale), (int)(canvas.Height * scale), 30);
        if (source is null)
        {
            mirror.Failed = true;
            return;
        }
        source.Add(mirror);
        mirror.Source = source;
        Log.Info($"Screen mirror started: {mirror.ScreenId}");
    }

    private void Sweep()
    {
        foreach (var (id, mirror) in mirrors.ToList())
        {
            if (DateTime.UtcNow - mirror.LastAsked < Idle) continue;
            mirror.Source?.Remove(mirror);
            mirrors.Remove(id);
            if (mirror.Source is not null) Log.Info($"Screen mirror stopped: {mirror.ScreenId}");
        }
        if (mirrors.Count == 0) sweeper.Stop();
    }

    public void Dispose()
    {
        sweeper.Stop();
        foreach (var mirror in mirrors.Values) mirror.Source?.Remove(mirror);
        mirrors.Clear();
    }

    /// <summary>One mirrored screen: a destination of its feed that keeps a copy of the newest picture.</summary>
    private sealed class Mirror(string screenId) : IProgramSink
    {
        private readonly Lock gate = new();
        // Three buffers taken in turn: the newest picture, the one before (the UI thread may still be
        // copying it), and the one being written. No new 4 MB buffer every frame for the GC to sweep.
        private readonly byte[][] buffers = [[], [], []];
        private int next;
        private VideoFrame? latest;

        public string ScreenId { get; } = screenId;
        public IProgramSource? Source;
        public bool Failed;
        public DateTime LastAsked = DateTime.UtcNow;

        public VideoFrame? Latest
        {
            get { lock (gate) return latest; }
        }

        public void WriteVideo(VideoFrame frame)
        {
            // The feed's pixels are only valid during this call: keep a copy.
            var size = frame.Height * frame.Stride;
            byte[] copy;
            lock (gate)
            {
                if (buffers[next].Length != size) buffers[next] = new byte[size];
                copy = buffers[next];
                next = (next + 1) % buffers.Length;
            }
            frame.Pixels.Span[..Math.Min(size, frame.Pixels.Length)].CopyTo(copy);
            lock (gate) latest = new VideoFrame(frame.Width, frame.Height, frame.Stride, copy, frame.HostTime);
        }

        public void WriteAudio(AudioBuffer buffer)
        {
        }
    }
}
