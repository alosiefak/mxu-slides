using System.Globalization;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Outputs;

/// <summary>A size offered by the "Add Screen" menu.</summary>
public sealed record ScreenSizeChoice(string Label, int Width, int Height);

/// <summary>One line of a screen's status in the screen list, e.g. "1920×1080 · Projector".</summary>
public sealed record ScreenStatusLine(string Text, bool? Connected);

/// <summary>A blend seam between two neighbouring Edge Blend tiles, left then right.</summary>
public sealed record OutputSeam(OutputSliceState Leading, OutputSliceState Trailing)
{
    /// <summary>How far the tiles overlap, as a fraction of the canvas.</summary>
    public double Overlap => Math.Max(Leading.SourceRect.Right - Trailing.SourceRect.X, 0);
}

/// <summary>
/// The non-UI logic of the Mac's screen settings (ScreenConfigurationView.swift
/// and OutputsSection.swift): menus' choices, naming, the layout picker's and
/// output stepper's actions, seam overlap, titles and status lines. The WPF
/// settings screen binds to these so it behaves like the Mac's.
/// </summary>
public static class ScreenEditing
{
    public static readonly IReadOnlyList<ScreenSizeChoice> AddScreenSizes =
    [
        new("1080p  (1920×1080)", 1920, 1080),
        new("4K  (3840×2160)", 3840, 2160),
        new("Vertical 9:16  (1080×1920)", 1080, 1920),
    ];

    /// <summary>The frame-rate menu: match the venue or stream rate.</summary>
    public static readonly IReadOnlyList<int> FrameRates = [24, 25, 30, 50, 60];

    /// <summary>How many outputs Mirror, Grouped and Edge Blend can have.</summary>
    public const int MinOutputCount = 2;
    public const int MaxOutputCount = 6;

    /// <summary>The overlap a new Edge Blend starts with: 6% of the canvas.</summary>
    public const double DefaultOverlap = 0.06;

    /// <summary>The placement steppers move by this many pixels.</summary>
    public const double PlacementStep = 8;

    /// <summary>Adds a screen named "Screen n" (n = how many there will be). The menu's main click adds 1080p.</summary>
    public static PlaceholderScreen? AddScreen(OutputConfiguration outputs, int width = 1920, int height = 1080) =>
        outputs.AddScreen($"Screen {outputs.Screens.Count + 1}", width, height);

    /// <summary>
    /// Applies the name and size typed in a screen's header. Returns false when
    /// they aren't valid (a blank name, or a size that isn't a positive whole
    /// number); the field should then show the screen's current values again.
    /// </summary>
    public static bool CommitIdentity(OutputConfiguration outputs, Guid screenId, string name, string widthText, string heightText)
    {
        if (outputs.Screen(screenId) is not { } screen) return false;
        var trimmed = name.Trim(' ', '\t');
        if (!int.TryParse(widthText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var width)
            || !int.TryParse(heightText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var height)
            || width <= 0 || height <= 0 || trimmed.Length == 0)
            return false;
        if (trimmed != screen.Name || width != screen.Width || height != screen.Height)
            outputs.ReconfigureScreen(screenId, trimmed, width, height, screen.FramesPerSecond);
        return true;
    }

    public static void SetFrameRate(OutputConfiguration outputs, Guid screenId, int rate)
    {
        if (outputs.Screen(screenId) is not { } screen || rate == screen.FramesPerSecond) return;
        outputs.ReconfigureScreen(screenId, screen.Name, screen.Width, screen.Height, rate);
    }

    /// <summary>
    /// The screen list's second line: size, " · confidence" for a stage screen,
    /// then what carries it. <see cref="ScreenStatusLine.Connected"/> is true for
    /// a connected display (green), false for a missing one (orange), null otherwise.
    /// </summary>
    public static ScreenStatusLine Status(OutputConfiguration outputs, PlaceholderScreen screen)
    {
        var size = $"{screen.Width}×{screen.Height}";
        if (outputs.Role(screen.Id) == ScreenRole.Confidence) size += " · confidence";
        if (outputs.Device(screen.Id) is not { } device) return new ScreenStatusLine($"{size} · placeholder", null);
        switch (OutputDevice.Kind(device))
        {
            case OutputDeviceKind.Ndi:
                return new ScreenStatusLine($"{size} · NDI “{OutputDevice.Value(device)}”", null);
            case OutputDeviceKind.DeckLink:
                return new ScreenStatusLine($"{size} · {outputs.DeviceName(screen.Id) ?? "DeckLink"}", null);
        }
        var connected = outputs.Displays.FirstOrDefault(display => display.Id == device);
        var name = connected?.Name ?? outputs.DeviceName(screen.Id) ?? "Display";
        return connected is not null
            ? new ScreenStatusLine($"{size} · {name}", true)
            : new ScreenStatusLine($"{size} · disconnected: {name}", false);
    }

    /// <summary>What carries a slice, for its tile: the device's name, "NDI", or "Unconfigured".</summary>
    public static string BackingSummary(OutputConfiguration outputs, Guid sliceId)
    {
        if (outputs.Device(sliceId) is not { } device) return "Unconfigured";
        return OutputDevice.Kind(device) switch
        {
            OutputDeviceKind.Ndi => "NDI",
            OutputDeviceKind.DeckLink => outputs.DeviceName(sliceId) ?? "DeckLink",
            _ => outputs.DeviceName(sliceId) ?? outputs.Displays.FirstOrDefault(display => display.Id == device)?.Name ?? "Display",
        };
    }

    /// <summary>A slice's title: the default slice is "Main" in a mirror and "Full Canvas" otherwise.</summary>
    public static string SliceTitle(Guid screenId, OutputSliceState slice, OutputLayoutKind layout) =>
        slice.Id == screenId ? layout == OutputLayoutKind.Mirror ? "Main" : "Full Canvas" : slice.Name ?? "Output";

    /// <summary>The layout picker's choices: the named layouts, plus Custom while the setup is custom.</summary>
    public static IReadOnlyList<OutputLayoutKind> LayoutChoices(OutputLayoutKind current) =>
        current == OutputLayoutKind.Custom
            ? OutputLayoutKinds.All
            : [OutputLayoutKind.Single, OutputLayoutKind.Mirror, OutputLayoutKind.Grouped, OutputLayoutKind.EdgeBlend, OutputLayoutKind.LedWall];

    /// <summary>The layout picker's tooltip.</summary>
    public static string LayoutHelp(OutputLayoutKind kind) => kind switch
    {
        OutputLayoutKind.Single => "One output carries the whole canvas.",
        OutputLayoutKind.Mirror => "Every output shows the whole canvas — same picture on each.",
        OutputLayoutKind.Grouped => "Outputs tile the canvas edge to edge — a video wall with hard seams.",
        OutputLayoutKind.EdgeBlend => "Outputs overlap and ramp into each other — click a seam to tune its blend.",
        OutputLayoutKind.LedWall => "Wall-native pixels landing at exact coordinates inside the processor's frame — 1:1, never scaled.",
        _ => "Hand-placed regions — anything the named shapes can't express.",
    };

    /// <summary>The extra slices left to right: the tiles of a Grouped or Edge Blend wall.</summary>
    public static IReadOnlyList<OutputSliceState> TilesByPosition(OutputConfiguration outputs, Guid screenId) =>
        outputs.ExtraSlices(screenId).OrderBy(slice => slice.SourceRect.X).ToList();

    /// <summary>The number the outputs stepper shows.</summary>
    public static int OutputCount(OutputConfiguration outputs, Guid screenId) =>
        outputs.Layout(screenId) == OutputLayoutKind.Mirror
            ? outputs.Slices(screenId).Count
            : Math.Max(outputs.ExtraSlices(screenId).Count, MinOutputCount);

    /// <summary>The layout picker's action: rebuilds the screen as the chosen layout (no-op when it already is one).</summary>
    public static void ChooseLayout(OutputConfiguration outputs, Guid screenId, OutputLayoutKind kind, Action<Guid>? releasingCarries = null)
    {
        if (kind == outputs.Layout(screenId)) return;
        outputs.ApplyLayout(kind, Math.Max(OutputCount(outputs, screenId), MinOutputCount), DefaultOverlap, screenId, releasingCarries);
    }

    /// <summary>
    /// The outputs stepper's action. A mirror gains or loses copies at the end
    /// (new ones named "Mirror n"); a Grouped or Edge Blend wall is rebuilt with
    /// that many tiles, keeping its current overlap.
    /// </summary>
    public static void SetOutputCount(OutputConfiguration outputs, Guid screenId, int count, Action<Guid>? releasingCarries = null)
    {
        var release = releasingCarries ?? (_ => { });
        var kind = outputs.Layout(screenId);
        switch (kind)
        {
            case OutputLayoutKind.Mirror:
                var current = outputs.ExtraSlices(screenId);
                while (current.Count + 1 < count)
                {
                    if (outputs.AddSlice(screenId, $"Mirror {current.Count + 2}") is null) break;
                    current = outputs.ExtraSlices(screenId);
                }
                while (current.Count + 1 > count && current.Count > 0)
                {
                    var last = current[^1];
                    release(last.Id);
                    outputs.RemoveSlice(last.Id);
                    current = outputs.ExtraSlices(screenId);
                }
                break;
            case OutputLayoutKind.Grouped:
            case OutputLayoutKind.EdgeBlend:
                var overlap = Seams(outputs, screenId).FirstOrDefault()?.Overlap ?? DefaultOverlap;
                outputs.ApplyLayout(kind, count, overlap, screenId, release);
                break;
        }
    }

    /// <summary>The seams between neighbouring tiles of an Edge Blend wall (none for any other layout).</summary>
    public static IReadOnlyList<OutputSeam> Seams(OutputConfiguration outputs, Guid screenId)
    {
        var tiles = TilesByPosition(outputs, screenId);
        if (outputs.Layout(screenId) != OutputLayoutKind.EdgeBlend || tiles.Count < 2) return [];
        return Enumerable.Range(0, tiles.Count - 1).Select(index => new OutputSeam(tiles[index], tiles[index + 1])).ToList();
    }

    /// <summary>A seam's overlap in canvas pixels, as the seam panel shows it.</summary>
    public static int OverlapPixels(PlaceholderScreen screen, OutputSeam seam) =>
        (int)Math.Round(seam.Overlap * screen.Width, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Changes every seam's overlap at once: re-tiles the wall and resizes each
    /// inner blend ramp to match, keeping each ramp's curve, depth and black lift.
    /// </summary>
    public static void SetOverlap(OutputConfiguration outputs, Guid screenId, double overlap)
    {
        var tiles = TilesByPosition(outputs, screenId);
        if (tiles.Count < 2) return;
        var rects = OutputLayout.Tiles(tiles.Count, overlap);
        for (var index = 0; index < tiles.Count; index++)
        {
            outputs.SetSliceSourceRect(tiles[index].Id, rects[index]);
            var adjustments = outputs.Adjustments(tiles[index].Id);
            var rampWidth = overlap / rects[index].Width;
            if (index > 0)
                adjustments = adjustments with { BlendLeft = (adjustments.BlendLeft ?? new OutputEdgeBlend(rampWidth)) with { Width = rampWidth } };
            if (index < tiles.Count - 1)
                adjustments = adjustments with { BlendRight = (adjustments.BlendRight ?? new OutputEdgeBlend(rampWidth)) with { Width = rampWidth } };
            outputs.SetAdjustments(tiles[index].Id, adjustments);
        }
    }

    /// <summary>Custom's "Add Output": a new full-canvas slice named "Output n", to be shrunk to its region.</summary>
    public static OutputSliceState? AddOutput(OutputConfiguration outputs, Guid screenId) =>
        outputs.AddSlice(screenId, $"Output {outputs.Slices(screenId).Count + 1}");

    /// <summary>"Place in frame" switched on: the slice at its own pixel size in a 1080p frame's top-left corner.</summary>
    public static OutputPlacement DefaultPlacement(OutputConfiguration outputs, Guid sliceId)
    {
        var info = outputs.SliceInfo(sliceId);
        return new OutputPlacement(1920, 1080, 0, 0, Math.Min(info?.Width ?? 1920, 1920), Math.Min(info?.Height ?? 1080, 1080));
    }

    /// <summary>A placement field's up arrow: to the next multiple of the step.</summary>
    public static double StepUp(double value, double step = PlacementStep) => Math.Floor(value / step) * step + step;

    /// <summary>A placement field's down arrow: to the previous multiple of the step, never below 0.</summary>
    public static double StepDown(double value, double step = PlacementStep) => Math.Max(0, Math.Ceiling(value / step) * step - step);

    /// <summary>A typed frame width or height: whole pixels, at least 8.</summary>
    public static int FrameDimension(double value) => Math.Max((int)value, 8);

    /// <summary>A typed content width or height: at least 8 pixels.</summary>
    public static double ContentDimension(double value) => Math.Max(value, 8);

    /// <summary>A slice inside a packed frame, labelled "Screen — Slice" (or just the screen for its default slice).</summary>
    public static string PackedLabel(OutputConfiguration outputs, Guid sliceId)
    {
        if (outputs.SliceInfo(sliceId) is not { } info) return "Output";
        var screenName = outputs.Screen(info.ScreenId)?.Name ?? "Screen";
        return info.Name is { } name ? $"{screenName} — {name}" : screenName;
    }

    /// <summary>The other slices sharing a slice's device (a packed frame), in id order.</summary>
    public static IReadOnlyList<Guid> CarrySiblings(OutputConfiguration outputs, Guid sliceId) =>
        outputs.Device(sliceId) is { } device
            ? outputs.Devices.Where(pair => pair.Value == device && pair.Key != sliceId)
                .Select(pair => pair.Key).OrderBy(SwiftUuid.Format, StringComparer.Ordinal).ToList()
            : [];

    /// <summary>The canvas rect a tile diagram draws a slice at, in the diagram's own size.</summary>
    public static SceneRect DiagramRect(OutputSliceState slice, SceneSize diagram, double minimum = 10) => new(
        slice.SourceRect.X * diagram.Width, slice.SourceRect.Y * diagram.Height,
        Math.Max(slice.SourceRect.Width * diagram.Width, minimum), Math.Max(slice.SourceRect.Height * diagram.Height, minimum));
}
