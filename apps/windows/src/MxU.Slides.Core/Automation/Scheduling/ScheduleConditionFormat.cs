using System.Globalization;

namespace MxU.Slides.Core.Automation;

/// <summary>
/// How a schedule condition's times are written down. A trigger stores wall-clock times as
/// text, the same on both apps: a time of day "09:00" (or "09:00:30" when the seconds
/// matter) and a local date-time "2026-07-20T18:00" (or with seconds). Port of
/// ScheduleConditionFormat at the end of Sources/SchedulerView.swift, with the Mac's
/// <c>Date</c> replaced by a plain wall-clock <see cref="DateTime"/> in a time zone.
/// </summary>
public static class ScheduleConditionFormat
{
    /// <summary>The pieces of "HH:MM" or "HH:MM:SS"; null when it isn't two or three whole numbers.</summary>
    public static (int Hour, int Minute, int Second)? ParseTime(string raw)
    {
        var parts = SwiftParse.Split(raw, c => c == ':');
        if (parts.Count is < 2 or > 3 || SwiftParse.Int(parts[0]) is not { } hour || SwiftParse.Int(parts[1]) is not { } minute) return null;
        return (hour, minute, parts.Count == 3 ? SwiftParse.Int(parts[2]) ?? 0 : 0);
    }

    /// <summary>"09:00", or "09:00:30" when there are seconds (seconds of zero are left off, as on the Mac).</summary>
    public static string TimeOfDay(int hour, int minute, int second)
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"{hour:00}:{minute:00}");
        return second > 0 ? text + string.Create(CultureInfo.InvariantCulture, $":{second:00}") : text;
    }

    /// <summary>The seconds in a stored time or date-time ("…T18:00:30" → 30); 0 when there are none.</summary>
    public static int Seconds(string? raw)
    {
        if (raw is null) return 0;
        var time = SwiftParse.Split(raw, c => c == 'T').LastOrDefault() ?? raw;
        var parts = SwiftParse.Split(time, c => c == ':');
        return parts.Count == 3 && SwiftParse.Int(parts[2]) is { } second ? second : 0;
    }

    /// <summary>A wall-clock time as stored: "2026-07-20T18:00", or "2026-07-20T18:00:30" with seconds.</summary>
    public static string LocalString(DateTime wall)
    {
        var text = wall.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);
        return wall.Second > 0 ? text + wall.ToString(":ss", CultureInfo.InvariantCulture) : text;
    }

    /// <summary>An instant as stored, written as the wall-clock time it is in the zone.</summary>
    public static string LocalString(DateTimeOffset instant, TimeZoneInfo zone) => LocalString(Wall(instant, zone));

    /// <summary>The wall-clock time an instant is in the zone.</summary>
    public static DateTime Wall(DateTimeOffset instant, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(instant, zone).DateTime;

    // MARK: Showing times to people (the Mac's Date.formatted styles)

    /// <summary>"9:00 AM" (or "09:00", by the computer's region).</summary>
    public static string ShortTime(DateTime wall, CultureInfo culture) => wall.ToString(culture.DateTimeFormat.ShortTimePattern, culture);

    /// <summary>"9:00:30 AM".</summary>
    public static string LongTime(DateTime wall, CultureInfo culture) => wall.ToString(culture.DateTimeFormat.LongTimePattern, culture);

    /// <summary>"Jul 20" (or "20 Jul"): the region's month-and-day pattern with the month shortened.</summary>
    public static string ShortMonthDay(DateTime wall, CultureInfo culture)
    {
        var pattern = culture.DateTimeFormat.MonthDayPattern;
        pattern = pattern.Contains("MMMM", StringComparison.Ordinal) ? pattern.Replace("MMMM", "MMM", StringComparison.Ordinal) : "MMM d";
        return wall.ToString(pattern, culture);
    }

    /// <summary>"Sun 9:00 AM" (the Mac's weekday-hour-minute style).</summary>
    public static string WeekdayTime(DateTime wall, CultureInfo culture) => $"{wall.ToString("ddd", culture)} {ShortTime(wall, culture)}";

    /// <summary>"Jul 20, 6:00 PM", with the seconds when <paramref name="withSeconds"/>.</summary>
    public static string MonthDayTime(DateTime wall, CultureInfo culture, bool withSeconds = false) =>
        $"{ShortMonthDay(wall, culture)}, {(withSeconds ? LongTime(wall, culture) : ShortTime(wall, culture))}";
}
