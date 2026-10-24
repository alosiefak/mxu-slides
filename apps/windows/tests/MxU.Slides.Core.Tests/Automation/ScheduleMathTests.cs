using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Automation;

// Ported from PresenterCoreTests/ScheduleMathTests.swift (a Gregorian calendar in
// America/Chicago becomes that TimeZoneInfo). 2026-07-12 is a Sunday (weekday 1).
public class ScheduleMathTests
{
    internal static readonly TimeZoneInfo Chicago = Zone("America/Chicago", "Central Standard Time");

    internal static TimeZoneInfo Zone(string iana, string windows)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(iana); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById(windows); }
    }

    internal static DateTimeOffset Date(int year, int month, int day, int hour, int minute, TimeZoneInfo? zone = null)
    {
        var wall = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(wall, (zone ?? Chicago).GetUtcOffset(wall));
    }

    private static ScheduleCondition Weekly(List<int>? days, string time, string id = "c") =>
        new() { Id = id, Kind = ScheduleConditionKind.Weekly, Days = days, TimeOfDay = time };

    private static ScheduleCondition OneTime(string raw, string id = "c") => new() { Id = id, Kind = ScheduleConditionKind.OneTime, Date = raw };

    private static ScheduleTrigger Trigger(string id, List<ScheduleCondition> conditions, bool? enabled = null) =>
        new() { Id = id, Name = id, Conditions = conditions, Actions = [], Enabled = enabled };

    private static DateTimeOffset Local(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Chicago);

    [Fact]
    public void WeeklyNextDueFindsTheComingSunday()
    {
        var due = ScheduleMath.NextDue(Weekly([1], "09:00"), Date(2026, 7, 8, 12, 0), Chicago);
        Assert.Equal(Date(2026, 7, 12, 9, 0), due);
    }

    [Fact]
    public void SecondsCarryThroughDueAndSatisfied()
    {
        var after = Date(2026, 7, 8, 12, 0);
        Assert.Equal(Date(2026, 7, 12, 8, 52).AddSeconds(30), ScheduleMath.NextDue(Weekly([1], "08:52:30"), after, Chicago));

        var condition = Weekly([1], "08:52:30");
        Assert.False(ScheduleMath.Satisfied(condition, Date(2026, 7, 12, 8, 52).AddSeconds(29), Chicago));
        Assert.True(ScheduleMath.Satisfied(condition, Date(2026, 7, 12, 8, 52).AddSeconds(30), Chicago));

        Assert.Null(ScheduleMath.NextDue(Weekly([1], "08:52:xx"), after, Chicago));
        Assert.Equal(Date(2026, 7, 12, 8, 52).AddSeconds(30), ScheduleMath.NextDue(OneTime("2026-07-12T08:52:30", "o"), after, Chicago));
    }

    [Fact]
    public void SnoozeGatesUntilTheMomentPasses()
    {
        var snoozed = Trigger("t", [Weekly([1], "09:00")]);
        snoozed.DisabledUntil = "2026-07-12T09:00:01";
        var before = Date(2026, 7, 12, 9, 0);
        var at = Date(2026, 7, 12, 9, 0).AddSeconds(1);
        Assert.True(ScheduleMath.Snoozed(snoozed, before, Chicago));
        Assert.False(ScheduleMath.Snoozed(snoozed, at, Chicago));
        Assert.Equal(at, ScheduleMath.SnoozeExpiry(snoozed, before, Chicago));
        Assert.Null(ScheduleMath.SnoozeExpiry(snoozed, at, Chicago));

        var broken = Trigger("t", [Weekly([1], "09:00")]);
        broken.DisabledUntil = "not-a-date";
        Assert.False(ScheduleMath.Snoozed(broken, before, Chicago));
        Assert.False(ScheduleMath.Snoozed(Trigger("t2", [Weekly([1], "09:00")]), before, Chicago));
    }

    [Fact]
    public void PlannedEndClosesTheGateForGood()
    {
        var series = Trigger("t", [Weekly([1], "09:00")]);
        series.EnabledUntil = "2026-07-12T23:59:59";
        var before = Date(2026, 7, 12, 9, 0);
        var at = Date(2026, 7, 12, 23, 59).AddSeconds(59);
        Assert.False(ScheduleMath.Ended(series, before, Chicago));
        Assert.True(ScheduleMath.Ended(series, at, Chicago));
        Assert.True(ScheduleMath.Ended(series, Date(2026, 8, 1, 0, 0), Chicago));

        var broken = Trigger("t", [Weekly([1], "09:00")]);
        broken.EnabledUntil = "not-a-date";
        Assert.False(ScheduleMath.Ended(broken, at, Chicago));
        Assert.False(ScheduleMath.Ended(Trigger("t2", [Weekly([1], "09:00")]), at, Chicago));
    }

    [Fact]
    public void SeriesCompletedOnTheFinalFireOnly()
    {
        var series = Trigger("t", [Weekly([1], "09:00")]);
        series.EnabledUntil = "2026-07-19T23:59:59";
        Assert.False(ScheduleMath.SeriesCompleted(series, Date(2026, 7, 12, 9, 0), Chicago));
        Assert.True(ScheduleMath.SeriesCompleted(series, Date(2026, 7, 19, 9, 0), Chicago));
        Assert.False(ScheduleMath.SeriesCompleted(Trigger("t2", [Weekly([1], "09:00")]), Date(2026, 7, 19, 9, 0), Chicago));

        var oneOff = Trigger("t3", [OneTime("2026-07-12T09:00", "o")]);
        oneOff.EnabledUntil = "2026-07-19T23:59:59";
        Assert.True(ScheduleMath.SeriesCompleted(oneOff, Date(2026, 7, 12, 9, 0), Chicago));
    }

    [Fact]
    public void ExcludedDaySkipsToTheFollowingWeek()
    {
        var condition = Weekly([1], "09:00");
        condition.ExcludedDates = ["2026-07-12"];
        Assert.Equal(Date(2026, 7, 19, 9, 0), ScheduleMath.NextDue(condition, Date(2026, 7, 8, 12, 0), Chicago));
        Assert.False(ScheduleMath.Satisfied(condition, Date(2026, 7, 12, 10, 0), Chicago));
        Assert.True(ScheduleMath.Satisfied(condition, Date(2026, 7, 19, 10, 0), Chicago));
    }

    [Fact]
    public void ConsecutiveExclusionsAndJunkEntriesStillResolve()
    {
        var condition = Weekly([1], "09:00");
        condition.ExcludedDates = ["2026-07-12", "2026-07-19", "christmas"];
        Assert.Equal(Date(2026, 7, 26, 9, 0), ScheduleMath.NextDue(condition, Date(2026, 7, 8, 12, 0), Chicago));
    }

    [Fact]
    public void WeeklySameDayLaterTimeStaysToday()
    {
        var condition = Weekly([1], "09:00");
        Assert.Equal(Date(2026, 7, 12, 9, 0), ScheduleMath.NextDue(condition, Date(2026, 7, 12, 8, 0), Chicago));
        Assert.Equal(Date(2026, 7, 19, 9, 0), ScheduleMath.NextDue(condition, Date(2026, 7, 12, 9, 30), Chicago));
    }

    [Fact]
    public void WeeklyDueMomentItselfIsStrictlyAfter()
    {
        Assert.Equal(Date(2026, 7, 19, 9, 0), ScheduleMath.NextDue(Weekly([1], "09:00"), Date(2026, 7, 12, 9, 0), Chicago));
    }

    [Fact]
    public void WeeklyMultipleDaysPickTheEarliest()
    {
        Assert.Equal(Date(2026, 7, 9, 19, 0), ScheduleMath.NextDue(Weekly([1, 5], "19:00"), Date(2026, 7, 8, 12, 0), Chicago));
    }

    [Fact]
    public void WeeklyAbsentDaysMeansEveryDay()
    {
        Assert.Equal(Date(2026, 7, 8, 13, 0), ScheduleMath.NextDue(Weekly(null, "13:00"), Date(2026, 7, 8, 12, 0), Chicago));
    }

    [Fact]
    public void WeeklyRidesDstBothDirections()
    {
        var condition = Weekly([1], "09:00");

        var springDue = ScheduleMath.NextDue(condition, Date(2026, 3, 7, 12, 0), Chicago);
        Assert.Equal(Date(2026, 3, 8, 9, 0), springDue);
        Assert.Equal((9, 0), (Local(springDue!.Value).Hour, Local(springDue.Value).Minute));

        var fallDue = ScheduleMath.NextDue(condition, Date(2026, 10, 31, 12, 0), Chicago);
        Assert.Equal(Date(2026, 11, 1, 9, 0), fallDue);
        Assert.Equal((9, 0), (Local(fallDue!.Value).Hour, Local(fallDue.Value).Minute));
    }

    [Fact]
    public void WeeklyNonexistentSpringForwardTimeStillResolves()
    {
        var due = ScheduleMath.NextDue(Weekly([1], "02:30"), Date(2026, 3, 7, 12, 0), Chicago);
        Assert.NotNull(due);
        Assert.Equal((3, 8), (Local(due.Value).Month, Local(due.Value).Day));
    }

    [Fact]
    public void OneTimeFiresOnceAndNeverRematches()
    {
        var condition = OneTime("2026-07-20T18:00");
        Assert.Equal(Date(2026, 7, 20, 18, 0), ScheduleMath.NextDue(condition, Date(2026, 7, 20, 17, 0), Chicago));
        Assert.Null(ScheduleMath.NextDue(condition, Date(2026, 7, 20, 18, 0), Chicago));
    }

    [Fact]
    public void BrokenConditionsAreNoOpsNeverErrors()
    {
        var after = Date(2026, 7, 8, 12, 0);
        ScheduleCondition[] conditions =
        [
            new() { Id = "c1", Kind = ScheduleConditionKind.Weekly },
            Weekly([9], "09:00", "c2"),
            Weekly([1], "25:99", "c3"),
            OneTime("not a date", "c4"),
            new() { Id = "c5", Kind = ScheduleConditionKind.TimerReaches, TimerId = "t", TimerSeconds = 0 },
        ];
        foreach (var condition in conditions) Assert.Null(ScheduleMath.NextDue(condition, after, Chicago));
    }

    [Fact]
    public void SweepFiresOnTimeAndDedupesAgainstLastDue()
    {
        var sunday = Trigger("t1", [Weekly([1], "09:00")]);
        var dueAt = Date(2026, 7, 12, 9, 0);

        var result = ScheduleMath.Sweep([sunday], _ => true, new Dictionary<string, DateTimeOffset>(), Date(2026, 7, 12, 8, 59), dueAt, zone: Chicago);
        Assert.Equal([new ScheduleMath.Firing("t1", dueAt, false)], result.Firings);
        Assert.Empty(result.Missed);
        Assert.Equal(dueAt, result.LastDue["t1"]);

        result = ScheduleMath.Sweep([sunday], _ => true, result.LastDue, Date(2026, 7, 12, 8, 59), dueAt.AddSeconds(5), zone: Chicago);
        Assert.Empty(result.Firings);
        Assert.Equal(Date(2026, 7, 19, 9, 0), result.NextWake);
    }

    [Fact]
    public void SweepGraceBoundaries()
    {
        var sunday = Trigger("t1", [Weekly([1], "09:00")]);
        var dueAt = Date(2026, 7, 12, 9, 0);

        var result = ScheduleMath.Sweep([sunday], _ => true, new Dictionary<string, DateTimeOffset>(), dueAt.AddSeconds(-3600), dueAt.AddSeconds(59), zone: Chicago);
        Assert.Equal([new ScheduleMath.Firing("t1", dueAt, true)], result.Firings);

        result = ScheduleMath.Sweep([sunday], _ => true, new Dictionary<string, DateTimeOffset>(), dueAt.AddSeconds(-3600), dueAt.AddSeconds(61), zone: Chicago);
        Assert.Empty(result.Firings);
        Assert.Single(result.Missed);
        Assert.Equal(dueAt, result.LastDue["t1"]);
    }

    [Fact]
    public void SweepWalksEveryDueInTheWindow()
    {
        var daily = Trigger("t1", [Weekly(null, "09:00")]);
        var now = Date(2026, 7, 12, 9, 0);
        var result = ScheduleMath.Sweep([daily], _ => true, new Dictionary<string, DateTimeOffset>(), Date(2026, 7, 9, 12, 0), now, zone: Chicago);
        Assert.Equal([Date(2026, 7, 10, 9, 0), Date(2026, 7, 11, 9, 0)], result.Missed.Select(f => f.DueAt));
        Assert.Equal([now], result.Firings.Select(f => f.DueAt));
        Assert.Equal(now, result.LastDue["t1"]);
    }

    [Fact]
    public void SweepGateClosedConsumesSilently()
    {
        var sunday = Trigger("t1", [Weekly([1], "09:00")]);
        var dueAt = Date(2026, 7, 12, 9, 0);

        var result = ScheduleMath.Sweep([sunday], _ => false, new Dictionary<string, DateTimeOffset>(), dueAt.AddSeconds(-3600), dueAt.AddSeconds(10), zone: Chicago);
        Assert.Empty(result.Firings);
        Assert.Empty(result.Missed);
        Assert.Equal(dueAt, result.LastDue["t1"]);
        Assert.Null(result.NextWake);

        result = ScheduleMath.Sweep([sunday], _ => true, result.LastDue, dueAt.AddSeconds(-3600), dueAt.AddSeconds(20), zone: Chicago);
        Assert.Empty(result.Firings);
    }

    [Fact]
    public void SweepNextWakeIsTheEarliestAmongOpenTriggers()
    {
        var sunday = Trigger("t1", [Weekly([1], "09:00")]);
        var thursday = Trigger("t2", [Weekly([5], "19:00")]);
        var paused = Trigger("t3", [Weekly([4], "06:00")], enabled: false);
        var now = Date(2026, 7, 8, 12, 0);
        var result = ScheduleMath.Sweep([sunday, thursday, paused], t => t.Enabled ?? true, new Dictionary<string, DateTimeOffset>(), now, now, zone: Chicago);
        Assert.Equal(Date(2026, 7, 9, 19, 0), result.NextWake);
    }

    [Fact]
    public void SweepOrdersSimultaneousFiringsByDue()
    {
        var a = Trigger("a", [Weekly(null, "09:00")]);
        var b = Trigger("b", [Weekly(null, "08:59")]);
        var result = ScheduleMath.Sweep([a, b], _ => true, new Dictionary<string, DateTimeOffset>(), Date(2026, 7, 12, 8, 58), Date(2026, 7, 12, 9, 0), zone: Chicago);
        Assert.Equal(["b", "a"], result.Firings.Select(f => f.TriggerId));
    }

    [Fact]
    public void SatisfiedReadsTimeConditionsAsStates()
    {
        var now = Date(2026, 7, 12, 10, 0);
        Assert.True(ScheduleMath.Satisfied(Weekly([1], "09:00"), now, Chicago));
        Assert.False(ScheduleMath.Satisfied(Weekly([1], "11:00"), now, Chicago));
        Assert.False(ScheduleMath.Satisfied(Weekly([2], "09:00"), now, Chicago));

        var dayOnly = new ScheduleCondition { Id = "c", Kind = ScheduleConditionKind.Weekly, Days = [1] };
        Assert.True(ScheduleMath.Satisfied(dayOnly, now, Chicago));
        Assert.False(ScheduleMath.Satisfied(dayOnly, Date(2026, 7, 13, 10, 0), Chicago));

        Assert.True(ScheduleMath.Satisfied(OneTime("2026-07-01T08:00"), now, Chicago));
        Assert.False(ScheduleMath.Satisfied(Weekly([1], "nope"), now, Chicago));
    }

    [Fact]
    public void AllLogicGatesTheFiringMomentOnSiblings()
    {
        var trigger = new ScheduleTrigger
        {
            Id = "t1", Name = "t1",
            Conditions = [Weekly(null, "09:00", "time"), new() { Id = "gate", Kind = ScheduleConditionKind.Weekly, Days = [1] }],
            Actions = [], ConditionLogic = ScheduleConditionLogic.All,
        };
        var sunday = Date(2026, 7, 12, 9, 0);
        var result = ScheduleMath.Sweep([trigger], _ => true, new Dictionary<string, DateTimeOffset>(), Date(2026, 7, 12, 8, 59), sunday, zone: Chicago);
        Assert.Single(result.Firings);
        Assert.Empty(result.Gated);

        var monday = Date(2026, 7, 13, 9, 0);
        result = ScheduleMath.Sweep([trigger], _ => true, result.LastDue, Date(2026, 7, 13, 8, 59), monday, zone: Chicago);
        Assert.Empty(result.Firings);
        Assert.Single(result.Gated);
        Assert.Equal(monday, result.LastDue["t1"]);

        trigger.ConditionLogic = null;
        result = ScheduleMath.Sweep([trigger], _ => true, new Dictionary<string, DateTimeOffset>(), Date(2026, 7, 13, 8, 59), monday, zone: Chicago);
        Assert.Single(result.Firings);
    }

    [Fact]
    public void AllLogicAsksTheTimerClosureForTimerSiblings()
    {
        var trigger = new ScheduleTrigger
        {
            Id = "t1", Name = "t1",
            Conditions = [Weekly(null, "09:00", "time"), new() { Id = "timer", Kind = ScheduleConditionKind.TimerReaches, TimerId = "x", TimerSeconds = 0 }],
            Actions = [], ConditionLogic = ScheduleConditionLogic.All,
        };
        var now = Date(2026, 7, 12, 9, 0);
        var closed = ScheduleMath.Sweep([trigger], _ => true, new Dictionary<string, DateTimeOffset>(), Date(2026, 7, 12, 8, 59), now, zone: Chicago, timerSatisfied: _ => false);
        Assert.True(closed.Firings.Count == 0 && closed.Gated.Count == 1);
        var open = ScheduleMath.Sweep([trigger], _ => true, new Dictionary<string, DateTimeOffset>(), Date(2026, 7, 12, 8, 59), now, zone: Chicago, timerSatisfied: _ => true);
        Assert.True(open.Firings.Count == 1 && open.Gated.Count == 0);
    }

    [Fact]
    public void LogicGateExcludesTheFiringConditionItself()
    {
        var condition = new ScheduleCondition { Id = "timer", Kind = ScheduleConditionKind.TimerReaches, TimerId = "x", TimerSeconds = 0 };
        var trigger = new ScheduleTrigger { Id = "t1", Name = "t1", Conditions = [condition], Actions = [], ConditionLogic = ScheduleConditionLogic.All };
        Assert.True(ScheduleMath.LogicGateOpen(trigger, condition, Date(2026, 7, 12, 9, 0), Chicago, _ => false));
    }

    [Fact]
    public void CountdownCrossingFiresOncePerPass()
    {
        Assert.True(ScheduleMath.CountdownCrossed(5, 0, 0));
        Assert.False(ScheduleMath.CountdownCrossed(0, 0, 0));
        Assert.False(ScheduleMath.CountdownCrossed(0, -3, 0));
        Assert.True(ScheduleMath.CountdownCrossed(2, -1, 0));
        Assert.False(ScheduleMath.CountdownCrossed(10, 5, 0));
    }

    [Fact]
    public void CountUpCrossingMirrors()
    {
        Assert.True(ScheduleMath.CountUpCrossed(58, 60, 60));
        Assert.False(ScheduleMath.CountUpCrossed(60, 61, 60));
        Assert.False(ScheduleMath.CountUpCrossed(10, 20, 60));
    }

    [Fact]
    public void SpentOneOffNeedsEveryDatePassed()
    {
        var now = Date(2026, 8, 10, 12, 0);
        var spent = Trigger("t", [OneTime("2026-08-09T18:00", "c1"), OneTime("2026-08-10T09:30", "c2")]);
        Assert.Equal(Date(2026, 8, 10, 9, 30), ScheduleMath.SpentOneOffDue(spent, now, Chicago));

        var pending = Trigger("t", [OneTime("2026-08-09T18:00", "c1"), OneTime("2026-08-10T19:00", "c2")]);
        Assert.Null(ScheduleMath.SpentOneOffDue(pending, now, Chicago));
    }

    [Fact]
    public void RecurringAndBrokenConditionsNeverSpend()
    {
        var now = Date(2026, 8, 10, 12, 0);
        List<ScheduleCondition>[] cases =
        [
            [OneTime("2026-08-09T18:00", "c1"), Weekly([1], "09:00", "c2")],
            [OneTime("2026-08-09T18:00", "c1"), new() { Id = "c2", Kind = ScheduleConditionKind.TimerReaches, TimerId = "t1", TimerSeconds = 0 }],
            [OneTime("not a date")],
            [],
        ];
        foreach (var conditions in cases) Assert.Null(ScheduleMath.SpentOneOffDue(Trigger("t", conditions), now, Chicago));
    }

    [Fact]
    public void SpentnessFlipsExactlyAtTheDueMoment()
    {
        var oneOff = Trigger("t", [OneTime("2026-08-10T09:30")]);
        Assert.Null(ScheduleMath.SpentOneOffDue(oneOff, Date(2026, 8, 10, 9, 0), Chicago));
        Assert.Equal(Date(2026, 8, 10, 9, 30), ScheduleMath.SpentOneOffDue(oneOff, Date(2026, 8, 10, 9, 31), Chicago));
    }

    // C#: the wall-time rules Foundation's Calendar gave the Mac, spelled out.
    [Fact]
    public void LocalDatesFollowFoundationAcrossTheClockChanges()
    {
        // Spring forward: 02:30 doesn't exist on 2026-03-08; a stored one-off at that time reads an hour later.
        Assert.Equal(Date(2026, 3, 8, 3, 30), ScheduleMath.ParseLocalDate("2026-03-08T02:30", Chicago));
        // A weekly time in the gap fires as the clocks jump (03:00).
        Assert.Equal(Date(2026, 3, 8, 3, 0), ScheduleMath.NextDue(Weekly([1], "02:30"), Date(2026, 3, 7, 12, 0), Chicago));
        // Fall back: 01:30 happens twice on 2026-11-01; both read as the first (CDT), and a weekly fires once.
        var first = new DateTimeOffset(2026, 11, 1, 1, 30, 0, TimeSpan.FromHours(-5));
        Assert.Equal(first, ScheduleMath.ParseLocalDate("2026-11-01T01:30", Chicago));
        Assert.Equal(first, ScheduleMath.NextDue(Weekly([1], "01:30"), Date(2026, 10, 31, 12, 0), Chicago));
        Assert.Equal(first.AddDays(7).AddHours(1), ScheduleMath.NextDue(Weekly([1], "01:30"), first, Chicago));
        // Out-of-range days roll over; separators and junk don't parse.
        Assert.Equal(Date(2026, 8, 1, 9, 0), ScheduleMath.ParseLocalDate("2026-07-32T09:00", Chicago));
        Assert.Null(ScheduleMath.ParseLocalDate("2026-07-12 09:00", Chicago));
        Assert.Null(ScheduleMath.ParseLocalDate("2026-07-12T9", Chicago));
        Assert.Equal("2026-07-12", ScheduleMath.LocalDayString(Date(2026, 7, 12, 23, 59), Chicago));
        Assert.Equal(1, ScheduleMath.Weekday(Date(2026, 7, 12, 0, 0), Chicago));
    }
}
