using System.Diagnostics;
using System.Windows.Threading;
using MxU.Slides.Core.Diagnostics;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>
/// Watches the UI thread in the running app, as the self-test's watchdog does during a test
/// (SelfTest.Trail.cs). Four times a second a thread of its own asks the UI thread to answer; the
/// sums are Core's <see cref="HangDetector"/>:
/// <list type="bullet">
/// <item>an answer that took over a quarter of a second is a hitch, noted in the log (a few a minute at most);</item>
/// <item>no answer for five seconds is a hang: logged at once, with every thread's call stack saved to
/// Logs\hang-….txt (read with ClrMD: ThreadStacks.cs), so what it was stuck in can be read later,
/// and a problem report carries it.</item>
/// </list>
/// It never blocks the show itself: it only ever posts a tiny job to the UI thread and waits on its
/// own thread. Reading the stacks pauses the app for a moment, so that happens only when the UI is
/// already stuck, at most once every two minutes, and never during a self-test (whose own watchdog
/// does it at 20 s). The Mac's twin is DiagnosticsStore's watchdog ping (perf.hitch, hang samples).
/// </summary>
internal sealed class HangWatch : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan StacksAtMostEvery = TimeSpan.FromMinutes(2);

    private readonly Dispatcher dispatcher;
    private readonly bool captureStacks;
    private readonly int uiThreadId;
    private readonly HangDetector detector = new();
    private readonly Lock gate = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Thread thread;
    private volatile bool done;
    private DateTime lastStacks = DateTime.MinValue;

    private HangWatch(Dispatcher dispatcher, bool captureStacks)
    {
        this.dispatcher = dispatcher;
        this.captureStacks = captureStacks;
        uiThreadId = dispatcher.Thread.ManagedThreadId;
        thread = new Thread(Watch) { IsBackground = true, Name = "MxU hang watch" };
        thread.Start();
    }

    /// <summary>Starts watching <paramref name="dispatcher"/>'s thread (the UI thread).</summary>
    public static HangWatch Start(Dispatcher dispatcher, bool captureStacks)
    {
        PruneOldReports();
        return new HangWatch(dispatcher, captureStacks);
    }

    private double Now => clock.Elapsed.TotalSeconds;

    private void Watch()
    {
        while (!done)
        {
            HangDetector.Event? happened;
            lock (gate)
            {
                if (!detector.Waiting)
                {
                    detector.PingSent(Now);
                    // Normal priority, like any other job: a long queue of work counts as a hitch too.
                    dispatcher.BeginInvoke(Answered, DispatcherPriority.Normal);
                    happened = null;
                }
                else
                {
                    happened = detector.Check(Now);
                }
            }
            if (happened is HangDetector.Event.HangBegan) OnHang();
            Thread.Sleep(Interval);
        }
    }

    private void Answered()
    {
        HangDetector.Event? happened;
        lock (gate) happened = detector.Answered(Now);
        switch (happened)
        {
            case HangDetector.Event.Hitch hitch:
                Log.Info(PerfLineLimiter.Annotate($"UI thread blocked {hitch.Blocked.TotalMilliseconds:0} ms", hitch.Dropped));
                break;
            case HangDetector.Event.HangEnded ended:
                Log.Warn($"The UI thread answered again after {ended.Lasted.TotalSeconds:0.0} s");
                break;
        }
    }

    /// <summary>The UI thread has been stuck for five seconds: say so, and (now and then) save where every thread is.</summary>
    private void OnHang()
    {
        Log.Warn($"The UI thread hasn't answered for {HangDetector.HangAfter.TotalSeconds:0} s");
        if (!captureStacks || DateTime.Now - lastStacks < StacksAtMostEvery) return;
        lastStacks = DateTime.Now;
        // On a thread of its own, with a time limit: if reading the stacks hangs too, the watch carries on.
        var stacks = "(reading the threads' stacks didn't finish in time)";
        var reader = new Thread(() => stacks = ThreadStacks.Describe(uiThreadId)) { IsBackground = true, Name = "MxU hang stacks" };
        reader.Start();
        reader.Join(TimeSpan.FromSeconds(20));
        try
        {
            Directory.CreateDirectory(AppPaths.LogDirectory);
            var path = Path.Combine(AppPaths.LogDirectory, $"hang-{DateTime.Now:yyyy-MM-dd-HHmmss}.txt");
            File.WriteAllText(path, $"MxU Slides {typeof(HangWatch).Assembly.GetName().Version} stopped responding at {DateTime.Now:yyyy-MM-dd HH:mm:ss}.{Environment.NewLine}"
                + $"The UI thread's stack comes first.{Environment.NewLine}{Environment.NewLine}{stacks}");
            Log.Warn($"Saved the threads' stacks to {Path.GetFileName(path)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Couldn't save the hang report: {ex.Message}");
        }
    }

    /// <summary>Hang reports older than two weeks go, like the logs (the Mac's DiagnosticsBundle.expired).</summary>
    private static void PruneOldReports()
    {
        var old = DiagnosticsBundle.Expired(DiagnosticsBundle.Candidates(AppPaths.LogDirectory).Where(c => c.Name.StartsWith("hang-", StringComparison.Ordinal)), DateTimeOffset.Now);
        foreach (var file in old)
        {
            try { File.Delete(file.Path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public void Dispose()
    {
        done = true;
        thread.Join(TimeSpan.FromSeconds(1));
    }
}
