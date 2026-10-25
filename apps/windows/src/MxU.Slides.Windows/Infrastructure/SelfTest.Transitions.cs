using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.Rendering;
using MxU.Slides.Windows.Views;
using MxU.Slides.Windows.Views.Transitions;
using AnimatedScene = MxU.Slides.Core.Animation.AnimatedScene;
using LayerTransition = MxU.Slides.Core.Animation.SceneTransition;

namespace MxU.Slides.Windows.Infrastructure;

// The Windows-only transitions (Phase 6): every kind pictured half-way (60-*), a push on the real
// program feed with its frame pacing (61-*), the Mac-readable fallback and a Mac edit winning, the
// no-shader fallback, and the pickers (62-*).
public sealed partial class SelfTest
{
    private async Task TransitionsCheck()
    {
        try
        {
            await TransitionPicturesCheck();
            await TransitionOutputCheck();
            await TransitionPickersCheck();
        }
        finally
        {
            Environment.SetEnvironmentVariable("MXU_SLIDES_TRANSITIONS", null);
            model.ClearAllCommand.Execute(null);
        }
    }

    /// <summary>Draws a scene offscreen (WPF's software drawing, as thumbnails and the software copy do) and returns its pixels.</summary>
    private async Task<(BitmapSource Bitmap, byte[] Pixels)> Drawn(AnimatedScene scene, int width = 1280, int height = 720)
    {
        var view = new SceneView { Media = model.Media, PlaysVideo = false, Width = width, Height = height };
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.Present(scene, Rendering.SceneTransition.Cut);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return (bitmap, pixels);
    }

    /// <summary>How different two pictures are: the mean difference of their colours (0–255), every fourth pixel.</summary>
    private static double Difference(byte[] a, byte[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 255;
        double total = 0;
        var count = 0;
        for (var i = 0; i + 3 < a.Length; i += 16)
        {
            total += Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
            count += 3;
        }
        return total / count;
    }

    /// <summary>How far a picture is from a plain cross-fade of two others: small for a dissolve half-way, large for a push.</summary>
    private static double FromCrossFade(byte[] mid, byte[] a, byte[] b)
    {
        if (mid.Length != a.Length || a.Length != b.Length || a.Length == 0) return 255;
        double total = 0;
        var count = 0;
        for (var i = 0; i + 3 < a.Length; i += 16)
            for (var c = 0; c < 3; c++)
            {
                total += Math.Abs(mid[i + c] - (a[i + c] + b[i + c]) / 2.0);
                count++;
            }
        return total / count;
    }

    /// <summary>
    /// Every Windows-only kind, half-way between the two sample slides (a background and words, both
    /// changing), drawn as an output draws it: each picture must be neither the old slide nor the new
    /// one. Drawn offscreen, which is WPF's software drawing: it shows the shaders run without a
    /// graphics card too (they're ps_2_0), as on a PC that can't capture (the software copy).
    /// </summary>
    private async Task TransitionPicturesCheck()
    {
        var before = TransitionSamples.Before();
        var after = TransitionSamples.After();
        var (_, beforePixels) = await Drawn(before);
        var (_, afterPixels) = await Drawn(after);
        Check("every transition shader compiles on this PC", WindowsTransitions.All.All(TransitionShaders.CanDraw));

        var failed = new List<string>();
        var sheet = new DrawingVisual();
        var cells = new List<(BitmapSource Bitmap, string Name)>();
        var index = 0;
        var started = Stopwatch.StartNew();
        WindowsTransition[] extras =
        [
            new() { Kind = WindowsTransitionKind.Cube, Direction = TransitionDirection.Up },
            new() { Kind = WindowsTransitionKind.Push, Direction = TransitionDirection.Right },
            new() { Kind = WindowsTransitionKind.SoftWipe, Direction = TransitionDirection.Down, Softness = 0.8 },
            new() { Kind = WindowsTransitionKind.LightLeak, ColorHex = "#7FB2FF" },
        ];
        foreach (var transition in WindowsTransitions.WindowsOnly.Select(k => new WindowsTransition { Kind = k }).Concat(extras))
        {
            var engine = new SceneTransitionEngine(before);
            engine.Push(after, LayerTransition.Of(transition with { DurationSeconds = 1 }), now: 0);
            var half = engine.Scene(0.5);
            var name = transition.Kind.ToString() + (index >= WindowsTransitions.WindowsOnly.Count ? $"-{transition.Direction}{(transition.ColorHex is null ? "" : "-blue")}" : "");
            var (bitmap, pixels) = await Drawn(half);
            Write(bitmap, $"60-{++index:00}-{name}-half");
            var fromOld = Difference(pixels, beforePixels);
            var fromNew = Difference(pixels, afterPixels);
            if (fromOld < 4 || fromNew < 4) failed.Add($"{name} ({fromOld:0.0} / {fromNew:0.0})");
            if (index <= WindowsTransitions.WindowsOnly.Count) cells.Add((bitmap, WindowsTransitions.Name(transition.Kind)));
        }
        report.AppendLine($"      {index} transitions pictured half-way in {started.ElapsedMilliseconds} ms{(failed.Count > 0 ? "; unchanged: " + string.Join(", ", failed) : "")}");
        Check("every new transition draws half-way between the old picture and the new (software drawing)", failed.Count == 0);

        // One sheet with all eighteen, the picture to look at first.
        const int w = 384, h = 216, columns = 6;
        var rows = (cells.Count + columns - 1) / columns;
        using (var dc = sheet.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)), null, new Rect(0, 0, columns * (w + 8) + 8, rows * (h + 30) + 8));
            for (var i = 0; i < cells.Count; i++)
            {
                var x = 8 + i % columns * (w + 8);
                var y = 8 + i / columns * (h + 30);
                dc.DrawImage(cells[i].Bitmap, new Rect(x, y, w, h));
                dc.DrawText(new FormattedText(cells[i].Name, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 14, Brushes.White, 1), new Point(x, y + h + 4));
            }
        }
        var target = new RenderTargetBitmap(columns * (w + 8) + 8, rows * (h + 30) + 8, 96, 96, PixelFormats.Pbgra32);
        target.Render(sheet);
        Write(target, "60-00-all-transitions-half-way");
    }

    /// <summary>A plain two-slide deck: a full-screen colour and a word each, no theme and no animation, so a frame shows only the transition.</summary>
    private static Presentation TransitionTestDeck()
    {
        Slide Plain(string id, string color, string word) => new()
        {
            Id = id, Name = word,
            Objects =
            [
                new SlideObject
                {
                    Id = $"{id}-plate", ObjectKind = SlideObjectKind.Shape, Name = "Plate", Text = "", X = 0, Y = 0, Width = 1920, Height = 1080,
                    ShapeKind = ShapeKind.Rectangle, Fill = new ObjectFill { FillKind = FillKind.Solid, ColorHex = color },
                },
                new SlideObject { Id = $"{id}-word", ObjectKind = SlideObjectKind.Text, Name = "Word", Text = word, X = 160, Y = 380, Width = 1600, Height = 320 },
            ],
        };
        return new Presentation
        {
            Id = "selftest-transitions", Name = "Transitions test",
            Slides = [Plain("selftest-transition-1", "#1D4ED8FF", "Before"), Plain("selftest-transition-2", "#EA580CFF", "After")],
        };
    }

    /// <summary>
    /// Fires slide 1, then slide 2, which has its own Windows-only transition, and watches the
    /// program feed (the stream's picture) all the way through, timing every frame the screen draws.
    /// Returns the frame nearest the middle, the last one, and the first one, or null without frames.
    /// </summary>
    private Task<(byte[] Before, byte[] Middle, byte[] Landed, int Width, int Height, double Fps, double Longest, double First)?> PlayOnProgram(
        Presentation deck, double seconds) => WatchProgram(() => FireSlide(deck, 0), () => FireSlide(deck, 1), seconds);

    /// <summary>Does <paramref name="prepare"/>, then <paramref name="change"/>, watching the program feed through a transition of <paramref name="seconds"/>.</summary>
    private async Task<(byte[] Before, byte[] Middle, byte[] Landed, int Width, int Height, double Fps, double Longest, double First)?> WatchProgram(
        Action prepare, Action change, double seconds)
    {
        if (model.Program is not { } feed) return null;
        var sink = new CountingSink();
        feed.Add(sink);
        var ticks = new List<double>();
        var clock = new Stopwatch();
        void Tick(object? s, EventArgs e) => ticks.Add(clock.Elapsed.TotalMilliseconds);
        try
        {
            prepare();
            // Long enough for whatever the preparation set off to land (it may use the same transition).
            await Delay(Math.Max(1200, (int)(seconds * 1000) + 500));
            var before = sink.Last;
            clock.Start();
            CompositionTarget.Rendering += Tick;
            change();
            byte[]? middle = null;
            var bestDistance = double.MaxValue;
            while (clock.Elapsed.TotalSeconds < seconds + 0.4)
            {
                await Delay(25);
                var at = clock.Elapsed.TotalSeconds;
                if (sink.Last is { } frame && Math.Abs(at - seconds / 2) < bestDistance && at < seconds)
                {
                    bestDistance = Math.Abs(at - seconds / 2);
                    middle = frame;
                }
            }
            CompositionTarget.Rendering -= Tick;
            await Delay(400);
            if (before is null || middle is null || sink.Last is not { } landed) return null;
            var during = ticks.Where(t => t <= seconds * 1000).ToList();
            var gaps = during.Zip(during.Skip(1), (a, b) => b - a).ToList();
            return (before, middle, landed, sink.Width, sink.Height, during.Count / seconds, gaps.Count > 0 ? gaps.Max() : 0, during.Count > 0 ? during[0] : 0);
        }
        finally
        {
            CompositionTarget.Rendering -= Tick;
            feed.Remove(sink);
        }
    }

    /// <summary>
    /// The whole path on the real outputs: a slide given Push Left through the app (as its right-click
    /// menu does) plays a push on the program feed, at full frame rate from the first frame. Its
    /// document keeps a plain Dissolve for the Mac, a Mac edit wins, and on a PC without the
    /// transition shaders the same slide plays that Dissolve.
    /// </summary>
    private async Task TransitionOutputCheck()
    {
        var deck = TransitionTestDeck();
        model.Library.Save(deck);
        try
        {
            var push = new WindowsTransition { Kind = WindowsTransitionKind.Push, Direction = TransitionDirection.Left, DurationSeconds = 1.6 };
            var slideId = deck.Slides[1].Id;
            Check("a slide can be given a Windows-only transition", model.SaveSlideTransition(deck.Id, slideId, push));
            deck = model.Library.Get<Presentation>(deck.Id)!;
            var stored = File.ReadAllText(model.Library.DocumentPath(typeof(Presentation), deck.Id));
            Check("its document keeps a Dissolve the Mac can read, never the Windows kind",
                stored.Contains("\"dissolve\"", StringComparison.Ordinal) && !stored.Contains("push", StringComparison.OrdinalIgnoreCase));
            Check("the Push is kept beside the library", File.Exists(Path.Combine(model.Library.Root, WindowsExtras.FolderName, TransitionExtras.SlideKind, slideId + ".json")));
            Check("the app reads the Push back", model.SlideTransitionChoice(deck.Slides[1]) == push);

            if (await PlayOnProgram(deck, push.DurationSeconds) is { } played)
            {
                var (w, h) = (played.Width, played.Height);
                Write(BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, played.Before, w * 4), "61-push-on-program-before");
                Write(BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, played.Middle, w * 4), "61-push-on-program-middle");
                Write(BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, played.Landed, w * 4), "61-push-on-program-landed");
                var change = Difference(played.Before, played.Landed);
                var crossFade = FromCrossFade(played.Middle, played.Before, played.Landed);
                report.AppendLine($"      Push on the program feed: {played.Fps:0} frames a second on the screen, longest frame {played.Longest:0} ms, "
                    + $"first {played.First:0} ms; middle frame {crossFade / Math.Max(change, 1):0.00} of the change away from a cross-fade"
                    + (model.Program?.UsesSoftwareCopy == true ? " (software copy)" : ""));
                Check("a slide's Windows-only transition plays on the outputs (the program is mid-push, not a cross-fade)",
                    change > 20 && crossFade > change * 0.3 && Difference(played.Middle, played.Before) > 5 && Difference(played.Middle, played.Landed) > 5);
                Check("the push lands on the new slide", AudienceText().Contains("After", StringComparison.Ordinal) && Difference(played.Landed, played.Before) > 20);
                // The laptop's screen runs at 60 Hz; on a busy or shared PC, allow some slack before calling it a stutter.
                Check("the outputs keep drawing through the transition (30+ frames a second, no frame over 100 ms, the first on time)",
                    played.Fps >= 30 && played.Longest < 100 && played.First < 100);
            }
            else
            {
                Skip("a Windows-only transition on the program feed", "this PC's capture gave no frames");
            }

            // Pushed off to nothing: with a push as the slides' room default, clearing the slide (F2)
            // must slide it away, not make it vanish (the new picture is empty).
            var room = model.RoomTransitionChoice(media: false);
            model.SetRoomTransition(media: false, push);
            try
            {
                if (await WatchProgram(() => FireSlide(deck, 0), () => model.ClearSlideCommand.Execute(null), push.DurationSeconds) is { } cleared)
                {
                    var (w, h) = (cleared.Width, cleared.Height);
                    Write(BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, cleared.Middle, w * 4), "61-push-off-to-nothing-middle");
                    var change = Difference(cleared.Before, cleared.Landed);
                    Check("a slide pushed off to an empty screen slides away (it doesn't vanish)",
                        change > 20 && Difference(cleared.Middle, cleared.Landed) > change * 0.2 && Difference(cleared.Middle, cleared.Before) > change * 0.2);
                }
            }
            finally
            {
                model.SetRoomTransition(media: false, room);
            }

            // A Mac changes the slide's transition (the document comes back with a Fade Black): the Mac's choice wins.
            var edited = model.Library.Get<Presentation>(deck.Id)!;
            edited.Slides[1].Transition = new Transition { TransitionKind = TransitionKind.FadeBlack, DurationSeconds = 1.6 };
            model.Library.Save(edited);
            Check("a Mac edit to the slide's transition wins over the Windows one",
                model.SlideTransitionChoice(model.Library.Get<Presentation>(deck.Id)!.Slides[1])?.Kind == WindowsTransitionKind.FadeBlack);

            // A PC without the transition shaders (forced): the same Push plays as its Dissolve.
            model.SaveSlideTransition(deck.Id, slideId, push);
            deck = model.Library.Get<Presentation>(deck.Id)!;
            Environment.SetEnvironmentVariable("MXU_SLIDES_TRANSITIONS", "simple");
            if (await PlayOnProgram(deck, push.DurationSeconds) is { } simple)
            {
                var (w, h) = (simple.Width, simple.Height);
                Write(BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, simple.Middle, w * 4), "61-push-without-shaders-middle");
                var change = Difference(simple.Before, simple.Landed);
                var crossFade = FromCrossFade(simple.Middle, simple.Before, simple.Landed);
                report.AppendLine($"      Without shaders: middle frame {crossFade / Math.Max(change, 1):0.00} of the change away from a cross-fade");
                Check("without the shaders, a Windows-only transition plays its Dissolve instead", change > 20 && crossFade < change * 0.3);
            }
            else
            {
                Skip("the no-shader fallback on the program feed", "this PC's capture gave no frames");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("MXU_SLIDES_TRANSITIONS", null);
            model.ClearAllCommand.Execute(null);
            foreach (var slide in deck.Slides) model.TransitionExtras.Store.Delete(TransitionExtras.SlideKind, slide.Id);
            model.Library.Delete<Presentation>(deck.Id);
        }
    }

    /// <summary>
    /// Where they're chosen: a slide's transition window (its previews must be moving), the slide
    /// tile's right-click menu, the live header's chip with a Windows kind as the room default, and
    /// Settings › Transitions.
    /// </summary>
    private async Task TransitionPickersCheck()
    {
        var deck = TransitionTestDeck();
        model.Library.Save(deck);
        var room = model.RoomTransitionChoice(media: false);
        try
        {
            model.Open(deck, null, null);
            await Delay(300);
            if (TransitionPickerWindow.ForSlide(window, model, deck.Slides[1], deck) is { } picker)
            {
                picker.Show();
                await Delay(1500);
                Save(picker, "62-transition-picker");
                var first = PixelsOf(picker);
                await Delay(450);
                Check("the transition picker's previews play", first is not null && PixelsOf(picker) is { } second && Difference(first, second) > 0.05);
                picker.Picker.Pick(WindowsTransitionKind.Cube);
                await Delay(300);
                var cube = model.SlideTransitionChoice(model.Library.Get<Presentation>(deck.Id)!.Slides[1]);
                Check("choosing a tile saves it for the slide at once", cube?.Kind == WindowsTransitionKind.Cube);
                Save(picker, "62-transition-picker-cube");
                picker.Close();
            }
            else
            {
                Check("the transition picker opens for a slide", false);
            }

            // The slide tile's right-click menu gains the Transition item as it opens.
            if (Descendants<FrameworkElement>(window).FirstOrDefault(e => e.DataContext is ViewModels.SlideTileViewModel { Index: 1 }) is { } tile)
            {
                var menu = new ContextMenu { PlacementTarget = tile };
                menu.IsOpen = true;
                await Delay(250);
                var item = menu.Items.OfType<MenuItem>().FirstOrDefault(i => i.Header is string header && header.StartsWith("Transition", StringComparison.Ordinal));
                Check("a slide's right-click menu has Transition (this slide, the whole presentation)",
                    item is not null && (item.Header as string)?.Contains("Cube", StringComparison.Ordinal) == true && item.Items.Count == 2);
                menu.IsOpen = false;
            }
            else
            {
                Check("a slide tile is on the screen to right-click", false);
            }

            // A Windows kind as the slides' room default: the chip shows it, and Settings shows it playing.
            model.SetRoomTransition(media: false, new WindowsTransition { Kind = WindowsTransitionKind.Iris, DurationSeconds = 0.9, Softness = 0.5 });
            await Delay(200);
            if (Descendant<LiveToolbar>(window) is { } toolbar)
            {
                Check("a room default can be a Windows-only kind (the chip shows it)", toolbar.SlidesText == "Iris 0.9 s"
                    && File.ReadAllText(AppPaths.SettingsFile).Contains("\"Iris\"", StringComparison.Ordinal));
                var popover = toolbar.OpenTransitionPopover(forMedia: false);
                await Delay(1400);
                if (popover?.Child is FrameworkElement chip) SaveElement(chip, "62-transition-chip-popover");
                if (popover is not null) popover.IsOpen = false;
            }
            if (model.Outputs is { } outputs)
            {
                var settings = new SettingsWindow(model, outputs) { Owner = window };
                settings.Show();
                await Delay(1000);
                Check("Settings › Transitions shows the room defaults playing", Descendant<TransitionSettingsSection>(settings) is not null);
                Save(settings, "62-transition-settings");
                settings.Close();
            }
        }
        finally
        {
            model.SetRoomTransition(media: false, room);
            foreach (var slide in deck.Slides) model.TransitionExtras.Store.Delete(TransitionExtras.SlideKind, slide.Id);
            model.Library.Delete<Presentation>(deck.Id);
        }
    }

    /// <summary>A window's content as pixels (to tell whether anything in it moved).</summary>
    private static byte[]? PixelsOf(Window target)
    {
        if (target.Content is not FrameworkElement content || content.ActualWidth < 1) return null;
        var width = (int)content.ActualWidth;
        var height = (int)content.ActualHeight;
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }
}
