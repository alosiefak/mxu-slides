using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MxU.Slides.Windows.Outputs;

namespace MxU.Slides.Windows.Infrastructure;

public sealed partial class SelfTest
{
    /// <summary>
    /// A capture bigger than the main screen (a 4K feed; on most PCs the screen is smaller) keeps
    /// its full size and the picture fills it: it's drawn in screen-sized pieces put back together.
    /// A 1080p stream from a 1366 × 768 laptop used to come out shrunk with black bars.
    /// </summary>
    private async Task BiggerThanTheScreenCheck()
    {
        const int width = 3840, height = 2160;
        // Four coloured quarters: a shrunk or shifted picture puts the wrong colour in a corner.
        var quarters = new Grid();
        quarters.RowDefinitions.Add(new RowDefinition());
        quarters.RowDefinitions.Add(new RowDefinition());
        quarters.ColumnDefinitions.Add(new ColumnDefinition());
        quarters.ColumnDefinitions.Add(new ColumnDefinition());
        void Quarter(int row, int column, Color color)
        {
            var cell = new Border { Background = new SolidColorBrush(color) };
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, column);
            quarters.Children.Add(cell);
        }
        Quarter(0, 0, Colors.Red);
        Quarter(0, 1, Colors.Lime);
        Quarter(1, 0, Colors.Blue);
        Quarter(1, 1, Colors.White);
        var sink = new CountingSink();
        using var capture = new HiddenCapture(quarters, width, height, 30, () => [sink], "MxU Slides 4K self-test");
        static bool Near(byte[] pixels, int x, int y, byte r, byte g, byte b)
        {
            var i = (y * width + x) * 4;
            return Math.Abs(pixels[i + 2] - r) < 40 && Math.Abs(pixels[i + 1] - g) < 40 && Math.Abs(pixels[i] - b) < 40;
        }
        // A point well inside each quarter, near each corner of the picture.
        static bool Corners(byte[]? pixels) => pixels is { Length: width * height * 4 }
            && Near(pixels, 40, 40, 255, 0, 0) && Near(pixels, width - 40, 40, 0, 255, 0)
            && Near(pixels, 40, height - 40, 0, 0, 255) && Near(pixels, width - 40, height - 40, 255, 255, 255);
        // Up to 6 seconds for every piece's first drawn picture to arrive (a PC without Windows'
        // capture switches to the slower copy after 2).
        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < TimeSpan.FromSeconds(6) && !Corners(sink.Last)) await Delay(100);
        report.AppendLine($"      4K capture: {capture.Tiles} piece(s), windows as planned: {capture.WindowsAsPlanned}, frames {sink.Width}x{sink.Height}"
            + $" (screen {SystemParameters.PrimaryScreenWidth:0}x{SystemParameters.PrimaryScreenHeight:0}), "
            + (capture.UsesSoftwareCopy ? "software copy" : "Windows' capture") + $", whole picture after {waited.Elapsed.TotalSeconds:0.0} s");
        Check("a capture bigger than the screen keeps its full size (4K)", capture.WindowsAsPlanned && sink.Width == width && sink.Height == height);
        if (sink.Last is not { } pixels || sink.Width != width || sink.Height != height)
        {
            Check("a 4K capture delivers 4K pictures", false);
            return;
        }
        Check("the 4K picture fills the frame (each corner shows its own quarter)", Corners(pixels));
        // Saved at a quarter of the size: 4K pictures are big.
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        Write(new TransformedBitmap(bitmap, new ScaleTransform(0.25, 0.25)), "04-capture-4k");
    }
}
