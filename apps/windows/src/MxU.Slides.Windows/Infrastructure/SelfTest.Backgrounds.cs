using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Motion;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Rendering;
using MxU.Slides.Windows.ViewModels;
using MxU.Slides.Windows.Views;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>
/// The Phase 6 motion-background checks (Views/MotionBackgroundWindow.cs): a picture of every
/// style; the Media bin's button opens the window, whose gallery and preview move; a small, quick
/// render lands in the Media bin as a looping background with its recipe, plays live behind a
/// lyric, and its tile's menu offers "Edit motion background…", which opens the recipe and
/// replaces the video in place; a cancelled render leaves nothing. Renders here are 480 × 270, 10
/// seconds, so the whole check takes well under a minute. Pictures are named "bg-…".
/// </summary>
public sealed partial class SelfTest
{
    private async Task MotionBackgroundsCheck()
    {
        var opened = new List<Window>();
        try
        {
            await MotionStylePictures();
            var made = await MotionWindowCheck(opened);
            if (made is not null) await MotionEditCheck(made, opened);
            await MotionCancelCheck(opened);
        }
        finally
        {
            foreach (var open in Application.Current.Windows.OfType<MotionBackgroundWindow>().ToList()) open.Close();
            model.MotionJob?.Cancel();
            model.ClearAllCommand.Execute(null);
        }
    }

    // MARK: Every style

    /// <summary>Each style in its own palette at 1280 × 720 (two moments), and one sheet of all of them.</summary>
    private async Task MotionStylePictures()
    {
        const int width = 1280, height = 720, sheetColumns = 4;
        var frames = await Task.Factory.StartNew(() =>
        {
            using var crew = new RenderCrew("Self-test motion");
            var drawn = new List<(MotionStyle Style, double Phase, byte[] Pixels)>();
            foreach (var style in MotionStyles.All)
            {
                var renderer = new MotionRenderer(MotionRecipe.For(style), width, height, crew);
                foreach (var phase in new[] { 0.15, 0.6 })
                {
                    var pixels = new byte[renderer.FrameBytes(MotionPixelFormat.Bgra32)];
                    renderer.Render(phase, (long)(phase * renderer.FrameCount), MotionPixelFormat.Bgra32, pixels);
                    drawn.Add((style, phase, pixels));
                }
            }
            return drawn;
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        foreach (var (style, phase, pixels) in frames)
            Write(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4), $"bg-02-{style.Id}-{phase:0.00}");
        Check($"all {MotionStyles.All.Count} motion background styles draw", frames.Count == MotionStyles.All.Count * 2
            && frames.All(f => f.Pixels.Where((_, i) => i % 4 == 1).Any(g => g > 24)));

        // The sheet: every style's first moment, small, with its name.
        var rows = (MotionStyles.All.Count + sheetColumns - 1) / sheetColumns;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, sheetColumns * 480, rows * 300));
            var index = 0;
            foreach (var (style, _, pixels) in frames.Where(f => f.Phase < 0.5))
            {
                var x = index % sheetColumns * 480;
                var y = index / sheetColumns * 300;
                dc.DrawImage(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4), new Rect(x + 4, y + 4, 472, 266));
                dc.DrawText(new FormattedText(style.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 15, Brushes.White, 1),
                    new Point(x + 8, y + 274));
                index++;
            }
        }
        var sheet = new RenderTargetBitmap(sheetColumns * 480, rows * 300, 96, 96, PixelFormats.Pbgra32);
        sheet.Render(visual);
        Write(sheet, "bg-01-all-styles");
    }

    // MARK: The window and a render

    private async Task<MediaItem?> MotionWindowCheck(List<Window> opened)
    {
        model.Bin = BinTab.Media;
        await Delay(300);
        // The Media bin's own button, as an operator would click it.
        var button = Descendants<Button>(window).FirstOrDefault(b => b.Content as string == "Motion background…");
        Check("the Media bin has a Motion background… button", button is not null);
        button?.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Delay(400);
        var editor = Application.Current.Windows.OfType<MotionBackgroundWindow>().FirstOrDefault();
        Check("it opens the motion background window", editor is not null);
        if (editor is null) return null;
        opened.Add(editor);

        // The previews run on their own thread: give them a moment, then check they move.
        var first = BitmapPixels(editor.Preview.Bitmap);
        await Delay(1500);
        Check($"the live preview runs ({editor.Preview.FramesShown} frames in 1.5 s)", editor.Preview.FramesShown >= 10
            && !first.AsSpan().SequenceEqual(BitmapPixels(editor.Preview.Bitmap)));
        Check("every style in the gallery has a moving preview", MotionStyles.All.All(s => editor.Tile(s.Id).FramesShown >= 3));
        Save(editor, "bg-10-window");

        editor.PickStyle("light-rays");
        await Delay(900);
        Check("picking a style shows it in its own palette", editor.Recipe is { Style: "light-rays", Palette: "Easter" });
        Save(editor, "bg-11-window-light-rays");

        editor.PickStyle("waves");
        editor.UseColours(["#0B1D3A", "#E11D48"]);
        await Delay(900);
        Check("two custom colours make a custom palette", editor.Recipe is { Palette: null, Colors.Count: 2 } && editor.Recipe.Name is null
            && editor.Recipe.SuggestedName() == "Gentle Waves · Custom colours");
        Save(editor, "bg-12-window-custom-colours");

        editor.Adjust(r => r with { Height = 2160, FrameRate = 60, Seconds = 30 });
        Check("4K at 60 fps can be chosen (1800 frames for 30 s)", editor.Recipe is { Width: 3840, Height: 2160, FrameRate: 60, FrameCount: 1800 });

        // A quick real render: small, 10 seconds.
        editor.PickPalette("Northern Lights");
        editor.PickStyle("aurora");
        editor.Adjust(r => r with { Height = 1080, FrameRate = 30, Seconds = 10, Speed = 1.2, Intensity = 0.7 });
        editor.SizeOverride = (480, 270);
        var before = model.Library.All<MediaItem>().Count();
        var started = DateTime.UtcNow;
        var rendering = editor.RenderAsync(replaceExisting: false);
        await Delay(700);
        Save(editor, "bg-13-window-rendering");
        var item = await Within(rendering, TimeSpan.FromMinutes(3));
        report.AppendLine($"      quick render (480 × 270, 300 frames): {(DateTime.UtcNow - started).TotalSeconds:0.0} s");
        Check("Render makes the video and adds it to the library", item is not null && model.Library.All<MediaItem>().Count() == before + 1);
        if (item is null) return null;
        item = model.Library.Get<MediaItem>(item.Id)!;
        Check("it's an ordinary looping background video, ready to play",
            item is { MediaKind: MediaKind.Video, Classification: MediaClassification.Background, Loops: true, FileStatus: MediaFileStatus.Ready, PixelWidth: 480 }
            && Math.Abs((item.DurationSeconds ?? 0) - 10) < 0.05);
        Check("it's in the Motion Backgrounds folder, named for its style and colours",
            item.Folder == "Motion Backgrounds" && item.Name == "Aurora · Northern Lights");
        Check("its recipe is kept with it (Windows extras), for Edit", model.MotionRecipeOf(item) is { Style: "aurora", Palette: "Northern Lights", Seconds: 10, Speed: 1.2 });
        Check("the window says it's done", editor.Status.StartsWith("Done", StringComparison.Ordinal));
        var tile = model.MediaItems.FirstOrDefault(m => m.Item.Id == item.Id);
        Check("its tile is in the Media bin, marked Loop", tile is { Kind: "Loop" });
        Save(editor, "bg-14-window-done");

        for (var i = 0; i < 50 && model.Media.Poster(item.Id) is null; i++) await Delay(100);
        Check("its first frame becomes the tile's picture", model.Media.Poster(item.Id) is { PixelWidth: > 0 });

        // Live, behind a lyric.
        var song = model.ServiceItems.FirstOrDefault(r => r.Name == "Amazing Grace");
        model.SelectedServiceItem = song;
        await Delay(300);
        if (model.Slides.Count > 1) model.GoLiveCommand.Execute(model.Slides[1]);
        model.FireMedia(item);
        var key = $"live:live-media-{LayerKind.LoopingVideos.Raw()}:{item.Id}";
        for (var i = 0; i < 100 && model.Media.Transport(key) is null && !model.Media.IsVideoFailed(key); i++) await Delay(100);
        Check("it plays live behind the lyrics, looping" + (model.Media.VideoFailureReason(key) is { } why ? $" (failed: {why})" : ""),
            model.Show.LiveMedia.TryGetValue(LayerKind.LoopingVideos, out var cue) && cue.MediaId == item.Id
            && model.Media.Transport(key) is { Playing: true });
        await Delay(1200);
        Save(window, "bg-20-live-behind-lyrics");
        return item;
    }

    // MARK: Edit and replace

    private async Task MotionEditCheck(MediaItem item, List<Window> opened)
    {
        foreach (var open in Application.Current.Windows.OfType<MotionBackgroundWindow>().ToList()) open.Close();
        var tile = model.MediaItems.FirstOrDefault(m => m.Item.Id == item.Id);
        var element = tile is null ? null : Descendants<FrameworkElement>(window).FirstOrDefault(e => e.ContextMenu is not null && e.DataContext == tile);
        var menuItem = element is null ? null : window.PrepareMotionMenu(element);
        Check("its tile's right-click menu offers Edit motion background…", menuItem is { Visibility: Visibility.Visible });
        var other = model.MediaItems.FirstOrDefault(m => m.Item.Id != item.Id && m.Item.MediaKind == MediaKind.Image);
        var otherElement = other is null ? null : Descendants<FrameworkElement>(window).FirstOrDefault(e => e.ContextMenu is not null && e.DataContext == other);
        Check("…but a picture's menu doesn't", otherElement is null || window.PrepareMotionMenu(otherElement) is { Visibility: Visibility.Collapsed });
        if (element?.ContextMenu is not { } menu || menuItem is null) return;

        menu.PlacementTarget = element;
        menu.IsOpen = true;
        await Delay(400);
        SaveElement(menu, "bg-30-tile-menu");
        menuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.IsOpen = false;
        await Delay(900);
        var editor = Application.Current.Windows.OfType<MotionBackgroundWindow>().FirstOrDefault();
        Check("Edit opens the window on the background's recipe", editor?.Recipe is { Style: "aurora", Palette: "Northern Lights", Seconds: 10 }
            && editor.Title.Contains(item.Name, StringComparison.Ordinal));
        if (editor is null) return;
        opened.Add(editor);
        editor.PickPalette("Pentecost");
        editor.SizeOverride = (480, 270);
        await Delay(800);
        Save(editor, "bg-31-edit-window");
        var oldHash = item.FileHash;
        var replaced = await Within(editor.RenderAsync(replaceExisting: true), TimeSpan.FromMinutes(3));
        var now = model.Library.Get<MediaItem>(item.Id);
        Check("Replace gives the same item the new video", replaced?.Id == item.Id && now is not null && now.FileHash != oldHash
            && model.MotionRecipeOf(now) is { Palette: "Pentecost" });
        Check("…and what's live shows the new version", model.Show.LiveMedia.TryGetValue(LayerKind.LoopingVideos, out var cue) && cue.MediaId == item.Id);
        await Delay(1500);
        Save(window, "bg-32-replaced-live");
        editor.Close();
    }

    // MARK: Cancel

    private async Task MotionCancelCheck(List<Window> opened)
    {
        var editor = MotionBackgroundWindow.Open(window, model);
        opened.Add(editor);
        editor.PickStyle("bokeh");
        editor.Adjust(r => r with { Seconds = 30 });
        editor.SizeOverride = (480, 270);
        var before = model.Library.All<MediaItem>().Count();
        var rendering = editor.RenderAsync(replaceExisting: false);
        for (var i = 0; i < 100 && model.MotionJob?.Latest is null; i++) await Delay(50);
        model.MotionJob?.Cancel();
        var result = await Within(rendering, TimeSpan.FromMinutes(1));
        var leftovers = Directory.EnumerateFiles(Broadcast.Library.MotionBackgroundMaker.WorkFolder(model.Library.Root)).ToList();
        Check("Cancel stops a render and leaves nothing behind", result is null && model.Library.All<MediaItem>().Count() == before
            && leftovers.Count == 0 && editor.Status.StartsWith("Stopped", StringComparison.Ordinal));
        editor.Close();
    }

    // MARK: Helpers

    private static async Task<T?> Within<T>(Task<T?> task, TimeSpan limit) where T : class =>
        await Task.WhenAny(task, Task.Delay(limit)) == task ? await task : null;

    private static byte[] BitmapPixels(WriteableBitmap bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }
}
