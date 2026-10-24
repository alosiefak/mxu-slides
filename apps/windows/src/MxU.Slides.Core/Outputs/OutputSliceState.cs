using System.Text.Json.Serialization;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Outputs;

// A screen is a routing identity (say "Main Wall", 3840×1080). Its picture can
// leave the computer through several outputs, called slices: a projector for
// the left half, another for the right, a mirror for the lobby. Every screen
// has one implicit slice whose id is the screen's own id and which shows the
// whole canvas; extra slices each show a region of it. Port of
// OutputEngine/OutputSliceState.swift.

/// <summary>
/// Where a slice's pixels land inside the signal its device sends, for LED
/// processors that read a fixed-size frame (say 1920×1080) and expect the wall's
/// pixels at exact coordinates in it, 1:1 and never scaled.
/// </summary>
public sealed record OutputPlacement
{
    public OutputPlacement() { }

    public OutputPlacement(int frameWidth, int frameHeight, double x, double y, double width, double height)
    {
        FrameWidth = frameWidth;
        FrameHeight = frameHeight;
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    /// <summary>The full size of the signal on the wire, in pixels.</summary>
    [JsonPropertyName("frameWidth")]
    public int FrameWidth { get; init; }

    [JsonPropertyName("frameHeight")]
    public int FrameHeight { get; init; }

    /// <summary>The rectangle inside that frame where the content goes, in frame pixels.</summary>
    [JsonPropertyName("x")]
    public double X { get; init; }

    [JsonPropertyName("y")]
    public double Y { get; init; }

    [JsonPropertyName("width")]
    public double Width { get; init; }

    [JsonPropertyName("height")]
    public double Height { get; init; }

    /// <summary>The content rectangle as fractions of the frame (what the renderer's placement takes).</summary>
    [JsonIgnore]
    public SceneRect UnitRect
    {
        get
        {
            double w = Math.Max(FrameWidth, 1), h = Math.Max(FrameHeight, 1);
            return new SceneRect(X / w, Y / h, Width / w, Height / h);
        }
    }

    /// <summary>True when the content fills the whole frame, which is the same as having no placement.</summary>
    [JsonIgnore]
    public bool IsFill
    {
        get
        {
            var unit = UnitRect;
            return Math.Abs(unit.X) < 0.0001 && Math.Abs(unit.Y) < 0.0001
                && Math.Abs(unit.Width - 1) < 0.0001 && Math.Abs(unit.Height - 1) < 0.0001;
        }
    }
}

/// <summary>
/// One output of a screen. <see cref="SourceRect"/> is the region of the
/// screen's canvas it shows, in unit coordinates (0–1); the default slice's
/// is the whole canvas. Change with <c>with</c>.
/// </summary>
public sealed record OutputSliceState(Guid Id, string? Name, SceneRect SourceRect);

/// <summary>
/// Everything needed to render one slice: its screen, the canvas region it
/// shows, its size in pixels (the region at the screen's resolution) and its name.
/// </summary>
public sealed record OutputSliceInfo(Guid ScreenId, SceneRect SourceRect, int Width, int Height, string? Name);
