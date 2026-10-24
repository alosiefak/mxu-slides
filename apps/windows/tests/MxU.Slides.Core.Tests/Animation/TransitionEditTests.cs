using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Animation;

// Port of SlideSceneTests/TransitionEditTests.swift.
public class TransitionEditTests
{
    private static Transition T(TransitionKind kind, double? seconds = null, string? color = null) =>
        new() { TransitionKind = kind, DurationSeconds = seconds, ColorHex = color };

    [Fact]
    public void DurationChoicesKeepAnOffPresetCurrentValue()
    {
        Assert.Equal([0.5, 1, 1.5, 2, 3], TransitionEdit.DurationChoices(null));
        Assert.Equal([0.5, 0.6, 1, 1.5, 2, 3], TransitionEdit.DurationChoices(T(TransitionKind.Dissolve, 0.6000000238)));
    }

    [Fact]
    public void LabelsReadInSeconds()
    {
        Assert.Equal("1 s", TransitionEdit.Label(1));
        Assert.Equal("1.5 s", TransitionEdit.Label(1.5));
        Assert.Equal("0.41 s", TransitionEdit.Label(0.4115));
    }

    [Fact]
    public void KindAndDurationEdits()
    {
        var dissolve = T(TransitionKind.Dissolve, 2, "#FF0000FF");
        Assert.Null(TransitionEdit.WithKind(null, dissolve)); // nil kind = room default
        var fade = TransitionEdit.WithKind(TransitionKind.FadeColor, dissolve)!;
        Assert.Equal((TransitionKind.FadeColor, 2.0, "#FF0000FF"), (fade.TransitionKind, fade.DurationSeconds!.Value, fade.ColorHex));
        Assert.Equal(12, TransitionEdit.WithDuration(12, dissolve)?.DurationSeconds); // typed past the slider
        Assert.Equal(30, TransitionEdit.WithDuration(45, dissolve)?.DurationSeconds); // clamped to what can be typed
        Assert.Null(TransitionEdit.WithDuration(1, null)); // no transition stays none
    }

    [Fact]
    public void EntranceOrderOwnThenPresentation()
    {
        var own = T(TransitionKind.Cut);
        var deck = T(TransitionKind.Dissolve, 1);
        Assert.Same(own, TransitionEdit.Entrance(own, deck)); // own wins, even a cut
        Assert.Same(deck, TransitionEdit.Entrance(null, deck));
        Assert.Null(TransitionEdit.Entrance(null, null)); // room default
    }

    [Fact]
    public void MenuValueNamesTheLevelItFollows()
    {
        var deck = T(TransitionKind.Dissolve, 1);
        Assert.Equal("Room Default", TransitionEdit.MenuValue(null));
        Assert.Equal("Presentation Default (Dissolve 1 s)", TransitionEdit.MenuValue(null, inherited: deck));
        Assert.Equal("Cut", TransitionEdit.MenuValue(T(TransitionKind.Cut), inherited: deck));
        Assert.Equal("Mixed", TransitionEdit.MenuValue(deck, mixed: true));
    }
}
