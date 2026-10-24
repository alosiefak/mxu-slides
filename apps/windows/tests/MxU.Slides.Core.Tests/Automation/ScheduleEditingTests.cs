using System.Globalization;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Automation;

// C#: the logic of Sources/SchedulerView.swift (ScheduleConditionFormat, conditionSummary,
// the board rows, the enable chip and menus, the condition editors and the snooze and
// end-date sheets). The Mac has no unit tests for this view file (its fields are checked by
// source sweeps), so these test the rules directly, in America/Chicago with the invariant
// culture ("HH:mm", "Jul 20") so the expected text doesn't depend on this PC's region.
// 2026-07-12 is a Sunday (weekday 1).
public class ScheduleEditingTests
{
    private static readonly TimeZoneInfo Chicago = ScheduleMathTests.Chicago;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static DateTimeOffset Date(int year, int month, int day, int hour, int minute) => ScheduleMathTests.Date(year, month, day, hour, minute);

    private static ScheduleTrigger Trigger(params ScheduleCondition[] conditions) =>
        new() { Id = "t", Name = "Walk-in", Conditions = [.. conditions], Actions = [] };

    private static ScheduleCondition Weekly(string time, params int[] days) =>
        new() { Id = "w", Kind = ScheduleConditionKind.Weekly, Days = days.Length == 0 ? null : [.. days], TimeOfDay = time };

    private static ScheduleCondition OneTime(string date) => new() { Id = "o", Kind = ScheduleConditionKind.OneTime, Date = date };

    /// <summary>Some regions put a narrow no-break space before AM/PM; tests compare plain spaces.</summary>
    private static string Plain(string text) => text.Replace(' ', ' ').Replace(' ', ' ');

    // MARK: ScheduleConditionFormat

    [Fact]
    public void TimesAreStoredAsTheMacWritesThem()
    {
        Assert.Equal((9, 0, 0), ScheduleConditionFormat.ParseTime("09:00"));
        Assert.Equal((9, 5, 30), ScheduleConditionFormat.ParseTime("09:05:30"));
        Assert.Null(ScheduleConditionFormat.ParseTime("9"));
        Assert.Null(ScheduleConditionFormat.ParseTime("a:b"));
        Assert.Equal("09:00", ScheduleConditionFormat.TimeOfDay(9, 0, 0));
        Assert.Equal("18:05:07", ScheduleConditionFormat.TimeOfDay(18, 5, 7));
        Assert.Equal(30, ScheduleConditionFormat.Seconds("2026-07-20T18:00:30"));
        Assert.Equal(0, ScheduleConditionFormat.Seconds("09:00"));
        Assert.Equal(0, ScheduleConditionFormat.Seconds(null));
        Assert.Equal("2026-07-20T18:00", ScheduleConditionFormat.LocalString(new DateTime(2026, 7, 20, 18, 0, 0)));
        Assert.Equal("2026-07-20T18:00:09", ScheduleConditionFormat.LocalString(new DateTime(2026, 7, 20, 18, 0, 9)));
        Assert.Equal("2026-07-12T09:30", ScheduleConditionFormat.LocalString(Date(2026, 7, 12, 9, 30), Chicago));
    }

    [Fact]
    public void TimesAreShownInTheRegionsStyle()
    {
        var nine = new DateTime(2026, 7, 12, 9, 0, 0);
        Assert.Equal("Sun 09:00", ScheduleConditionFormat.WeekdayTime(nine, Invariant));
        Assert.Equal("Jul 12, 09:00", ScheduleConditionFormat.MonthDayTime(nine, Invariant));
        Assert.Equal("Sun 9:00 AM", Plain(ScheduleConditionFormat.WeekdayTime(nine, CultureInfo.GetCultureInfo("en-US"))));
        Assert.Equal("Jul 12", ScheduleConditionFormat.ShortMonthDay(nine, CultureInfo.GetCultureInfo("en-US")));
        Assert.Equal("12 Jul", ScheduleConditionFormat.ShortMonthDay(nine, CultureInfo.GetCultureInfo("en-GB")));
        Assert.Equal("09:00", ScheduleConditionFormat.ShortTime(nine, CultureInfo.GetCultureInfo("en-GB")));
    }

    // MARK: Conditions

    [Fact]
    public void NewConditionsStartAsOnTheMac()
    {
        var weekly = ScheduleEditing.NewWeekly(() => "w");
        Assert.Equal((ScheduleConditionKind.Weekly, "09:00"), (weekly.Kind, weekly.TimeOfDay));
        Assert.Equal([1], weekly.Days!);
        var once = ScheduleEditing.NewOneTime(Date(2026, 7, 12, 23, 30), Chicago, () => "o");
        Assert.Equal("2026-07-13T00:30", once.Date);
        var timer = ScheduleEditing.NewTimerReaches("sermon", () => "t");
        Assert.Equal(("sermon", (double?)0), (timer.TimerId, timer.TimerSeconds));
        Assert.Null(ScheduleEditing.NewTimerReaches(null).TimerId);
        Assert.Equal("Timer Reaches", ScheduleEditing.KindLabel(ScheduleConditionKind.TimerReaches));
    }

    [Fact]
    public void DayButtonsToggleAndKeepTheDaysInOrder()
    {
        var weekly = Weekly("09:00", 1);
        ScheduleEditing.ToggleDay(weekly, 4);
        ScheduleEditing.ToggleDay(weekly, 3);
        Assert.Equal([1, 3, 4], weekly.Days!);
        ScheduleEditing.ToggleDay(weekly, 1);
        Assert.Equal([3, 4], weekly.Days!);
        Assert.True(ScheduleEditing.HasDay(weekly, 3));
        Assert.False(ScheduleEditing.HasDay(weekly, 1));

        var everyDay = Weekly("09:00");
        Assert.False(ScheduleEditing.HasDay(everyDay, 2));
        ScheduleEditing.ToggleDay(everyDay, 2);
        Assert.Equal([2], everyDay.Days!);
    }

    [Fact]
    public void ATimerConditionsMinutesAndSecondsEditOneValue()
    {
        var condition = ScheduleEditing.NewTimerReaches("t");
        ScheduleEditing.SetTimerMinutes(condition, 5);
        ScheduleEditing.SetTimerSeconds(condition, 30);
        Assert.Equal(330, condition.TimerSeconds);
        ScheduleEditing.SetTimerMinutes(condition, 2000);
        Assert.Equal(999 * 60 + 30, condition.TimerSeconds);
        ScheduleEditing.SetTimerSeconds(condition, 99);
        Assert.Equal(999 * 60 + 59, condition.TimerSeconds);
        Assert.Equal("remaining", ScheduleEditing.TimerHint(TimerMode.CountdownToTime));
        Assert.Equal("elapsed", ScheduleEditing.TimerHint(TimerMode.CountUp));
        Assert.Equal("", ScheduleEditing.TimerHint(null));
    }

    // MARK: The trigger

    [Fact]
    public void NamesNotesAndLogicAreStoredAsOnTheMac()
    {
        var trigger = Trigger(Weekly("09:00", 1));
        Assert.False(ScheduleEditing.Rename(trigger, "   "));
        Assert.True(ScheduleEditing.Rename(trigger, "  Countdown loop "));
        Assert.Equal("Countdown loop", trigger.Name);
        ScheduleEditing.SetNotes(trigger, "");
        Assert.Null(trigger.Notes);
        ScheduleEditing.SetLogic(trigger, ScheduleConditionLogic.All);
        Assert.Equal(ScheduleConditionLogic.All, trigger.ConditionLogic);
        ScheduleEditing.SetLogic(trigger, ScheduleConditionLogic.Any);
        Assert.Null(trigger.ConditionLogic);
    }

    [Fact]
    public void EndDatesAndPausesStartWhereTheMacsSheetsDo()
    {
        var now = Date(2026, 7, 12, 10, 0);
        Assert.Equal("2026-07-19T23:59", ScheduleEditing.DefaultEnd(now, Chicago));
        var trigger = Trigger(Weekly("09:00", 1));
        ScheduleEditing.SetEnds(trigger, true, now, Chicago);
        Assert.Equal("2026-07-19T23:59", trigger.EnabledUntil);
        ScheduleEditing.SetEnds(trigger, false, now, Chicago);
        Assert.Null(trigger.EnabledUntil);
        Assert.Equal(new DateTime(2026, 7, 13, 8, 0, 0), ScheduleEditing.DefaultSnoozeUntil(now, Chicago));
        Assert.Equal(new DateTime(2026, 7, 19, 23, 59, 0), ScheduleEditing.DefaultEndUntil(now, Chicago));
        ScheduleEditing.Snooze(trigger, new DateTime(2026, 7, 13, 8, 0, 0));
        Assert.Equal("2026-07-13T08:00", trigger.DisabledUntil);
        Assert.True(ScheduleEditing.IsFuture(new DateTime(2026, 7, 12, 10, 1, 0), now, Chicago));
        Assert.False(ScheduleEditing.IsFuture(new DateTime(2026, 7, 12, 9, 59, 0), now, Chicago));
    }

    [Fact]
    public void SkipNextFirePausesUntilJustAfterTheNextDueTime()
    {
        var trigger = Trigger(Weekly("09:00", 1));
        Assert.Equal(new DateTime(2026, 7, 12, 9, 0, 1), ScheduleEditing.SkipNextFireUntil(trigger, Date(2026, 7, 11, 12, 0), Chicago));
        Assert.Null(ScheduleEditing.SkipNextFireUntil(Trigger(), Date(2026, 7, 11, 12, 0), Chicago));
    }

    [Fact]
    public void TheEnabledMenuSaysOffPausedOrOn()
    {
        var now = Date(2026, 7, 12, 10, 0);
        var trigger = Trigger(Weekly("09:00", 1));
        Assert.Equal("Enabled: On", ScheduleEditing.EnabledTitle(trigger, now, Chicago));
        trigger.DisabledUntil = "2026-07-13T08:00";
        Assert.Equal("Enabled: Paused", ScheduleEditing.EnabledTitle(trigger, now, Chicago));
        trigger.Enabled = false;
        Assert.Equal("Enabled: Off", ScheduleEditing.EnabledTitle(trigger, now, Chicago));
    }

    [Fact]
    public void TheChipSwitchesARunningTriggerOffAndBringsAnyOtherFullyBack()
    {
        var now = Date(2026, 7, 12, 10, 0);
        var trigger = Trigger(Weekly("09:00", 1));
        ScheduleEditing.ToggleFromChip(trigger, now, Chicago);
        Assert.False(trigger.Enabled);
        ScheduleEditing.ToggleFromChip(trigger, now, Chicago);
        Assert.Null(trigger.Enabled);

        trigger.DisabledUntil = "2026-07-13T08:00";
        ScheduleEditing.ToggleFromChip(trigger, now, Chicago);
        Assert.Equal(((bool?)null, (string?)null), (trigger.Enabled, trigger.DisabledUntil));

        trigger.EnabledUntil = "2026-07-01T23:59";
        ScheduleEditing.ToggleFromChip(trigger, now, Chicago);
        Assert.Null(trigger.EnabledUntil);

        trigger.EnabledUntil = "2026-08-01T23:59";
        ScheduleEditing.ToggleFromChip(trigger, now, Chicago);
        Assert.Equal((false, "2026-08-01T23:59"), (trigger.Enabled, trigger.EnabledUntil));
    }

    // MARK: Summaries

    [Fact]
    public void SummariesNameEachConditionAndCountTheActions()
    {
        string Summary(ScheduleTrigger trigger) => ScheduleEditing.ConditionSummary(trigger, id => id == "sermon" ? "Sermon" : null, Chicago, Invariant);
        Assert.Equal("No conditions", Summary(Trigger()));
        Assert.Equal("Sun Wed 09:00 · no actions", Summary(Trigger(Weekly("09:00", 1, 4))));
        Assert.Equal("Every day 09:00:30 · no actions", Summary(Trigger(Weekly("09:00:30"))));
        Assert.Equal("Every day any time · no actions", Summary(Trigger(new ScheduleCondition { Id = "w", Kind = ScheduleConditionKind.Weekly })));
        Assert.Equal("Every day invalid time · no actions", Summary(Trigger(Weekly("9 o'clock"))));
        Assert.Equal("Jul 20, 18:00 · no actions", Summary(Trigger(OneTime("2026-07-20T18:00"))));
        Assert.Equal("Jul 20, 18:00:30 · no actions", Summary(Trigger(OneTime("2026-07-20T18:00:30"))));
        Assert.Equal("One time: not set · no actions", Summary(Trigger(new ScheduleCondition { Id = "o", Kind = ScheduleConditionKind.OneTime })));

        var timer = new ScheduleCondition { Id = "x", Kind = ScheduleConditionKind.TimerReaches, TimerId = "sermon", TimerSeconds = 300 };
        var both = Trigger(Weekly("09:00", 1), timer);
        both.Actions = [Make.Action("a", SlideActionKind.ClearAll)];
        Assert.Equal("Sun 09:00 · Sermon reaches 5:00 · 1 action", Summary(both));
        both.ConditionLogic = ScheduleConditionLogic.All;
        both.Actions.Add(Make.Action("b", SlideActionKind.ClearAudio));
        Assert.Equal("Sun 09:00 & Sermon reaches 5:00 · 2 actions", Summary(both));
        timer.TimerId = "gone";
        Assert.StartsWith("Sun 09:00 & missing timer reaches 5:00", Summary(both));
    }

    [Fact]
    public void ARowShowsTheNextFireOrWhyItWont()
    {
        var now = Date(2026, 7, 11, 12, 0);
        TriggerRowStatus Row(ScheduleTrigger trigger, bool folderOn = true, bool schedulerOn = true, DateTimeOffset? upcoming = null,
            IReadOnlyList<Scheduler.RunRecord>? history = null) =>
            ScheduleEditing.RowStatus(trigger, folderOn, schedulerOn, upcoming, history ?? [], now, Chicago, Invariant);

        var weekly = Trigger(Weekly("09:00", 1));
        var sunday = Date(2026, 7, 12, 9, 0);
        Assert.Equal(new TriggerRowStatus(false, true, true, "Sun 09:00", false, null), Row(weekly, upcoming: sunday));
        Assert.Equal(new TriggerRowStatus(true, true, false, "Sun 09:00", false, null), Row(weekly, folderOn: false, upcoming: sunday));
        Assert.Equal(new TriggerRowStatus(false, true, false, null, false, null), Row(weekly, schedulerOn: false));

        weekly.DisabledUntil = "2026-07-11T15:30";
        Assert.Equal(new TriggerRowStatus(true, false, false, "until 15:30", false, null), Row(weekly));
        weekly.DisabledUntil = "2026-07-13T08:00";
        Assert.Equal("until Jul 13, 08:00", Row(weekly).When);

        weekly.DisabledUntil = null;
        weekly.EnabledUntil = "2026-07-10T23:59";
        Assert.Equal(new TriggerRowStatus(true, false, false, "ended Jul 10", false, null), Row(weekly));
        weekly.EnabledUntil = "2026-07-19T23:59";
        Assert.Equal("ends Jul 19", Row(weekly, upcoming: sunday).Ends);

        var spent = Trigger(OneTime("2026-07-10T18:00"));
        Assert.Equal("18:00", Row(spent).When);
        var missed = new Scheduler.RunRecord(Guid.NewGuid(), "t", "Walk-in", Date(2026, 7, 10, 18, 0), Scheduler.RunOutcome.Missed, Scheduler.RunSource.Schedule);
        Assert.Equal(("Missed 18:00", true), (Row(spent, history: [missed]).When, Row(spent, history: [missed]).WhenIsWarning));
    }

    [Fact]
    public void TheBoardsStatusLineAndFolderCountsReadAsOnTheMac()
    {
        Assert.Equal("Off — triggers author normally, nothing fires.", ScheduleEditing.StatusLine(false, null, Chicago, Invariant));
        Assert.Equal("No upcoming time triggers.", ScheduleEditing.StatusLine(true, null, Chicago, Invariant));
        Assert.Equal("Next: Walk-in · Sun 09:00",
            ScheduleEditing.StatusLine(true, new Scheduler.UpcomingFire("t", "Walk-in", Date(2026, 7, 12, 9, 0)), Chicago, Invariant));

        var folder = new ScheduleFolder { Id = "f", Name = "Sunday", TriggerIds = ["a", "b", "gone"] };
        var archived = Trigger();
        archived.Archived = true;
        Assert.Equal(2, ScheduleEditing.ActiveTriggerCount(folder, id => id == "b" ? archived : id == "a" ? Trigger() : null));
    }

    [Fact]
    public void TheDetailCardSaysWhenItFiresNextAndWhyNot()
    {
        var now = Date(2026, 7, 11, 12, 0);
        string Line(ScheduleTrigger trigger, bool folderOn = true, bool schedulerOn = true) =>
            ScheduleEditing.NextFireLine(trigger, folderOn, schedulerOn, now, Chicago, Invariant);

        var weekly = Trigger(Weekly("09:00", 1));
        Assert.Equal("Fires next: Sun Jul 12, 09:00 (tomorrow)", Line(weekly));
        Assert.Equal("Fires next: Sun Jul 12, 09:00 (tomorrow) Won't fire now: the Scheduler is off.", Line(weekly, schedulerOn: false));
        Assert.EndsWith("Won't fire now: its folder is off.", Line(weekly, folderOn: false));
        weekly.Enabled = false;
        Assert.EndsWith("Won't fire now: this trigger is off.", Line(weekly));
        weekly.Enabled = null;
        weekly.DisabledUntil = "2026-07-13T08:00";
        Assert.EndsWith("Won't fire now: it's paused until Jul 13, 08:00.", Line(weekly));
        weekly.DisabledUntil = null;
        weekly.EnabledUntil = "2026-07-12T08:00";
        Assert.Equal("Nothing coming up.", Line(weekly));

        Assert.Equal("Add a condition to say when this runs.", Line(Trigger()));
        Assert.Equal("Fires when its timer reaches the time.", Line(Trigger(ScheduleEditing.NewTimerReaches("t"))));
        var done = Trigger(OneTime("2026-07-10T18:00"));
        done.Archived = true;
        Assert.StartsWith("Done", Line(done));
        Assert.Equal("Sat Jul 11, 18:00 (today)", ScheduleEditing.When(Date(2026, 7, 11, 18, 0), now, Chicago, Invariant));
        Assert.Equal("Tue Jul 14, 18:00 (in 3 days)", ScheduleEditing.When(Date(2026, 7, 14, 18, 0), now, Chicago, Invariant));
    }

    [Fact]
    public void TheHistoryListsSpentOneOffsNewestFirst()
    {
        var older = Trigger(OneTime("2026-07-01T18:00"));
        older.Id = "older";
        older.Archived = true;
        var newer = Trigger(OneTime("2026-07-05T18:00"));
        newer.Id = "newer";
        newer.Archived = true;
        var active = Trigger(OneTime("2026-07-09T18:00"));
        Assert.Equal(["newer", "older"], ScheduleEditing.Archived([older, active, newer], Chicago).Select(t => t.Id));
        Assert.Equal(DateTimeOffset.MinValue, ScheduleEditing.SpentDate(Trigger(Weekly("09:00", 1)), Chicago));
        Assert.Equal("missed", ScheduleEditing.OutcomeTag(Scheduler.RunOutcome.Missed));
        Assert.Null(ScheduleEditing.OutcomeTag(Scheduler.RunOutcome.Fired));
    }

    // MARK: Typing dates and times

    [Theory]
    [InlineData("9", 9, 0, 0)]
    [InlineData("09:30", 9, 30, 0)]
    [InlineData("9:30:15", 9, 30, 15)]
    [InlineData("9:30 pm", 21, 30, 0)]
    [InlineData("9pm", 21, 0, 0)]
    [InlineData("12:15 AM", 0, 15, 0)]
    [InlineData("12 p.m.", 12, 0, 0)]
    [InlineData("21.30", 21, 30, 0)]
    [InlineData("2130", 21, 30, 0)]
    [InlineData("930", 9, 30, 0)]
    public void TypedTimesAreRead(string text, int hour, int minute, int second) =>
        Assert.Equal((hour, minute, second), ScheduleEditing.ParseTimeInput(text));

    [Theory]
    [InlineData("")]
    [InlineData("noon")]
    [InlineData("24:00")]
    [InlineData("9:60")]
    [InlineData("13 pm")]
    [InlineData("1:2:3:4")]
    [InlineData("12345")]
    public void NonsenseTimesAreRefused(string text) => Assert.Null(ScheduleEditing.ParseTimeInput(text));

    [Fact]
    public void TypedDatesAreReadInTheRegionsStyleOrAsIso()
    {
        var today = new DateTime(2026, 10, 24, 15, 0, 0);
        Assert.Equal(new DateTime(2026, 10, 25), ScheduleEditing.ParseDateInput("2026-10-25", today, CultureInfo.GetCultureInfo("en-GB")));
        Assert.Equal(new DateTime(2026, 10, 25), ScheduleEditing.ParseDateInput("25/10/2026", today, CultureInfo.GetCultureInfo("en-GB")));
        Assert.Equal(new DateTime(2026, 10, 25), ScheduleEditing.ParseDateInput("10/25/2026", today, CultureInfo.GetCultureInfo("en-US")));
        Assert.Equal(new DateTime(2026, 10, 24), ScheduleEditing.ParseDateInput("Today", today, Invariant));
        Assert.Equal(new DateTime(2026, 10, 25), ScheduleEditing.ParseDateInput("tomorrow", today, Invariant));
        Assert.Null(ScheduleEditing.ParseDateInput("next week", today, Invariant));
        Assert.Null(ScheduleEditing.ParseDateInput("2026-02-30", today, Invariant));
        Assert.Equal("25/10/2026", ScheduleEditing.DateInputText(new DateTime(2026, 10, 25), CultureInfo.GetCultureInfo("en-GB")));
        Assert.Contains("2026", ScheduleEditing.DateConfirmation(new DateTime(2026, 10, 25), CultureInfo.GetCultureInfo("en-GB")));
        Assert.Equal("09:00", ScheduleEditing.TimeInputText(9, 0, 0, Invariant));
        Assert.Equal("09:00:30", ScheduleEditing.TimeInputText(9, 0, 30, Invariant));
        Assert.Equal("9:00 AM", Plain(ScheduleEditing.TimeInputText(9, 0, 0, CultureInfo.GetCultureInfo("en-US"))));
        // What the box shows must read back as the same time.
        foreach (var culture in new[] { Invariant, CultureInfo.GetCultureInfo("en-US"), CultureInfo.GetCultureInfo("en-GB") })
            Assert.Equal((21, 5, 9), ScheduleEditing.ParseTimeInput(ScheduleEditing.TimeInputText(21, 5, 9, culture)));
    }
}
