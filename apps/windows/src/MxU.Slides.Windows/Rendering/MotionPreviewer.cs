using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MxU.Slides.Core.Motion;

namespace MxU.Slides.Windows.Rendering;

/// <summary>
/// Keeps the motion background window's pictures moving in real time: the big preview and the
/// small animated tiles of the style gallery. They're drawn with the same <see cref="MotionRenderer"/>
/// that makes the final video, just small, so what you see is what you'll get.
/// <para>
/// One thread of its own does the drawing (with a few helpers), below normal priority, so it
/// never slows the show. It goes round all its pictures about 30 times a second (the gallery
/// tiles every third round), working out where each loop is from a stopwatch, so the preview
/// moves at the real speed. While a background renders it goes <see cref="Quiet"/>: the gallery
/// stops and the big preview drops to a few frames a second, leaving the PC to the render. Each finished picture is handed to the UI thread to copy into its
/// <see cref="WriteableBitmap"/>; a picture isn't drawn again until the UI has taken the last one,
/// so a busy UI thread just sees fewer frames, never a pile-up.
/// </para>
/// </summary>
internal sealed class MotionPreviewer : IDisposable
{
    /// <summary>One picture to keep moving.</summary>
    public sealed class Target
    {
        internal Target(int width, int height, bool everyRound)
        {
            Width = width;
            Height = height;
            EveryRound = everyRound;
            Bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
            Back = new byte[width * height * 4];
        }

        public int Width { get; }

        public int Height { get; }

        public WriteableBitmap Bitmap { get; }

        /// <summary>What to show (set on the UI thread; null shows nothing new).</summary>
        public MotionRecipe? Recipe
        {
            get => Volatile.Read(ref recipe);
            set => Volatile.Write(ref recipe, value);
        }

        /// <summary>Pictures shown so far (the self-test checks they move).</summary>
        public int FramesShown { get; internal set; }

        internal bool EveryRound { get; }

        internal byte[] Back { get; }

        internal volatile bool Waiting;
        internal MotionRenderer? Renderer;
        internal MotionRecipe? DrawnFrom;
        private MotionRecipe? recipe;
    }

    private readonly Dispatcher dispatcher;
    private readonly List<Target> targets = [];
    private readonly Lock gate = new();
    private readonly Thread thread;
    private readonly RenderCrew crew;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private volatile bool disposed;

    public MotionPreviewer(Dispatcher dispatcher)
    {
        this.dispatcher = dispatcher;
        // A few helpers, not every core: the preview should leave the PC room to run the show.
        crew = new RenderCrew("Motion preview", Math.Clamp(Environment.ProcessorCount / 2 - 1, 1, 4));
        thread = new Thread(Run) { IsBackground = true, Name = "Motion preview", Priority = ThreadPriority.BelowNormal };
        thread.Start();
    }

    /// <summary>A new picture of this size. <paramref name="everyRound"/> for the big preview; gallery tiles go every third round.</summary>
    public Target Add(int width, int height, bool everyRound)
    {
        var target = new Target(width, height, everyRound);
        lock (gate) targets.Add(target);
        return target;
    }

    /// <summary>True while a background renders: only the big preview, a few times a second.</summary>
    public bool Quiet { get; set; }

    private void Run()
    {
        var round = 0;
        while (!disposed)
        {
            var started = clock.Elapsed;
            var quiet = Quiet;
            Target[] now;
            lock (gate) now = [.. targets];
            foreach (var target in now)
            {
                if (disposed) return;
                if (target.Waiting || target.Recipe is not { } recipe || (!target.EveryRound && (quiet || round % 3 != 0))) continue;
                try
                {
                    Draw(target, recipe);
                }
                catch (Exception ex)
                {
                    Infrastructure.Log.Warn($"Motion preview failed: {ex.Message}");
                    target.Recipe = null;
                }
            }
            round++;
            // About 30 rounds a second (4 when quiet); fewer when drawing takes longer.
            var spare = TimeSpan.FromMilliseconds(quiet ? 250 : 33) - (clock.Elapsed - started);
            if (spare > TimeSpan.Zero) Thread.Sleep(spare);
        }
    }

    private void Draw(Target target, MotionRecipe recipe)
    {
        // The window makes a new recipe for every change (recipes never change once made), so a
        // different object means a different look: set up a renderer for it.
        if (target.Renderer is null || !ReferenceEquals(target.DrawnFrom, recipe))
        {
            target.Renderer = new MotionRenderer(recipe, target.Width, target.Height, crew);
            target.DrawnFrom = recipe;
        }
        var renderer = target.Renderer;
        // Where the loop is now, at the recipe's own speed and length.
        var seconds = clock.Elapsed.TotalSeconds;
        var phase = seconds / renderer.Recipe.Seconds % 1;
        renderer.Render(phase, (long)(seconds * renderer.Recipe.FrameRate), MotionPixelFormat.Bgra32, target.Back);
        target.Waiting = true;
        dispatcher.BeginInvoke(() =>
        {
            target.Bitmap.WritePixels(new Int32Rect(0, 0, target.Width, target.Height), target.Back, target.Width * 4, 0);
            target.FramesShown++;
            target.Waiting = false;
        }, DispatcherPriority.Render);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        thread.Join(TimeSpan.FromSeconds(2));
        crew.Dispose();
    }
}
