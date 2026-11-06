using MxU.Slides.Core.Model;
using MxU.Slides.Core.Outputs;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Outputs;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// The Screen Map in the live show: a preset's custom layers (media can be fired onto them) and
/// its placements (each screen draws its part of a placed layer). Port of the Mac's
/// OutputPresetsController.route (placements) and the custom-layer half of ServiceControls.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Makes a preset (or none) the outputs' routing, placements and custom layers.</summary>
    private void ApplyPresetToOutputs(OutputManager outputs, OutputPreset? preset)
    {
        OutputPresets.Apply(outputs.Config, preset, ThemeExists);
        Show.SetCustomLayers(preset?.Layers);
    }

    /// <summary>The active preset's custom layers, for a media item's Layer menu.</summary>
    public IReadOnlyList<CustomLayer> CustomLayers => Show.CustomLayers;

    /// <summary>Fires a media item onto one of the active preset's custom layers.</summary>
    public void FireMediaOnLayer(MediaItem item, string customLayerId)
    {
        var cue = MediaCueRouting.Dropped(item);
        cue.LayerId = customLayerId;
        Show.Fire(cue, customLayerId);
        RenderOutputs(DefaultTransition());
    }

    /// <summary>Clears one layer by id (a custom layer's own Clear entry, or the Local API's clear by id).</summary>
    public void ClearLayer(string layerId)
    {
        Show.Clear(layerId);
        RenderOutputs(DefaultTransition());
    }
}
