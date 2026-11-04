using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.MotionTemplates;
using MxU.Slides.Core.Scene;
using MxU.Slides.MotionTemplates.Export;
using MxU.Slides.Windows.Outputs;
using MxU.Slides.Windows.ViewModels;
using MxU.Slides.Windows.Views;
using static MxU.Slides.Core.MotionTemplates.LottieBuilder;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>
/// The Phase 11 motion-template checks (MainViewModel.MotionTemplates.cs, Views/MotionTemplatesWindow.cs):
/// a Lottie lower third written by the test is imported through the window; filled in and put up
/// over a lyric, where the program feed shows it with the slide through its transparent parts;
/// its words are changed while it's up; F4 plays its Out; it's exported to MP4; a full-screen
/// template plays live as a background while its video is what a Mac gets; the Mac's documents
/// hold only what the shared format knows; the bin's fields and hover preview work; the After
/// Effects window, with a stand-in After Effects when the tests have built one. The speed of a
/// 1080p template is measured and reported. Pictures are named "mt-…".
/// </summary>
public sealed partial class SelfTest
{
    private async Task MotionTemplatesCheck()
    {
        var sink = new CountingSink();
        model.Program?.Add(sink);
        var temp = Path.Combine(Path.GetTempPath(), $"MxU self-test motion {Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            var editor = await MotionWindowCheck(temp);
            if (editor is null) return;
            var overlay = await MotionLiveCheck(editor, sink);
            if (overlay is not null)
            {
                await MotionTakeDownCheck(overlay, sink);
                MotionMacCheck(overlay);
                await MotionBinCheck(overlay);
                await MotionEditWindowCheck(overlay);
            }
            var export = await MotionExportCheck(editor, temp);
            await MotionBackgroundCheck(editor);
            await MotionAfterEffectsCheck(temp, export);
        }
        finally
        {
            model.Program?.Remove(sink);
            foreach (var open in Application.Current.Windows.OfType<Window>().Where(w => w is MotionTemplatesWindow or AfterEffectsWindow or LottieGuideWindow).ToList()) open.Close();
            model.MotionTemplateJob?.Cancel();
            model.ClearAllCommand.Execute(null);
            model.ShowingOverlays = false;
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// The test's own Lottie lower third: a blue plate slides in from the left with a white name on
    /// it (In 0–30), holds (30–150), and slides back out (Out 150–180), at 60 fps.
    /// </summary>
    private static string WriteSampleLottie(string folder)
    {
        var b = new LottieBuilder("Self-test Strap", 1920, 1080, 60, 180)
            .Font("SegoeUI-Bold", "Segoe UI", "Bold")
            .Marker("In", 0, 30).Marker("Hold", 30, 120).Marker("Out", 150, 30);
        // The plate and the name move together: in from the left, and back out to the left.
        static JsonObject Slide(double x, double y) =>
            Animated(At(0, Ease.Out, x - 460, y, 0), At(30, Ease.Linear, x, y, 0), At(150, Ease.In, x, y, 0), At(180, Ease.Linear, x - 1100, y, 0));
        b.Layer(b.TextLayer("Speaker", Transform(Slide(170, 870)), "Speaker name", "SegoeUI-Bold", 56, "#FFFFFF", 700, 70));
        b.Layer(b.ShapeLayer("Plate", Transform(Slide(0, 0)), Group("Plate", Rect(560, 900, 900, 150, 6), Fill(PlateHex))));
        var path = Path.Combine(folder, "Self-test Strap.json");
        File.WriteAllText(path, b.Build().ToJsonString());
        return path;
    }

    private const string PlateHex = "#1E40AF";

    // MARK: Import and the window

    private async Task<MotionTemplatesWindow?> MotionWindowCheck(string temp)
    {
        model.ShowingOverlays = true;
        await Delay(300);
        var button = Descendants<Button>(window).FirstOrDefault(b => b.Content as string == "Motion templates…");
        Check("the Overlays bin has a Motion templates… button", button is not null);
        // As a click does: the button runs its command (a raised Click event alone doesn't).
        if (button?.Command is { } command && command.CanExecute(button.CommandParameter)) command.Execute(button.CommandParameter);
        await Delay(500);
        var editor = Application.Current.Windows.OfType<MotionTemplatesWindow>().FirstOrDefault();
        Check("it opens the motion templates window", editor is not null);
        if (editor is null) return null;

        var starters = await Until(() => model.MotionStore.All().Count(t => t.Starter), n => n >= 3, 10000);
        Check("the three starter templates are in the library", starters == 3);

        var sample = WriteSampleLottie(temp);
        var imported = await editor.ImportAsync([sample]);
        var template = imported.FirstOrDefault();
        Check("a Lottie file written by the test imports as a motion template", template is { Name: "Self-test Strap", SuggestedUse: MotionUse.Overlay });
        if (template is null) return editor;
        Check("importing it picks it, with its field and its markers", editor.Picked?.Id == template.Id
            && model.MotionStore.Lottie(template.LottieHash) is { } lottie && MotionTimeline.SegmentsOf(lottie) is { HasMarkers: true, HasOut: true }
            && MotionFields.Discover(lottie).Any(f => f.Id == "text:L0" && f.Label == "Speaker"));
        var again = await editor.ImportAsync([sample]);
        Check("importing the same file again doesn't list it twice", again.FirstOrDefault()?.Id == template.Id
            && model.MotionStore.All().Count(t => t.Name == "Self-test Strap") == 1);

        // The preview plays on its own thread: wait until it has shown some frames.
        var frames = await Until(() => editor.Preview.FramesShown, n => n >= 10, 10000);
        Check($"the big preview plays ({frames} frames)", frames >= 10);
        Save(editor, "mt-01-window");

        if (model.MotionStore.All().FirstOrDefault(t => t.Id == "starter.name-strap") is { } strap)
        {
            editor.Hover(strap.Id, over: true);
            var tileFrames = await Until(() => editor.TilePreview(strap.Id)?.FramesShown ?? 0, n => n >= 5, 8000);
            Check("pointing at a tile plays it", tileFrames >= 5);
            await Delay(400);
            Save(editor, "mt-02-tile-playing");
            editor.Hover(strap.Id, over: false);
        }
        editor.Pick(template.Id);
        return editor;
    }

    // MARK: Live, over a lyric

    private async Task<Overlay?> MotionLiveCheck(MotionTemplatesWindow editor, CountingSink sink)
    {
        // A lyric over a background, settled, as the "before" picture.
        var song = model.ServiceItems.FirstOrDefault(r => r.Name == "Amazing Grace");
        model.SelectedServiceItem = null;
        model.SelectedServiceItem = song;
        await Delay(300);
        if (model.MediaItems.FirstOrDefault(m => m.Name == "Deep Blue") is { } background) model.MediaClickedCommand.Execute(background);
        if (model.Slides.Count > 1) model.GoLiveCommand.Execute(model.Slides[1]);
        await Delay(1600);
        var before = await FrameAfter(sink, ShowState.HostClock());

        editor.SetValue("text:L0", "Pastor Grace Okafor");
        await Delay(500);
        Save(editor, "mt-03-fields-filled-in");
        Check("filling in a field changes the preview's values", editor.Values.TryGetValue("text:L0", out var typed) && typed == "Pastor Grace Okafor");

        var added = await editor.AddAsync(goLive: true) as Overlay;
        Check("Add and go live adds an overlay to the library", added is not null && model.Library.Get<Overlay>(added.Id) is not null);
        if (added is null) return null;
        var pictureId = MainViewModel.OverlayPicturePrefix + added.Id;
        var live = model.Show.LiveOverlays.FirstOrDefault(o => o.Id == added.Id);
        Check("it's up, as its live animation (not the still)", live is not null && live.Objects.Any(o => o.MediaId == pictureId)
            && model.MotionPlayers[pictureId] is not null);
        // Timed from the moment it really went up (adding it first made its still, which takes a moment).
        var up = model.MotionPlayers[pictureId]?.UpAt ?? ShowState.HostClock();
        var shown = await Until(() => model.Media.MotionFramesShown(pictureId), n => n >= 5, 10000);
        Check($"the screens are showing its frames ({shown} so far)", shown >= 5);

        // Part-way through its In, then holding.
        await WhenShown(() => ShowState.HostClock() >= up + 0.25, 5);
        if (model.Outputs?[OutputRole.Audience] is { } audience) Save(audience, "mt-04-live-in");
        var inFrame = await FrameAfter(sink, ShowState.HostClock());
        if (inFrame is not null) Write(Picture(sink, inFrame), "mt-04-program-in");
        await WhenShown(() => ShowState.HostClock() >= up + 1.2, 5);
        var holding = await FrameAfter(sink, ShowState.HostClock());
        if (model.Outputs?[OutputRole.Audience] is { } holdingAudience) Save(holdingAudience, "mt-05-live-hold");
        if (holding is not null)
        {
            Write(Picture(sink, holding), "mt-05-program-hold");
            var plate = PixelAt(sink, holding, 960, 955);
            Check($"the program shows the plate in its colour ({plate})", Near(plate, PlateHex, 16));
            // Above the lower third the animation is see-through: the slide and background show exactly as before.
            Check($"above it, the slide shows through unchanged (mean difference {MeanDifference(sink, before, holding, 120, 640):0.0})",
                before is not null && MeanDifference(sink, before, holding, 120, 640) < 3);
            var name = BrightIn(sink, holding, 200, 860, 900, 940);
            Check("the name is drawn on the plate", name > 300);
        }

        // Typing in the bin while it's up changes it on the screen at once.
        model.ShowingOverlays = true;
        model.SelectedOverlay = model.Overlays.FirstOrDefault(o => o.Overlay.Id == added.Id);
        await Delay(200);
        var field = model.OverlayTextFields.FirstOrDefault(f => f.Name == "Speaker");
        Check("the bin shows the template's field", field is not null && field.Text == "Pastor Grace Okafor" && model.SelectedOverlayIsMotion);
        if (field is not null && holding is not null)
        {
            var changedAt = ShowState.HostClock();
            field.Text = "Rev. Ade Johnson";
            var edited = await Until(() => model.MotionPlayers[pictureId]?.Document is { } d ? MotionFields.CurrentValues(d)["text:L0"] : "", t => t == "Rev. Ade Johnson", 5000);
            Check("editing its words while it's up changes the live animation", edited == "Rev. Ade Johnson"
                && model.Show.LiveOverlays.Any(o => o.Id == added.Id) && !model.Show.IsLeaving(added.Id));
            await Delay(300);
            var after = await FrameAfter(sink, changedAt + 0.2);
            if (after is not null)
            {
                Write(Picture(sink, after), "mt-06-program-edited-live");
                Check($"the program shows the new words (difference in the name {MeanDifference(sink, holding, after, 860, 940, 150, 900):0.0})",
                    MeanDifference(sink, holding, after, 860, 940, 150, 900) > 4);
            }
            if (model.Outputs?[OutputRole.Audience] is { } editedAudience) Save(editedAudience, "mt-06-live-edited");
        }
        return model.Library.Get<Overlay>(added.Id);
    }

    // MARK: Take Down

    private async Task MotionTakeDownCheck(Overlay overlay, CountingSink sink)
    {
        var pictureId = MainViewModel.OverlayPicturePrefix + overlay.Id;
        var down = ShowState.HostClock();
        model.ClearOverlaysCommand.Execute(null);
        Check("F4 starts its Out instead of cutting it", model.Show.IsLeaving(overlay.Id) && model.MotionPlayers[pictureId]?.DownAt is not null);
        // Its Out eases in (slow, then fast): 0.4 s into the half-second Out the plate has moved about 275 pixels.
        await WhenShown(() => ShowState.HostClock() >= down + 0.4, 5);
        var leaving = await FrameAfter(sink, down + 0.4);
        if (model.Outputs?[OutputRole.Audience] is { } audience) Save(audience, "mt-07-live-out");
        if (leaving is not null)
        {
            Write(Picture(sink, leaving), "mt-07-program-out");
            Check("during the Out the plate slides away (its right end has gone)", !Near(PixelAt(sink, leaving, 960, 955), PlateHex, 16));
        }
        var gone = await WhenShown(() => !model.Show.LiveOverlays.Any(o => o.Id == overlay.Id), 5);
        Check($"it leaves once its Out has played ({gone:0.00} s)", gone is not null && model.MotionPlayers[pictureId] is null);
    }

    // MARK: What a Mac gets

    private void MotionMacCheck(Overlay overlay)
    {
        var use = model.MotionStore.UseOf(overlay);
        Check("the use is kept beside the overlay, fingerprinted by it", use is { Use: MotionUse.Overlay }
            && File.Exists(Path.Combine(model.Library.Root, Core.Library.WindowsExtras.FolderName, MotionTemplateStore.UseKind, overlay.Id + ".json")));
        var json = File.ReadAllText(model.Library.DocumentPath(typeof(Overlay), overlay.Id));
        Check("the overlay file holds only the shared format (a picture, fading in and out)", !json.Contains("lottie", StringComparison.OrdinalIgnoreCase)
            && !json.Contains("motion::", StringComparison.Ordinal) && overlay.Objects.Count == 1 && overlay.Objects[0].ObjectKind == SlideObjectKind.Media
            && Core.Json.MxuJson.Deserialize<Overlay>(json).Objects[0].MediaId == use?.FallbackMediaId);
        var still = use is null ? null : model.Library.Get<MediaItem>(use.FallbackMediaId);
        var path = still is null ? null : model.Library.BlobPath(still);
        if (path is null)
        {
            Check("its still is in the library", false);
            return;
        }
        var decoder = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        var pixels = new byte[frame.PixelWidth * frame.PixelHeight * 4];
        frame.CopyPixels(pixels, frame.PixelWidth * 4, 0);
        var corner = pixels[3];
        var plateIndex = ((int)(frame.PixelHeight * 955 / 1080.0) * frame.PixelWidth + (int)(frame.PixelWidth * 960 / 1920.0)) * 4;
        Check($"the still is a PNG of the settled design, see-through around it ({still!.Name})", still.MediaKind == MediaKind.Image
            && path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && corner == 0 && pixels[plateIndex + 3] > 240);
        Write(frame, "mt-08-mac-still");
    }

    // MARK: The bin

    private async Task MotionBinCheck(Overlay overlay)
    {
        model.ShowingOverlays = true;
        model.SelectedOverlay = model.Overlays.FirstOrDefault(o => o.Overlay.Id == overlay.Id);
        await Delay(500);
        var tile = model.SelectedOverlay;
        Check("its tile is in the Motion Templates pack, with Edit motion template… beside its fields",
            tile is not null && model.SelectedOverlayPack == MotionTemplateStore.Pack && model.SelectedOverlayIsMotion);
        Save(window, "mt-09-bin");
        if (tile is null) return;
        // Pinned: the real mouse (wherever it is) mustn't move the preview off while the test looks.
        window.HoverOverlayTile(tile, pinned: true);
        var id = window.HoveredMotionPreview;
        var frames = id is null ? 0 : await Until(() => model.Media.MotionFramesShown(id), n => n >= 5, 8000);
        Check("pointing at its tile plays it there", frames >= 5);
        await Delay(300);
        Save(window, "mt-10-bin-hover");
        window.HoverOverlayTile(null, pinned: true);
    }

    private async Task MotionEditWindowCheck(Overlay overlay)
    {
        var editor = MotionTemplatesWindow.Open(window, model, editing: overlay);
        await Delay(800);
        Check("Edit motion template… opens on the use's own values", editor.Values.TryGetValue("text:L0", out var words) && words == "Rev. Ade Johnson");
        editor.SetValue(MotionFields.ColorPrefix + PlateHex, "#7A1F2B");
        await Delay(300);
        Save(editor, "mt-11-edit-window");
        await model.FlushMotionUsesAsync();
        Check("a new colour is saved with the use, and its still is made again",
            model.MotionStore.UseOf(model.Library.Get<Overlay>(overlay.Id)!) is { } use && use.Values.GetValueOrDefault(MotionFields.ColorPrefix + PlateHex) == "#7A1F2B"
            && !MotionTemplateStore.FallbackIsStale(use));
        editor.Close();
    }

    // MARK: MP4

    private async Task<string?> MotionExportCheck(MotionTemplatesWindow editor, string temp)
    {
        var copy = Path.Combine(temp, "strap over green.mp4");
        var started = editor.Export(new MotionExportOptions(480, 270, 30, MotionExportOptions.KeyGreen, HoldSeconds: 1), addToMedia: true, background: false,
            copyTo: copy, name: "Self-test Strap over green");
        Check("Export MP4 starts a render", started is not null);
        if (started is null) return null;
        await Delay(300);
        Save(editor, "mt-12-exporting");
        var item = await Within(started.Done, TimeSpan.FromMinutes(2));
        var expected = 15 + 30 + 15; // In 0.5 s, a 1 s hold, Out 0.5 s, at 30 fps
        Check("it's an H.264 video in the Media bin, as long as the play-through",
            item is { MediaKind: MediaKind.Video, FileStatus: MediaFileStatus.Ready, Classification: MediaClassification.Foreground, PixelWidth: 480 }
            && Math.Abs((item.DurationSeconds ?? 0) - expected / 30.0) < 0.1);
        Check("and a copy was saved where asked", File.Exists(copy) && new FileInfo(copy).Length > 1000);
        if (item is not null)
        {
            for (var i = 0; i < 100 && model.Media.Poster(item.Id) is null; i++) await Delay(100);
            if (model.Media.Poster(item.Id) is { } poster) Write(poster, "mt-13-export-first-frame");
        }
        Save(editor, "mt-14-exported");
        return File.Exists(copy) ? copy : null;
    }

    // MARK: Full screen

    private async Task MotionBackgroundCheck(MotionTemplatesWindow editor)
    {
        if (model.MotionStore.Get("starter.title-card") is not { } card) return;
        editor.Pick(card.Id);
        Check("the title card is picked as a full-screen background", editor.Use == MotionUse.Background && editor.Playback == MotionPlayback.Loop);
        editor.SetValue("text:L0", "WELCOME HOME");
        editor.BackgroundSizeOverride = (480, 270);
        var adding = editor.AddAsync(goLive: true);
        var item = await Within(adding, TimeSpan.FromMinutes(3)) as MediaItem;
        Check("Add and show it renders the Mac's video and files it as a looping background", item is { MediaKind: MediaKind.Video, Loops: true, Classification: MediaClassification.Background }
            && model.MotionStore.UseOf(item) is { Use: MotionUse.Background });
        if (item is null) return;
        var pictureId = MainViewModel.BackgroundPicturePrefix + item.Id;
        var playing = await Until(() => model.Media.MotionFramesShown(pictureId), n => n >= 10, 10000);
        Check("on Windows it plays live as its animation, in its video's place",
            model.Show.LiveMedia.TryGetValue(LayerKind.LoopingVideos, out var cue) && cue.MediaId == item.Id && model.Media.MotionStandIn(item.Id) == pictureId && playing >= 10);
        await Delay(800);
        if (model.Outputs?[OutputRole.Audience] is { } audience) Save(audience, "mt-15-background-live");
        Save(window, "mt-16-background-main-window");

        // How fast: a full-screen 1080p animation that never holds still, for three seconds.
        var motion = model.MotionPlayers[pictureId];
        var (drawn, drawMs, shown, copyTime) = (motion?.FramesDrawn ?? 0, motion?.DrawMilliseconds ?? 0, model.Media.MotionFramesShown(pictureId), model.Media.MotionCopyTime);
        await Delay(3000);
        if (motion is not null)
        {
            var drew = motion.FramesDrawn - drawn;
            var each = drew == 0 ? 0 : (motion.DrawMilliseconds - drawMs) / drew;
            var copies = model.Media.MotionFramesShown(pictureId) - shown;
            var copyEach = copies == 0 ? 0 : (model.Media.MotionCopyTime - copyTime).TotalMilliseconds / copies;
            report.AppendLine($"INFO  motion template speed, {motion.Width}x{motion.Height} full screen, always moving: drew {drew / 3.0:0} frames a second "
                + $"({each:0.0} ms each, on its own thread); the screens took {copies / 3.0:0} a second ({copyEach:0.0} ms each on the UI thread)");
            Check($"a 1080p template keeps moving smoothly ({copies / 3.0:0} new pictures a second on screen)", copies / 3.0 >= 20);
        }
        model.ClearAllCommand.Execute(null);
        await Delay(300);
        Check("clearing it stops its animation", model.MotionPlayers[pictureId] is null && model.Media.MotionStandIn(item.Id) is null);
    }

    // MARK: After Effects

    private async Task MotionAfterEffectsCheck(string temp, string? video)
    {
        var bridge = AfterEffectsWindow.Open(window, model);
        await Delay(500);
        Save(bridge, "mt-17-after-effects");
        Check("the After Effects window says whether After Effects is here",
            bridge.InstallText.Contains(model.AfterEffects is { } found ? found.Title : "isn't installed", StringComparison.Ordinal));
        var fake = FakeAfterEffects();
        if (fake is null || video is null)
        {
            Skip("rendering through the After Effects bridge", "the stand-in After Effects isn't built (dotnet build apps/windows/tests/MxU.Slides.FakeAfterEffects)");
            bridge.Close();
            return;
        }
        var chosen = model.Settings.AfterEffectsAerender;
        try
        {
            // A stand-in install, laid out as Adobe's, and a stand-in project (see tests/MxU.Slides.FakeAfterEffects).
            var support = Path.Combine(temp, "Adobe", "Adobe After Effects 2026", "Support Files");
            Directory.CreateDirectory(support);
            foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(fake)!, "MxU.Slides.FakeAfterEffects.*").Where(f => !f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                File.Copy(file, Path.Combine(support, Path.GetFileName(file)), overwrite: true);
            File.Copy(fake, Path.Combine(support, "aerender.exe"), overwrite: true);
            File.Copy(fake, Path.Combine(support, "AfterFX.exe"), overwrite: true);
            var project = Path.Combine(temp, "Sunday Graphics.aep");
            File.WriteAllLines(project, ["FAKE-AEP", $"source={video}", "frames=40", "delay=25", "comp=Name Strap|1920|1080|5|30",
                "text=Name Strap|1|Speaker Name|Pastor Grace", "colour=Name Strap|2|Plate|Fill 1 Color|1,2,3|#10233F"]);
            var original = File.ReadAllBytes(project);
            bridge.Close();
            Check("Locate aerender.exe… takes the chosen After Effects", model.ChooseAfterEffects(Path.Combine(support, "aerender.exe")) is { Version: "2026" });
            bridge = AfterEffectsWindow.Open(window, model);
            await Delay(300);
            bridge.UseProject(project);
            await bridge.ReadAsync();
            Check("Read compositions lists the project's, with their text layers", bridge.Listing?.Compositions is [{ Name: "Name Strap", Texts.Count: 1 }]);
            bridge.SetWords(1, "Pastor Grace Okafor");
            var job = bridge.StartRender();
            await Delay(400);
            Save(bridge, "mt-18-after-effects-rendering");
            var item = job is null ? null : await Within(job.Done, TimeSpan.FromMinutes(2));
            Check("the render (by the stand-in) lands in the Media bin, and the .aep is untouched",
                item is { MediaKind: MediaKind.Video, Folder: "After Effects", FileStatus: MediaFileStatus.Ready } && File.ReadAllBytes(project).AsSpan().SequenceEqual(original));
            Save(bridge, "mt-19-after-effects-done");
        }
        finally
        {
            model.Settings.AfterEffectsAerender = chosen;
            model.Settings.Save();
            bridge.Close();
        }
    }

    /// <summary>The stand-in After Effects the unit tests build, if it has been built (this is a developer's tool, run from the repo).</summary>
    private static string? FakeAfterEffects()
    {
        var at = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && at is not null; i++, at = at.Parent)
        {
            var folder = Path.Combine(at.FullName, "tests", "MxU.Slides.FakeAfterEffects", "bin");
            if (!Directory.Exists(folder)) continue;
            return Directory.EnumerateFiles(folder, "MxU.Slides.FakeAfterEffects.exe", SearchOption.AllDirectories).FirstOrDefault();
        }
        return null;
    }

    // MARK: Pixels of the program feed

    private static BitmapSource Picture(CountingSink sink, byte[] pixels) =>
        BitmapSource.Create(sink.Width, sink.Height, 96, 96, PixelFormats.Pbgra32, null, pixels, sink.Width * 4);

    private static (byte R, byte G, byte B) PixelAt(CountingSink sink, byte[] pixels, int x, int y)
    {
        // The feed may be another size than 1080p on a small PC: the coordinates are 1080p ones.
        var px = x * sink.Width / 1920;
        var py = y * sink.Height / 1080;
        var i = (py * sink.Width + px) * 4;
        return (pixels[i + 2], pixels[i + 1], pixels[i]);
    }

    private static bool Near((byte R, byte G, byte B) pixel, string hex, int tolerance)
    {
        var colour = ColorHex.Color(hex)!.Value;
        return Math.Abs(pixel.R - colour.Red * 255) <= tolerance && Math.Abs(pixel.G - colour.Green * 255) <= tolerance && Math.Abs(pixel.B - colour.Blue * 255) <= tolerance;
    }

    /// <summary>The mean difference per channel between two frames, over rows <paramref name="top"/> to <paramref name="bottom"/> (1080p rows), and columns if given.</summary>
    private static double MeanDifference(CountingSink sink, byte[]? a, byte[]? b, int top, int bottom, int left = 0, int right = 1920)
    {
        if (a is null || b is null || a.Length != b.Length) return double.MaxValue;
        long sum = 0, count = 0;
        for (var y = top * sink.Height / 1080; y < bottom * sink.Height / 1080; y += 2)
            for (var x = left * sink.Width / 1920; x < right * sink.Width / 1920; x += 2)
            {
                var i = (y * sink.Width + x) * 4;
                sum += Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
                count += 3;
            }
        return count == 0 ? double.MaxValue : (double)sum / count;
    }

    private static int BrightIn(CountingSink sink, byte[] pixels, int left, int top, int right, int bottom)
    {
        var bright = 0;
        for (var y = top * sink.Height / 1080; y < bottom * sink.Height / 1080; y++)
            for (var x = left * sink.Width / 1920; x < right * sink.Width / 1920; x++)
            {
                var i = (y * sink.Width + x) * 4;
                if (pixels[i] > 220 && pixels[i + 1] > 220 && pixels[i + 2] > 220) bright++;
            }
        return bright;
    }
}
