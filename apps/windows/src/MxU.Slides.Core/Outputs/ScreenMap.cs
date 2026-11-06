using System.Text.Json.Serialization;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Outputs;

/// <summary>
/// The Screen Map: the room drawn as one big picture, with each screen placed where it sits
/// (in pixels of that picture). A preset can then place a layer anywhere on the map ("the
/// countdown across both side screens", "the logo on the LED wall"), and each screen draws
/// its own part of it. The map belongs to this PC (its screens are this PC's), so it is kept
/// in the PC's own setup, not in the shared library. Port of the Mac's OutputEngine/ScreenMap.swift.
/// </summary>
public sealed record ScreenMap
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "Main Room";

    /// <summary>Each placed screen's rectangle on the map, by the screen's id (upper-case UUID, as the Mac writes it).</summary>
    [JsonPropertyName("screens")]
    public IReadOnlyDictionary<string, SceneRect> Screens { get; init; } = new Dictionary<string, SceneRect>();

    public SceneRect? RectFor(Guid screenId) => Screens.TryGetValue(SwiftUuid.Format(screenId), out var rect) ? rect : null;

    /// <summary>The map with a screen placed (or taken off, for null).</summary>
    public ScreenMap Placing(Guid screenId, SceneRect? rect)
    {
        var screens = Screens.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (rect is { } r) screens[SwiftUuid.Format(screenId)] = r;
        else screens.Remove(SwiftUuid.Format(screenId));
        return this with { Screens = screens };
    }

    /// <summary>The smallest rectangle holding every screen (zero when none is placed).</summary>
    public SceneRect Bounds => Screens.Values.Aggregate(new SceneRect(0, 0, 0, 0), (union, rect) => ScreenGeometry.Union(union, rect));

    /// <summary>Screens in a row, left to right, each at its own size.</summary>
    public static ScreenMap SideBySide(IEnumerable<(Guid Id, int Width, int Height)> screens, string name = "Main Room")
    {
        var map = new ScreenMap { Name = name };
        var x = 0.0;
        foreach (var (id, width, height) in screens)
        {
            map = map.Placing(id, new SceneRect(x, 0, width, height));
            x += width;
        }
        return map;
    }

    /// <summary>Where a newly added screen goes: to the right of everything.</summary>
    public SceneRect NextSeat(int width, int height) => new(Bounds.Right, 0, width, height);

    public bool Equals(ScreenMap? other) =>
        other is not null && Name == other.Name && Screens.Count == other.Screens.Count
        && Screens.All(pair => other.Screens.TryGetValue(pair.Key, out var rect) && rect == pair.Value);

    public override int GetHashCode() => HashCode.Combine(Name, Screens.Count);
}

/// <summary>A layer's place on the map, fitted (letterboxed) or filling (cropped).</summary>
public sealed record MapPlacement(SceneRect Rect, bool Fill = false);

/// <summary>A placement as one screen sees it: a frame in units of that screen (0–1 across and down).</summary>
public sealed record ResolvedPlacement(SceneRect Frame, bool Fill);

/// <summary>Rectangle sums the Mac gets from CGRect. A zero-sized rectangle is "nothing".</summary>
public static class ScreenGeometry
{
    public static SceneRect Union(SceneRect a, SceneRect b)
    {
        if (a.Width <= 0 || a.Height <= 0) return b;
        if (b.Width <= 0 || b.Height <= 0) return a;
        var x = Math.Min(a.X, b.X);
        var y = Math.Min(a.Y, b.Y);
        return new SceneRect(x, y, Math.Max(a.Right, b.Right) - x, Math.Max(a.Bottom, b.Bottom) - y);
    }

    public static SceneRect? Intersection(SceneRect a, SceneRect b)
    {
        var x = Math.Max(a.X, b.X);
        var y = Math.Max(a.Y, b.Y);
        var right = Math.Min(a.Right, b.Right);
        var bottom = Math.Min(a.Bottom, b.Bottom);
        return right > x && bottom > y ? new SceneRect(x, y, right - x, bottom - y) : null;
    }
}

/// <summary>Turns map placements into each screen's frames. Port of ScreenMapResolver.</summary>
public static class ScreenMapResolver
{
    /// <summary>
    /// The placements that touch this screen, as frames in its own units (a frame may run past
    /// 0–1: the screen shows only its part). Null when there's nothing to place or the screen
    /// isn't on the map; empty when the layer misses this screen (so it's hidden there).
    /// </summary>
    public static List<ResolvedPlacement>? Resolve(IReadOnlyList<MapPlacement> placements, SceneRect? screen)
    {
        if (placements.Count == 0 || screen is not { Width: > 0, Height: > 0 } s) return null;
        return placements
            .Where(p => ScreenGeometry.Intersection(p.Rect, s) is not null)
            .Select(p => new ResolvedPlacement(
                new SceneRect((p.Rect.X - s.X) / s.Width, (p.Rect.Y - s.Y) / s.Height, p.Rect.Width / s.Width, p.Rect.Height / s.Height),
                p.Fill))
            .ToList();
    }

    /// <summary>Preset: one copy of the layer on every screen.</summary>
    public static List<MapPlacement> OnePerScreen(IEnumerable<SceneRect> screens) => [.. screens.Select(rect => new MapPlacement(rect))];

    /// <summary>Preset: one picture stretched across all the screens together.</summary>
    public static List<MapPlacement> AcrossScreens(IReadOnlyList<SceneRect> screens) =>
        screens.Count == 0 ? [] : [new MapPlacement(screens.Skip(1).Aggregate(screens[0], ScreenGeometry.Union))];

    /// <summary>
    /// The screen most of a placement lands on (by area): a placed Slide layer takes that
    /// screen's look. Null when none overlaps. Port of OutputManager.dominantScreen.
    /// </summary>
    public static string? DominantScreen(IReadOnlyList<MapPlacement> placements, ScreenMap map)
    {
        (string Id, double Area)? best = null;
        foreach (var (id, rect) in map.Screens.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var area = placements.Sum(p => ScreenGeometry.Intersection(p.Rect, rect) is { } o ? o.Width * o.Height : 0);
            if (area > (best?.Area ?? 0)) best = (id, area);
        }
        return best?.Id;
    }
}
