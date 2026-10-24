using System.Globalization;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Automation;

/// <summary>
/// When schedule triggers are due. A trigger has conditions (weekly at a time on some
/// days, once at a date, or when a timer reaches a value), any of which fires it, or
/// with "all" logic every one of which must hold. Times are wall-clock times in a time
/// zone (the computer's, normally), so 9:00 stays 9:00 across daylight saving.
/// <see cref="Sweep"/> walks every due time between two instants and sorts each into
/// fire, missed (more than <see cref="MisfireGrace"/> late, e.g. the computer was asleep)
/// or gated (the "all" logic was closed). Port of PresenterCore/Library/ScheduleMath.swift,
/// with Foundation's Gregorian <c>Calendar</c> replaced by a <see cref="TimeZoneInfo"/>.
/// Weekdays are numbered as on the Mac: 1 = Sunday … 7 = Saturday.
/// </summary>
public static class ScheduleMath
{
    /// <summary>A due time missed by more than this many seconds is recorded as missed instead of fired late.</summary>
    public const double MisfireGrace = 60;

    /// <summary>"HH:MM" or "HH:MM:SS".</summary>
    internal static (int Hour, int Minute, int Second)? ParseTimeOfDay(string raw)
    {
        var parts = SwiftParse.Split(raw, c => c == ':');
        if (parts.Count is < 2 or > 3 || SwiftParse.Int(parts[0]) is not { } hour || SwiftParse.Int(parts[1]) is not { } minute
            || hour is < 0 or > 23 || minute is < 0 or > 59)
            return null;
        var second = parts.Count == 3 ? SwiftParse.Int(parts[2]) ?? -1 : 0;
        if (second is < 0 or > 59) return null;
        return (hour, minute, second);
    }

    /// <summary>A stored local date-time ("2026-07-20T18:00" or with seconds) as an instant in the zone.</summary>
    public static DateTimeOffset? ParseLocalDate(string raw, TimeZoneInfo zone)
    {
        var parts = SwiftParse.Split(raw, c => c == 'T');
        if (parts.Count != 2) return null;
        var dateParts = SwiftParse.Split(parts[0], c => c == '-');
        if (dateParts.Count != 3 || SwiftParse.Int(dateParts[0]) is not { } year || SwiftParse.Int(dateParts[1]) is not { } month
            || SwiftParse.Int(dateParts[2]) is not { } day || ParseTimeOfDay(parts[1]) is not { } time)
            return null;
        if (year is < 1 or > 9998) return null;
        try
        {
            // Like Foundation, an out-of-range month or day rolls over rather than failing.
            var wall = new DateTime(year, 1, 1).AddMonths(month - 1).AddDays(day - 1)
                .AddHours(time.Hour).AddMinutes(time.Minute).AddSeconds(time.Second);
            return Instant(wall, zone);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    /// The instant a wall-clock time names in the zone, as Foundation's <c>calendar.date(from:)</c>
    /// reads it: a time skipped by the clocks going forward lands that much later; a time the
    /// clocks go back over is its first occurrence.
    /// </summary>
    internal static DateTimeOffset Instant(DateTime wall, TimeZoneInfo zone)
    {
        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        DateTimeOffset instant;
        if (zone.IsInvalidTime(wall)) instant = new DateTimeOffset(wall, zone.GetUtcOffset(wall.AddHours(-12)));
        else if (zone.IsAmbiguousTime(wall)) instant = new DateTimeOffset(wall, zone.GetAmbiguousTimeOffsets(wall).Max());
        else instant = new DateTimeOffset(wall, zone.GetUtcOffset(wall));
        return TimeZoneInfo.ConvertTime(instant, zone);
    }

    /// <summary>
    /// The first instant at or after a wall time that exists (<c>matchingPolicy: .nextTime</c>):
    /// a time skipped by the clocks going forward becomes the moment the clocks jump to.
    /// A time that happens twice is its first occurrence (<c>repeatedTimePolicy: .first</c>).
    /// </summary>
    private static DateTimeOffset NextTime(DateTime wall, TimeZoneInfo zone)
    {
        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        if (!zone.IsInvalidTime(wall)) return Instant(wall, zone);
        var next = new DateTime(wall.Year, wall.Month, wall.Day, wall.Hour, wall.Minute, 0, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(next)) next = next.AddMinutes(1);
        return Instant(next, zone);
    }

    private static DateTime Local(DateTimeOffset instant, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(instant, zone).DateTime;

    /// <summary>The Mac's weekday number (1 = Sunday … 7 = Saturday) of an instant in the zone.</summary>
    public static int Weekday(DateTimeOffset instant, TimeZoneInfo zone) => (int)Local(instant, zone).DayOfWeek + 1;

    /// <summary>"2026-07-12": the local day, as excluded dates are stored.</summary>
    public static string LocalDayString(DateTimeOffset date, TimeZoneInfo zone) =>
        Local(date, zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal static bool IsExcluded(DateTimeOffset date, ScheduleCondition condition, TimeZoneInfo zone) =>
        condition.ExcludedDates is { Count: > 0 } excluded && excluded.Contains(LocalDayString(date, zone));

    /// <summary>"Skip until": a snoozed trigger doesn't fire before its <see cref="ScheduleTrigger.DisabledUntil"/>.</summary>
    public static bool Snoozed(ScheduleTrigger trigger, DateTimeOffset now, TimeZoneInfo zone) =>
        trigger.DisabledUntil is { } raw && ParseLocalDate(raw, zone) is { } until && now < until;

    /// <summary>When a snooze ends (null when not snoozed), so the scheduler can wake then.</summary>
    public static DateTimeOffset? SnoozeExpiry(ScheduleTrigger trigger, DateTimeOffset now, TimeZoneInfo zone) =>
        trigger.DisabledUntil is { } raw && ParseLocalDate(raw, zone) is { } until && now < until ? until : null;

    /// <summary>"Run until": the end of a series.</summary>
    public static DateTimeOffset? PlannedEnd(ScheduleTrigger trigger, TimeZoneInfo zone) =>
        trigger.EnabledUntil is { } raw ? ParseLocalDate(raw, zone) : null;

    public static bool Ended(ScheduleTrigger trigger, DateTimeOffset now, TimeZoneInfo zone) =>
        PlannedEnd(trigger, zone) is { } end && now >= end;

    /// <summary>The fire at <paramref name="now"/> was the series' last (nothing else is due before its end).</summary>
    public static bool SeriesCompleted(ScheduleTrigger trigger, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (PlannedEnd(trigger, zone) is not { } end) return false;
        if (NextDue(trigger, now, zone) is not { } next) return true;
        return next >= end;
    }

    /// <summary>A condition's next due time strictly after <paramref name="after"/>; null for timer conditions and broken ones.</summary>
    public static DateTimeOffset? NextDue(ScheduleCondition condition, DateTimeOffset after, TimeZoneInfo zone)
    {
        switch (condition.Kind)
        {
            case ScheduleConditionKind.Weekly:
            {
                if (condition.TimeOfDay is not { } raw || ParseTimeOfDay(raw) is not { } time) return null;
                var days = condition.Days is { Count: > 0 } chosen ? chosen.Where(d => d is >= 1 and <= 7).ToList() : Enumerable.Range(1, 7).ToList();
                if (days.Count == 0) return null;
                DateTimeOffset? earliest = null;
                foreach (var weekday in days)
                {
                    var cursor = after;
                    for (var attempt = 0; attempt <= (condition.ExcludedDates?.Count ?? 0); attempt++)
                    {
                        if (NextMatch(cursor, weekday, time, zone) is not { } candidate) break;
                        if (!IsExcluded(candidate, condition, zone))
                        {
                            if (earliest is null || candidate < earliest) earliest = candidate;
                            break;
                        }
                        cursor = candidate;
                    }
                }
                return earliest;
            }
            case ScheduleConditionKind.OneTime:
                return condition.Date is { } date && ParseLocalDate(date, zone) is { } at && at > after ? at : null;
            default:
                return null;
        }
    }

    /// <summary>The next time strictly after <paramref name="after"/> on that weekday at that wall time.</summary>
    private static DateTimeOffset? NextMatch(DateTimeOffset after, int weekday, (int Hour, int Minute, int Second) time, TimeZoneInfo zone)
    {
        var local = Local(after, zone);
        for (var day = 0; day <= 8; day++)
        {
            var date = local.Date.AddDays(day);
            if ((int)date.DayOfWeek + 1 != weekday) continue;
            var candidate = NextTime(date.AddHours(time.Hour).AddMinutes(time.Minute).AddSeconds(time.Second), zone);
            if (candidate > after) return candidate;
        }
        return null;
    }

    public static DateTimeOffset? NextDue(ScheduleTrigger trigger, DateTimeOffset after, TimeZoneInfo zone) =>
        NextDueDetailed(trigger, after, zone)?.Due;

    /// <summary>The trigger's next due time and the condition that makes it due.</summary>
    internal static (DateTimeOffset Due, ScheduleCondition Condition)? NextDueDetailed(ScheduleTrigger trigger, DateTimeOffset after, TimeZoneInfo zone)
    {
        (DateTimeOffset Due, ScheduleCondition Condition)? best = null;
        foreach (var condition in trigger.Conditions)
            if (NextDue(condition, after, zone) is { } due && (best is null || due < best.Value.Due)) best = (due, condition);
        return best;
    }

    /// <summary>
    /// A condition read as a state for "all" logic: a weekly one holds on its days from its
    /// time on (all day with no time), a one-off from its date on. Timer conditions are asked separately.
    /// </summary>
    public static bool Satisfied(ScheduleCondition condition, DateTimeOffset now, TimeZoneInfo zone)
    {
        switch (condition.Kind)
        {
            case ScheduleConditionKind.Weekly:
            {
                var weekday = Weekday(now, zone);
                IReadOnlyList<int> days = condition.Days is { Count: > 0 } chosen ? chosen : [1, 2, 3, 4, 5, 6, 7];
                if (!days.Contains(weekday) || IsExcluded(now, condition, zone)) return false;
                if (condition.TimeOfDay is not { } raw) return true;
                if (ParseTimeOfDay(raw) is not { } time) return false;
                var local = Local(now, zone);
                return (local.Hour, local.Minute, local.Second).CompareTo((time.Hour, time.Minute, time.Second)) >= 0;
            }
            case ScheduleConditionKind.OneTime:
                return condition.Date is { } date && ParseLocalDate(date, zone) is { } at && now >= at;
            default:
                return false;
        }
    }

    /// <summary>
    /// "All" logic: every other condition must hold when one fires (the firing one is taken as
    /// met). With "any" logic (the default) the gate is always open.
    /// </summary>
    public static bool LogicGateOpen(
        ScheduleTrigger trigger, ScheduleCondition? firing, DateTimeOffset now, TimeZoneInfo zone, Func<ScheduleCondition, bool> timerSatisfied)
    {
        if (trigger.ConditionLogic != ScheduleConditionLogic.All) return true;
        return trigger.Conditions.All(other =>
        {
            if (firing is not null && other.Id == firing.Id) return true;
            if (other.Kind == ScheduleConditionKind.TimerReaches) return timerSatisfied(other);
            return Satisfied(other, now, zone);
        });
    }

    /// <summary>A due time a sweep found; <see cref="Late"/> when it is being run more than 1.5 s after it.</summary>
    public sealed record Firing(string TriggerId, DateTimeOffset DueAt, bool Late);

    /// <summary>
    /// What a sweep found: due times to fire (in due order), missed ones, gated ones, the
    /// updated last-due times to keep, and when to sweep next.
    /// </summary>
    public sealed record SweepResult(
        List<Firing> Firings, List<Firing> Missed, List<Firing> Gated, Dictionary<string, DateTimeOffset> LastDue, DateTimeOffset? NextWake);

    /// <summary>
    /// Walks every trigger's due times in (max(its last due, <paramref name="from"/>), <paramref name="now"/>].
    /// A trigger whose gate is closed (disabled, snoozed, its folder off) uses up its due
    /// times silently. Each due time is used once: <see cref="SweepResult.LastDue"/> remembers it.
    /// </summary>
    public static SweepResult Sweep(
        IReadOnlyList<ScheduleTrigger> triggers, Func<ScheduleTrigger, bool> gateOpen, IReadOnlyDictionary<string, DateTimeOffset> lastDue,
        DateTimeOffset from, DateTimeOffset now, double grace = MisfireGrace, TimeZoneInfo? zone = null,
        Func<ScheduleCondition, bool>? timerSatisfied = null)
    {
        zone ??= TimeZoneInfo.Local;
        timerSatisfied ??= _ => false;
        var firings = new List<Firing>();
        var missed = new List<Firing>();
        var gated = new List<Firing>();
        var due = new Dictionary<string, DateTimeOffset>(lastDue);
        DateTimeOffset? nextWake = null;
        foreach (var trigger in triggers)
        {
            var open = gateOpen(trigger);
            var cursor = due.TryGetValue(trigger.Id, out var last) && last > from ? last : from;
            var remaining = 200;
            while (remaining > 0 && NextDueDetailed(trigger, cursor, zone) is { } hit && hit.Due <= now)
            {
                remaining--;
                if (open)
                {
                    var age = (now - hit.Due).TotalSeconds;
                    if (age > grace) missed.Add(new Firing(trigger.Id, hit.Due, true));
                    else if (!LogicGateOpen(trigger, hit.Condition, now, zone, timerSatisfied)) gated.Add(new Firing(trigger.Id, hit.Due, false));
                    else firings.Add(new Firing(trigger.Id, hit.Due, age > 1.5));
                }
                due[trigger.Id] = hit.Due;
                cursor = hit.Due;
            }
            if (open && NextDue(trigger, now, zone) is { } upcoming)
                nextWake = nextWake is { } wake && wake < upcoming ? wake : upcoming;
        }
        return new SweepResult(firings.OrderBy(f => f.DueAt).ToList(), missed, gated, due, nextWake);
    }

    /// <summary>
    /// A trigger made only of one-off dates that have all passed is spent (it gets archived
    /// after firing); the latest of its dates is returned. Anything recurring never spends.
    /// </summary>
    public static DateTimeOffset? SpentOneOffDue(ScheduleTrigger trigger, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (trigger.Conditions.Count == 0) return null;
        var latest = DateTimeOffset.MinValue;
        foreach (var condition in trigger.Conditions)
        {
            if (condition.Kind != ScheduleConditionKind.OneTime || condition.Date is not { } raw
                || ParseLocalDate(raw, zone) is not { } date || date > now)
                return null;
            if (date > latest) latest = date;
        }
        return latest;
    }

    /// <summary>A countdown passing down through a value (once per pass).</summary>
    public static bool CountdownCrossed(double previous, double current, double threshold) => previous > threshold && current <= threshold;

    /// <summary>A count-up passing up through a value.</summary>
    public static bool CountUpCrossed(double previous, double current, double threshold) => previous < threshold && current >= threshold;
}
