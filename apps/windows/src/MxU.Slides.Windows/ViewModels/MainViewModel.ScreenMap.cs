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
    /// <summary>Makes a preset (or none) the outputs' routing and placements, and refreshes the show's custom layers.</summary>
    private void ApplyPresetToOutputs(OutputManager outputs, OutputPreset? preset)
    {
        OutputPresets.Apply(outputs.Config, preset, ThemeExists);
        SyncCustomLayers();
    }

    /// <summary>
    /// Every output preset's custom layers, once each, by name: what a media tile's Layer menu, a
    /// slide's Layer menu and a Clear step can name. The Mac's <c>OutputPresetsController.customLayers</c>,
    /// which lists every preset's layers (not only the active one's), so a slide that names a layer
    /// plays there whichever preset is up.
    /// </summary>
    public IReadOnlyList<CustomLayer> CustomLayers => Core.Media.CueMediaEditing.CustomLayers(Library.All<OutputPreset>());

    /// <summary>Hands the show the custom layers (at launch, when a preset is applied, and when the Screen Map adds or removes one).</summary>
    public void SyncCustomLayers() => Show.SetCustomLayers(CustomLayers);

    /// <summary>Fires a media item onto a custom layer.</summary>
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
