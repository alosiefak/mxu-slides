using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Rendering;
using MxU.Slides.Windows.ViewModels;
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
            var added = await GalleryWindowCheck(camera);
            await OverlayPresetsCheck(added);
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
    /// Two stand-in pictures to judge designs against (Views/StandInPictures.cs): a worship
    /// background (dark, with coloured haze and light beams, for the full-screen lyrics) and a
    /// camera shot of someone on stage (for lower thirds on the stream). Imported as ordinary media
    /// so the outputs draw them.
    /// </summary>
    private async Task<(MediaItem Worship, MediaItem Camera)> LooksBackdrops()
    {
        var folder = Path.Combine(Path.GetTempPath(), "MxU self-test looks " + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        MediaItem Import(string name, Action<DrawingContext> paint)
        {
            var path = Path.Combine(folder, name + ".png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(StandInPictures.Render(paint)));
            using (var file = File.Create(path)) encoder.Save(file);
            var item = model.Library.ImportMedia(path, MediaClassification.Background)!;
            item.Name = name;
            model.Library.Save(item);
            return item;
        }
        var worship = Import("Self-test worship", StandInPictures.PaintWorship);
        var camera = Import("Self-test camera", dc => StandInPictures.PaintStage(dc, speaker: true));
        // The library keeps its own copies; the drawn files aren't needed any more.
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        foreach (var id in new[] { worship.Id, camera.Id })
            for (var i = 0; i < 60 && (model.Media.Image(id, 1280) is null || model.Media.Image(id, 3840) is null); i++) await Delay(100);
        return (worship, camera);
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
    private static bool FullScreen(LowerThirdTemplate template) => LowerThirdGalleryViewModel.FillsTheScreen(template.Make());

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

    /// <summary>
    /// The gallery window, as an operator uses it: pick a design, type a name, add it to the
    /// library, put it up, fix the name while it's up, take it down; fill song credits from the
    /// song; start a countdown for a countdown design. Returns the overlay it added.
    /// </summary>
    private async Task<Overlay?> GalleryWindowCheck(MediaItem camera)
    {
        var gallery = LowerThirdGalleryWindow.Open(window, model);
        var vm = gallery.Model;
        Overlay? speaker = null;
        try
        {
            await Delay(900);
            Save(gallery, "64-gallery-window");
            Check("the gallery shows every design", vm.Visible.Count == LowerThirdGallery.All.Count && LowerThirdGallery.All.Count >= 20);
            vm.Category = "Speaker";
            Check("the gallery filters by what a design is for", vm.Visible.Count >= 3 && vm.Visible.All(t => t.Category == "Speaker"));
            vm.Category = LowerThirdGalleryViewModel.AllCategories;

            gallery.Pick(LowerThirdGallery.All.Single(t => t.Id == "gallery.speaker.broadcast"));
            vm.SetField("Name", "Pastor Adaeze Nwosu");
            vm.SetField("Title", "Teaching Pastor");
            await Delay(2600);
            Save(gallery, "64-gallery-window-speaker");
            Check("the pack box shows the pack it goes into", vm.Pack == LowerThirdGallery.DefaultPack && vm.Packs.Contains(vm.Pack));
            Check("typing into a draft's field changes only the preview", !model.Library.All<Overlay>().Any(o => o.Objects.Any(x => x.Text == "Pastor Adaeze Nwosu")));

            speaker = vm.AddToLibrary();
            Check($"Add to library saves it as an overlay in the Lower Thirds pack, under Speaker (folder {speaker?.Folder ?? "none"}, bin showing {model.SelectedOverlayPack})",
                speaker is { Folder: "Lower Thirds/Speaker" } && model.Library.Get<Overlay>(speaker.Id) is not null && model.Overlays.Any(t => t.Overlay.Id == speaker.Id));
            if (speaker is null) return null;

            model.Show.Fire(new CueMedia { MediaId = camera.Id, Layer = CueMediaLayer.LoopingVideos }, LayerKind.LoopingVideos);
            vm.GoLive();
            await Delay(1600);
            Check("Go live puts it up on the program", model.IsOverlayLive(speaker.Id) && VisibleAudienceText().Contains("Pastor Adaeze Nwosu"));
            await Snapshot("64-gallery-live-program", model.ProgramScene());
            foreach (var t in new[] { 0.15, 0.35, 0.7 })
                await SnapshotAt($"64-gallery-live-in-{t:0.00}s", model.ProgramAnimatedScene(), t);
            vm.SetField("Name", "Pastor Adaeze Nwosu-Bello");
            Check("fixing the name while it's up fixes it on the program", AudienceText().Contains("Pastor Adaeze Nwosu-Bello") && !model.Show.IsLeaving(speaker.Id));
            await Snapshot4K("64-gallery-live-4k", model.ProgramScene());
            vm.TakeDown();
            Check("Take down plays its Out", model.Show.IsLeaving(speaker.Id));
            await Delay(1200);
            Check("…and then it's gone", !AudienceText().Contains("Adaeze Nwosu"));

            // Song credits from the song that's open (the sample song has a title and writer, no CCLI number).
            gallery.Pick(LowerThirdGallery.All.Single(t => t.Id == "gallery.credits.clean"));
            var song = model.Library.Get<Presentation>(StarterContent.SampleSongId)!;
            model.Show.Fire(SlideSceneBuilder.ArrangedSlides(song, song.DefaultArrangementId)[1], song, model.ThemeFor(song.Slides[0], song));
            vm.FillFromSong();
            Check("song credits fill from the open song", vm.Working?.Objects.FirstOrDefault(o => o.Name == "Song Title")?.Text == "Amazing Grace"
                && vm.Working.Objects.First(o => o.Name == "Writers").Text.Contains("John Newton"));

            // A name typed into a word-by-word design still arrives word by word.
            gallery.Pick(LowerThirdGallery.All.Single(t => t.Id == "gallery.speaker.glass"));
            vm.SetField("Name", "Grace Okafor");
            var name = vm.Working!.Objects.Single(o => o.Name == "Name");
            Check("a word-by-word name follows the words typed in", name.AnimationSteps!.Count(s => s.Kind == AnimationKind.In) == 2
                && name.AnimationSteps!.Where(s => s.Kind == AnimationKind.In).Select(s => AnimationRanges.Text(s.Ranges!.Single(), name.Text)).SequenceEqual(["Grace", "Okafor"]));

            // A countdown follows the timer it's started with.
            gallery.Pick(LowerThirdGallery.All.Single(t => t.Id == "gallery.countdown.corner"));
            vm.StartCountdown(5);
            var countdown = vm.AddToLibrary();
            vm.GoLive();
            await Delay(1500);
            Check("a countdown design counts down its timer on the program", countdown is not null && TextOf(model.ProgramScene()).Contains("04:5", StringComparison.Ordinal));
            await Snapshot("64-gallery-countdown-program", model.ProgramScene());
            Save(gallery, "64-gallery-window-countdown");
            Check("the timer picker follows the countdown just started", vm.TimerChoice == "Countdown 5:00");
            vm.TakeDown();
            if (vm.TimerChoices.Count > 1 && model.Timers.Timers.FirstOrDefault(t => t.Name == "Countdown 5:00") is { } timer) model.Timers.Remove(timer.Id);
            await Delay(800);

            // A full-screen title previews over the worship stand-in; the Welcome shelf.
            vm.Category = "Welcome";
            gallery.Pick(LowerThirdGallery.All.Single(t => t.Id == "gallery.welcome.editorial"));
            await Delay(2200);
            Save(gallery, "64-gallery-window-welcome");
            vm.Category = LowerThirdGalleryViewModel.AllCategories;
        }
        catch (Exception ex)
        {
            failures++;
            report.AppendLine($"FAIL  the gallery window crashed: {ex}");
        }
        finally
        {
            gallery.Close();
            model.ClearAllCommand.Execute(null);
        }
        return speaker;
    }

    /// <summary>The overlay editor's presets: kinetic text on a name, the Mac's presets, one step per word, saving a preset, look effects.</summary>
    private async Task OverlayPresetsCheck(Overlay? overlay)
    {
        if (overlay is null) return;
        var editor = OverlayEditorWindow.Open(window, model, overlay.Id);
        var vm = editor.Model;
        try
        {
            await Delay(800);
            vm.ShowingAnimationTab = true;
            var name = overlay.Objects.Single(o => o.Name == "Name");
            var title = overlay.Objects.Single(o => o.Name == "Title");
            var plate = overlay.Objects.Single(o => o.Name == "Plate");
            vm.SelectObjects([name.Id]);
            Check("the presets list kinetic text, the Mac's recommended set", vm.PresetChoices.Count(p => p.Title.StartsWith("Kinetic text", StringComparison.Ordinal)) >= 12
                && vm.PresetChoices.Any(p => p.Title == "Recommended › Settle · In + Out"));
            vm.SelectedPreset = vm.PresetChoices.First(p => p.Title == "Kinetic text › Word by word: rise");
            vm.ApplyPresetCommand.Execute("replace");
            var ins = name.AnimationSteps!.Where(s => s.Kind == AnimationKind.In).ToList();
            Check("a kinetic preset gives each word its own step, playing as the overlay goes up", ins.Count == 3 && ins.All(s => s.Ranges is { Count: 1 })
                && AnimationSequence.Groups(overlay.Objects, overlay.AnimationOrder).Click.Count == 0 && vm.StepRows.All(r => r.Warning is null));

            vm.SelectObjects([title.Id]);
            vm.GalleryKindIndex = 0;
            vm.GenerateStepsCommand.Execute("word");
            Check("one step per word splits the title into its words", title.AnimationSteps!.Count(s => s.Ranges is { Count: 1 } && s.Kind == AnimationKind.In) == 2
                && AnimationSequence.Groups(overlay.Objects, overlay.AnimationOrder).Click.Count == 0);

            vm.SelectObjects([plate.Id]);
            vm.SelectedPreset = vm.PresetChoices.First(p => p.Title == "Recommended › Blur Resolve · In + Out");
            vm.ApplyPresetCommand.Execute("replace");
            Check("a Mac preset replaces the plate's steps", plate.AnimationSteps?.Count == 2 && plate.AnimationSteps.All(s => s.Animation == StepAnimation.Blur));
            Check("Save as preset puts it on the animation preset board", vm.SavePreset("Self-test blur")
                && model.Library.Get<AnimationPresetBoard>(AnimationPresetBoard.WellKnownId)?.Presets.Any(p => p.Name == "Self-test blur") == true
                && vm.PresetChoices.Any(p => p.Title == "My presets › Self-test blur"));
            vm.SelectedEffect = vm.EffectChoices.First(e => e.Title == "Frost · Light");
            vm.ApplyEffectCommand.Execute("replace");
            Check("a look effect lands on the plate", plate.Effects is [{ EffectKind: EffectKind.Scatter }]);
            vm.ApplyEffectCommand.Execute("clear");
            Check("a preset you save is on the gallery's Mine shelf", editor.PresetGallery.Shelf == PresetChoice.Mine);

            // The gallery as you'd see it: kinetic text, one tile picked and playing on its sample.
            vm.SelectObjects([name.Id]);
            vm.SelectedStepRow = null;
            vm.SelectedPreset = vm.PresetChoices.First(p => p.Title == "Kinetic text › Word by word: blur in");
            Check("picking a kinetic preset turns the gallery to its shelf", editor.PresetGallery.Shelf == PresetChoice.Kinetic);
            await Delay(500);
            editor.AnimationScroll.ScrollToTop();
            await Delay(300);
            Save(editor, "65-overlay-editor-presets");
            editor.PresetGallery.Play(vm.SelectedPreset);
            await Delay(650);
            Save(editor, "65-overlay-editor-presets-tile-playing");
            editor.PresetGallery.Shelf = PresetChoice.Recommended;
            await Delay(500);
            Save(editor, "65-overlay-editor-presets-recommended");
            vm.PlayPreviewCommand.Execute(null);
            await Delay(450);
            Save(editor, "65-overlay-editor-presets-preview");
            await Delay((int)(vm.PreviewDuration * 1000) + 900);
        }
        catch (Exception ex)
        {
            failures++;
            report.AppendLine($"FAIL  the overlay editor's presets crashed: {ex}");
        }
        finally
        {
            editor.Close();
        }
    }

    /// <summary>A scene drawn at 3840 × 2160, saved whole and as a 1280 × 720 crop of its lower-left (to judge sharpness at 4K).</summary>
    private async Task Snapshot4K(string name, RenderScene scene)
    {
        var view = new SceneView { Media = model.Media, PlaysVideo = false, Width = 3840, Height = 2160 };
        view.Measure(new Size(3840, 2160));
        view.Arrange(new Rect(0, 0, 3840, 2160));
        view.Present(AnimatedScene.From(scene), Rendering.SceneTransition.Cut);
        await System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        var bitmap = new RenderTargetBitmap(3840, 2160, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        Write(bitmap, name);
        Write(new CroppedBitmap(bitmap, new Int32Rect(160, 1360, 1280, 720)), name + "-crop");
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
                // The digits follow the running timer (the first one running, when earlier checks left
                // others going), as minutes and seconds; a second either side allows for the tick.
                var primary = Core.Stage.ConfidenceSceneBuilder.PrimaryTimer(model.Timers.Timers);
                var now = DateTimeOffset.Now;
                var expected = primary is null ? [] : new[] { -1, 0, 1 }.Select(s => primary.DisplayString(now.AddSeconds(s), null, "MM:ss")).ToList();
                Check($"the {pack.Title()} countdown shows the running timer's minutes and seconds ({primary?.Name}: {string.Join(" / ", expected)})",
                    primary is { IsLive: true } && expected.Any(e => text.Contains(e, StringComparison.Ordinal))
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
