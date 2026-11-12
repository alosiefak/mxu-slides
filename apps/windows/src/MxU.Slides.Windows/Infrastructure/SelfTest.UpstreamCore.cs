using System.Windows;
using System.Windows.Media;
using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Media;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Outputs;
using MxU.Slides.Core.Scene;
using MxU.Slides.Core.Stage;
using MxU.Slides.Windows.Views;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>
/// The upstream engine port (MxU's autumn 2026 releases), in the real app: photo spots and Scale
/// and Blur, a video's end fade, video marks firing their actions and feeding the stage
/// countdown, a dissolve fading words as one picture, the Screen Map placing a layer across two
/// screens, and the stream checks (SelfTest.UpstreamStreams.cs). Pictures are named upstream-*.
/// </summary>
public sealed partial class SelfTest
{
    private string upstreamScratch = "";

    private async Task UpstreamCoreCheck()
    {
        upstreamScratch = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"MxU self-test upstream {Guid.NewGuid():N}")).FullName;
        try
        {
            await PhotoSpotsCheck();
            await DissolveStacksCheck();
            await ScreenMapCheck();
            await VideoMarksAndEndFadeCheck();
            // The stream checks put the open deck's second slide up: open the sample song again
            // (the checks above left their own one-slide decks open, then deleted them).
            if (model.Library.Get<Presentation>(StarterContent.SampleSongId) is { } song) model.Open(song, null, null);
            await UpstreamStreamsChecks();
        }
        finally
        {
            model.ClearAllCommand.Execute(null);
            try { Directory.Delete(upstreamScratch, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // MARK: Photo spots and Scale and Blur

    private async Task PhotoSpotsCheck()
    {
        var tall = Path.Combine(upstreamScratch, "Self-test Tall Photo.png");
        var wide = Path.Combine(upstreamScratch, "Self-test Wide Photo.png");
        WritePicture(tall, Color.FromRgb(0xE8, 0x6A, 0x2C), Color.FromRgb(0x2C, 0x6A, 0xE8), 360, 720);
        WritePicture(wide, Color.FromRgb(0x10, 0x80, 0x40), Color.FromRgb(0xF0, 0xE0, 0x40), 800, 450);
        var imported = await model.ImportMediaFilesAsync([tall, wide]);
        var tallItem = imported.FirstOrDefault(i => i.Name.Contains("Tall", StringComparison.Ordinal));
        var wideItem = imported.FirstOrDefault(i => i.Name.Contains("Wide", StringComparison.Ordinal));
        Check("two pictures import for the photo slides", tallItem is not null && wideItem is not null);
        if (tallItem is null || wideItem is null) return;

        var pack = StarterPack.CleanGeometric;
        var theme = model.Library.Get<Theme>(pack.ThemeId()) ?? pack.MakeTheme();
        Check("the starter theme has the photo designs", theme.Slides?.Any(s => s.Name == "2 Photos") == true && theme.Slides.Any(s => s.Name == "Photo + Text"));

        // "2 Photos": the tall picture with Scale and Blur in the left spot, the wide one filling the right.
        var deck = new Presentation
        {
            Id = "SELFTEST-PHOTOS", Name = "Self-test Photo Slides", PresentationKind = PresentationKind.Deck, ThemeId = theme.Id,
            Slides =
            [
                new Slide
                {
                    Id = "SELFTEST-PHOTOS-1", Name = "Two photos", ThemeSlideName = "2 Photos",
                    Objects = [PhotoSpots.Photo(tallItem.Id, MediaScaleMode.ScaleAndBlur), PhotoSpots.Photo(wideItem.Id, MediaScaleMode.Fill)],
                },
                new Slide
                {
                    // A picture slide: a tall photo full screen with Scale and Blur, so no black bars.
                    Id = "SELFTEST-PHOTOS-2", Name = "Picture", Objects = [],
                    Background = new CueMedia { MediaId = tallItem.Id, Layer = CueMediaLayer.StillGraphics, ScaleMode = MediaScaleMode.ScaleAndBlur, Classification = MediaClassification.Foreground },
                },
            ],
        };
        model.Library.Save(deck);
        model.Open(deck, null, null);
        model.GoLiveCommand.Execute(model.Slides[0]);
        // The spots slide in (their builds): picture them once they've landed.
        await Delay(2000);
        var scene = model.Show.AnimatedScene();
        var spots = theme.Slides!.First(s => s.Name == "2 Photos").PhotoSpotsOf();
        var drawn = scene[LayerKind.Slide].Items.Select(i => i.Render).Where(r => r.Content is ItemContent.Shape { Style.Fill: SceneFill.Media }).ToList();
        Check("each photo is drawn in its design's spot, in reading order",
            drawn.Count == 2 && spots.Count == 2
            && drawn.Any(r => r.Frame.X == spots[0].X && ((SceneFill.Media)((ItemContent.Shape)r.Content).Style.Fill).Id == tallItem.Id)
            && drawn.Any(r => r.Frame.X == spots[1].X && ((SceneFill.Media)((ItemContent.Shape)r.Content).Style.Fill).Id == wideItem.Id));
        Check("Scale and Blur in a spot becomes a blurred copy clipped to the spot under the fitted picture",
            MediaLooks.Expanding(scene, _ => null).AllItems.Any(i => i.Id.EndsWith("#scalebg", StringComparison.Ordinal) && i.Render.MaskedBy is not null));
        // Give the decodes a moment, then picture it.
        for (var i = 0; i < 30 && (model.Media.Image(tallItem.Id, 1280) is null || model.Media.Image(wideItem.Id, 1280) is null); i++) await Delay(100);
        var pixels = await Picture("upstream-photo-spots", scene);
        var spot = spots[0];
        // The tall picture fits its 840-wide spot with room each side: that room is the blurred copy, not the spot's plain grey.
        var edge = Sample(pixels, 1280, (int)((spot.X!.Value + 30) * 1280 / 1920), (int)((spot.Y!.Value + spot.Height!.Value / 2) * 720 / 1080));
        Check("the room beside a tall photo in its spot is filled with its blurred colours", edge.R + edge.G + edge.B > 90 && !(Math.Abs(edge.R - edge.G) < 8 && Math.Abs(edge.G - edge.B) < 8));

        model.GoLiveCommand.Execute(model.Slides[1]);
        await Delay(300);
        var blurred = await Picture("upstream-scale-and-blur", model.Show.AnimatedScene());
        var side = Sample(blurred, 1280, 60, 360);
        Check("a tall picture slide with Scale and Blur has no black bars", side.R + side.G + side.B > 90);
        model.ClearAllCommand.Execute(null);
        model.Library.Delete<Presentation>(deck.Id);
    }

    private static (int R, int G, int B) Sample(byte[] bgra, int width, int x, int y)
    {
        var i = (y * width + x) * 4;
        return (bgra[i + 2], bgra[i + 1], bgra[i]);
    }

    // MARK: Dissolves fade each layer as one picture

    private async Task DissolveStacksCheck()
    {
        var canvas = new SceneSize(1920, 1080);
        var old = new AnimatedScene(canvas, SceneColor.Black);
        old.AddItem(new RenderItem { Id = "backdrop", Frame = new SceneRect(canvas), Content = new ItemContent.Shape(new ShapeStyle(SceneShapeKind.Rect, new SceneFill.Solid(SceneColor.White))) }, LayerKind.Slide);
        old.AddItem(new RenderItem { Id = "picture", Frame = new SceneRect(canvas), Content = new ItemContent.Shape(new ShapeStyle(SceneShapeKind.Rect, new SceneFill.Solid(new SceneColor(1, 0, 0)))) }, LayerKind.Slide);
        var engine = new SceneTransitionEngine(old);
        engine.Push(new AnimatedScene(canvas, SceneColor.Black), new SceneTransition(SceneTransitionKind.Dissolve, 1), now: 0);
        var mid = engine.Scene(0.5);
        Check("a dissolve on the slide layer mixes its two stacks as pictures", mid[LayerKind.Slide].CrossfadeProgress is 0.5);
        var pixels = await Picture("upstream-dissolve-stacks", mid);
        var (r, g, b) = Sample(pixels, 1280, 640, 360);
        Check($"half-way out, an opaque stack shows half its picture and none of what it covers (got {r},{g},{b})", Math.Abs(r - 128) < 24 && g < 20 && b < 20);
    }

    // MARK: The Screen Map

    private async Task ScreenMapCheck()
    {
        if (model.Outputs is not { } outputs)
        {
            Skip("the Screen Map", "no outputs");
            return;
        }
        var config = outputs.Config;
        var mapBefore = config.ScreenMap;
        var presetBefore = model.ActiveOutputPresetId;
        var left = config.AddScreen("Self-test Map Left", 1920, 1080)!;
        var right = config.AddScreen("Self-test Map Right", 1920, 1080)!;
        var preset = new OutputPreset { Id = "SELFTEST-MAP-PRESET", Name = "Self-test Screen Map", Assignments = [] };
        ScreenMapWindow? mapWindow = null;
        try
        {
            config.SetScreenMap(new ScreenMap { Name = "Self-test Room" }
                .Placing(left.Id, new SceneRect(0, 0, 1920, 1080)).Placing(right.Id, new SceneRect(1920, 0, 1920, 1080)));
            var map = config.ScreenMap!;
            ScreenMapEditing.Place(preset, LayerKind.Slide.Raw(), ScreenMapEditing.Start.AcrossScreens, map);
            var lower = ScreenMapEditing.AddCustomLayer(preset, "Self-test Wall Layer", "slide");
            model.Library.Save(preset);
            model.SwitchOutputPreset(preset.Id);
            Check("the active preset's custom layer reaches the show", model.Show.CustomLayers.Any(l => l.Id == lower));

            model.Open(model.Library.Get<Presentation>(StarterContent.SampleSongId) ?? model.Library.All<Presentation>().First(), null, null);
            // Verse 1 (the song's first slide is blank), and time for its words to arrive.
            model.GoLiveCommand.Execute(model.Slides[model.Slides.Count > 1 ? 1 : 0]);
            await Delay(1200);
            // What each screen's window is given to draw (OutputManager and ScreenFeeds call this same Program).
            var onLeft = config.Program(config.Feed(SwiftUuid.Format(left.Id)), model.Show.AnimatedScene());
            var onRight = config.Program(config.Feed(SwiftUuid.Format(right.Id)), model.Show.AnimatedScene());
            Check("a slide placed across two screens lands half on each",
                onLeft[LayerKind.Slide].Placed?.Frames.Single().Rect == new SceneRect(0, 0, 3840, 1080)
                && onRight[LayerKind.Slide].Placed?.Frames.Single().Rect == new SceneRect(-1920, 0, 3840, 1080));
            var leftPixels = await Picture("upstream-screen-map-left", onLeft);
            var rightPixels = await Picture("upstream-screen-map-right", onRight);
            // The words are centred on the room's picture, so each screen shows its half of them:
            // lit pixels on the left screen's right side, and on the right screen's left side.
            int leftInk = LitColumns(leftPixels, 1280, 720, 640, 1280), rightInk = LitColumns(rightPixels, 1280, 720, 0, 640);
            Check($"each screen draws its half of the placed slide's words (lit columns: {leftInk} left, {rightInk} right)",
                onLeft[LayerKind.Slide].Items.Count > 0 && leftInk > 20 && rightInk > 20);

            // A picture fired onto the custom layer composites above the slide.
            if (model.Library.All<MediaItem>().FirstOrDefault(m => m.MediaKind == MediaKind.Image) is { } still)
            {
                model.FireMediaOnLayer(still, lower);
                var ids = model.Show.AnimatedScene().Layers.Select(l => l.Id).ToList();
                Check("media fired onto a custom layer draws just above the layer it names",
                    ids.IndexOf(lower) == ids.IndexOf("slide") + 1 && model.Show.CustomLayerMedia.ContainsKey(lower));
                // A Mac-made Clear Layer step names the custom layer by its id.
                model.Actions.Execute([new SlideAction { Id = "SELFTEST-CLEAR-CUSTOM", Kind = SlideActionKind.ClearLayer, Layer = lower }]);
                Check("a Clear Layer action clears a custom layer by its id", !model.Show.CustomLayerMedia.ContainsKey(lower));
            }

            mapWindow = ScreenMapWindow.Open(Application.Current.MainWindow, outputs, model);
            await Delay(500);
            Save(mapWindow, "upstream-screen-map-window");
            var wall = ScreenMapEditing.NewLedWall(config, "Self-test LED Wall", 1536, 864);
            Check("New LED Wall… seeds the wall 1:1 into an HD frame and seats it on the map",
                wall is { } w && config.Placement(w) == new OutputPlacement(1920, 1080, 0, 0, 1536, 864) && config.ScreenMap?.RectFor(w) is { X: 3840 });
            if (wall is { } added) config.RemoveScreen(added);
        }
        finally
        {
            mapWindow?.Close();
            model.ClearAllCommand.Execute(null);
            model.SwitchOutputPreset(presetBefore);
            model.Library.Delete<OutputPreset>(preset.Id);
            config.RemoveScreen(left.Id);
            config.RemoveScreen(right.Id);
            config.SetScreenMap(mapBefore);
        }
    }

    // MARK: Video marks and end fades

    private async Task VideoMarksAndEndFadeCheck()
    {
        var video = model.Library.All<MediaItem>().FirstOrDefault(m => m.MediaKind == MediaKind.Video && m.FileStatus == MediaFileStatus.Ready && (m.DurationSeconds ?? 0) >= 4);
        if (video is null)
        {
            Skip("video marks and end fades", "no test video in the library (the Video section makes one)");
            return;
        }
        var timer = model.Timers.Timers.FirstOrDefault();
        if (timer is not null) model.Timers.Reset(timer.Id);
        var copy = Core.Json.MxuJson.Clone(video);
        copy.Loops = false;
        copy.Classification = MediaClassification.Foreground;
        copy.Marks = null;
        model.Library.Save(copy);
        // The marks are made the way a person makes them: in the Video Actions window.
        var editor = new Views.VideoActionsWindow(Application.Current.MainWindow, model, copy);
        try
        {
            editor.Show();
            editor.SetNewTime("0:02.5");
            var walkOn = editor.AddMark();
            editor.SetNewTime("9");
            var lights = editor.AddMark();
            Check("Video Actions adds marks in time order, named Mark 1, Mark 2",
                editor.Marks.Select(m => m.Name).SequenceEqual(["Mark 1", "Mark 2"]) && editor.Marks.Select(m => m.Id).SequenceEqual([walkOn, lights]));
            Check("typing an earlier time moves a mark ahead of the other", lights is not null && editor.Move(lights, "1") && editor.Marks[0].Id == lights && editor.Marks[0].At == 1.0);
            Check("a mark can be renamed, but not to nothing", lights is not null && walkOn is not null
                && editor.Rename(lights, "Lights") && editor.Rename(walkOn, " Band Walk-On ") && editor.Rename(walkOn, "  ")
                && editor.Marks.Select(m => m.Name).SequenceEqual(["Lights", "Band Walk-On"]));
            if (timer is not null && lights is not null)
                Check("a mark is given an action", editor.SetActions(lights, [new SlideAction { Id = "SELFTEST-MARK-ACTION", Kind = SlideActionKind.TimerStart, TimerId = timer.Id }])
                    && editor.Marks[0].Actions.Count == 1);
            editor.SetNewTime("3");
            var extra = editor.AddMark();
            Check("a mark can be removed", extra is not null && editor.Marks.Count == 3 && editor.Remove(extra) && editor.Marks.Count == 2);
            Check("the marks are saved in the shared document, as the Mac writes them",
                model.Library.Get<MediaItem>(copy.Id)?.Marks is { Count: 2 } stored && stored[1].At == 2.5 && stored[1].Name == "Band Walk-On");
            await Delay(300);
            Save(editor, "upstream-video-actions-window");
        }
        finally
        {
            editor.Close();
        }
        copy = model.Library.Get<MediaItem>(copy.Id) ?? copy;
        var fired = new List<string>();
        void Note(string mediaId, string name) { if (mediaId == copy.Id) fired.Add(name); }
        model.VideoMarkFired += Note;
        try
        {
            model.FireMedia(copy);
            var key = $"live:live-media-videos:{copy.Id}";
            for (var i = 0; i < 150 && model.Media.Transport(key) is not { Playing: true }; i++) await Delay(100);
            var info = model.StageInfoNow();
            var name = LinkedText.Resolve(new TextLink { Source = TextSourceKind.VideoMarkName }, info, DateTimeOffset.Now);
            Check($"the stage's Video Action Name shows the coming mark (got \"{name}\")", name is "Lights" or "Band Walk-On");
            var countdown = LinkedText.Resolve(new TextLink { Source = TextSourceKind.VideoCountdown, ShowsVideoName = false, VideoTarget = VideoCountdownTarget.NamedMark, VideoMarkName = "band walk-on" }, info, DateTimeOffset.Now);
            Check($"a countdown to a named mark counts to it, not the end (got {countdown})", countdown is "0:01" or "0:02" or "0:03" or "0:00");
            var crossed = await WhenShown(() => fired.Contains("Lights") && fired.Contains("Band Walk-On"), 15);
            Check("playing across the marks fires them, in order", crossed is not null && fired.IndexOf("Lights") < fired.IndexOf("Band Walk-On"));
            if (timer is not null) Check("a mark's action runs (it starts a timer)", model.Timers.Timers.FirstOrDefault(t => t.Id == timer.Id)?.IsRunning == true);
            Save(window, "upstream-video-marks");

            // The end fade: a video slide whose cue fades to black over its last second.
            model.ClearMediaCommand.Execute(null);
            var deck = new Presentation
            {
                Id = "SELFTEST-ENDFADE", Name = "Self-test End Fade", PresentationKind = PresentationKind.Deck, ThemeId = "",
                Slides = [new Slide { Id = "SELFTEST-ENDFADE-1", Name = "Video", Objects = [],
                    Background = new CueMedia { MediaId = copy.Id, Layer = CueMediaLayer.Videos, Loops = false, EndFade = MediaEndFade.Black, Classification = MediaClassification.Foreground } }],
            };
            model.Library.Save(deck);
            model.Open(deck, null, null);
            model.GoLiveCommand.Execute(model.Slides[0]);
            var cueKey = model.Media.ActiveVideos.Where(v => v.MediaId == copy.Id && v.Key.StartsWith("live:", StringComparison.Ordinal)).Select(v => v.Key).FirstOrDefault();
            for (var i = 0; i < 150 && (cueKey is null || model.Media.Transport(cueKey) is not { Duration: not null }); i++)
            {
                await Delay(100);
                cueKey ??= model.Media.ActiveVideos.Where(v => v.MediaId == copy.Id && v.Key.StartsWith("live:", StringComparison.Ordinal)).Select(v => v.Key).FirstOrDefault();
            }
            if (cueKey is not null && model.Media.Transport(cueKey) is { Duration: { } length })
            {
                model.Media.Pause(cueKey);
                var before = await Picture("upstream-end-fade-before", model.Show.AnimatedScene(), playsVideo: true);
                model.Media.Seek(cueKey, length - TimeSpan.FromSeconds(0.1));
                await WhenShown(() => model.Media.PlaybackRemaining(cueKey) is < 0.3, 5);
                var after = await Picture("upstream-end-fade-black", model.Show.AnimatedScene(), playsVideo: true);
                var brightBefore = Brightness(before);
                var brightAfter = Brightness(after);
                Check($"a video fading to black is near black in its last moment (brightness {brightBefore:0} → {brightAfter:0})", brightAfter < 25 && brightBefore > brightAfter + 20);
                var faded = MediaLooks.Expanding(FadeScene(copy.Id, MediaEndFade.Clear), _ => 0.5);
                Check("a video fading to clear is half seen half a second from its end", faded.AllItems.Single().Render.Opacity is 0.5);
            }
            else
            {
                Skip("a video's end fade", "the video didn't open on this PC");
            }
            model.ClearAllCommand.Execute(null);
            model.Library.Delete<Presentation>(deck.Id);
        }
        finally
        {
            model.VideoMarkFired -= Note;
            if (timer is not null) model.Timers.Reset(timer.Id);
            var restored = Core.Json.MxuJson.Clone(copy);
            restored.Marks = video.Marks;
            restored.Loops = video.Loops;
            restored.Classification = video.Classification;
            model.Library.Save(restored);
        }
    }

    private static AnimatedScene FadeScene(string mediaId, MediaEndFade fade)
    {
        var scene = new AnimatedScene(SceneSize.Hd);
        scene.AddItem(new RenderItem { Id = "v", Frame = new SceneRect(SceneSize.Hd), Content = new ItemContent.Media(mediaId, SceneMediaScaleMode.Fill), EndFade = fade }, LayerKind.Videos);
        return scene;
    }

    /// <summary>How many columns from <paramref name="fromX"/> up to <paramref name="toX"/> hold a clearly lit pixel (words, say).</summary>
    private static int LitColumns(byte[] bgra, int width, int height, int fromX, int toX)
    {
        var lit = 0;
        for (var x = fromX; x < toX; x++)
            for (var y = 0; y < height; y += 2)
            {
                var i = (y * width + x) * 4;
                if (bgra[i] + bgra[i + 1] + bgra[i + 2] <= 360) continue;
                lit++;
                break;
            }
        return lit;
    }

    /// <summary>The average of a picture's red, green and blue.</summary>
    private static double Brightness(byte[] bgra)
    {
        long sum = 0;
        for (var i = 0; i < bgra.Length; i += 4) sum += bgra[i] + bgra[i + 1] + bgra[i + 2];
        return sum / (bgra.Length / 4.0) / 3;
    }
}
