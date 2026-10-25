using MxU.Slides.Core.Scene;

namespace MxU.Slides.Windows.ViewModels;

// Phase 6 looks: the lower-thirds gallery and what it needs from the show.
public sealed partial class MainViewModel
{
    /// <summary>
    /// The audience picture as the outputs draw it, at rest, with linked text filled in (clocks,
    /// timers: a countdown's digits). <see cref="ShowState.Scene"/> alone leaves linked boxes
    /// showing their sample words.
    /// </summary>
    public RenderScene ProgramScene() => Show.Scene(resolveLinkedText: ResolveLinkedText);

    /// <summary>The same, with its animations (the frames the outputs draw as it moves).</summary>
    public Core.Animation.AnimatedScene ProgramAnimatedScene() => Show.AnimatedScene(resolveLinkedText: ResolveLinkedText);
}
