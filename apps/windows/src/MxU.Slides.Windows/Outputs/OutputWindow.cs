using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MxU.Slides.Core.Outputs;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.Rendering;
using static MxU.Slides.Windows.Infrastructure.NativeMethods;

namespace MxU.Slides.Windows.Outputs;

/// <summary>The two jobs a screen can have: show the audience program, or the stage (confidence) display.</summary>
public enum OutputRole { Audience, Stage }

/// <summary>
/// What a display shows: one or more slices (usually one), each placed in its part
/// of the display. Borderless, topmost, covering the whole display, never taking
/// keyboard focus, hiding the cursor. Port of the Mac's OutputWindowController.
/// </summary>
public sealed class OutputWindow : Window
{
    private readonly Canvas surface = new() { Background = Brushes.Black, ClipToBounds = true };
    private readonly MediaHub media;
    private readonly List<(SliceView View, SceneRect Placement)> slices = [];

    public OutputWindow(string deviceId, string name, MediaHub media)
    {
        this.media = media;
        DeviceId = deviceId;
        Title = $"MxU Slides — {name}";
        Background = Brushes.Black;
        ShowActivated = false;
        ShowInTaskbar = false;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        Cursor = Cursors.None;
        Content = surface;
        surface.SizeChanged += (_, _) => LayoutSlices();
        SourceInitialized += (_, _) => ApplyStyles();
        // Closing is owned by the OutputManager; Alt+F4 on an output just hides it.
        Closing += (_, e) =>
        {
            if (AllowClose) return;
            e.Cancel = true;
            Hide();
        };
    }

    /// <summary>The display this window covers, as an output device id (<c>display:\\.\DISPLAY2</c>).</summary>
    public string DeviceId { get; }
    public DisplayInfo? Display { get; private set; }
    public IReadOnlyList<SliceView> Slices => [.. slices.Select(s => s.View)];
    internal bool AllowClose { get; set; }

    /// <summary>
    /// Shows exactly these slices, each at its placement (0–1 of the display). Slices that
    /// stay keep their view (and video players); new ones get a view in the screen's playback
    /// scope so videos stay in step with the other outputs.
    /// </summary>
    public void SetSlices(IReadOnlyList<(Guid SliceId, Guid ScreenId, SceneRect SourceRect, SceneRect Placement, string PlaybackScope)> wanted)
    {
        foreach (var gone in slices.Where(s => wanted.All(w => w.SliceId != s.View.SliceId)).ToList())
        {
            surface.Children.Remove(gone.View);
            gone.View.View.Present((Core.Scene.RenderScene?)null, SceneTransition.Cut);
            slices.Remove(gone);
        }
        foreach (var w in wanted)
        {
            var index = slices.FindIndex(s => s.View.SliceId == w.SliceId);
            if (index < 0)
            {
                var view = new SliceView(w.SliceId, w.ScreenId, media, w.PlaybackScope);
                surface.Children.Add(view);
                slices.Add((view, w.Placement));
                index = slices.Count - 1;
            }
            slices[index] = (slices[index].View, w.Placement);
            slices[index].View.SourceRect = w.SourceRect;
        }
        LayoutSlices();
    }

    private void LayoutSlices()
    {
        var size = surface.RenderSize;
        foreach (var (view, placement) in slices)
        {
            Canvas.SetLeft(view, placement.X * size.Width);
            Canvas.SetTop(view, placement.Y * size.Height);
            view.Width = Math.Max(0, placement.Width * size.Width);
            view.Height = Math.Max(0, placement.Height * size.Height);
        }
    }

    /// <summary>The slice's size in physical pixels on this display (for the correction pass).</summary>
    public (int Width, int Height) PixelSize(SliceView view)
    {
        var placement = slices.FirstOrDefault(s => s.View == view).Placement;
        var (w, h) = Display is { } d ? (d.Width, d.Height) : ((int)ActualWidth, (int)ActualHeight);
        return ((int)Math.Round(placement.Width * w), (int)Math.Round(placement.Height * h));
    }

    /// <summary>Covers <paramref name="display"/> completely (physical pixels, any DPI).</summary>
    public void PlaceOn(DisplayInfo display)
    {
        Display = display;
        if (!IsVisible) Show();
        ApplyStyles();
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowPos(hwnd, HWND_TOPMOST, display.Left, display.Top, display.Width, display.Height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_FRAMECHANGED);
        Log.Info($"Output on {display.Label}: {string.Join(", ", slices.Select(s => s.View.SliceId))}");
    }

    private void ApplyStyles()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var style = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
    }
}

/// <summary>
/// A screen shown as an ordinary resizable window, for a laptop with one display: exactly
/// what that screen would show (before keystone and colour correction).
/// </summary>
public sealed class PreviewWindow : Window
{
    public PreviewWindow(Guid screenId, string name, MediaHub media, string playbackScope, Window owner, int offset)
    {
        ScreenId = screenId;
        Title = $"MxU Slides — {name}";
        Background = Brushes.Black;
        ShowActivated = true;
        ShowInTaskbar = false;
        View = new SceneView { Media = media, PlaysVideo = true, PlaybackScope = playbackScope };
        Content = View;
        Width = 800;
        Height = 450 + SystemParameters.WindowCaptionHeight + 16;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = owner.Left + 60 + offset * 60;
        Top = owner.Top + 80 + offset * 60;
        Closing += (_, e) =>
        {
            if (AllowClose) return;
            e.Cancel = true;
            Hide();
        };
    }

    public Guid ScreenId { get; }
    public SceneView View { get; }
    internal bool AllowClose { get; set; }
}
