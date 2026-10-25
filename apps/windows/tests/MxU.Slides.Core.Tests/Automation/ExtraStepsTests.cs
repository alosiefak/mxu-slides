using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Core.Stage;
using T = MxU.Slides.Core.Automation.PresentationTimelines;

namespace MxU.Slides.Core.Tests.Automation;

// Windows only: the router, the scheduler and the timeline tell the extra steps (production
// cues kept in WindowsExtras) which owner just ran, so they can run alongside.
public class ExtraStepsTests
{
    private sealed class RecordingExtras : IExtraSteps
    {
        public List<string> Ran { get; } = [];
        public int Cancelled { get; private set; }

        public void OwnerRan(string ownerId) => Ran.Add(ownerId);
        public void CancelPending() => Cancelled++;
    }

    private readonly RecordingShow show = new();
    private readonly MemoryLibrary library = new();
    private readonly ManualClock clock = new(new DateTimeOffset(2026, 7, 12, 9, 0, 0, TimeSpan.Zero));
    private readonly RecordingExtras extras = new();

    private ActionRouter Router() => new(show, library, clock) { ExtraSteps = extras };

    private static ActionCombo Combo(string id, params SlideAction[] actions) => new() { Id = id, Name = id, Actions = [.. actions] };

    [Fact]
    public void ASlideWithNoSharedStepsStillRunsItsExtras()
    {
        var slide = new Slide { Id = "slide-1", Name = "Verse", Objects = [] };
        Router().SlideDidFire(slide, null);
        Assert.Equal(["slide-1"], extras.Ran);
        Assert.Empty(show.Calls);
    }

    [Fact]
    public void AFiredMediaItemRunsItsExtras()
    {
        Router().MediaDidFire(Make.Media("loop"), LayerKind.LoopingVideos);
        Assert.Equal(["loop"], extras.Ran);
    }

    [Fact]
    public void ACombosExtrasRunAndSoDoThoseOfTheCombosItFires()
    {
        library.Add(
            Combo("outer", Make.Action("1", SlideActionKind.FireCombo, a => a.ComboId = "inner")),
            Combo("inner", Make.Action("2", SlideActionKind.ClearAll)));
        Router().Fire("outer");
        Assert.Equal(["outer", "inner"], extras.Ran);
        Assert.Equal(["clearAll:"], show.Calls);
    }

    [Fact]
    public void ACombosItFiresAfterADelayRunTheirExtrasThen()
    {
        library.Add(
            Combo("outer", Make.Action("1", SlideActionKind.FireCombo, a => { a.ComboId = "inner"; a.DelaySeconds = 2; })),
            Combo("inner", Make.Action("2", SlideActionKind.ClearAll)));
        Router().Fire("outer");
        Assert.Equal(["outer"], extras.Ran);
        clock.Advance(2.1);
        Assert.Equal(["outer", "inner"], extras.Ran);
    }

    [Fact]
    public void AComboThatFiresItselfRunsItsExtrasOnce()
    {
        library.Add(Combo("loop", Make.Action("1", SlideActionKind.FireCombo, a => a.ComboId = "loop")));
        Router().Fire("loop");
        Assert.Equal(["loop"], extras.Ran);
    }

    [Fact]
    public void TriggersAndCuesNameThemselvesAndStartupCombosRunTheirs()
    {
        library.Add(new ActionCombo { Id = "boot", Name = "Boot", RunOnStartup = true, Actions = [] });
        var router = Router();
        router.Execute("trigger-1", [Make.Action("1", SlideActionKind.ClearAudio)]);
        router.RunStartupCombos();
        Assert.Equal(["trigger-1", "boot"], extras.Ran);
        Assert.Equal(["clearAudio"], show.Calls);
    }

    [Fact]
    public void ClearAllDropsTheExtrasStillWaitingToo()
    {
        var router = Router();
        router.CancelPendingDelayedActions();
        Assert.Equal(1, extras.Cancelled);
    }

    [Fact]
    public void WithoutExtrasNothingChanges()
    {
        var router = new ActionRouter(show, library, clock);
        router.SlideDidFire(new Slide { Id = "s", Name = "s", Objects = [] }, null);
        router.Execute("trigger", []);
        router.CancelPendingDelayedActions();
        Assert.Empty(show.Calls);
    }

    private sealed class SchedulerHost : ISchedulerHost
    {
        public List<ScheduleTrigger> All { get; } = [];
        public IReadOnlyList<ScheduleTrigger> Triggers => All;
        public SchedulerBoard Board { get; } = new() { Id = SchedulerBoardLogic.WellKnownId };
        public List<string> Ran { get; } = [];
        public TimerSnapshot? Timer(string id) => null;
        public void Run(IReadOnlyList<SlideAction> actions) => Ran.Add("shared");
        public void Run(string triggerId, IReadOnlyList<SlideAction> actions) => Ran.Add(triggerId);
        public void Archive(string triggerId) { }
    }

    [Fact]
    public void ASchedulerTriggerNamesItselfWhenItRuns()
    {
        var host = new SchedulerHost();
        host.All.Add(new ScheduleTrigger { Id = "sunday", Name = "Sunday", Conditions = [], Actions = [] });
        var scheduler = new Scheduler(host, new SchedulerMemory(true, new Dictionary<string, DateTimeOffset>(), clock.Now, []), clock);
        scheduler.RunNow("sunday");
        Assert.Equal(["sunday"], host.Ran);
    }

    [Fact]
    public void AnActionsCueNamesItselfEvenWithNoSharedSteps()
    {
        var host = new TimelineHost();
        List<TimelinePlayedBlock> blocks = [new("v", ["v1"])];
        var deck = Make.Deck("song", ("v1", "v"));
        deck.Timelines =
        [
            Make.Timeline("t", cues:
            [
                T.SlideCue("s0", 0, T.Anchor(0, blocks)!),
                Make.Cue("lights-only", "actions", 1, actions: []),
                Make.Cue("both", "actions", 2, actions: [Make.Action("a1", SlideActionKind.ClearAll)]),
            ]),
        ];
        host.Decks["song"] = deck;
        var player = new TimelinePlayer(host, show, library, clock: clock);
        player.Play("item", deck, null);
        clock.Advance(3);
        Assert.Equal(["lights-only", "both"], host.RanCues);
        Assert.Contains("actions:a1", host.Fired);
    }
}
