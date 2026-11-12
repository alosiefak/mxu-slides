using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Media;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Scene;

// Custom layers (the Screen Map): the active output preset's own layers, each sitting above the
// layer it names. Media fired with a layer id (CueMedia.LayerId) plays on that layer, which the
// Screen Map can then place on any screen. They behave as foreground media layers: a new slide
// sweeps foreground media off them, and Clear Media and Clear All clear them. Port of the
// custom-layer half of the Mac's ShowState.swift (setCustomLayers, liveLayerID, clear(layerID:),
// layerStackTopFirst) and MediaTransportTarget.swift's seeds.
public sealed partial class ShowState
{
    private readonly Dictionary<string, CueMedia> customMedia = new(StringComparer.Ordinal);
    private IReadOnlyList<CustomLayer> customLayers = [];

    /// <summary>The active preset's custom layers, bottom first.</summary>
    public IReadOnlyList<CustomLayer> CustomLayers => customLayers;

    /// <summary>What plays on each custom layer, by its id.</summary>
    public IReadOnlyDictionary<string, CueMedia> CustomLayerMedia => customMedia;

    /// <summary>
    /// The active preset changed its layers. Media on a layer that's gone is dropped (it has
    /// nowhere to draw), as on the Mac.
    /// </summary>
    public void SetCustomLayers(IReadOnlyList<CustomLayer>? layers)
    {
        var next = layers ?? [];
        var known = next.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        var dropped = customMedia.Keys.Where(id => !known.Contains(id)).ToList();
        var same = next.Count == customLayers.Count && next.Zip(customLayers).All(p => p.First.Id == p.Second.Id && p.First.Name == p.Second.Name && p.First.Above == p.Second.Above);
        customLayers = [.. next];
        foreach (var id in dropped) customMedia.Remove(id);
        if (!same || dropped.Count > 0) Changed?.Invoke();
    }

    /// <summary>The custom layer a cue names, when the active preset has it.</summary>
    public string? CustomLayerOf(CueMedia cue) =>
        cue.LayerId is { Length: > 0 } id && customLayers.Any(l => l.Id == id) ? id : null;

    /// <summary>Fires media onto a custom layer (a media item's Layer menu, or a cue that names one).</summary>
    public void Fire(CueMedia media, string customLayerId)
    {
        if (customLayers.All(l => l.Id != customLayerId)) return;
        if (customMedia.TryGetValue(customLayerId, out var current) && current.MediaId == media.MediaId) return;
        customMedia[customLayerId] = media;
        Changed?.Invoke();
    }

    /// <summary>Clears one layer by id: a built-in layer's raw name, or a custom layer.</summary>
    public void Clear(string layerId, double? atHostTime = null)
    {
        if (LayerKinds.Parse(layerId) is { } kind) Clear(kind, atHostTime);
        else if (customMedia.Remove(layerId)) Changed?.Invoke();
    }

    /// <summary>A new slide sweeps foreground media off the custom layers (unless it brings it).</summary>
    private void SweepCustomLayers(CueMedia? background)
    {
        var keep = background is null ? null : CustomLayerOf(background);
        foreach (var (id, cue) in customMedia.ToList())
            if (id != keep && cue.ResolvedClassification() == MediaClassification.Foreground) customMedia.Remove(id);
        if (keep is not null && background is not null) customMedia[keep] = background;
    }

    /// <summary>Clears the custom layers' media (Clear Media, Clear All).</summary>
    private void ClearCustomLayers() => customMedia.Clear();

    /// <summary>Adds the custom layers, and their media, to a scene being built.</summary>
    private void AddCustomLayers(AnimatedScene scene, SceneSize canvas)
    {
        foreach (var layer in customLayers) scene.InsertCustomLayer(layer.Id, layer.Name, layer.Above);
        foreach (var (id, media) in customMedia)
            scene.AddItem(SlideSceneBuilder.MediaItem($"live-media-{id}", media, canvas), id);
    }

    /// <summary>The layers top first, custom ones in their places, for pickers (the Mac's layerStackTopFirst).</summary>
    public IReadOnlyList<(string Id, string Name)> LayerStackTopFirst()
    {
        var scene = new AnimatedScene(SceneSize.Hd);
        foreach (var layer in customLayers) scene.InsertCustomLayer(layer.Id, layer.Name, layer.Above);
        return [.. scene.Layers.Reverse().Select(l => (l.Id, l.Name))];
    }

    /// <summary>Custom-layer videos for the transport and video marks: foreground media, by layer id.</summary>
    private IEnumerable<TransportSeed> CustomLayerSeeds(HashSet<string> seen) =>
        customMedia.Where(pair => pair.Value.MediaId.Length > 0 && seen.Add(pair.Value.MediaId))
            .Select(pair => new TransportSeed(pair.Value.MediaId, LayerKind.Videos, TransportOrigin.ForegroundServiceItem) { LayerId = pair.Key });
}
