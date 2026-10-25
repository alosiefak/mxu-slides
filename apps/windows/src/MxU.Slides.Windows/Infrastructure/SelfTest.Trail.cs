using System.Windows.Threading;

namespace MxU.Slides.Windows.Infrastructure;

public sealed partial class SelfTest
{
    private string ReportFolder => folder;

    private Dispatcher UiDispatcher => window.Dispatcher;

    /// <summary>
    /// A trail for a run that never finishes, such as a hang on a test machine. Every two
    /// seconds the report so far is written to report.partial.txt. If the UI thread (where
    /// every check runs) stops answering for 20 seconds, or still answers but no check has
    /// finished for 45 (something it awaits never comes), that's logged with the last line of
    /// the report and every thread's call stack. All of this runs on its own background thread,
    /// because a stuck UI thread can't report on itself.
    /// </summary>
    private sealed class Trail : IDisposable
    {
        private readonly SelfTest test;
        private readonly Thread thread;
        private readonly int uiThreadId = Environment.CurrentManagedThreadId; // made on the UI thread
        private volatile bool done;

        public Trail(SelfTest test)
        {
            this.test = test;
            thread = new Thread(Watch) { IsBackground = true, Name = "MxU self-test trail" };
            thread.Start();
        }

        public void Dispose()
        {
            done = true;
            thread.Join(TimeSpan.FromSeconds(3));
        }

        private void Watch()
        {
            var partial = Path.Combine(test.ReportFolder, "report.partial.txt");
            var dispatcher = test.UiDispatcher;
            var stuckSince = (DateTime?)null;
            var stacks = "";
            var lastWrite = DateTime.MinValue;
            var grownTo = -1;
            var lastGrowth = DateTime.Now;
            var stalled = false;
            var stallNote = "";
            while (!done)
            {
                // Is the UI thread answering? (A check awaiting a delay still lets it answer.)
                var answered = dispatcher.InvokeAsync(() => { }, DispatcherPriority.Send).Task.Wait(TimeSpan.FromSeconds(20));
                if (done) return;
                var text = Snapshot();
                if (!answered)
                {
                    var first = stuckSince is null;
                    stuckSince ??= DateTime.Now.AddSeconds(-20);
                    var line = $"STUCK the UI thread hasn't answered since {stuckSince:HH:mm:ss} (the last report line: {LastLine(text)})";
                    // Where every thread is, once per hang: what the UI thread is stuck in, and what it may be waiting for.
                    if (first)
                    {
                        stacks = ThreadStacks.Describe(uiThreadId);
                        Log.Warn($"Self-test: {line}{Environment.NewLine}{stacks}");
                    }
                    text += line + Environment.NewLine + stacks;
                }
                else
                {
                    stuckSince = null;
                    // The UI thread answers but the report hasn't grown: a check is awaiting something that
                    // never comes (a timer starved of threads, say). Once per stall, show what every thread is doing.
                    if (text.Length != grownTo)
                    {
                        grownTo = text.Length;
                        lastGrowth = DateTime.Now;
                        stalled = false;
                    }
                    else if (!stalled && DateTime.Now - lastGrowth > TimeSpan.FromSeconds(45))
                    {
                        stalled = true;
                        System.Threading.ThreadPool.GetAvailableThreads(out var workers, out _);
                        System.Threading.ThreadPool.GetMaxThreads(out var maxWorkers, out _);
                        stacks = ThreadStacks.Describe(uiThreadId);
                        var line = $"STALLED no new report line for 45 s, though the UI thread answers (the last line: {LastLine(text)}); "
                            + $"thread pool: {ThreadPool.ThreadCount} threads, {maxWorkers - workers} busy, {ThreadPool.PendingWorkItemCount} jobs waiting";
                        Log.Warn($"Self-test: {line}{Environment.NewLine}{stacks}");
                        stallNote = line + Environment.NewLine + stacks;
                    }
                    if (stalled) text += stallNote;
                }
                if (!answered || DateTime.Now - lastWrite > TimeSpan.FromSeconds(2))
                {
                    try { File.WriteAllText(partial, text); }
                    catch (IOException) { }
                    lastWrite = DateTime.Now;
                }
                if (answered) Thread.Sleep(2000);
            }
            try { File.Delete(partial); }
            catch (IOException) { }
        }

        // The report is written on the UI thread; reading it from here is best effort (a torn read is retried next time).
        private string Snapshot()
        {
            try { return test.report.ToString(); }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException) { return ""; }
        }

        private static string LastLine(string text) =>
            text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "none";
    }
}
