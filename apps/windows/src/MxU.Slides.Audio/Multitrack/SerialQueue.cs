using System.Collections.Concurrent;

namespace MxU.Slides.Audio.Multitrack;

/// <summary>
/// Runs work one item at a time, in order, on its own thread: the Windows stand-in for a
/// serial DispatchQueue, so the tracks player's server and client can keep the Mac's
/// "everything happens on my queue" design.
/// </summary>
internal sealed class SerialQueue : IDisposable
{
    private readonly BlockingCollection<Action> work = [];
    private readonly Thread thread;

    public SerialQueue(string name)
    {
        thread = new Thread(Run) { IsBackground = true, Name = name };
        thread.Start();
    }

    public bool IsCurrent => Thread.CurrentThread == thread;

    public void Async(Action action)
    {
        try { work.Add(action); }
        catch (InvalidOperationException) { }
    }

    public void AsyncAfter(TimeSpan delay, Action action)
    {
        Timer? timer = null;
        timer = new Timer(_ =>
        {
            timer?.Dispose();
            Async(action);
        }, null, delay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Runs <paramref name="action"/> on the queue and waits for it (inline when already on it).</summary>
    public T Sync<T>(Func<T> action)
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
        if (!done.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The queue didn't answer");
        if (failure is not null) throw failure;
        return result;
    }

    public void Sync(Action action) => Sync(() =>
    {
        action();
        return 0;
    });

    private void Run()
    {
        foreach (var action in work.GetConsumingEnumerable())
        {
            try { action(); }
            catch { }
        }
    }

    public void Dispose() => work.CompleteAdding();
}
