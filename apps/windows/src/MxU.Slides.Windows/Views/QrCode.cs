using System.Windows;
using System.Windows.Media;
using QRCoder;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// A QR code as a WPF picture, for Settings' browser remote: point a phone's camera at it and the
/// phone opens the remote's address. It holds only the address (never a key), so anyone who sees
/// it still has to pair with the code. QRCoder (MIT licence) works out the black and white squares;
/// they're drawn here as one vector picture, so it's sharp at any size. Windows only.
/// </summary>
public static class QrCode
{
    public static DrawingImage Image(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        // The matrix includes the four-square white border ("quiet zone") scanners need.
        var modules = data.ModuleMatrix;
        var size = modules.Count;
        var dark = new GeometryGroup { FillRule = FillRule.Nonzero };
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                if (modules[y][x]) dark.Children.Add(new RectangleGeometry(new Rect(x, y, 1, 1)));
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, size, size))));
        drawing.Children.Add(new GeometryDrawing(Brushes.Black, null, dark));
        // Edges snap to whole squares so the code doesn't blur between pixels.
        RenderOptions.SetEdgeMode(drawing, EdgeMode.Aliased);
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }
}
