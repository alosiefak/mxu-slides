using System.Diagnostics;
using MxU.Slides.Core.MotionTemplates;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.MotionTemplates.Lottie;

/// <summary>
/// One template playing on the program: its animation (with its fields filled in), its
/// timeline, when it went up and when it was taken down. The newest picture is in
/// <see cref="MotionPlayers"/>' hands until the screen copies it.
/// </summary>
public sealed class LiveMotion
{
    internal LiveMotion(string id, LottieDocument document, MotionTimeline timeline, int width, int height, double upAt)
    {
        Id = id;
        document_ = document;
        timeline_ = timeline;
        Width = width;
        Height = height;
        UpAt = upAt;
        Front = new LottieFrame(width, height);
        Back = new LottieFrame(width, height);
    }

    /// <summary>The picture id the screens draw it under ("motion::…").</summary>
    public string Id { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>When it was put up (show clock, <see cref="ShowState.HostClock"/>).</summary>
    public double UpAt { get; }

    /// <summary>When it was taken down, or null while it's up.</summary>
    public double? DownAt
    {
        get { lock (Gate) return downAt; }
        internal set { lock (Gate) downAt = value; }
    }

    public MotionTimeline Timeline
    {
        get { lock (Gate) return timeline_; }
    }

    public LottieDocument Document
    {
        get { lock (Gate) return document_; }
    }

    /// <summary>Goes up by one each time a new picture is ready.</summary>
    public int Version
    {
        get { lock (Gate) return version; }
    }

    /// <summary>The frame (of the animation) the newest picture shows.</summary>
    public double ShownFrame
    {
        get { lock (Gate) return shownFrame; }
    }

    /// <summary>True once it has nothing more to show: its Out is over, or a Once has played through.</summary>
    public bool IsFinished => Volatile.Read(ref finished);

    /// <summary>Pictures drawn, and the time drawing them took in all (for the speed report).</summary>
    public int FramesDrawn => Volatile.Read(ref framesDrawn);

    public double DrawMilliseconds
    {
        get { lock (Gate) return drawMilliseconds; }
    }

    /// <summary>Why it stopped drawing, if drawing failed.</summary>
    public string? Failure { get; internal set; }

    internal readonly Lock Gate = new();
    internal LottieFrame Front;
    internal LottieFrame Back;
    internal LottieRenderer? Renderer;
    internal bool Redraw = true;
    internal bool FinishedRaised;
    private LottieDocument document_;
    private LottieDocument? pending;
    private MotionTimeline timeline_;
    private double? downAt;
    private int version;
    private double shownFrame = double.NaN;
    private bool finished;
    private int framesDrawn;
    private double drawMilliseconds;

    internal void Replace(LottieDocument document)
    {
        lock (Gate)
        {
            document_ = document;
            pending = document;
        }
    }

    internal void Replace(MotionTimeline timeline)
    {
        lock (Gate)
        {
            timeline_ = timeline;
            Redraw = true;
        }
    }

    /// <summary>A new animation to draw (its fields changed), taken by the drawing thread.</summary>
    internal LottieDocument? TakePending()
    {
        lock (Gate)
        {
            var next = pending;
            pending = null;
            return next;
        }
    }

    internal void MarkFinished() => Volatile.Write(ref finished, true);

    // What changed in each of the last few pictures, so a screen a picture or two behind copies only that.
    private readonly PixelRect[] changes = new PixelRect[8];

    /// <summary>Hands the picture just drawn (in <see cref="Back"/>) to the screens; <paramref name="changed"/> is what differs from the one before.</summary>
    internal void Publish(double frame, double milliseconds, PixelRect changed)
    {
        lock (Gate)
        {
            (Front, Back) = (Back, Front);
            shownFrame = frame;
            version++;
            changes[version % changes.Length] = changed;
            drawMilliseconds += milliseconds;
        }
        Interlocked.Increment(ref framesDrawn);
    }

    /// <summary>
    /// The part that changed between picture <paramref name="since"/> and now: all of it when that's
    /// too long ago, or when <paramref name="since"/> isn't one of this animation's (the screen last
    /// copied an earlier animation under the same id, which started again). Call under <see cref="Gate"/>.
    /// </summary>
    internal PixelRect ChangedSince(int since)
    {
        if (since <= 0 || since > version || version - since >= changes.Length) return new PixelRect(0, 0, Width, Height);
        var union = default(PixelRect);
        for (var v = since + 1; v <= version; v++) union = union.Union(changes[v % changes.Length]);
        return union;
    }

    internal void Dispose()
    {
        lock (Gate)
        {
            Renderer?.Dispose();
            Renderer = null;
            Front.Dispose();
            Back.Dispose();
        }
    }
}

/// <summary>
/// Plays templates live: lower thirds over the slides, full-screen backgrounds, with their
/// transparency. One thread of its own draws every playing template (never .NET's shared pool,
/// HOW-IT-WORKS section 26), off the UI thread.
/// <para>
/// <b>Paced by the screen.</b> Each time WPF is about to draw a picture, the app calls
/// <see cref="Request"/> with the moment that picture will be shown; this thread wakes and draws
/// each template at that moment (<see cref="MotionTimeline.FrameAt"/>), into a spare picture, and
/// swaps it in. The screen copies the newest one with <see cref="CopyLatest"/>. Every screen
/// draws that one picture, so they all show the same frame; a busy PC shows fewer frames, never
/// a late or jumbled one, because each frame is worked out from the clock, not from a count.
/// </para>
/// <para>
/// <b>Holding costs nothing.</b> A frame the same as the last one isn't drawn again, so a lower
/// third holding still uses no time at all until it's taken down.
/// </para>
/// </summary>
public sealed class MotionPlayers : IDisposable
{
    private readonly ILottieFonts? fonts;
    private readonly Func<double> clock;
    private readonly Lock gate = new();
    private readonly Dictionary<string, LiveMotion> playing = new(StringComparer.Ordinal);
    private readonly AutoResetEvent wake = new(false);
    private readonly Thread thread;
    private double requestedFor = double.NaN;
    private volatile bool disposed;

    /// <param name="fonts">Fonts for templates that use one Windows hasn't got.</param>
    /// <param name="clock">The show clock (default <see cref="ShowState.HostClock"/>); the tests give their own.</param>
    /// <param name="thread">False for the tests, which call <see cref="DrawNow"/> themselves.</param>
    public MotionPlayers(ILottieFonts? fonts = null, Func<double>? clock = null, bool thread = true)
    {
        this.fonts = fonts;
        this.clock = clock ?? (() => ShowState.HostClock());
        this.thread = new Thread(Run) { IsBackground = true, Name = "Motion templates" };
        if (thread) this.thread.Start();
    }

    /// <summary>A template has nothing more to show (a Once has played through, or an Out finished). Raised on the drawing thread, once each.</summary>
    public event Action<string>? Finished;

    /// <summary>Drawing a template failed (the template id, why). Raised on the drawing thread; that template stops drawing.</summary>
    public event Action<string, string>? Failed;

    /// <summary>Starts playing a template under <paramref name="id"/> (replacing one already there), as if it went up at <paramref name="upAt"/>.</summary>
    public LiveMotion Start(string id, LottieDocument document, MotionTimeline timeline, int width, int height, double upAt)
    {
        var motion = new LiveMotion(id, document, timeline, width, height, upAt);
        LiveMotion? old;
        lock (gate)
        {
            playing.TryGetValue(id, out old);
            playing[id] = motion;
        }
        old?.Dispose();
        wake.Set();
        return motion;
    }

    /// <summary>Takes a template down at <paramref name="at"/>: it jumps to its Out (or fades) from then. False when it isn't playing or is already down.</summary>
    public bool TakeDown(string id, double at)
    {
        var motion = this[id];
        if (motion is null || motion.DownAt is not null) return false;
        motion.DownAt = at;
        wake.Set();
        return true;
    }

    /// <summary>Its fields changed while it's up: the new words or colours show from the next picture, without starting it again.</summary>
    public void Update(string id, LottieDocument document)
    {
        this[id]?.Replace(document);
        wake.Set();
    }

    /// <summary>Its playback changed (loop, hold, once) while it's up.</summary>
    public void Update(string id, MotionTimeline timeline)
    {
        this[id]?.Replace(timeline);
        wake.Set();
    }

    public void Stop(string id)
    {
        LiveMotion? motion;
        lock (gate)
        {
            if (!playing.Remove(id, out motion)) return;
        }
        // The drawing thread may be part-way through it: it's let go once that thread is done with it.
        lock (drawing) motion.Dispose();
    }

    public LiveMotion? this[string id]
    {
        get { lock (gate) return playing.GetValueOrDefault(id); }
    }

    public IReadOnlyList<LiveMotion> Playing
    {
        get { lock (gate) return [.. playing.Values]; }
    }

    /// <summary>The screen will show its next picture at <paramref name="presentAt"/> (show clock): draw every template for then.</summary>
    public void Request(double presentAt)
    {
        Volatile.Write(ref requestedFor, presentAt);
        wake.Set();
    }

    /// <summary>
    /// If <paramref name="id"/> has a picture newer than <paramref name="version"/>, hands it to
    /// <paramref name="copy"/> (while the drawing thread waits to swap) with the part that changed
    /// since that version, and moves <paramref name="version"/> on. Keep <paramref name="copy"/>
    /// quick: it's one copy of (part of) the pixels.
    /// </summary>
    public bool CopyLatest(string id, ref int version, Action<LottieFrame, PixelRect> copy)
    {
        if (this[id] is not { } motion) return false;
        lock (motion.Gate)
        {
            if (motion.Version == version || motion.Version == 0) return false;
            copy(motion.Front, motion.ChangedSince(version));
            version = motion.Version;
            return true;
        }
    }

    // MARK: Drawing

    private readonly Lock drawing = new();

    private void Run()
    {
        while (!disposed)
        {
            // Woken by the screen (about every refresh), or at least 25 times a second without it,
            // so a template keeps moving in a recording even while no window draws.
            wake.WaitOne(TimeSpan.FromMilliseconds(40));
            if (disposed) return;
            var requested = Volatile.Read(ref requestedFor);
            var now = clock();
            // A request from long ago (the screen stopped asking) is stale: draw for now instead.
            DrawNow(double.IsNaN(requested) || Math.Abs(requested - now) > 0.5 ? now : requested);
        }
    }

    /// <summary>Draws every playing template as it is at <paramref name="at"/> (the drawing thread's work; the tests call it directly).</summary>
    public void DrawNow(double at)
    {
        foreach (var motion in Playing)
        {
            if (disposed) return;
            lock (drawing)
            {
                if (motion.Failure is not null || !IsPlaying(motion)) continue;
                try
                {
                    Draw(motion, at);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    motion.Failure = error.Message;
                    Failed?.Invoke(motion.Id, error.Message);
                }
            }
        }
    }

    private bool IsPlaying(LiveMotion motion)
    {
        lock (gate) return playing.TryGetValue(motion.Id, out var current) && ReferenceEquals(current, motion);
    }

    private void Draw(LiveMotion motion, double at)
    {
        if (motion.TakePending() is { } changed)
        {
            motion.Renderer?.Dispose();
            motion.Renderer = new LottieRenderer(changed, fonts);
            motion.Redraw = true;
        }
        motion.Renderer ??= new LottieRenderer(motion.Document, fonts);
        var downAt = motion.DownAt;
        var (frame, finished) = motion.Timeline.FrameAt(at - motion.UpAt, downAt is { } d ? at - d : null);
        if (finished)
        {
            motion.MarkFinished();
            if (!motion.FinishedRaised)
            {
                motion.FinishedRaised = true;
                Finished?.Invoke(motion.Id);
            }
        }
        if (!motion.Redraw && frame == motion.ShownFrame) return;
        var started = Stopwatch.GetTimestamp();
        // Nothing moved since the last picture (a lower third holding still): no new picture needed.
        if (!motion.Renderer.Seek(frame, motion.Width, motion.Height, out var region) && !motion.Redraw) return;
        if (motion.Redraw) region = new PixelRect(0, 0, motion.Width, motion.Height);
        motion.Redraw = false;
        motion.Renderer.RenderCurrent(motion.Back);
        motion.Publish(frame, Stopwatch.GetElapsedTime(started).TotalMilliseconds, region);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        wake.Set();
        if (thread.IsAlive) thread.Join(TimeSpan.FromSeconds(2));
        foreach (var motion in Playing) lock (drawing) motion.Dispose();
        lock (gate) playing.Clear();
        wake.Dispose();
    }
}
