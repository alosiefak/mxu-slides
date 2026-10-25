namespace MxU.Slides.Core.Motion;

/// <summary>
/// A small team of threads of its own that share out the rows of a picture. Drawing a 4K
/// frame is millions of pixels, so it's split across every core; but not on .NET's shared
/// thread pool, which the rest of the app relies on staying free (HOW-IT-WORKS section 26),
/// and at below-normal priority, so a background rendering during a service never makes the
/// projector stutter: Windows gives the show's threads the cores first.
/// <para>
/// <see cref="For"/> hands out work items one at a time from a shared counter, so a fast
/// thread takes more of them, and the calling thread joins in rather than waiting idle.
/// Every item writes only its own part of the picture, so the result is identical whatever
/// the number of threads or the order they finish in.
/// </para>
/// </summary>
public sealed class RenderCrew : IDisposable
{
    private readonly Thread[] threads;
    private readonly SemaphoreSlim start = new(0);
    private readonly Lock gate = new();
    private CountdownEvent? done;
    private Action<int, int>? body;
    private int count;
    private int next;
    private Exception? failure;
    private volatile bool disposed;

    /// <param name="name">The threads' name, for the debugger.</param>
    /// <param name="helpers">Threads besides the caller; default one fewer than the PC's cores.</param>
    public RenderCrew(string name, int? helpers = null, ThreadPriority priority = ThreadPriority.BelowNormal)
    {
        var size = Math.Max(0, helpers ?? Environment.ProcessorCount - 1);
        threads = new Thread[size];
        for (var i = 0; i < size; i++)
        {
            var worker = i + 1;
            threads[i] = new Thread(() => Run(worker)) { IsBackground = true, Name = $"{name} {worker}", Priority = priority };
            threads[i].Start();
        }
    }

    /// <summary>How many threads take part in <see cref="For"/>, the caller included. Scratch space is indexed 0 to this − 1.</summary>
    public int Size => threads.Length + 1;

    /// <summary>
    /// Runs <paramref name="work"/>(item, worker) for every item from 0 to <paramref name="items"/> − 1,
    /// spread over the crew, and returns when all are done. <c>worker</c> says which thread
    /// (0 is the caller), so each can have scratch memory of its own. One call at a time; the
    /// first exception thrown by an item is rethrown here.
    /// </summary>
    public void For(int items, Action<int, int> work)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (items <= 0) return;
        lock (gate)
        {
            body = work;
            count = items;
            next = 0;
            failure = null;
            using var finished = new CountdownEvent(threads.Length);
            done = finished;
            if (threads.Length > 0) start.Release(threads.Length);
            Work(0);
            finished.Wait();
            done = null;
            body = null;
            if (failure is { } error) throw new AggregateException("Drawing a frame failed", error);
        }
    }

    private void Run(int worker)
    {
        while (true)
        {
            try { start.Wait(); }
            catch (ObjectDisposedException) { return; }
            if (disposed) return;
            try { Work(worker); }
            finally { done?.Signal(); }
        }
    }

    private void Work(int worker)
    {
        var work = body!;
        int item;
        while ((item = Interlocked.Increment(ref next) - 1) < count)
        {
            try
            {
                work(item, worker);
            }
            catch (Exception error)
            {
                Interlocked.CompareExchange(ref failure, error, null);
                // Stop handing out items: the frame is lost anyway.
                Interlocked.Exchange(ref next, count);
            }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lock (gate)
        {
            start.Release(threads.Length);
        }
        foreach (var thread in threads) thread.Join(TimeSpan.FromSeconds(2));
        start.Dispose();
    }
}
