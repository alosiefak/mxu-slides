using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Animation;

// The Windows-only transitions' catalogue (Phase 6) and the rule that keeps shared documents
// readable on the Mac: a Windows-only kind is stored as its nearest Mac kind at the same length.
public class WindowsTransitionsTests
{
    private static readonly TransitionKind[] MacKinds = Enum.GetValues<TransitionKind>();

    [Fact]
    public void TheMacsSevenComeFirstAndRoundTripExactly()
    {
        Assert.Equal(7, WindowsTransitions.All.Count(WindowsTransitions.IsShared));
        Assert.Equal(18, WindowsTransitions.WindowsOnly.Count);
        foreach (var kind in MacKinds)
        {
            var shared = new Transition { TransitionKind = kind, DurationSeconds = 1.3, ColorHex = kind == TransitionKind.FadeColor ? "#FF0000" : null };
            var windows = WindowsTransitions.FromShared(shared);
            Assert.True(windows.IsShared);
            var back = windows.ToShared();
            Assert.Equal((kind, 1.3, shared.ColorHex), (back.TransitionKind, back.DurationSeconds, back.ColorHex));
        }
    }

    [Fact]
    public void AWindowsOnlyKindIsStoredAsTheNearestMacKindAtTheSameLength()
    {
        foreach (var kind in WindowsTransitions.WindowsOnly)
        {
            var shared = new WindowsTransition { Kind = kind, DurationSeconds = 1.25, ColorHex = "#FFAA00" }.ToShared();
            Assert.Contains(shared.TransitionKind, MacKinds);
            Assert.Equal(1.25, shared.DurationSeconds);
            Assert.Null(shared.ColorHex); // only Fade Color carries a colour on the Mac
        }
        Assert.Equal(TransitionKind.Dissolve, new WindowsTransition { Kind = WindowsTransitionKind.Push }.ToShared().TransitionKind); // usually a dissolve
        Assert.Equal(TransitionKind.FilmBurn, new WindowsTransition { Kind = WindowsTransitionKind.LightLeak }.ToShared().TransitionKind);
        Assert.Equal(TransitionKind.BlurDissolve, new WindowsTransition { Kind = WindowsTransitionKind.ZoomThrough }.ToShared().TransitionKind);
    }

    [Fact]
    public void TheSharedFallbackSerialisesAsAKindTheMacKnows()
    {
        // What the Mac reads: the shared document's JSON must only ever name one of its seven kinds.
        string[] macNames = ["cut", "dissolve", "fadeBlack", "fadeWhite", "fadeColor", "blurDissolve", "filmBurn"];
        foreach (var kind in WindowsTransitions.All)
        {
            var json = MxuJson.Serialize(new WindowsTransition { Kind = kind }.ToShared());
            Assert.Contains(macNames, name => json.Contains($"\"{name}\"", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void AWindowsTransitionIsSavedWithItsKindsByName()
    {
        var push = new WindowsTransition { Kind = WindowsTransitionKind.SlideOver, DurationSeconds = 0.9, Direction = TransitionDirection.Up, Softness = 0.2 };
        var json = MxuJson.Serialize(push);
        Assert.Contains("\"slideOver\"", json); // names, so a kind added later never changes what an old file means
        Assert.Contains("\"up\"", json);
        Assert.Equal(push, MxuJson.Deserialize<WindowsTransition>(json));
    }

    [Fact]
    public void EachKindSaysWhichSettingsItHas()
    {
        Assert.True(WindowsTransitions.HasDirection(WindowsTransitionKind.Push));
        Assert.True(WindowsTransitions.HasDirection(WindowsTransitionKind.Cube));
        Assert.False(WindowsTransitions.HasDirection(WindowsTransitionKind.Iris));
        Assert.True(WindowsTransitions.HasSoftness(WindowsTransitionKind.Iris));
        Assert.True(WindowsTransitions.HasSoftness(WindowsTransitionKind.SoftWipe));
        Assert.False(WindowsTransitions.HasSoftness(WindowsTransitionKind.Push));
        Assert.True(WindowsTransitions.HasColor(WindowsTransitionKind.LightLeak));
        Assert.True(WindowsTransitions.HasColor(WindowsTransitionKind.FadeColor));
        Assert.False(WindowsTransitions.HasDuration(WindowsTransitionKind.Cut));
        Assert.Equal(WindowsTransitions.LightLeakColor, WindowsTransitions.Color(new WindowsTransition { Kind = WindowsTransitionKind.LightLeak }));
        // Every kind has a name, a group and a description for the picker.
        foreach (var kind in WindowsTransitions.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(WindowsTransitions.Name(kind)));
            Assert.False(string.IsNullOrWhiteSpace(WindowsTransitions.Describe(kind)));
        }
        Assert.Equal(["Classic", "Move", "Zoom", "Wipe", "3-D", "Effects"], WindowsTransitions.All.Select(WindowsTransitions.Group).Distinct());
    }

    [Fact]
    public void CaptionsReadAsTheChipsAlwaysHave()
    {
        Assert.Equal("Fade White 0.6 s", WindowsTransitions.ChipCaption(new WindowsTransition { Kind = WindowsTransitionKind.FadeWhite, DurationSeconds = 0.6 }));
        Assert.Equal("Blur Dslv 1 s", WindowsTransitions.ChipCaption(new WindowsTransition { Kind = WindowsTransitionKind.BlurDissolve, DurationSeconds = 1 }));
        Assert.Equal("Cut", WindowsTransitions.ChipCaption(new WindowsTransition { Kind = WindowsTransitionKind.Cut }));
        Assert.Equal("Push ← 0.8 s", WindowsTransitions.ChipCaption(new WindowsTransition { Kind = WindowsTransitionKind.Push }));
        Assert.Equal("Cube Right 1.5 s", WindowsTransitions.Describe(new WindowsTransition { Kind = WindowsTransitionKind.Cube, Direction = TransitionDirection.Right, DurationSeconds = 1.5 }));
        Assert.Equal("Iris 1 s", WindowsTransitions.Describe(new WindowsTransition { Kind = WindowsTransitionKind.Iris, DurationSeconds = 1 }));
    }

    [Fact]
    public void ChangingTheKindKeepsTheOtherSettings()
    {
        var push = new WindowsTransition { Kind = WindowsTransitionKind.Push, DurationSeconds = 1.4, Direction = TransitionDirection.Down, Softness = 0.7 };
        var cube = push.WithKind(WindowsTransitionKind.Cube);
        Assert.Equal((WindowsTransitionKind.Cube, 1.4, TransitionDirection.Down, 0.7), (cube.Kind, cube.DurationSeconds, cube.Direction, cube.Softness));
        Assert.Equal(WindowsTransitions.DefaultSeconds, ((WindowsTransition?)null).WithKind(WindowsTransitionKind.Iris).DurationSeconds);
    }
}
