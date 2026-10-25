using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace MxU.Slides.Windows.Views;

/// <summary>Which edge of the element under the pointer a drop lands at, or Around it (into a folder).</summary>
public enum DropEdge { Top, Bottom, Left, Right, Around }

/// <summary>
/// The accent line that shows where a drag will land (the Mac's RowInsertionLine): drawn over the
/// row or pill under the pointer, on its own layer, so nothing in the list moves while dragging.
/// </summary>
internal sealed class DropLine : Adorner
{
    private readonly DropEdge edge;

    private DropLine(UIElement adorned, DropEdge edge) : base(adorned)
    {
        this.edge = edge;
        IsHitTestVisible = false;
    }

    /// <summary>Shows the line on <paramref name="target"/>; null when the window has no layer to draw it on.</summary>
    public static DropLine? Show(UIElement target, DropEdge edge)
    {
        if (AdornerLayer.GetAdornerLayer(target) is not { } layer) return null;
        var line = new DropLine(target, edge);
        layer.Add(line);
        return line;
    }

    public void Remove()
    {
        if (AdornerLayer.GetAdornerLayer(AdornedElement) is { } layer) layer.Remove(this);
    }

    protected override void OnRender(DrawingContext context)
    {
        var size = AdornedElement.RenderSize;
        var brush = (Brush)Application.Current.FindResource("Accent");
        switch (edge)
        {
            case DropEdge.Top: context.DrawRoundedRectangle(brush, null, new Rect(2, -1.5, Math.Max(0, size.Width - 4), 3), 1.5, 1.5); break;
            case DropEdge.Bottom: context.DrawRoundedRectangle(brush, null, new Rect(2, size.Height - 1.5, Math.Max(0, size.Width - 4), 3), 1.5, 1.5); break;
            case DropEdge.Left: context.DrawRoundedRectangle(brush, null, new Rect(-3, 1, 2, Math.Max(0, size.Height - 2)), 1, 1); break;
            case DropEdge.Right: context.DrawRoundedRectangle(brush, null, new Rect(size.Width + 1, 1, 2, Math.Max(0, size.Height - 2)), 1, 1); break;
            default: context.DrawRoundedRectangle(null, new Pen(brush, 1.5), new Rect(0.75, 0.75, Math.Max(0, size.Width - 1.5), Math.Max(0, size.Height - 1.5)), 6, 6); break;
        }
    }
}
