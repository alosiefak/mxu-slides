using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MxU.Slides.Core.Media;
using MxU.Slides.Core.Outputs;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Infrastructure;

namespace MxU.Slides.Windows.Outputs;

/// <summary>
/// Turns any WPF content into a steady stream of frames, for destinations that aren't a
/// display (recording, streaming, NDI, DeckLink).
/// <list type="number">
/// <item>The content is shown in a borderless window of exactly Width × Height pixels, so WPF
/// draws it on the graphics card like any window. Windows only draws windows that are on a
/// screen, so it sits at the top-left of the main screen, behind everything, fully transparent
/// and click-through: you never see it, but Windows keeps its picture up to date. A picture
/// bigger than the main screen is drawn as several screen-sized windows, one piece each
/// (<see cref="CaptureTiles"/>), put back together as they're captured.</item>
/// <item><see cref="WindowCapture"/> copies each new picture of that window into memory.</item>
/// <item>A pacing thread hands the latest picture to the destinations <c>frameRate</c> times
/// a second, repeating it when nothing changed (encoders want a steady rate).</item>
/// </list>
/// If the destinations fall behind, frames are skipped and counted rather than slowing the
/// operator's screen.
/// <para>
/// Some PCs can't use Windows' capture at all (virtual machines, some remote sessions): it
/// starts but never delivers a picture. Two seconds without one, this switches to a slower
/// software copy (<see cref="UsesSoftwareCopy"/>): WPF draws the content into a bitmap on the
/// UI thread, as often as it can without taking more than about a third of that thread's time.
/// Recording and streaming then still work, at a lower frame rate.
/// </para>
/// </summary>
public sealed class HiddenCapture : IDisposable
{
    private readonly Func<IProgramSink[]> sinks;
    private readonly Lock frameGate = new();
    private readonly List<(CaptureTiles.Tile Tile, HwndSource Window, VisualBrush? Piece)> tiles = [];
    private readonly Border stage;
    private readonly string title;
    private readonly DispatcherTimer watchdog;
    private readonly List<WindowCapture> captures = [];
    private DispatcherTimer? softwareCopy;
    private RenderTargetBitmap? copyTarget;
    private byte[] copyPixels = [];
    private readonly Thread pacer;
    private volatile bool running = true;
    private byte[] latest = [];
    private long captured;
    private long delivered;
    private long late;

    public HiddenCapture(FrameworkElement content, int width, int height, int frameRate, Func<IProgramSink[]> sinks, string title)
    {
        this.sinks = sinks;
        this.title = title;
        Width = width;
        Height = height;
        FrameRate = frameRate;
        // The content sits on a stage the size of the whole picture. WPF only draws the part of a
        // window that's on a screen, so a picture bigger than the main screen (a 1080p stream from
        // a 1366 × 768 laptop, 4K NDI from a 1080p one) is shown as several screen-sized windows:
        // the first holds the stage, each other one shows its own piece of it through a brush, and
        // the pieces are put back together as they're captured. One window when the picture fits.
        stage = new Border { Background = Brushes.Black, Child = content };
        var screen = Displays.All().FirstOrDefault(d => d.IsPrimary);
        foreach (var tile in CaptureTiles.Plan(width, height, screen?.Width ?? 0, screen?.Height ?? 0))
        {
            VisualBrush? piece = null;
            Visual root;
            if (tiles.Count == 0)
            {
                // A Canvas lets the stage be bigger than this first window (it shows the top-left piece).
                root = new Canvas { Background = Brushes.Black, Children = { stage } };
            }
            else
            {
                piece = new VisualBrush(stage) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute };
                root = new Border { Background = piece };
            }
            tiles.Add((tile, HiddenWindow($"{title} ({tiles.Count + 1})", tile, root), piece));
        }
        FitStageToDpi();
        tiles[0].Window.DpiChanged += (_, _) => FitStageToDpi();
        watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        watchdog.Tick += (_, _) =>
        {
            watchdog.Stop();
            if (running && FramesCaptured == 0) StartSoftwareCopy("Windows' capture delivered no picture");
        };
        try
        {
            // MXU_SLIDES_CAPTURE=software forces the slower copy (to test it, or on a PC whose capture misbehaves).
            if (string.Equals(Environment.GetEnvironmentVariable("MXU_SLIDES_CAPTURE"), "software", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("MXU_SLIDES_CAPTURE is set to software");
            // Once Windows' capture has failed on this PC, later captures (the next recording) start on the copy straight away.
            if (FastCaptureFailed) throw new InvalidOperationException("it delivered nothing earlier");
            foreach (var (tile, window, _) in tiles)
                captures.Add(new WindowCapture(window.Handle, (pixels, w, h) => OnCaptured(tile, pixels, w, h)));
            watchdog.Start();
        }
        catch (Exception ex)
        {
            Log.Error($"Starting the capture for {title} failed", ex);
            StartSoftwareCopy(ex.Message);
        }
        pacer = new Thread(Pace) { IsBackground = true, Name = title, Priority = ThreadPriority.AboveNormal };
        pacer.Start();
    }

    /// <summary>
    /// A bare WPF window (an HwndSource) of exactly the tile's size in pixels, whatever the DPI:
    /// never focused, not in Alt+Tab, clicks pass through, invisible (alpha 0 of 255), at the
    /// top-left of the main screen and the bottom of the window stack. It's shown only once
    /// invisible, so it never flashes. (A WPF Window would also cap its layout at the screen's size.)
    /// </summary>
    private HwndSource HiddenWindow(string name, CaptureTiles.Tile tile, Visual root)
    {
        var source = new HwndSource(new HwndSourceParameters(name, tile.Width, tile.Height)
        {
            WindowStyle = NativeMethods.WS_POPUP,
            PositionX = 0,
            PositionY = 0,
            HwndSourceHook = AllowFullSize,
        })
        {
            SizeToContent = SizeToContent.Manual,
            RootVisual = root,
        };
        var hwnd = source.Handle;
        var style = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(style | NativeMethods.WS_EX_NOACTIVATE
            | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT));
        NativeMethods.SetLayeredWindowAttributes(hwnd, 0, 0, NativeMethods.LWA_ALPHA);
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_BOTTOM, 0, 0, tile.Width, tile.Height, NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        return source;
    }

    /// <summary>The stage in DPI-independent units (WPF's), so it covers Width × Height pixels; each piece frames its part.</summary>
    private void FitStageToDpi()
    {
        var fromDevice = tiles[0].Window.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        stage.Width = Width * fromDevice.M11;
        stage.Height = Height * fromDevice.M22;
        foreach (var (tile, _, piece) in tiles)
            if (piece is not null)
                piece.Viewbox = new Rect(tile.X * fromDevice.M11, tile.Y * fromDevice.M22, tile.Width * fromDevice.M11, tile.Height * fromDevice.M22);
    }

    /// <summary>How many screen-sized windows draw the picture (1 unless it's bigger than the main screen).</summary>
    public int Tiles => tiles.Count;

    /// <summary>True when every window really has its planned size (Windows didn't cap one at the screen's).</summary>
    public bool WindowsAsPlanned => tiles.All(t =>
        NativeMethods.GetClientRect(t.Window.Handle, out var area) && area.Width == t.Tile.Width && area.Height == t.Tile.Height);

    /// <summary>
    /// Windows' capture failed on this PC (this run of the app), so captures use the slower copy.
    /// It can't draw the graphics-card effects and output corrections (WPF's software drawing
    /// skips pixel shader 3.0), so recordings and streams from such a PC show the slides without them.
    /// </summary>
    public static bool FastCaptureFailed { get; private set; }

    /// <summary>The slower fallback: WPF draws the content into a bitmap on the UI thread.</summary>
    private void StartSoftwareCopy(string reason)
    {
        if (softwareCopy is not null) return;
        FastCaptureFailed = true;
        foreach (var capture in captures) capture.Dispose();
        captures.Clear();
        Problem = $"This PC can't use Windows' fast capture ({reason}), so recording, streaming and NDI use a slower copy.";
        Log.Warn($"{title}: {Problem}");
        copyTarget = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        copyPixels = new byte[Width * Height * 4];
        softwareCopy = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.0 / Math.Min(FrameRate, 15)) };
        softwareCopy.Tick += (_, _) => CopyInSoftware();
        softwareCopy.Start();
    }

    private void CopyInSoftware()
    {
        if (!running || copyTarget is null || softwareCopy is null) return;
        var started = Stopwatch.GetTimestamp();
        try
        {
            // Draw the whole stage stretched to exactly Width × Height pixels, whatever the screen's DPI.
            // The brush must look at the stage's area, not at what the content draws: by default a
            // VisualBrush frames the drawing's bounds, and anything off screen (the next page of a lyric,
            // an item flying in) would widen them and squash the picture into the middle.
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var area = new Rect(0, 0, Width, Height);
                var shown = stage.RenderSize is { Width: > 0, Height: > 0 } size ? new Rect(size) : area;
                dc.DrawRectangle(Brushes.Black, null, area);
                dc.DrawRectangle(new VisualBrush(stage)
                {
                    Stretch = Stretch.Fill,
                    ViewboxUnits = BrushMappingMode.Absolute,
                    Viewbox = shown,
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top,
                }, null, area);
            }
            copyTarget.Clear();
            copyTarget.Render(visual);
            copyTarget.CopyPixels(copyPixels, Width * 4, 0);
            OnCaptured(new CaptureTiles.Tile(0, 0, Width, Height), copyPixels, Width, Height);
        }
        catch (Exception ex)
        {
            Log.Error($"{title}: the software copy failed", ex);
        }
        // Keep the operator's screen responsive: at most about a third of the UI thread's time.
        var spent = TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency);
        var fastest = TimeSpan.FromSeconds(1.0 / Math.Min(FrameRate, 15));
        softwareCopy.Interval = spent * 3 > fastest ? spent * 3 : fastest;
    }

    public int Width { get; }
    public int Height { get; }
    public int FrameRate { get; }

    /// <summary>Why frames can't be captured the fast way, or null.</summary>
    public string? Problem { get; private set; }

    /// <summary>True when frames come from the slower software copy (see the class summary).</summary>
    public bool UsesSoftwareCopy => softwareCopy is not null;

    /// <summary>New pictures read from the window (only when something changed).</summary>
    public long FramesCaptured => Interlocked.Read(ref captured);

    /// <summary>Frames handed to destinations, repeats included.</summary>
    public long FramesDelivered => Interlocked.Read(ref delivered);

    /// <summary>Frame slots missed because the destinations were still busy with the last one.</summary>
    public long FramesLate => Interlocked.Read(ref late);

    /// <summary>Copies one window's new picture (<paramref name="width"/> × <paramref name="height"/>, BGRA) into its place in the whole picture.</summary>
    private void OnCaptured(CaptureTiles.Tile tile, byte[] pixels, int width, int height)
    {
        lock (frameGate)
        {
            // Until every piece has arrived once, the rest of the picture is black.
            if (latest.Length != Width * Height * 4) latest = new byte[Width * Height * 4];
            var rowBytes = Math.Min(width, tile.Width) * 4;
            for (var row = 0; row < Math.Min(height, tile.Height); row++)
                Buffer.BlockCopy(pixels, row * width * 4, latest, ((tile.Y + row) * Width + tile.X) * 4, rowBytes);
        }
        Interlocked.Increment(ref captured);
    }

    private void Pace()
    {
        var interval = Stopwatch.Frequency / FrameRate;
        var next = Stopwatch.GetTimestamp();
        var outgoing = Array.Empty<byte>();
        while (running)
        {
            var wait = next - Stopwatch.GetTimestamp();
            if (wait > 0)
            {
                var ms = (int)(wait * 1000 / Stopwatch.Frequency);
                if (ms > 1) Thread.Sleep(ms - 1);
                else Thread.Yield();
                continue;
            }
            next += interval;
            if (Stopwatch.GetTimestamp() - next > interval)
            {
                // Fell more than a frame behind (destinations too slow): skip ahead instead of bursting.
                Interlocked.Increment(ref late);
                next = Stopwatch.GetTimestamp() + interval;
            }

            lock (frameGate)
            {
                if (latest.Length == 0) continue;
                if (outgoing.Length != latest.Length) outgoing = new byte[latest.Length];
                Buffer.BlockCopy(latest, 0, outgoing, 0, latest.Length);
            }
            var frame = new VideoFrame(Width, Height, Width * 4, outgoing, ShowState.HostClock());
            foreach (var sink in sinks())
            {
                try { sink.WriteVideo(frame); }
                catch (Exception ex) { Log.Error("A program destination failed to take a frame", ex); }
            }
            Interlocked.Increment(ref delivered);
        }
    }

    public void Dispose()
    {
        running = false;
        watchdog.Stop();
        softwareCopy?.Stop();
        pacer.Join(1000);
        foreach (var capture in captures) capture.Dispose();
        captures.Clear();
        // Hand the content back (the caller may show it somewhere else), then close the windows.
        stage.Child = null;
        foreach (var (_, window, _) in tiles)
        {
            if (window.RootVisual is Canvas canvas) canvas.Children.Clear();
            window.RootVisual = null;
            window.Dispose();
        }
    }

    // Answers "how big may this window be?" with at least its own size (Windows' default is the screens' size).
    private IntPtr AllowFullSize(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != NativeMethods.WM_GETMINMAXINFO) return IntPtr.Zero;
        var info = Marshal.PtrToStructure<NativeMethods.MINMAXINFO>(lParam);
        info.MaxTrackSize.X = Math.Max(info.MaxTrackSize.X, Width);
        info.MaxTrackSize.Y = Math.Max(info.MaxTrackSize.Y, Height);
        info.MaxSize = info.MaxTrackSize;
        Marshal.StructureToPtr(info, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }
}
