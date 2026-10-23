using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>
/// Paints a few calm 1920×1080 gradient backgrounds for a new library, so
/// there is something to put behind lyrics on day one. They are ordinary
/// media items: rename, replace or delete them like any other.
/// </summary>
public static class StarterBackgrounds
{
    private sealed record Look(string Name, Color Top, Color Bottom, Color Glow, Point GlowCenter);

    private static readonly Look[] Looks =
    [
        new("Deep Blue", Color.FromRgb(0x0B, 0x12, 0x20), Color.FromRgb(0x14, 0x2A, 0x55), Color.FromArgb(0x80, 0x3B, 0x82, 0xF6), new(0.7, 0.25)),
        new("Dusk", Color.FromRgb(0x1A, 0x10, 0x2E), Color.FromRgb(0x4A, 0x1D, 0x4F), Color.FromArgb(0x70, 0xF4, 0x72, 0xB6), new(0.25, 0.8)),
        new("Sunrise", Color.FromRgb(0x2B, 0x14, 0x0B), Color.FromRgb(0x6B, 0x2E, 0x12), Color.FromArgb(0x90, 0xFA, 0xCC, 0x15), new(0.5, 0.95)),
        new("Evergreen", Color.FromRgb(0x06, 0x1A, 0x17), Color.FromRgb(0x0E, 0x3B, 0x33), Color.FromArgb(0x70, 0x2D, 0xD4, 0xBF), new(0.3, 0.2)),
    ];

    public static void AddTo(LibraryStore library)
    {
        var folder = Path.Combine(Path.GetTempPath(), "MxU Slides starter backgrounds");
        Directory.CreateDirectory(folder);
        foreach (var look in Looks)
        {
            try
            {
                var path = Path.Combine(folder, look.Name + ".png");
                Paint(look, path);
                if (library.ImportMedia(path, MediaClassification.Background) is { } item && item.Name != look.Name)
                {
                    item.Name = look.Name;
                    library.Save(item);
                }
                File.Delete(path);
            }
            catch (Exception ex)
            {
                Log.Warn($"Starter background {look.Name} skipped: {ex.Message}");
            }
        }
    }

    private static void Paint(Look look, string path)
    {
        const int width = 1920, height = 1080;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var rect = new Rect(0, 0, width, height);
            dc.DrawRectangle(new LinearGradientBrush(look.Top, look.Bottom, 90), null, rect);
            var glow = new RadialGradientBrush(look.Glow, Colors.Transparent)
            {
                Center = look.GlowCenter,
                GradientOrigin = look.GlowCenter,
                RadiusX = 0.75,
                RadiusY = 0.9,
            };
            dc.DrawRectangle(glow, null, rect);
            // A soft vignette keeps white lyrics readable at the edges.
            var vignette = new RadialGradientBrush(Colors.Transparent, Color.FromArgb(0x90, 0, 0, 0)) { RadiusX = 0.8, RadiusY = 0.8 };
            dc.DrawRectangle(vignette, null, rect);
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
