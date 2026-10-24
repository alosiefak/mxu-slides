using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.Rendering;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// Present settings with a control of their own: "Show a repeated chord-only section once",
/// "Confidence NEXT skips blank slides" (Settings › Present) and the room default transitions,
/// which the live header's two chips change (the Mac's TransitionChip). They save at once.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The operator panels' start-up: alerts, the present settings, and (once the outputs exist) the screens.</summary>
    private void InitializeOperator()
    {
        SlideSceneBuilder.EmptyRepeatsOnce = Settings.EmptyRepeatsOnce;
        InitializeAlerts();
    }

    /// <summary>Raised when a room default transition changes (the header chips and Settings show it).</summary>
    public event Action? TransitionsChanged;

    public bool EmptyRepeatsOnce
    {
        get => Settings.EmptyRepeatsOnce;
        set
        {
            if (Settings.EmptyRepeatsOnce == value) return;
            Settings.EmptyRepeatsOnce = value;
            SlideSceneBuilder.EmptyRepeatsOnce = value;
            Settings.Save();
            OnPropertyChanged();
            // The played slides change: the grid, the stage's NEXT and the live position follow at once.
            RebuildSlides();
            RenderOutputs(SceneTransition.Cut, refreshOnly: true);
        }
    }

    public bool NextSkipsBlanks
    {
        get => Settings.NextSkipsBlanks;
        set
        {
            if (Settings.NextSkipsBlanks == value) return;
            Settings.NextSkipsBlanks = value;
            Settings.Save();
            OnPropertyChanged();
            RenderStage(SceneTransition.Cut, refreshOnly: true);
        }
    }

    /// <summary>The slide or media layer's room default, as the chip and Settings › Transitions hold it.</summary>
    public (OutputTransition Kind, double Seconds) RoomTransitionSetting(bool media) =>
        media ? (Settings.MediaTransition, Settings.MediaTransitionSeconds) : (Settings.Transition, Settings.TransitionSeconds);

    /// <summary>The chips change the same settings as Settings › Transitions; the next cue that doesn't name its own uses them.</summary>
    public void SetRoomTransition(bool media, OutputTransition kind, double seconds)
    {
        var rounded = Math.Round(Math.Clamp(seconds, 0.1, Core.Animation.TransitionEdit.SliderMaximum), 1, MidpointRounding.AwayFromZero);
        if (RoomTransitionSetting(media) == (kind, rounded)) return;
        if (media)
        {
            Settings.MediaTransition = kind;
            Settings.MediaTransitionSeconds = rounded;
        }
        else
        {
            Settings.Transition = kind;
            Settings.TransitionSeconds = rounded;
        }
        Settings.Save();
        TransitionsChanged?.Invoke();
    }

    /// <summary>The Core's name for a room default (the chips' wording comes from Core/Animation/TransitionChips.cs).</summary>
    public static TransitionKind KindOf(OutputTransition transition) => transition switch
    {
        OutputTransition.Cut => TransitionKind.Cut,
        OutputTransition.FadeThroughBlack => TransitionKind.FadeBlack,
        OutputTransition.FadeThroughWhite => TransitionKind.FadeWhite,
        OutputTransition.BlurDissolve => TransitionKind.BlurDissolve,
        _ => TransitionKind.Dissolve,
    };

    public static OutputTransition RoomTransitionOf(TransitionKind kind) => kind switch
    {
        TransitionKind.Cut => OutputTransition.Cut,
        TransitionKind.FadeBlack => OutputTransition.FadeThroughBlack,
        TransitionKind.FadeWhite => OutputTransition.FadeThroughWhite,
        TransitionKind.BlurDissolve => OutputTransition.BlurDissolve,
        _ => OutputTransition.Dissolve,
    };
}
