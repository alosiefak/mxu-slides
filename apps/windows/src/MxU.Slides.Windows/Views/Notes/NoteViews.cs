using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Notes;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Rendering;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views.Notes;

/// <summary>Drawing a note's blocks and its passages' types, shared by the note windows.</summary>
internal static class NoteViews
{
    /// <summary>Each slide type's colour, as the Mac's note margin marks them.</summary>
    public static Brush TypeBrush(string? type) => new SolidColorBrush(type switch
    {
        "scripture" => Color.FromRgb(0x8B, 0x5C, 0xF6),
        "points" => Color.FromRgb(0x3B, 0x82, 0xF6),
        "quote" => Color.FromRgb(0xF5, 0x9E, 0x0B),
        "title" => Color.FromRgb(0xEC, 0x48, 0x99),
        "lyrics" => Color.FromRgb(0x10, 0xB9, 0x81),
        "picture" or "video" or "media" => Color.FromRgb(0x06, 0xB6, 0xD4),
        null or "body" => Color.FromRgb(0x94, 0xA3, 0xB8),
        _ => Color.FromRgb(0xA3, 0xE6, 0x35),
    });

    /// <summary>A block's words with their bold, italic, underline and strike, and a heading's size.</summary>
    public static TextBlock Text(NoteBlock block, double size = 14)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = size };
        void Runs(IEnumerable<NoteRun> runs)
        {
            foreach (var run in runs)
            {
                var inline = new Run(run.Text);
                if (run.Bold == true) inline.FontWeight = FontWeights.SemiBold;
                if (run.Italic == true) inline.FontStyle = FontStyles.Italic;
                var decorations = new TextDecorationCollection();
                if (run.Underline == true) decorations.Add(TextDecorations.Underline);
                if (run.Strike == true) decorations.Add(TextDecorations.Strikethrough);
                if (decorations.Count > 0) inline.TextDecorations = decorations;
                if (!string.IsNullOrEmpty(run.Href)) inline.Foreground = EditorKit.Res<Brush>("AccentHover");
                text.Inlines.Add(inline);
            }
        }
        switch (block.Type)
        {
            case NoteBlockType.Heading:
                text.FontSize = size + (block.Level switch { 1 => 10, 2 => 6, _ => 3 });
                text.FontWeight = FontWeights.SemiBold;
                Runs(block.Runs ?? []);
                break;
            case NoteBlockType.List or NoteBlockType.TaskList:
                var items = block.Items ?? [];
                for (var i = 0; i < items.Count; i++)
                {
                    if (i > 0) text.Inlines.Add(new LineBreak());
                    text.Inlines.Add(new Run(block.Ordered == true ? $"{i + 1}.  " : "•  ") { Foreground = EditorKit.Res<Brush>("MutedText") });
                    Runs(items[i].Runs);
                    foreach (var inner in items[i].Blocks ?? [])
                    {
                        text.Inlines.Add(new LineBreak());
                        text.Inlines.Add(new Run("    " + NoteReading.Text(inner)));
                    }
                }
                break;
            case NoteBlockType.Quote:
                text.FontStyle = FontStyles.Italic;
                text.Text = NoteReading.Text(block);
                break;
            case NoteBlockType.Rule:
                text.Text = "———";
                text.Foreground = EditorKit.Res<Brush>("FaintText");
                break;
            case NoteBlockType.Image:
                text.Text = "Picture" + (string.IsNullOrEmpty(block.Alt) ? "" : $": {block.Alt}");
                text.Foreground = EditorKit.Res<Brush>("MutedText");
                break;
            case NoteBlockType.Attachment:
                text.Text = (NoteMedia.IsVideo(block) ? "Video: " : "File: ") + NoteMedia.FileName(block);
                text.Foreground = EditorKit.Res<Brush>("MutedText");
                break;
            default:
                Runs(block.Runs ?? []);
                if (block.Runs is null) text.Text = NoteReading.Text(block);
                break;
        }
        return text;
    }

    /// <summary>A picture kept on this PC, scaled to a column; null for one on the web (never fetched here).</summary>
    public static Image? Picture(NoteBlock block, double width)
    {
        if (block.Type != NoteBlockType.Image || block.Url is not { } url || NoteMedia.Resolve(url) is not { IsFile: true } uri || !File.Exists(uri.LocalPath)) return null;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = (int)width;
            bitmap.UriSource = uri;
            bitmap.EndInit();
            bitmap.Freeze();
            var size = NoteMedia.Fitted((bitmap.PixelWidth, bitmap.PixelHeight), width);
            return new Image { Source = bitmap, Width = size.Width, Height = size.Height, HorizontalAlignment = HorizontalAlignment.Left };
        }
        catch (Exception e) when (e is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A slide's thumbnail.</summary>
    public static FrameworkElement Thumbnail(MainViewModel model, RenderScene? scene, double width)
    {
        if (scene is null)
            return new Border { Width = width, Height = width * 9 / 16, Background = Brushes.Black, Child = EditorKit.Faint("Slide gone") };
        return new Border
        {
            Width = width, Height = width * 9 / 16, BorderBrush = EditorKit.Res<Brush>("Border"), BorderThickness = new Thickness(1),
            Child = new SceneView { StaticScene = scene, PlaysVideo = false, Letterbox = Brushes.Black, Media = model.Media },
        };
    }
}
