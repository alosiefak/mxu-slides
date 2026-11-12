using System.Collections.Immutable;
using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Outputs;

// The Screen Map half of OutputManager.swift: the map itself (saved with this PC's screen
// setup), the active preset's layer placements, and the step in each output's scene that
// turns a placed layer into "draw the room's picture of this layer into these frames".
public sealed partial class OutputConfiguration
{
    private ScreenMap? screenMap;
    private ImmutableDictionary<string, IReadOnlyList<MapPlacement>> layerPlacements =
        ImmutableDictionary<string, IReadOnlyList<MapPlacement>>.Empty;

    /// <summary>Raised when the map changes, so the app can save it.</summary>
    public event Action? ScreenMapChanged;

    public ScreenMap? ScreenMap => screenMap;

    public void SetScreenMap(ScreenMap? map)
    {
        if (Equals(map, screenMap)) return;
        screenMap = map;
        ScreenMapChanged?.Invoke();
    }

    /// <summary>The active preset's placements, by layer id. Empty when the preset places nothing.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<MapPlacement>> LayerPlacements => layerPlacements;

    public void SetLayerPlacements(IReadOnlyDictionary<string, IReadOnlyList<MapPlacement>> placements) =>
        layerPlacements = placements.ToImmutableDictionary(StringComparer.Ordinal);

    /// <summary>A preset's placements as the map uses them (OutputPresetsController.route).</summary>
    public static Dictionary<string, IReadOnlyList<MapPlacement>> PlacementsOf(OutputPreset? preset) =>
        (preset?.Placements ?? [])
            .GroupBy(p => p.LayerId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<MapPlacement>)
                [.. g.Select(p => new MapPlacement(new SceneRect(p.X, p.Y, p.Width, p.Height), p.Scale == MediaScaleMode.Fill))],
                StringComparer.Ordinal);

    /// <summary>
    /// Places the scene's mapped layers for this screen: a layer the active preset places is
    /// drawn from the room's (unscaled) picture into the frames it covers on this screen, and
    /// hidden on a screen it misses. Layers the preset doesn't place are left as they are.
    /// Returns null when nothing is placed here. Port of the placement step in
    /// OutputManager.provider(for:).
    /// </summary>
    internal AnimatedScene? Placed(OutputFeed feed, AnimatedScene room, AnimatedScene scene, Func<string, AnimatedScene?>? looks = null)
    {
        if (layerPlacements.IsEmpty || feed.ScreenId is not { } screenId || screenMap?.RectFor(screenId) is not { } mapRect) return null;
        AnimatedScene? placed = null;
        foreach (var (layerId, placements) in layerPlacements)
        {
            if (ScreenMapResolver.Resolve(placements, mapRect) is not { } hits) continue;
            placed ??= scene.Copy();
            var source = room.Layer(layerId);
            var target = placed.Layer(layerId);
            if (source is null || target is null) continue;
            if (hits.Count == 0)
            {
                target.IsHidden = true;
                continue;
            }
            // A placed Slide layer wears the look of the screen it mostly lands on, so a lyric
            // spanning a wall reads the same on every piece (each screen would otherwise draw
            // its own look's half).
            if (source.Kind == LayerKind.Slide && looks is not null && screenMap is { } map
                && ScreenMapResolver.DominantScreen(placements, map) is { } host
                && slideThemeRouting.TryGetValue(host, out var look)
                && looks(look)?.Layer(layerId) is { } variant)
                source = variant;
            var canvas = placed.CanvasSize;
            // Copied first: the picture may be this very layer (a screen already showing that look).
            var picture = source.Items.ToList();
            target.Items.Clear();
            target.Items.AddRange(picture);
            target.Mix = null;
            target.Placed = new PlacedLayer(room.CanvasSize, [.. hits.Select(hit => new PlacedLayer.Frame(
                new SceneRect(hit.Frame.X * canvas.Width, hit.Frame.Y * canvas.Height, hit.Frame.Width * canvas.Width, hit.Frame.Height * canvas.Height),
                hit.Fill))]);
        }
        return placed;
    }

    /// <inheritdoc cref="Placed(OutputFeed, AnimatedScene, AnimatedScene)"/>
    internal RenderScene? Placed(OutputFeed feed, RenderScene room, RenderScene scene)
    {
        if (layerPlacements.IsEmpty || feed.ScreenId is not { } screenId || screenMap?.RectFor(screenId) is not { } mapRect) return null;
        RenderScene? placed = null;
        foreach (var (layerId, placements) in layerPlacements)
        {
            if (ScreenMapResolver.Resolve(placements, mapRect) is not { } hits) continue;
            placed ??= CopyOf(scene);
            if (room.Layer(layerId) is not { } source || placed.Layer(layerId) is not { } target) continue;
            // A placed Slide layer wears the look of the screen it mostly lands on, so a lyric
            // spanning a wall reads the same on every piece.
            if (source.Kind == LayerKind.Slide && screenMap is { } map
                && ScreenMapResolver.DominantScreen(placements, map) is { } host
                && slideThemeRouting.TryGetValue(host, out var look)
                && VariantSceneProvider?.Invoke(look)?.Layer(layerId) is { } variant)
                source = variant;
            target.Items.Clear();
            if (hits.Count == 0) continue;
            var canvas = placed.CanvasSize;
            target.Items.AddRange(source.Items);
            target.Placed = new PlacedLayer(room.CanvasSize, [.. hits.Select(hit => new PlacedLayer.Frame(
                new SceneRect(hit.Frame.X * canvas.Width, hit.Frame.Y * canvas.Height, hit.Frame.Width * canvas.Width, hit.Frame.Height * canvas.Height),
                hit.Fill))]);
        }
        return placed;
    }

    private static RenderScene CopyOf(RenderScene scene)
    {
        var copy = new RenderScene(scene.CanvasSize, scene.Background);
        string? below = null;
        foreach (var layer in scene.Layers)
        {
            var into = copy.Matching(layer.Id, layer.Kind, layer.Name, below);
            below = layer.Id;
            into.Placed = layer.Placed;
            into.Items.AddRange(layer.Items);
        }
        return copy;
    }
}
