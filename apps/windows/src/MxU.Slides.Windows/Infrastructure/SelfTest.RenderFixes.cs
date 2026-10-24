using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Outputs;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Outputs;
using MxU.Slides.Windows.Rendering;
using AnimatedItem = MxU.Slides.Core.Animation.AnimatedItem;
using AnimatedScene = MxU.Slides.Core.Animation.AnimatedScene;
using AnimationContext = MxU.Slides.Core.Animation.AnimationContext;

namespace MxU.Slides.Windows.Infrastructure;

// Rendering correctness (Phase 5): Mac and ProPresenter documents drawn as the Mac draws them.
// A deck mixing themes (and a Blank slide), the clipped-text badge, tickers and text on a path,
// keystone, a font saved in the library, a live picture inside a slide object (a screen mirror),
// and a trimmed video played at double speed. Pictures are numbered 50-…
public sealed partial class SelfTest
{
    private async Task RenderFixesCheck()
    {
        try
        {
            await PerSlideThemeCheck();
            await ClippedTextCheck();
            await PathTextCheck();
            await KeystoneCheck();
            await LibraryFontCheck();
            await LivePictureFillCheck();
            await VideoTrimCheck();
        }
        finally
        {
            model.ClearAllCommand.Execute(null);
        }
    }

    private static SlideObject TextBox(string id, string text, string name = "Lyrics") =>
        new() { Id = id, ObjectKind = SlideObjectKind.Text, Name = name, Text = text };

    /// <summary>The font a scene's first slide-layer text is drawn in.</summary>
    private static string? FontOf(RenderScene scene) =>
        scene[LayerKind.Slide].Items.Select(i => i.Content).OfType<ItemContent.Text>().FirstOrDefault()?.Styled.FontName;

    /// <summary>A scene drawn offscreen (with its pose, keystone and tickers), saved, and its pixels returned (BGRA).</summary>
    private async Task<byte[]> Picture(string name, AnimatedScene scene, int width = 1280, int height = 720, bool playsVideo = false)
    {
        var view = new SceneView { Media = model.Media, PlaysVideo = playsVideo, Width = width, Height = height };
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.Present(scene, SceneTransition.Cut);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        Write(bitmap, name);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    /// <summary>The scene frozen <paramref name="seconds"/> after each item's anchor (a ticker's start), so moving things can be pictured.</summary>
    private static AnimatedScene FrozenAt(AnimatedScene scene, double seconds)
    {
        var frozen = scene.Copy();
        foreach (var layer in frozen.Layers)
            for (var i = 0; i < layer.Items.Count; i++)
            {
                var item = layer.Items[i];
                var anchor = item.TickerAnchorHostTime ?? item.AnimationContext?.AnchorHostTime ?? 0;
                var context = item.AnimationContext ?? new AnimationContext(anchor);
                layer.Items[i] = item with { TickerAnchorHostTime = anchor, AnimationContext = context with { FrozenHostTime = anchor + seconds } };
            }
        return frozen;
    }

    private static bool SameRegion(byte[] a, byte[] b, int width, int x0, int y0, int x1, int y1)
    {
        for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
            {
                var i = (y * width + x) * 4;
                if (Math.Abs(a[i] - b[i]) > 8 || Math.Abs(a[i + 1] - b[i + 1]) > 8 || Math.Abs(a[i + 2] - b[i + 2]) > 8) return false;
            }
        return true;
    }

    /// <summary>
    /// A deck on one starter theme with a slide on another theme and a Blank slide: each is drawn with
    /// its own theme in the grid and on the outputs, and the Blank one with none.
    /// </summary>
    private async Task PerSlideThemeCheck()
    {
        var themes = model.Library.All<Theme>().Where(t => t.Slides is { Count: > 0 }).ToList();
        var deckTheme = themes.FirstOrDefault();
        var slideTheme = themes.FirstOrDefault(t => deckTheme is not null && t.Id != deckTheme.Id && FontOf(Sample(t)) != FontOf(Sample(deckTheme)));
        Check("two starter themes with different fonts to mix", deckTheme is not null && slideTheme is not null);
        if (deckTheme is null || slideTheme is null) return;

        var deck = new Presentation
        {
            Id = "selftest-mixed-themes", Name = "Mixed themes", PresentationKind = PresentationKind.Deck, ThemeId = deckTheme.Id,
            Slides =
            [
                new Slide { Id = "mixed-1", Name = "", Objects = [TextBox("mixed-1-t", "This slide follows the deck's theme")] },
                new Slide { Id = "mixed-2", Name = "", ThemeId = slideTheme.Id, Objects = [TextBox("mixed-2-t", "This slide follows its own theme")] },
                new Slide { Id = "mixed-3", Name = "", Unthemed = true, Objects = [TextBox("mixed-3-t", "A Blank slide follows no theme")] },
            ],
        };
        model.Library.Save(deck);
        await Step("50-mixed-themes-grid", () => model.Open(deck, null, null), settle: 900);
        Check("the grid draws the deck's slide with the deck's theme", model.Slides.Count == 3 && FontOf(model.Slides[0].Scene) == FontOf(Sample(deckTheme)));
        Check("the grid draws a slide with its own theme in that theme", FontOf(model.Slides[1].Scene) == FontOf(Sample(slideTheme)));
        Check("the grid draws a Blank slide with no theme", FontOf(model.Slides[2].Scene) == "HelveticaNeue-Bold" && model.Slides[2].Scene.Background == SceneColor.Black);

        model.GoLiveCommand.Execute(model.Slides[1]);
        await Delay(1200);
        Check("a slide with its own theme goes live in that theme", model.Show.Live?.Theme?.Id == slideTheme.Id
            && FontOf(model.Show.Scene()) == FontOf(Sample(slideTheme)));
        await Picture("50-own-theme-live", model.Show.AnimatedScene());
        model.GoLiveCommand.Execute(model.Slides[2]);
        await Delay(900);
        Check("a Blank slide goes live with no theme", model.Live?.Occurrence == 2 && model.Show.Live?.Theme is null);
        await Picture("50-blank-slide-live", model.Show.AnimatedScene());
        model.ClearAllCommand.Execute(null);

        RenderScene Sample(Theme theme) => SlideSceneBuilder.Scene(new Slide { Id = "s", Name = "", Objects = [TextBox("t", "Sample")] }, theme);
    }

    /// <summary>
    /// The clipped-text warning: the Mac's overflow rule measured with real text, then a deck whose
    /// first slide's words don't fit their box shows the orange badge on its tile, and the second doesn't.
    /// </summary>
    private async Task ClippedTextCheck()
    {
        var verse = string.Join("\n", Enumerable.Repeat("Amazing grace how sweet the sound", 12));
        var frame = new SceneSize(800, 200);
        Check("long words in a short box overflow; a short word doesn't",
            TextLayout.OverflowsFrame(new StyledText { String = verse, FontSize = 96 }, frame)
            && !TextLayout.OverflowsFrame(new StyledText { String = "Hi", FontSize = 40 }, frame));
        Check("text that shrinks to fit isn't clipped; one whose smallest size can't fit is",
            !TextLayout.OverflowsFrame(new StyledText { String = verse, FontSize = 96, AutoShrink = true, MinFontSize = 4 }, frame)
            && TextLayout.OverflowsFrame(new StyledText { String = verse, FontSize = 96, AutoShrink = true, MinFontSize = 90 }, frame));
        Check("a keep-lines-whole floor that wraps but fits the height isn't clipped",
            !TextLayout.OverflowsFrame(new StyledText
            {
                String = string.Join(" ", Enumerable.Repeat("word", 40)), FontSize = 100, AutoShrink = true, MinFontSize = 90, KeepLinesWhole = true,
            }, new SceneSize(800, 4000)));

        var big = TextBox("clip-1-t", verse);
        (big.X, big.Y, big.Width, big.Height) = (100, 100, 900, 200);
        var fits = TextBox("clip-2-t", "Fits");
        (fits.X, fits.Y, fits.Width, fits.Height) = (100, 100, 900, 400);
        var deck = new Presentation
        {
            Id = "selftest-clipped", Name = "Clipped text", PresentationKind = PresentationKind.Deck, ThemeId = "",
            Slides = [new Slide { Id = "clip-1", Name = "", Objects = [big] }, new Slide { Id = "clip-2", Name = "", Objects = [fits] }],
        };
        model.Library.Save(deck);
        await Step("51-clipped-text-badge", () => model.Open(deck, null, null), settle: 1200);
        Check("a slide whose text doesn't fit shows the warning on its tile", model.Slides.Count == 2 && model.Slides[0].HasClippedText);
        Check("a slide whose text fits shows no warning", model.Slides.Count == 2 && !model.Slides[1].HasClippedText);
    }

    /// <summary>
    /// Tickers and text on a path: a ring of text going round, a stream of announcements along the
    /// bottom and words round a shape's edge. Frozen a second apart they've moved; the grid shows them still.
    /// </summary>
    private async Task PathTextCheck()
    {
        var ring = TextBox("ticker-ring", "GRACE FELLOWSHIP WELCOMES YOU • ", "Ring");
        (ring.X, ring.Y, ring.Width, ring.Height) = (160, 140, 560, 560);
        ring.TextStyle = new TextStyle { FontSize = 52, PathData = PathPresets.Ellipse, TickerSpeed = 80, ColorHex = "#FFFFFFFF" };
        var news = TextBox("ticker-news", "Service times: Sunday 9am and 11am", "News");
        (news.X, news.Y, news.Width, news.Height) = (0, 960, 1920, 100);
        news.TextStyle = new TextStyle
        {
            FontSize = 50, PathData = PathPresets.Line, TickerSpeed = 240, TickerStream = true, TickerGap = 80, TickerSeparator = "  •  ", ColorHex = "#FFD60AFF",
        };
        var badge = new SlideObject
        {
            Id = "edge-badge", ObjectKind = SlideObjectKind.Shape, Name = "Badge", Text = "TEXT ROUND A SHAPE'S EDGE", ShapeKind = ShapeKind.Ellipse,
            X = 1160, Y = 220, Width = 480, Height = 480, ShapeTextPlacement = ShapeTextPlacement.EdgeOutside,
            Fill = new ObjectFill { FillKind = FillKind.Solid, ColorHex = "#2563EBFF" },
            TextStyle = new TextStyle { FontSize = 44, PathOffset = 8, ColorHex = "#FFFFFFFF" },
        };
        var slide = new Slide { Id = "selftest-tickers", Name = "Tickers", Objects = [ring, news, badge] };
        var deck = new Presentation { Id = "selftest-tickers", Name = "Tickers", PresentationKind = PresentationKind.Deck, ThemeId = "", Slides = [slide] };
        model.Library.Save(deck);
        await Step("52-tickers-grid", () => model.Open(deck, null, null), settle: 800);
        FireSlide(deck, 0);
        await Delay(600);

        var live = model.Show.AnimatedScene();
        Check("the scene carries the tickers and the shape's edge text along paths",
            live[LayerKind.Slide].Items.Count(i => i.Render.Content is ItemContent.Text { Styled.PathData: not null }) == 3);
        var at1 = await Picture("52-tickers-1s", FrozenAt(live, 1));
        var at2 = await Picture("52-tickers-2s", FrozenAt(live, 2));
        // The canvas is drawn at 2/3 size: the news band is the bottom rows, the ring the left third.
        Check("the stream ticker moves along its line", !SameRegion(at1, at2, 1280, 0, 640, 1280, 707));
        Check("the ring ticker goes round", !SameRegion(at1, at2, 1280, 90, 80, 490, 480));
        Check("text round a shape's edge is drawn and stays put", SameRegion(at1, at2, 1280, 760, 130, 1110, 480)
            && !SameRegion(at1, new byte[at1.Length], 1280, 760, 130, 1110, 145));
        var still1 = await Picture("52-tickers-thumbnail", live);
        var still2 = await Picture("52-tickers-thumbnail-again", live);
        Check("thumbnails show a ticker at rest", SameRegion(still1, still2, 1280, 0, 0, 1280, 720));

        if (model.Program is { } feed && !HiddenCapture.FastCaptureFailed)
        {
            var sink = new CountingSink();
            feed.Add(sink);
            await Delay(800);
            var first = sink.Last?.ToArray();
            await Delay(700);
            var second = sink.Last?.ToArray();
            feed.Remove(sink);
            if (first is not null && second is not null && sink.Width > 0)
            {
                Write(BitmapSource.Create(sink.Width, sink.Height, 96, 96, PixelFormats.Pbgra32, null, second, sink.Width * 4), "52-tickers-program");
                Check("on the program feed the ticker keeps moving", !SameRegion(first, second, sink.Width, 0, sink.Height * 9 / 10, sink.Width, sink.Height));
            }
        }
        model.ClearAllCommand.Execute(null);
    }

    /// <summary>Keystone: a box with its top edge at half width is a trapezoid (perspective and stretch both).</summary>
    private async Task KeystoneCheck()
    {
        if (!KeystoneEffect.IsSupported)
        {
            Skip("keystone", "this PC can't run even software pixel shaders");
            return;
        }
        SlideObject Box(string id, double x, KeystoneMode mode) => new()
        {
            Id = id, ObjectKind = SlideObjectKind.Shape, Name = "Box", Text = mode.ToString().ToUpperInvariant(), X = x, Y = 340, Width = 720, Height = 400,
            Fill = new ObjectFill { FillKind = FillKind.Solid, ColorHex = "#FFFFFFFF" }, KeystoneTop = 50, KeystoneMode = mode,
            TextStyle = new TextStyle { FontSize = 72, ColorHex = "#111111FF" },
        };
        var slide = new Slide { Id = "selftest-keystone", Name = "Keystone", Objects = [Box("key-p", 120, KeystoneMode.Perspective), Box("key-s", 1080, KeystoneMode.Stretch)] };
        var deck = new Presentation { Id = "selftest-keystone", Name = "Keystone", PresentationKind = PresentationKind.Deck, ThemeId = "", Slides = [slide] };
        FireSlide(deck, 0);
        await Delay(500);
        var pixels = await Picture("53-keystone", model.Show.AnimatedScene());
        // At 2/3 size the perspective box spans x 80–560, y 227–493; its top edge is now 240 wide, centred.
        static bool White(byte[] p, int x, int y) => p[(y * 1280 + x) * 4] > 200 && p[(y * 1280 + x) * 4 + 2] > 200;
        Check("a keystoned box's narrowed top corners are empty", !White(pixels, 95, 232) && !White(pixels, 545, 232));
        Check("its full-width bottom corners are filled", White(pixels, 95, 488) && White(pixels, 545, 488));
        Check("the stretch keystone narrows its top too", !White(pixels, 735, 232) && White(pixels, 735, 488));
        if (model.Program is { } feed && !HiddenCapture.FastCaptureFailed && RenderCapability.Tier >> 16 > 0)
        {
            var sink = new CountingSink();
            feed.Add(sink);
            await Delay(700);
            feed.Remove(sink);
            if (sink.Last is { } program && sink.Width > 0)
                Write(BitmapSource.Create(sink.Width, sink.Height, 96, 96, PixelFormats.Pbgra32, null, program, sink.Width * 4), "53-keystone-program");
        }
        model.ClearAllCommand.Execute(null);
    }

    /// <summary>
    /// A font saved in the library (as a PowerPoint import saves a deck's embedded fonts) is loaded
    /// without installing it: a copy of Georgia renamed "MxUText" draws exactly like Georgia.
    /// </summary>
    private async Task LibraryFontCheck()
    {
        var georgia = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "georgia.ttf");
        if (!File.Exists(georgia))
        {
            Skip("fonts saved in the library", "this PC has no Georgia to copy");
            return;
        }
        Check("the renamed font isn't installed in Windows", !Fonts.SystemFontFamilies.Any(f => f.Source == "MxUText"));
        var folder = Directory.CreateDirectory(Path.Combine(model.Library.Root, Core.Library.FontFiles.FolderName)).FullName;
        File.WriteAllBytes(Path.Combine(folder, "selftest-font.ttf"), RenamedFont(File.ReadAllBytes(georgia), "Georgia", "MxUText"));
        model.ActivateLibraryFonts();
        Check("a font in the library's fonts folder is loaded for drawing", FontMap.IsLibraryFont("MxUText")
            && FontMap.Resolve("MxUText").Family.Source.Contains("fonts", StringComparison.OrdinalIgnoreCase));
        Check("it's offered in the font lists", FontMap.InstalledFamilies().Contains("MxUText"));

        AnimatedScene Words(string font)
        {
            var scene = new RenderScene(SceneSize.Hd, new SceneColor(0.1, 0.1, 0.15));
            scene.AddItem(new RenderItem
            {
                Id = "words", Frame = new SceneRect(100, 300, 1720, 480),
                Content = new ItemContent.Text(new StyledText { String = $"Amazing grace", FontSize = 160, FontName = font }),
            }, LayerKind.Slide);
            return AnimatedScene.From(scene);
        }
        var own = await Picture("54-library-font", Words("MxUText"));
        var real = await Picture("54-library-font-georgia", Words("Georgia"));
        var fallback = await Picture("54-library-font-fallback", Words("NoSuchFont"));
        Check("text in the library font draws as that font, not a stand-in",
            SameRegion(own, real, 1280, 0, 150, 1280, 570) && !SameRegion(own, fallback, 1280, 0, 150, 1280, 570));
    }

    /// <summary>A copy of a font with its family name changed (same length), so it's a font Windows doesn't have.</summary>
    private static byte[] RenamedFont(byte[] font, string from, string to)
    {
        var data = font.ToArray();
        int U16(int o) => data[o] << 8 | data[o + 1];
        int U32(int o) => data[o] << 24 | data[o + 1] << 16 | data[o + 2] << 8 | data[o + 3];
        for (var i = 0; i < U16(4); i++)
        {
            var record = 12 + i * 16;
            if (Encoding.ASCII.GetString(data, record, 4) != "name") continue;
            var start = U32(record + 8);
            var end = start + U32(record + 12);
            foreach (var encoding in new Encoding[] { Encoding.BigEndianUnicode, Encoding.ASCII })
            {
                var a = encoding.GetBytes(from);
                var b = encoding.GetBytes(to);
                for (var j = start; j + a.Length <= end; j++)
                    if (data.AsSpan(j, a.Length).SequenceEqual(a)) b.CopyTo(data, j);
            }
        }
        return data;
    }

    /// <summary>
    /// A live picture inside a slide object: the grid shows the Mac's camera placeholder for an input,
    /// and a slide whose shape mirrors the stage screen shows that screen's picture inside it on air.
    /// </summary>
    private async Task LivePictureFillCheck()
    {
        var pip = new SlideObject
        {
            Id = "pip-camera", ObjectKind = SlideObjectKind.Shape, Name = "Camera", Text = "", ShapeKind = ShapeKind.RoundedRectangle, CornerRadius = 40,
            X = 1180, Y = 560, Width = 640, Height = 360, Fill = new ObjectFill { FillKind = FillKind.Media, LiveInputId = "selftest-camera" },
            Stroke = new ObjectStroke { ColorHex = "#FFFFFFFF", Width = 6 },
        };
        var cameraSlide = new Slide { Id = "selftest-pip", Name = "", Objects = [TextBox("pip-t", "Welcome"), pip] };
        var deck = new Presentation { Id = "selftest-pip", Name = "Picture in picture", PresentationKind = PresentationKind.Deck, ThemeId = "", Slides = [cameraSlide] };
        model.Library.Save(deck);
        await Step("55-live-input-placeholder-grid", () => model.Open(deck, null, null), settle: 800);
        Check("a live input fill reaches the slide's scene", model.Slides.FirstOrDefault()?.Scene.MediaIds.Contains("input::item::selftest-camera") == true);
        await Picture("55-live-input-placeholder", AnimatedScene.From(model.Slides[0].Scene));

        if (model.Outputs is not { } outputs) return;
        var config = outputs.Config;
        var screen = config.AddScreen("Mirror test", 1280, 720)!;
        try
        {
            config.SetRole(screen.Id, ScreenRole.Confidence);
            outputs.Apply();
            var mirrorId = SwiftUuid.Format(screen.Id);
            var mirror = Core.Json.MxuJson.Clone(pip);
            mirror.Id = "pip-mirror";
            mirror.Fill = new ObjectFill { FillKind = FillKind.Media, ScreenSourceId = mirrorId, MediaScaleMode = MediaScaleMode.Fit };
            var mirrorSlide = new Slide { Id = "selftest-mirror", Name = "", Objects = [TextBox("mirror-t", "The stage screen, inside a slide"), mirror] };
            var mirrorDeck = new Presentation { Id = "selftest-mirror", Name = "Screen mirror", PresentationKind = PresentationKind.Deck, ThemeId = "", Slides = [mirrorSlide] };
            FireSlide(mirrorDeck, 0);
            var engineId = SlideSceneBuilder.ScreenMirrorEngineId(mirrorId);
            for (var i = 0; i < 60 && model.Media.LiveInputPicture(engineId) is null; i++) await Delay(100);
            Check("a shape mirroring the stage screen starts that screen's feed", model.ScreenMirrors.RunningCount >= 1);
            Check("the mirrored screen's picture arrives", model.Media.LiveInputPicture(engineId) is { PixelWidth: > 0 });
            await Step("55-screen-mirror-live", () => { }, settle: 900);
            if (model.Program is { } feed)
            {
                var sink = new CountingSink();
                feed.Add(sink);
                await Delay(900);
                feed.Remove(sink);
                if (sink.Last is { } program && sink.Width > 0)
                    Write(BitmapSource.Create(sink.Width, sink.Height, 96, 96, PixelFormats.Pbgra32, null, program, sink.Width * 4), "55-screen-mirror-program");
            }
        }
        finally
        {
            model.ClearAllCommand.Execute(null);
            config.RemoveScreen(screen.Id);
            outputs.Apply();
        }
    }

    /// <summary>
    /// A video with an in point, an out point and double speed: it reports the trimmed window, ends its
    /// pass at the out point (VideoEnded once), and when it loops, loops inside the trim.
    /// </summary>
    private async Task VideoTrimCheck()
    {
        var video = model.Library.All<MediaItem>().FirstOrDefault(m => m.MediaKind == MediaKind.Video && model.Library.BlobPath(m) is not null);
        if (video is null)
        {
            Skip("video trim and play rate", "no video in the test library (this PC couldn't make one)");
            return;
        }
        var original = (video.InPoint, video.OutPoint, video.PlayRate, video.Loops, video.Classification);
        var key = $"live:live-media-videos:{video.Id}";
        var ends = new List<double>();
        void Ended(string playbackKey)
        {
            if (playbackKey == key) ends.Add(ShowState.HostClock());
        }
        model.Media.VideoEnded += Ended;
        try
        {
            video.Classification = MediaClassification.Foreground;
            video.InPoint = 0.5;
            video.OutPoint = 1.7;
            video.PlayRate = 2;
            video.Loops = false;
            model.Library.Save(video);
            var fired = ShowState.HostClock();
            model.FireMedia(video);
            for (var i = 0; i < 150 && model.Media.Transport(key) is null && !model.Media.IsVideoFailed(key); i++) await Delay(100);
            var transport = model.Media.Transport(key);
            Check("a trimmed video reports its window's length", transport is { Duration.TotalSeconds: > 1.1 and < 1.3 });
            for (var i = 0; i < 40 && ends.Count == 0; i++) await Delay(100);
            Check("a trimmed video's pass ends at its out point, at its play rate",
                ends.Count == 1 && ends[0] - fired < 2.5 && model.Media.Transport(key) is { Playing: false });
            await Delay(600);
            Check("a pass that ended is reported once", ends.Count == 1);
            model.ClearMediaCommand.Execute(null);
            await Delay(3200); // the player is let go a moment after the last view does (after the media dissolve)

            video.Loops = true;
            model.Library.Save(video);
            ends.Clear();
            model.FireMedia(video);
            for (var i = 0; i < 150 && model.Media.Transport(key) is null && !model.Media.IsVideoFailed(key); i++) await Delay(100);
            await Delay(2600);
            var looping = model.Media.Transport(key);
            report.AppendLine($"      looping trim: {ends.Count} passes in 2.6 s, at {looping?.Position.TotalSeconds:0.00} s of {looping?.Duration?.TotalSeconds:0.00} s");
            Check("a looping trimmed video loops inside its trim (a pass each 0.6 s at double speed)",
                ends.Count >= 2 && looping is { Playing: true } && looping.Value.Position.TotalSeconds <= 1.25);
            await Step("56-trimmed-video", () => { }, settle: 300);
        }
        finally
        {
            model.Media.VideoEnded -= Ended;
            model.ClearMediaCommand.Execute(null);
            (video.InPoint, video.OutPoint, video.PlayRate, video.Loops, video.Classification) = original;
            model.Library.Save(video);
        }
    }
}
