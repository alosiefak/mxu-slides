using MxU.Slides.Core.Model;
using MxU.Slides.Core.Stage;

namespace MxU.Slides.Core.Tests.Stage;

// Port of the VisibilityRules.isShown cases in SlideSceneTests/SlideSceneBuilderTests.swift.
public class VisibilityRulesTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private static VisibilityCondition TimerCondition(VisibilityConditionState state, string? timerId = "T1") =>
        new() { ConditionKind = VisibilityConditionKind.Timer, State = state, TimerId = timerId };

    private static VisibilityCondition Condition(VisibilityConditionKind kind, VisibilityConditionState state, string? objectId = null, string? liveInputId = null) =>
        new() { ConditionKind = kind, State = state, ObjectId = objectId, LiveInputId = liveInputId };

    private static SlideObject Conditioned(List<VisibilityCondition> conditions, VisibilityMatch? match = null) => new()
    {
        Id = "c", ObjectKind = SlideObjectKind.Text, Name = "Timer Box", Text = "0:00",
        VisibilityMatch = match, VisibilityConditions = conditions,
    };

    private static SlideObject Text(string id, string text) => new() { Id = id, ObjectKind = SlideObjectKind.Text, Name = "Next", Text = text };

    [Fact]
    public void TimerConditionsFollowRunningState()
    {
        var running = new TimerSnapshot("Worship", TimerMode.Countdown, id: "T1", isRunning: true, runningSince: Now, durationSeconds: 300);
        var parked = new TimerSnapshot("Video", TimerMode.Countdown, id: "T2", durationSeconds: 60);
        var info = new ConfidenceInfo { Timers = [running, parked] };

        Assert.True(VisibilityRules.IsShown(Conditioned([TimerCondition(VisibilityConditionState.IsRunning, "T1")]), [], info, Now));
        Assert.False(VisibilityRules.IsShown(Conditioned([TimerCondition(VisibilityConditionState.IsRunning, "T2")]), [], info, Now));

        var expired = new TimerSnapshot("Worship", TimerMode.Countdown, id: "T1", isRunning: true, runningSince: Now.AddSeconds(-400), durationSeconds: 300);
        var late = new ConfidenceInfo { Timers = [expired] };
        Assert.False(VisibilityRules.IsShown(Conditioned([TimerCondition(VisibilityConditionState.HasTimeRemaining)]), [], late, Now));
        Assert.True(VisibilityRules.IsShown(Conditioned([TimerCondition(VisibilityConditionState.HasExpired)]), [], late, Now));
    }

    [Fact]
    public void MatchModesCombineAndNoneInverts()
    {
        var running = new TimerSnapshot("A", TimerMode.Countdown, id: "T1", isRunning: true, runningSince: Now, durationSeconds: 300);
        var info = new ConfidenceInfo { Timers = [running] };
        var met = TimerCondition(VisibilityConditionState.IsRunning);
        var unmet = TimerCondition(VisibilityConditionState.IsNotRunning);

        Assert.False(VisibilityRules.IsShown(Conditioned([met, unmet]), [], info, Now));
        Assert.True(VisibilityRules.IsShown(Conditioned([met, unmet], VisibilityMatch.Any), [], info, Now));
        Assert.False(VisibilityRules.IsShown(Conditioned([met], VisibilityMatch.None), [], info, Now));
        Assert.True(VisibilityRules.IsShown(Conditioned([unmet], VisibilityMatch.None), [], info, Now));
    }

    [Fact]
    public void UnevaluableConditionsFailVisible()
    {
        var info = new ConfidenceInfo();
        var dangling = TimerCondition(VisibilityConditionState.IsRunning, "missing");
        var future = Condition(VisibilityConditionKind.LiveInput, VisibilityConditionState.IsActive);

        Assert.True(VisibilityRules.IsShown(Conditioned([dangling]), [], info, Now));
        Assert.True(VisibilityRules.IsShown(Conditioned([future]), [], info, Now));
        Assert.True(VisibilityRules.IsShown(Conditioned([dangling], VisibilityMatch.Any), [], info, Now));
        Assert.True(VisibilityRules.IsShown(Conditioned([future], VisibilityMatch.None), [], info, Now));
    }

    [Fact]
    public void VideoCountdownAbsenceIsAStateNotAnUnknown()
    {
        var videoBox = Conditioned([Condition(VisibilityConditionKind.VideoCountdown, VisibilityConditionState.HasTimeRemaining)]);
        Assert.False(VisibilityRules.IsShown(videoBox, [], new ConfidenceInfo(), Now));

        var playing = new ConfidenceInfo { VideoCountdown = new VideoCountdown("Bumper", 120, 10, Now, IsPlaying: true) };
        Assert.True(VisibilityRules.IsShown(videoBox, [], playing, Now));
    }

    [Fact]
    public void ObjectTextConditionsReadResolvedSiblings()
    {
        var sibling = Text("s", "Amazing grace");
        var empty = Text("e", "  ");
        var box = Conditioned([Condition(VisibilityConditionKind.ObjectText, VisibilityConditionState.HasText, objectId: "s")]);
        Assert.True(VisibilityRules.IsShown(box, [sibling], new ConfidenceInfo(), Now));
        var boxOnEmpty = Conditioned([Condition(VisibilityConditionKind.ObjectText, VisibilityConditionState.HasNoText, objectId: "e")]);
        Assert.True(VisibilityRules.IsShown(boxOnEmpty, [empty], new ConfidenceInfo(), Now));
        var unnamed = Conditioned([Condition(VisibilityConditionKind.ObjectText, VisibilityConditionState.HasText)]);
        Assert.True(VisibilityRules.IsShown(unnamed, [], new ConfidenceInfo(), Now)); // no object picked: never hides
    }

    [Fact]
    public void AudioInputAndCaptureConditionsEvaluate()
    {
        var audioBox = Conditioned([Condition(VisibilityConditionKind.AudioPlayback, VisibilityConditionState.IsRunning)]);
        Assert.False(VisibilityRules.IsShown(audioBox, [], new ConfidenceInfo(), Now));
        var playing = new ConfidenceInfo { AudioCountdown = new VideoCountdown("Walk-in", 180, 30, Now, IsPlaying: true) };
        Assert.True(VisibilityRules.IsShown(audioBox, [], playing, Now));

        var inputBox = Conditioned([Condition(VisibilityConditionKind.LiveInput, VisibilityConditionState.IsActive, liveInputId: "in1")]);
        Assert.False(VisibilityRules.IsShown(inputBox, [], new ConfidenceInfo(), Now));
        Assert.True(VisibilityRules.IsShown(inputBox, [], new ConfidenceInfo { ActiveLiveInputIds = ["in1"] }, Now));

        var unpicked = Conditioned([Condition(VisibilityConditionKind.LiveInput, VisibilityConditionState.IsActive)]);
        Assert.True(VisibilityRules.IsShown(unpicked, [], new ConfidenceInfo(), Now));

        var recBox = Conditioned([Condition(VisibilityConditionKind.Capture, VisibilityConditionState.IsActive)]);
        Assert.False(VisibilityRules.IsShown(recBox, [], new ConfidenceInfo(), Now));
        Assert.True(VisibilityRules.IsShown(recBox, [], new ConfidenceInfo { CaptureActive = true }, Now));
    }

    [Fact]
    public void ConnectedInputConditionsEvaluateIndependentlyOfActive()
    {
        var badge = Conditioned([Condition(VisibilityConditionKind.LiveInput, VisibilityConditionState.IsConnected, liveInputId: "in1")]);
        var disconnected = new ConfidenceInfo { ActiveLiveInputIds = ["in1"] };
        Assert.False(VisibilityRules.IsShown(badge, [], disconnected, Now));
        var connected = new ConfidenceInfo { ConnectedLiveInputIds = ["in1"] };
        Assert.True(VisibilityRules.IsShown(badge, [], connected, Now));

        var warning = Conditioned([Condition(VisibilityConditionKind.LiveInput, VisibilityConditionState.IsDisconnected, liveInputId: "in1")]);
        Assert.True(VisibilityRules.IsShown(warning, [], disconnected, Now));
        Assert.False(VisibilityRules.IsShown(warning, [], connected, Now));
    }

    [Fact]
    public void ItemVisibilityMapsTheSchema()
    {
        Assert.Null(VisibilityRules.ItemVisibilityFor(Text("x", "plain")));
        Assert.Null(VisibilityRules.ItemVisibilityFor(Conditioned([])));
        var visibility = VisibilityRules.ItemVisibilityFor(Conditioned(
        [
            TimerCondition(VisibilityConditionState.IsInactive, ""),
            Condition(VisibilityConditionKind.ObjectText, VisibilityConditionState.HasText),
            Condition(VisibilityConditionKind.LiveInput, VisibilityConditionState.IsDisconnected, liveInputId: "in1"),
        ]))!;
        Assert.Equal(VisibilityMatch.All, visibility.Match);
        Assert.Equal(new ItemVisibility.Condition(new ItemVisibility.ConditionKind.Timer(null), ItemVisibility.RequiredState.IsNotRunning), visibility.Conditions[0]);
        Assert.IsType<ItemVisibility.ConditionKind.Unevaluable>(visibility.Conditions[1].Kind);
        Assert.Equal(new ItemVisibility.Condition(new ItemVisibility.ConditionKind.LiveInput("in1"), ItemVisibility.RequiredState.IsNotConnected), visibility.Conditions[2]);
    }
}
