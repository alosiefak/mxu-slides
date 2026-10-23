using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.Rendering;
using static MxU.Slides.Windows.Infrastructure.NativeMethods;

namespace MxU.Slides.Windows.Outputs;

public enum OutputRole { Audience, Stage }

/// <summary>
/// A window that shows one output. On a projector or TV it is borderless,
/// topmost, covers the whole display, never takes keyboard focus and hides
/// the cursor. In "preview" mode (a laptop with one screen) it is an ordinary
/// resizable window, so you can see exactly what the room would see.
/// </summary>
public sealed class OutputWindow : Window
{
    public OutputWindow(OutputRole role, MediaHub media)
    {
        Role = role;
        Title = role == OutputRole.Audience ? "MxU Slides — Audience" : "MxU Slides — Stage";
        Background = Brushes.Black;
        ShowActivated = false;
        ShowInTaskbar = false;
        View = new SceneView { Media = media, PlaysVideo = true, PlaybackScope = role == OutputRole.Audience ? "live" : "stage" };
        Content = View;
        SourceInitialized += (_, _) => ApplyStyles();
        // Closing is owned by the OutputManager; Alt+F4 on an output just hides it.
        Closing += (_, e) =>
        {
            if (AllowClose) return;
            e.Cancel = true;
            Hide();
        };
    }

    public OutputRole Role { get; }
    public SceneView View { get; }
    public DisplayInfo? Display { get; private set; }
    public bool IsPreviewWindow { get; private set; }
    internal bool AllowClose { get; set; }

    /// <summary>Covers <paramref name="display"/> completely (physical pixels, any DPI).</summary>
    public void PlaceOn(DisplayInfo display)
    {
        Display = display;
        IsPreviewWindow = false;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        Cursor = Cursors.None;
        if (!IsVisible) Show();
        ApplyStyles();
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowPos(hwnd, HWND_TOPMOST, display.Left, display.Top, display.Width, display.Height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_FRAMECHANGED);
        Log.Info($"{Role} output on {display.Label}");
    }

    /// <summary>Shows the output as a normal 16:9 window for single-screen use.</summary>
    public void ShowAsPreviewWindow(Window owner)
    {
        Display = null;
        IsPreviewWindow = true;
        Topmost = false;
        Cursor = null;
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        if (!IsVisible)
        {
            Width = 800;
            Height = 450 + SystemParameters.WindowCaptionHeight + 16;
            Left = owner.Left + 60 + (Role == OutputRole.Stage ? 60 : 0);
            Top = owner.Top + 80 + (Role == OutputRole.Stage ? 60 : 0);
            Show();
        }
        ApplyStyles();
        Activate();
    }

    private void ApplyStyles()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var style = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        style = IsPreviewWindow
            ? style & ~(long)(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW)
            : style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
    }
}
