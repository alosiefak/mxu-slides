using System.Globalization;
using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Automation;

/// <summary>What a trigger's row on the scheduler board shows on its right.</summary>
/// <param name="Dimmed">The name shows faint: the trigger, its folder or the snooze has it off, or it has ended.</param>
/// <param name="ChipOn">The chip reads "On" (else "Paused").</param>
/// <param name="ChipLive">The chip is green: on, and the folder and the Scheduler are on too.</param>
/// <param name="When">"Sun 9:00 AM" (its next fire), "until 10:30 AM" (snoozed), "ended Oct 3", "Missed 9:00 AM" or a spent one-off's time.</param>
/// <param name="WhenIsWarning">The When text is a missed one-off (shown orange).</param>
/// <param name="Ends">"ends Oct 31" when the series has an end date still to come.</param>
public sealed record TriggerRowStatus(bool Dimmed, bool ChipOn, bool ChipLive, string? When, bool WhenIsWarning, string? Ends);

/// <summary>
/// The rules behind the Scheduler's editor: what a new condition starts as, how the chips and
/// menus switch a trigger on and off, the one-line summaries and "fires next" texts, and how
/// typed dates and times are read. Port of the non-UI logic in Sources/SchedulerView.swift
/// (SchedulerView, TriggerDetailEditor, ConditionEditor, the snooze and end-date sheets and
/// conditionSummary). The library side (creating, duplicating, deleting) is in
/// <see cref="AutomationLibrary"/>. Weekdays are numbered as on the Mac: 1 = Sunday … 7 = Saturday.
/// </summary>
public static class ScheduleEditing
{
    // MARK: New conditions

    /// <summary>Add Condition › Weekly: Sundays at 9:00.</summary>
    public static ScheduleCondition NewWeekly(Func<string>? newId = null) => new()
    {
        Id = (newId ?? TimelineClips.NewId)(), Kind = ScheduleConditionKind.Weekly, Days = [1], TimeOfDay = "09:00",
    };

    /// <summary>Add Condition › One Time: an hour from now.</summary>
    public static ScheduleCondition NewOneTime(DateTimeOffset now, TimeZoneInfo zone, Func<string>? newId = null) => new()
    {
        Id = (newId ?? TimelineClips.NewId)(), Kind = ScheduleConditionKind.OneTime,
        Date = ScheduleConditionFormat.LocalString(now.AddHours(1), zone),
    };

    /// <summary>Add Condition › Timer Reaches: the first timer, at 0:00.</summary>
    public static ScheduleCondition NewTimerReaches(string? firstTimerId, Func<string>? newId = null) => new()
    {
        Id = (newId ?? TimelineClips.NewId)(), Kind = ScheduleConditionKind.TimerReaches, TimerId = firstTimerId, TimerSeconds = 0,
    };

    /// <summary>"Weekly", "One Time", "Timer Reaches": a condition card's header.</summary>
    public static string KindLabel(ScheduleConditionKind kind) => kind switch
    {
        ScheduleConditionKind.Weekly => "Weekly",
        ScheduleConditionKind.OneTime => "One Time",
        _ => "Timer Reaches",
    };

    // MARK: Editing a condition

    /// <summary>A day button: adds or removes the weekday, keeping the days in order.</summary>
    public static void ToggleDay(ScheduleCondition condition, int weekday)
    {
        var days = new SortedSet<int>(condition.Days ?? []);
        if (!days.Remove(weekday)) days.Add(weekday);
        condition.Days = [.. days];
    }

    /// <summary>
    /// True when that weekday's button shows picked. As on the Mac, a condition with no days
    /// picked shows none (and runs every day: see <see cref="ConditionSummary"/>).
    /// </summary>
    public static bool HasDay(ScheduleCondition condition, int weekday) => condition.Days?.Contains(weekday) == true;

    /// <summary>The "At" minutes box of a timer condition (0–999), keeping the seconds.</summary>
    public static void SetTimerMinutes(ScheduleCondition condition, int minutes)
    {
        var seconds = (int)(condition.TimerSeconds ?? 0);
        condition.TimerSeconds = Math.Clamp(minutes, 0, 999) * 60 + seconds % 60;
    }

    /// <summary>The "At" seconds box of a timer condition (0–59), keeping the minutes.</summary>
    public static void SetTimerSeconds(ScheduleCondition condition, int seconds)
    {
        var total = (int)(condition.TimerSeconds ?? 0);
        condition.TimerSeconds = total / 60 * 60 + Math.Clamp(seconds, 0, 59);
    }

    /// <summary>The hint after a timer condition's time: a countdown counts what's "remaining", a count-up what has "elapsed".</summary>
    public static string TimerHint(TimerMode? mode) => mode switch
    {
        TimerMode.Countdown or TimerMode.CountdownToTime => "remaining",
        TimerMode.CountUp => "elapsed",
        _ => "",
    };

    // MARK: Editing a trigger

    /// <summary>A rename: trimmed, and ignored when nothing is left.</summary>
    public static bool Rename(ScheduleTrigger trigger, string name)
    {
        var trimmed = name.TrimmingWhitespaces();
        if (trimmed.Length == 0) return false;
        trigger.Name = trimmed;
        return true;
    }

    /// <summary>Notes: empty is stored as absent.</summary>
    public static void SetNotes(ScheduleTrigger trigger, string notes) => trigger.Notes = notes.Length == 0 ? null : notes;

    /// <summary>"Fire when": any match (the default, stored as absent) or all match.</summary>
    public static void SetLogic(ScheduleTrigger trigger, ScheduleConditionLogic logic) =>
        trigger.ConditionLogic = logic == ScheduleConditionLogic.Any ? null : logic;

    /// <summary>The "Ends" switch turned on: a week from now, at 23:59.</summary>
    public static string DefaultEnd(DateTimeOffset now, TimeZoneInfo zone) =>
        ScheduleConditionFormat.LocalString(ScheduleConditionFormat.Wall(now, zone).Date.AddDays(7).AddHours(23).AddMinutes(59));

    /// <summary>The "Ends" switch: on stores <see cref="DefaultEnd"/>, off clears the end.</summary>
    public static void SetEnds(ScheduleTrigger trigger, bool on, DateTimeOffset now, TimeZoneInfo zone) =>
        trigger.EnabledUntil = on ? DefaultEnd(now, zone) : null;

    /// <summary>Off Until… starts at tomorrow 8:00.</summary>
    public static DateTime DefaultSnoozeUntil(DateTimeOffset now, TimeZoneInfo zone) =>
        ScheduleConditionFormat.Wall(now, zone).Date.AddDays(1).AddHours(8);

    /// <summary>On Until… starts a week from now at 23:59.</summary>
    public static DateTime DefaultEndUntil(DateTimeOffset now, TimeZoneInfo zone) =>
        ScheduleConditionFormat.Wall(now, zone).Date.AddDays(7).AddHours(23).AddMinutes(59);

    /// <summary>Off Until…: the trigger pauses now and comes back by itself at that moment.</summary>
    public static void Snooze(ScheduleTrigger trigger, DateTime until) => trigger.DisabledUntil = ScheduleConditionFormat.LocalString(until);

    /// <summary>Skip Next Fire pauses until a second after the next due time; null when nothing is coming up.</summary>
    public static DateTime? SkipNextFireUntil(ScheduleTrigger trigger, DateTimeOffset now, TimeZoneInfo zone) =>
        ScheduleMath.NextDue(trigger, now, zone) is { } due ? ScheduleConditionFormat.Wall(due.AddSeconds(1), zone) : null;

    /// <summary>The trigger's Enabled menu title: "Enabled: Off", "Enabled: Paused" (snoozed) or "Enabled: On".</summary>
    public static string EnabledTitle(ScheduleTrigger trigger, DateTimeOffset now, TimeZoneInfo zone) =>
        "Enabled: " + (trigger.Enabled == false ? "Off" : ScheduleMath.Snoozed(trigger, now, zone) ? "Paused" : "On");

    /// <summary>
    /// The row's On/Paused chip. When the trigger is running normally it switches it off;
    /// otherwise it brings it back fully: on, any snooze cleared, and an end date that has
    /// passed cleared too (so it can run again).
    /// </summary>
    public static void ToggleFromChip(ScheduleTrigger trigger, DateTimeOffset now, TimeZoneInfo zone)
    {
        var enabled = trigger.Enabled ?? true;
        var snoozed = ScheduleMath.SnoozeExpiry(trigger, now, zone) is not null;
        var ended = ScheduleMath.Ended(trigger, now, zone);
        if (enabled && !snoozed && !ended)
        {
            trigger.Enabled = false;
            return;
        }
        trigger.Enabled = null;
        trigger.DisabledUntil = null;
        if (ended) trigger.EnabledUntil = null;
    }

    /// <summary>A date picked in Off Until… or On Until… must still be to come.</summary>
    public static bool IsFuture(DateTime wall, DateTimeOffset now, TimeZoneInfo zone) => ScheduleMath.Instant(wall, zone) > now;

    // MARK: What the board shows

    /// <summary>
    /// The trigger's one-line summary: each condition ("Sun Wed 9:00 AM", "Jul 20, 6:00 PM",
    /// "Sermon reaches 5:00") joined by " · " (any) or " &amp; " (all), then how many actions.
    /// </summary>
    public static string ConditionSummary(ScheduleTrigger trigger, Func<string, string?> timerName, TimeZoneInfo zone, CultureInfo culture)
    {
        if (trigger.Conditions.Count == 0) return "No conditions";
        var parts = trigger.Conditions.Select(condition =>
        {
            switch (condition.Kind)
            {
                case ScheduleConditionKind.Weekly:
                {
                    var days = condition.Days is { Count: > 0 } chosen
                        ? string.Join(' ', chosen.Where(d => d is >= 1 and <= 7).Select(d => culture.DateTimeFormat.AbbreviatedDayNames[d - 1]))
                        : "Every day";
                    return $"{days} {TimeOfDayText(condition.TimeOfDay, culture)}";
                }
                case ScheduleConditionKind.OneTime:
                {
                    if (condition.Date is not { } raw || ScheduleMath.ParseLocalDate(raw, zone) is not { } date) return "One time: not set";
                    return ScheduleConditionFormat.MonthDayTime(ScheduleConditionFormat.Wall(date, zone), culture,
                        withSeconds: ScheduleConditionFormat.Seconds(raw) > 0);
                }
                default:
                {
                    var name = condition.TimerId is { } id ? timerName(id) : null;
                    var seconds = (int)(condition.TimerSeconds ?? 0);
                    return $"{name ?? "missing timer"} reaches {SlideActions.MinutesSeconds(seconds)}";
                }
            }
        });
        var summary = string.Join(trigger.ConditionLogic == ScheduleConditionLogic.All ? " & " : " · ", parts);
        var count = trigger.Actions.Count;
        return count == 0 ? summary + " · no actions" : summary + $" · {count.ToString(CultureInfo.InvariantCulture)} action{(count == 1 ? "" : "s")}";
    }

    /// <summary>
    /// A weekly condition's time as people read it ("9:00 AM", with seconds when it has them;
    /// "any time" when none is set). The Mac shows the current time for an unreadable one;
    /// Windows says so instead.
    /// </summary>
    public static string TimeOfDayText(string? raw, CultureInfo culture)
    {
        if (raw is null) return "any time";
        if (ScheduleConditionFormat.ParseTime(raw) is not { } time || time.Hour is < 0 or > 23 || time.Minute is < 0 or > 59 || time.Second is < 0 or > 59)
            return "invalid time";
        var wall = new DateTime(2000, 1, 1, time.Hour, time.Minute, time.Second);
        return ScheduleConditionFormat.Seconds(raw) > 0 ? ScheduleConditionFormat.LongTime(wall, culture) : ScheduleConditionFormat.ShortTime(wall, culture);
    }

    /// <summary>
    /// A trigger row's right-hand side. In order of importance: ended, snoozed ("until …"),
    /// its next fire, or (for a spent one-off) its time, marked "Missed" when the run log says so.
    /// </summary>
    public static TriggerRowStatus RowStatus(
        ScheduleTrigger trigger, bool folderOn, bool schedulerOn, DateTimeOffset? upcoming,
        IReadOnlyList<Scheduler.RunRecord> history, DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture)
    {
        var enabled = trigger.Enabled ?? true;
        var snoozedUntil = ScheduleMath.SnoozeExpiry(trigger, now, zone);
        var plannedEnd = ScheduleMath.PlannedEnd(trigger, zone);
        var ended = plannedEnd is { } end && now >= end;
        var running = enabled && snoozedUntil is null && !ended;
        var dimmed = !(running && folderOn);
        string? when = null;
        var warning = false;
        if (ended)
        {
            when = $"ended {ScheduleConditionFormat.ShortMonthDay(ScheduleConditionFormat.Wall(plannedEnd!.Value, zone), culture)}";
        }
        else if (snoozedUntil is { } until)
        {
            var wall = ScheduleConditionFormat.Wall(until, zone);
            var sameDay = wall.Date == ScheduleConditionFormat.Wall(now, zone).Date;
            when = sameDay ? $"until {ScheduleConditionFormat.ShortTime(wall, culture)}" : $"until {ScheduleConditionFormat.MonthDayTime(wall, culture)}";
        }
        else if (upcoming is { } next)
        {
            when = ScheduleConditionFormat.WeekdayTime(ScheduleConditionFormat.Wall(next, zone), culture);
        }
        else if (ScheduleMath.SpentOneOffDue(trigger, now, zone) is { } due)
        {
            warning = history.Any(r => r.TriggerId == trigger.Id && r.At >= due && r.Outcome == Scheduler.RunOutcome.Missed);
            var time = ScheduleConditionFormat.ShortTime(ScheduleConditionFormat.Wall(due, zone), culture);
            when = warning ? $"Missed {time}" : time;
        }
        var ends = !ended && plannedEnd is { } endsAt
            ? $"ends {ScheduleConditionFormat.ShortMonthDay(ScheduleConditionFormat.Wall(endsAt, zone), culture)}"
            : null;
        return new TriggerRowStatus(dimmed, running, running && folderOn && schedulerOn, when, warning, ends);
    }

    /// <summary>A folder's count: its triggers that aren't archived.</summary>
    public static int ActiveTriggerCount(ScheduleFolder folder, Func<string, ScheduleTrigger?> trigger) =>
        folder.TriggerIds.Count(id => trigger(id)?.Archived != true);

    /// <summary>The line under the board's title: off, the next fire, or nothing coming up.</summary>
    public static string StatusLine(bool schedulerOn, Scheduler.UpcomingFire? next, TimeZoneInfo zone, CultureInfo culture)
    {
        if (!schedulerOn) return "Off — triggers author normally, nothing fires.";
        if (next is null) return "No upcoming time triggers.";
        return $"Next: {next.Name} · {ScheduleConditionFormat.WeekdayTime(ScheduleConditionFormat.Wall(next.Date, zone), culture)}";
    }

    /// <summary>
    /// The detail card's "fires next" line, worked out from the trigger's own conditions (so it
    /// shows even while the Scheduler or the trigger is off, with the reason it won't fire).
    /// Windows-only: the Mac shows the next time on the board row only.
    /// </summary>
    public static string NextFireLine(ScheduleTrigger trigger, bool folderOn, bool schedulerOn, DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture)
    {
        if (trigger.Archived == true) return "Done: this one-time trigger has run and is in the History.";
        if (trigger.Conditions.Count == 0) return "Add a condition to say when this runs.";
        var reason = !schedulerOn ? "the Scheduler is off"
            : trigger.Enabled == false ? "this trigger is off"
            : !folderOn ? "its folder is off"
            : ScheduleMath.Ended(trigger, now, zone) ? "its end date has passed"
            : ScheduleMath.SnoozeExpiry(trigger, now, zone) is { } until
                ? $"it's paused until {ScheduleConditionFormat.MonthDayTime(ScheduleConditionFormat.Wall(until, zone), culture)}"
                : null;
        var due = ScheduleMath.NextDue(trigger, now, zone);
        if (due is { } next && ScheduleMath.PlannedEnd(trigger, zone) is { } end && next >= end) due = null;
        string text;
        if (due is { } at)
        {
            text = $"Fires next: {When(at, now, zone, culture)}";
        }
        else
        {
            var timers = trigger.Conditions.Any(c => c.Kind == ScheduleConditionKind.TimerReaches);
            text = timers ? "Fires when its timer reaches the time." : "Nothing coming up.";
        }
        return reason is null ? text : $"{text} Won't fire now: {reason}.";
    }

    /// <summary>"Sun Oct 26, 9:00 AM (tomorrow)": a coming time with how far off it is.</summary>
    public static string When(DateTimeOffset at, DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture)
    {
        var wall = ScheduleConditionFormat.Wall(at, zone);
        var days = (wall.Date - ScheduleConditionFormat.Wall(now, zone).Date).Days;
        var relative = days switch
        {
            0 => "today",
            1 => "tomorrow",
            > 1 => $"in {days.ToString(CultureInfo.InvariantCulture)} days",
            _ => null,
        };
        var text = $"{wall.ToString("ddd", culture)} {ScheduleConditionFormat.MonthDayTime(wall, culture, withSeconds: wall.Second > 0)}";
        return relative is null ? text : $"{text} ({relative})";
    }

    /// <summary>A run-log line's outcome tag: "late", "missed", or nothing for a run on time.</summary>
    public static string? OutcomeTag(Scheduler.RunOutcome outcome) => outcome switch
    {
        Scheduler.RunOutcome.Late => "late",
        Scheduler.RunOutcome.Missed => "missed",
        _ => null,
    };

    /// <summary>When an archived one-off ran (its latest date), for sorting the History; the earliest possible time when it has none.</summary>
    public static DateTimeOffset SpentDate(ScheduleTrigger trigger, TimeZoneInfo zone) =>
        ScheduleMath.SpentOneOffDue(trigger, DateTimeOffset.MaxValue, zone) ?? DateTimeOffset.MinValue;

    /// <summary>The History's archived triggers, the most recently spent first.</summary>
    public static List<ScheduleTrigger> Archived(IEnumerable<ScheduleTrigger> triggers, TimeZoneInfo zone) =>
        [.. triggers.Where(t => t.Archived == true).OrderByDescending(t => SpentDate(t, zone))];

    // MARK: Reading what people type (Windows-only: the Mac uses date pickers)

    /// <summary>
    /// A typed time of day: "9", "9:30", "09:30:15", "9:30 pm", "9pm", "21.30" or "2130".
    /// Null when it isn't a time.
    /// </summary>
    public static (int Hour, int Minute, int Second)? ParseTimeInput(string text)
    {
        var t = text.Trim().ToLowerInvariant().TrimEnd('.');
        bool? pm = null;
        foreach (var (suffix, isPm) in new[] { ("a.m", false), ("p.m", true), ("am", false), ("pm", true), ("a", false), ("p", true) })
        {
            if (!t.EndsWith(suffix, StringComparison.Ordinal)) continue;
            pm = isPm;
            t = t[..^suffix.Length].Trim();
            break;
        }
        // "21.30" reads like "21:30".
        var parts = t.Replace('.', ':').Split(':');
        if (parts.Length is < 1 or > 3 || parts.Any(p => p.Length == 0 || !p.All(char.IsAsciiDigit))) return null;
        int hour, minute = 0, second = 0;
        if (parts.Length == 1 && parts[0].Length is 3 or 4)
        {
            hour = int.Parse(parts[0][..^2], CultureInfo.InvariantCulture);
            minute = int.Parse(parts[0][^2..], CultureInfo.InvariantCulture);
        }
        else
        {
            if (parts.Any(p => p.Length > 2)) return null;
            hour = int.Parse(parts[0], CultureInfo.InvariantCulture);
            if (parts.Length > 1) minute = int.Parse(parts[1], CultureInfo.InvariantCulture);
            if (parts.Length > 2) second = int.Parse(parts[2], CultureInfo.InvariantCulture);
        }
        if (pm is { } afternoon)
        {
            if (hour is < 1 or > 12) return null;
            hour = afternoon ? (hour == 12 ? 12 : hour + 12) : (hour == 12 ? 0 : hour);
        }
        if (hour is < 0 or > 23 || minute is < 0 or > 59 || second is < 0 or > 59) return null;
        return (hour, minute, second);
    }

    /// <summary>A time of day for a text box, in the region's style ("9:00 AM", "09:00"; seconds only when there are some).</summary>
    public static string TimeInputText(int hour, int minute, int second, CultureInfo culture)
    {
        var wall = new DateTime(2000, 1, 1, hour, minute, second);
        return second > 0 ? ScheduleConditionFormat.LongTime(wall, culture) : ScheduleConditionFormat.ShortTime(wall, culture);
    }

    /// <summary>
    /// A typed date: "2026-10-25", the region's short date ("25/10/2026", "10/25/2026"),
    /// "today" or "tomorrow". Null when it isn't a date.
    /// </summary>
    public static DateTime? ParseDateInput(string text, DateTime today, CultureInfo culture)
    {
        var t = text.Trim();
        if (t.Equals("today", StringComparison.OrdinalIgnoreCase)) return today.Date;
        if (t.Equals("tomorrow", StringComparison.OrdinalIgnoreCase)) return today.Date.AddDays(1);
        if (DateTime.TryParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso)) return iso.Date;
        if (DateTime.TryParseExact(t, culture.DateTimeFormat.ShortDatePattern, culture, DateTimeStyles.None, out var local)) return local.Date;
        return DateTime.TryParse(t, culture, DateTimeStyles.None, out var loose) && loose.TimeOfDay == TimeSpan.Zero ? loose.Date : null;
    }

    /// <summary>A date for a text box, in the region's short style.</summary>
    public static string DateInputText(DateTime date, CultureInfo culture) => date.ToString(culture.DateTimeFormat.ShortDatePattern, culture);

    /// <summary>The date written out in full ("Sunday, 25 October 2026"), shown beside the box so a typed date can be checked.</summary>
    public static string DateConfirmation(DateTime date, CultureInfo culture) => date.ToString(culture.DateTimeFormat.LongDatePattern, culture);
}
