using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MxU.Slides.PptxImport;

/// <summary>
/// Bakes tiled picture and structural pattern backgrounds into a canvas-sized PNG,
/// since MxU fills cannot tile. Drawing follows the Mac's Core Graphics code, whose
/// origin is bottom-left, so rectangles and diagonals are flipped into WPF's top-left space.
/// </summary>
internal static class TextureBaker
{
    public static readonly HashSet<string> StructuralPatterns = new(StringComparer.Ordinal)
    {
        "lgGrid", "smGrid", "dotGrid",
        "horz", "ltHorz", "narHorz", "dkHorz",
        "vert", "ltVert", "narVert", "dkVert",
        "cross", "diagCross",
        "lgCheck", "smCheck",
        "ltUpDiag", "ltDnDiag", "dkUpDiag", "dkDnDiag", "wdUpDiag", "wdDnDiag",
    };

    public static bool Bake(TextureBake bake, int canvasWidth, int canvasHeight, string output) =>
        RunOnSta(() => BakeHere(bake, canvasWidth, canvasHeight, output));

    /// <summary>Bakes each entry into <c>baked-background-N.png</c> in <paramref name="directory"/>, returning the ones that worked.</summary>
    public static List<MediaWant> BakeAll(IReadOnlyList<TextureBake> bakes, int canvasWidth, int canvasHeight, string directory) =>
        RunOnSta(() =>
        {
            var baked = new List<MediaWant>();
            for (var index = 0; index < bakes.Count; index++)
            {
                var output = Path.Combine(directory, $"baked-background-{index}.png");
                if (BakeHere(bakes[index], canvasWidth, canvasHeight, output))
                    baked.Add(new MediaWant(bakes[index].PlaceholderId, output));
            }
            return baked;
        });

    private static bool BakeHere(TextureBake bake, int canvasWidth, int canvasHeight, string output)
    {
        if (canvasWidth <= 0 || canvasHeight <= 0) return false;
        try
        {
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                switch (bake.Kind)
                {
                    case TextureBakeKind.Texture texture:
                        if (!DrawTexture(texture, bake.K, canvasWidth, canvasHeight, context)) return false;
                        break;
                    case TextureBakeKind.Pattern pattern:
                        DrawPattern(pattern, bake.K, canvasWidth, canvasHeight, context);
                        break;
                }
            }
            var bitmap = new RenderTargetBitmap(canvasWidth, canvasHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(output);
            encoder.Save(stream);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException
                                       or FileFormatException or ArgumentException or InvalidOperationException
                                       or System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    private static bool DrawTexture(TextureBakeKind.Texture texture, double k, int canvasWidth, int canvasHeight, DrawingContext context)
    {
        BitmapSource image;
        using (var source = File.OpenRead(texture.SourcePath))
            image = BitmapDecoder.Create(source, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];

        context.DrawRectangle(Brushes.White, null, new Rect(0, 0, canvasWidth, canvasHeight));
        context.PushOpacity(Math.Clamp(texture.Alpha, 0, 1));

        var tileWidth = image.PixelWidth * texture.ScaleX * k;
        var tileHeight = image.PixelHeight * texture.ScaleY * k;
        if (tileWidth < 1 || tileHeight < 1) return false;

        if (texture.Tiled)
        {
            for (var y = 0.0; y < canvasHeight; y += tileHeight)
            {
                for (var x = 0.0; x < canvasWidth; x += tileWidth)
                    context.DrawImage(image, Flipped(x, y, tileWidth, tileHeight, canvasHeight));
            }
        }
        else
        {
            context.DrawImage(image, new Rect(0, 0, canvasWidth, canvasHeight));
        }
        context.Pop();
        return true;
    }

    private static void DrawPattern(TextureBakeKind.Pattern pattern, double k, int canvasWidth, int canvasHeight, DrawingContext context)
    {
        double width = canvasWidth, height = canvasHeight;
        context.DrawRectangle(Brush(pattern.BackgroundHex), null, new Rect(0, 0, width, height));
        var ink = Brush(pattern.ForegroundHex);

        var cell = 8 * k;
        var thin = Math.Max(1, k);
        var thick = Math.Max(2, 2 * k);

        void Fill(double x, double y, double w, double h) => context.DrawRectangle(ink, null, Flipped(x, y, w, h, height));
        void Lines(bool horizontal, double stride, double lineWidth)
        {
            var limit = horizontal ? height : width;
            for (var position = 0.0; position < limit; position += stride)
            {
                if (horizontal) Fill(0, position, width, lineWidth);
                else Fill(position, 0, lineWidth, height);
            }
        }
        void Diagonals(bool up, double stride, double lineWidth)
        {
            var pen = new Pen(ink, lineWidth);
            pen.Freeze();
            for (var offset = -height; offset < width; offset += stride)
            {
                // Core Graphics y runs upward: (offset, up ? 0 : h) → (offset + h, up ? h : 0).
                context.DrawLine(pen, new Point(offset, up ? height : 0), new Point(offset + height, up ? 0 : height));
            }
        }

        switch (pattern.Preset)
        {
            case "lgGrid":
                Lines(horizontal: true, cell, thin);
                Lines(horizontal: false, cell, thin);
                break;
            case "smGrid" or "cross":
                Lines(horizontal: true, cell / 2, thin);
                Lines(horizontal: false, cell / 2, thin);
                break;
            case "dotGrid":
                for (var y = 0.0; y < height; y += cell)
                {
                    for (var x = 0.0; x < width; x += cell) Fill(x, y, thin, thin);
                }
                break;
            case "horz" or "ltHorz": Lines(horizontal: true, cell / 2, thin); break;
            case "narHorz": Lines(horizontal: true, cell / 4, thin); break;
            case "dkHorz": Lines(horizontal: true, cell / 2, thick); break;
            case "vert" or "ltVert": Lines(horizontal: false, cell / 2, thin); break;
            case "narVert": Lines(horizontal: false, cell / 4, thin); break;
            case "dkVert": Lines(horizontal: false, cell / 2, thick); break;
            case "ltUpDiag": Diagonals(up: true, cell / 2, thin); break;
            case "ltDnDiag": Diagonals(up: false, cell / 2, thin); break;
            case "dkUpDiag": Diagonals(up: true, cell / 2, thick); break;
            case "dkDnDiag": Diagonals(up: false, cell / 2, thick); break;
            case "wdUpDiag": Diagonals(up: true, cell, thick); break;
            case "wdDnDiag": Diagonals(up: false, cell, thick); break;
            case "diagCross":
                Diagonals(up: true, cell / 2, thin);
                Diagonals(up: false, cell / 2, thin);
                break;
            case "lgCheck" or "smCheck":
            {
                var square = pattern.Preset == "lgCheck" ? cell / 2 : cell / 4;
                var row = 0;
                for (var y = 0.0; y < height; y += square, row++)
                {
                    for (var x = row % 2 == 0 ? 0.0 : square; x < width; x += square * 2) Fill(x, y, square, square);
                }
                break;
            }
        }
    }

    private static Rect Flipped(double x, double y, double width, double height, double canvasHeight) =>
        new(x, canvasHeight - y - height, width, height);

    private static SolidColorBrush Brush(string hex)
    {
        var padded = hex.Length == 6 ? hex + "FF" : hex;
        var values = new List<byte>();
        for (var offset = 0; offset + 2 <= padded.Length && offset < 8; offset += 2)
        {
            if (byte.TryParse(padded.AsSpan(offset, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
                values.Add(value);
        }
        var color = values.Count == 4 ? Color.FromArgb(values[3], values[0], values[1], values[2]) : Colors.Black;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>Runs WPF drawing on an STA thread with its own dispatcher, inline when already on one.</summary>
    internal static T RunOnSta<T>(Func<T> body)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) return body();
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = body();
            }
            catch (Exception e)
            {
                failure = ExceptionDispatchInfo.Capture(e);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        failure?.Throw();
        return result;
    }
}
