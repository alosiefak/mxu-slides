using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Rendering;
using SceneTransition = MxU.Slides.Windows.Rendering.SceneTransition;

namespace MxU.Slides.Windows.Views.Transitions;

/// <summary>
/// A small picture that plays a transition over and over between the two sample slides
/// (<see cref="TransitionSamples"/>): it pushes them in turn to a real <see cref="SceneView"/>,
/// so what you see is exactly what the outputs will do. It only runs while it's on the screen.
/// </summary>
public sealed class TransitionPreview : Border
{
    // A small canvas: the previews are thumbnails, and a smaller canvas is less to lay out.
    private static readonly SceneSize Canvas = new(640, 360);
    private static readonly Core.Animation.AnimatedScene Before = TransitionSamples.Before(Canvas);
    private static readonly Core.Animation.AnimatedScene After = TransitionSamples.After(Canvas);

    private readonly SceneView view = new() { PlaysVideo = false };
    private readonly DispatcherTimer timer;
    private bool showingAfter;

    public TransitionPreview(WindowsTransition transition, double width, double height, double delay = 0)
    {
        Transition = transition;
        Width = width;
        Height = height;
        Child = view;
        ClipToBounds = true;
        // A UI nicety, not show timing: the dispatcher's own timer is the right clock for it.
        timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(0.05 + delay) };
        timer.Tick += (_, _) => Step();
        Loaded += (_, _) =>
        {
            view.Present(Before, SceneTransition.Cut);
            showingAfter = false;
            timer.Start();
        };
        Unloaded += (_, _) => timer.Stop();
    }

    /// <summary>What it plays; changing it plays the new one from the next swap.</summary>
    public WindowsTransition Transition { get; set; }

    /// <summary>Swaps to the other sample with the transition, then waits for it to play and holds a moment.</summary>
    private void Step()
    {
        if (!IsVisible) return;
        showingAfter = !showingAfter;
        var played = Transition;
        var layer = LiveTransitions.FromWindows(played);
        view.Present(showingAfter ? After : Before, SceneTransition.PerLayer(_ => layer));
        var length = WindowsTransitions.HasDuration(played.Kind) ? Math.Clamp(played.DurationSeconds, 0.1, 4) : 0;
        timer.Interval = TimeSpan.FromSeconds(length + 0.9);
    }
}
