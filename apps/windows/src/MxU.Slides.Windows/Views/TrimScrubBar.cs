using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MxU.Slides.Core.Media;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The media editor's scrub bar with trim handles: click or drag anywhere to move the playhead; drag
/// a handle (the bright bar at either end of the trim window) to trim the start or the end. The part
/// outside the trim is shaded. A handle's drag is committed when it's let go, as on the Mac.
/// Port of the trim half of the Mac's Sources/ScrubBar.swift (grab zone 10 points, handles at least
/// 2 % apart).
/// </summary>
public sealed class TrimScrubBar : FrameworkElement
{
    private const double GrabZone = 10;
    private const double MinTrimGap = 0.02;
    private const double TrackHeight = 8;

    private enum Drag { None, Seek, TrimIn, TrimOut }

    private Drag drag;
    private double draftFraction;

    public TrimScrubBar()
    {
        Height = 30;
        Cursor = Cursors.Hand;
        Focusable = false;
    }

    /// <summary>The file's length in seconds (0 until it's known).</summary>
    public double Duration
    {
        get => duration;
        set { duration = value; InvalidateVisual(); }
    }

    private double duration;

    /// <summary>Where the playhead is, in seconds.</summary>
    public double Position
    {
        get => position;
        set { position = value; if (drag != Drag.Seek) InvalidateVisual(); }
    }

    private double position;

    /// <summary>The trim window as fractions of the bar, or null for the whole file.</summary>
    public (double Start, double End)? Trim
    {
        get => trim;
        set { trim = value; InvalidateVisual(); }
    }

    private (double Start, double End)? trim;

    /// <summary>The playhead was dragged or clicked: seconds, and true once the mouse is let go.</summary>
    public event Action<double, bool>? Seeked;

    /// <summary>A handle was let go: the new window as fractions.</summary>
    public event Action<(double Start, double End)>? Trimmed;

    private double Width0 => Math.Max(1, ActualWidth);

    private double FractionAt(double x) => Math.Clamp(x / Width0, 0, 1);

    /// <summary>The window being shown: the stored one with the handle being dragged moved.</summary>
    private (double Start, double End) Shown()
    {
        var (start, end) = trim ?? (0, 1);
        return drag switch
        {
            Drag.TrimIn => (Math.Min(draftFraction, end - MinTrimGap), end),
            Drag.TrimOut => (start, Math.Max(draftFraction, start + MinTrimGap)),
            _ => (start, end),
        };
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (duration <= 0) return;
        var x = e.GetPosition(this).X;
        var (start, end) = trim ?? (0, 1);
        var inDistance = Math.Abs(x - start * Width0);
        var outDistance = Math.Abs(x - end * Width0);
        if (Math.Min(inDistance, outDistance) <= GrabZone)
            drag = inDistance <= outDistance ? Drag.TrimIn : Drag.TrimOut;
        else
            drag = Drag.Seek;
        draftFraction = FractionAt(x);
        CaptureMouse();
        if (drag == Drag.Seek) Seek(false);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var x = e.GetPosition(this).X;
        if (drag == Drag.None)
        {
            var (start, end) = trim ?? (0, 1);
            Cursor = duration > 0 && Math.Min(Math.Abs(x - start * Width0), Math.Abs(x - end * Width0)) <= GrabZone ? Cursors.SizeWE : Cursors.Hand;
            return;
        }
        draftFraction = FractionAt(x);
        if (drag == Drag.Seek) Seek(false);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (drag == Drag.None) return;
        var finished = drag;
        var window = Shown();
        drag = Drag.None;
        ReleaseMouseCapture();
        if (finished == Drag.Seek) Seek(true);
        else
        {
            trim = (Math.Clamp(window.Start, 0, 1), Math.Clamp(window.End, 0, 1));
            Trimmed?.Invoke(trim.Value);
        }
        InvalidateVisual();
    }

    private void Seek(bool final)
    {
        position = draftFraction * duration;
        Seeked?.Invoke(position, final);
        InvalidateVisual();
    }

    private static readonly Brush Track = Frozen(new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush Played = Frozen(new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)));
    private static readonly Brush Shroud = Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0x10, 0x10, 0x10)));
    private static readonly Brush Handle = Frozen(new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)));
    private static readonly Pen Playhead = Frozen(new Pen(Brushes.White, 2));

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = Width0;
        var mid = ActualHeight / 2;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // the whole height takes clicks
        var track = new Rect(0, mid - TrackHeight / 2, width, TrackHeight);
        dc.DrawRoundedRectangle(Track, null, track, 4, 4);
        if (duration <= 0) return;
        var head = drag == Drag.Seek ? draftFraction : Math.Clamp(position / duration, 0, 1);
        dc.DrawRoundedRectangle(Played, null, new Rect(0, track.Y, head * width, TrackHeight), 4, 4);
        var (start, end) = Shown();
        var editable = trim is not null || drag is Drag.TrimIn or Drag.TrimOut;
        if (editable || start > 0 || end < 1)
        {
            if (start > 0) dc.DrawRectangle(Shroud, null, new Rect(0, track.Y - 4, start * width, TrackHeight + 8));
            if (end < 1) dc.DrawRectangle(Shroud, null, new Rect(end * width, track.Y - 4, (1 - end) * width, TrackHeight + 8));
        }
        foreach (var at in new[] { start, end })
            dc.DrawRoundedRectangle(Handle, null, new Rect(at * width - 3, 2, 6, ActualHeight - 4), 2, 2);
        var x = head * width;
        dc.DrawLine(Playhead, new Point(x, 1), new Point(x, ActualHeight - 1));
    }

    /// <summary>The bar's trim from the item's points (the handles at the ends when there's none).</summary>
    public static (double, double)? FromPoints(double? inPoint, double? outPoint, double duration) =>
        MediaTrimEdit.Fractions(inPoint, outPoint, duration);
}
