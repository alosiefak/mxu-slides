using CommunityToolkit.Mvvm.Input;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Windows.ViewModels;

// Phase 6 looks: the lower-thirds gallery and what it needs from the show.
public sealed partial class MainViewModel
{
    /// <summary>Opens the lower-thirds gallery (Views/LowerThirdGalleryWindow.cs): browse, fill in, add to the library, go live.</summary>
    [RelayCommand]
    private void OpenLowerThirdGallery()
    {
        if (System.Windows.Application.Current?.MainWindow is not { } owner) return;
        Views.LowerThirdGalleryWindow.Open(owner, this);
    }

    /// <summary>
    /// The audience picture as the outputs draw it, at rest, with linked text filled in (clocks,
    /// timers: a countdown's digits). <see cref="ShowState.Scene"/> alone leaves linked boxes
    /// showing their sample words.
    /// </summary>
    public RenderScene ProgramScene() => Show.Scene(resolveLinkedText: ResolveLinkedText);

    /// <summary>The same, with its animations (the frames the outputs draw as it moves).</summary>
    public Core.Animation.AnimatedScene ProgramAnimatedScene() => Show.AnimatedScene(resolveLinkedText: ResolveLinkedText);

    /// <summary>
    /// An overlay with its linked text filled in as the outputs would show it now (a countdown's
    /// digits from its timer, the clock), for previews; when nothing would show (no timer yet),
    /// the box keeps its sample digits so the design still reads.
    /// </summary>
    public Core.Model.Overlay WithLiveText(Core.Model.Overlay overlay)
    {
        if (!overlay.Objects.Any(o => o.TextLink is not null)) return overlay;
        var copy = Core.Json.MxuJson.Clone(overlay);
        var resolved = ResolveLinkedText(copy.Objects);
        for (var i = 0; i < resolved.Count; i++)
            if (resolved[i].TextLink is not null && resolved[i].Text.Length == 0) resolved[i].Text = copy.Objects[i].Text;
        copy.Objects = resolved;
        return copy;
    }
}
