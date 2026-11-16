using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MxU.Slides.Broadcast.Media;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Motion;
using MxU.Slides.Core.Openers;
using MxU.Slides.MotionTemplates.Lottie;
using MxU.Slides.MotionTemplates.Openers;
using MxU.Slides.Windows.ViewModels;
using MxU.Slides.Windows.Views;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>
/// The Phase 13 opener checks (Views/OpenerWindow.cs): full-size pictures of each kind in each
/// look; the Media bin's button opens the window, whose preview and stills move; one of each kind
/// is made, small and short (a bumper, a countdown, a montage of made-up photos cut to a made-up
/// click track, an announcement loop) and each lands in the Media bin as the right kind, the right
/// length, playable, the montage with its sound; a frame is read back out of each file; "Edit
/// opener…" opens the recipe and makes it again in place; a cancelled render leaves nothing.
/// Renders here are 480 × 270 and 5 to 15 seconds. Pictures are named "op-…".
/// </summary>
public sealed partial class SelfTest
{
    private async Task OpenersCheck()
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"MxU self-test openers {Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        try
        {
            var photos = OpenerPhotos(scratch);
            var music = Path.Combine(scratch, "clicks.wav");
            WriteClicks(music, seconds: 8, beatsPerMinute: 120);
            await OpenerLookPictures(photos);
            var bumper = await OpenerWindowCheck(photos, music);
            if (bumper is not null) await OpenerEditCheck(bumper);
            await OpenerCancelCheck();
        }
        finally
        {
            foreach (var open in Application.Current.Windows.OfType<OpenerWindow>().ToList()) open.Close();
            model.OpenerJob?.Cancel();
            model.ClearAllCommand.Execute(null);
            try { Directory.Delete(scratch, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // MARK: Made-up photos and music

    /// <summary>Three "photos" for the montage: two landscape and one portrait, each a soft scene with a caption, saved as JPEGs.</summary>
    private static List<string> OpenerPhotos(string scratch)
    {
        (string Name, int Width, int Height, Color Sky, Color Ground, string Caption)[] scenes =
        [
            ("camp-lake.jpg", 1600, 1000, Color.FromRgb(0x4F, 0x9D, 0xD9), Color.FromRgb(0x1F, 0x5E, 0x3A), "Lake day"),
            ("camp-fire.jpg", 1600, 1000, Color.FromRgb(0x2A, 0x1B, 0x4D), Color.FromRgb(0xD9, 0x6B, 0x1F), "Campfire"),
            ("camp-team.jpg", 900, 1400, Color.FromRgb(0xE8, 0xB8, 0x5C), Color.FromRgb(0x8A, 0x3B, 0x2E), "The team"),
        ];
        var paths = new List<string>();
        foreach (var (name, width, height, sky, ground, caption) in scenes)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new LinearGradientBrush(sky, ground, 90), null, new Rect(0, 0, width, height));
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(200, 255, 244, 214)), null, new Point(width * 0.7, height * 0.28), height * 0.09, height * 0.09);
                for (var i = 0; i < 5; i++)
                    dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), null, new Point(width * (0.15 + i * 0.18), height * 0.72), height * 0.07, height * 0.11);
                dc.DrawText(new FormattedText(caption, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), height * 0.06, Brushes.White, 1),
                    new Point(width * 0.06, height * 0.86));
            }
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.Combine(scratch, name);
            using (var file = File.Create(path)) encoder.Save(file);
            paths.Add(path);
        }
        return paths;
    }

    /// <summary>A click track as a 16-bit WAV: a short tick on every beat, the kind of music with the clearest beat there is.</summary>
    private static void WriteClicks(string path, double seconds, double beatsPerMinute)
    {
        const int rate = 44_100;
        var samples = new short[(int)(seconds * rate)];
        var beat = (int)(60 / beatsPerMinute * rate);
        for (var i = 0; i < samples.Length; i++)
        {
            var into = i % beat;
            samples[i] = (short)(22_000 * Math.Exp(-into / (rate * 0.008)) * Math.Sin(2 * Math.PI * 880 * into / rate));
        }
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);
        writer.Write(36 + samples.Length * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples.Length * 2);
        foreach (var sample in samples) writer.Write(sample);
    }

    // MARK: Full-size pictures to judge the looks by

    /// <summary>Each kind at 1920 × 1080 in a couple of looks, drawn straight from the renderer (no encoding), to see what a church screen would show.</summary>
    private async Task OpenerLookPictures(List<string> photos)
    {
        var fonts = model.MotionFonts;
        (string Name, OpenerRecipe Recipe, double? At)[] wanted =
        [
            ("op-02-bumper-clean", OpenerRecipe.For(OpenerKind.Bumper), null),
            ("op-02-bumper-serif", OpenerRecipe.For(OpenerKind.Bumper) with { Look = "serif" }, null),
            ("op-02-bumper-broadcast-picture", OpenerRecipe.For(OpenerKind.Bumper) with { Look = "broadcast", Picture = photos[0] }, null),
            ("op-03-countdown-bold", OpenerRecipe.For(OpenerKind.Countdown) with { Subtitle = "Grace Community Church" }, 12.4),
            ("op-03-countdown-minimal", OpenerRecipe.For(OpenerKind.Countdown) with { Look = "minimal" }, 61.5),
            ("op-03-countdown-welcome", OpenerRecipe.For(OpenerKind.Countdown) with { Seconds = 60 }, 62.5),
            ("op-04-montage-title", OpenerRecipe.For(OpenerKind.Montage) with { Title = "Summer Camp 2026", Subtitle = "A week to remember", Shots = photos }, 1.9),
            ("op-04-montage-landscape", OpenerRecipe.For(OpenerKind.Montage) with { Shots = photos }, 2.5),
            ("op-04-montage-dissolve", OpenerRecipe.For(OpenerKind.Montage) with { Shots = photos }, 4.0),
            ("op-04-montage-portrait", OpenerRecipe.For(OpenerKind.Montage) with { Shots = photos }, 10.0),
            ("op-05-loop-broadcast-picture", OpenerRecipe.For(OpenerKind.Announcements) with
            {
                Cards = [new("Small Groups", "New groups start this month.\nSign up at the welcome desk.", photos[1]), new("Youth Night", "Friday at 7 pm")],
            }, 3),
            ("op-05-loop-clean", OpenerRecipe.For(OpenerKind.Announcements) with { Look = "clean" }, 3),
            ("op-05-loop-changing-cards", OpenerRecipe.For(OpenerKind.Announcements), 7.25),
        ];
        var drawn = await Task.Factory.StartNew(() =>
        {
            using var crew = new RenderCrew("Self-test openers");
            using var target = new LottieFrame(1920, 1080);
            var pictures = new List<(string Name, byte[] Pixels)>();
            foreach (var (name, recipe, at) in wanted)
            {
                var plan = OpenerPlan.For(recipe);
                using var renderer = new OpenerRenderer(plan, 1920, 1080, crew, fonts);
                var seconds = at ?? plan.HeroSeconds;
                renderer.Render(plan.FrameAt(seconds), (long)(seconds * 30), target);
                pictures.Add((name, [.. target.Pixels]));
            }
            return pictures;
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        foreach (var (name, pixels) in drawn) Write(BitmapSource.Create(1920, 1080, 96, 96, PixelFormats.Bgr32, null, pixels, 1920 * 4), name);
        // Every one has something bright on it (words or a photo), and none is a flat colour.
        Check($"each kind of opener draws at full size ({drawn.Count} pictures)", drawn.Count == wanted.Length
            && drawn.All(d => d.Pixels.Where((_, i) => i % 4 == 1).Any(g => g > 200) && d.Pixels.Where((_, i) => i % 97 == 1).Distinct().Count() > 8));
    }

    // MARK: The window, and one of each kind

    private async Task<MediaItem?> OpenerWindowCheck(List<string> photos, string music)
    {
        model.Bin = BinTab.Media;
        await Delay(300);
        // The Media bin's own button, as an operator would click it.
        var button = Descendants<Button>(window).FirstOrDefault(b => b.Content as string == "Make an opener…");
        Check("the Media bin has a Make an opener… button", button is not null);
        button?.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Delay(400);
        var editor = Application.Current.Windows.OfType<OpenerWindow>().FirstOrDefault();
        Check("it opens the opener window", editor is not null);
        if (editor is null) return null;
        editor.SizeOverride = (480, 270);

        // The preview runs on its own thread, below normal priority: wait (up to 10 s) for it, then check it moves.
        var first = BitmapPixels(editor.Preview.Bitmap);
        for (var i = 0; i < 100 && (editor.Preview.FramesShown < 10 || editor.Preview.StillsShown < 1); i++) await Delay(100);
        // On a fast release build the frames may still be on a held moment: give the picture up to 2 s more to move.
        for (var i = 0; i < 20 && first.AsSpan().SequenceEqual(BitmapPixels(editor.Preview.Bitmap)); i++) await Delay(100);
        Check($"the live preview plays ({editor.Preview.FramesShown} frames)", editor.Preview.FramesShown >= 10 && !first.AsSpan().SequenceEqual(BitmapPixels(editor.Preview.Bitmap)));
        Check("each look has a still of this opener", editor.Preview.StillsShown >= 1
            && OpenerLooks.All.All(l => BitmapPixels(editor.Preview.Stills[l.Id]).Where((_, i) => i % 4 == 1).Any(g => g > 150)));

        // 1. A bumper.
        editor.Adjust(r => r with { Title = "Rooted", Subtitle = "Six weeks in Colossians", Seconds = 5 });
        editor.Preview.Seek(2.5);
        await OpenerSettle(editor);
        Save(editor, "op-10-window-bumper");
        Check("a bumper is 5 seconds, 150 frames", editor.Plan is { Seconds: 5, FrameCount: 150 } && editor.Summary.StartsWith("0:05", StringComparison.Ordinal));
        var before = model.Library.All<MediaItem>().Count();
        var making = editor.MakeAsync(replaceExisting: false);
        await Delay(500);
        Save(editor, "op-11-window-making");
        var bumper = await Within(making, TimeSpan.FromMinutes(3));
        Check("Make draws the bumper and adds it to the library", bumper is not null && model.Library.All<MediaItem>().Count() == before + 1);
        if (bumper is null) return null;
        bumper = model.Library.Get<MediaItem>(bumper.Id)!;
        Check("it's an ordinary video that plays once, in the Openers folder, named for its title",
            bumper is { MediaKind: MediaKind.Video, Classification: MediaClassification.Foreground, Loops: false, FileStatus: MediaFileStatus.Ready, PixelWidth: 480 }
            && bumper.Folder == OpenerRecipe.Folder && bumper.Name == "Rooted · Bumper" && Math.Abs((bumper.DurationSeconds ?? 0) - 5) < 0.05);
        Check("its recipe is kept with it (Windows extras), for Edit", model.OpenerRecipeOf(bumper) is { Kind: OpenerKind.Bumper, Title: "Rooted", Seconds: 5 });
        Check("the window says it's done", editor.Status.StartsWith("Done", StringComparison.Ordinal));
        Check("its tile is in the Media bin, as a Video", model.MediaItems.FirstOrDefault(m => m.Item.Id == bumper.Id) is { Kind: "Video" });
        OpenerFrame(bumper, 2.5, "op-20-bumper-frame");

        // It plays on the screens like any other video.
        model.FireMedia(bumper);
        var key = $"live:live-media-videos:{bumper.Id}";
        for (var i = 0; i < 150 && model.Media.Transport(key) is null && !model.Media.IsVideoFailed(key); i++) await Delay(100);
        Check("the bumper plays on the screens" + (model.Media.VideoFailureReason(key) is { } why ? $" (failed: {why})" : ""),
            model.Media.Transport(key) is { Duration.TotalSeconds: > 4.9 and < 5.1 });
        await Delay(2200);
        if (model.Outputs?[Outputs.OutputRole.Audience] is { } audience) Save(audience, "op-21-bumper-audience-screen");
        for (var i = 0; i < 50 && !editor.WarnsShowIsLive; i++) await Delay(100);
        Check("with something on the screens, the window warns before making another", model.ShowIsLive && editor.WarnsShowIsLive);
        Save(editor, "op-12-window-live-warning");
        model.ClearAllCommand.Execute(null);
        for (var i = 0; i < 50 && editor.WarnsShowIsLive; i++) await Delay(100);
        Check("…and stops warning once the screens are clear", !editor.WarnsShowIsLive);

        // 2. A countdown: ten seconds, then the Welcome card.
        editor.Pick(OpenerKind.Countdown);
        editor.Adjust(r => r with { Seconds = 10, Subtitle = "Grace Community Church" });
        editor.Preview.Seek(2.2);
        await OpenerSettle(editor);
        Save(editor, "op-13-window-countdown");
        Check("a ten-second countdown with a Welcome card is 15 seconds", editor.Plan.Seconds == 15 && editor.Plan.CountdownTextAt(2.5) == "0:08");
        var countdown = await Within(editor.MakeAsync(replaceExisting: false), TimeSpan.FromMinutes(3));
        Check("the countdown is made: 15 seconds, plays once", countdown is { Classification: MediaClassification.Foreground, Loops: false, FileStatus: MediaFileStatus.Ready, Name: "Countdown 0:10" }
            && Math.Abs((countdown.DurationSeconds ?? 0) - 15) < 0.05);
        if (countdown is not null)
        {
            OpenerFrame(countdown, 3.5, "op-22-countdown-frame");
            OpenerFrame(countdown, 12, "op-23-countdown-welcome-frame");
        }

        // 3. A montage: three photos cut to an eight-second click track.
        editor.Pick(OpenerKind.Montage);
        editor.AddShots(photos);
        editor.Adjust(r => r with { Music = music });
        for (var i = 0; i < 200 && (editor.FindingBeats || !editor.Plan.OnBeats); i++) await Delay(50);
        Check($"the montage is as long as its music and cut on its beat ({editor.Summary})", editor.Plan is { OnBeats: true, Shots.Count: 3 }
            && Math.Abs(editor.Plan.Seconds - 8) < 0.1 && editor.Summary.Contains("cut on the beat (120 a minute)", StringComparison.Ordinal));
        Check("every cut is on a beat (every half second)", editor.Plan.Shots.Skip(1).All(s => Math.Abs(s.Start * 2 - Math.Round(s.Start * 2)) < 0.04));
        editor.Preview.Seek(1.5);
        await OpenerSettle(editor);
        Save(editor, "op-14-window-montage");
        var montage = await Within(editor.MakeAsync(replaceExisting: false), TimeSpan.FromMinutes(3));
        Check("the montage is made: 8 seconds, plays once", montage is { Classification: MediaClassification.Foreground, Loops: false, FileStatus: MediaFileStatus.Ready, Name: "Montage" }
            && Math.Abs((montage.DurationSeconds ?? 0) - 8) < 0.15);
        if (montage is not null && model.Library.BlobPath(montage) is { } montageFile)
        {
            Check("the montage's video has a sound track (its music)", MediaImportProbe.ProbeVideo(montageFile).HasAudio);
            OpenerFrame(montage, 1.5, "op-24-montage-frame");
            OpenerFrame(montage, 6.6, "op-25-montage-portrait-frame");
        }

        // 4. An announcement loop: two cards, three seconds each, the second with a picture.
        editor.Pick(OpenerKind.Announcements);
        editor.Adjust(r => r with { CardSeconds = 3, Cards = [r.Cards[0], r.Cards[1] with { Picture = photos[0] }] });
        editor.Preview.Seek(4.2);
        await OpenerSettle(editor);
        Save(editor, "op-15-window-announcements");
        var loop = await Within(editor.MakeAsync(replaceExisting: false), TimeSpan.FromMinutes(3));
        Check("the announcement loop is made: 6 seconds, a looping background", loop is { Classification: MediaClassification.Background, Loops: true, FileStatus: MediaFileStatus.Ready, Name: "Announcements (2)" }
            && Math.Abs((loop.DurationSeconds ?? 0) - 6) < 0.05);
        Check("its tile is marked Loop", loop is not null && model.MediaItems.FirstOrDefault(m => m.Item.Id == loop.Id) is { Kind: "Loop" });
        if (loop is not null)
        {
            OpenerFrame(loop, 1.2, "op-26-loop-frame");
            OpenerFrame(loop, 4.2, "op-27-loop-picture-frame");
        }
        Check("all four are in the Openers folder", model.Library.All<MediaItem>().Count(m => m.Folder == OpenerRecipe.Folder) == 4);
        model.Bin = BinTab.Media;
        await Delay(600);
        Save(window, "op-16-media-bin");
        editor.Close();
        return bumper;
    }

    /// <summary>Waits for the preview to show the latest change (a few more pictures, and the stills redrawn).</summary>
    private static async Task OpenerSettle(OpenerWindow editor)
    {
        var (frames, stills) = (editor.Preview.FramesShown, editor.Preview.StillsShown);
        for (var i = 0; i < 80 && (editor.Preview.FramesShown < frames + 4 || editor.Preview.StillsShown <= stills); i++) await Delay(100);
    }

    /// <summary>Reads the picture at <paramref name="seconds"/> back out of a made video's file and saves it: what's really in the MP4.</summary>
    private void OpenerFrame(MediaItem item, double seconds, string name)
    {
        var ok = false;
        try
        {
            if (model.Library.BlobPath(item) is { } path)
            {
                using var reader = new VideoClipReader(path);
                if (reader.FrameAt(seconds, out var width, out var height) is { } pixels)
                {
                    Write(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4), name);
                    // Not a black frame: the words or a photo are in it.
                    ok = pixels.Where((_, i) => i % 4 == 1).Any(g => g > 160);
                }
            }
        }
        catch (IOException)
        {
        }
        Check($"a frame {seconds:0.#} s into “{item.Name}” reads back from its file", ok);
    }

    // MARK: Edit and make again

    private async Task OpenerEditCheck(MediaItem item)
    {
        var tile = model.MediaItems.FirstOrDefault(m => m.Item.Id == item.Id);
        var element = tile is null ? null : Descendants<FrameworkElement>(window).FirstOrDefault(e => e.ContextMenu is not null && e.DataContext == tile);
        var menuItem = element is null ? null : window.PrepareOpenerMenu(element);
        Check("its tile's right-click menu offers Edit opener…", menuItem is { Visibility: Visibility.Visible });
        var other = model.MediaItems.FirstOrDefault(m => m.Item.Folder != OpenerRecipe.Folder);
        var otherElement = other is null ? null : Descendants<FrameworkElement>(window).FirstOrDefault(e => e.ContextMenu is not null && e.DataContext == other);
        Check("…but other media doesn't", otherElement is null || window.PrepareOpenerMenu(otherElement) is { Visibility: Visibility.Collapsed });
        if (element?.ContextMenu is not { } menu || menuItem is null) return;

        menu.PlacementTarget = element;
        menu.IsOpen = true;
        await Delay(400);
        SaveElement(menu, "op-30-tile-menu");
        menuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.IsOpen = false;
        await Delay(900);
        var editor = Application.Current.Windows.OfType<OpenerWindow>().FirstOrDefault();
        Check("Edit opens the window on the opener's recipe", editor?.Recipe is { Kind: OpenerKind.Bumper, Title: "Rooted", Seconds: 5 }
            && editor.Title.Contains(item.Name, StringComparison.Ordinal));
        if (editor is null) return;
        // Next week's title, in another look.
        editor.Adjust(r => r with { Title = "Rooted: Week 2", Look = "serif" });
        editor.SizeOverride = (480, 270);
        editor.Preview.Seek(2.5);
        await OpenerSettle(editor);
        Save(editor, "op-31-edit-window");
        var oldHash = item.FileHash;
        var replaced = await Within(editor.MakeAsync(replaceExisting: true), TimeSpan.FromMinutes(3));
        var now = model.Library.Get<MediaItem>(item.Id);
        Check("Replace makes the same item again with the change", replaced?.Id == item.Id && now is not null && now.FileHash != oldHash
            && model.OpenerRecipeOf(now) is { Title: "Rooted: Week 2", Look: "serif" } && now.Name == item.Name);
        if (now is not null) OpenerFrame(now, 2.5, "op-32-replaced-frame");
        editor.Close();
    }

    // MARK: Cancel

    private async Task OpenerCancelCheck()
    {
        var editor = OpenerWindow.Open(window, model);
        editor.Pick(OpenerKind.Countdown);
        editor.Adjust(r => r with { Seconds = 600 });
        editor.SizeOverride = (480, 270);
        var before = model.Library.All<MediaItem>().Count();
        var making = editor.MakeAsync(replaceExisting: false);
        for (var i = 0; i < 200 && model.OpenerJob?.Latest is null; i++) await Delay(50);
        model.OpenerJob?.Cancel();
        var result = await Within(making, TimeSpan.FromMinutes(1));
        var leftovers = Directory.EnumerateFiles(OpenerVideoMaker.WorkFolder(model.Library.Root)).ToList();
        Check("Cancel stops a ten-minute countdown and leaves nothing behind", result is null && model.Library.All<MediaItem>().Count() == before
            && leftovers.Count == 0 && editor.Status.StartsWith("Stopped", StringComparison.Ordinal));
        editor.Close();
    }
}
