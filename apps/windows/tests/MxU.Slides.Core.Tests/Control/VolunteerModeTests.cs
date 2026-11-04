using MxU.Slides.Core.Control;
using static MxU.Slides.Core.Control.VolunteerMode;

namespace MxU.Slides.Core.Tests.Control;

// Ported from PresenterCoreTests/VolunteerModeLogicTests.swift (the switcher), plus the run-only
// rules the Mac's RunOnlySweepTests checks every view asks, and Windows' optional PIN.
public class VolunteerModeTests
{
    [Fact]
    public void OfferedOnlyWhenTurnedOn()
    {
        Assert.Equal([Choice.Show, Choice.Edit], Choices(enabled: false));
        Assert.Equal([Choice.Show, Choice.Edit, Choice.Volunteer], Choices(enabled: true));
    }

    [Fact]
    public void NoPinToEnterAndOnlyAskedForWhenSet()
    {
        Assert.Equal(new VolunteerMode.Action.EnterVolunteer(), Picking(Choice.Volunteer, volunteerOn: false, hasPin: true));
        Assert.Equal(new VolunteerMode.Action.LeaveVolunteer(Choice.Show, false), Picking(Choice.Show, volunteerOn: true, hasPin: false));
        Assert.Equal(new VolunteerMode.Action.LeaveVolunteer(Choice.Edit, true), Picking(Choice.Edit, volunteerOn: true, hasPin: true));
        Assert.Equal(new VolunteerMode.Action.Nothing(), Picking(Choice.Volunteer, volunteerOn: true, hasPin: true));
        Assert.Equal(new VolunteerMode.Action.SwitchMode(Choice.Edit), Picking(Choice.Edit, volunteerOn: false, hasPin: true));
    }

    [Fact]
    public void TheSwitcherSaysVolunteerWhileItsOn()
    {
        Assert.Equal(Choice.Volunteer, Selected(editing: false, volunteerOn: true));
        Assert.Equal(Choice.Edit, Selected(editing: true, volunteerOn: false));
        Assert.Equal("Volunteer", Choice.Volunteer.Title());
    }

    [Fact]
    public void ThePinIsStoredAsASaltedHash()
    {
        var stored = HashPin("2468", iterations: 1000);
        Assert.DoesNotContain("2468", stored);
        Assert.StartsWith("pbkdf2:1000:", stored);
        Assert.True(PinMatches(stored, "2468"));
        Assert.False(PinMatches(stored, "2469"));
        Assert.False(PinMatches(stored, ""));
        // The same PIN hashes differently each time (a new salt), and both still match.
        var again = HashPin("2468", iterations: 1000);
        Assert.NotEqual(stored, again);
        Assert.True(PinMatches(again, "2468"));
    }

    [Fact]
    public void NoPinLetsAnyoneLeaveAndADamagedOneLetsNobody()
    {
        Assert.True(PinMatches(null, "anything"));
        Assert.True(PinMatches("", ""));
        Assert.False(PinMatches("pbkdf2:1000:not base64!:x", "1234"));
        Assert.False(PinMatches("sha1:abc", "1234"));
    }

    [Fact]
    public void APinIsFourToEightDigits()
    {
        Assert.Null(PinProblem("1234"));
        Assert.Null(PinProblem("12345678"));
        Assert.NotNull(PinProblem("123"));
        Assert.NotNull(PinProblem("123456789"));
        Assert.NotNull(PinProblem("12a4"));
    }

    [Fact]
    public void PanelsVolunteersKeepRoundTrip()
    {
        Assert.Equal(VolunteerPanels.All, VolunteerPanels.Parse(null));
        Assert.Equal([VolunteerPanel.Timers, VolunteerPanel.Combos], VolunteerPanels.Parse("combos, timers,bogus,timers"));
        Assert.Equal("timers,stageMessage", VolunteerPanels.Format([VolunteerPanel.StageMessage, VolunteerPanel.Timers]));
        Assert.Empty(VolunteerPanels.Parse(""));
        Assert.Equal("Service Tracking", VolunteerPanel.ServiceTracking.Title());
    }

    // The Mac's "Libraries volunteers keep" (LibraryPresentLogic.volunteerKinds): kept by default,
    // and a list saved before it existed stays as the operator chose it.
    [Fact]
    public void VolunteersKeepTheLibrarysDecksUnlessTheOperatorChoseOtherwise()
    {
        Assert.Contains(VolunteerPanel.Presentations, VolunteerPanels.Parse(null));
        Assert.DoesNotContain(VolunteerPanel.Presentations, VolunteerPanels.Parse("timers,overlays,media,music"));
        Assert.Equal("overlays,presentations", VolunteerPanels.Format([VolunteerPanel.Presentations, VolunteerPanel.Overlays]));
        Assert.Equal([VolunteerPanel.Presentations], VolunteerPanels.Parse("presentations"));
        Assert.True(RunOnly.Allows(OperatorAction.PlayFromLibrary, runOnly: true));
    }

    [Fact]
    public void RunOnlyRunsTheShowAndChangesNothing()
    {
        foreach (var action in new[] { OperatorAction.FireSlide, OperatorAction.NextPrevious, OperatorAction.Clear, OperatorAction.StartPauseResetTimer,
                     OperatorAction.FireAlert, OperatorAction.ToggleOverlay, OperatorAction.RunCombo, OperatorAction.SelectService, OperatorAction.ChoosePlanTime })
            Assert.True(RunOnly.Allows(action, runOnly: true), action.ToString());
        foreach (var action in new[] { OperatorAction.EditSlides, OperatorAction.EditLibrary, OperatorAction.EditService, OperatorAction.ChangeArrangement,
                     OperatorAction.ReorderBoards, OperatorAction.ChangeScreens, OperatorAction.ChangeLook, OperatorAction.ChangeSettings,
                     OperatorAction.ChangeShortcuts, OperatorAction.Broadcast, OperatorAction.EditTimers, OperatorAction.EditAlerts })
            Assert.False(RunOnly.Allows(action, runOnly: true), action.ToString());
        // Out of volunteer mode everything is allowed.
        Assert.All(Enum.GetValues<OperatorAction>(), action => Assert.True(RunOnly.Allows(action, runOnly: false)));
    }
}
