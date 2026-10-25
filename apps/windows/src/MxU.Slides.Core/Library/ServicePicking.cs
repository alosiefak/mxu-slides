using System.Globalization;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Library;

/// <summary>
/// The service picker: this week's service first. Services dated today or later come in date
/// order, then the undated ones by name; everything earlier waits under "Earlier", newest first.
/// Long lists show twelve at a time with "Show 12 more". Port of the Mac's
/// ServiceMenuLogic.swift (the date column) and AppShell's groupLocalServices and paging.
/// </summary>
public static class ServicePicking
{
    /// <summary>How many rows each group shows before "Show more" (the Mac's servicePage).</summary>
    public const int Page = 12;

    /// <summary>The picker's two groups.</summary>
    public sealed record Groups(IReadOnlyList<Service> Current, IReadOnlyList<Service> Earlier);

    /// <summary>Splits services around today's date ("yyyy-MM-dd", the way services store it).</summary>
    public static Groups Group(IEnumerable<Service> services, string today)
    {
        var all = services.ToList();
        var dated = all.Where(s => s.ServiceDate.Length > 0).ToList();
        var undated = all.Where(s => s.ServiceDate.Length == 0)
            .OrderBy(s => s.Name, StringComparer.Create(CultureInfo.CurrentCulture, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering));
        var upcoming = dated.Where(s => string.CompareOrdinal(s.ServiceDate, today) >= 0)
            .OrderBy(s => s.ServiceDate, StringComparer.Ordinal).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase);
        var earlier = dated.Where(s => string.CompareOrdinal(s.ServiceDate, today) < 0)
            .OrderByDescending(s => s.ServiceDate, StringComparer.Ordinal).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase);
        return new Groups([.. upcoming, .. undated], [.. earlier]);
    }

    /// <summary>Today as a service date, on this PC's calendar.</summary>
    public static string Today(DateTime now) => now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>How many a "Show more" row adds, and how many are left after it.</summary>
    public static (int Adds, int Remaining) More(int total, int shown) => (Math.Min(total - shown, Page), total - shown);

    /// <summary>"2026-07-19" as a date, or null when it isn't one.</summary>
    public static DateOnly? ParseIsoDate(string iso)
    {
        var parts = iso.Split('-');
        if (parts.Length != 3 || !parts.All(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _))) return null;
        try { return new DateOnly(int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture), int.Parse(parts[2], CultureInfo.InvariantCulture)); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>
    /// The date column: "Jul 19, 2026" (or "19 Jul 2026", the way this PC writes dates), and
    /// "No Date" for an undated or unreadable one.
    /// </summary>
    public static string DateColumnLabel(string serviceDate, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (ParseIsoDate(serviceDate) is not { } date) return "No Date";
        // Month first where this PC writes "July 19" (the US), day first elsewhere.
        var monthFirst = culture.DateTimeFormat.MonthDayPattern.TrimStart().StartsWith('M');
        return date.ToString(monthFirst ? "MMM d, yyyy" : "d MMM yyyy", culture);
    }
}

/// <summary>
/// Plan times: a service imported from Planning Center can have several (9:00 and 11:00), each
/// leaving some rows out or running them in its own order. The rules are ported in
/// <see cref="ServiceRunOrder"/>; this is the picker's half, which the Mac doesn't have yet (it
/// always runs every row in the service's order). Which time a PC runs is its own choice, kept in
/// its settings, so the 9:00 desk and the 11:00 desk can each run theirs. Windows-only.
/// </summary>
public static class ServicePlanTimes
{
    /// <summary>The service's times (rehearsals too), or none when it has fewer than two.</summary>
    public static IReadOnlyList<MxUPlanTime> Times(Service service) => service.MxuPlanTimes is { Count: > 1 } times ? times : [];

    /// <summary>The time this PC runs: the stored one if the service still has it, else none (every row, in the service's order).</summary>
    public static MxUPlanTime? Chosen(Service service, string? stored) =>
        stored is null ? null : Times(service).FirstOrDefault(t => t.HexId == stored);

    /// <summary>The order the chosen time runs its plan items in, or null for the service's own order.</summary>
    public static IReadOnlyList<string>? Order(Service service, string? timeHexId) =>
        timeHexId is null ? null : service.MxuTimeOrders?.FirstOrDefault(o => o.TimeHexId == timeHexId)?.ItemHexIds;

    /// <summary>The rows this PC runs, in order: every row the time keeps (hidden ones too, for the Hidden group), in the time's order.</summary>
    public static List<ServiceItem> Rows(Service service, string? timeHexId) =>
        ServiceRunOrder.Ordered([.. service.Items.Where(i => i.AppliesToTime(timeHexId))], Order(service, timeHexId));

    /// <summary>How many rows the time leaves out (for the chip's tooltip).</summary>
    public static int LeftOut(Service service, string? timeHexId) => service.Items.Count(i => !i.AppliesToTime(timeHexId));

    /// <summary>
    /// A time's name in the picker: its own name, with its start ("9:00 AM", in this PC's way of
    /// writing times) when the plan has one, and "Rehearsal" for a rehearsal.
    /// </summary>
    public static string Label(MxUPlanTime time, TimeZoneInfo? zone = null, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var label = time.Name.Trim();
        if (time.StartsAt is { } starts && DateTimeOffset.TryParse(starts, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
        {
            var local = TimeZoneInfo.ConvertTime(at, zone ?? TimeZoneInfo.Local);
            // Some cultures put a narrow no-break space before "AM": a plain one reads the same and fits menus.
            var clock = local.ToString("t", culture).Replace(' ', ' ').Replace(' ', ' ');
            // A time called "9:00" (Planning Center's usual name) reads as its clock alone.
            label = label.Length == 0 || clock.StartsWith(label, StringComparison.OrdinalIgnoreCase) ? clock : $"{label} · {clock}";
        }
        if (label.Length == 0) label = "Time";
        return time.IsRehearsal == true ? $"{label} (Rehearsal)" : label;
    }
}
