using System.Collections.Concurrent;

namespace MxU.Slides.Core.Threading;

/// <summary>
/// Runs work one item at a time, in order, on its own thread: the Windows stand-in for a
/// serial DispatchQueue on the Mac. Work that a show depends on (a track ending, a song
/// handing over, the next song loading) goes on one of these rather than .NET's shared
/// thread pool, so it still happens on time when the pool is tied up by an import, a slow
/// disk or a busy machine. A failing item is reported to <see cref="Failed"/> and the queue
/// carries on.
/// </summary>
public sealed class WorkQueue : IDisposable
{
    private readonly BlockingCollection<Action> work = [];
    private readonly Thread thread;

    /// <param name="name">The thread's name (it shows in the debugger and crash dumps).</param>
    /// <param name="priority">Above normal for timing-critical notifications.</param>
    /// <param name="apartment">MTA for queues that talk to COM objects that need it (Windows' audio devices).</param>
    public WorkQueue(string name, ThreadPriority priority = ThreadPriority.Normal, ApartmentState apartment = ApartmentState.Unknown)
    {
        Name = name;
        thread = new Thread(Run) { IsBackground = true, Name = name, Priority = priority };
        if (apartment != ApartmentState.Unknown && OperatingSystem.IsWindows()) thread.SetApartmentState(apartment);
        thread.Start();
    }

    public string Name { get; }

    /// <summary>A work item threw (the queue, the exception). Raised on the queue's thread.</summary>
    public static event Action<WorkQueue, Exception>? Failed;

    /// <summary>True when the caller is running on this queue.</summary>
    public bool IsCurrent => Thread.CurrentThread == thread;

    /// <summary>Items waiting to run (diagnostics and tests).</summary>
    public int Pending => work.Count;

    /// <summary>Adds <paramref name="action"/> to the end of the queue; ignored once disposed.</summary>
    public void Async(Action action)
    {
        try { work.Add(action); }
        catch (InvalidOperationException) { }
    }

    /// <summary>Adds <paramref name="action"/> after <paramref name="delay"/>, timed by the show clock.</summary>
    public void AsyncAfter(TimeSpan delay, Action action)
    {
        ITimer? timer = null;
        timer = ShowTimeProvider.Shared.CreateTimer(_ =>
        {
            timer?.Dispose();
            Async(action);
        }, null, delay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Runs <paramref name="action"/> on the queue and waits for it (inline when already on it).</summary>
    /// <exception cref="TimeoutException">The queue didn't get to it within <paramref name="timeout"/> (default 10 s).</exception>
    public T Sync<T>(Func<T> action, TimeSpan? timeout = null)
    {
        if (IsCurrent) return action();
        T result = default!;
        Exception? failure = null;
        using var done = new ManualResetEventSlim(false);
        Async(() =>
        {
            try { result = action(); }
            catch (Exception error) { failure = error; }
            finally { done.Set(); }
        });
        if (!done.Wait(timeout ?? TimeSpan.FromSeconds(10))) throw new TimeoutException($"{Name} didn't answer");
        if (failure is not null) throw failure;
        return result;
    }

    public void Sync(Action action, TimeSpan? timeout = null) => Sync(() =>
    {
        action();
        return 0;
    }, timeout);

    private void Run()
    {
        foreach (var action in work.GetConsumingEnumerable())
        {
            try
            {
                action();
            }
            catch (Exception error)
            {
                try { Failed?.Invoke(this, error); }
                catch { }
            }
        }
    }

    /// <summary>Stops taking work; what's already queued still runs.</summary>
    public void Dispose() => work.CompleteAdding();
}
