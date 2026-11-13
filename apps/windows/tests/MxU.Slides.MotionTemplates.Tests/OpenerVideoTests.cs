using MxU.Slides.Broadcast.Library;
using MxU.Slides.Broadcast.Media;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Motion;
using MxU.Slides.Core.MotionTemplates;
using MxU.Slides.Core.Openers;
using MxU.Slides.MotionTemplates.Export;
using MxU.Slides.MotionTemplates.Lottie;
using MxU.Slides.MotionTemplates.Openers;
using SkiaSharp;

namespace MxU.Slides.MotionTemplates.Tests;

// The opener and montage maker's drawing and video files (Phase 13): one of each kind is made
// small and short, plays, is the right length, has (for a montage) a sound track; the
// announcement loop's pixels come round exactly; the words are really drawn; a cancel leaves
// nothing. Encoding keeps cores busy, so these run one at a time with the other encoding tests.
[Collection(EncodingCollection.Name)]
public sealed class OpenerVideoTests : IDisposable
{
    private const int W = 320, H = 180;
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "Openers-" + Guid.NewGuid().ToString("N"));

    public OpenerVideoTests() => Directory.CreateDirectory(scratch);

    public void Dispose()
    {
        try { Directory.Delete(scratch, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FolderFonts Fonts => LottieRendererTests.ShippedFonts;

    /// <summary>A made-up photo: a diagonal blend of two colours with a white disc, saved as a JPEG or PNG.</summary>
    private string Photo(string name, int width, int height, SKColor a, SKColor b)
    {
        var path = Path.Combine(scratch, name);
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        using var paint = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(width, height), [a, b], SKShaderTileMode.Clamp) };
        surface.Canvas.DrawRect(0, 0, width, height, paint);
        using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
        surface.Canvas.DrawCircle(width / 2f, height / 2f, Math.Min(width, height) / 5f, white);
        using var image = surface.Snapshot();
        using var data = image.Encode(name.EndsWith(".png", StringComparison.Ordinal) ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg, 90);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    /// <summary>A click track at 120 beats a minute, as the montage's music.</summary>
    private sealed class Clicks(double seconds) : IOpenerSound
    {
        private long at;

        public int SampleRate => 48_000;

        public int Read(Span<float> interleavedStereo)
        {
            var frames = (int)Math.Min(interleavedStereo.Length / 2, (long)(seconds * SampleRate) - at);
            for (var i = 0; i < frames; i++, at++)
            {
                var into = at % (SampleRate / 2);
                var sample = (float)(0.7 * Math.Exp(-into / (SampleRate * 0.008)) * Math.Sin(2 * Math.PI * 1000 * into / SampleRate));
                interleavedStereo[i * 2] = interleavedStereo[i * 2 + 1] = sample;
            }
            return Math.Max(0, frames);
        }
    }

    private static byte[] Draw(OpenerPlan plan, long frame, OpenerPictures? pictures = null)
    {
        using var crew = new RenderCrew("Opener test", 1);
        using var renderer = new OpenerRenderer(plan, W, H, crew, Fonts, pictures);
        using var target = new LottieFrame(W, H);
        renderer.Render(frame, target);
        return [.. target.Pixels];
    }

    private static int Bright(byte[] bgra) => Enumerable.Range(0, bgra.Length / 4).Count(i => bgra[i * 4] > 200 && bgra[i * 4 + 1] > 200 && bgra[i * 4 + 2] > 200);

    /// <summary>The brightest colour value in a picture (its alpha is always 255, so that's left out).</summary>
    private static int Brightest(byte[] bgra) => bgra.Where((_, i) => i % 4 != 3).Max();

    [Fact]
    public void ABumperBecomesAVideoThatPlaysOnce()
    {
        var plan = OpenerPlan.For(OpenerRecipe.For(OpenerKind.Bumper) with { Seconds = 5 });
        var path = Path.Combine(scratch, "bumper.mp4");
        OpenerVideoMaker.Render(plan, path, Fonts, cancellationToken: Ct, width: W, height: H);
        var probe = MediaImportProbe.ProbeVideo(path);
        Assert.Equal((MediaFileStatus.Ready, W, H, false), (probe.FileStatus, probe.PixelWidth, probe.PixelHeight, probe.HasAudio));
        Assert.Equal(5, probe.DurationSeconds ?? 0, 2);
        Assert.False(File.Exists(Path.Combine(scratch, "bumper.partial.mp4")));

        // Black at each end; in the middle, the background with the white title over it.
        Assert.Equal(0, Brightest(Draw(plan, 0)));
        var middle = Draw(plan, plan.FrameCount / 2);
        Assert.True(Bright(middle) > 150, $"{Bright(middle)} white pixels of title");
        Assert.True(middle.Where((_, i) => i % 4 != 3).Average(b => b) > 12, "the background shows");
        // The same recipe always draws the same picture.
        Assert.Equal(middle, Draw(plan, plan.FrameCount / 2));
    }

    [Fact]
    public void ACountdownIsAsLongAsItsCountPlusTheWelcomeCardAndTheClockChanges()
    {
        var plan = OpenerPlan.For(OpenerRecipe.For(OpenerKind.Countdown) with { Seconds = 10, EndTitle = "Welcome" });
        var path = Path.Combine(scratch, "countdown.mp4");
        OpenerVideoMaker.Render(plan, path, Fonts, cancellationToken: Ct, width: W, height: H);
        Assert.Equal(15, MediaImportProbe.ProbeVideo(path).DurationSeconds ?? 0, 2);

        // "0:08" and "0:07" differ where the figures are, but frames within one second only by the drifting background.
        var (a, b, c) = (Draw(plan, 75), Draw(plan, 85), Draw(plan, 105));
        Assert.True(Bright(a) > 200, $"{Bright(a)} white pixels of clock");
        int Changed(byte[] x, byte[] y) => Enumerable.Range(0, x.Length / 4).Count(i => Math.Abs(x[i * 4 + 1] - y[i * 4 + 1]) > 120);
        Assert.True(Changed(a, b) < 20, $"{Changed(a, b)} pixels changed within a second");
        Assert.True(Changed(a, c) > 100, $"{Changed(a, c)} pixels changed between seconds");
    }

    [Fact]
    public void AnAnnouncementLoopsPixelsComeRoundExactly()
    {
        var picture = Photo("groups.jpg", 800, 600, SKColors.DarkOrange, SKColors.DarkRed);
        var recipe = OpenerRecipe.For(OpenerKind.Announcements) with
        {
            CardSeconds = 3, Cards = [new("Welcome", "We are glad you are here"), new("Small Groups", "Sign up at the desk", picture)],
        };
        var plan = OpenerPlan.For(recipe);
        Assert.Equal(180, plan.FrameCount);
        // The frame after the last is the first, pixel for pixel; and the last is only a small step away.
        var first = Draw(plan, 0);
        Assert.Equal(first, Draw(plan, plan.FrameCount));
        var last = Draw(plan, plan.FrameCount - 1);
        double Step(byte[] x, byte[] y) => x.Zip(y, (p, q) => Math.Abs(p - q)).Average();
        var seam = Step(last, first);
        var ordinary = Step(Draw(plan, 30), Draw(plan, 31));
        Assert.True(seam < Math.Max(ordinary * 3, 1.5), $"the seam's step is {seam:0.00}, an ordinary one {ordinary:0.00}");

        var path = Path.Combine(scratch, "loop.mp4");
        OpenerVideoMaker.Render(plan, path, Fonts, cancellationToken: Ct, width: W, height: H);
        Assert.Equal(6, MediaImportProbe.ProbeVideo(path).DurationSeconds ?? 0, 2);

        // Filed as the bin's looping kind, with its recipe kept for next time.
        var library = new LibraryStore(Path.Combine(scratch, "library")) { Prober = new MediaImportProbe() };
        var item = OpenerVideoMaker.FileIntoLibrary(library, path, recipe);
        Assert.Equal((MediaKind.Video, MediaClassification.Background, true, OpenerRecipe.Folder, "Announcements (2)"),
            (item.MediaKind, item.Classification, item.Loops, item.Folder, item.Name));
        Assert.False(File.Exists(path));
        var kept = OpenerVideoMaker.RecipeOf(library, item);
        Assert.Equal((OpenerKind.Announcements, 2, "Small Groups", picture), (kept?.Kind, kept?.Cards.Count, kept?.Cards[1].Title, kept?.Cards[1].Picture));

        // Made again with a change: the same item gets the new file, and the recipe follows it.
        var again = Path.Combine(scratch, "again.mp4");
        var changed = recipe with { Cards = [new("Welcome", "Service begins at ten")] };
        OpenerVideoMaker.Render(OpenerPlan.For(changed), again, Fonts, cancellationToken: Ct, width: W, height: H);
        var replaced = OpenerVideoMaker.FileIntoLibrary(library, again, changed, item.Id);
        Assert.Equal(item.Id, replaced.Id);
        Assert.NotEqual(item.FileHash, replaced.FileHash);
        Assert.Equal(1, OpenerVideoMaker.RecipeOf(library, replaced)?.Cards.Count);
    }

    [Fact]
    public void AMontageShowsItsPicturesAndClipAndCarriesItsMusic()
    {
        // A landscape photo, a portrait one (shown whole over its own blur), and a video clip.
        var landscape = Photo("one.jpg", 1600, 900, SKColors.Blue, SKColors.Blue);
        var portrait = Photo("two.png", 600, 1200, SKColors.Red, SKColors.Red);
        var clip = Path.Combine(scratch, "clip.mp4");
        // A lower third over key green: its top corner is plain green all the way through.
        var strap = StarterMotionTemplates.NameStrap();
        LottieVideoMaker.Render(strap, MotionTimeline.For(strap, MotionPlayback.Loop), new MotionExportOptions(W, H, 30, MotionExportOptions.KeyGreen, HoldSeconds: 2), clip, Fonts, cancellationToken: Ct);

        var recipe = OpenerRecipe.For(OpenerKind.Montage) with { Shots = [landscape, portrait, clip], Music = "clicks", Seconds = 0 };
        var music = new OpenerMusic(6, Enumerable.Range(0, 12).Select(i => i * 0.5).ToList());
        var plan = OpenerPlan.For(recipe, music);
        Assert.Equal((6.0, 3, true), (plan.Seconds, plan.Shots.Count, plan.OnBeats));

        var path = Path.Combine(scratch, "montage.mp4");
        OpenerVideoMaker.Render(plan, path, Fonts, new Clicks(6), cancellationToken: Ct, width: W, height: H);
        var probe = MediaImportProbe.ProbeVideo(path);
        Assert.Equal((MediaFileStatus.Ready, true), (probe.FileStatus, probe.HasAudio));
        Assert.Equal(6, probe.DurationSeconds ?? 0, 1);

        // Mid-way through each shot: blue fills the screen; red stands in the middle with its blur beside it; the clip's green.
        (int B, int G, int R) At(byte[] p, int x, int y) => (p[(y * W + x) * 4], p[(y * W + x) * 4 + 1], p[(y * W + x) * 4 + 2]);
        int Middle(int shot) => (int)((plan.Shots[shot].Start + plan.Shots[shot].End) / 2 * 30);
        var one = Draw(plan, Middle(0));
        Assert.True(At(one, 20, 20) is { B: > 200, R: < 60 }, $"landscape corner {At(one, 20, 20)}");
        var two = Draw(plan, Middle(1));
        Assert.True(At(two, W / 2, 20) is { R: > 200, B: < 60 }, $"portrait middle {At(two, W / 2, 20)}");
        var side = At(two, 20, H / 2);
        Assert.True(side is { R: > 40 and < 200, B: < 60 }, $"blurred backdrop {side}");
        var three = Draw(plan, Middle(2));
        Assert.True(At(three, 10, 10) is { G: > 120, R: < 80 }, $"clip corner {At(three, 10, 10)}");
        // A missing picture doesn't stop the montage: its shot is simply black.
        var holed = OpenerPlan.For(recipe with { Shots = [Path.Combine(scratch, "gone.jpg")] });
        Assert.Equal(0, Brightest(Draw(holed, 45)));
    }

    [Fact]
    public void EveryLookDrawsItsWordsInItsOwnWay()
    {
        var pictures = OpenerLooks.All.Select(look => Draw(OpenerPlan.For(OpenerRecipe.For(OpenerKind.Bumper) with { Look = look.Id, Background = "smoke", Palette = "Midnight" }), 120)).ToList();
        Assert.All(pictures, p => Assert.True(Bright(p) > 40, $"{Bright(p)} white pixels"));
        for (var i = 0; i < pictures.Count; i++)
            for (var j = i + 1; j < pictures.Count; j++)
                Assert.NotEqual(pictures[i], pictures[j]);
        // A very long title is set smaller rather than running off the screen: nothing bright touches the side edges.
        var longTitle = Draw(OpenerPlan.For(OpenerRecipe.For(OpenerKind.Bumper) with
        {
            Look = "bold", Background = "smoke", Palette = "Midnight", Subtitle = "",
            Title = "An extraordinarily long sermon series title that could never fit on three lines at its usual size however hard it tried to",
        }), 120);
        Assert.True(Bright(longTitle) > 60);
        for (var y = 0; y < H; y++)
        {
            Assert.True(longTitle[(y * W + 2) * 4 + 1] < 200, $"words touch the left edge at row {y}");
            Assert.True(longTitle[(y * W + W - 3) * 4 + 1] < 200, $"words touch the right edge at row {y}");
        }
    }

    [Fact]
    public void CancellingLeavesNothingBehind()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var path = Path.Combine(scratch, "cancelled.mp4");
        var progress = new SyncProgress(p =>
        {
            if (p.Frames >= 5) cancel.Cancel();
        });
        var plan = OpenerPlan.For(OpenerRecipe.For(OpenerKind.Countdown) with { Seconds = 600 });
        Assert.ThrowsAny<OperationCanceledException>(() => OpenerVideoMaker.Render(plan, path, Fonts, progress: progress, cancellationToken: cancel.Token, width: W, height: H));
        Assert.Empty(Directory.EnumerateFiles(scratch));
    }

    private sealed class SyncProgress(Action<MotionRenderProgress> report) : IProgress<MotionRenderProgress>
    {
        public void Report(MotionRenderProgress value) => report(value);
    }

    /// <summary>
    /// Not a check: with MXU_OPENER_PICTURES set to a folder, writes full-size frames of each
    /// kind and look there as PNGs, to look at while working on the looks.
    /// </summary>
    [Fact]
    public void PicturesToLookAt()
    {
        if (Environment.GetEnvironmentVariable("MXU_OPENER_PICTURES") is not { Length: > 0 } folder) return;
        Directory.CreateDirectory(folder);
        var photo = Photo("photo.jpg", 1600, 1000, SKColors.SteelBlue, SKColors.DarkSlateBlue);
        var tall = Photo("tall.jpg", 900, 1400, SKColors.IndianRed, SKColors.DarkOrange);
        using var crew = new RenderCrew("Opener pictures");
        void Save(string name, OpenerRecipe recipe, params double[] seconds)
        {
            var plan = OpenerPlan.For(recipe);
            using var renderer = new OpenerRenderer(plan, 1920, 1080, crew, Fonts);
            using var target = new LottieFrame(1920, 1080);
            foreach (var at in seconds)
            {
                renderer.Render((long)(at * 30), target);
                using var bitmap = new SKBitmap();
                var pin = System.Runtime.InteropServices.GCHandle.Alloc(target.Pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
                bitmap.InstallPixels(new SKImageInfo(1920, 1080, SKColorType.Bgra8888, SKAlphaType.Premul), pin.AddrOfPinnedObject(), target.Stride);
                using var data = bitmap.Encode(SKEncodedImageFormat.Png, 90);
                File.WriteAllBytes(Path.Combine(folder, $"{name}-{at:00.0}.png"), data.ToArray());
                pin.Free();
            }
        }
        foreach (var look in OpenerLooks.All)
        {
            Save($"bumper-{look.Id}", OpenerRecipe.For(OpenerKind.Bumper) with { Look = look.Id }, 1.1, 4);
            Save($"countdown-{look.Id}", OpenerRecipe.For(OpenerKind.Countdown) with { Look = look.Id, Subtitle = "Grace Community Church" }, 12.4);
            Save($"loop-{look.Id}", OpenerRecipe.For(OpenerKind.Announcements) with
            {
                Look = look.Id,
                Cards = [new("Small Groups", "New groups start this month.\nSign up at the welcome desk or online.", photo), new("Youth Night", "Friday at 7 pm in the hall. Bring a friend!")],
            }, 3, 7.4, 11);
        }
        Save("bumper-logo", OpenerRecipe.For(OpenerKind.Bumper) with { Picture = photo }, 4);
        Save("bumper-logo-broadcast", OpenerRecipe.For(OpenerKind.Bumper) with { Look = "broadcast", Picture = photo }, 4);
        Save("montage", OpenerRecipe.For(OpenerKind.Montage) with { Title = "Summer Camp 2026", Subtitle = "A week to remember", EndTitle = "See you next year", Shots = [photo, tall] }, 2, 5.5, 7.5, 9.5, 12.5);
    }
}
