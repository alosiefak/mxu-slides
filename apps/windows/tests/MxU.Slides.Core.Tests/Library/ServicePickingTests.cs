using System.Globalization;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Library;

// Ported from PresenterCoreTests/ServiceMenuLogicTests.swift (the date column), plus the picker's
// Upcoming / Earlier grouping (AppShell.groupLocalServices, which the Mac tests through its UI)
// and Windows' plan-time picker over ServiceRunOrder's ported rules.
public class ServicePickingTests
{
    private static Service S(string id, string date, string? name = null) => new() { Id = id, Name = name ?? id, ServiceDate = date };

    [Fact]
    public void ServiceDateColumnFormatsIsoDates()
    {
        Assert.Equal("Jul 19, 2026", ServicePicking.DateColumnLabel("2026-07-19", CultureInfo.GetCultureInfo("en-US")));
        Assert.Equal("19 Jul 2026", ServicePicking.DateColumnLabel("2026-07-19", CultureInfo.GetCultureInfo("en-GB")));
    }

    [Fact]
    public void ServiceDateColumnSaysNoDateForUndatedAndMalformed()
    {
        var us = CultureInfo.GetCultureInfo("en-US");
        Assert.Equal("No Date", ServicePicking.DateColumnLabel("", us));
        Assert.Equal("No Date", ServicePicking.DateColumnLabel("not-a-date", us));
        Assert.Equal("No Date", ServicePicking.DateColumnLabel("2026-07", us));
        Assert.Equal("No Date", ServicePicking.DateColumnLabel("2026-13-40", us));
    }

    [Fact]
    public void IsoParseRoundTrips()
    {
        Assert.Equal(new DateOnly(2026, 7, 19), ServicePicking.ParseIsoDate("2026-07-19"));
        Assert.Null(ServicePicking.ParseIsoDate(""));
        Assert.Equal("2026-07-19", ServicePicking.Today(new DateTime(2026, 7, 19, 23, 59, 0)));
    }

    [Fact]
    public void UpcomingComesFirstInDateOrderThenUndatedThenEarlierNewestFirst()
    {
        var groups = ServicePicking.Group(
        [
            S("last-week", "2026-07-12"), S("next-week", "2026-07-26"), S("today", "2026-07-19"), S("Youth 10", ""),
            S("Youth 2", ""), S("long-ago", "2025-12-25"),
        ], today: "2026-07-19");
        Assert.Equal(["today", "next-week", "Youth 2", "Youth 10"], groups.Current.Select(s => s.Id)); // numbers in names sort as numbers
        Assert.Equal(["last-week", "long-ago"], groups.Earlier.Select(s => s.Id));
    }

    [Fact]
    public void ShowMorePagesByTwelve()
    {
        Assert.Equal((12, 30), ServicePicking.More(42, 12));
        Assert.Equal((3, 3), ServicePicking.More(15, 12));
    }

    private static ServiceItem Row(string id, string? hex = null, params string[] excludedFrom) =>
        new() { Id = id, ItemKind = ServiceItemKind.Presentation, Name = id, RefId = id, MxuItemHexId = hex, ExcludedTimeHexIds = excludedFrom.Length > 0 ? [.. excludedFrom] : null };

    private static Service Planned() => new()
    {
        Id = "svc", Name = "Sunday", ServiceDate = "2026-07-19",
        Items = [Row("welcome", "h-w"), Row("baptism", "h-b", "nine"), Row("sermon", "h-s"), Row("local")],
        MxuPlanTimes =
        [
            new() { HexId = "nine", Name = "9:00", StartsAt = "2026-07-19T09:00:00Z" },
            new() { HexId = "eleven", Name = "Second Service", StartsAt = "2026-07-19T11:00:00Z" },
        ],
        MxuTimeOrders = [new() { TimeHexId = "eleven", ItemHexIds = ["h-s", "h-w", "h-b"] }],
    };

    [Fact]
    public void APlanTimeLeavesRowsOutAndRunsItsOwnOrder()
    {
        var service = Planned();
        Assert.Equal(2, ServicePlanTimes.Times(service).Count);
        Assert.Equal(["welcome", "baptism", "sermon", "local"], ServicePlanTimes.Rows(service, null).Select(i => i.Id));
        Assert.Equal(["welcome", "sermon", "local"], ServicePlanTimes.Rows(service, "nine").Select(i => i.Id));
        // 11:00 runs the sermon first; the local row follows the planned row it came after.
        Assert.Equal(["sermon", "local", "welcome", "baptism"], ServicePlanTimes.Rows(service, "eleven").Select(i => i.Id));
        Assert.Equal(1, ServicePlanTimes.LeftOut(service, "nine"));
        Assert.Equal(0, ServicePlanTimes.LeftOut(service, "eleven"));
    }

    [Fact]
    public void TheStoredTimeCountsOnlyWhileTheServiceHasIt()
    {
        var service = Planned();
        Assert.Equal("eleven", ServicePlanTimes.Chosen(service, "eleven")?.HexId);
        Assert.Null(ServicePlanTimes.Chosen(service, "gone"));
        Assert.Null(ServicePlanTimes.Chosen(service, null));
        service.MxuPlanTimes = [service.MxuPlanTimes![0]];
        Assert.Empty(ServicePlanTimes.Times(service)); // one time is no choice
        Assert.Null(ServicePlanTimes.Chosen(service, "nine"));
    }

    [Fact]
    public void ATimesLabelHasItsClock()
    {
        var us = CultureInfo.GetCultureInfo("en-US");
        var times = Planned().MxuPlanTimes!;
        Assert.Equal("9:00 AM", ServicePlanTimes.Label(times[0], TimeZoneInfo.Utc, us));
        Assert.Equal("Second Service · 11:00 AM", ServicePlanTimes.Label(times[1], TimeZoneInfo.Utc, us));
        Assert.Equal("Run-through (Rehearsal)", ServicePlanTimes.Label(new MxUPlanTime { HexId = "r", Name = "Run-through", IsRehearsal = true }, TimeZoneInfo.Utc, us));
    }
}
