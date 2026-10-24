using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Outputs;

/// <summary>The named shapes a screen's outputs can take, as the layout picker offers them.</summary>
public enum OutputLayoutKind
{
    /// <summary>One output carries the whole canvas.</summary>
    Single,

    /// <summary>Every output shows the whole canvas.</summary>
    Mirror,

    /// <summary>Outputs tile the canvas edge to edge: a video wall with hard seams.</summary>
    Grouped,

    /// <summary>Outputs overlap and ramp into each other (projector blending).</summary>
    EdgeBlend,

    /// <summary>Wall-native pixels placed 1:1 at exact coordinates in a processor's frame.</summary>
    LedWall,

    /// <summary>Hand-placed regions: anything the named shapes can't express.</summary>
    Custom,
}

public static class OutputLayoutKinds
{
    public static readonly OutputLayoutKind[] All = Enum.GetValues<OutputLayoutKind>();

    public static string Raw(this OutputLayoutKind kind) => kind switch
    {
        OutputLayoutKind.Single => "single",
        OutputLayoutKind.Mirror => "mirror",
        OutputLayoutKind.Grouped => "grouped",
        OutputLayoutKind.EdgeBlend => "edgeBlend",
        OutputLayoutKind.LedWall => "ledWall",
        _ => "custom",
    };

    public static string DisplayName(this OutputLayoutKind kind) => kind switch
    {
        OutputLayoutKind.Single => "Single",
        OutputLayoutKind.Mirror => "Mirror",
        OutputLayoutKind.Grouped => "Grouped",
        OutputLayoutKind.EdgeBlend => "Edge Blend",
        OutputLayoutKind.LedWall => "LED Wall",
        _ => "Custom",
    };
}

/// <summary>
/// Reads a screen's slices back as one of the named layouts, and the tile
/// maths the layouts are built from. Port of OutputEngine/OutputLayout.swift.
/// </summary>
public static class OutputLayout
{
    private const double Epsilon = 0.001;

    /// <summary>
    /// Which named layout a screen's slices (default slice first) currently form.
    /// Anything that isn't exactly one of the named shapes reads as Custom, so a
    /// hand-edited setup stays fully editable instead of being forced into a shape.
    /// </summary>
    public static OutputLayoutKind Classify(IReadOnlyList<OutputSliceState> slices, Func<Guid, bool> hasBlends, Func<Guid, bool>? hasPlacement = null)
    {
        hasPlacement ??= _ => false;
        if (slices.Count == 1 && hasPlacement(slices[0].Id)) return OutputLayoutKind.LedWall;
        if (slices.Any(slice => hasPlacement(slice.Id))) return OutputLayoutKind.Custom;
        if (slices.Count <= 1) return OutputLayoutKind.Single;

        static bool IsFull(SceneRect rect) =>
            Math.Abs(rect.X) < Epsilon && Math.Abs(rect.Y) < Epsilon
            && Math.Abs(rect.Width - 1) < Epsilon && Math.Abs(rect.Height - 1) < Epsilon;

        if (slices.All(slice => IsFull(slice.SourceRect)))
            return slices.Any(slice => hasBlends(slice.Id)) ? OutputLayoutKind.Custom : OutputLayoutKind.Mirror;

        // Grouped and Edge Blend: the default slice stays whole and the extras are
        // full-height columns, left to right, with no gaps, spanning the canvas.
        var extras = slices.Skip(1).OrderBy(slice => slice.SourceRect.X).ToList();
        if (!IsFull(slices[0].SourceRect) || extras.Count < 2
            || !extras.All(slice => Math.Abs(slice.SourceRect.Y) < Epsilon && Math.Abs(slice.SourceRect.Height - 1) < Epsilon))
            return OutputLayoutKind.Custom;
        var overlapping = false;
        for (var index = 1; index < extras.Count; index++)
        {
            var gap = extras[index].SourceRect.X - extras[index - 1].SourceRect.Right;
            if (gap > Epsilon) return OutputLayoutKind.Custom;
            if (gap < -Epsilon) overlapping = true;
        }
        var spansCanvas = Math.Abs(extras[0].SourceRect.X) < Epsilon && Math.Abs(extras[^1].SourceRect.Right - 1) < Epsilon;
        if (!spansCanvas) return OutputLayoutKind.Custom;
        return overlapping || extras.Any(slice => hasBlends(slice.Id)) ? OutputLayoutKind.EdgeBlend : OutputLayoutKind.Grouped;
    }

    /// <summary>
    /// Full-height columns across the canvas, each overlapping the next by
    /// <paramref name="overlap"/> (a fraction of the canvas, at most 0.2), so
    /// that together they cover it exactly.
    /// </summary>
    public static IReadOnlyList<SceneRect> Tiles(int columns, double overlap)
    {
        var count = Math.Max(1, columns);
        var clamped = count > 1 ? Math.Min(Math.Max(overlap, 0), 0.2) : 0;
        var width = (1 + (count - 1) * clamped) / count;
        return Enumerable.Range(0, count).Select(index => new SceneRect(index * (width - clamped), 0, width, 1)).ToList();
    }

    /// <summary>Left/Right, Left/Center/Right, otherwise "Output n".</summary>
    public static string TileName(int index, int count) => (count, index) switch
    {
        (2, 0) => "Left",
        (2, 1) => "Right",
        (3, 0) => "Left",
        (3, 1) => "Center",
        (3, 2) => "Right",
        _ => $"Output {index + 1}",
    };
}

// The layout picker's action: rebuilds a screen's slices as a named layout.
public sealed partial class OutputConfiguration
{
    /// <summary>The named layout a screen's slices form now (blends and placements as the picker reads them).</summary>
    public OutputLayoutKind Layout(Guid screenId) =>
        OutputLayout.Classify(Slices(screenId), id => Adjustments(id).HasBlends, id => Placement(id) is not null);

    /// <summary>
    /// Replaces a screen's extra slices with the named layout. Each removed slice
    /// (and, for Grouped/Edge Blend, the default slice, which becomes plumbing
    /// while the tiles carry the glass) is first passed to
    /// <paramref name="releasingCarries"/> so the app can stop its NDI or DeckLink
    /// feed. Edge Blend tiles get blend ramps on their inner edges only.
    /// </summary>
    public void ApplyLayout(OutputLayoutKind kind, int outputCount, double overlap, Guid screenId, Action<Guid>? releasingCarries = null)
    {
        if (Screen(screenId) is not { } screen) return;
        var release = releasingCarries ?? (_ => { });
        foreach (var slice in ExtraSlices(screenId))
        {
            release(slice.Id);
            RemoveSlice(slice.Id);
        }
        if (kind is not (OutputLayoutKind.LedWall or OutputLayoutKind.Custom)) SetPlacement(screenId, null);

        switch (kind)
        {
            case OutputLayoutKind.Single:
            case OutputLayoutKind.Custom:
                return;
            case OutputLayoutKind.LedWall:
            {
                // Keep a placement the user already moved; otherwise seed the wall 1:1 at the frame's top-left.
                if (Placement(screenId) is not null) return;
                var frameWidth = screen.Width <= 1920 && screen.Height <= 1080 ? 1920 : 3840;
                var frameHeight = frameWidth == 1920 ? 1080 : 2160;
                SetPlacement(screenId, new OutputPlacement(
                    frameWidth, frameHeight, 0, 0, Math.Min(screen.Width, frameWidth), Math.Min(screen.Height, frameHeight)));
                return;
            }
            case OutputLayoutKind.Mirror:
                for (var index = 1; index < Math.Max(2, outputCount); index++)
                    AddSlice(screenId, $"Mirror {index + 1}");
                return;
            case OutputLayoutKind.Grouped:
            case OutputLayoutKind.EdgeBlend:
            {
                release(screenId);
                AssignDevice(screenId, null);
                var columns = Math.Max(2, outputCount);
                var spread = kind == OutputLayoutKind.EdgeBlend ? Math.Min(Math.Max(overlap, 0), 0.2) : 0;
                var rects = OutputLayout.Tiles(columns, spread);
                for (var index = 0; index < rects.Count; index++)
                {
                    if (AddSlice(screenId, OutputLayout.TileName(index, columns), rects[index]) is not { } slice) continue;
                    if (kind != OutputLayoutKind.EdgeBlend || spread <= 0) continue;
                    // The ramp spans the overlap, measured as a fraction of this tile's own width.
                    var rampWidth = spread / rects[index].Width;
                    SetAdjustments(slice.Id, new OutputAdjustments
                    {
                        BlendLeft = index > 0 ? new OutputEdgeBlend(rampWidth) : null,
                        BlendRight = index < columns - 1 ? new OutputEdgeBlend(rampWidth) : null,
                    });
                }
                return;
            }
        }
    }
}
