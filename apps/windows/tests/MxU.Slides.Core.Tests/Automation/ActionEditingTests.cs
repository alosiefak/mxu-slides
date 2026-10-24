using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Tests.Automation;

// C#: the rules of ActionParameterFields, AddActionMenuItems and ActionsMenu in
// Sources/ActionEditors.swift, and of Sources/LiveInputCatalog.swift. The Mac checks its
// editors through SwiftUI source sweeps (SlideActionsMenuSweepTests); here the same rules
// are tested directly.
public class ActionEditingTests
{
    private static readonly ActionChoices Everything = new()
    {
        OutputPresets = [new("p1", "Worship")],
        Media = [new("m1", "Dusk")],
        Presentations = [new("d1", "Announcements")],
        AudioItems = [new("a1", "Walk-in")],
        AudioPlaylists = [new("pl1", "Pre-service")],
        Alerts = [new("al1", "Nursery")],
        Overlays = [new("o1", "Name + Title")],
        LiveInputs = [new("in1", "Pulpit camera")],
        AudioInputs = [new("mic", "Mic")],
        ConfidenceLayouts = [new("cl1", "Lyrics")],
        ConfidenceScreens = [new("S1", "Stage")],
        Timers = [new("t1", "Sermon")],
        Combos = [new("c1", "Walk-in"), new("c2", "Offering")],
        CapturePresets = [new("cap", "Stream")],
        MidiDevices = [new("port", "Lights")],
    };

    private static List<string> Labels(SlideAction action) => [.. ActionEditing.Fields(action).Select(f => f.Label)];

    private static T Field<T>(SlideAction action, string label) where T : ActionField =>
        ActionEditing.Fields(action).OfType<T>().Single(f => f.Label == label);

    [Fact]
    public void EachKindShowsTheMacsSettingsInOrder()
    {
        Assert.Equal(["Layer"], Labels(Make.Action("a", SlideActionKind.ClearLayer)));
        Assert.Equal(["Preset"], Labels(Make.Action("a", SlideActionKind.SwitchOutputPreset)));
        Assert.Equal(["Media"], Labels(Make.Action("a", SlideActionKind.FireMedia)));
        Assert.Equal(["Timer"], Labels(Make.Action("a", SlideActionKind.TimerPause)));
        Assert.Equal(["Presentation", "Slide"], Labels(Make.Action("a", SlideActionKind.FirePresentation)));
        Assert.Equal(["Layout", "Screen"], Labels(Make.Action("a", SlideActionKind.SetConfidenceLayout)));
        Assert.Equal(["Source", "Layer"], Labels(Make.Action("a", SlideActionKind.FireLiveInput)));
        Assert.Equal(["File", "Repeat"], Labels(Make.Action("a", SlideActionKind.FireAudio)));
        Assert.Equal(["Playlist", "Repeat"], Labels(Make.Action("a", SlideActionKind.FireAudioPlaylist)));
        Assert.Equal(["Input"], Labels(Make.Action("a", SlideActionKind.DisableAudioInput)));
        Assert.Empty(ActionEditing.Fields(Make.Action("a", SlideActionKind.ClearAll)));
        Assert.Empty(ActionEditing.Fields(Make.Action("a", SlideActionKind.ClearAudio)));
        Assert.Empty(ActionEditing.Fields(Make.Action("a", SlideActionKind.DismissAlert)));
        var retired = Assert.IsType<NoteField>(Assert.Single(ActionEditing.Fields(Make.Action("a", SlideActionKind.SetSignage))));
        Assert.Contains("Digital Signage was removed", retired.Text);
        Assert.Equal("FIRE MEDIA", ActionEditing.Header(Make.Action("a", SlideActionKind.FireMedia)));
    }

    [Fact]
    public void ATimersModeAndAMidiMessageChooseTheirOtherSettings()
    {
        var configure = Make.Action("a", SlideActionKind.TimerConfigure);
        Assert.Equal(["Timer", "Mode", "Minutes", "Seconds"], Labels(configure));
        Assert.True(Field<OptionField>(configure, "Mode").Reshapes);
        Field<OptionField>(configure, "Mode").Set(configure, "countdownToTime");
        Assert.Equal(TimerMode.CountdownToTime, configure.TimerMode);
        Assert.Equal(["Timer", "Mode", "Hour", "Minute"], Labels(configure));
        Field<OptionField>(configure, "Mode").Set(configure, "");
        Assert.Null(configure.TimerMode);

        var midi = Make.Action("m", SlideActionKind.MidiOut);
        Assert.Equal(["Device", "Message", "Channel", "Note", "Velocity"], Labels(midi));
        Assert.Equal("noteOn", Field<OptionField>(midi, "Message").Get(midi));
        Field<OptionField>(midi, "Message").Set(midi, "controlChange");
        Assert.Equal(["Device", "Message", "Channel", "Controller", "Value"], Labels(midi));
        Assert.Equal("All Enabled Devices", Field<PickField>(midi, "Device").EmptyLabel);
    }

    [Fact]
    public void NumbersStartAtTheMacsDefaultsAndStayInRange()
    {
        var midi = Make.Action("m", SlideActionKind.MidiOut);
        var channel = Field<NumberField>(midi, "Channel");
        Assert.Equal((1, 1, 16), (channel.Get(midi), channel.Min, channel.Max));
        channel.Apply(midi, 40);
        Assert.Equal(16, midi.MidiChannel);
        channel.Apply(midi, -3);
        Assert.Equal(1, midi.MidiChannel);
        Assert.Equal(127, Field<NumberField>(midi, "Velocity").Get(midi));
        var note = Field<NumberField>(midi, "Note");
        Assert.Equal("C3 · 60", note.Display!(60));
        note.Apply(midi, 200);
        Assert.Equal(127, midi.MidiNumber);

        var clock = Make.Action("t", SlideActionKind.TimerConfigure, a => a.TimerMode = TimerMode.CountdownToTime);
        Assert.Equal(9, Field<NumberField>(clock, "Hour").Get(clock));
        Field<NumberField>(clock, "Minute").Apply(clock, 75);
        Assert.Equal(59, clock.TimerMinute);
    }

    [Fact]
    public void MinutesAndSecondsEditOneTotalAndTheSlideNumberIsShownFromOne()
    {
        var timer = Make.Action("t", SlideActionKind.TimerConfigure, a => a.TimerDurationSeconds = 65);
        Assert.Equal((1, 5), (Field<NumberField>(timer, "Minutes").Get(timer), Field<NumberField>(timer, "Seconds").Get(timer)));
        Field<NumberField>(timer, "Minutes").Apply(timer, 3);
        Assert.Equal(185, timer.TimerDurationSeconds);
        Field<NumberField>(timer, "Seconds").Apply(timer, 75);
        Assert.Equal(239, timer.TimerDurationSeconds);
        Assert.Equal(600, Field<NumberField>(timer, "Minutes").Max);

        var deck = Make.Action("d", SlideActionKind.FirePresentation);
        var slide = Field<NumberField>(deck, "Slide");
        Assert.Equal((1, 1, 999), (slide.Get(deck), slide.Min, slide.Max));
        slide.Apply(deck, 4);
        Assert.Equal(3, deck.SlideIndex);
        slide.Apply(deck, 0);
        Assert.Equal(0, deck.SlideIndex);
    }

    [Fact]
    public void PickersOfferTheirEmptyChoiceFirstAndStoreItAsAbsent()
    {
        var clear = Make.Action("c", SlideActionKind.ClearLayer);
        var layers = ActionEditing.Entries(Field<PickField>(clear, "Layer"), clear, Everything);
        Assert.Equal(("", ActionEditing.Choose), (layers[0].Id, layers[0].Name));
        Assert.Equal("alerts", layers[1].Id);
        Assert.Equal(1 + LayerKinds.All.Length, layers.Count);

        var input = Make.Action("i", SlideActionKind.FireLiveInput);
        var inputLayers = ActionEditing.Entries(Field<PickField>(input, "Layer"), input, Everything);
        Assert.Equal("Video Input", inputLayers[0].Name);
        Assert.DoesNotContain(inputLayers, c => c.Id == "videoInput");

        var overlay = Make.Action("o", SlideActionKind.DismissOverlay, a => a.OverlayId = "o1");
        var field = Field<PickField>(overlay, "Overlay");
        Assert.Equal("All Overlays", field.EmptyLabel);
        field.Apply(overlay, "");
        Assert.Null(overlay.OverlayId);
        Assert.False(field.Searchable);
        Assert.True(Field<PickField>(Make.Action("m", SlideActionKind.FireMedia), "Media").Searchable);
    }

    [Fact]
    public void AComboIsNeverOfferedToItselfAndMissingTargetsSaySo()
    {
        var fire = Make.Action("f", SlideActionKind.FireCombo);
        var combos = ActionEditing.Entries(Field<PickField>(fire, "Combo"), fire, Everything, editingComboId: "c1");
        Assert.Equal(["", "c2"], combos.Select(c => c.Id));

        var media = Make.Action("m", SlideActionKind.FireMedia, a => a.MediaId = "gone");
        var field = Field<PickField>(media, "Media");
        Assert.Equal("Missing media", ActionEditing.CurrentTitle(field, media, Everything));
        media.MediaId = "m1";
        Assert.Equal("Dusk", ActionEditing.CurrentTitle(field, media, Everything));
        media.MediaId = null;
        Assert.Equal(ActionEditing.Choose, ActionEditing.CurrentTitle(field, media, Everything));

        var timer = Make.Action("t", SlideActionKind.TimerStart, a => a.TimerId = "old");
        Assert.Equal("Missing timer", ActionEditing.CurrentTitle(Field<PickField>(timer, "Timer"), timer, Everything));
    }

    [Fact]
    public void AnOlderLiveInputStepShowsItsDeviceAndPickingAnInputReplacesIt()
    {
        var older = Make.Action("l", SlideActionKind.FireLiveInput, a =>
        {
            a.InputSourceKind = CaptureSourceKind.Ndi;
            a.InputSourceId = "BOOTH (Cam 1)";
        });
        var source = Field<PickField>(older, "Source");
        Assert.Equal("ndi::BOOTH (Cam 1)", source.Get(older));
        Assert.Equal("NDI: BOOTH (Cam 1)", ActionEditing.CurrentTitle(source, older, Everything));
        Assert.Contains(ActionEditing.Entries(source, older, Everything), c => c.Id == "item::in1" && c.Name == "Pulpit camera");

        source.Apply(older, "item::in1");
        Assert.Equal(("in1", (CaptureSourceKind?)null, (string?)null), (older.LiveInputId, older.InputSourceKind, older.InputSourceId));
        Assert.Equal("Pulpit camera", ActionEditing.CurrentTitle(source, older, Everything));
    }

    [Fact]
    public void LiveInputTokensRoundTripAsOnTheMac()
    {
        Assert.Equal("item::in1", LiveInputTokens.SelectionToken("in1", CaptureSourceKind.Camera, "cam"));
        Assert.Equal("camera::cam", LiveInputTokens.SelectionToken(null, CaptureSourceKind.Camera, "cam"));
        Assert.Equal("", LiveInputTokens.SelectionToken("", null, "cam"));
        Assert.Equal("in1", LiveInputTokens.LiveInputId("item::in1"));
        Assert.Null(LiveInputTokens.LiveInputId("item::"));
        Assert.Null(LiveInputTokens.LiveInputId("ndi::x"));
        Assert.Equal((CaptureSourceKind.Ndi, "A:B"), LiveInputTokens.Choice("ndi::A:B"));
        Assert.Null(LiveInputTokens.Choice("screen::1"));
        Assert.Null(LiveInputTokens.Choice(""));

        var step = Make.Action("s", SlideActionKind.FireLiveInput, a => a.LiveInputId = "in1");
        LiveInputTokens.Apply(step, "decklink::42");
        Assert.Equal(((string?)null, (CaptureSourceKind?)CaptureSourceKind.Decklink, "42"), (step.LiveInputId, step.InputSourceKind, step.InputSourceId));
        LiveInputTokens.Apply(step, "");
        Assert.Equal(((string?)null, (CaptureSourceKind?)null, (string?)null), (step.LiveInputId, step.InputSourceKind, step.InputSourceId));
        Assert.Equal("None", LiveInputTokens.LegacyLabel(null, null));
        Assert.Equal("Camera: abc", LiveInputTokens.LegacyLabel(CaptureSourceKind.Camera, "abc"));
    }

    [Fact]
    public void AStepNeedsAChoiceOnlyWhileAChooseEntryIsUnset()
    {
        Assert.True(ActionEditing.NeedsChoice(Make.Action("m", SlideActionKind.FireMedia)));
        Assert.False(ActionEditing.NeedsChoice(Make.Action("m", SlideActionKind.FireMedia, a => a.MediaId = "m1")));
        Assert.True(ActionEditing.NeedsChoice(Make.Action("t", SlideActionKind.TimerConfigure)));
        Assert.True(ActionEditing.NeedsChoice(Make.Action("l", SlideActionKind.FireLiveInput)));
        Assert.False(ActionEditing.NeedsChoice(Make.Action("c", SlideActionKind.ClearAll)));
        Assert.False(ActionEditing.NeedsChoice(Make.Action("o", SlideActionKind.DismissOverlay)));
        Assert.False(ActionEditing.NeedsChoice(Make.Action("s", SlideActionKind.CaptureStop)));
        Assert.False(ActionEditing.NeedsChoice(Make.Action("l", SlideActionKind.SetConfidenceLayout)));
        Assert.False(ActionEditing.NeedsChoice(Make.Action("x", SlideActionKind.MidiOut)));
    }

    [Fact]
    public void DelaysAreTenthsOfASecondUpToAnHourAndZeroIsAbsent()
    {
        Assert.Equal(2, ActionEditing.ParseDelay("2"));
        Assert.Equal(2.5, ActionEditing.ParseDelay(" 2.54 "));
        Assert.Equal(2.5, ActionEditing.ParseDelay("2,5"));
        Assert.Equal(3, ActionEditing.ParseDelay("3s"));
        Assert.Equal(0, ActionEditing.ParseDelay("-3"));
        Assert.Equal(3600, ActionEditing.ParseDelay("9999"));
        Assert.Null(ActionEditing.ParseDelay("soon"));
        Assert.Null(ActionEditing.ParseDelay("NaN"));

        var step = Make.Action("s", SlideActionKind.ClearAll);
        Assert.Equal("0", ActionEditing.DelayText(step));
        ActionEditing.StepDelay(step, 1);
        Assert.Equal(1, step.DelaySeconds);
        ActionEditing.StepDelay(step, -5);
        Assert.Null(step.DelaySeconds);
        step.DelaySeconds = 3600;
        ActionEditing.StepDelay(step, 1);
        Assert.Equal(3600, step.DelaySeconds);
        step.DelaySeconds = 2.5;
        Assert.Equal("2.5", ActionEditing.DelayText(step));
    }

    [Fact]
    public void TheSlideMenuCountsItsStepsAndAnEmptyListIsStoredAsAbsent()
    {
        Assert.Equal("Actions: None", ActionEditing.ActionsMenuTitle(0));
        Assert.Equal("Actions: 3", ActionEditing.ActionsMenuTitle(3));
        Assert.False(ActionEditing.OffersEditAll(1));
        Assert.True(ActionEditing.OffersEditAll(2));
        Assert.Null(ActionEditing.Stored([]));
        Assert.Single(ActionEditing.Stored([Make.Action("a", SlideActionKind.ClearAll)])!);
    }

    [Fact]
    public void TheAddMenuFollowsTheMacsOrderAndLeavesOutEmptySubmenus()
    {
        Assert.Equal(
            ["Clear", "Fire Media…", "Fire Presentation", "Music", "Alert", "Overlay", "Confidence Layout", "Capture", "MIDI Out"],
            ActionEditing.AddMenu(new ActionChoices()).Select(i => i.Title));
        Assert.Equal(
            ["Output Preset", "Clear", "Fire Media…", "Fire Presentation", "Music", "Alert", "Overlay", "Live Input", "Audio Input",
             "Confidence Layout", "Timer", "Action Combo", "Capture", "MIDI Out"],
            ActionEditing.AddMenu(Everything).Select(i => i.Title));
    }

    [Fact]
    public void TheClearAlertAndCaptureSubmenusHoldTheirClosingCommands()
    {
        var menu = ActionEditing.AddMenu(Everything, newId: Make.Ids());
        var clear = ActionEditing.Find(menu, "Clear")!.Children!;
        Assert.Equal("Clear All", clear[0].Title);
        Assert.True(clear[1].IsSeparator);
        Assert.Equal("Alerts", clear[2].Title);
        Assert.Equal("Clear Music", clear[^1].Title);
        Assert.Equal(1 + 1 + LayerKinds.All.Length + 1 + 1, clear.Count);
        Assert.Equal("alerts", clear[2].Make!().Layer);

        Assert.Equal(["Nursery", "-", "Dismiss Alert"], ActionEditing.Find(menu, "Alert")!.Children!.Select(i => i.Title));
        Assert.Equal(["Dismiss Alert"], ActionEditing.Find(ActionEditing.AddMenu(new ActionChoices()), "Alert")!.Children!.Select(i => i.Title));
        Assert.Equal(["Name + Title", "-", "Take Down Overlays"], ActionEditing.Find(menu, "Overlay")!.Children!.Select(i => i.Title));
        Assert.Equal(["Start", "Stop", "-", "Stop All Capture"], ActionEditing.Find(menu, "Capture")!.Children!.Select(i => i.Title));
        var stopAll = ActionEditing.Find(ActionEditing.AddMenu(new ActionChoices()), "Capture", "Stop All Capture")!.Make!();
        Assert.Equal((SlideActionKind.CaptureStop, (string?)null), (stopAll.Kind, stopAll.CapturePresetId));
        Assert.Equal(["Built-in Layout", "-", "Lyrics"], ActionEditing.Find(menu, "Confidence Layout")!.Children!.Select(i => i.Title));
        Assert.Equal(["Playlist", "File…"], ActionEditing.Find(menu, "Music")!.Children!.Select(i => i.Title));
        Assert.Equal(["Mic On", "Mic Off"], ActionEditing.Find(menu, "Audio Input")!.Children!.Select(i => i.Title));
    }

    [Fact]
    public void MenuCommandsMakeFreshStepsWithTheirTargetFilledIn()
    {
        var menu = ActionEditing.AddMenu(Everything, newId: Make.Ids());
        var start = ActionEditing.Find(menu, "Timer", "Start", "Sermon")!.Make!();
        Assert.Equal((SlideActionKind.TimerStart, "t1"), (start.Kind, start.TimerId));
        var configure = ActionEditing.Find(menu, "Timer", "Configure", "Sermon")!.Make!();
        Assert.Equal((TimerMode.Countdown, (double?)300), (configure.TimerMode, configure.TimerDurationSeconds));
        var preset = ActionEditing.Find(menu, "Output Preset", "Worship")!.Make!();
        Assert.Equal("p1", preset.PresetId);
        var input = ActionEditing.Find(menu, "Live Input", "Pulpit camera")!.Make!();
        Assert.Equal("in1", input.LiveInputId);
        var playlist = ActionEditing.Find(menu, "Music", "Playlist", "Pre-service")!.Make!();
        Assert.Equal("pl1", playlist.PlaylistId);
        var media = ActionEditing.Find(menu, "Fire Media…")!.Make!();
        Assert.Equal((SlideActionKind.FireMedia, (string?)null), (media.Kind, media.MediaId));
        Assert.Equal(["n1", "n2", "n3", "n4", "n5", "n6"], new[] { start, configure, preset, input, playlist, media }.Select(a => a.Id));
        Assert.Null(ActionEditing.Find(menu, "Timer", "Start", "Nobody"));
    }

    [Fact]
    public void TheComboBeingEditedIsLeftOutOfTheComboMenu()
    {
        var menu = ActionEditing.AddMenu(Everything, excludeComboId: "c1");
        Assert.Equal(["Offering"], ActionEditing.Find(menu, "Action Combo")!.Children!.Select(i => i.Title));
        var alone = Everything with { Combos = [new("c1", "Walk-in")] };
        Assert.Null(ActionEditing.Find(ActionEditing.AddMenu(alone, excludeComboId: "c1"), "Action Combo"));
    }
}
