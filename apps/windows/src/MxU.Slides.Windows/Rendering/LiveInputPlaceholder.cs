using System.Windows;
using System.Windows.Media;

namespace MxU.Slides.Windows.Rendering;

/// <summary>
/// The picture a live input or a screen mirror shows where the real one isn't drawn: in slide
/// thumbnails and the editor (so a picture-in-picture camera reads as "a camera goes here"), and
/// while an input isn't delivering. Faint diagonal stripes, a border and a glyph: a camera, the
/// NDI waves, or a screen on a stand. Port of the Mac's Sources/LiveInputPlaceholder.swift (its
/// 1920×1080 drawing, its colours and line widths), drawn as vectors.
/// </summary>
public static class LiveInputPlaceholder
{
    public enum Glyph { Camera, Ndi, Screen }

    private const double Width = 1920;
    private const double Height = 1080;

    public static readonly Size NaturalSize = new(Width, Height);

    private static readonly Dictionary<Glyph, Drawing> Drawings = [];

    /// <summary>Which placeholder an engine id gets (LiveInputPlaceholder.remap); null for ordinary media.</summary>
    public static Glyph? For(string mediaId)
    {
        if (mediaId.StartsWith("screen::", StringComparison.Ordinal)) return Glyph.Screen;
        if (!mediaId.StartsWith("input::", StringComparison.Ordinal)) return null;
        return mediaId.StartsWith("input::ndi::", StringComparison.Ordinal) ? Glyph.Ndi : Glyph.Camera;
    }

    /// <summary>The placeholder drawn over <paramref name="target"/> (it's 16:9; place it with the fill's scale mode first).</summary>
    public static void Draw(DrawingContext dc, Glyph glyph, Rect target)
    {
        dc.PushTransform(new MatrixTransform(target.Width / Width, 0, 0, target.Height / Height, target.X, target.Y));
        dc.DrawDrawing(DrawingFor(glyph));
        dc.Pop();
    }

    private static Drawing DrawingFor(Glyph glyph)
    {
        lock (Drawings)
        {
            if (Drawings.TryGetValue(glyph, out var cached)) return cached;
            var drawing = Make(glyph);
            Drawings[glyph] = drawing;
            return drawing;
        }
    }

    private static Pen Stroke(double alpha, double width, bool round = false)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb((byte)Math.Round(alpha * 255), 255, 255, 255)), width);
        if (round)
        {
            pen.StartLineCap = PenLineCap.Round;
            pen.EndLineCap = PenLineCap.Round;
            pen.LineJoin = PenLineJoin.Round;
        }
        pen.Freeze();
        return pen;
    }

    // The Mac draws with Core Graphics, y up from the bottom; WPF's y runs down, so each y is flipped.
    private static Point P(double x, double y) => new(x, Height - y);

    private static Drawing Make(Glyph glyph)
    {
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            var bounds = new Rect(0, 0, Width, Height);
            var fill = new SolidColorBrush(Color.FromArgb((byte)Math.Round(0.20 * 255), 26, 28, 33));
            fill.Freeze();
            dc.DrawRectangle(fill, null, bounds);

            dc.PushClip(new RectangleGeometry(bounds));
            var stripes = Stroke(0.16, 22);
            for (var x = -Height; x < Width; x += 200) dc.DrawLine(stripes, P(x, 0), P(x + Height, Height));
            dc.Pop();

            dc.DrawRectangle(null, Stroke(0.42, 12), new Rect(22, 22, Width - 44, Height - 44));

            var ink = Stroke(0.62, 18, round: true);
            var inkBrush = ink.Brush;
            var cx = Width / 2;
            var cy = Height / 2;
            switch (glyph)
            {
                case Glyph.Ndi:
                {
                    var dot = new Point(cx - 130 + 22, Height - cy);
                    dc.DrawEllipse(inkBrush, null, dot, 22, 22);
                    var radii = new[] { 90.0, 150.0, 210.0 };
                    for (var i = 0; i < radii.Length; i++)
                    {
                        // Quarter arcs facing right, each a little fainter, as the Mac draws the waves.
                        var r = radii[i];
                        var from = new Point(dot.X + r * Math.Cos(-Math.PI / 4), dot.Y + r * Math.Sin(-Math.PI / 4));
                        var to = new Point(dot.X + r * Math.Cos(Math.PI / 4), dot.Y + r * Math.Sin(Math.PI / 4));
                        var arc = new PathGeometry([new PathFigure(from, [new ArcSegment(to, new Size(r, r), 0, false, SweepDirection.Clockwise, true)], false)]);
                        dc.PushOpacity(1 - i * 0.18);
                        dc.DrawGeometry(null, ink, arc);
                        dc.Pop();
                    }
                    break;
                }
                case Glyph.Camera:
                {
                    var body = new Rect(cx - 190, Height - (cy + 105), 250, 210);
                    dc.DrawRoundedRectangle(null, ink, body, 34, 34);
                    var lens = new PathGeometry([new PathFigure(P(body.Right + 26, cy - 42),
                        [new PolyLineSegment([P(body.Right + 128, cy - 88), P(body.Right + 128, cy + 88), P(body.Right + 26, cy + 42)], true)], true)]);
                    dc.DrawGeometry(null, ink, lens);
                    break;
                }
                case Glyph.Screen:
                {
                    // The panel sits above the centre with its stand and foot below it.
                    var panelMinY = cy - 60;
                    var panel = new Rect(cx - 220, Height - (panelMinY + 280), 440, 280);
                    dc.DrawRoundedRectangle(null, ink, panel, 30, 30);
                    dc.DrawLine(ink, P(cx, panelMinY), P(cx, panelMinY - 70));
                    dc.DrawLine(ink, P(cx - 120, panelMinY - 90), P(cx + 120, panelMinY - 90));
                    break;
                }
            }
        }
        group.Freeze();
        return group;
    }
}
