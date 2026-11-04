using MxU.Slides.Core.Media;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// A picture or video slide's media, edited from the slide's menu: the layer it plays on (a built-in
/// one or an output preset's custom layer), a picture's scale (Scale and Blur among them) and what a
/// video does at its end. The rules are Core's <see cref="CueMediaEditing"/>; the values are shared
/// fields, so a Mac reads them. Drawing Scale and Blur, end fades and custom layers is the engine's.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The output presets' custom layers (Screen Map), for a slide's Layer menu and Clear steps.</summary>
    public IReadOnlyList<CustomLayer> CustomLayers() => CueMediaEditing.CustomLayers(Library.All<OutputPreset>());

    /// <summary>Sends the slide's media to a layer (null: where it plays by default).</summary>
    public bool SetSlideMediaLayer(Presentation presentation, Slide slide, string? layerId)
    {
        if (slide.Background is not { } cue) return false;
        CueMediaEditing.SetRoutingLayer(cue, layerId);
        SlidesRestyled(presentation);
        return true;
    }

    /// <summary>Sets how the slide's picture is scaled.</summary>
    public bool SetSlideMediaScale(Presentation presentation, Slide slide, MediaScaleMode mode)
    {
        if (slide.Background is not { } cue) return false;
        CueMediaEditing.SetScale(cue, mode);
        SlidesRestyled(presentation);
        return true;
    }

    /// <summary>Sets what the slide's video does at its end (next slide, stop, loop, fade to black or clear).</summary>
    public bool SetSlideVideoEnd(Presentation presentation, Slide slide, CueVideoEnd end)
    {
        if (!CueMediaEditing.SetVideoEnd(slide, end)) return false;
        SlidesRestyled(presentation);
        return true;
    }
}
