using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Tests.Automation;

// C#: the editing rules of Sources/Modules/ActionCombosModule.swift and Sources/ActionEditors.swift
// (the Mac checks those through SwiftUI source sweeps; here the rules are tested directly).
public class ActionCombosTests
{
    private static ActionCombo Combo(string id, params SlideAction[] actions) => new() { Id = id, Name = id, Actions = [.. actions] };

    [Fact]
    public void CombosCanUseTheLightIconAndDefaultToTheBolt()
    {
        Assert.Contains(ActionCombos.Icons, i => i.Glyph == "light" && i.Name == "Light");
        var combo = Combo("c");
        Assert.Equal("combos", ActionCombos.Icon(combo));
        combo.IconName = "light";
        Assert.Equal("light", ActionCombos.Icon(combo));
        Assert.Null(ActionCombos.StoredIcon("combos"));
        Assert.Equal("midi", ActionCombos.StoredIcon("midi"));
        Assert.Equal(6, ActionCombos.Palette.Count);
    }

    [Fact]
    public void CaptionsCountTheSteps()
    {
        Assert.Equal("empty", ActionCombos.StepCaption(Combo("c")));
        Assert.Equal("1 action", ActionCombos.StepCaption(Combo("c", Make.Action("a", SlideActionKind.ClearAll))));
        Assert.Equal("2 actions", ActionCombos.StepCaption(Combo("c", Make.Action("a", SlideActionKind.ClearAll), Make.Action("b", SlideActionKind.ClearAudio))));
    }

    [Fact]
    public void DuplicateAndPasteGiveEveryStepANewIdAndKeepTheLook()
    {
        var combo = Combo("c", Make.Action("a", SlideActionKind.FireMedia, a => a.MediaId = "m"));
        combo.ColorHex = "#FF5F57FF";
        combo.IconName = "light";
        combo.RunOnStartup = true;
        var copy = ActionCombos.Duplicate(combo, "c2", Make.Ids());
        Assert.Equal(("c2", "c Copy", "#FF5F57FF", "light", (bool?)null), (copy.Id, copy.Name, copy.ColorHex, copy.IconName, copy.RunOnStartup));
        Assert.Equal(["n1"], copy.Actions.Select(a => a.Id));
        Assert.Equal("m", copy.Actions[0].MediaId);
        Assert.Equal("a", combo.Actions[0].Id);
        Assert.Equal("c", ActionCombos.Pasted(combo, "c3").Name);
    }

    [Fact]
    public void DropsAddFireMediaOrAnotherCombo()
    {
        Assert.Equal(SlideActionKind.FireMedia, ActionCombos.Dropped("media", "m", "self")?.Kind);
        Assert.Equal("m", ActionCombos.Dropped("media", "m", "self")?.MediaId);
        Assert.Equal("other", ActionCombos.Dropped("actionCombo", "other", "self")?.ComboId);
        Assert.Null(ActionCombos.Dropped("actionCombo", "self", "self"));
        Assert.Null(ActionCombos.Dropped("overlay", "o", "self"));
        Assert.Equal(["a", "b"], ActionCombos.Choosable([Combo("a"), Combo("self"), Combo("b")], "self").Select(c => c.Id));
    }

    [Fact]
    public void StepsMoveWithinTheListAndStopAtTheEnds()
    {
        List<SlideAction> actions = [Make.Action("a", SlideActionKind.ClearAll), Make.Action("b", SlideActionKind.ClearAudio), Make.Action("c", SlideActionKind.DismissAlert)];
        Assert.True(ActionCombos.Move(actions, "c", -1));
        Assert.Equal(["a", "c", "b"], actions.Select(a => a.Id));
        Assert.False(ActionCombos.Move(actions, "a", -1));
        Assert.False(ActionCombos.Move(actions, "b", 1));
        Assert.True(ActionCombos.Remove(actions, "c"));
        Assert.Equal(["a", "b"], actions.Select(a => a.Id));
    }

    [Fact]
    public void StoredValuesFollowTheMacsConventions()
    {
        var combo = Combo("c");
        ActionCombos.SetRunOnStartup(combo, true);
        Assert.True(combo.RunOnStartup);
        ActionCombos.SetRunOnStartup(combo, false);
        Assert.Null(combo.RunOnStartup);
        Assert.False(ActionCombos.Rename(combo, "  \n"));
        Assert.True(ActionCombos.Rename(combo, "  Walk-in \n"));
        Assert.Equal("Walk-in", combo.Name);

        var action = Make.Action("a", SlideActionKind.FireOverlay);
        ActionCombos.SetDelay(action, 0);
        Assert.Null(action.DelaySeconds);
        ActionCombos.SetDelay(action, 2.5);
        Assert.Equal(2.5, action.DelaySeconds);
        Assert.Null(ActionCombos.Stored(""));
        Assert.Equal(16, ActionCombos.Clamped(40, 1, 16));
        Assert.Null(ActionCombos.ChipSlideIndex(0));
        Assert.Equal(0, ActionCombos.StepperSlideIndex(0));
        Assert.Equal(185.0, ActionCombos.WithMinutes(65, 3));
        Assert.Equal(90.0, ActionCombos.WithSeconds(65, 30));
        Assert.Null(ActionCombos.FileRepeat(false));
        Assert.Equal(("", (bool?)null), (ActionCombos.RepeatToken(null), ActionCombos.RepeatFromToken("")));
        Assert.False(ActionCombos.RepeatFromToken("off"));
    }

    [Fact]
    public void ChipTitlesReadAsOnTheMac()
    {
        Assert.Equal("Keep Mode", ActionCombos.TimerModeTitle(null));
        Assert.Equal("Countdown to Time", ActionCombos.TimerModeTitle(TimerMode.CountdownToTime));
        Assert.Equal("Duration…", ActionCombos.DurationTitle(0));
        Assert.Equal("7:30", ActionCombos.DurationTitle(450));
        Assert.Equal("Repeat: Playlist Setting", ActionCombos.RepeatTitle(null));
        Assert.Equal("Built-in Layout", ActionCombos.ConfidenceLayoutTitle(null, _ => null));
        Assert.Equal("Missing", ActionCombos.ConfidenceLayoutTitle("gone", _ => null));
        Assert.Null(ActionCombos.EntryTitle("", _ => "x"));
        Assert.False(ActionCombos.HasParameters(SlideActionKind.DismissAlert));
        Assert.False(ActionCombos.HasParameters(SlideActionKind.ClearSignage));
        Assert.True(ActionCombos.HasParameters(SlideActionKind.MidiOut));
        Assert.Equal(LayerKind.Alerts, ActionCombos.ClearableLayers.First());
    }

    [Fact]
    public void ANewStepAndAKindChangeKeepWhatTheyShould()
    {
        var configure = ActionCombos.MakeConfigureTimer("t1", () => "id");
        Assert.Equal(("id", "t1", TimerMode.Countdown, (double?)300), (configure.Id, configure.TimerId, configure.TimerMode, configure.TimerDurationSeconds));
        var draft = Make.Action("keep", SlideActionKind.ClearAll, a => a.DelaySeconds = 4);
        var replaced = ActionCombos.Replacing(draft, Make.Action("other", SlideActionKind.FireOverlay, a => a.OverlayId = "o"));
        Assert.Equal(("keep", (double?)4, SlideActionKind.FireOverlay, "o"), (replaced.Id, replaced.DelaySeconds, replaced.Kind, replaced.OverlayId));
    }
}
