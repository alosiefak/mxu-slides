using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Tests.Automation;

// Ported from SlideSceneTests/ActionDelayTests.swift and FireActionsTests in
// SlideSceneTests/BackgroundSourcesMapTests.swift; plus MIDIOutTarget from
// PresenterCoreTests/MIDIControlTests.swift and C# tests of the labels.
public class SlideActionsTests
{
    private static SlideAction Step(SlideActionKind kind, string? id = null, string? layer = null, double? delay = null) =>
        Make.Action(id ?? Guid.NewGuid().ToString(), kind, a =>
        {
            a.Layer = layer;
            a.DelaySeconds = delay;
        });

    private static SlideAction Clear(string layer) => Step(SlideActionKind.ClearLayer, layer: layer);

    private static CueMedia Background(string mediaId, CueMediaLayer? layer = null) => new() { MediaId = mediaId, Layer = layer };

    private static Slide Slide(CueMedia? background = null) => new() { Id = "s", Name = "s", Objects = [], Background = background };

    [Fact]
    public void ScheduleSplitsAndOrdersBatches()
    {
        List<SlideAction> actions =
        [
            Step(SlideActionKind.ClearAll, "a"),
            Step(SlideActionKind.TimerConfigure, "b", delay: 5),
            Step(SlideActionKind.FireOverlay, "c"),
            Step(SlideActionKind.TimerStart, "d", delay: 5),
            Step(SlideActionKind.CaptureStop, "e", delay: 2),
        ];
        var schedule = SlideActions.DelaySchedule(actions);
        Assert.Equal(["a", "c"], schedule.Immediate.Select(a => a.Id));
        Assert.Equal([2.0, 5.0], schedule.Delayed.Select(b => b.Delay));
        Assert.Equal([["e"], ["b", "d"]], schedule.Delayed.Select(b => b.Actions.Select(a => a.Id).ToList()).ToList());
    }

    [Fact]
    public void ZeroAndAbsentDelaysAreImmediate()
    {
        List<SlideAction> actions = [Step(SlideActionKind.ClearAll, "a", delay: 0), Step(SlideActionKind.ClearAudio, "b")];
        var schedule = SlideActions.DelaySchedule(actions);
        Assert.Equal(["a", "b"], schedule.Immediate.Select(a => a.Id));
        Assert.Empty(schedule.Delayed);
        Assert.False(SlideActions.WaitsBeforeFiring(actions[0]));
    }

    [Fact]
    public void DelayedOwnLayerClearSurvivesTheFireFilter()
    {
        var deck = Slide();
        deck.Actions = [Step(SlideActionKind.ClearLayer, "now", "slide"), Step(SlideActionKind.ClearLayer, "later", "slide", 30)];
        Assert.Equal(["later"], SlideActions.FireActions(deck).Select(a => a.Id));
    }

    [Fact]
    public void DelayedOwnLayerClearSurvivesTheMediaFireFilter()
    {
        var item = Make.Media("m");
        item.Actions = [Step(SlideActionKind.ClearLayer, "now", "videos"), Step(SlideActionKind.ClearLayer, "later", "videos", 10)];
        Assert.Equal(["later"], SlideActions.FireActions(item, LayerKind.Videos).Select(a => a.Id));
    }

    [Fact]
    public void OwnBackgroundLayerClearIsDropped()
    {
        var deck = Slide(Background("give", CueMediaLayer.StillGraphics));
        deck.Actions = [Clear("loopingVideos"), Clear("stillGraphics"), Clear("videos"), Make.Action("combo", SlideActionKind.FireCombo, a => a.ComboId = "c")];
        Assert.Equal(["loopingVideos", "videos", "fireCombo"], SlideActions.FireActions(deck).Select(a => a.Layer ?? a.Kind.Raw()));
    }

    [Fact]
    public void SlideLayerClearNeverRunsOnOwnFire()
    {
        var deck = Slide();
        deck.Actions = [Clear("slide"), Clear("stillGraphics")];
        Assert.Equal(["stillGraphics"], SlideActions.FireActions(deck).Select(a => a.Layer));
    }

    [Fact]
    public void AbsentBackgroundLayerProtectsTheDefaultLayer()
    {
        var deck = Slide(Background("bg"));
        deck.Actions = [Clear("loopingVideos"), Clear("videos")];
        Assert.Equal(["videos"], SlideActions.FireActions(deck).Select(a => a.Layer));
    }

    [Fact]
    public void FireMediaTargetsJoinTheProtectedSet()
    {
        var deck = Slide(Background("bg"));
        deck.Actions = [Make.Action("f", SlideActionKind.FireMedia, a => a.MediaId = "logo"), Clear("stillGraphics"), Clear("videos")];
        var protectedLayers = SlideActions.ProtectedLayers(deck, id => id == "logo" ? LayerKind.StillGraphics : null);
        Assert.Equal(new HashSet<LayerKind> { LayerKind.Slide, LayerKind.LoopingVideos, LayerKind.StillGraphics }, protectedLayers);
        Assert.Equal(["fireMedia", "videos"], SlideActions.FireActions(deck, protectedLayers).Select(a => a.Layer ?? a.Kind.Raw()));
    }

    [Fact]
    public void UnresolvedFireMediaProtectsNothingExtra()
    {
        var deck = Slide();
        deck.Actions = [Make.Action("f", SlideActionKind.FireMedia, a => a.MediaId = "ghost")];
        Assert.Equal(new HashSet<LayerKind> { LayerKind.Slide }, SlideActions.ProtectedLayers(deck, _ => null));
    }

    [Fact]
    public void MidiOutTargetResolvesThroughItemsAndNeverFallsBackToBroadcast()
    {
        IReadOnlyList<MidiOutDevice> items = [new("lk", true, MIDIDeviceItemDirection.Output, "7")];
        Assert.IsType<MidiOutTarget.SettingsSelection>(MidiOutTarget.Resolve(null, items));
        Assert.IsType<MidiOutTarget.SettingsSelection>(MidiOutTarget.Resolve("", items));
        Assert.Equal(new MidiOutTarget.Device("7"), MidiOutTarget.Resolve("lk", items));
        Assert.Equal(new MidiOutTarget.Skip("device missing"), MidiOutTarget.Resolve("gone", items));
        Assert.Equal(new MidiOutTarget.Skip("device off"), MidiOutTarget.Resolve("lk", [items[0] with { IsEnabled = false }]));
        Assert.Equal(new MidiOutTarget.Skip("device is input-only"), MidiOutTarget.Resolve("lk", [new("lk", true, MIDIDeviceItemDirection.Input, null)]));
        Assert.Equal(new MidiOutTarget.Skip("port not present"), MidiOutTarget.Resolve("lk", [items[0] with { DestinationId = null }]));

        var device = new MIDIDevice { Id = "d", Name = "D" };
        Assert.Equal(new MidiOutDevice("d", true, MIDIDeviceItemDirection.Both, "p"), MidiOutDevice.From(device, "p"));
    }

    // C#: the bytes MIDIOutService.send put on the wire.
    [Fact]
    public void MidiOutMessagesCarryTheStepsDefaultsAndClampTheChannel()
    {
        var note = MidiOutMessage.From(Make.Action("m", SlideActionKind.MidiOut));
        Assert.Equal(new MidiOutMessage(MIDIMessageKind.NoteOn, 1, 0, 127), note);
        Assert.Equal([0x90, 0, 127], note.Bytes());
        Assert.Equal([0xBF, 7, 1], new MidiOutMessage(MIDIMessageKind.ControlChange, 99, 135, 129).Bytes());
        Assert.Equal([0x90, 60, 100], new MidiOutMessage(MIDIMessageKind.NoteOn, 0, 60, 100).Bytes());
    }

    // C#: slideActionLabel (PresentGridView.swift) and GlyphKind(action:) (Glyphs.swift).
    [Fact]
    public void LabelsNameTheTargetOrSayWhatIsMissing()
    {
        var names = new SlideActions.LabelNames(
            id => id == "lt" ? "Lower Third" : null,
            Timers: [("t1", "Sermon")],
            ConfidenceScreens: [("8A9B7C6D-0000-0000-0000-000000000001", "Booth")]);
        string Label(SlideActionKind kind, Action<SlideAction>? fill = null) => SlideActions.Label(Make.Action("x", kind, fill), names);

        Assert.Equal("Fire Overlay: Lower Third", Label(SlideActionKind.FireOverlay, a => a.OverlayId = "lt"));
        Assert.Equal("Fire Overlay: missing", Label(SlideActionKind.FireOverlay, a => a.OverlayId = "gone"));
        Assert.Equal("Fire Overlay: not set", Label(SlideActionKind.FireOverlay));
        Assert.Equal("Take Down Overlays", Label(SlideActionKind.DismissOverlay));
        Assert.Equal("Clear Background Media", Label(SlideActionKind.ClearLayer, a => a.Layer = "loopingVideos"));
        Assert.Equal("Clear Layer", Label(SlideActionKind.ClearLayer));
        Assert.Equal("Start Timer: Sermon", Label(SlideActionKind.TimerStart, a => a.TimerId = "t1"));
        Assert.Equal("Configure Timer: Sermon (Countdown 5:00)",
            Label(SlideActionKind.TimerConfigure, a => { a.TimerId = "t1"; a.TimerMode = TimerMode.Countdown; a.TimerDurationSeconds = 300; }));
        Assert.Equal("Configure Timer: missing (Until 9:30 AM)",
            Label(SlideActionKind.TimerConfigure, a => { a.TimerId = "t9"; a.TimerMode = TimerMode.CountdownToTime; a.TimerHour = 9; a.TimerMinute = 30; a.TimerDurationSeconds = 60; }));
        Assert.Equal("MIDI Out: ch 3", Label(SlideActionKind.MidiOut, a => a.MidiChannel = 3));
        Assert.Equal("Stop All Capture", Label(SlideActionKind.CaptureStop));
        Assert.Equal("Confidence Monitor: Built-in Layout", Label(SlideActionKind.SetConfidenceLayout));
        Assert.Equal("Confidence Monitor: Built-in Layout on Booth",
            Label(SlideActionKind.SetConfidenceLayout, a => a.ConfidenceScreenId = "8a9b7c6d-0000-0000-0000-000000000001"));
        Assert.Equal("Signage (removed)", Label(SlideActionKind.SetSignage));
        Assert.Equal("Audio Input On: not set", Label(SlideActionKind.EnableAudioInput));
        Assert.Equal("Music Playlist: not set", Label(SlideActionKind.FireAudioPlaylist));
        Assert.Equal("Take Down Overlay", SlideActionKind.DismissOverlay.DisplayName());
        Assert.Equal("Music File", SlideActionKind.FireAudio.DisplayName());
    }

    [Fact]
    public void EachStepWearsItsIconAndAComboItsOwn()
    {
        Assert.Equal("screens", SlideActions.IconName(Make.Action("x", SlideActionKind.SwitchOutputPreset)));
        Assert.Equal("clear", SlideActions.IconName(Make.Action("x", SlideActionKind.ClearAudio)));
        Assert.Equal("audio", SlideActions.IconName(Make.Action("x", SlideActionKind.FireAudioPlaylist)));
        var fire = Make.Action("x", SlideActionKind.FireCombo, a => a.ComboId = "c");
        Assert.Equal("light", SlideActions.IconName(fire, _ => "light"));
        Assert.Equal("combos", SlideActions.IconName(fire, _ => "nonsense"));
        Assert.Equal("combos", SlideActions.IconName(fire));
    }

    [Fact]
    public void AnOldPlaylistIsJudgedByItsFirstEntry()
    {
        var playlist = new Playlist { Id = "p", Name = "p", PlaybackMode = PlaybackMode.PlayAll };
        Assert.Equal(PlaylistKind.Audio, playlist.EffectiveKind());
        playlist.Entries = [new PlaylistEntry { Id = "e", RefKind = PlaylistRefKind.Media, RefId = "m" }];
        Assert.Equal(PlaylistKind.Media, playlist.EffectiveKind());
        playlist.PlaylistKind = PlaylistKind.Audio;
        Assert.Equal(PlaylistKind.Audio, playlist.EffectiveKind());
    }
}
