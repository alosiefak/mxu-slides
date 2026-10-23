using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MxU.Slides.Core.Library;

namespace MxU.Slides.PptxImport.Tests;

/// <summary>A temp folder deleted when the test ends, like the Mac tests' <c>defer removeItem</c>.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder(string prefix = "pptx-test")
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string relative) => System.IO.Path.Combine(Path, relative.Replace('/', '\\'));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal static class TestPackages
{
    public const string PNs = "xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"";
    public const string ANs = "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"";
    public const string RNs = "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";
    public const string RelsNs = "xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"";
    public const string RelType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    /// <summary>Writes parts (forward-slash paths) under <paramref name="root"/>, as UTF-8 without a BOM.</summary>
    public static void Stage(string root, IEnumerable<KeyValuePair<string, string>> parts)
    {
        foreach (var (path, contents) in parts)
        {
            var file = Path.Combine(root, path.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, Encoding.UTF8.GetBytes(contents));
        }
    }

    public static void Zip(string archivePath, IEnumerable<KeyValuePair<string, byte[]>> entries)
    {
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        foreach (var (name, bytes) in entries)
        {
            using var stream = archive.CreateEntry(name).Open();
            stream.Write(bytes);
        }
    }

    public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    public static LibraryStore Library(TempFolder temp)
    {
        var library = new LibraryStore(temp.File("Library"));
        library.Load();
        return library;
    }

    /// <summary>A small solid PNG, encoded with WPF on an STA thread.</summary>
    public static byte[] Png(Color color, int size = 4) => TextureBaker.RunOnSta(() =>
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
            context.DrawRectangle(new SolidColorBrush(color), null, new Rect(0, 0, size, size));
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    });

    /// <summary>Decodes an image to BGRA bytes, row by row from the top.</summary>
    public static byte[] Bgra(string path, out int width, out int height)
    {
        var (pixels, w, h) = TextureBaker.RunOnSta(() =>
        {
            using var stream = File.OpenRead(path);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var bytes = new byte[converted.PixelWidth * converted.PixelHeight * 4];
            converted.CopyPixels(bytes, converted.PixelWidth * 4, 0);
            return (bytes, converted.PixelWidth, converted.PixelHeight);
        });
        width = w;
        height = h;
        return pixels;
    }
}
