using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Tests.Automation;

// C#: the logic of Sources/ActionRouter.swift (the Mac has no tests for the router),
// against a recording show, an in-memory library and a fake clock.
public class ActionRouterTests
{
    private readonly RecordingShow show = new();
    private readonly MemoryLibrary library = new();
    private readonly ManualClock clock = new(new DateTimeOffset(2026, 7, 12, 9, 0, 0, TimeSpan.Zero));
    private readonly List<(string Crumb, string Detail)> notes = [];

    private ActionRouter Router()
    {
        var router = new ActionRouter(show, library, clock);
        router.Noted += (crumb, detail) => notes.Add((crumb, detail));
        return router;
    }

    private static ActionCombo Combo(string id, params SlideAction[] actions) => new() { Id = id, Name = id, Actions = [.. actions] };

    private static SlideAction Step(string id, SlideActionKind kind, Action<SlideAction>? fill = null) => Make.Action(id, kind, fill);

    [Fact]
    public void EachStepReachesTheShowWithItsTarget()
    {
        library.Add(
            Make.Media("m"), new Overlay { Id = "o", Name = "o", Objects = [] }, new AlertPreset { Id = "al", Name = "al" },
            new AudioItem { Id = "au", Name = "au", FileHash = "", FileName = "a.wav", Tags = [], Favorite = false },
            new Playlist { Id = "pl", Name = "pl", PlaylistKind = PlaylistKind.Audio, PlaybackMode = PlaybackMode.PlayAll },
            new StreamRecordPreset { Id = "cap", Name = "cap" });
        var router = Router();
        router.Execute(
        [
            Step("1", SlideActionKind.ClearLayer, a => a.Layer = "stillGraphics"),
            Step("2", SlideActionKind.FireMedia, a => a.MediaId = "m"),
            Step("3", SlideActionKind.FireOverlay, a => a.OverlayId = "o"),
            Step("4", SlideActionKind.DismissOverlay),
            Step("5", SlideActionKind.FireAlert, a => a.AlertId = "al"),
            Step("6", SlideActionKind.TimerConfigure, a => { a.TimerId = "t"; a.TimerMode = TimerMode.Countdown; a.TimerDurationSeconds = 300; }),
            Step("7", SlideActionKind.FireLiveInput, a => a.LiveInputId = "cam"),
            Step("8", SlideActionKind.FireLiveInput, a => { a.InputSourceId = "ndi-1"; a.InputSourceKind = CaptureSourceKind.Ndi; a.Layer = "videos"; }),
            Step("9", SlideActionKind.DisableAudioInput, a => a.AudioInputId = "mic"),
            Step("10", SlideActionKind.FireAudio, a => { a.AudioItemId = "au"; a.AudioRepeat = true; }),
            Step("11", SlideActionKind.FireAudioPlaylist, a => { a.PlaylistId = "pl"; a.AudioEntryId = "e2"; }),
            Step("12", SlideActionKind.CaptureStart, a => a.CapturePresetId = "cap"),
            Step("13", SlideActionKind.CaptureStop),
            Step("14", SlideActionKind.SetConfidenceLayout, a => { a.ConfidenceLayoutId = "lay"; a.ConfidenceScreenId = "not-a-uuid"; }),
            Step("15", SlideActionKind.ClearAudio),
            Step("16", SlideActionKind.SetSignage),
        ]);
        Assert.Equal(
        [
            "clearLayer:stillGraphics", "media:m", "overlay:o", "dismissOverlays", "alert:al",
            "timerConfigure:t:Countdown:300::", "liveInput:cam@videoInput", "liveSource:Ndi:ndi-1@videos", "audioInput:mic=False",
            "audio:au:True", "playlist:pl:e2:", "captureStart:cap", "captureStopAll", "confidence:lay:", "clearAudio",
        ], show.Calls);
        Assert.Contains(("actions.retired", "setSignage"), notes);
    }

    [Fact]
    public void MissingTargetsAreSkippedAndNoted()
    {
        library.Add(new Playlist { Id = "video", Name = "v", PlaylistKind = PlaylistKind.Media, PlaybackMode = PlaybackMode.PlayAll });
        show.HasConfidenceMonitors = false;
        var router = Router();
        router.Execute(
        [
            Step("1", SlideActionKind.FireMedia, a => a.MediaId = "gone"),
            Step("2", SlideActionKind.ClearLayer, a => a.Layer = "nonsense"),
            Step("3", SlideActionKind.TimerStart),
            Step("4", SlideActionKind.FireAudioPlaylist, a => a.PlaylistId = "video"),
            Step("5", SlideActionKind.SetConfidenceLayout),
            Step("6", SlideActionKind.CaptureStart, a => a.CapturePresetId = "gone"),
            Step("7", SlideActionKind.FireLiveInput),
            Step("8", SlideActionKind.MidiOut, a => a.MidiDeviceId = "ghost"),
        ]);
        Assert.Empty(show.Calls);
        Assert.Equal(
        [
            "fireMedia missing mediaId", "clearLayer missing layer", "timerStart missing timerId", "fireAudioPlaylist missing playlistId",
            "setConfidenceLayout missing confidenceMonitor", "captureStart missing capturePresetId", "fireLiveInput missing liveInputId",
            "midiOut device missing",
        ], notes.Where(n => n.Crumb == "actions.skipped").Select(n => n.Detail));
    }

    [Fact]
    public void MidiOutGoesToTheSettingsOutputsOrTheStepsDevice()
    {
        show.MidiOutDevices = [new MidiOutDevice("lk", true, MIDIDeviceItemDirection.Output, "port-7")];
        Router().Execute(
        [
            Step("1", SlideActionKind.MidiOut, a => { a.MidiChannel = 2; a.MidiNumber = 60; }),
            Step("2", SlideActionKind.MidiOut, a => { a.MidiDeviceId = "lk"; a.MidiKind = MIDIMessageKind.ControlChange; a.MidiNumber = 7; a.MidiValue = 64; }),
        ]);
        Assert.Equal(["midi:913C7F->settings", "midi:B00740->port-7"], show.Calls);
    }

    [Fact]
    public void CombosNestFourDeepAndNeverLoop()
    {
        library.Add(
            Combo("a", Step("a1", SlideActionKind.FireCombo, s => s.ComboId = "b"), Step("a2", SlideActionKind.ClearAudio)),
            Combo("b", Step("b1", SlideActionKind.FireCombo, s => s.ComboId = "a"), Step("b2", SlideActionKind.FireCombo, s => s.ComboId = "c")),
            Combo("c", Step("c1", SlideActionKind.FireCombo, s => s.ComboId = "d")),
            Combo("d", Step("d1", SlideActionKind.FireCombo, s => s.ComboId = "e")),
            Combo("e", Step("e1", SlideActionKind.DismissAlert)));
        var router = Router();
        router.Fire("a");
        // a (1) → b (2) → c (3) → d (4) stops before e; b's step back to a is a cycle.
        Assert.Equal(["clearAudio"], show.Calls);
        Assert.Equal(["a depth=2", "e depth=4"], notes.Where(n => n.Crumb == "actions.combo.cycle").Select(n => n.Detail));
        Assert.Equal(["a"], library.Used);

        router.Fire("ghost");
        Assert.Contains(("actions.combo.missing", "ghost"), notes);
    }

    [Fact]
    public void DelayedStepsWaitAndRunTogether()
    {
        var router = Router();
        router.Execute(
        [
            Step("now", SlideActionKind.ClearAudio),
            Step("later", SlideActionKind.DismissAlert, s => s.DelaySeconds = 2),
            Step("later2", SlideActionKind.DismissOverlay, s => s.DelaySeconds = 2),
            Step("last", SlideActionKind.ClearAll, s => s.DelaySeconds = 5),
        ]);
        Assert.Equal(["clearAudio"], show.Calls);
        Assert.Equal(2, router.PendingDelayedBatches);
        clock.Advance(2);
        Assert.Equal(["clearAudio", "dismissAlert", "dismissOverlays"], show.Calls);
        clock.Advance(3);
        Assert.Equal(["clearAudio", "dismissAlert", "dismissOverlays", "clearAll:"], show.Calls);
        Assert.Equal(0, router.PendingDelayedBatches);
    }

    [Fact]
    public void RefiringAStepRestartsItsWaitAndCancellingDropsEverything()
    {
        var router = Router();
        SlideAction[] steps = [Step("later", SlideActionKind.DismissAlert, s => s.DelaySeconds = 2)];
        router.Execute(steps);
        clock.Advance(1.5);
        router.Execute(steps);
        clock.Advance(1);
        Assert.Empty(show.Calls);
        clock.Advance(1);
        Assert.Equal(["dismissAlert"], show.Calls);

        router.Execute(steps);
        router.CancelPendingDelayedActions();
        clock.Advance(5);
        Assert.Equal(["dismissAlert"], show.Calls);
        Assert.Contains(notes, n => n.Crumb == "actions.delayed.cancelled");
    }

    [Fact]
    public void ASlideFiringSkipsClearsOfItsOwnLayersAndItsPresetSteps()
    {
        library.Add(Make.Media("logo", MediaKind.Image), new OutputPreset { Id = "p", Name = "p", Assignments = [] });
        var slide = new Slide
        {
            Id = "s", Name = "Welcome", Objects = [],
            Actions =
            [
                Step("1", SlideActionKind.FireMedia, s => s.MediaId = "logo"),
                Step("2", SlideActionKind.ClearLayer, s => s.Layer = "stillGraphics"),
                Step("3", SlideActionKind.ClearLayer, s => s.Layer = "videos"),
                Step("4", SlideActionKind.SwitchOutputPreset, s => s.PresetId = "p"),
                Step("5", SlideActionKind.ClearAll),
            ],
        };
        var router = Router();
        router.ApplyPresetSwitches(slide, null);
        Assert.Equal(["preset:p"], show.Calls);
        router.SlideDidFire(slide, null);
        Assert.Equal(["preset:p", "media:logo", "clearLayer:videos", "clearAll:slide,stillGraphics"], show.Calls);
        Assert.Contains(("actions.clear.skippedOwnLayer", "Welcome"), notes);
    }

    [Fact]
    public void ThePresetComesFromTheSlideItsCombosOrItsServiceItem()
    {
        library.Add(
            new OutputPreset { Id = "item", Name = "item", Assignments = [] }, new OutputPreset { Id = "combo", Name = "combo", Assignments = [] },
            Combo("c", Step("c1", SlideActionKind.SwitchOutputPreset, s => s.PresetId = "combo")));
        library.ItemPresets["ctx"] = "item";
        var router = Router();
        var plain = new Slide { Id = "s", Name = "s", Objects = [] };
        Assert.Equal("item", router.PredictedPresetId(plain, "ctx"));
        Assert.Null(router.PredictedPresetId(plain, null));

        var withCombo = new Slide { Id = "s2", Name = "s2", Objects = [], Actions = [Step("1", SlideActionKind.FireCombo, s => s.ComboId = "c")] };
        Assert.Equal("combo", router.PredictedPresetId(withCombo, "ctx"));
        var delayed = new Slide
        {
            Id = "s3", Name = "s3", Objects = [],
            Actions = [Step("1", SlideActionKind.SwitchOutputPreset, s => { s.PresetId = "combo"; s.DelaySeconds = 3; })],
        };
        Assert.Equal("item", router.DeclaredPresetId(delayed.Actions) ?? "item");
        Assert.Null(router.DeclaredPresetId(delayed.Actions));

        router.ApplyPresetSwitches(withCombo, "ctx");
        router.ApplyPresetSwitches(withCombo, "ctx");
        Assert.Equal(["preset:combo"], show.Calls);
    }

    [Fact]
    public void FirePresentationFiresItsSlideAndStopsARunawayChain()
    {
        var deck = Make.Deck("p", ("s1", null), ("s2", null));
        deck.Slides[1].Actions = [Step("again", SlideActionKind.FirePresentation, s => { s.PresentationId = "p"; s.SlideIndex = 1; })];
        library.Add(deck);
        var router = Router();
        show.OnFireSlide = (slide, _) => router.SlideDidFire(slide, null);
        router.Execute([Step("go", SlideActionKind.FirePresentation, s => { s.PresentationId = "p"; s.SlideIndex = 1; })]);
        Assert.Equal(4, show.Calls.Count(c => c == "slide:p/s2"));
        Assert.Contains(("actions.presentation.cycle", "p depth=4"), notes);

        router.Execute([Step("bad", SlideActionKind.FirePresentation, s => { s.PresentationId = "p"; s.SlideIndex = 7; })]);
        Assert.Contains(("actions.skipped", "firePresentation missing slideIndex"), notes);
    }

    [Fact]
    public void StartupCombosRunOnceByName()
    {
        var second = Combo("z1", Step("z", SlideActionKind.DismissAlert));
        second.Name = "B";
        second.RunOnStartup = true;
        var first = Combo("a1", Step("a", SlideActionKind.ClearAudio));
        first.Name = "A";
        first.RunOnStartup = true;
        library.Add(second, first, Combo("skip", Step("s", SlideActionKind.ClearAll)));
        var router = Router();
        router.RunStartupCombos();
        router.RunStartupCombos();
        Assert.Equal(["clearAudio", "dismissAlert"], show.Calls);
    }

    [Fact]
    public void AMediaItemFiringSkipsClearsOfItsLayer()
    {
        var item = Make.Media("v");
        item.Actions = [Step("1", SlideActionKind.ClearLayer, s => s.Layer = "videos"), Step("2", SlideActionKind.ClearLayer, s => s.Layer = "slide")];
        Router().MediaDidFire(item, LayerKind.Videos);
        Assert.Equal(["clearLayer:slide"], show.Calls);
    }
}
