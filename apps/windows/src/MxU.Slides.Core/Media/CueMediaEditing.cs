using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Media;

/// <summary>What a picture or video slide's video does when it reaches its end.</summary>
public enum CueVideoEnd
{
    /// <summary>The next slide fires the moment the video ends (an Auto Advance of 0 s after playback).</summary>
    Next,

    /// <summary>It stops on its last frame.</summary>
    Stop,

    Loop,
    FadeToBlack,

    /// <summary>It dissolves into whatever is behind it (best over a background).</summary>
    FadeToClear,
}

/// <summary>A layer a slide's media can be sent to: a built-in one, or an output preset's custom layer.</summary>
public sealed record CueLayerChoice(string? Id, string Name);

/// <summary>
/// Editing a picture or video slide's media (its <see cref="Slide.Background"/> cue): the layer it
/// plays on, how a picture is scaled (Fill, Fit, Stretch, Scale and Blur) and what a video does at
/// its end (next slide, stop, loop, fade to black or to clear). Every value lives in shared fields
/// (<c>layer</c>/<c>layerId</c>, <c>scaleMode</c>, <c>loops</c>, <c>endFade</c>, the slide's
/// <c>autoAdvance</c>), so a Mac reads what's set here.
/// <para>
/// Ports of the Mac's PresenterCore/SlideLayerFacts.swift (<c>storedRoutingLayerId</c>,
/// <c>setRoutingLayer</c>, <c>defaultLayer</c>), MediaCueMenu.swift's <c>SpotLayer</c> picker,
/// PhotoSpots.swift's <c>inPhotoSpot</c>, and SlidePlanner's <c>showMedia</c> (the video end, as
/// SlideDesignMap's <c>VideoEnd</c> names them). The Mac sets the scale and video end when it makes
/// slides from notes; Windows also lets you change them on a slide.
/// </para>
/// </summary>
public static class CueMediaEditing
{
    // MARK: The layer (MediaCueMenu's Layer picker)

    /// <summary>The layer the cue names: a custom layer's id, else its built-in layer, else null (the default).</summary>
    public static string? RoutingLayerId(CueMedia cue) =>
        !string.IsNullOrEmpty(cue.LayerId) ? cue.LayerId : cue.Layer is { } layer ? CueMediaLayerJsonConverter.ToRaw(layer) : null;

    /// <summary>Sends the cue to a layer: a built-in cue layer goes in <c>layer</c>, anything else (overlays, a custom layer) in <c>layerId</c>; null is the default.</summary>
    public static void SetRoutingLayer(CueMedia cue, string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            cue.Layer = null;
            cue.LayerId = null;
        }
        else if (CueMediaLayerJsonConverter.TryParse(id, out var builtIn))
        {
            cue.Layer = builtIn;
            cue.LayerId = null;
        }
        else
        {
            cue.Layer = null;
            cue.LayerId = id;
        }
    }

    /// <summary>Where the cue plays when no layer is chosen: as the item fires (background media, or still graphics / foreground videos).</summary>
    public static CueMediaLayer DefaultLayer(CueMedia cue, MediaKind kind) => MediaCueRouting.FiredLayer(kind, cue.ResolvedClassification());

    /// <summary>The built-in layers the picker offers (the Mac's <c>SpotLayer.builtIn</c>).</summary>
    public static readonly IReadOnlyList<LayerKind> BuiltInLayers = [LayerKind.LoopingVideos, LayerKind.StillGraphics, LayerKind.Videos, LayerKind.Overlays];

    /// <summary>"Default (Background Media)", then the built-in layers, then the custom ones.</summary>
    public static IReadOnlyList<CueLayerChoice> LayerChoices(CueMedia cue, MediaKind kind, IReadOnlyList<CustomLayer> custom)
    {
        var list = new List<CueLayerChoice> { new(null, $"Default ({DefaultLayerKind(cue, kind).DisplayName()})") };
        list.AddRange(BuiltInLayers.Select(l => new CueLayerChoice(l.Raw(), l.DisplayName())));
        list.AddRange(custom.Select(l => new CueLayerChoice(l.Id, l.Name)));
        return list;
    }

    /// <summary>The picker's entry for the cue now: its layer when it's one offered (and not the default), else the default entry (null).</summary>
    public static string? ChosenLayer(CueMedia cue, MediaKind kind, IReadOnlyList<CustomLayer> custom)
    {
        var stored = RoutingLayerId(cue) ?? "";
        var offered = BuiltInLayers.Select(l => l.Raw()).Concat(custom.Select(l => l.Id));
        return offered.Contains(stored) && stored != DefaultLayerKind(cue, kind).Raw() ? stored : null;
    }

    private static LayerKind DefaultLayerKind(CueMedia cue, MediaKind kind) => SlideSceneBuilder.LayerKindOf(DefaultLayer(cue, kind));

    /// <summary>
    /// Every output preset's custom layers, once each, by name (the Mac's
    /// <c>OutputPresetsController.customLayers</c>): what a cue or a Clear step can name.
    /// </summary>
    public static IReadOnlyList<CustomLayer> CustomLayers(IEnumerable<OutputPreset> presets)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return [.. presets.SelectMany(p => p.Layers ?? [])
            .OrderBy(l => l.Name, StringComparer.Ordinal).ThenBy(l => l.Id, StringComparer.Ordinal)
            .Where(l => seen.Add(l.Id))];
    }

    // MARK: A picture's scale

    /// <summary>The scales a picture slide offers, in the Mac's Make Slides order.</summary>
    public static readonly IReadOnlyList<(MediaScaleMode Mode, string Name)> ScaleChoices =
    [
        (MediaScaleMode.ScaleAndBlur, "Scale and Blur"),
        (MediaScaleMode.Fit, "Fit"),
        (MediaScaleMode.Fill, "Fill"),
        (MediaScaleMode.Stretch, "Stretch"),
    ];

    /// <summary>The cue's scale (Fill when none is stored).</summary>
    public static MediaScaleMode Scale(CueMedia cue) => cue.ScaleMode ?? MediaScaleMode.Fill;

    /// <summary>Sets the scale; Fill, the default, is stored as nothing (as the Mac's planner stores it).</summary>
    public static void SetScale(CueMedia cue, MediaScaleMode mode) => cue.ScaleMode = mode == MediaScaleMode.Fill ? null : mode;

    /// <summary>A scale inside a photo spot: Scale and Blur fills it instead (port of <c>MediaScaleMode.inPhotoSpot</c>).</summary>
    public static MediaScaleMode InPhotoSpot(MediaScaleMode mode) => mode == MediaScaleMode.ScaleAndBlur ? MediaScaleMode.Fill : mode;

    public static string ScaleName(MediaScaleMode mode) => ScaleChoices.FirstOrDefault(c => c.Mode == mode).Name ?? mode.ToString();

    // MARK: A video's end

    public static readonly IReadOnlyList<(CueVideoEnd End, string Name)> VideoEndChoices =
    [
        (CueVideoEnd.Next, "Go to next slide"),
        (CueVideoEnd.Stop, "Stop"),
        (CueVideoEnd.Loop, "Loop"),
        (CueVideoEnd.FadeToBlack, "Fade to black"),
        (CueVideoEnd.FadeToClear, "Fade to clear"),
    ];

    /// <summary>What the slide's video does at its end, read from its cue and Auto Advance.</summary>
    public static CueVideoEnd VideoEnd(Slide slide)
    {
        if (slide.Background is not { } cue) return CueVideoEnd.Stop;
        return cue.EndFade switch
        {
            MediaEndFade.Black => CueVideoEnd.FadeToBlack,
            MediaEndFade.Clear => CueVideoEnd.FadeToClear,
            _ when cue.Loops == true => CueVideoEnd.Loop,
            _ when slide.AutoAdvance is { AfterPlayback: true, DelaySeconds: <= 0 } => CueVideoEnd.Next,
            _ => CueVideoEnd.Stop,
        };
    }

    /// <summary>
    /// Sets what the slide's video does at its end, as the Mac's <c>showMedia</c> does: Next is an
    /// Auto Advance 0 s after playback; Loop loops the cue; the fades set its end fade. Choosing
    /// another end takes away the one before (a timed Auto Advance of the slide's own is kept).
    /// </summary>
    public static bool SetVideoEnd(Slide slide, CueVideoEnd end)
    {
        if (slide.Background is not { } cue) return false;
        cue.EndFade = end switch
        {
            CueVideoEnd.FadeToBlack => MediaEndFade.Black,
            CueVideoEnd.FadeToClear => MediaEndFade.Clear,
            _ => null,
        };
        cue.Loops = end == CueVideoEnd.Loop;
        var followsPlayback = slide.AutoAdvance is { AfterPlayback: true };
        if (end == CueVideoEnd.Next) slide.AutoAdvance = new AutoAdvance { DelaySeconds = 0, AfterPlayback = true };
        else if (followsPlayback) slide.AutoAdvance = null;
        return true;
    }

    public static string VideoEndName(CueVideoEnd end) => VideoEndChoices.First(c => c.End == end).Name;
}
