using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// A video's marks drawn on the transport strip's scrub bar: a short coloured line at each one,
/// lined up with where the bar's knob would be at that time. It lies over the slider and takes no
/// clicks, so dragging and clicking the bar work as before. Port of the marks the Mac's
/// ScrubBar.swift draws (2 × 10 points, in the mark's colour, yellow when it has none).
/// </summary>
public sealed class ScrubMarks : FrameworkElement
{
    public static readonly DependencyProperty MarksProperty = DependencyProperty.Register(
        nameof(Marks), typeof(IEnumerable), typeof(ScrubMarks), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SliderProperty = DependencyProperty.Register(
        nameof(Slider), typeof(Slider), typeof(ScrubMarks), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public ScrubMarks()
    {
        IsHitTestVisible = false;
        SizeChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>The marks (<see cref="MainViewModel.TransportMark"/>s), each at a share of the video's length.</summary>
    public IEnumerable? Marks
    {
        get => (IEnumerable?)GetValue(MarksProperty);
        set => SetValue(MarksProperty, value);
    }

    /// <summary>The scrub bar the marks line up with (its track, less half the knob at each end).</summary>
    public Slider? Slider
    {
        get => (Slider?)GetValue(SliderProperty);
        set => SetValue(SliderProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Marks is null || ActualWidth < 4) return;
        var (left, width) = Track();
        foreach (var mark in Marks.OfType<MainViewModel.TransportMark>())
        {
            var x = left + Math.Clamp(mark.Fraction, 0, 1) * width;
            dc.DrawRectangle(mark.Brush, null, new Rect(Math.Round(x) - 1, (ActualHeight - 10) / 2, 2, 10));
        }
    }

    /// <summary>Where the knob's centre runs, from 0 to the end: the slider's track, less half the knob each side.</summary>
    private (double Left, double Width) Track()
    {
        if (Slider?.Template?.FindName("PART_Track", Slider) is Track { Thumb: { ActualWidth: > 0 } thumb } track && track.ActualWidth > 0)
        {
            try
            {
                var origin = track.TranslatePoint(new Point(0, 0), this);
                return (origin.X + thumb.ActualWidth / 2, Math.Max(0, track.ActualWidth - thumb.ActualWidth));
            }
            catch (InvalidOperationException)
            {
                // Not in the same window (yet): the usual geometry below.
            }
        }
        // The usual slider's knob is 11 wide.
        return (5.5, Math.Max(0, ActualWidth - 11));
    }
}
