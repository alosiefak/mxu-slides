using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MxU.Slides.Core.Media;
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
/// and click-through: you never see it, but Windows keeps its picture up to date.</item>
/// <item><see cref="WindowCapture"/> copies each new picture of that window into memory.</item>
/// <item>A pacing thread hands the latest picture to the destinations <c>frameRate</c> times
/// a second, repeating it when nothing changed (encoders want a steady rate).</item>
/// </list>
/// If the destinations fall behind, frames are skipped and counted rather than slowing the
/// operator's screen.
/// </summary>
public sealed class HiddenCapture : IDisposable
{
    private readonly Func<IProgramSink[]> sinks;
    private readonly Lock frameGate = new();
    private readonly Window window;
    private readonly WindowCapture? capture;
    private readonly Thread pacer;
    private volatile bool running = true;
    private byte[] latest = [];
    private int latestWidth, latestHeight;
    private long captured;
    private long delivered;
    private long late;

    public HiddenCapture(FrameworkElement content, int width, int height, int frameRate, Func<IProgramSink[]> sinks, string title)
    {
        this.sinks = sinks;
        Width = width;
        Height = height;
        FrameRate = frameRate;
        window = new Window
        {
            Title = title,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Background = Brushes.Black,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 0,
            Top = 0,
            Width = width,
            Height = height,
            Content = content,
        };
        window.SourceInitialized += (_, _) =>
        {
            // Exactly Width × Height pixels whatever the DPI; never focused, not in Alt+Tab,
            // clicks pass through, invisible (alpha 0 of 255), and at the bottom of the window stack.
            var hwnd = new WindowInteropHelper(window).Handle;
            var style = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(style | NativeMethods.WS_EX_NOACTIVATE
                | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT));
            NativeMethods.SetLayeredWindowAttributes(hwnd, 0, 0, NativeMethods.LWA_ALPHA);
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_BOTTOM, 0, 0, width, height, NativeMethods.SWP_NOACTIVATE);
        };
        window.Show();
        try
        {
            capture = new WindowCapture(new WindowInteropHelper(window).Handle, OnCaptured);
        }
        catch (Exception ex)
        {
            Problem = $"This PC can't capture the picture for recording, streaming or NDI ({ex.Message}).";
            Log.Error($"Starting the capture for {title} failed", ex);
        }
        pacer = new Thread(Pace) { IsBackground = true, Name = title, Priority = ThreadPriority.AboveNormal };
        pacer.Start();
    }

    public int Width { get; }
    public int Height { get; }
    public int FrameRate { get; }

    /// <summary>Why frames can't be captured, or null.</summary>
    public string? Problem { get; }

    /// <summary>New pictures read from the window (only when something changed).</summary>
    public long FramesCaptured => Interlocked.Read(ref captured);

    /// <summary>Frames handed to destinations, repeats included.</summary>
    public long FramesDelivered => Interlocked.Read(ref delivered);

    /// <summary>Frame slots missed because the destinations were still busy with the last one.</summary>
    public long FramesLate => Interlocked.Read(ref late);

    private void OnCaptured(byte[] pixels, int width, int height)
    {
        lock (frameGate)
        {
            if (latest.Length != pixels.Length) latest = new byte[pixels.Length];
            Buffer.BlockCopy(pixels, 0, latest, 0, pixels.Length);
            latestWidth = width;
            latestHeight = height;
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

            int width, height;
            lock (frameGate)
            {
                if (latest.Length == 0) continue;
                if (outgoing.Length != latest.Length) outgoing = new byte[latest.Length];
                Buffer.BlockCopy(latest, 0, outgoing, 0, latest.Length);
                (width, height) = (latestWidth, latestHeight);
            }
            var frame = new VideoFrame(width, height, width * 4, outgoing, ShowState.HostClock());
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
        pacer.Join(1000);
        capture?.Dispose();
        window.Content = null;
        window.Close();
    }
}
