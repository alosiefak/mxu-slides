namespace MxU.Slides.Core.Diagnostics;

/// <summary>
/// A cap on how many lines of one kind go into the log each minute, so a storm of hitches can't
/// fill it; the next line let through says how many were dropped. Port of the Mac's
/// PerfLineLimiter.swift.
/// </summary>
public sealed class PerfLineLimiter(int perMinute)
{
    private readonly Dictionary<string, (double Start, int Admitted, int Dropped)> windows = [];

    public int PerMinute { get; } = perMinute;

    /// <summary>The number dropped since the last line let through, or null when this one is dropped too.</summary>
    public int? Admit(string kind, double now)
    {
        var window = windows.TryGetValue(kind, out var found) ? found : (Start: now, Admitted: 0, Dropped: 0);
        if (now - window.Start >= 60) window = (now, 0, window.Dropped);
        int? result = null;
        if (window.Admitted < PerMinute)
        {
            window.Admitted++;
            result = window.Dropped;
            window.Dropped = 0;
        }
        else
        {
            window.Dropped++;
        }
        windows[kind] = window;
        return result;
    }

    public static string Annotate(string detail, int dropped) => dropped > 0 ? $"{detail} (+{dropped} dropped)" : detail;
}

/// <summary>
/// Notices the UI thread getting stuck. A watcher on another thread asks the UI thread to answer a
/// "ping" a few times a second and tells this what happens:
/// <list type="bullet">
/// <item>an answer that took over a quarter of a second is a <b>hitch</b> (logged, at most six a
/// minute): the Mac's watchdog notes the same, "main thread blocked 312ms";</item>
/// <item>no answer for <see cref="HangAfter"/> is a <b>hang</b>: reported once, at once, so the
/// watcher can save every thread's call stack while it's still stuck;</item>
/// <item>the answer that finally comes ends the hang, with how long it lasted.</item>
/// </list>
/// Times are seconds on any steady clock. It only does sums, so it's tested without threads; the
/// watcher (the app's HangWatch.cs) does the waiting, and never on the UI thread itself.
/// </summary>
public sealed class HangDetector
{
    public static readonly TimeSpan HitchAfter = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan HangAfter = TimeSpan.FromSeconds(5);

    public const int HitchesPerMinute = 6;

    private readonly PerfLineLimiter limiter = new(HitchesPerMinute);
    private double? pingSentAt;
    private double? hangSince;

    public abstract record Event
    {
        /// <summary>The UI thread was busy this long, then answered.</summary>
        public sealed record Hitch(TimeSpan Blocked, int Dropped) : Event;

        /// <summary>No answer since <see cref="Since"/>: it's stuck now.</summary>
        public sealed record HangBegan(double Since) : Event;

        /// <summary>It answered at last, after <see cref="Lasted"/>.</summary>
        public sealed record HangEnded(TimeSpan Lasted) : Event;
    }

    /// <summary>True while a ping is waiting for its answer (the watcher sends no other until it's answered).</summary>
    public bool Waiting => pingSentAt is not null;

    public bool IsHung => hangSince is not null;

    public void PingSent(double at) => pingSentAt ??= at;

    /// <summary>The ping was answered at <paramref name="at"/>.</summary>
    public Event? Answered(double at)
    {
        if (pingSentAt is not { } sent) return null;
        pingSentAt = null;
        var blocked = TimeSpan.FromSeconds(Math.Max(0, at - sent));
        if (hangSince is { } since)
        {
            hangSince = null;
            return new Event.HangEnded(TimeSpan.FromSeconds(at - since));
        }
        if (blocked < HitchAfter) return null;
        return limiter.Admit("hitch", at) is { } dropped ? new Event.Hitch(blocked, dropped) : null;
    }

    /// <summary>Called while waiting: reports the hang once it has gone on for <see cref="HangAfter"/>.</summary>
    public Event? Check(double now)
    {
        if (pingSentAt is not { } sent || hangSince is not null) return null;
        if (now - sent < HangAfter.TotalSeconds) return null;
        hangSince = sent;
        return new Event.HangBegan(sent);
    }
}
