using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Stage;

/// <summary>How a timer's readout is coloured on the Timers panel.</summary>
/// <param name="WarningHex">The active warning's colour ("#RRGGBBAA"), when one is active.</param>
/// <param name="IsLive">Running (or counting to a time): drawn green when no warning is active.</param>
public readonly record struct TimerTint(string? WarningHex, bool IsLive);

/// <summary>A wall-clock time as the panel's hour/minute/AM-PM fields hold it.</summary>
public readonly record struct WallClockFields(int Hour12, int Minute, bool IsPm)
{
    /// <summary>The fields clamped the way the Mac's boxes clamp them: hour 1–12, minute 0–59.</summary>
    public WallClockFields Clamped => new(Math.Clamp(Hour12, 1, 12), Math.Clamp(Minute, 0, 59), IsPm);

    public int Hour24 => Clamped.Hour12 % 12 + (IsPm ? 12 : 0);

    public static WallClockFields From(int hour24, int minute) =>
        new(hour24 % 12 == 0 ? 12 : hour24 % 12, minute, hour24 >= 12);
}

/// <summary>What the inline editor holds when Apply is pressed (TimerInlineEditor's state in Swift).</summary>
public sealed record TimerEdit(string Name, int Hours, int Minutes, int Seconds, bool HasLimit, WallClockFields Until);

/// <summary>
/// The Timers panel's rules: the quick lengths, the add menu's defaults, the inline editor
/// (length, limit, target time, warnings) and how rows and folders read. Port of the logic in
/// Sources/Modules/TimersModule.swift, so the WPF panel only draws.
/// </summary>
public static class TimerPanel
{
    /// <summary>The add menu's countdown lengths and the row menu's Duration choices, in minutes.</summary>
    public static readonly IReadOnlyList<int> QuickLengths = [5, 10, 15, 20, 30, 45, 60];

    /// <summary>The Duration choices for a countdown: the quick lengths, plus its own length when that isn't one of them.</summary>
    public static List<int> DurationChoices(int currentMinutes) =>
        QuickLengths.Contains(currentMinutes) || currentMinutes == 0 ? [.. QuickLengths] : [.. QuickLengths.Append(currentMinutes).Order()];

    /// <summary>"Countdown to Time…" starts at the next half hour: before :30 it offers :30, otherwise the next hour.</summary>
    public static WallClockFields CountdownToTimeDefault(DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var minute = local.Minute < 30 ? 30 : 0;
        var hour24 = minute == 0 ? (local.Hour + 1) % 24 : local.Hour;
        return WallClockFields.From(hour24, minute);
    }

    /// <summary>The colours a warning can take, by name.</summary>
    public static readonly IReadOnlyList<(string Name, string Hex)> WarningPalette =
    [
        ("Amber", TimerWarning.AmberHex),
        ("Red", TimerWarning.RedHex),
        ("Orange", "#FF8A3DFF"),
        ("Yellow", "#FFE04AFF"),
        ("Green", "#34C759FF"),
        ("Blue", "#3B82F6FF"),
        ("Purple", "#A855F7FF"),
        ("White", "#FFFFFFFF"),
    ];

    /// <summary>What + adds to a timer's warnings: amber at one minute left.</summary>
    public static readonly TimerWarning NewWarning = new(60, TimerWarning.AmberHex);

    public static string ColorName(string hex) =>
        WarningPalette.FirstOrDefault(p => string.Equals(p.Hex, hex, StringComparison.OrdinalIgnoreCase)).Name ?? "Custom";

    /// <summary>A timer with an end has warnings; an unlimited Count Up has nothing to warn against.</summary>
    public static bool ShowsWarnings(TimerSnapshot timer) => timer.Mode != TimerMode.CountUp || timer.DurationSeconds > 0;

    /// <summary>The editor's fields for a timer, as the Mac seeds them when it opens.</summary>
    public static TimerEdit Seed(TimerSnapshot timer, TimeZoneInfo zone)
    {
        var (hours, minutes, seconds) = Split(timer.DurationSeconds);
        var until = timer.TargetTime is { } target
            ? WallClockFields.From(TimeZoneInfo.ConvertTime(target, zone).Hour, TimeZoneInfo.ConvertTime(target, zone).Minute)
            : new WallClockFields(11, 0, false);
        return new TimerEdit(timer.Name, hours, minutes, seconds, timer.Mode == TimerMode.Countdown || timer.DurationSeconds > 0, until);
    }

    /// <summary>
    /// Apply (or Return in a field): renames, and sets the countdown's length, the count up's
    /// limit (0 when "Count to" is off) or the target time, each only when it changed.
    /// </summary>
    public static void Apply(TimerController controller, TimerSnapshot timer, TimerEdit edit)
    {
        if (edit.Name != timer.Name) controller.Rename(timer.Id, edit.Name);
        var total = Join(edit.Hours, edit.Minutes, edit.Seconds);
        switch (timer.Mode)
        {
            case TimerMode.Countdown:
                if (total != timer.DurationSeconds) controller.SetDuration(timer.Id, total);
                break;
            case TimerMode.CountUp:
                var limit = edit.HasLimit ? total : 0;
                if (limit != timer.DurationSeconds) controller.SetLimit(timer.Id, limit);
                break;
            case TimerMode.CountdownToTime:
                var wanted = edit.Until.Clamped;
                var current = timer.TargetTime is { } target ? TimeZoneInfo.ConvertTime(target, controller.Clock.LocalTimeZone) : (DateTimeOffset?)null;
                if (current?.Hour != wanted.Hour24 || current?.Minute != wanted.Minute)
                    controller.SetTarget(timer.Id, wanted.Hour24, wanted.Minute);
                break;
        }
    }

    /// <summary>Hours, minutes and seconds of a length (whole seconds).</summary>
    public static (int Hours, int Minutes, int Seconds) Split(double seconds)
    {
        var total = (int)Math.Max(0, Math.Truncate(seconds));
        return (total / 3600, total / 60 % 60, total % 60);
    }

    /// <summary>A length from the HH:MM:SS boxes, each clamped as the Mac's boxes clamp (hours 0–99, minutes and seconds 0–59).</summary>
    public static double Join(int hours, int minutes, int seconds) =>
        Math.Clamp(hours, 0, 99) * 3600 + Math.Clamp(minutes, 0, 59) * 60 + Math.Clamp(seconds, 0, 59);

    /// <summary>A warning's "AT mm:ss left" fields (minutes up to 999).</summary>
    public static (int Minutes, int Seconds) WarningFields(TimerWarning warning)
    {
        var total = (int)Math.Max(0, Math.Truncate(warning.RemainingSeconds));
        return (total / 60, total % 60);
    }

    public static double WarningSeconds(int minutes, int seconds) => Math.Clamp(minutes, 0, 999) * 60 + Math.Clamp(seconds, 0, 59);

    /// <summary>The warnings with one level added (+).</summary>
    public static List<TimerWarning> AddingWarning(IReadOnlyList<TimerWarning> warnings) => [.. warnings, NewWarning];

    /// <summary>The warnings with one level changed; out of range does nothing.</summary>
    public static List<TimerWarning> ChangingWarning(IReadOnlyList<TimerWarning> warnings, int index, Func<TimerWarning, TimerWarning> change)
    {
        var list = warnings.ToList();
        if (index >= 0 && index < list.Count) list[index] = change(list[index]);
        return list;
    }

    public static List<TimerWarning> RemovingWarning(IReadOnlyList<TimerWarning> warnings, int index)
    {
        var list = warnings.ToList();
        if (index >= 0 && index < list.Count) list.RemoveAt(index);
        return list;
    }

    // MARK: How rows read

    /// <summary>A service-tracking timer that isn't following anything reads "--:--".</summary>
    public static string RowText(TimerSnapshot timer, DateTimeOffset at) =>
        TimerController.IsPermanent(timer.Id) && timer.IsIdle ? "--:--" : timer.DisplayString(at);

    public static TimerTint RowTint(TimerSnapshot timer, DateTimeOffset at) =>
        TimerController.IsPermanent(timer.Id) && timer.IsIdle
            ? new TimerTint(null, false)
            : new TimerTint(timer.ActiveWarning(at)?.ColorHex, timer.IsLive);

    /// <summary>A countdown to a time runs by the clock, so it has no play/pause; service-tracking timers have neither.</summary>
    public static bool ShowsPlayPause(TimerSnapshot timer) => timer.Mode != TimerMode.CountdownToTime && !TimerController.IsPermanent(timer.Id);

    public static bool ShowsReset(TimerSnapshot timer) => !TimerController.IsPermanent(timer.Id);

    public static string ResetTooltip(TimerSnapshot timer) => timer.Mode == TimerMode.CountdownToTime ? "Re-arm to the next occurrence" : "Reset";

    /// <summary>The folder row: the featured timer's readout, tinted by the worst warning, else green while any member is live.</summary>
    public static (string? Text, TimerTint Tint) FolderReadout(IReadOnlyList<TimerSnapshot> members, DateTimeOffset at) =>
        (TimerController.Featured(members, at)?.DisplayString(at),
         new TimerTint(TimerController.RollupWarning(members, at)?.ColorHex, members.Any(t => t.IsLive)));

    /// <summary>The Service Tracking folder follows the service; it can't be renamed, deleted or filed into.</summary>
    public static bool IsEditableFolder(string folderId) => folderId != ServiceTrackingTimers.FolderId;

    /// <summary>The read-only note a service-tracking timer shows when opened; null for the operator's own timers.</summary>
    public static string? PermanentNote(string timerId) =>
        ServiceTrackingTimers.SubjectAndFace(timerId) is { } parts
            ? ServiceTrackingTimers.Description(parts.Subject, parts.Face) + " Follows Service Tracking; not editable."
            : null;
}
