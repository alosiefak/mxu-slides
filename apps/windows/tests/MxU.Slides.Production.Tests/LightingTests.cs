using MxU.Slides.Production.Lighting;

namespace MxU.Slides.Production.Tests;

// The fade and merge maths: crossfades, HTP and LTP, the grand master, blackout, the programmer.
public class DmxMixerTests
{
    private static LightingLook Look(string id, params (int Channel, int Value)[] levels) =>
        new() { Id = id, Name = id, Levels = [.. levels.Select(l => new LookLevel(1, l.Channel, l.Value))] };

    private static byte At(DmxMixer mixer, double now, int channel) => mixer.Frame(now)[1][channel - 1];

    [Fact]
    public void NothingIsActiveUntilALookTouchesAUniverse()
    {
        var mixer = new DmxMixer();
        mixer.AllOff(2, 0);
        mixer.Blackout = true;
        Assert.False(mixer.IsActive);
        Assert.Empty(mixer.Frame(0));
        mixer.Fire(Look("a", (1, 10)), 0, 0);
        Assert.True(mixer.IsActive);
        Assert.Equal([1], mixer.Universes);
    }

    [Fact]
    public void FiringALookCrossfadesInAStraightLine()
    {
        var mixer = new DmxMixer();
        mixer.Fire(Look("a", (1, 200)), 0, 0);
        Assert.Equal(200, At(mixer, 0, 1));
        mixer.Fire(Look("b", (2, 100)), 2, 10);
        Assert.Equal((200, 0), (At(mixer, 10, 1), At(mixer, 10, 2)));
        Assert.Equal((100, 50), (At(mixer, 11, 1), At(mixer, 11, 2)));
        Assert.Equal((0, 100), (At(mixer, 12, 1), At(mixer, 12, 2)));
        Assert.Equal("b", mixer.MainLookId);
    }

    [Fact]
    public void ALookFiredHalfWayThroughAFadeStartsFromWhereItHadGot()
    {
        var mixer = new DmxMixer();
        mixer.Fire(Look("a", (1, 200)), 0, 0);
        mixer.Fire(Look("b", (2, 100)), 2, 10);
        mixer.Fire(Look("c", (1, 255)), 1, 11);     // channel 1 was at 100, channel 2 at 50
        Assert.Equal((178, 25), (At(mixer, 11.5, 1), At(mixer, 11.5, 2)));
        Assert.Equal((255, 0), (At(mixer, 12, 1), At(mixer, 12, 2)));
    }

    [Fact]
    public void ALookOnTopMergesHighestTakesPrecedenceAndFadesOutWhenReleased()
    {
        var mixer = new DmxMixer();
        mixer.Fire(Look("stage", (1, 100)), 0, 0);
        mixer.Add(Look("house", (1, 200), (3, 50)), 2, 0);
        Assert.Equal((100, 25), (At(mixer, 1, 1), At(mixer, 1, 3)));   // 200 × ½ = 100: no brighter than the stage yet
        Assert.Equal((200, 50), (At(mixer, 2, 1), At(mixer, 2, 3)));
        Assert.Equal(["house"], mixer.LayerLookIds);
        mixer.Release("house", 1, 5);
        Assert.Equal((100, 25), (At(mixer, 5.5, 1), At(mixer, 5.5, 3)));
        Assert.Equal((100, 0), (At(mixer, 6, 1), At(mixer, 6, 3)));
        Assert.Empty(mixer.LayerLookIds);
    }

    [Fact]
    public void ReAddingALookBeingLetGoTurnsRoundWithoutAJump()
    {
        var mixer = new DmxMixer();
        mixer.Add(Look("house", (1, 200)), 2, 0);
        mixer.Release("house", 2, 10);
        Assert.Equal(100, At(mixer, 11, 1));
        mixer.Add(Look("house", (1, 200)), 2, 11);
        Assert.Equal(100, At(mixer, 11, 1));
        Assert.Equal(150, At(mixer, 11.5, 1));
    }

    [Fact]
    public void AttributesMergeLatestTakesPrecedenceAndHoldWhenANewLookLeavesThemOut()
    {
        var mixer = new DmxMixer();
        mixer.SetAttributes([(1, 10)]);
        mixer.Fire(Look("blue", (1, 200), (10, 50)), 0, 0);
        // On top, channel 10 is an attribute: it blends towards the newer look's value as it fades in (even downwards would win).
        mixer.Add(Look("red", (10, 250)), 2, 0);
        Assert.Equal(150, At(mixer, 1, 10));
        Assert.Equal(250, At(mixer, 2, 10));
        // A main look that doesn't mention channel 10 leaves it where it was; brightness it doesn't mention goes out.
        var fresh = new DmxMixer();
        fresh.SetAttributes([(1, 10)]);
        fresh.Fire(Look("blue", (1, 200), (10, 50)), 0, 0);
        fresh.Fire(Look("dim", (2, 30)), 0, 1);
        Assert.Equal((0, 30, 50), (At(fresh, 1, 1), At(fresh, 1, 2), At(fresh, 1, 10)));
    }

    [Fact]
    public void TheGrandMasterAndBlackoutDimBrightnessButNotAttributes()
    {
        var mixer = new DmxMixer();
        mixer.SetAttributes([(1, 10)]);
        mixer.Fire(Look("a", (1, 200), (10, 80)), 0, 0);
        mixer.SetGrandMaster(0.5, 2, 0);
        Assert.Equal((150, 80), (At(mixer, 1, 1), At(mixer, 1, 10)));
        Assert.Equal((100, 80), (At(mixer, 2, 1), At(mixer, 2, 10)));
        mixer.Blackout = true;
        Assert.Equal((0, 80), (At(mixer, 3, 1), At(mixer, 3, 10)));
        mixer.Blackout = false;
        Assert.Equal(100, At(mixer, 3, 1));
    }

    [Fact]
    public void TheProgrammerOverridesEverythingUntilLetGo()
    {
        var mixer = new DmxMixer();
        mixer.Fire(Look("a", (1, 200)), 0, 0);
        mixer.Add(Look("b", (1, 255)), 0, 0);
        mixer.SetProgrammer([new LookLevel(1, 1, 10), new LookLevel(2, 4, 99)]);
        Assert.Equal(10, At(mixer, 1, 1));
        Assert.Equal(99, mixer.Frame(1)[2][3]);
        mixer.SetProgrammer(null);
        Assert.Equal(255, At(mixer, 1, 1));
    }

    [Fact]
    public void AllOffFadesTheMainLookAndLetsGoOfEveryLookOnTop()
    {
        var mixer = new DmxMixer();
        mixer.Fire(Look("a", (1, 200)), 0, 0);
        mixer.Add(Look("b", (2, 100)), 0, 0);
        mixer.AllOff(2, 10);
        Assert.Equal((100, 50), (At(mixer, 11, 1), At(mixer, 11, 2)));
        Assert.Equal((0, 0), (At(mixer, 12, 1), At(mixer, 12, 2)));
        Assert.Null(mixer.MainLookId);
        Assert.False(mixer.IsMoving(12));
    }
}

public class LightingControllerTests
{
    private double now;

    private LightingController Controller()
    {
        var controller = new LightingController(() => now);
        controller.Load(new LightingShow
        {
            Looks =
            [
                new LightingLook { Id = "walk", Name = "Walk-in", FadeSeconds = 2, Levels = [new(1, 1, 100)] },
                new LightingLook { Id = "sermon", Name = "Sermon", FadeSeconds = 4, Levels = [new(1, 1, 200)] },
            ],
            Cues =
            [
                new LightingCue { Id = "q1", LookId = "walk" },
                new LightingCue { Id = "q2", LookId = "sermon", FadeSeconds = 1 },
            ],
        });
        return controller;
    }

    [Fact]
    public void GoStepsThroughTheCueListWithEachCuesFade()
    {
        var lights = Controller();
        Assert.Null(lights.CurrentCue);
        Assert.Equal("q1", lights.NextCue?.Id);
        Assert.True(lights.Go());
        Assert.Equal("q1", lights.CurrentCue?.Id);
        now = 2;
        Assert.Equal(100, lights.Frame()[1][0]);
        Assert.True(lights.Go());
        now = 2.5;
        Assert.Equal(150, lights.Frame()[1][0]);   // the cue's own 1 s fade, not the look's 4 s
        Assert.False(lights.Go());                // the end of the list
        Assert.True(lights.Back());
        Assert.Equal("q1", lights.CurrentCue?.Id);
        Assert.True(lights.Go("q2"));
        Assert.Equal("q2", lights.CurrentCue?.Id);
    }

    [Fact]
    public void ALookUsesItsOwnFadeUnlessAStepGivesOne()
    {
        var lights = Controller();
        Assert.True(lights.FireLook("sermon"));
        now = 2;
        Assert.Equal(100, lights.Frame()[1][0]);
        Assert.True(lights.FireLook("walk", 0));
        Assert.Equal(100, lights.Frame()[1][0]);
        Assert.False(lights.FireLook("missing"));
    }

    [Fact]
    public void TheGrandMasterIsInPerCent()
    {
        var lights = Controller();
        lights.FireLook("sermon", 0);
        lights.SetGrandMaster(25);
        Assert.Equal(50, lights.Frame()[1][0]);
        Assert.Equal(0.25, lights.GrandMaster, 3);
        lights.SetBlackout(null);
        Assert.True(lights.Blackout);
        lights.SetBlackout(null);
        Assert.False(lights.Blackout);
    }

    [Fact]
    public void ChannelListsReadTheWayLightingPeopleTypeThem()
    {
        Assert.Equal([1, 2, 3, 4, 5, 6, 9, 12, 13], ChannelList.Parse("1-6, 9, 12 - 13"));
        Assert.Equal([3, 4], ChannelList.Parse("4-3, nonsense, 0, 600"));
        Assert.Equal("1-6, 9, 12-13", ChannelList.Format([9, 1, 2, 3, 4, 5, 6, 12, 13]));
        Assert.Equal("", ChannelList.Format([]));
    }

    [Fact]
    public void GroupsMarkedAsAttributesAreTheLatestTakesPrecedenceChannels()
    {
        var show = new LightingShow
        {
            Groups =
            [
                new ChannelGroup { Name = "Wash", Universe = 1, Channels = [1, 2] },
                new ChannelGroup { Name = "Colour", Universe = 2, Channels = [7, 8], Attribute = true },
            ],
        };
        Assert.Equal([(2, 7), (2, 8)], show.AttributeChannels().Order());
    }
}
