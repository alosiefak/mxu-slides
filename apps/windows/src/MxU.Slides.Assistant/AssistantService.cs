using MxU.Slides.Core.Threading;

namespace MxU.Slides.Assistant;

/// <summary>
/// The app's one door to the assistant. It decides whether a job may go at all (the PC's switch
/// is on and there's a key), and runs each job on its own thread, one at a time, so neither the
/// screen nor the show's timing ever waits on the internet.
/// <para>
/// <b>Quiet when it can't work.</b> With the switch off or no key, <see cref="Unavailable"/> says
/// why and <see cref="Ask"/> fails at once without touching the network; windows use that to grey
/// their AI buttons. With no internet a job fails with a calm message and nothing else changes.
/// </para>
/// </summary>
public sealed class AssistantService : IDisposable
{
    private readonly WorkQueue queue = new("Assistant", ThreadPriority.BelowNormal);
    private readonly AssistantClient client;
    private readonly Func<bool> enabled;
    private readonly Func<string?> model;
    private int running;

    /// <param name="enabled">The PC's on/off switch (Settings › Assistant).</param>
    /// <param name="model">The chosen model's id.</param>
    /// <param name="baseUrl">Null for Anthropic; a test's stand-in server otherwise.</param>
    public AssistantService(AssistantKeyVault vault, AssistantLog log, Func<bool> enabled, Func<string?> model, Uri? baseUrl = null)
    {
        Vault = vault;
        Log = log;
        this.enabled = enabled;
        this.model = model;
        client = new AssistantClient(log, baseUrl);
    }

    public AssistantKeyVault Vault { get; }

    public AssistantLog Log { get; }

    public AssistantModels.Info Model => AssistantModels.Find(model());

    /// <summary>Why the assistant can't be used right now (off, or no key); null when it can.</summary>
    public AssistantProblem? Unavailable => !enabled() ? AssistantProblem.Off : !Vault.HasKey ? AssistantProblem.NoKey : null;

    public bool IsReady => Unavailable is null;

    /// <summary>True while a job is being sent or waited for.</summary>
    public bool IsBusy => Volatile.Read(ref running) > 0;

    /// <summary>Raised (on any thread) when a job starts or ends.</summary>
    public event Action? BusyChanged;

    /// <summary>
    /// Sends a job. The task fails with an <see cref="AssistantException"/> whose message is for
    /// the operator; it never throws anything else. Awaiting it from the UI thread comes back on
    /// the UI thread.
    /// </summary>
    public Task<AssistantReply> Ask(AssistantRequest request, CancellationToken cancel = default)
    {
        if (Unavailable is { } why) return Task.FromException<AssistantReply>(new AssistantException(why, AssistantException.Words(why)));
        var key = Vault.Load()!;
        var job = request.Model is null ? request with { Model = Model.Id } : request;
        return OnQueue(() => client.Ask(key, job, cancel));
    }

    /// <summary>Checks a key (one free call) before it's kept. Works with the switch off, so a church can test first.</summary>
    public Task TestKey(string key, CancellationToken cancel = default) =>
        OnQueue(async () =>
        {
            await client.TestKey(key, Model.Id, cancel);
            return true;
        });

    private Task<T> OnQueue<T>(Func<Task<T>> work)
    {
        // Continuations run elsewhere, never on the queue's own thread, so a slow one can't hold up the next job.
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Increment(ref running);
        BusyChanged?.Invoke();
        queue.Async(() =>
        {
            try
            {
                // The queue's thread waits here for the network; that is its whole job.
                done.SetResult(work().GetAwaiter().GetResult());
            }
            catch (Exception e)
            {
                done.SetException(e as AssistantException ?? new AssistantException(AssistantProblem.Other, AssistantException.Words(AssistantProblem.Other), e));
            }
            finally
            {
                Interlocked.Decrement(ref running);
                BusyChanged?.Invoke();
            }
        });
        return done.Task;
    }

    public void Dispose() => queue.Dispose();
}
