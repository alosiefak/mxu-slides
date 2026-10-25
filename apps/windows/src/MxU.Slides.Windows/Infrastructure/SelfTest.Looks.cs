using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Rendering;
using MxU.Slides.Windows.Views;

namespace MxU.Slides.Windows.Infrastructure;

// Phase 6 looks: the bundled fonts (each face really is the weight its name says), the four
// worship looks and their kinetic lyrics, the pre-service countdown, the lower-thirds gallery and
// the overlay editor's presets. Most of this area is about how things look, so it saves a picture
// of every design, settled and part-way through its animation, over a stand-in camera shot and a
// worship background (pictures 60-… to 69-…).
public sealed partial class SelfTest
{
    private async Task LooksCheck()
    {
        try
        {
            await BundledFontsCheck();
            var (worship, camera) = await LooksBackdrops();
            await WorshipLooksCheck(worship, camera);
            await CountdownCheck(worship);
            await GalleryDesignsCheck(worship, camera);
        }
        catch (Exception ex)
        {
            failures++;
            report.AppendLine($"FAIL  the looks checks crashed: {ex}");
        }
        finally
        {
            model.ClearAllCommand.Execute(null);
        }
    }

    // MARK: Fonts

    private async Task BundledFontsCheck()
    {
        var shipped = BundledFonts.Files(BundledFonts.ShippedFolder);
        Check("the 15 bundled fonts ship beside the app", shipped.Count == 15
            && File.Exists(Path.Combine(BundledFonts.ShippedFolder, BundledFonts.LicenceFileName))
            && File.Exists(Path.Combine(BundledFonts.ShippedFolder, BundledFonts.NoticeFileName)));
        var stored = FontFiles.Stored(model.Library.Root).Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Check("the library's fonts folder has every bundled font (named by its hash)", shipped.All(f =>
            stored.Contains(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))))));

        // WPF must land on the exact face: Montserrat Black really Black, not a Regular made bold.
        var problems = new List<string>();
        var specimen = new RenderScene(SceneSize.Hd, new SceneColor(0.06, 0.06, 0.07));
        var row = 0;
        foreach (var file in shipped)
        {
            var face = FontFiles.Faces(File.ReadAllBytes(file))[0];
            var wantStyle = face.Italic ? FontStyles.Italic : FontStyles.Normal;
            foreach (var name in new[] { face.PostScriptName, face.FullName })
            {
                var resolved = FontMap.Resolve(name);
                if (!resolved.Typeface.TryGetGlyphTypeface(out var glyphs))
                {
                    problems.Add($"{name}: no glyphs");
                    continue;
                }
                var family = glyphs.FamilyNames.Values.FirstOrDefault() ?? "";
                if (glyphs.Weight.ToOpenTypeWeight() != face.Weight || glyphs.Style != wantStyle || glyphs.StyleSimulations != StyleSimulations.None
                    || !family.Equals(face.Family, StringComparison.OrdinalIgnoreCase))
                    problems.Add($"{name} → {family} {glyphs.Weight} {glyphs.Style} {glyphs.StyleSimulations}");
            }
            specimen.AddItem(new RenderItem
            {
                Id = $"specimen-{row}", Frame = new SceneRect(80, 40 + row * 66, 1760, 64),
                Content = new ItemContent.Text(new StyledText
                {
                    String = $"{face.FullName}  ·  Amazing grace, how sweet the sound 0123456789",
                    FontName = face.PostScriptName, FontSize = 40, Color = SceneColor.White,
                    Alignment = SceneTextAlignment.Left, VerticalAlignment = SceneTextVerticalAlignment.Middle,
                }),
            }, LayerKind.Slide);
            row++;
        }
        Check("each bundled face resolves to itself by PostScript and full name (weight, style, no simulation)" + (problems.Count > 0 ? ": " + string.Join("; ", problems) : ""),
            problems.Count == 0);
        // The editor's bold and italic toggles ask for "Family-Bold" / "Family-Italic".
        Check("bold and italic toggles land on the real faces", FontMap.Resolve(FontMap.WithTraits("Poppins", bold: true, italic: false)).Typeface.TryGetGlyphTypeface(out var bold)
            && bold.Weight == FontWeights.Bold && bold.StyleSimulations == StyleSimulations.None
            && FontMap.Resolve(FontMap.WithTraits("DMSerifDisplay-Regular", bold: false, italic: true)).Typeface.TryGetGlyphTypeface(out var italic)
            && italic.Style == FontStyles.Italic && italic.StyleSimulations == StyleSimulations.None);
        await Snapshot("60-looks-fonts", specimen);

        // The credits under Settings › About, and the full licence.
        var about = new Window
        {
            Title = "About", Width = 640, SizeToContent = SizeToContent.Height, Background = EditorKit.Res<Brush>("BasePlane"),
            Content = new Border { Padding = new Thickness(20), Child = new AboutSection() }, Owner = window, ShowInTaskbar = false,
        };
        about.Show();
        await Delay(500);
        Save(about, "60-looks-font-credits");
        about.Close();
        var licences = FontCreditsWindow.Open(window);
        await Delay(500);
        Check("Font licences shows each copyright and the OFL", FontCreditsWindow.NoticeText().Contains("The Montserrat Project Authors")
            && FontCreditsWindow.NoticeText().Contains("SIL OPEN FONT LICENSE Version 1.1"));
        Save(licences, "60-looks-font-licences");
        licences.Close();
    }

    // MARK: Backdrops

    /// <summary>
    /// Two stand-in pictures to judge designs against: a worship background (dark, with coloured
    /// haze and light beams, for the full-screen lyrics) and a camera shot of someone on stage (for
    /// lower thirds on the stream). Imported as ordinary media so the outputs draw them.
    /// </summary>
    private async Task<(MediaItem Worship, MediaItem Camera)> LooksBackdrops()
    {
        var folder = Path.Combine(Path.GetTempPath(), "MxU self-test looks " + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        MediaItem Import(string name, Action<DrawingContext> paint)
        {
            var path = Path.Combine(folder, name + ".png");
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) paint(dc);
            var bitmap = new RenderTargetBitmap(1920, 1080, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(path)) encoder.Save(file);
            var item = model.Library.ImportMedia(path, MediaClassification.Background)!;
            item.Name = name;
            model.Library.Save(item);
            return item;
        }
        var worship = Import("Self-test worship", PaintWorship);
        var camera = Import("Self-test camera", PaintCamera);
        foreach (var id in new[] { worship.Id, camera.Id })
            for (var i = 0; i < 60 && (model.Media.Image(id, 1280) is null || model.Media.Image(id, 3840) is null); i++) await Delay(100);
        return (worship, camera);
    }

    private static void PaintWorship(DrawingContext dc)
    {
        var rect = new Rect(0, 0, 1920, 1080);
        dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x08, 0x0B, 0x1C), Color.FromRgb(0x1C, 0x0C, 0x2A), 90), null, rect);
        void Glow(Color color, double x, double y, double rx, double ry) =>
            dc.DrawRectangle(new RadialGradientBrush(color, Colors.Transparent) { Center = new Point(x, y), GradientOrigin = new Point(x, y), RadiusX = rx, RadiusY = ry }, null, rect);
        Glow(Color.FromArgb(0x90, 0x5B, 0x3D, 0xF5), 0.25, 0.3, 0.5, 0.6);
        Glow(Color.FromArgb(0x80, 0xF5, 0x6B, 0x3D), 0.8, 0.75, 0.45, 0.55);
        Glow(Color.FromArgb(0x60, 0x2D, 0xB4, 0xF5), 0.6, 0.15, 0.35, 0.4);
        // Light beams from the rig.
        foreach (var (x, tilt, alpha) in new[] { (380.0, 220.0, 0x38), (960.0, -60.0, 0x30), (1540.0, -260.0, 0x34) })
        {
            var beam = new StreamGeometry();
            using (var g = beam.Open())
            {
                g.BeginFigure(new Point(x - 18, -10), true, true);
                g.LineTo(new Point(x + 18, -10), true, false);
                g.LineTo(new Point(x + tilt + 220, 1090), true, false);
                g.LineTo(new Point(x + tilt - 220, 1090), true, false);
            }
            dc.DrawGeometry(new LinearGradientBrush(Color.FromArgb((byte)alpha, 0xFF, 0xF2, 0xE0), Colors.Transparent, 90), null, beam);
        }
        var random = new Random(7);
        for (var i = 0; i < 38; i++)
        {
            var r = 8 + random.NextDouble() * 34;
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(18 + random.Next(30)), 0xFF, 0xE8, 0xC8)), null,
                new Point(random.NextDouble() * 1920, random.NextDouble() * 1080), r, r);
        }
        dc.DrawRectangle(new RadialGradientBrush(Colors.Transparent, Color.FromArgb(0xA0, 0, 0, 0)) { RadiusX = 0.85, RadiusY = 0.85 }, null, rect);
    }

    private static void PaintCamera(DrawingContext dc)
    {
        var rect = new Rect(0, 0, 1920, 1080);
        // A stage: warm back wall, a darker floor, practical lights out of focus.
        dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x2A, 0x22, 0x1E), Color.FromRgb(0x14, 0x10, 0x10), 90), null, rect);
        dc.DrawRectangle(new RadialGradientBrush(Color.FromArgb(0xB0, 0xE9, 0x9A, 0x55), Colors.Transparent)
            { Center = new Point(0.72, 0.35), GradientOrigin = new Point(0.72, 0.35), RadiusX = 0.5, RadiusY = 0.6 }, null, rect);
        dc.DrawRectangle(new RadialGradientBrush(Color.FromArgb(0x70, 0x4F, 0x7C, 0xD9), Colors.Transparent)
            { Center = new Point(0.12, 0.2), GradientOrigin = new Point(0.12, 0.2), RadiusX = 0.4, RadiusY = 0.5 }, null, rect);
        var random = new Random(3);
        for (var i = 0; i < 26; i++)
        {
            var r = 14 + random.NextDouble() * 40;
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(26 + random.Next(40)), 0xFF, 0xD9, 0xA0)), null,
                new Point(random.NextDouble() * 1920, random.NextDouble() * 560), r, r);
        }
        // Someone speaking, a little left of centre (where a camera operator frames them).
        var skin = new SolidColorBrush(Color.FromRgb(0x8A, 0x5E, 0x46));
        var shirt = new LinearGradientBrush(Color.FromRgb(0x2C, 0x3E, 0x58), Color.FromRgb(0x18, 0x22, 0x33), 90);
        dc.DrawRoundedRectangle(shirt, null, new Rect(640, 620, 640, 520), 220, 220);
        dc.DrawRectangle(skin, null, new Rect(905, 520, 110, 130));
        dc.DrawEllipse(skin, null, new Point(960, 420), 118, 148);
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x24, 0x18, 0x12)), null, new Point(960, 312), 124, 70);
        dc.DrawRectangle(new LinearGradientBrush(Colors.Transparent, Color.FromArgb(0x90, 0, 0, 0), 90), null, new Rect(0, 640, 1920, 440));
        dc.DrawRectangle(new RadialGradientBrush(Colors.Transparent, Color.FromArgb(0x70, 0, 0, 0)) { RadiusX = 0.9, RadiusY = 0.9 }, null, rect);
    }

    /// <summary>A still of <paramref name="scene"/> with <paramref name="media"/> full screen underneath (on the background layer).</summary>
    private static RenderScene Over(RenderScene scene, MediaItem media)
    {
        scene.AddItem(new RenderItem
        {
            Id = $"looks-backdrop-{media.Id}", Frame = new SceneRect(scene.CanvasSize),
            Content = new ItemContent.Media(media.Id, SceneMediaScaleMode.Fill),
        }, LayerKind.LoopingVideos);
        return scene;
    }

    // MARK: Worship looks

    /// <summary>A slide that shows a design with its own sample words (what the Mac's theme previews show).</summary>
    private static Slide SampleSlide(Slide design) => new()
    {
        Id = $"looks-sample-{design.Id}", Name = design.Name, ThemeSlideName = design.Name,
        Objects = [.. design.Objects.Where(o => o.ObjectKind == SlideObjectKind.Text && o.TextLink is null)
            .Select(o => new SlideObject { Id = $"looks-{o.Id}", ObjectKind = SlideObjectKind.Text, Name = o.Name, Text = o.Text })],
    };

    private async Task WorshipLooksCheck(MediaItem worship, MediaItem camera)
    {
        foreach (var pack in StarterPacks.All.Where(p => p.IsWorshipLook()))
        {
            var theme = model.Library.Get<Theme>(pack.ThemeId());
            Check($"the {pack.Title()} look is in the library", theme is not null && model.Themes.Any(t => t.Id == pack.ThemeId()));
            if (theme is null) continue;
            foreach (var design in theme.Slides ?? [])
            {
                var slide = SampleSlide(design);
                var backdrop = design.Folder == "Full Slide" ? worship : camera;
                await Snapshot($"61-look-{Safe(pack.Title())}-{Safe(design.Name)}", Over(SlideSceneBuilder.Scene(slide, theme, pager: TextLayout.Pager), backdrop));
            }
        }

        // The kinetic lyrics, live: Arena's lines rise in one after another (a Windows extra beside the theme).
        var arena = model.Library.Get<Theme>(StarterPack.Arena.ThemeId())!;
        var song = model.Library.Get<Presentation>(StarterContent.SampleSongId)!;
        var verse = SlideSceneBuilder.ArrangedSlides(song, song.DefaultArrangementId)[1];
        model.Show.Fire(new CueMedia { MediaId = worship.Id, Layer = CueMediaLayer.LoopingVideos }, LayerKind.LoopingVideos);
        model.Show.Fire(verse, song, arena);
        model.RenderOutputs(Rendering.SceneTransition.Cut);
        var lyric = model.Show.AnimatedScene().Layers.SelectMany(l => l.Items).FirstOrDefault(i => i.Render.Content is ItemContent.Text t
            && t.Styled.String.Contains("Amazing grace", StringComparison.OrdinalIgnoreCase));
        var ranged = lyric?.AnimationSteps.Count(s => s.Kind == SceneAnimationKind.Enter && s.Ranges is { Count: 1 }) ?? 0;
        Check("Arena's lyrics rise in line by line on Windows (one ranged step per line)", ranged >= 2);
        foreach (var t in new[] { 0.12, 0.3, 0.5, 0.8, 1.4 })
            await SnapshotAt($"61-look-Arena-kinetic-{t:0.00}s", model.Show.AnimatedScene(), t);
        var serif = model.Library.Get<Theme>(StarterPack.ModernSerif.ThemeId())!;
        model.Show.Fire(verse, song, serif);
        model.RenderOutputs(Rendering.SceneTransition.Cut);
        foreach (var t in new[] { 0.2, 0.5, 0.9 })
            await SnapshotAt($"61-look-ModernSerif-kinetic-{t:0.00}s", model.Show.AnimatedScene(), t);
        model.ClearAllCommand.Execute(null);
    }

    // MARK: The lower-thirds gallery's designs

    /// <summary>The designs that fill the screen (titles, countdowns) go over the worship background; the rest over the camera.</summary>
    private static bool FullScreen(LowerThirdTemplate template) =>
        template.Id is "gallery.welcome.editorial" or "gallery.soon.full" or "gallery.countdown.center";

    /// <summary>
    /// Every gallery design as the program draws it: part-way in, settled, and on its way out,
    /// over a stand-in camera shot (or the worship background for full-screen titles).
    /// </summary>
    private async Task GalleryDesignsCheck(MediaItem worship, MediaItem camera)
    {
        foreach (var template in LowerThirdGallery.All)
        {
            var overlay = model.WithLiveText(template.Make());
            var backdrop = Over(new RenderScene(SceneSize.Hd), FullScreen(template) ? worship : camera);
            var timeline = AnimationSequence.PreviewTimeline(overlay.Objects, overlay.AnimationOrder);
            var name = $"63-gallery-{Safe(template.Category)}-{Safe(template.StyleName)}-{Safe(template.Name)}";
            Task Frame(string suffix, double t) => Snapshot(suffix.Length == 0 ? name : $"{name}-{suffix}",
                OverlayPreview.PlayThrough(overlay, SceneSize.Hd, 0, t, Copy(backdrop)));
            await Frame("in-0.25s", 0.25);
            await Frame("in-0.60s", 0.6);
            await Frame("", Math.Max(0, timeline.DismissAt - 0.05));
            await Frame("out", timeline.DismissAt + 0.18);
        }
    }

    private static RenderScene Copy(RenderScene scene)
    {
        var copy = new RenderScene(scene.CanvasSize, scene.Background);
        foreach (var layer in scene.Layers)
            foreach (var item in layer.Items) copy.AddItem(item, layer.Kind);
        return copy;
    }

    // MARK: Countdown

    private async Task CountdownCheck(MediaItem worship)
    {
        var deck = model.Library.Get<Presentation>(StarterContent.CountdownPresentationId);
        Check("the Pre-Service Countdown deck is in the library", deck is { Slides.Count: 4 });
        if (deck is null) return;
        var timer = model.Timers.AddCountdown("Pre-service", 5 * 60);
        model.Timers.Start(timer);
        try
        {
            model.Show.Fire(new CueMedia { MediaId = worship.Id, Layer = CueMediaLayer.LoopingVideos }, LayerKind.LoopingVideos);
            foreach (var slide in deck.Slides)
            {
                model.Show.Fire(slide, deck, model.ThemeFor(slide, deck));
                model.RenderOutputs(Rendering.SceneTransition.Cut);
                await Delay(300);
                var text = TextOf(model.ProgramScene());
                var pack = StarterPacks.All.First(p => p.ThemeId() == slide.ThemeId);
                Check($"the {pack.Title()} countdown shows the running timer's minutes and seconds", text.Contains("04:5", StringComparison.Ordinal)
                    && text.Contains(slide.Objects[0].Text, StringComparison.Ordinal));
                await Snapshot($"62-countdown-{Safe(pack.Title())}", model.ProgramScene());
                await SnapshotAt($"62-countdown-{Safe(pack.Title())}-in-0.40s", model.ProgramAnimatedScene(), 0.4);
            }
        }
        finally
        {
            model.Timers.Remove(timer);
            model.ClearAllCommand.Execute(null);
        }
    }
}
