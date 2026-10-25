using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.ViewModels;
using Block = MxU.Slides.Core.Animation.AnimationTimelineLayout.Block;
using Column = MxU.Slides.Core.Animation.AnimationTimelineLayout.Column;
using Slot = MxU.Slides.Core.Animation.AnimationSequence.GroupSlot;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The animation timeline: a column for each group (with the slide or going up, each click, the
/// final group), a row for each object, and a block for each step, where and as long as it plays.
/// Drag a block to move it (into another column to play it on another click), drag its right edge
/// to make it longer and its left edge to start it later; click the ruler to see that moment on the
/// canvas. Right-click a block to link it to the step before, copy or delete it; right-click a
/// column's heading to move or delete the column. Every gesture is a rule in Core
/// (<see cref="AnimationTimelineEditing"/>). Port of the Mac's Sources/AnimationTimelinePanel.swift,
/// drawn straight onto a DrawingContext like the song timeline (Views/TimelineLanesView.cs).
/// </summary>
public sealed class AnimationTimelineView : FrameworkElement
{
    private const double LabelWidth = 92;
    private const double RulerHeight = 22;
    private const double RowHeight = 22;
    private const double EdgeGrab = 5;

    private AnimationTabViewModel? tab;

    public AnimationTimelineView()
    {
        ClipToBounds = true;
        Focusable = false;
        DataContextChanged += (_, e) => Attach(e.NewValue as AnimationTabViewModel);
    }

    private void Attach(AnimationTabViewModel? next)
    {
        if (tab is not null) tab.PropertyChanged -= OnTabChanged;
        tab = next;
        if (tab is not null) tab.PropertyChanged += OnTabChanged;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void OnTabChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AnimationTabViewModel.Timeline) or nameof(AnimationTabViewModel.SelectedStepRow) or "")
        {
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    /// <summary>Where the preview is, in seconds into the play-through (drawn as the playhead).</summary>
    public double Playhead
    {
        get => playhead;
        set
        {
            if (Math.Abs(playhead - value) < 1e-6) return;
            playhead = value;
            InvalidateVisual();
        }
    }

    private double playhead;

    private AnimationTimelineLayout.Timeline? Timeline => tab?.Timeline;

    protected override Size MeasureOverride(Size available)
    {
        var rows = Timeline?.Rows.Count ?? 0;
        return new Size(double.IsInfinity(available.Width) ? 300 : available.Width, RulerHeight + Math.Max(rows, 1) * RowHeight + 4);
    }

    // MARK: Geometry

    /// <summary>The play-through's length as drawn: never shorter than its last column's end.</summary>
    private double Span(AnimationTimelineLayout.Timeline timeline) =>
        Math.Max(timeline.Columns.Select(c => c.Start + c.DisplayDuration).DefaultIfEmpty(1).Max(), Math.Max(timeline.Duration, 1));

    private double PixelsPerSecond(AnimationTimelineLayout.Timeline timeline) => Math.Max(20, (ActualWidth - LabelWidth - 8) / Span(timeline));

    private double X(AnimationTimelineLayout.Timeline timeline, double seconds) => LabelWidth + seconds * PixelsPerSecond(timeline);

    private double Seconds(AnimationTimelineLayout.Timeline timeline, double x) => Math.Max(0, (x - LabelWidth) / PixelsPerSecond(timeline));

    private static Column? ColumnOf(AnimationTimelineLayout.Timeline timeline, SceneAnimationGroup group) =>
        timeline.Columns.FirstOrDefault(c => c.Group == group);

    private static double Start(AnimationTimelineLayout.Timeline timeline, Block block) => (ColumnOf(timeline, block.Group)?.Start ?? 0) + block.StartOffset;

    private Rect BlockRect(AnimationTimelineLayout.Timeline timeline, int row, Block block)
    {
        var x = X(timeline, Start(timeline, block));
        var width = Math.Max(6, block.Duration * PixelsPerSecond(timeline));
        return new Rect(x, RulerHeight + row * RowHeight + 3, width, RowHeight - 6);
    }

    private static Slot SlotOf(SceneAnimationGroup group) => group.Kind switch
    {
        SceneAnimationGroupKind.Auto => Slot.Auto,
        SceneAnimationGroupKind.Click => Slot.Click(group.Index),
        _ => Slot.Exit,
    };

    /// <summary>The column a time falls in (the last that starts before it).</summary>
    private static Column ColumnAt(AnimationTimelineLayout.Timeline timeline, double seconds) =>
        timeline.Columns.LastOrDefault(c => c.Start <= seconds + 1e-6) ?? timeline.Columns[0];

    private string ColumnTitle(Column column) => column.Group.Kind switch
    {
        SceneAnimationGroupKind.Auto => tab?.IsOverlay == true ? "Goes up" : "With slide",
        SceneAnimationGroupKind.Click => $"Click {column.Group.Index + 1}",
        _ => tab?.IsOverlay == true ? "Taken down" : "Final",
    };

    // MARK: Drawing

    private static readonly Brush LaneBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x24)));
    private static readonly Brush AltLane = Frozen(new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E)));
    private static readonly Pen ColumnPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)), 1) { DashStyle = DashStyles.Dash });
    private static readonly Pen SelectedPen = Frozen(new Pen(Brushes.White, 2));
    private static readonly Pen PlayheadPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xF4, 0x72, 0xB6)), 1.5));
    private static readonly Brush TickBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)));

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    private static Brush KindBrush(AnimationKind kind, bool faded) =>
        new SolidColorBrush((Color)ColorConverter.ConvertFromString(StepRowViewModel.ColorOf(kind))) { Opacity = faded ? 0.55 : 1 };

    private FormattedText Text(string text, double size, Brush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (Timeline is not { } timeline || timeline.Columns.Count == 0) return;
        for (var r = 0; r < timeline.Rows.Count; r++)
            dc.DrawRectangle(r % 2 == 0 ? LaneBrush : AltLane, null, new Rect(0, RulerHeight + r * RowHeight, ActualWidth, RowHeight));
        // Ruler: whole seconds, and each column's heading where it starts.
        var span = Span(timeline);
        for (var s = 0; s <= Math.Ceiling(span); s++)
        {
            var x = X(timeline, s);
            dc.DrawRectangle(TickBrush, null, new Rect(x, RulerHeight - 5, 1, 5));
            if (s % Math.Max(1, (int)Math.Ceiling(28 / PixelsPerSecond(timeline))) == 0)
                dc.DrawText(Text($"{s}s", 9, TickBrush), new Point(x + 2, RulerHeight - 16));
        }
        foreach (var column in timeline.Columns)
        {
            var x = X(timeline, column.Start);
            dc.DrawLine(ColumnPen, new Point(x, 2), new Point(x, ActualHeight));
            dc.DrawText(Text(ColumnTitle(column), 10, EditorKit.Res<Brush>("Text")), new Point(x + 3, 1));
        }
        var selected = tab?.SelectedStepRow?.StepId;
        for (var r = 0; r < timeline.Rows.Count; r++)
        {
            var row = timeline.Rows[r];
            var name = Text(tab?.RowName(row.ObjectId) ?? row.ObjectId, 10, EditorKit.Res<Brush>("MutedText"));
            name.MaxTextWidth = LabelWidth - 8;
            name.MaxLineCount = 1;
            name.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(name, new Point(4, RulerHeight + r * RowHeight + (RowHeight - name.Height) / 2));
            foreach (var block in row.Blocks)
            {
                var rect = DraggedRect(timeline, r, block);
                dc.DrawRoundedRectangle(KindBrush(block.Kind, block.Ranged), block.StepId == selected ? SelectedPen : null, rect, 4, 4);
                if (block.Linked) dc.DrawText(WithFont(Text("", 9, Brushes.White)), new Point(rect.X + 3, rect.Y + 1)); // the chain: after the step before
            }
        }
        var head = X(timeline, playhead);
        dc.DrawLine(PlayheadPen, new Point(head, 0), new Point(head, ActualHeight));
    }

    private static FormattedText WithFont(FormattedText text)
    {
        text.SetFontFamily(EditorKit.IconFont);
        return text;
    }

    // MARK: Gestures

    private enum Grab { None, Body, LeftEdge, RightEdge, Ruler }

    private Grab grab;
    private Block? grabbed;
    private int grabbedRow;
    private Point pressAt;
    private double dragSeconds;
    private bool dragged;

    private (int Row, Block Block)? BlockAt(AnimationTimelineLayout.Timeline timeline, Point p)
    {
        for (var r = 0; r < timeline.Rows.Count; r++)
            foreach (var block in timeline.Rows[r].Blocks.Reverse())
            {
                var rect = BlockRect(timeline, r, block);
                rect.Inflate(EdgeGrab / 2, 0);
                if (rect.Contains(p)) return (r, block);
            }
        return null;
    }

    /// <summary>A block as drawn during a drag: moved, stretched or trimmed by the drag so far.</summary>
    private Rect DraggedRect(AnimationTimelineLayout.Timeline timeline, int row, Block block)
    {
        var rect = BlockRect(timeline, row, block);
        if (!dragged || grabbed?.StepId != block.StepId) return rect;
        var dx = dragSeconds * PixelsPerSecond(timeline);
        return grab switch
        {
            Grab.Body => rect with { X = Math.Max(LabelWidth, rect.X + dx) },
            Grab.RightEdge => rect with { Width = Math.Max(4, rect.Width + dx) },
            Grab.LeftEdge => rect with { X = rect.X + Math.Min(dx, rect.Width - 4), Width = Math.Max(4, rect.Width - dx) },
            _ => rect,
        };
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Timeline is not { } timeline || tab is null) return;
        var p = e.GetPosition(this);
        pressAt = p;
        dragged = false;
        dragSeconds = 0;
        if (p.Y < RulerHeight)
        {
            grab = Grab.Ruler;
            Scrub(timeline, p.X);
        }
        else if (BlockAt(timeline, p) is { } hit)
        {
            grabbed = hit.Block;
            grabbedRow = hit.Row;
            var rect = BlockRect(timeline, hit.Row, hit.Block);
            grab = p.X >= rect.Right - EdgeGrab ? Grab.RightEdge : p.X <= rect.Left + EdgeGrab ? Grab.LeftEdge : Grab.Body;
            tab.PickStep(hit.Block.StepId);
        }
        else
        {
            grab = Grab.Ruler;
            Scrub(timeline, p.X);
        }
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Timeline is not { } timeline) return;
        var p = e.GetPosition(this);
        if (grab == Grab.None)
        {
            Cursor = BlockAt(timeline, p) is { } hit && BlockRect(timeline, hit.Row, hit.Block) is var rect
                ? p.X >= rect.Right - EdgeGrab || p.X <= rect.Left + EdgeGrab ? Cursors.SizeWE : Cursors.SizeAll
                : Cursors.Arrow;
            return;
        }
        if (grab == Grab.Ruler)
        {
            Scrub(timeline, p.X);
            return;
        }
        if (!dragged && Math.Abs(p.X - pressAt.X) < 3) return;
        dragged = true;
        dragSeconds = AnimationTimelineEditing.Snap((p.X - pressAt.X) / PixelsPerSecond(timeline));
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        ReleaseMouseCapture();
        var was = grab;
        grab = Grab.None;
        if (!dragged || grabbed is not { } block || tab is null || Timeline is not { } timeline)
        {
            dragged = false;
            InvalidateVisual();
            return;
        }
        dragged = false;
        switch (was)
        {
            case Grab.Body:
            {
                var at = Math.Max(0, Start(timeline, block) + dragSeconds);
                var column = ColumnAt(timeline, at);
                if (column.Group == block.Group) tab.PlaceStep(block.StepId, at - column.Start);
                else tab.MoveStepInto(block.StepId, SlotOf(column.Group), at - column.Start);
                break;
            }
            case Grab.RightEdge:
                tab.ResizeStep(block.StepId, block.Duration + dragSeconds);
                break;
            case Grab.LeftEdge:
                tab.TrimStepStart(block.StepId, dragSeconds);
                break;
        }
        grabbed = null;
        InvalidateVisual();
    }

    private void Scrub(AnimationTimelineLayout.Timeline timeline, double x)
    {
        Playhead = Math.Min(Seconds(timeline, x), Span(timeline));
        tab?.Scrub(Playhead);
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (Timeline is not { } timeline || tab is null) return;
        var p = e.GetPosition(this);
        var menu = new ContextMenu();
        if (p.Y < RulerHeight)
        {
            var column = ColumnAt(timeline, Seconds(timeline, p.X));
            var slot = SlotOf(column.Group);
            var clicks = timeline.Columns.Count(c => c.Group.Kind == SceneAnimationGroupKind.Click);
            if (column.Group.Kind == SceneAnimationGroupKind.Click)
            {
                var index = column.Group.Index;
                menu.Items.Add(EditorKit.Command("Play this click earlier", () => tab.MoveColumn(slot, Slot.NewClick(index - 1)), index > 0));
                menu.Items.Add(EditorKit.Command("Play this click later", () => tab.MoveColumn(slot, Slot.NewClick(index + 2)), index < clicks - 1));
                menu.Items.Add(EditorKit.Command("Play it with the slide (no click)", () => tab.MoveColumn(slot, Slot.Auto)));
                menu.Items.Add(EditorKit.Separator());
            }
            menu.Items.Add(EditorKit.Command($"Delete every step in \"{ColumnTitle(column)}\"", () => tab.DeleteColumn(slot)));
        }
        else if (BlockAt(timeline, p) is { } hit)
        {
            tab.PickStep(hit.Block.StepId);
            menu.Items.Add(EditorKit.Command(hit.Block.Linked ? "Play with the step before (unlink)" : "Play after the step before (link)", () => tab.ToggleLink(hit.Block.StepId)));
            menu.Items.Add(EditorKit.Command("Duplicate", () => tab.DuplicateStepCommand.Execute(null)));
            menu.Items.Add(EditorKit.Separator());
            menu.Items.Add(EditorKit.Command("Delete", () => tab.RemoveStep(hit.Block.StepId)));
        }
        else return;
        menu.PlacementTarget = this;
        menu.IsOpen = true;
        e.Handled = true;
    }
}
