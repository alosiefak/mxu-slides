using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Outputs;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Outputs;
using MxU.Slides.Windows.Rendering;
using MxU.Slides.Windows.Views;
using AnimatedScene = MxU.Slides.Core.Animation.AnimatedScene;

namespace MxU.Slides.Windows.Infrastructure;

// Phase 14, the key feed: a screen that shows only words and overlays over black or green, so a
// video switcher (a Blackmagic ATEM) can lay them over the camera. Three screens on stand-in
// network outputs show the same moment: an ordinary one, a key feed over black and one over
// green. A song with a theme colour and a media background is live with a lower third up, and
// the pictures the outputs really sent are checked pixel by pixel (pictures 70-keyfeed-…).
public sealed partial class SelfTest
{
    private const string KeyFeedThemeId = "selftest-keyfeed-theme";

    /// <summary>One picture an output sent: its pixels (blue, green, red, alpha) and size.</summary>
    private sealed record KeyFrame(byte[] Pixels, int Width, int Height, bool FromOutput)
    {
        public (byte R, byte G, byte B) At(int x, int y)
        {
            var i = (y * Width + x) * 4;
            return (Pixels[i + 2], Pixels[i + 1], Pixels[i]);
        }

        /// <summary>Whether every pixel of a 40 × 24 block in each of the four corners is exactly this colour.</summary>
        public bool CornersAre(byte r, byte g, byte b)
        {
            foreach (var (x0, y0) in new[] { (0, 0), (Width - 40, 0), (0, Height - 24), (Width - 40, Height - 24) })
                for (var y = y0; y < y0 + 24; y++)
                    for (var x = x0; x < x0 + 40; x++)
                        if (At(x, y) != (r, g, b)) return false;
            return true;
        }

        /// <summary>How many pixels inside a rectangle (given in 0–1 of the picture) pass a test.</summary>
        public int Count(SceneRect unit, Func<(byte R, byte G, byte B), bool> test)
        {
            var count = 0;
            int x1 = (int)(Math.Clamp(unit.X + unit.Width, 0, 1) * Width), y1 = (int)(Math.Clamp(unit.Y + unit.Height, 0, 1) * Height);
            for (var y = (int)(Math.Clamp(unit.Y, 0, 1) * Height); y < y1; y++)
                for (var x = (int)(Math.Clamp(unit.X, 0, 1) * Width); x < x1; x++)
                    if (test(At(x, y))) count++;
            return count;
        }

        /// <summary>The share of the whole picture that is neither black nor pure green: something is drawn there.</summary>
        public double Drawn => Count(new SceneRect(0, 0, 1, 1), p => !(p.R < 12 && p.B < 12 && (p.G < 12 || p.G > 243))) / (double)(Width * Height);
    }

    private static bool IsWhite((byte R, byte G, byte B) p) => p is { R: > 200, G: > 200, B: > 200 };

    private async Task KeyFeedCheck()
    {
        if (model.Outputs is not { } outputs) return;
        var config = outputs.Config;
        var starter = model.Library.All<Theme>().FirstOrDefault(t =>
            t.Slides?.Any(s => s.Id.EndsWith(".lyricsLower", StringComparison.Ordinal)) == true);
        var namePlate = model.Overlays.FirstOrDefault(o => o.Name == "Name + Title");
        Check("a starter theme with a lower-third design and the Name + Title lower third are there", starter is not null && namePlate is not null);
        if (starter is null || namePlate is null) return;

        // A theme with a background colour of its own (deep violet), so the Slide layer's scene has one to drop.
        var theme = MxuJson.Clone(starter);
        theme.Id = KeyFeedThemeId;
        theme.Name = "Self-test key feed";
        theme.BackgroundColorHex = "#5B21B6";
        model.Library.Save(theme);
        var (worship, _) = await LooksBackdrops();

        Slide Lyric(string id, string text) => new()
        {
            Id = id, Name = "Verse", ThemeSlideName = "Lyrics",
            Objects = [new SlideObject { Id = $"{id}-lyrics", ObjectKind = SlideObjectKind.Text, Name = "Lyrics", Text = text }],
        };
        var song = new Presentation
        {
            Id = "selftest-keyfeed", Name = "Key feed test", PresentationKind = PresentationKind.Song, ThemeId = theme.Id,
            Slides =
            [
                Lyric("selftest-keyfeed-1", "Amazing grace\nhow sweet the sound"),
                Lyric("selftest-keyfeed-2", "That saved a wretch\nlike me"),
                Lyric("selftest-keyfeed-3", "I once was lost\nbut now am found"),
            ],
        };
        // Slide 2 has a background fill of its own (teal): the "-backdrop" piece on the Slide layer.
        song.Slides[1].BackgroundFill = new ObjectFill { FillKind = FillKind.Solid, ColorHex = "#0F766E" };
        // Slide 3 fades through white for 1.6 s: a key feed must never show that white.
        song.Slides[2].Transition = new Transition { TransitionKind = TransitionKind.FadeWhite, DurationSeconds = 1.6 };

        var sinks = new[] { new CountingSink(), new CountingSink(), new CountingSink() };
        var factory = outputs.DeviceSinkFactory;
        outputs.DeviceSinkFactory = device => device switch
        {
            "ndi:MxU key test ordinary" => sinks[0],
            "ndi:MxU key test black" => sinks[1],
            "ndi:MxU key test green" => sinks[2],
            _ => factory?.Invoke(device),
        };
        var ordinary = config.AddScreen("Key test: projector", 1280, 720)!;
        var black = config.AddScreen("Key test: ATEM luma", 1280, 720)!;
        var green = config.AddScreen("Key test: ATEM chroma", 1280, 720)!;
        try
        {
            config.AssignDevice(ordinary.Id, "ndi:MxU key test ordinary", "Key test ordinary");
            config.AssignDevice(black.Id, "ndi:MxU key test black", "Key test black");
            config.AssignDevice(green.Id, "ndi:MxU key test green", "Key test green");
            Check("a new screen is not a key feed", config.KeyFeed(black.Id) == KeyFeedMode.Off);
            config.SetKeyFeed(black.Id, KeyFeedMode.Black);
            outputs.Apply();

            // The green one is switched on in the Screens window, as the operator would.
            var screens = new ScreensWindow(outputs) { Owner = window };
            screens.Show();
            await Delay(500);
            screens.ScreenList.SelectedIndex = config.Screens.ToList().FindIndex(s => s.Id == green.Id);
            await Delay(300);
            Check("the Screens window offers Off, Over black and Over green", screens.KeyFeedCombo.Items.Count == 3 && screens.KeyFeedCombo.SelectedIndex == 0);
            screens.KeyFeedCombo.SelectedIndex = 2;
            await Delay(600);
            Check("choosing Over green makes the screen a green key feed", config.KeyFeed(green.Id) == KeyFeedMode.Green);
            Check("it is saved with the screen in this PC's settings, beside its look",
                model.Settings.Screens?.FirstOrDefault(s => s.Id == green.Id)?.KeyFeed == "green"
                && model.Settings.Screens.FirstOrDefault(s => s.Id == black.Id)?.KeyFeed == "black"
                && model.Settings.Screens.FirstOrDefault(s => s.Id == ordinary.Id)?.KeyFeed is null);
            Save(screens, "70-keyfeed-screens-window");
            screens.Close();

            // The moment: a media background, the song's first verse on the violet theme, and a lower third.
            model.FireMedia(worship);
            FireSlide(song, 0);
            model.ToggleOverlayCommand.Execute(namePlate);
            if (model.OverlayTextFields.FirstOrDefault(f => f.Name == "Name") is { } nameField) nameField.Text = "Pastor Ade Johnson";
            await Delay(2500);
            Save(window, "70-keyfeed-operator");
            Check("the operator's Screens panel marks the key feeds", model.ScreenRows().Count(r => r.Key != KeyFeedMode.Off) == 2);

            // What each screen's scene holds.
            var shownOrdinary = outputs.ShownScene(ordinary.Id);
            var shownBlack = outputs.ShownScene(black.Id);
            var shownGreen = outputs.ShownScene(green.Id);
            static IEnumerable<string> Visible(AnimatedScene? scene, Func<Core.Animation.AnimatedLayer, bool> which) =>
                scene?.Layers.Where(l => !l.IsHidden && which(l)).SelectMany(l => l.Items).Select(i => i.Id) ?? [];
            Check("the ordinary screen still has its media background and the theme's colour",
                Visible(shownOrdinary, l => l.Kind == LayerKind.LoopingVideos).Any() && shownOrdinary?.Background == ColorHex.Color("#5B21B6"));
            Check("the key feeds have no media layer showing", !Visible(shownBlack, l => !KeyFeeds.Shows(l.Kind)).Any() && !Visible(shownGreen, l => !KeyFeeds.Shows(l.Kind)).Any());
            Check("the key feeds sit on black and on pure green, not the theme's colour",
                shownBlack?.Background == SceneColor.Black && shownGreen?.Background == new SceneColor(0, 1, 0));
            Check("the key feeds keep the lyric and the lower third",
                LyricFrame(shownBlack, "Amazing grace") is not null && LyricFrame(shownGreen, "Amazing grace") is not null
                && Visible(shownBlack, l => l.Kind == LayerKind.Overlays).Any() && Visible(shownGreen, l => l.Kind == LayerKind.Overlays).Any());

            // The pictures the outputs sent.
            var a = await KeyFeedFrame(outputs, ordinary.Id, sinks[0], "70-keyfeed-1-ordinary");
            var b = await KeyFeedFrame(outputs, black.Id, sinks[1], "70-keyfeed-1-over-black");
            var c = await KeyFeedFrame(outputs, green.Id, sinks[2], "70-keyfeed-1-over-green");
            if (!(a.FromOutput && b.FromOutput && c.FromOutput))
                report.AppendLine("NOTE  this PC's graphics-card capture gave no frames: the key feed pictures are each screen's scene drawn offscreen, not what its output sent");
            var lyric = LyricFrame(shownBlack, "Amazing grace") is { } f && shownBlack is not null
                ? new SceneRect(f.X / shownBlack.CanvasSize.Width, f.Y / shownBlack.CanvasSize.Height, f.Width / shownBlack.CanvasSize.Width, f.Height / shownBlack.CanvasSize.Height)
                : new SceneRect(0, 0, 1, 1);
            Check("over black: every corner pixel is exactly black", b.CornersAre(0, 0, 0));
            Check("over green: every corner pixel is exactly pure green (0, 255, 0)", c.CornersAre(0, 255, 0));
            Check($"over black: the lyric's letters are drawn ({b.Count(lyric, IsWhite)} white pixels)", b.Count(lyric, IsWhite) > 500);
            Check($"over green: the lyric's letters are drawn ({c.Count(lyric, IsWhite)} white pixels)", c.Count(lyric, IsWhite) > 500);
            var lowerThird = new SceneRect(0, 0.66, 1, 0.34);
            bool NotKey((byte R, byte G, byte B) p) => !(p.R < 12 && p.B < 12 && (p.G < 12 || p.G > 243));
            Check($"the lower third is on both key feeds ({b.Count(lowerThird, NotKey)} and {c.Count(lowerThird, NotKey)} pixels in the bottom third)",
                b.Count(lowerThird, NotKey) > 2000 && c.Count(lowerThird, NotKey) > 2000);
            Check($"the ordinary screen still shows its background ({a.Drawn:P0} of it drawn; the key feeds {b.Drawn:P0} and {c.Drawn:P0})",
                a.Drawn > 0.6 && b.Drawn < 0.4 && c.Drawn < 0.4 && !a.CornersAre(0, 0, 0) && !a.CornersAre(0, 255, 0));

            // A slide with a background fill of its own: the ordinary screen is teal, the key feeds still key colour.
            model.ToggleOverlayCommand.Execute(namePlate);
            FireSlide(song, 1);
            await Delay(2000);
            a = await KeyFeedFrame(outputs, ordinary.Id, sinks[0], "70-keyfeed-2-fill-ordinary");
            b = await KeyFeedFrame(outputs, black.Id, sinks[1], "70-keyfeed-2-fill-over-black");
            c = await KeyFeedFrame(outputs, green.Id, sinks[2], "70-keyfeed-2-fill-over-green");
            var corner = a.At(8, 8);
            Check($"a slide's own background fill shows on the ordinary screen (corner {corner.R}, {corner.G}, {corner.B})",
                Math.Abs(corner.R - 0x0F) < 6 && Math.Abs(corner.G - 0x76) < 6 && Math.Abs(corner.B - 0x6E) < 6);
            Check("and is dropped from the key feeds: the lyric sits straight on the key colour",
                b.CornersAre(0, 0, 0) && c.CornersAre(0, 255, 0) && b.Count(new SceneRect(0, 0, 1, 1), IsWhite) > 500 && c.Count(new SceneRect(0, 0, 1, 1), IsWhite) > 500
                && shownBlack is not null && !Visible(outputs.ShownScene(black.Id), _ => true).Any(id => id.EndsWith("-backdrop", StringComparison.Ordinal)));

            // A fade through white: the ordinary screen goes white, the key feed's corner never leaves green.
            var changedAt = ShowState.HostClock();
            FireSlide(song, 2);
            var stayedGreen = true;
            var sampled = 0;
            double brightest = 0;
            var giveUp = DateTime.UtcNow.AddSeconds(8);
            while (sinks[2].LastTime < changedAt + 2.0 && DateTime.UtcNow < giveUp)
            {
                await Delay(40);
                if (sinks[2].Last is { } greenFrame && sinks[2].Width == 1280)
                {
                    sampled++;
                    stayedGreen &= new KeyFrame(greenFrame, 1280, 720, true).CornersAre(0, 255, 0);
                }
                if (sinks[0].Last is { } plain && sinks[0].Width == 1280) brightest = Math.Max(brightest, Brightness(plain, 1280, 0, 0, 1280, 720));
            }
            if (sampled > 5)
            {
                Check($"through a Fade White the green key feed's corners stay green ({sampled} pictures; the ordinary screen reached brightness {brightest:0})", stayedGreen);
            }
            else
            {
                Skip("the key feed through a Fade White", "this PC's graphics-card capture gave no frames");
            }
            await Delay(600);

            // With the screen's Look: the luma feed shows the lyric as a lower third, still over black.
            config.SetScreenLook(black.Id, new SlideThemeLook(starter.Id, null, starter.Slides!.First(s => s.Id.EndsWith(".lyricsLower", StringComparison.Ordinal)).Id));
            await Delay(800);
            FireSlide(song, 0);
            await Delay(2000);
            var looked = outputs.ShownScene(black.Id);
            b = await KeyFeedFrame(outputs, black.Id, sinks[1], "70-keyfeed-3-look-over-black");
            a = await KeyFeedFrame(outputs, ordinary.Id, sinks[0], "70-keyfeed-3-look-ordinary");
            Check("a key feed works with the screen's Look: the lyric is a lower third there, full screen on the ordinary screen",
                LyricFrame(looked, "Amazing grace") is { } low && looked is not null && low.Y / looked.CanvasSize.Height > 0.6
                && LyricFrame(outputs.ShownScene(ordinary.Id), "Amazing grace") is { } whole && whole.Y / 1080 < 0.5);
            Check("and it is still over black, edge to edge at the top", b.CornersAre(0, 0, 0) && b.Count(new SceneRect(0, 0, 1, 0.5), NotKey) == 0);
            config.SetScreenLook(black.Id, null);

            // Off again: the backgrounds are back.
            config.SetKeyFeed(black.Id, KeyFeedMode.Off);
            config.SetKeyFeed(green.Id, KeyFeedMode.Off);
            await Delay(1500);
            a = await KeyFeedFrame(outputs, ordinary.Id, sinks[0], "70-keyfeed-4-off-ordinary");
            b = await KeyFeedFrame(outputs, black.Id, sinks[1], "70-keyfeed-4-off-was-black");
            c = await KeyFeedFrame(outputs, green.Id, sinks[2], "70-keyfeed-4-off-was-green");
            Check("switched off, the screens show their backgrounds again",
                Visible(outputs.ShownScene(black.Id), l => l.Kind == LayerKind.LoopingVideos).Any()
                && Visible(outputs.ShownScene(green.Id), l => l.Kind == LayerKind.LoopingVideos).Any()
                && outputs.ShownScene(green.Id)?.Background == ColorHex.Color("#5B21B6"));
            Check($"and their pictures match the ordinary screen's ({a.Drawn:P0}, {b.Drawn:P0}, {c.Drawn:P0} drawn)",
                b.Drawn > 0.6 && c.Drawn > 0.6 && !c.CornersAre(0, 255, 0) && b.At(8, 8) == a.At(8, 8) && c.At(8, 8) == a.At(8, 8));
            Check("and the setting is gone from the saved screens", model.Settings.Screens?.All(s => s.KeyFeed is null) == true);
        }
        catch (Exception ex)
        {
            failures++;
            report.AppendLine($"FAIL  the key feed checks crashed: {ex}");
        }
        finally
        {
            model.ClearAllCommand.Execute(null);
            config.RemoveScreen(ordinary.Id);
            config.RemoveScreen(black.Id);
            config.RemoveScreen(green.Id);
            outputs.Apply();
            outputs.DeviceSinkFactory = factory;
            model.Library.Delete<Theme>(KeyFeedThemeId);
        }
    }

    /// <summary>
    /// The newest picture a screen's output sent (waiting for one drawn from now on), saved as a
    /// PNG. On a PC whose graphics-card capture gives no frames, the screen's scene drawn offscreen instead.
    /// </summary>
    private async Task<KeyFrame> KeyFeedFrame(OutputManager outputs, Guid screenId, CountingSink sink, string name)
    {
        var from = ShowState.HostClock();
        var giveUp = DateTime.UtcNow.AddSeconds(4);
        while (sink.LastTime < from && DateTime.UtcNow < giveUp) await Delay(40);
        if (sink.Last is { } pixels && sink.Width > 0 && sink.LastTime >= from)
        {
            Write(BitmapSource.Create(sink.Width, sink.Height, 96, 96, PixelFormats.Pbgra32, null, pixels, sink.Width * 4), name);
            return new KeyFrame(pixels, sink.Width, sink.Height, FromOutput: true);
        }
        var fill = KeyFeeds.Fill(outputs.Config.KeyFeed(screenId));
        var view = new SceneView { Media = model.Media, PlaysVideo = false, Width = 1280, Height = 720, Letterbox = Painting.Brush(fill ?? SceneColor.Black) };
        view.Measure(new Size(1280, 720));
        view.Arrange(new Rect(0, 0, 1280, 720));
        view.Present(outputs.ShownScene(screenId), SceneTransition.Cut);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        var bitmap = new RenderTargetBitmap(1280, 720, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        Write(bitmap, name);
        var drawn = new byte[1280 * 720 * 4];
        bitmap.CopyPixels(drawn, 1280 * 4, 0);
        return new KeyFrame(drawn, 1280, 720, FromOutput: false);
    }
}
