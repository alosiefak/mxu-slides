using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using MxU.Slides.Core.Editing;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>
/// The Windows clipboard, for slides and slide objects. They go on it in a format of our own
/// (<see cref="EditorClipboard.SlidesFormat"/>: the JSON of the slides, as UTF-8), and as plain
/// text too, so pasting into Word or an email gets the words. Pasting reads ours first, then files,
/// a picture, and plain text. The rules for what a paste does are in Core/Editing/EditorClipboard.cs.
/// <para>
/// Another program can hold the clipboard open for a moment, which makes Windows refuse it; each
/// call tries a few times, briefly, and gives up quietly. Call on the UI thread.
/// </para>
/// </summary>
public static class AppClipboard
{
    public static void Put(SlideClip clip) =>
        Put(EditorClipboard.SlidesFormat, EditorClipboard.Encode(clip), EditorClipboard.PlainText(clip.Slides));

    public static void Put(ObjectClip clip) =>
        Put(EditorClipboard.ObjectsFormat, EditorClipboard.Encode(clip), EditorClipboard.PlainText(clip.Objects));

    private static void Put(string format, string json, string text)
    {
        var data = new DataObject();
        data.SetData(format, new MemoryStream(Encoding.UTF8.GetBytes(json)));
        if (text.Length > 0) data.SetText(text);
        // copy: true leaves it on the clipboard after the app quits, as other apps do.
        Try(() => Clipboard.SetDataObject(data, copy: true));
    }

    public static SlideClip? Slides() => EditorClipboard.DecodeSlides(Read(EditorClipboard.SlidesFormat));

    public static ObjectClip? Objects() => EditorClipboard.DecodeObjects(Read(EditorClipboard.ObjectsFormat));

    /// <summary>Files copied in Explorer.</summary>
    public static IReadOnlyList<string> Files() =>
        Try(() => Clipboard.ContainsFileDropList() ? Clipboard.GetFileDropList().Cast<string>().ToList() : []) ?? [];

    /// <summary>A picture copied in another app (a screenshot, a browser image).</summary>
    public static BitmapSource? Image() => Try(() => Clipboard.ContainsImage() ? Clipboard.GetImage() : null);

    public static string? Text() => Try(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);

    /// <summary>Something a paste could use is on the clipboard.</summary>
    public static bool HasAnything() =>
        Try(() => Clipboard.ContainsData(EditorClipboard.SlidesFormat) || Clipboard.ContainsData(EditorClipboard.ObjectsFormat)
            || Clipboard.ContainsFileDropList() || Clipboard.ContainsImage() || Clipboard.ContainsText());

    /// <summary>Saves a picture as a PNG in a fresh temporary folder, to import it.</summary>
    public static string? SaveImage(BitmapSource image)
    {
        try
        {
            var folder = Path.Combine(Path.GetTempPath(), "MxU Pasted Images", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "Pasted Image.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var file = File.Create(path);
            encoder.Save(file);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Log.Warn($"Couldn't keep the pasted picture: {ex.Message}");
            return null;
        }
    }

    private static string? Read(string format) => Try(() =>
    {
        if (!Clipboard.ContainsData(format)) return null;
        return Clipboard.GetData(format) switch
        {
            string text => text,
            MemoryStream stream => Encoding.UTF8.GetString(stream.ToArray()).TrimEnd('\0'),
            _ => null,
        };
    });

    private static void Try(Action action) => Try(() =>
    {
        action();
        return true;
    });

    private static T? Try<T>(Func<T> read)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return read();
            }
            catch (ExternalException ex)
            {
                // Held open by another program: a moment later it's usually free.
                if (attempt >= 3)
                {
                    Log.Warn($"The clipboard is busy: {ex.Message}");
                    return default;
                }
                Thread.Sleep(15);
            }
        }
    }
}
