using System.Diagnostics;
using MxU.Slides.Production.Lighting;

namespace MxU.Slides.Production.Dmx;

/// <summary>
/// The DMX heartbeat: one thread of its own that, about 40 times a second, asks the
/// <see cref="LightingController"/> for every level and hands the frame to each DMX node.
/// <para>
/// Why keep sending when nothing changes: DMX over a network is a stream, not a set of
/// commands. Nodes expect a steady flow (sACN receivers give a source up after 2.5 s of
/// silence) and a lost packet is simply mended by the next one 25 ms later. A fade is just
/// the levels being a little different in each frame.
/// </para>
/// <para>
/// The ticks are kept to a schedule (start + n × 25 ms) rather than "wait 25 ms each time",
/// so Windows' coarse timer can make one gap shorter and the next longer, but the rate
/// averages 40 Hz and never drifts. A change (a look fired, blackout) wakes the thread so the
/// first frame of it goes at once. Until the first lighting step nothing is sent at all, so
/// starting the app never takes the rig from a lighting desk.
/// </para>
/// </summary>
public sealed class DmxEngine : IDisposable
{
    public const double FramesPerSecond = 40;

    private readonly LightingController lighting;
    private readonly AutoResetEvent wake = new(false);
    private readonly Thread thread;
    private volatile IReadOnlyList<DmxOutput> outputs = [];
    private volatile bool stopped;
    private long frames;

    public DmxEngine(LightingController lighting)
    {
        this.lighting = lighting;
        lighting.Nudged += Wake;
        thread = new Thread(Run) { IsBackground = true, Name = "MxU DMX engine", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    /// <summary>Frames worked out and sent so far (diagnostics and tests).</summary>
    public long Frames => Interlocked.Read(ref frames);

    /// <summary>A frame failed (a node threw). Raised on the engine's thread; the engine carries on.</summary>
    public event Action<Exception>? Failed;

    /// <summary>The nodes to send to (Settings › Production changes them; the swap is one step, no lock needed).</summary>
    public void SetOutputs(IReadOnlyList<DmxOutput> nodes)
    {
        outputs = nodes;
        Wake();
    }

    /// <summary>Sends a frame now, not at the next tick.</summary>
    public void Wake() => wake.Set();

    private void Run()
    {
        var period = (long)(Stopwatch.Frequency / FramesPerSecond);
        // The shortest gap an early frame may leave after the last one: 44 frames a second at most,
        // the fastest DMX itself goes, however often a dragged slider wakes the thread.
        var shortest = Stopwatch.Frequency / 44;
        var next = Stopwatch.GetTimestamp();
        var lastTick = 0L;
        while (!stopped)
        {
            // Nothing to send (no node, or no lighting step yet): sleep until something changes.
            if (outputs.Count == 0 || !lighting.IsActive)
            {
                wake.WaitOne(250);
                next = Stopwatch.GetTimestamp();
                continue;
            }
            var now = Stopwatch.GetTimestamp();
            var woken = false;
            if (next > now)
            {
                var wait = (int)Math.Ceiling((next - now) * 1000.0 / Stopwatch.Frequency);
                woken = wake.WaitOne(Math.Max(1, wait));
                if (stopped) return;
                // Too soon after the last frame: the change goes out with the next one instead.
                if (woken && Stopwatch.GetTimestamp() - lastTick < shortest) continue;
            }
            lastTick = Stopwatch.GetTimestamp();
            Tick();
            if (woken)
            {
                // An early frame: the steady beat carries on a whole tick after it.
                next = lastTick + period;
                continue;
            }
            next += period;
            // Far behind (the PC was asleep, or very busy): carry on from now rather than send a burst.
            if (next < Stopwatch.GetTimestamp() - period) next = Stopwatch.GetTimestamp() + period;
        }
    }

    private void Tick()
    {
        try
        {
            if (!lighting.IsActive) return;
            var frame = lighting.Frame();
            foreach (var output in outputs) output.Send(frame);
            Interlocked.Increment(ref frames);
        }
        catch (Exception ex)
        {
            try { Failed?.Invoke(ex); }
            catch { }
        }
    }

    public void Dispose()
    {
        stopped = true;
        lighting.Nudged -= Wake;
        wake.Set();
    }
}
