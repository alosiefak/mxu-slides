using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MxU.Slides.Audio.Timeline;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Rendering;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The timeline editor's lanes: the ruler, the sections bar, and every lane's cards, fades,
/// waveforms, the song's transition and the picked range. It draws the view model's
/// <see cref="TimelineEditorLayout"/> straight onto a <see cref="DrawingContext"/>, only the
/// part in view (a long song at a close zoom is tens of thousands of points wide), and turns
/// the mouse into the view model's PointerPressed/Moved/Released calls, so a drag here and a
/// drag in the self-test go through the same code. The playhead is drawn by a separate
/// <see cref="TimelinePlayheadLayer"/> on top, so it can move 30 times a second without
/// redrawing all of this. Port of PresentationTimelineLanes' drawing (SwiftUI views on the Mac).
/// </summary>
public sealed class TimelineLanesView : FrameworkElement
{
    private readonly TimelineEditorViewModel vm;
    private readonly DispatcherTimer autoScroll = new(DispatcherPriority.Input) { Interval = TimeSpan.FromSeconds(1.0 / 45) };
    private Point lastPointer;
    private TimelineRowKind? dropRow;
    private TimelineLane? dropLane;

    public TimelineLanesView(TimelineEditorViewModel vm)
    {
        this.vm = vm;
        Focusable = true;
        ClipToBounds = true;
        AllowDrop = true;
        SnapsToDevicePixels = true;
        vm.LayoutChanged += () =>
        {
            InvalidateMeasure();
            InvalidateVisual();
        };
        vm.Redraw += InvalidateVisual;
        autoScroll.Tick += (_, _) => AutoScrollTick();
    }

    /// <summary>The scroller the lanes sit in (Shift+wheel scrolls it).</summary>
    public ScrollViewer? VerticalScroller { get; set; }

    /// <summary>A right-click: on a card (with the card), or on empty lane (card null). The window shows the menu.</summary>
    public event Action<TimelineCard?, TimelineRow?, double>? MenuRequested;

    // MARK: Colours (the Mac's dark palette; tints from TimelineLane.tint and AnimationKind.tint)

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Color Rgb(double r, double g, double b) => Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));

    internal static Color WithAlpha(Color color, double alpha) => Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), color.R, color.G, color.B);

    internal static readonly Color Accent = Color.FromRgb(0x3B, 0x82, 0xF6);
    private static readonly Color Primary = Color.FromRgb(0xED, 0xED, 0xED);
    private static readonly Color Secondary = Color.FromRgb(0xA3, 0xA3, 0xA3);
    private static readonly Color Faint = Color.FromRgb(0x6E, 0x6E, 0x6E);
    private static readonly Color Plane = Color.FromRgb(0x1F, 0x1F, 0x1E);
    private static readonly Color Red = Color.FromRgb(0xEF, 0x44, 0x44);
    internal static readonly Color TracksTint = Color.FromRgb(0xAF, 0x52, 0xDE);

    /// <summary>Each lane's colour, as on the Mac.</summary>
    internal static Color Tint(TimelineLane lane) => lane switch
    {
        TimelineLane.Alerts => Color.FromRgb(0xFF, 0xCC, 0x00),
        TimelineLane.Overlays => Rgb(0.55, 0.38, 0.85),
        TimelineLane.Slide => Rgb(0.20, 0.55, 0.85),
        TimelineLane.Videos => Rgb(0.85, 0.47, 0.16),
        TimelineLane.StillGraphics => Color.FromRgb(0x30, 0xB0, 0xC7),
        TimelineLane.LoopingVideos => Rgb(0.16, 0.62, 0.36),
        TimelineLane.VideoInput => Color.FromRgb(0xFF, 0x2D, 0x55),
        TimelineLane.Audio => Color.FromRgb(0x58, 0x56, 0xD6),
        _ => Color.FromRgb(0x8E, 0x8E, 0x93),
    };

    /// <summary>A track family's colour by name (the Mac's strip colours).</summary>
    internal static Color Named(string name) => name switch
    {
        "Orange" => Color.FromRgb(0xFF, 0x95, 0x00),
        "Red" => Color.FromRgb(0xFF, 0x3B, 0x30),
        "Purple" => Color.FromRgb(0xAF, 0x52, 0xDE),
        "Yellow" => Color.FromRgb(0xFF, 0xCC, 0x00),
        "Green" => Color.FromRgb(0x34, 0xC7, 0x59),
        "Blue" => Color.FromRgb(0x00, 0x7A, 0xFF),
        "Teal" => Color.FromRgb(0x30, 0xB0, 0xC7),
        "Pink" => Color.FromRgb(0xFF, 0x2D, 0x55),
        _ => Color.FromRgb(0x8E, 0x8E, 0x93),
    };

    private static Color HexColor(string? hex, Color fallback) => ColorHex.Color(hex) is { } c ? Painting.ToColor(c) with { A = 255 } : fallback;

    /// <summary>A card's colour: a slide cue takes its section's; a stem its track family's; a missing slide is red.</summary>
    private Color CardTint(TimelineCard card)
    {
        var cue = card.Cue;
        if (vm.IsOrphan(cue)) return Red;
        if (cue.Kind() == TimelineLane.Slide) return HexColor(vm.SectionColor(cue.SectionId), Tint(TimelineLane.Slide));
        if (cue.Kind() is { } lane) return Tint(lane);
        if (card.Title is not null || TimelineEditing.IsFolded(cue)) return TracksTint;
        if (vm.Channel(cue.TrackChannelId()) is not { } channel) return TracksTint;
        return channel.ColorHex is { } hex ? HexColor(hex, TracksTint) : Named(TimelineTracks.FamilyOf(channel).ColorName());
    }

    // MARK: Text

    private static readonly Typeface UiFace = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface UiBold = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface Glyphs = new(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private FormattedText Text(string text, double size, Color color, Typeface? face = null, double maxWidth = 0)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face ?? UiFace, size,
            new SolidColorBrush(color), VisualTreeHelper.GetDpi(this).PixelsPerDip)
        { Trimming = TextTrimming.CharacterEllipsis, MaxLineCount = 1 };
        if (maxWidth > 1) formatted.MaxTextWidth = maxWidth;
        return formatted;
    }

    // MARK: Layout

    protected override Size MeasureOverride(Size availableSize) => new(0, vm.Layout.Height);

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (sizeInfo.WidthChanged) vm.SetViewport(sizeInfo.NewSize.Width);
    }

    private double X(double time) => vm.Layout.Map.XOfTime(time) - vm.ScrollX;

    private double RowsBottom => vm.Layout.Rows.Count > 0 ? vm.Layout.Rows[^1].Bottom : TimelineEditorLayout.LanesTop;

    // MARK: Drawing

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
        if (!vm.HasSong)
        {
            dc.DrawText(Text("Open a song to edit its timeline.", 12, Secondary), new Point(12, 40));
            return;
        }
        DrawRuler(dc, width);
        DrawSections(dc, width);
        foreach (var row in vm.Layout.Rows) DrawRowBackground(dc, row, width);
        DrawTransition(dc, width);
        foreach (var card in vm.Layout.Cards) DrawCard(dc, card, width);
        foreach (var row in vm.Layout.Rows.Where(r => r.HoldsAudio)) DrawFades(dc, row);
        DrawTransitionHandover(dc);
        if (vm.Range is { } range)
        {
            var left = X(range.Lower);
            var right = X(range.Upper);
            var top = TimelineEditorLayout.LanesTop;
            var rect = new Rect(left, top, Math.Max(right - left, 1), Math.Max(RowsBottom - top, 1));
            dc.DrawRectangle(Frozen(WithAlpha(Accent, 0.16)), null, rect);
            dc.DrawRectangle(Frozen(Accent), null, new Rect(left, top, 1, rect.Height));
            dc.DrawRectangle(Frozen(Accent), null, new Rect(right - 1, top, 1, rect.Height));
        }
        if (vm.Cues.Count == 0 && vm.Layout.Rows.FirstOrDefault() is { } first)
            dc.DrawText(Text("Press Record and click the slides as the song plays, or use + beside a lane to add at the playhead.", 11, Faint),
                new Point(8, first.Top + 6));
    }

    private void DrawRuler(DrawingContext dc, double width)
    {
        dc.DrawRectangle(Frozen(WithAlpha(Primary, 0.04)), null, new Rect(0, 0, width, TimelineEditorLayout.RulerHeight));
        var pps = vm.Zoom;
        var interval = TimelineEditing.Interval(pps);
        var first = Math.Floor(vm.ScrollX / pps / interval) * interval;
        var tick = Frozen(WithAlpha(Primary, 0.18));
        for (var t = Math.Max(first, 0); t <= vm.DisplayLength + interval; t += interval)
        {
            var x = X(t);
            if (x > width) break;
            dc.DrawRectangle(tick, null, new Rect(x, 0, 0.5, TimelineEditorLayout.RulerHeight));
            dc.DrawText(Text(TimelineEditing.Clock(t), 8.5, Faint), new Point(x + 2, 2));
        }
        var end = X(vm.DisplayLength);
        if (end < width) dc.DrawRectangle(Frozen(WithAlpha(Plane, 0.6)), null, new Rect(end, 0, width - end, RowsBottom));
    }

    private void DrawSections(DrawingContext dc, double width)
    {
        var top = TimelineEditorLayout.RulerHeight + TimelineEditorLayout.RowGap;
        foreach (var span in vm.SectionSpans)
        {
            var left = X(span.Start);
            var right = X(span.End) - 1;
            if (right < 0 || left > width) continue;
            var color = HexColor(vm.SectionColor(span.SectionId), Color.FromRgb(0x5C, 0x5C, 0x5C));
            dc.DrawRectangle(Frozen(WithAlpha(color, 0.8)), null, new Rect(left, top + 15, Math.Max(right - left, 1), 3));
            // The name sticks to the left edge of the view while its span is scrolled across.
            var at = Math.Max(left, 0) + 3;
            var room = Math.Min(right - at - 3, 140);
            if (room > 8 && vm.SectionName(span.SectionId) is { } name)
                dc.DrawText(Text(name, 9.5, Secondary, UiBold, room), new Point(at, top + 1));
        }
    }

    private void DrawRowBackground(DrawingContext dc, TimelineRow row, double width)
    {
        var target = dropRow is { } kind && (kind == row.Kind || (kind != TimelineRowKind.Lane && row.Kind != TimelineRowKind.Lane))
            && (row.Kind != TimelineRowKind.Lane || row.Lane == dropLane);
        dc.DrawRectangle(Frozen(target ? WithAlpha(Accent, 0.12) : WithAlpha(Primary, 0.04)), null, new Rect(0, row.Top, width, row.Height));
        if (row.Kind == TimelineRowKind.Tracks && !vm.Layout.Cards.Any(c => c.Row == row) && TimelineTracks.Stems(vm.Cues).Count == 0)
            dc.DrawText(Text("Import Audio with + beside Tracks, or drop a song's stems (files or its folder) here", 10, Faint),
                new Point(6, row.Top + Math.Max(row.Height / 2 - 7, 1)));
    }

    private void DrawCard(DrawingContext dc, TimelineCard card, double width)
    {
        var (liveLeft, liveWidth) = vm.CardDrag is { } drag ? drag.LiveSpan(card, vm.MovesWithDrag(card)) : (card.Left, card.Width);
        var left = liveLeft - vm.ScrollX;
        if (left + liveWidth < -2 || left > width + 2) return;
        var selected = vm.Selection.Contains(card.Cue.Id);
        var tint = CardTint(card);
        if (card.IsPin) DrawPin(dc, card, left, liveWidth, selected, tint);
        else DrawBlock(dc, card, left, liveWidth, selected, tint, width);
    }

    private void DrawPin(DrawingContext dc, TimelineCard card, double left, double width, bool selected, Color tint)
    {
        var cue = card.Cue;
        var slide = cue.Kind() == TimelineLane.Slide;
        var lineX = left + TimelineEditing.PinSize / 2;
        var lineWidth = selected || slide ? 2.0 : 1.0;
        var lineColor = selected ? Accent : slide ? tint : Secondary;
        dc.DrawRectangle(Frozen(lineColor), null, new Rect(lineX - lineWidth / 2, card.Row.Top + 2, lineWidth, Math.Max(card.Row.Height - 4, 1)));
        var textColor = selected ? Accent : slide ? Primary : Secondary;
        var room = width - TimelineEditing.PinSize - 3;
        if (room < 6) return;
        var x = left + TimelineEditing.PinSize + 1;
        if (!slide)
        {
            var bolt = Text("", 9, textColor, Glyphs);
            dc.DrawText(bolt, new Point(x, card.Row.Top + (card.Row.Height - bolt.Height) / 2));
            x += 12;
            room -= 12;
        }
        if (room < 6) return;
        var label = Text(slide ? vm.SlideWords(cue, room) : vm.Label(cue), 10, textColor, slide ? UiBold : UiFace, room);
        dc.DrawText(label, new Point(x, card.Row.Top + (card.Row.Height - label.Height) / 2));
    }

    private void DrawBlock(DrawingContext dc, TimelineCard card, double left, double width, bool selected, Color tint, double viewWidth)
    {
        var top = card.Top;
        var height = card.Height;
        var rect = new Rect(left, top, Math.Max(width, 1), height);
        var shape = new RectangleGeometry(rect, 6, 6);
        dc.PushClip(shape);
        dc.DrawRectangle(Frozen(WithAlpha(tint, 0.16)), null, rect);
        var audio = card.Cue.Kind() == TimelineLane.Audio || card.Cue.TrackChannelId() is not null;
        if (audio) DrawWaveform(dc, card, rect, tint, viewWidth);
        // Grips: the left edge trims, the right edge stretches.
        var grip = Frozen(WithAlpha(tint, 0.55));
        var gripHeight = Math.Min(14, Math.Max(height - 8, 2));
        dc.DrawRoundedRectangle(grip, null, new Rect(left + 2, top + (height - gripHeight) / 2, 4, gripHeight), 2, 2);
        dc.DrawRoundedRectangle(grip, null, new Rect(left + width - 6, top + (height - gripHeight) / 2, 4, gripHeight), 2, 2);
        // The label keeps to the visible part, so a long clip scrolled half off still says what it is.
        var labelX = Math.Max(left, 0) + 9;
        var room = left + width - 9 - labelX;
        if (room > 8)
        {
            var label = Text(card.Title ?? vm.Label(card.Cue), 10, Primary, maxWidth: room);
            dc.DrawText(label, new Point(labelX, top + Math.Max((height - label.Height) / 2, 0)));
        }
        dc.Pop();
        var stroke = new Pen(Frozen(selected ? Accent : WithAlpha(tint, 0.4)), selected ? 1.5 : 1);
        dc.DrawRoundedRectangle(null, stroke, new Rect(left + 0.5, top + 0.5, Math.Max(width - 1, 1), Math.Max(height - 1, 1)), 6, 6);
    }

    /// <summary>The clip's waveform (worked out in the background; drawn once it is ready). Port of TimelineWaveform's path.</summary>
    private void DrawWaveform(DrawingContext dc, TimelineCard card, Rect rect, Color tint, double viewWidth)
    {
        var pps = vm.Zoom;
        var sources = vm.WaveSources(card.Group ?? [card.Cue]);
        if (sources.Count == 0) return;
        var origin = card.Cue.At;
        var length = card.Width / pps;
        if (vm.Waveforms.Levels(sources, origin, length, card.Cue.Id) is not { } levels) return;
        var level = levels.Level(pps);
        var rate = level.BucketsPerSecond;
        // Only the part of the card in view (plus a little), on the lane's own x.
        var from = Math.Max(-rect.Left, 0);
        var to = Math.Min(viewWidth - rect.Left, rect.Width);
        if (to <= from) return;
        var first = Math.Max((int)Math.Floor(from / pps * rate), 0);
        var last = Math.Min((int)Math.Ceiling(to / pps * rate), level.Count);
        if (first >= last) return;
        var top = rect.Top + 2;
        var height = Math.Max(rect.Height - 4, 1);
        var mid = top + height / 2;
        var reach = Math.Max(height / 2 - 1, 1);
        var scale = levels.FillScale;
        double Xs(int index) => rect.Left + index / rate * pps;
        double Y(float value, double sign) => mid - sign * Math.Max(Math.Abs(value) * scale * reach, 0.5);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(Xs(first), Y(level.Maxs[first], 1)), true, true);
            var top0 = new List<Point>(last - first + 1);
            for (var index = first; index < last; index++) top0.Add(new Point(Xs(index + 1), Y(level.Maxs[index], 1)));
            context.PolyLineTo(top0, false, false);
            var bottom = new List<Point>(last - first + 1);
            for (var index = last - 1; index >= first; index--) bottom.Add(new Point(Xs(index), Y(level.Mins[index], -1)));
            context.PolyLineTo(bottom, false, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(Frozen(WithAlpha(tint, 0.55)), null, geometry);
    }

    /// <summary>Fade curves over a row's clips, and their handles. Port of ClipFadesOverlay.</summary>
    private void DrawFades(DrawingContext dc, TimelineRow row)
    {
        var shade = Frozen(Color.FromArgb(0x47, 0, 0, 0));
        var line = new Pen(Frozen(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)), 1.25) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var handles = vm.FadeHandles(row);
        foreach (var group in handles.GroupBy(h => (h.CueId, h.Out)))
        {
            var length = group.First(h => !h.Midpoint);
            var fade = length.Fade;
            var clip = vm.Cues.FirstOrDefault(c => c.Id == length.CueId);
            if (clip is null) continue;
            var start = ClipFadeGeometry.Start(clip, fade, length.Out, length.End);
            var points = ClipFadeGeometry.Curve(fade, length.Out, start)
                .Select(p => new Point(X(p.Time), ClipFadeGeometry.Y(p.Level, row.Top, row.Height))).ToList();
            var curve = new StreamGeometry();
            using (var context = curve.Open())
            {
                context.BeginFigure(points[0], false, false);
                context.PolyLineTo(points.Skip(1).ToList(), true, true);
            }
            var taken = new StreamGeometry();
            using (var context = taken.Open())
            {
                context.BeginFigure(points[0], true, true);
                context.PolyLineTo(points.Skip(1).Append(new Point(X(start + fade.Seconds), row.Top + ClipFadeGeometry.Inset))
                    .Append(new Point(X(start), row.Top + ClipFadeGeometry.Inset)).ToList(), false, false);
            }
            dc.DrawGeometry(shade, null, taken);
            dc.DrawGeometry(null, line, curve);
        }
        var fill = Brushes.White;
        var edge = new Pen(Frozen(Color.FromArgb(0x80, 0, 0, 0)), 1);
        foreach (var handle in handles)
        {
            var center = new Point(X(handle.Time), ClipFadeGeometry.HandleY(handle, row.Top, row.Height));
            if (handle.Midpoint) dc.DrawEllipse(fill, edge, center, 3.5, 3.5);
            else dc.DrawRoundedRectangle(fill, edge, new Rect(center.X - 3.5, center.Y - 3.5, 7, 7), 1.5, 1.5);
        }
    }

    /// <summary>The song's handover curves on the music lane and the tracks. Port of SongTransitionCurves.</summary>
    private void DrawTransition(DrawingContext dc, double width)
    {
        if (vm.ShownTransition is not { } shown) return;
        var plan = shown.Plan;
        var areas = new List<(double Top, double Height)>();
        if (vm.Layout.Rows.FirstOrDefault(r => r.Lane == TimelineLane.Audio) is { } music) areas.Add((music.Top, music.Height));
        var tracks = vm.Layout.Rows.Where(r => r.Kind != TimelineRowKind.Lane).ToList();
        if (tracks.Count > 0) areas.Add((tracks[0].Top, tracks[^1].Bottom - tracks[0].Top));
        var start = Math.Min(plan.NextAt, plan.FadeFrom ?? plan.NextAt);
        var left = X(start);
        var right = X(plan.End);
        var handover = X(plan.NextAt);
        if (right < 0 || left > width) return;
        foreach (var (top, height) in areas)
        {
            const double inset = 3;
            var span = Math.Max(height - inset * 2, 1);
            double Y(double level) => top + inset + span * (1 - level);
            if (right > handover + 1)
                dc.DrawRoundedRectangle(Frozen(WithAlpha(Secondary, 0.16)), null, new Rect(handover, top + inset, right - handover, span), 4, 4);
            var outgoing = new List<Point>();
            var incoming = new List<Point>();
            const int steps = 32;
            for (var step = 0; step <= steps; step++)
            {
                var t = start + (plan.End - start) * step / steps;
                var levels = SongTransitions.Levels(plan, Math.Min(t, plan.End - 0.0001));
                outgoing.Add(new Point(X(t), Y(levels.Outgoing)));
                if (t >= plan.NextAt)
                {
                    if (incoming.Count == 0) incoming.Add(new Point(X(t), Y(0)));
                    incoming.Add(new Point(X(t), Y(levels.Incoming)));
                }
            }
            var under = new StreamGeometry();
            using (var context = under.Open())
            {
                context.BeginFigure(outgoing[0], true, true);
                context.PolyLineTo(outgoing.Skip(1).Append(new Point(right, Y(0))).Append(new Point(left, Y(0))).ToList(), false, false);
            }
            dc.DrawGeometry(Frozen(WithAlpha(Accent, 0.10)), null, under);
            dc.DrawGeometry(null, new Pen(Frozen(Accent), 1.5) { LineJoin = PenLineJoin.Round }, Polyline(outgoing));
            if (incoming.Count > 1)
                dc.DrawGeometry(null, new Pen(Frozen(WithAlpha(Primary, 0.55)), 1.5) { DashStyle = new DashStyle([2.6, 2], 0) }, Polyline(incoming));
        }
        if (vm.CanEdit && vm.TransitionHandle is { } handle)
            dc.DrawEllipse(Frozen(Accent), new Pen(Frozen(Plane), 1.5), new Point(handle.X - vm.ScrollX, handle.Y), 5, 5);
    }

    private static StreamGeometry Polyline(IReadOnlyList<Point> points)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], false, false);
            context.PolyLineTo(points.Skip(1).ToList(), true, true);
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>The dashed line where the slides go on to the next item, with its label. Port of SongTransitionHandoverLine.</summary>
    private void DrawTransitionHandover(DrawingContext dc)
    {
        if (vm.ShownTransition is not { } shown) return;
        var plan = shown.Plan;
        var at = X(plan.NextAt);
        var top = TimelineEditorLayout.LanesTop;
        dc.DrawLine(new Pen(Frozen(WithAlpha(Accent, 0.7)), 1) { DashStyle = new DashStyle([3, 3], 0) }, new Point(at, top), new Point(at, RowsBottom));
        var seconds = plan.Kind == SongTransitions.Kind.Overlap ? plan.End - plan.NextAt : plan.FadeSeconds;
        var what = $"{plan.Kind.Title()} {TimelineEditing.Seconds(seconds)}";
        var label = Text(shown.NextName is { } next ? $"{what} → {next}" : $"{what}, then the end", 9.5, Accent, UiBold);
        var chip = new Rect(at + 4, top + 2, label.Width + 12, label.Height + 4);
        dc.DrawRoundedRectangle(Frozen(Plane), new Pen(Frozen(WithAlpha(Accent, 0.4)), 1), chip, chip.Height / 2, chip.Height / 2);
        dc.DrawText(label, new Point(chip.Left + 6, chip.Top + 2));
    }

    // MARK: Mouse

    private Point Content(Point view) => new(view.X + vm.ScrollX, view.Y);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var point = e.GetPosition(this);
        lastPointer = point;
        var content = Content(point);
        var toggle = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (vm.PointerPressed(content.X, content.Y, toggle, e.ClickCount) && vm.IsPressing)
        {
            CaptureMouse();
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        lastPointer = point;
        if (vm.IsPressing && IsMouseCaptured)
        {
            var content = Content(point);
            vm.PointerMoved(content.X, content.Y, Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
            if ((point.X < 36 || point.X > ActualWidth - 36) && !autoScroll.IsEnabled) autoScroll.Start();
            return;
        }
        UpdateCursor(point);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        autoScroll.Stop();
        if (!vm.IsPressing) return;
        var content = Content(e.GetPosition(this));
        vm.PointerReleased(content.X, content.Y, Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
        if (IsMouseCaptured) ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        autoScroll.Stop();
        if (vm.IsPressing) vm.PointerCancelled();
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        var content = Content(e.GetPosition(this));
        var hit = vm.Layout.CardAt(content.X, content.Y);
        MenuRequested?.Invoke(hit?.Card, vm.Layout.RowAt(content.Y), vm.Layout.TimeAt(content.X));
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var modifiers = Keyboard.Modifiers;
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            var point = e.GetPosition(this);
            vm.SetZoom(e.Delta > 0 ? TimelineZoom.ZoomedIn(vm.Zoom, TimelineZoom.SongRange) : TimelineZoom.ZoomedOut(vm.Zoom, TimelineZoom.SongRange), point.X);
        }
        else if (modifiers.HasFlag(ModifierKeys.Shift) && VerticalScroller is { } scroller)
        {
            scroller.ScrollToVerticalOffset(scroller.VerticalOffset - e.Delta / 2.0);
        }
        else
        {
            vm.ScrollTo(vm.ScrollX - e.Delta / 2.0);
        }
        e.Handled = true;
    }

    private string? shownTip;

    private void UpdateCursor(Point point)
    {
        var content = Content(point);
        string? tip = null;
        if (TimelineEditorLayout.InRuler(content.Y))
        {
            Cursor = Cursors.IBeam;
        }
        else if (vm.Layout.CardAt(content.X, content.Y) is { } hit)
        {
            Cursor = hit.Mode == TimelineBlockEdit.Mode.Move ? Cursors.Hand : Cursors.SizeWE;
            var cue = hit.Card.Cue;
            tip = vm.IsOrphan(cue)
                ? "This slide is no longer in the arrangement: the cue won't fire"
                : $"{hit.Card.Title ?? vm.Label(cue)} · {PresentationTimelines.PlayheadClock(cue.At)}"
                  + (hit.Mode == TimelineBlockEdit.Mode.Stretch ? " · drag to change how long it runs"
                     : hit.Mode == TimelineBlockEdit.Mode.Trim ? " · drag to move where it starts; the end stays put" : "");
        }
        else
        {
            Cursor = null;
        }
        // Like the Mac's .help on each card: what it is and when it fires.
        if (tip == shownTip) return;
        shownTip = tip;
        ToolTip = tip;
    }

    // Dragging a card or a range near either edge scrolls the lanes along (TimelineEdgeAutoScroll).
    private void AutoScrollTick()
    {
        if (!vm.IsPressing || !IsMouseCaptured)
        {
            autoScroll.Stop();
            return;
        }
        double step = lastPointer.X < 36 ? -14 : lastPointer.X > ActualWidth - 36 ? 14 : 0;
        if (step == 0)
        {
            autoScroll.Stop();
            return;
        }
        var before = vm.ScrollX;
        vm.ScrollTo(vm.ScrollX + step);
        if (vm.ScrollX == before) return;
        var content = Content(lastPointer);
        vm.PointerMoved(content.X, content.Y, Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
    }

    // MARK: Dropping files (a song's stems on the tracks, music on the Music lane)

    private static string[] Files(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files ? files : [];

    private (TimelineRow? Row, bool Takes) DropTarget(DragEventArgs e)
    {
        var files = Files(e);
        if (files.Length == 0 || !vm.CanEdit) return (null, false);
        var row = vm.Layout.RowAt(e.GetPosition(this).Y);
        if (row is null) return (null, false);
        if (row.Kind != TimelineRowKind.Lane) return (row, TrackChannelEditing.TakesDrop(files));
        if (row.Lane == TimelineLane.Audio) return (row, files.Any(MxU.Slides.Audio.Library.AudioImporter.IsAudioFile));
        return (row, false);
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        var (row, takes) = DropTarget(e);
        e.Effects = takes ? DragDropEffects.Copy : DragDropEffects.None;
        var kind = takes ? row?.Kind : null;
        var lane = takes ? row?.Lane : null;
        if (kind != dropRow || lane != dropLane)
        {
            dropRow = kind;
            dropLane = lane;
            InvalidateVisual();
        }
        e.Handled = true;
    }

    protected override void OnDragLeave(DragEventArgs e)
    {
        base.OnDragLeave(e);
        dropRow = null;
        dropLane = null;
        InvalidateVisual();
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        var (row, takes) = DropTarget(e);
        dropRow = null;
        dropLane = null;
        InvalidateVisual();
        if (!takes || row is null) return;
        var files = Files(e);
        var at = vm.Layout.TimeAt(Content(e.GetPosition(this)).X);
        if (row.Kind == TimelineRowKind.Lane) vm.ImportMusic(files, at);
        else vm.ImportStems(files, at, row.Kind == TimelineRowKind.Track ? row.ChannelId : null);
        e.Handled = true;
    }
}

/// <summary>
/// The playhead over the lanes: a line through every row and its time in the ruler. Its own
/// element so it can move 30 times a second without redrawing the lanes under it (the Mac
/// draws it on its own layers for the same reason: PlayheadLayers in TimelineControls.swift).
/// </summary>
public sealed class TimelinePlayheadLayer : FrameworkElement
{
    private readonly TimelineEditorViewModel vm;

    public TimelinePlayheadLayer(TimelineEditorViewModel vm)
    {
        this.vm = vm;
        IsHitTestVisible = false;
        vm.PlayheadMoved += InvalidateVisual;
        vm.LayoutChanged += InvalidateVisual;
        vm.Redraw += InvalidateVisual;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (!vm.HasSong || !vm.IsCurrent) return;
        var x = vm.Layout.Map.XOfTime(vm.Playhead) - vm.ScrollX;
        if (x < -30 || x > ActualWidth + 30) return;
        var accent = new SolidColorBrush(vm.IsRecording ? Color.FromRgb(0xEF, 0x44, 0x44) : TimelineLanesView.Accent);
        accent.Freeze();
        // Down through the rows (not the empty space under them).
        var bottom = Math.Min(ActualHeight, vm.Layout.Rows.Count > 0 ? vm.Layout.Rows[^1].Bottom : TimelineEditorLayout.LanesTop);
        dc.DrawRectangle(accent, null, new Rect(x - 0.75, TimelineEditorLayout.RulerHeight, 1.5, Math.Max(bottom - TimelineEditorLayout.RulerHeight, 0)));
        const double width = 44;
        var card = new Rect(Math.Max(x - width / 2, 0), 2, width, 12);
        dc.DrawRoundedRectangle(accent, null, card, 3, 3);
        var text = new FormattedText(PresentationTimelines.PlayheadClock(vm.Playhead), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            8.5, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, new Point(card.Left + (width - text.Width) / 2, card.Top + (card.Height - text.Height) / 2));
    }
}
