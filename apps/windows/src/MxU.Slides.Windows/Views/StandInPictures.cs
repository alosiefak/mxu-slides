using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// Pictures to judge a design against, drawn in code so the app needs no image files: a camera
/// shot of a stage (warm light, out-of-focus practicals, optionally someone speaking), for lower
/// thirds on the stream; and a worship background (dark, coloured haze, light beams), for
/// full-screen lyrics. The lower-thirds gallery shows its designs over them, and the self-test
/// imports them as media for its pictures. Each paints a 1920 × 1080 canvas.
/// </summary>
public static class StandInPictures
{
    public const int Width = 1920;
    public const int Height = 1080;

    private static BitmapSource? stage;
    private static BitmapSource? worship;

    /// <summary>The stage shot without anyone in it (for the gallery's previews), drawn once at 1280 × 720.</summary>
    public static BitmapSource Stage => stage ??= Render(dc => PaintStage(dc, speaker: false), 1280, 720);

    /// <summary>The worship background, drawn once at 1280 × 720.</summary>
    public static BitmapSource Worship => worship ??= Render(PaintWorship, 1280, 720);

    /// <summary>Draws <paramref name="paint"/> (a 1920 × 1080 canvas) into a bitmap of the given size.</summary>
    public static BitmapSource Render(Action<DrawingContext> paint, int width = Width, int height = Height)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform((double)width / Width, (double)height / Height));
            paint(dc);
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    public static void PaintWorship(DrawingContext dc)
    {
        var rect = new Rect(0, 0, Width, Height);
        dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x08, 0x0B, 0x1C), Color.FromRgb(0x1C, 0x0C, 0x2A), 90), null, rect);
        void Glow(Color color, double x, double y, double rx, double ry) =>
            dc.DrawRectangle(new RadialGradientBrush(color, Colors.Transparent) { Center = new Point(x, y), GradientOrigin = new Point(x, y), RadiusX = rx, RadiusY = ry }, null, rect);
        Glow(Color.FromArgb(0x90, 0x5B, 0x3D, 0xF5), 0.25, 0.3, 0.5, 0.6);
        Glow(Color.FromArgb(0x80, 0xF5, 0x6B, 0x3D), 0.8, 0.75, 0.45, 0.55);
        Glow(Color.FromArgb(0x60, 0x2D, 0xB4, 0xF5), 0.6, 0.15, 0.35, 0.4);
        // Light beams from the rig.
        foreach (var (x, tilt, alpha) in new[] { (380.0, 220.0, 0x38), (960.0, -60.0, 0x30), (1540.0, -260.0, 0x34) })
        {
            var beam = new StreamGeometry();
            using (var g = beam.Open())
            {
                g.BeginFigure(new Point(x - 18, -10), true, true);
                g.LineTo(new Point(x + 18, -10), true, false);
                g.LineTo(new Point(x + tilt + 220, 1090), true, false);
                g.LineTo(new Point(x + tilt - 220, 1090), true, false);
            }
            dc.DrawGeometry(new LinearGradientBrush(Color.FromArgb((byte)alpha, 0xFF, 0xF2, 0xE0), Colors.Transparent, 90), null, beam);
        }
        var random = new Random(7);
        for (var i = 0; i < 38; i++)
        {
            var r = 8 + random.NextDouble() * 34;
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(18 + random.Next(30)), 0xFF, 0xE8, 0xC8)), null,
                new Point(random.NextDouble() * Width, random.NextDouble() * Height), r, r);
        }
        dc.DrawRectangle(new RadialGradientBrush(Colors.Transparent, Color.FromArgb(0xA0, 0, 0, 0)) { RadiusX = 0.85, RadiusY = 0.85 }, null, rect);
    }

    /// <summary>A stage: warm back wall, a darker floor, practical lights out of focus; with <paramref name="speaker"/>, someone a little left of centre.</summary>
    public static void PaintStage(DrawingContext dc, bool speaker = true)
    {
        var rect = new Rect(0, 0, Width, Height);
        dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x2A, 0x22, 0x1E), Color.FromRgb(0x14, 0x10, 0x10), 90), null, rect);
        dc.DrawRectangle(new RadialGradientBrush(Color.FromArgb(0xB0, 0xE9, 0x9A, 0x55), Colors.Transparent)
            { Center = new Point(0.72, 0.35), GradientOrigin = new Point(0.72, 0.35), RadiusX = 0.5, RadiusY = 0.6 }, null, rect);
        dc.DrawRectangle(new RadialGradientBrush(Color.FromArgb(0x70, 0x4F, 0x7C, 0xD9), Colors.Transparent)
            { Center = new Point(0.12, 0.2), GradientOrigin = new Point(0.12, 0.2), RadiusX = 0.4, RadiusY = 0.5 }, null, rect);
        var random = new Random(3);
        for (var i = 0; i < 26; i++)
        {
            var r = 14 + random.NextDouble() * 40;
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(26 + random.Next(40)), 0xFF, 0xD9, 0xA0)), null,
                new Point(random.NextDouble() * Width, random.NextDouble() * 560), r, r);
        }
        if (speaker)
        {
            // Someone speaking, a little left of centre (where a camera operator frames them).
            var skin = new SolidColorBrush(Color.FromRgb(0x8A, 0x5E, 0x46));
            var shirt = new LinearGradientBrush(Color.FromRgb(0x2C, 0x3E, 0x58), Color.FromRgb(0x18, 0x22, 0x33), 90);
            dc.DrawRoundedRectangle(shirt, null, new Rect(640, 620, 640, 520), 220, 220);
            dc.DrawRectangle(skin, null, new Rect(905, 520, 110, 130));
            dc.DrawEllipse(skin, null, new Point(960, 420), 118, 148);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x24, 0x18, 0x12)), null, new Point(960, 312), 124, 70);
        }
        dc.DrawRectangle(new LinearGradientBrush(Colors.Transparent, Color.FromArgb(0x90, 0, 0, 0), 90), null, new Rect(0, 640, Width, 440));
        dc.DrawRectangle(new RadialGradientBrush(Colors.Transparent, Color.FromArgb(0x70, 0, 0, 0)) { RadiusX = 0.9, RadiusY = 0.9 }, null, rect);
    }
}
