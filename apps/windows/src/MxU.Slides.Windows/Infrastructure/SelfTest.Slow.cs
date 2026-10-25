using global::Windows.Foundation;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>
/// Running on a slow PC (GitHub's test machine has no graphics card, so WPF draws every window
/// in software, and no sound device). The checks wait for what they look for, with a generous
/// limit, rather than sleeping a fixed time (<see cref="Until"/>, <see cref="WhenShown"/>), and
/// compare clocks against the time that really passed. Windows' own media calls get a time limit
/// here, so one that never returns becomes a SKIP instead of a run that hangs.
/// </summary>
public sealed partial class SelfTest
{
    /// <summary>How long Windows may take to make a test video before the checks that need it are skipped.</summary>
    private static readonly TimeSpan TestVideoTimeLimit = TimeSpan.FromSeconds(90);

    /// <summary>Why the checks that need a test video were skipped, when Windows' encoder ran out of time.</summary>
    private static string TestVideoTimedOut =>
        $"Windows' video encoder didn't finish making the test video within {TestVideoTimeLimit.TotalSeconds:0} s on this PC";

    // A Windows (WinRT) call with a time limit: when the limit passes the call is asked to stop,
    // and the test stops waiting for it either way (OperationCanceledException), even if it never answers.
    private static Task<T> Limited<T>(IAsyncOperation<T> operation, CancellationToken limit) =>
        operation.AsTask(limit).WaitAsync(limit);

    private static Task<T> Limited<T, TProgress>(IAsyncOperationWithProgress<T, TProgress> operation, CancellationToken limit) =>
        operation.AsTask(limit).WaitAsync(limit);
}
