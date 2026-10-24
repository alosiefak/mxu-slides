using System.Windows.Media;
using System.Windows.Media.Imaging;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Outputs;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Outputs;
using MxU.Slides.Windows.Rendering;
using AnimatedScene = MxU.Slides.Core.Animation.AnimatedScene;

namespace MxU.Slides.Windows.Infrastructure;

// The render pipeline checks (Phase 4): two screens showing the live slide through different
// looks, a long lyric that turns its pages, a slide's own transition, and Echo and Ghost Trails.
// Pictures come from the real outputs (graphics-card capture) where this PC can capture them,
// and from offscreen drawing otherwise.
public sealed partial class SelfTest
{
    private const string ShortLyric = "Amazing grace how sweet the sound\nThat saved a wretch like me";

    private const string LongLyric =
        "Amazing grace how sweet the sound\nThat saved a wretch like me\nI once was lost but now am found\nWas blind but now I see\n"
        + "Twas grace that taught my heart to fear\nAnd grace my fears relieved\nHow precious did that grace appear\nThe hour I first believed\n"
        + "Through many dangers toils and snares\nI have already come\nTis grace hath brought me safe thus far\nAnd grace will lead me home\n"
        + "The Lord has promised good to me\nHis word my hope secures\nHe will my shield and portion be\nAs long as life endures";

    private async Task RenderCheck()
    {
        try
        {
            await LooksAndPagingCheck();
            await LayerTransitionCheck();
            await TrailsCheck();
        }
        finally
        {
            model.ClearAllCommand.Execute(null);
        }
    }

    /// <summary>A song on a starter theme: a short verse, a long one, and a verse with its own Fade White transition.</summary>
    private static Presentation RenderTestSong(Theme theme)
    {
        Slide Lyric(string id, string text, Transition? transition = null) => new()
        {
            Id = id, Name = "Verse", ThemeSlideName = "Lyrics", Transition = transition,
            Objects = [new SlideObject { Id = $"{id}-lyrics", ObjectKind = SlideObjectKind.Text, Name = "Lyrics", Text = text }],
        };
        return new Presentation
        {
            Id = "selftest-render", Name = "Render test", PresentationKind = PresentationKind.Song, ThemeId = theme.Id,
            Slides =
            [
                Lyric("selftest-render-1", ShortLyric),
                Lyric("selftest-render-2", LongLyric),
                Lyric("selftest-render-3", "Was blind but now I see", new Transition { TransitionKind = TransitionKind.FadeWhite, DurationSeconds = 1.6 }),
            ],
        };
    }

    private void FireSlide(Presentation song, int index) => ((IShowActions)model).FireSlide(song.Slides[index], song);

    /// <summary>The frame of the text item showing (part of) the lyric, in the scene's canvas.</summary>
    private static SceneRect? LyricFrame(AnimatedScene? scene, string startsWith) => scene?[LayerKind.Slide].Items
        .FirstOrDefault(i => i.Render.Content is ItemContent.Text t && t.Styled.String.StartsWith(startsWith, StringComparison.Ordinal))?.Render.Frame;

    /// <summary>The text a scene shows right now, after its animation (pages turned, builds played).</summary>
    private static string VisibleText(AnimatedScene? scene)
    {
        if (scene is null) return "";
        var resolved = Core.Animation.AnimationEvaluator.Resolve(scene, ShowState.HostClock());
        return string.Join("\n", resolved.Layers.Where(l => !l.IsHidden).SelectMany(l => l.Items)
            .Where(r => r.Item.Render.Opacity > 0.05 && r.Item.HiddenRuns.Count == 0)
            .Select(r => r.Item.Render.Content is ItemContent.Text t ? t.Styled.String : null).OfType<string>());
    }

    /// <summary>
    /// Two screens on stand-in network outputs, one showing the slides through a starter theme's
    /// lower-third lyric design and one through its side-third design. The same slide must land in
    /// each look's place on its screen while the projector shows it full screen; then a long lyric
    /// must show its first page on the lower third and turn to the second on Next.
    /// </summary>
    private async Task LooksAndPagingCheck()
    {
        if (model.Outputs is not { } outputs) return;
        var config = outputs.Config;
        var theme = model.Library.All<Theme>().FirstOrDefault(t =>
            t.Slides?.Any(s => s.Id.EndsWith(".lyricsLower", StringComparison.Ordinal)) == true
            && t.Slides.Any(s => s.Id.EndsWith(".lyricsSide", StringComparison.Ordinal)));
        Check("a starter theme has lower-third and side-third lyric designs", theme is not null);
        if (theme is null) return;
        var lowerId = theme.Slides!.First(s => s.Id.EndsWith(".lyricsLower", StringComparison.Ordinal)).Id;
        var sideId = theme.Slides!.First(s => s.Id.EndsWith(".lyricsSide", StringComparison.Ordinal)).Id;

        var sinkA = new CountingSink();
        var sinkB = new CountingSink();
        var factory = outputs.DeviceSinkFactory;
        outputs.DeviceSinkFactory = device => device switch
        {
            "ndi:MxU look test A" => sinkA,
            "ndi:MxU look test B" => sinkB,
            _ => factory?.Invoke(device),
        };
        var lower = config.AddScreen("Look test: lower third", 1280, 720)!;
        var side = config.AddScreen("Look test: side third", 1280, 720)!;
        try
        {
            config.AssignDevice(lower.Id, "ndi:MxU look test A", "Look test A");
            config.AssignDevice(side.Id, "ndi:MxU look test B", "Look test B");
            config.SetScreenLook(lower.Id, new SlideThemeLook(theme.Id, null, lowerId));
            config.SetScreenLook(side.Id, new SlideThemeLook(theme.Id, null, sideId));
            outputs.Apply();
            var song = RenderTestSong(theme);
            FireSlide(song, 0);
            await Delay(1800);

            var a = outputs.ShownScene(lower.Id);
            var b = outputs.ShownScene(side.Id);
            var frameA = LyricFrame(a, "Amazing grace");
            var frameB = LyricFrame(b, "Amazing grace");
            var projector = LyricFrame(model.Show.AnimatedScene(), "Amazing grace");
            Check("two screens show the live slide through different looks", frameA is not null && frameB is not null && frameA != frameB);
            Check("the lower-third look puts the words in the bottom third",
                frameA is { } fa && a is not null && fa.Y / a.CanvasSize.Height > 0.6);
            Check("the side-third look puts the words in a column",
                frameB is { } fb && b is not null && fb.Width / b.CanvasSize.Width < 0.5);
            Check("the projector still shows the slide as designed", projector is { Width: > 1000 });
            Check("both looks' builds count on the live slide", model.Show.SlideThemeOverrides.Count == 2);
            if (a is not null) await Snapshot("20-look-lower-third-scene", a);
            if (b is not null) await Snapshot("20-look-side-third-scene", b);
            if (sinkA.Last is { } pixelsA && sinkB.Last is { } pixelsB && sinkA.Width == 1280 && sinkB.Width == 1280)
            {
                Write(BitmapSource.Create(1280, 720, 96, 96, PixelFormats.Pbgra32, null, pixelsA, 1280 * 4), "20-look-lower-third-output");
                Write(BitmapSource.Create(1280, 720, 96, 96, PixelFormats.Pbgra32, null, pixelsB, 1280 * 4), "20-look-side-third-output");
                Check("each look's screen gets its own picture", !pixelsA.AsSpan().SequenceEqual(pixelsB));
            }
            else
            {
                Skip("look pictures from the outputs", "this PC's graphics-card capture gave no frames");
            }

            // A long lyric pages on the lower third (its design pages on click), but not on the projector.
            FireSlide(song, 1);
            await Delay(1800);
            var page1 = VisibleText(outputs.ShownScene(lower.Id));
            Check("a long lyric shows only its first page on the lower third",
                page1.StartsWith("Amazing grace", StringComparison.Ordinal) && page1.Length < LongLyric.Length * 0.7);
            Check("its pages count as clicks", model.Show.SlideClickCount >= 2);
            Check("the projector's design holds the whole lyric", VisibleText(model.Show.AnimatedScene()).Contains("As long as life endures", StringComparison.Ordinal));
            if (outputs.ShownScene(lower.Id) is { } before) await Snapshot("21-page-1", before);
            if (sinkA.Last is { } first && sinkA.Width == 1280)
                Write(BitmapSource.Create(1280, 720, 96, 96, PixelFormats.Pbgra32, null, first, 1280 * 4), "21-page-1-output");
            model.NextCommand.Execute(null);
            await Delay(1500);
            var page2 = VisibleText(outputs.ShownScene(lower.Id));
            Check("Next turns the lower third to the next page",
                page2.Length > 0 && page2 != page1 && !page2.StartsWith("Amazing grace", StringComparison.Ordinal) && LongLyric.Contains(page2.Split('\n')[0], StringComparison.Ordinal));
            if (outputs.ShownScene(lower.Id) is { } after) await Snapshot("21-page-2", after);
            if (sinkA.Last is { } second && sinkA.Width == 1280)
                Write(BitmapSource.Create(1280, 720, 96, 96, PixelFormats.Pbgra32, null, second, 1280 * 4), "21-page-2-output");
        }
        finally
        {
            model.ClearAllCommand.Execute(null);
            config.RemoveScreen(lower.Id);
            config.RemoveScreen(side.Id);
            outputs.Apply();
            outputs.DeviceSinkFactory = factory;
        }
        Check("removing the test screens drops their looks", model.Outputs.Config.ActiveLooks.Count == 0);
    }

    /// <summary>Average brightness (0–255) of a frame's pixels inside a rectangle.</summary>
    private static double Brightness(byte[] bgra, int width, int x0, int y0, int x1, int y1)
    {
        double total = 0;
        var count = 0;
        for (var y = y0; y < y1; y += 2)
            for (var x = x0; x < x1; x += 2)
            {
                var i = (y * width + x) * 4;
                total += (bgra[i] + bgra[i + 1] + bgra[i + 2]) / 3.0;
                count++;
            }
        return count == 0 ? 0 : total / count;
    }

    /// <summary>
    /// A slide with its own Fade White transition (1.6 s): halfway through, the program is white;
    /// once it lands, the new verse shows. The same rules decide every layer on every screen.
    /// </summary>
    private async Task LayerTransitionCheck()
    {
        if (model.Program is not { } feed || model.Library.All<Theme>().FirstOrDefault(t => t.Slides?.Any(s => s.Name == "Lyrics") == true) is not { } theme) return;
        var sink = new CountingSink();
        feed.Add(sink);
        try
        {
            var song = RenderTestSong(theme);
            FireSlide(song, 0);
            await Delay(1500);
            var startedAt = DateTime.UtcNow;
            FireSlide(song, 2);
            double peak = 0;
            byte[]? peakFrame = null;
            while (DateTime.UtcNow - startedAt < TimeSpan.FromSeconds(1.1))
            {
                await Delay(40);
                if (sink.Last is { } frame && sink.Width > 0)
                {
                    var brightness = Brightness(frame, sink.Width, 0, 0, sink.Width, sink.Height);
                    if (brightness > peak)
                    {
                        peak = brightness;
                        peakFrame = frame;
                    }
                }
            }
            await Delay(1500);
            if (peakFrame is not null && sink.Last is { } landed)
            {
                Write(BitmapSource.Create(sink.Width, sink.Height, 96, 96, PixelFormats.Pbgra32, null, peakFrame, sink.Width * 4), "22-transition-fade-white-middle");
                Write(BitmapSource.Create(sink.Width, sink.Height, 96, 96, PixelFormats.Pbgra32, null, landed, sink.Width * 4), "22-transition-landed");
                Check("a slide's own transition plays on the outputs (Fade White passes through white)", peak > 200);
                Check("the transition lands on the new slide", Brightness(landed, sink.Width, 0, 0, sink.Width, sink.Height) < 120
                    && AudienceText().Contains("Was blind", StringComparison.Ordinal));
            }
            else
            {
                Skip("per-slide transition on the outputs", "this PC's graphics-card capture gave no frames");
            }
        }
        finally
        {
            feed.Remove(sink);
        }
    }

    /// <summary>
    /// Echo: a dot that jumps from the left to the right must leave a fading copy on the left.
    /// Ghost Trails: a still word must smoke (many soft pixels around it, where the plain word has few).
    /// </summary>
    private async Task TrailsCheck()
    {
        if (model.Program is not { } feed) return;
        Slide Dot(string id, TextHorizontalAlignment align, List<Effect>? effects) => new()
        {
            Id = id, Name = "Echo",
            Objects =
            [
                new SlideObject
                {
                    Id = "selftest-dot", ObjectKind = SlideObjectKind.Text, Name = "Dot", Text = "●",
                    X = 160, Y = 290, Width = 1600, Height = 500, Effects = effects,
                    TextStyle = new TextStyle { FontSize = 320, ColorHex = "#FFFFFFFF", HorizontalAlignment = align, VerticalAlignment = TextVerticalAlignment.Middle },
                },
            ],
        };
        Slide Word(string id, List<Effect>? effects) => new()
        {
            Id = id, Name = "Ghost",
            Objects =
            [
                new SlideObject
                {
                    Id = "selftest-word", ObjectKind = SlideObjectKind.Text, Name = "Word", Text = "GHOST",
                    X = 260, Y = 340, Width = 1400, Height = 400, Effects = effects,
                    TextStyle = new TextStyle { FontSize = 220, ColorHex = "#FFFFFFFF", HorizontalAlignment = TextHorizontalAlignment.Center },
                },
            ],
        };
        var deck = new Presentation { Id = "selftest-trails", Name = "Trails", PresentationKind = PresentationKind.Deck, Slides = [] };
        void Show(Slide slide)
        {
            model.Show.Fire(slide, deck);
            model.RenderOutputs(SceneTransition.Cut);
        }

        var sink = new CountingSink();
        feed.Add(sink);
        try
        {
            // The trails are blended by a pixel shader. A capture that draws in software (no graphics
            // card, or a fallback capture) can't show shader effects, so first check one shows at all.
            Show(new Slide
            {
                Id = "selftest-trails-probe", Name = "Probe",
                Objects =
                [
                    new SlideObject
                    {
                        Id = "probe", ObjectKind = SlideObjectKind.Shape, Name = "Box", Text = "", X = 0, Y = 0, Width = 1920, Height = 1080,
                        Fill = new ObjectFill { FillKind = FillKind.Solid, ColorHex = "#FFFFFFFF" },
                        Effects = [new Effect { EffectKind = EffectKind.Invert }],
                    },
                ],
            });
            await Delay(700);
            if (sink.Last is not { } probe || sink.Width == 0)
            {
                Skip("Echo and Ghost Trails on the outputs", "this PC's graphics-card capture gave no frames");
                return;
            }
            var middle = (sink.Height / 2 * sink.Width + sink.Width / 2) * 4;
            if (probe[middle] > 40 || probe[middle + 1] > 40 || probe[middle + 2] > 40)
            {
                Skip("Echo and Ghost Trails on the outputs", "the program capture doesn't show pixel-shader effects on this PC");
                return;
            }

            List<Effect> echo = [new Effect { EffectKind = EffectKind.Echo, Amount = 2 }];
            Show(Dot("selftest-echo-left", TextHorizontalAlignment.Left, echo));
            await Delay(900);
            Show(Dot("selftest-echo-right", TextHorizontalAlignment.Right, echo));
            await Delay(250);
            var withEcho = sink.Last;
            var width = sink.Width;
            Show(Dot("selftest-plain-left", TextHorizontalAlignment.Left, null));
            await Delay(900);
            Show(Dot("selftest-plain-right", TextHorizontalAlignment.Right, null));
            await Delay(250);
            var withoutEcho = sink.Last;
            if (withEcho is not null && withoutEcho is not null && width > 0)
            {
                var height = withEcho.Length / 4 / width;
                // The dot's left place: the first fifth of the canvas, across the middle band.
                double Left(byte[] frame) => Brightness(frame, width, width * 10 / 100, height * 35 / 100, width * 25 / 100, height * 65 / 100);
                double Right(byte[] frame) => Brightness(frame, width, width * 75 / 100, height * 35 / 100, width * 90 / 100, height * 65 / 100);
                Write(BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, withEcho, width * 4), "23-echo");
                Write(BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, withoutEcho, width * 4), "23-echo-off");
                Check($"Echo leaves a fading copy where the dot was (left {Left(withEcho):0} with echo, {Left(withoutEcho):0} without)",
                    Left(withEcho) > Left(withoutEcho) + 15 && Right(withEcho) > 30);
            }
            else
            {
                Skip("Echo on the outputs", "this PC's graphics-card capture gave no frames");
            }

            Show(Word("selftest-ghost-plain", null));
            await Delay(700);
            var plain = sink.Last;
            Show(Word("selftest-ghost", [new Effect { EffectKind = EffectKind.GhostTrails, Fade = 1.5, Amount = 10, Scale = 140, Speed = 1 }]));
            await Delay(1500);
            var ghosted = sink.Last;
            Check("ghost trails keep a history while they're on screen", TrailStore.Count > 0);
            if (plain is not null && ghosted is not null && width > 0)
            {
                var height = ghosted.Length / 4 / width;
                // Soft pixels: neither black nor full white. A ghost's smoke is made of them.
                int Soft(byte[] frame)
                {
                    var soft = 0;
                    for (var i = 0; i < frame.Length; i += 16)
                    {
                        var luma = (frame[i] + frame[i + 1] + frame[i + 2]) / 3;
                        if (luma is > 20 and < 200) soft++;
                    }
                    return soft;
                }
                Write(BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, ghosted, width * 4), "23-ghost-trails");
                Write(BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, plain, width * 4), "23-ghost-trails-off");
                Check($"Ghost Trails smoke the word ({Soft(ghosted)} soft pixels, {Soft(plain)} without)", Soft(ghosted) > Soft(plain) * 2 + 200);
            }
            else
            {
                Skip("Ghost Trails on the outputs", "this PC's graphics-card capture gave no frames");
            }
        }
        finally
        {
            feed.Remove(sink);
        }
    }
}
