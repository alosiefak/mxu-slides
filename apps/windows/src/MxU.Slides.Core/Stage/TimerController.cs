using System.Globalization;
using System.Text.Json.Serialization;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Threading;

namespace MxU.Slides.Core.Stage;

/// <summary>A user timer as saved between launches: its setup, never its running state.</summary>
public sealed record PersistedTimer(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("durationSeconds")] double DurationSeconds,
    [property: JsonPropertyName("hour")] int? Hour = null,
    [property: JsonPropertyName("minute")] int? Minute = null,
    [property: JsonPropertyName("warnings")] IReadOnlyList<TimerWarning>? Warnings = null);

/// <summary>
/// Owns the show's timers and their transport (start, pause, reset, configure).
/// Port of the Mac app's TimersController minus the timer board, undo and storage:
/// <see cref="Changed"/> fires after every edit so the app can publish
/// <see cref="Timers"/> into <see cref="ConfidenceInfo.Timers"/> and save <see cref="Export"/>.
/// Not thread-safe; drive it from the UI thread.
/// </summary>
public sealed class TimerController(TimeProvider? clock = null)
{
    /// <summary>Ids of the app's own service-tracking timers, which users cannot edit.</summary>
    public const string PermanentIdPrefix = "service_tracking.";

    private readonly List<TimerSnapshot> timers = [];

    public TimeProvider Clock { get; } = clock ?? ShowTimeProvider.Shared;

    public IReadOnlyList<TimerSnapshot> Timers => timers;

    public event Action? Changed;

    private DateTimeOffset Now => Clock.GetUtcNow();

    public static bool IsPermanent(string id) => id.StartsWith(PermanentIdPrefix, StringComparison.Ordinal);

    public TimerSnapshot? Snapshot(string id) => timers.FirstOrDefault(t => t.Id == id);

    public List<TimerSnapshot> Snapshots(IEnumerable<string> ids) =>
        ids.Select(Snapshot).OfType<TimerSnapshot>().ToList();

    /// <summary>Replaces the service-tracking timers (permanent ids), keeping the user's timers first.</summary>
    public void SetSystemTimers(IEnumerable<TimerSnapshot> system)
    {
        var merged = timers.Where(t => !IsPermanent(t.Id)).Concat(system).ToList();
        if (merged.SequenceEqual(timers)) return;
        timers.Clear();
        timers.AddRange(merged);
        Push();
    }

    /// <summary>Adds or replaces a timer by id, idle, as a show file or remote command describes it.</summary>
    public void ImportTimer(string id, string name, TimerMode mode, double durationSeconds = 0, DateTimeOffset? targetTime = null)
    {
        if (IsPermanent(id)) return;
        var snapshot = new TimerSnapshot(
            name, mode, id: id,
            durationSeconds: Math.Max(0, durationSeconds),
            targetTime: targetTime,
            armedAt: mode == TimerMode.CountdownToTime ? Now : null);
        var existing = timers.FindIndex(t => t.Id == id);
        if (existing >= 0) timers[existing] = snapshot;
        else timers.Add(snapshot);
        Push();
    }

    public string AddCountdown(string name = "Countdown", double seconds = 600) =>
        Add(new TimerSnapshot(name, TimerMode.Countdown, durationSeconds: Math.Max(0, seconds)));

    public string AddCountdownToTime(int hour, int minute, string? name = null) =>
        Add(new TimerSnapshot(
            name ?? $"Until {WallLabel(hour, minute)}", TimerMode.CountdownToTime,
            targetTime: NextOccurrence(hour, minute), armedAt: Now));

    public string AddCountUp(string name = "Count Up", double limitSeconds = 0) =>
        Add(new TimerSnapshot(name, TimerMode.CountUp, durationSeconds: Math.Max(0, limitSeconds)));

    private string Add(TimerSnapshot snapshot)
    {
        timers.Add(snapshot);
        Push();
        return snapshot.Id;
    }

    public void SetLimit(string id, double seconds) => Mutate(id, t => t with { DurationSeconds = Math.Max(0, seconds) });

    /// <summary>Stores the warnings latest-first (largest remaining time first).</summary>
    public void SetWarnings(string id, IEnumerable<TimerWarning> warnings)
    {
        var sorted = warnings.OrderByDescending(w => w.RemainingSeconds).ToList();
        Mutate(id, t => t with { Warnings = sorted });
    }

    public void Rename(string id, string name)
    {
        var trimmed = TrimSpaces(name);
        if (trimmed.Length == 0) return;
        Mutate(id, t => t with { Name = trimmed });
    }

    /// <summary>A new length restarts the count; a running timer keeps running from now.</summary>
    public void SetDuration(string id, double seconds) => Mutate(id, t => t with
    {
        DurationSeconds = Math.Max(0, seconds),
        Banked = 0,
        RunningSince = t.IsRunning ? Now : null,
    });

    public void SetTarget(string id, int hour, int minute) =>
        Mutate(id, t => t with { TargetTime = NextOccurrence(hour, minute), ArmedAt = Now });

    public void Remove(string id)
    {
        if (IsPermanent(id)) return;
        if (timers.RemoveAll(t => t.Id == id) > 0) Push();
    }

    /// <summary>
    /// Edits a timer's setup from the inspector or a slide action. Changing mode (or a
    /// countdown's length) restarts the count; an hour sets a countdown-to-time's target.
    /// </summary>
    public void Configure(string id, TimerMode? mode = null, double? durationSeconds = null, int? hour = null, int? minute = null) =>
        Mutate(id, t =>
        {
            var newMode = mode ?? t.Mode;
            var modeChanged = newMode != t.Mode;
            t = t with { Mode = newMode };
            if (durationSeconds is { } duration) t = t with { DurationSeconds = Math.Max(0, duration) };
            if (newMode == TimerMode.CountdownToTime)
            {
                if (hour is { } h) t = t with { TargetTime = NextOccurrence(h, minute ?? 0) };
                if (modeChanged || hour is not null) t = t with { ArmedAt = Now };
            }
            if (modeChanged || (newMode == TimerMode.Countdown && durationSeconds is not null))
                t = t with { Banked = 0, RunningSince = t.IsRunning ? Now : null };
            return t;
        });

    public void Start(string id) => Mutate(id, t => t.IsRunning ? t : t with { IsRunning = true, RunningSince = Now });

    /// <summary>Banks the time run so far; resuming continues from it.</summary>
    public void Pause(string id) => Mutate(id, t =>
        !t.IsRunning ? t : t with { Banked = t.Elapsed(Now), IsRunning = false, RunningSince = null });

    public void Toggle(string id)
    {
        if (Snapshot(id) is not { } timer) return;
        if (timer.IsRunning) Pause(id);
        else Start(id);
    }

    /// <summary>Back to the start, stopped. A countdown-to-time re-arms for the next occurrence of its wall time.</summary>
    public void Reset(string id) => Mutate(id, t =>
    {
        t = t with { Banked = 0, IsRunning = false, RunningSince = null };
        if (t.Mode == TimerMode.CountdownToTime && t.TargetTime is { } target)
        {
            var wall = TimeZoneInfo.ConvertTime(target, Clock.LocalTimeZone);
            t = t with { TargetTime = NextOccurrence(wall.Hour, wall.Minute), ArmedAt = Now };
        }
        return t;
    });

    private void Mutate(string id, Func<TimerSnapshot, TimerSnapshot> change)
    {
        if (IsPermanent(id)) return;
        var index = timers.FindIndex(t => t.Id == id);
        if (index < 0) return;
        timers[index] = change(timers[index]);
        Push();
    }

    private void Push() => Changed?.Invoke();

    /// <summary>The user timers' setup to save; warnings are left out while they are the defaults.</summary>
    public List<PersistedTimer> Export() => timers.Where(t => !IsPermanent(t.Id)).Select(t =>
    {
        var wall = t.TargetTime is { } target ? TimeZoneInfo.ConvertTime(target, Clock.LocalTimeZone) : (DateTimeOffset?)null;
        return new PersistedTimer(
            t.Id, t.Name, ModeKey(t.Mode), t.DurationSeconds, wall?.Hour, wall?.Minute,
            t.Warnings.SequenceEqual(TimerWarning.Defaults) ? null : t.Warnings);
    }).ToList();

    /// <summary>Replaces the user timers with saved ones, idle; countdowns-to-time re-arm for their next wall time.</summary>
    public void Restore(IEnumerable<PersistedTimer> saved)
    {
        var restored = new List<TimerSnapshot>();
        foreach (var config in saved)
        {
            if (ModeForKey(config.Mode) is not { } mode || IsPermanent(config.Id)) continue;
            restored.Add(new TimerSnapshot(
                config.Name, mode, id: config.Id,
                durationSeconds: config.DurationSeconds,
                targetTime: config.Hour is { } hour ? NextOccurrence(hour, config.Minute ?? 0) : null,
                armedAt: config.Hour is not null ? Now : null,
                warnings: config.Warnings ?? TimerWarning.Defaults));
        }
        var system = timers.Where(t => IsPermanent(t.Id)).ToList();
        timers.Clear();
        timers.AddRange(restored);
        timers.AddRange(system);
        Push();
    }

    public static string ModeKey(TimerMode mode) => mode switch
    {
        TimerMode.CountdownToTime => "countdownToTime",
        TimerMode.CountUp => "countUp",
        _ => "countdown",
    };

    public static TimerMode? ModeForKey(string key) => key switch
    {
        "countdown" => TimerMode.Countdown,
        "countdownToTime" => TimerMode.CountdownToTime,
        "countUp" or "elapsed" => TimerMode.CountUp,
        _ => null,
    };

    /// <summary>The next hour:minute wall time strictly after now, in the clock's local time zone.</summary>
    public DateTimeOffset NextOccurrence(int hour, int minute) => NextOccurrence(hour, minute, Now, Clock.LocalTimeZone);

    /// <summary>
    /// The first instant strictly after <paramref name="after"/> whose local time reads hour:minute:00.
    /// A time skipped by a daylight-saving jump lands on the first time after the gap.
    /// </summary>
    public static DateTimeOffset NextOccurrence(int hour, int minute, DateTimeOffset after, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(after, zone).DateTime;
        for (var day = 0; day <= 2; day++)
        {
            var wall = local.Date.AddDays(day).AddHours(hour).AddMinutes(minute);
            foreach (var candidate in Instants(wall, zone))
                if (candidate > after) return candidate;
        }
        return after;
    }

    private static IEnumerable<DateTimeOffset> Instants(DateTime wall, TimeZoneInfo zone)
    {
        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(wall))
        {
            var next = wall;
            while (zone.IsInvalidTime(next)) next = next.AddMinutes(1);
            yield return new DateTimeOffset(next, zone.GetUtcOffset(next));
        }
        else if (zone.IsAmbiguousTime(wall))
        {
            foreach (var offset in zone.GetAmbiguousTimeOffsets(wall).OrderByDescending(o => o))
                yield return new DateTimeOffset(wall, offset);
        }
        else
            yield return new DateTimeOffset(wall, zone.GetUtcOffset(wall));
    }

    public static string WallLabel(int hour, int minute)
    {
        var twelve = hour % 12 == 0 ? 12 : hour % 12;
        return string.Create(CultureInfo.InvariantCulture, $"{twelve}:{minute:00} {(hour < 12 ? "AM" : "PM")}");
    }

    /// <summary>The live timer nearest its end; an unlimited Count Up ranks last. Port of TimerBoard.featured.</summary>
    public static TimerSnapshot? Featured(IEnumerable<TimerSnapshot> timers, DateTimeOffset at)
    {
        TimerSnapshot? best = null;
        var bestRemaining = double.PositiveInfinity;
        foreach (var timer in timers.Where(t => t.IsLive))
        {
            var remaining = timer.Mode == TimerMode.CountUp
                ? timer.DurationSeconds > 0 ? timer.DurationSeconds - timer.Elapsed(at) : double.PositiveInfinity
                : timer.Value(at);
            if (best is null || remaining < bestRemaining) (best, bestRemaining) = (timer, remaining);
        }
        return best;
    }

    /// <summary>The worst urgency among the timers. Port of TimerBoard.rollupUrgency.</summary>
    public static TimerUrgency RollupUrgency(IEnumerable<TimerSnapshot> timers, DateTimeOffset at)
    {
        var rollup = TimerUrgency.Normal;
        foreach (var timer in timers)
        {
            switch (timer.Urgency(at))
            {
                case TimerUrgency.Overrun: return TimerUrgency.Overrun;
                case TimerUrgency.Warning: rollup = TimerUrgency.Warning; break;
            }
        }
        return rollup;
    }

    /// <summary>The most severe active warning among the timers. Port of TimerBoard.rollupWarning.</summary>
    public static TimerWarning? RollupWarning(IEnumerable<TimerSnapshot> timers, DateTimeOffset at)
    {
        TimerWarning? best = null;
        foreach (var timer in timers)
            if (timer.ActiveWarning(at) is { } warning && (best is null || warning.RemainingSeconds < best.Value.RemainingSeconds))
                best = warning;
        return best;
    }

    /// <summary>Trims spaces and tabs but not newlines, like Swift's trimmingCharacters(in: .whitespaces).</summary>
    private static string TrimSpaces(string text)
    {
        static bool Space(char c) => c == '\t' || char.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;
        int start = 0, end = text.Length;
        while (start < end && Space(text[start])) start++;
        while (end > start && Space(text[end - 1])) end--;
        return text[start..end];
    }
}
