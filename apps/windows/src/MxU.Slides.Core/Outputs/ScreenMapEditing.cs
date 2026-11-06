using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Outputs;

/// <summary>
/// The Screen Map window's edits, kept here so they can be tested: placing a layer with one of
/// the starting points, adding and removing a preset's custom layers, and New LED Wall…. Edits to
/// a preset use only fields the shared format already has (<c>placements</c>, <c>layers</c>), so a
/// Mac reads them. Port of the rules in the Mac's ScreenMapEditor.swift and OutputPresetsController.swift.
/// </summary>
public static class ScreenMapEditing
{
    /// <summary>How a layer is placed, as the window's buttons offer it.</summary>
    public enum Start { NotPlaced, OnePerScreen, AcrossScreens }

    /// <summary>The preset with a layer's placements replaced (none for <see cref="Start.NotPlaced"/>).</summary>
    public static void Place(OutputPreset preset, string layerId, Start start, ScreenMap map, bool fill = false)
    {
        var screens = map.Screens.OrderBy(pair => pair.Value.X).ThenBy(pair => pair.Value.Y).Select(pair => pair.Value).ToList();
        var placements = start switch
        {
            Start.OnePerScreen => ScreenMapResolver.OnePerScreen(screens),
            Start.AcrossScreens => ScreenMapResolver.AcrossScreens(screens),
            _ => [],
        };
        SetPlacements(preset, layerId, placements.Select(p => p with { Fill = fill }).ToList());
    }

    /// <summary>Replaces one layer's placements in a preset; the other layers' stay as they were, in order.</summary>
    public static void SetPlacements(OutputPreset preset, string layerId, IReadOnlyList<MapPlacement> placements)
    {
        var kept = (preset.Placements ?? []).Where(p => p.LayerId != layerId).ToList();
        kept.AddRange(placements.Select(p => new LayerPlacement
        {
            LayerId = layerId, X = p.Rect.X, Y = p.Rect.Y, Width = p.Rect.Width, Height = p.Rect.Height,
            Scale = p.Fill ? MediaScaleMode.Fill : null,
        }));
        preset.Placements = kept.Count == 0 ? null : kept;
    }

    /// <summary>A short line for a layer's placements: "Not placed", "On 2 screens", "Across the room".</summary>
    public static string Summary(OutputPreset? preset, string layerId)
    {
        var count = (preset?.Placements ?? []).Count(p => p.LayerId == layerId);
        return count switch
        {
            0 => "Not placed (shows where the preset routes it)",
            1 => "Placed once on the map",
            _ => $"Placed {count} times on the map",
        };
    }

    /// <summary>Adds a custom layer above <paramref name="above"/> and returns its id.</summary>
    public static string AddCustomLayer(OutputPreset preset, string name, string? above)
    {
        var id = Guid.NewGuid().ToString().ToUpperInvariant();
        var layers = preset.Layers ?? [];
        layers.Add(new CustomLayer { Id = id, Name = string.IsNullOrWhiteSpace(name) ? NextName(preset) : name.Trim(), Above = above });
        preset.Layers = layers;
        return id;
    }

    /// <summary>"Layer N": the next number no custom layer is already called.</summary>
    public static string NextName(OutputPreset preset)
    {
        var names = (preset.Layers ?? []).Select(l => l.Name.ToLowerInvariant()).ToHashSet();
        var n = (preset.Layers?.Count ?? 0) + 1;
        while (names.Contains($"layer {n}")) n++;
        return $"Layer {n}";
    }

    /// <summary>Removes a custom layer and its placements; a layer that sat above it now sits where it was.</summary>
    public static void RemoveCustomLayer(OutputPreset preset, string id)
    {
        var gone = preset.Layers?.FirstOrDefault(l => l.Id == id);
        if (gone is null) return;
        preset.Layers!.Remove(gone);
        foreach (var layer in preset.Layers.Where(l => l.Above == id)) layer.Above = gone.Above;
        if (preset.Layers.Count == 0) preset.Layers = null;
        SetPlacements(preset, id, []);
    }

    /// <summary>The layers a preset can place, top first: the built-in ones and its custom layers in their places.</summary>
    public static IReadOnlyList<(string Id, string Name)> Layers(OutputPreset? preset)
    {
        var scene = new RenderScene(SceneSize.Hd);
        foreach (var layer in preset?.Layers ?? []) scene.InsertCustomLayer(layer.Id, layer.Name, layer.Above);
        return [.. scene.Layers.Reverse().Select(l => (l.Id, l.Name))];
    }

    /// <summary>
    /// New LED Wall…: a screen of the wall's own pixel size, seeded 1:1 into the processor's
    /// frame (1080p, or 4K for a wall bigger than HD), and seated on the map to the right of the
    /// other screens. Returns the new screen's id.
    /// </summary>
    public static Guid? NewLedWall(OutputConfiguration outputs, string name, int width, int height)
    {
        if (outputs.AddScreen(name, width, height) is not { } screen) return null;
        var frameWidth = width <= 1920 && height <= 1080 ? 1920 : 3840;
        var frameHeight = frameWidth == 1920 ? 1080 : 2160;
        outputs.SeedPlacement(screen.Id, frameWidth, frameHeight);
        var map = outputs.ScreenMap ?? ScreenMap.SideBySide(outputs.Screens.Where(s => s.Id != screen.Id).Select(s => (s.Id, s.Width, s.Height)));
        outputs.SetScreenMap(map.Placing(screen.Id, map.NextSeat(width, height)));
        return screen.Id;
    }

    /// <summary>A first map: every audience screen side by side.</summary>
    public static ScreenMap FirstMap(OutputConfiguration outputs) =>
        ScreenMap.SideBySide(outputs.Screens.Where(s => outputs.Role(s.Id) != ScreenRole.Confidence).Select(s => (s.Id, s.Width, s.Height)));
}
