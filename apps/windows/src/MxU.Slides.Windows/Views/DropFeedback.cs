using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// What a drag shows over the place it would drop: an accent line between two things (a slide
/// lands between these tiles, a row between these rows) or an outline round one thing (this tile
/// gets the background, this shape gets the picture). Drawn on the control's adorner layer, so
/// it sits above everything and never takes the mouse. Esc during a drag cancels it, which is
/// Windows' own rule for drag and drop; the feedback is cleared then too.
/// </summary>
public sealed class DropFeedback : Adorner
{
    private static readonly Brush Line = Frozen(new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)));
    private static readonly Pen Ring = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)), 3));
    private static readonly Brush Wash = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0x3B, 0x82, 0xF6)));

    private Rect? line;
    private Rect? ring;

    private DropFeedback(UIElement owner) : base(owner) => IsHitTestVisible = false;

    private static T Frozen<T>(T value) where T : Freezable
    {
        value.Freeze();
        return value;
    }

    /// <summary>The feedback for <paramref name="owner"/>, made the first time it's needed.</summary>
    public static DropFeedback? For(UIElement owner)
    {
        if (AdornerLayer.GetAdornerLayer(owner) is not { } layer) return null;
        if (layer.GetAdorners(owner)?.OfType<DropFeedback>().FirstOrDefault() is { } existing) return existing;
        var made = new DropFeedback(owner);
        layer.Add(made);
        return made;
    }

    /// <summary>A vertical line at <paramref name="x"/> from <paramref name="top"/> to <paramref name="bottom"/> (between tiles).</summary>
    public void VerticalLine(double x, double top, double bottom) => Show(new Rect(x - 1.5, top, 3, Math.Max(0, bottom - top)), null);

    /// <summary>A horizontal line at <paramref name="y"/> (between rows).</summary>
    public void HorizontalLine(double y, double left, double right) => Show(new Rect(left, y - 1.5, Math.Max(0, right - left), 3), null);

    /// <summary>An outline round <paramref name="bounds"/>: the drop lands on that thing.</summary>
    public void Outline(Rect bounds) => Show(null, bounds);

    public void Clear() => Show(null, null);

    private void Show(Rect? newLine, Rect? newRing)
    {
        if (line == newLine && ring == newRing) return;
        line = newLine;
        ring = newRing;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (line is { } l) dc.DrawRoundedRectangle(Line, null, l, 1.5, 1.5);
        if (ring is { } r) dc.DrawRoundedRectangle(Wash, Ring, r, 6, 6);
    }
}
