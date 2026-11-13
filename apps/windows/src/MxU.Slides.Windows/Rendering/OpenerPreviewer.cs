using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MxU.Slides.Core.Motion;
using MxU.Slides.Core.Openers;
using MxU.Slides.MotionTemplates.Lottie;
using MxU.Slides.MotionTemplates.Openers;

namespace MxU.Slides.Windows.Rendering;

/// <summary>
/// Keeps the opener window's pictures up to date: the live preview, playing the opener in real
/// time, and a small still of the same opener in each look. They're drawn with the same
/// <see cref="OpenerRenderer"/> that makes the final video, just small, so what you see is what
/// you'll get (without the montage's music: the preview is silent).
/// <para>
/// Like the motion background window's previewer (MotionPreviewer.cs) it has one thread of its
/// own, below normal priority, so it never slows the show. Each finished picture is handed to the
/// UI thread to copy into its <see cref="WriteableBitmap"/>; a picture isn't drawn again until the
/// UI has taken the last one. The stills are only redrawn once the recipe has stopped changing
/// for a moment, so typing a title doesn't redraw five of them at every key.
/// </para>
/// </summary>
internal sealed class OpenerPreviewer : IDisposable
{
    public const int Width = 640, Height = 360, StillWidth = 160, StillHeight = 90;

    private readonly Dispatcher dispatcher;
    private readonly ILottieFonts fonts;
    private readonly Thread thread;
    private readonly RenderCrew crew;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly byte[] back = new byte[Width * Height * 4];
    private readonly Lock gate = new();
    private (OpenerPlan Plan, OpenerMusic? Music, OpenerRecipe Raw)? wanted;
    private double startedAt;
    private double? seekTo;
    private volatile bool waiting;
    private volatile bool disposed;

    public OpenerPreviewer(Dispatcher dispatcher, ILottieFonts fonts)
    {
        this.dispatcher = dispatcher;
        this.fonts = fonts;
        Bitmap = new WriteableBitmap(Width, Height, 96, 96, PixelFormats.Bgr32, null);
        foreach (var look in OpenerLooks.All) Stills[look.Id] = new WriteableBitmap(StillWidth, StillHeight, 96, 96, PixelFormats.Bgr32, null);
        // A few helpers, not every core: the preview should leave the PC room to run the show.
        crew = new RenderCrew("Opener preview", Math.Clamp(Environment.ProcessorCount / 2 - 1, 1, 4));
        thread = new Thread(Run) { IsBackground = true, Name = "Opener preview", Priority = ThreadPriority.BelowNormal };
        thread.Start();
    }

    /// <summary>The live preview.</summary>
    public WriteableBitmap Bitmap { get; }

    /// <summary>A still of the opener in each look, by the look's id.</summary>
    public Dictionary<string, WriteableBitmap> Stills { get; } = [];

    /// <summary>Pictures shown so far (the self-test checks they move).</summary>
    public int FramesShown { get; private set; }

    /// <summary>How many times the stills have been redrawn.</summary>
    public int StillsShown { get; private set; }

    /// <summary>Where the preview has got to, in seconds, each time a picture is shown (on the UI thread).</summary>
    public event Action<double>? Played;

    /// <summary>True while an opener renders: the preview drops to a few frames a second, leaving the PC to the render.</summary>
    public bool Quiet { get; set; }

    /// <summary>
    /// What to show: the plan, the music it was made with, and the recipe as the window holds it
    /// (where "no background chosen" is still blank, so each look's still can come in its own).
    /// </summary>
    public void Show(OpenerPlan plan, OpenerMusic? music, OpenerRecipe raw)
    {
        lock (gate) wanted = (plan, music, raw);
    }

    /// <summary>Jumps the preview to <paramref name="seconds"/>; it plays on from there.</summary>
    public void Seek(double seconds)
    {
        lock (gate) seekTo = seconds;
    }

    private void Run()
    {
        OpenerRenderer? renderer = null;
        OpenerPlan? showing = null;
        OpenerMusic? music = null;
        OpenerRecipe? raw = null;
        using var target = new LottieFrame(Width, Height);
        using var still = new LottieFrame(StillWidth, StillHeight);
        // Decoded pictures are kept across changes to the words (a new renderer for every keystroke mustn't decode the photos again).
        using var pictures = new OpenerPictures(Width, Height);
        using var stillPictures = new OpenerPictures(StillWidth, StillHeight);
        double changedAt = 0;
        var stillsDue = false;
        try
        {
            while (!disposed)
            {
                var began = clock.Elapsed;
                (OpenerPlan Plan, OpenerMusic? Music, OpenerRecipe Raw)? now;
                double? jump;
                lock (gate)
                {
                    now = wanted;
                    jump = seekTo;
                    seekTo = null;
                }
                try
                {
                    if (now is { } next && !ReferenceEquals(next.Plan, showing))
                    {
                        // A different kind of opener starts from its beginning; a change to the same one carries on where it was.
                        if (showing is null || showing.Recipe.Kind != next.Plan.Recipe.Kind) startedAt = clock.Elapsed.TotalSeconds;
                        renderer?.Dispose();
                        renderer = new OpenerRenderer(next.Plan, Width, Height, crew, fonts, pictures);
                        (showing, music, raw) = (next.Plan, next.Music, next.Raw);
                        changedAt = clock.Elapsed.TotalSeconds;
                        stillsDue = true;
                    }
                    if (showing is not null && renderer is not null)
                    {
                        if (jump is { } to) startedAt = clock.Elapsed.TotalSeconds - Math.Clamp(to, 0, showing.Seconds);
                        if (!waiting) Draw(renderer, showing, target);
                        if (stillsDue && !Quiet && clock.Elapsed.TotalSeconds - changedAt > 0.4)
                        {
                            stillsDue = false;
                            DrawStills(raw ?? showing.Recipe, music, still, stillPictures);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // A preview must never take the app down: say so in the log and wait for the next change.
                    Infrastructure.Log.Warn($"Opener preview failed: {ex.Message}");
                    renderer?.Dispose();
                    renderer = null;
                    showing = null;
                    lock (gate) wanted = null;
                }
                // About 30 pictures a second (4 when quiet); fewer when drawing takes longer.
                var spare = TimeSpan.FromMilliseconds(Quiet ? 250 : 33) - (clock.Elapsed - began);
                if (spare > TimeSpan.Zero) Thread.Sleep(spare);
            }
        }
        finally
        {
            renderer?.Dispose();
        }
    }

    private void Draw(OpenerRenderer renderer, OpenerPlan plan, LottieFrame target)
    {
        // Round and round, in real time.
        var position = (clock.Elapsed.TotalSeconds - startedAt) % plan.Seconds;
        if (position < 0) position += plan.Seconds;
        renderer.Render(plan.FrameAt(position), (long)(position * plan.Recipe.FrameRate), target);
        target.Pixels.AsSpan(0, back.Length).CopyTo(back);
        waiting = true;
        dispatcher.BeginInvoke(() =>
        {
            Bitmap.WritePixels(new Int32Rect(0, 0, Width, Height), back, Width * 4, 0);
            FramesShown++;
            waiting = false;
            Played?.Invoke(position);
        }, DispatcherPriority.Render);
    }

    private void DrawStills(OpenerRecipe recipe, OpenerMusic? music, LottieFrame still, OpenerPictures pictures)
    {
        var drawn = new List<(string Look, byte[] Pixels)>();
        foreach (var look in OpenerLooks.All)
        {
            if (disposed) return;
            // The same opener in this look. A background you've chosen stays; otherwise each look brings its own.
            var variant = OpenerPlan.For(recipe with { Look = look.Id }, music);
            using var renderer = new OpenerRenderer(variant, StillWidth, StillHeight, crew, fonts, pictures);
            renderer.Render(variant.FrameAt(variant.HeroSeconds), 0, still);
            drawn.Add((look.Id, [.. still.Pixels]));
        }
        dispatcher.BeginInvoke(() =>
        {
            foreach (var (look, pixels) in drawn)
                Stills[look].WritePixels(new Int32Rect(0, 0, StillWidth, StillHeight), pixels, StillWidth * 4, 0);
            StillsShown++;
        }, DispatcherPriority.Background);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        thread.Join(TimeSpan.FromSeconds(3));
        crew.Dispose();
    }
}
