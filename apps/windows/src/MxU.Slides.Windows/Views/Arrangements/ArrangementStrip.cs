using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Rendering;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The arrangement's sections as pills under the song's title (Verse 1 · Chorus · Verse 2 · Chorus):
/// click one to jump the slides there; with an arrangement picked, drag a pill to move that block,
/// right-click it to remove it, and + adds a section. The pill of the live slide's block is ringed.
/// With no arrangement picked the pills follow the slides as written and can't be moved. Port of the
/// Mac's ArrangementStrip (ServiceContinuousView.swift) and SectionPreviewPicker; the rules are
/// Core's (<see cref="ArrangementEditing"/>).
/// </summary>
public sealed class ArrangementStrip : UserControl
{
    private const string DragFormat = "mxu/arrangement-block";

    private readonly WrapPanel pills = new();
    private MainViewModel? model;
    private Point dragStart;
    private int? dragIndex;
    private DropLine? dropMark;

    public ArrangementStrip()
    {
        Content = pills;
        AllowDrop = true;
        DataContextChanged += (_, _) => Attach(OperatorKit.Model(this));
        Drop += OnDrop;
        DragOver += OnDragOver;
        DragLeave += (_, _) => ClearDropMark();
    }

    private void Attach(MainViewModel? next)
    {
        if (model is not null)
        {
            model.ArrangementsChanged -= Rebuild;
            model.PropertyChanged -= OnModelChanged;
        }
        model = next;
        if (model is not null)
        {
            model.ArrangementsChanged += Rebuild;
            model.PropertyChanged += OnModelChanged;
        }
        Rebuild();
    }

    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.SelectedArrangement) or nameof(MainViewModel.CurrentPresentation)) Rebuild();
        // A different slide went live: the ring moves to its block.
        else if (e.PropertyName is nameof(MainViewModel.LiveDetail)) MarkLive();
    }

    /// <summary>The pills' names, in order (the self-test reads these).</summary>
    internal List<string> PillNames { get; } = [];

    private readonly List<(Border Pill, int Start)> shown = [];

    internal void Rebuild()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(Rebuild);
            return;
        }
        pills.Children.Clear();
        PillNames.Clear();
        shown.Clear();
        if (model?.CurrentPresentation is not { } p || !ArrangementEditing.ShowsArrangements(p))
        {
            Visibility = Visibility.Collapsed;
            return;
        }
        Visibility = Visibility.Visible;
        var arrangement = model.CurrentArrangement;
        var starts = SlideSceneBuilder.ArrangementBlockStarts(p, arrangement?.Id);
        if (arrangement is not null)
        {
            for (var i = 0; i < arrangement.SectionIds.Count; i++)
            {
                var sectionId = arrangement.SectionIds[i];
                var section = p.Sections?.FirstOrDefault(s => s.Id == sectionId);
                var pill = Pill(ArrangementEditing.SectionName(p, sectionId), section, prominent: true, i < starts.Count ? starts[i] : 0);
                if (ArrangementEditing.SectionIsEmpty(p, sectionId))
                {
                    pill.Opacity = 0.45;
                    pill.ToolTip = $"{ArrangementEditing.SectionName(p, sectionId)} has no slides yet";
                }
                if (model.CanChangeArrangement) MakeMovable(pill, arrangement.Id, i);
                pills.Children.Add(pill);
            }
            if (model.CanChangeArrangement) pills.Children.Add(AddButton(arrangement.Id));
        }
        else
        {
            var runs = ArrangementEditing.BaseRunSections(p);
            for (var i = 0; i < runs.Count; i++) pills.Children.Add(Pill(runs[i].Name, runs[i], prominent: false, i < starts.Count ? starts[i] : 0));
        }
        MarkLive();
    }

    private Border Pill(string name, PresentationSection? section, bool prominent, int start)
    {
        PillNames.Add(name);
        var hex = StarterContent.GroupColor(section?.Name, section?.ColorHex);
        var brush = Painting.Brush(hex, new SceneColor(0.36, 0.36, 0.36));
        var pill = new Border
        {
            CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 1, 8, 2), Margin = new Thickness(0, 0, 4, 4),
            Background = brush, Opacity = prominent ? 1 : 0.6, Cursor = Cursors.Hand,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = name, FontSize = 11, FontWeight = FontWeights.Medium, Foreground = Brushes.White },
            ToolTip = "Jump the slides here",
        };
        pill.MouseLeftButtonUp += (_, e) =>
        {
            if (dragIndex is not null) return;
            Jump(start);
            e.Handled = true;
        };
        shown.Add((pill, start));
        return pill;
    }

    /// <summary>Rings the pill whose block holds the live slide, when this song is the one live.</summary>
    private void MarkLive()
    {
        if (model is null) return;
        var live = model.Live is { } context && context.PresentationId == model.CurrentPresentation?.Id
            && context.ArrangementId == model.CurrentArrangement?.Id && model.Show.Live is not null ? context.Occurrence : (int?)null;
        var index = live is { } occurrence ? ArrangementEditing.BlockIndex([.. shown.Select(s => s.Start)], occurrence) : null;
        for (var i = 0; i < shown.Count; i++)
        {
            shown[i].Pill.BorderBrush = i == index ? EditorKit.Res<Brush>("Live") : new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            shown[i].Pill.BorderThickness = new Thickness(i == index ? 2 : 1);
        }
        LivePill = index is { } at ? PillNames.ElementAtOrDefault(at) : null;
    }

    /// <summary>The ringed pill's name (the self-test reads it).</summary>
    internal string? LivePill { get; private set; }

    /// <summary>Scrolls the slides to a block's first slide: the grid's tile, or its card in the continuous view.</summary>
    private void Jump(int start)
    {
        if (Window.GetWindow(this) is not MainWindow window || model is null) return;
        window.RevealSlide(start);
    }

    // MARK: Adding, removing and moving blocks

    private Button AddButton(string arrangementId)
    {
        var add = OperatorKit.IconButton("", "Add a section", () => { }, 10);
        add.Margin = new Thickness(0, 0, 0, 4);
        add.Click += (_, _) => OperatorKit.Open(SectionMenu(model, arrangementId), add);
        return add;
    }

    /// <summary>The song's sections, each with its slide count and first line (the Mac's SectionPreviewPicker rows).</summary>
    internal static ContextMenu SectionMenu(MainViewModel? model, string arrangementId)
    {
        List<object> items = [];
        foreach (var (section, slides, firstLine) in model?.SectionChoices() ?? [])
        {
            var header = new StackPanel();
            var top = new StackPanel { Orientation = Orientation.Horizontal };
            top.Children.Add(new Border
            {
                Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
                Background = Painting.Brush(StarterContent.GroupColor(section.Name, section.ColorHex), new SceneColor(0.5, 0.5, 0.5)),
            });
            top.Children.Add(new TextBlock { Text = section.Name, FontWeight = FontWeights.Medium });
            top.Children.Add(new TextBlock { Text = slides == 1 ? "  1 slide" : $"  {slides} slides", FontSize = 10, Foreground = EditorKit.Res<Brush>("FaintText"), VerticalAlignment = VerticalAlignment.Center });
            header.Children.Add(top);
            if (firstLine.Length > 0)
                header.Children.Add(new TextBlock { Text = firstLine, FontSize = 11, Foreground = EditorKit.Res<Brush>("MutedText"), Margin = new Thickness(14, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 280 });
            var id = section.Id;
            var item = new MenuItem { Header = header, Style = (Style)EditorKit.Styles["CascadingMenuItem"] };
            item.Click += (_, _) => model?.AddArrangementBlock(arrangementId, id);
            items.Add(item);
        }
        if (items.Count == 0) items.Add(EditorKit.Command("Group slides into sections first (Edit lyrics)", () => { }, enabled: false));
        return OperatorKit.Menu(items);
    }

    private void MakeMovable(Border pill, string arrangementId, int index)
    {
        pill.ContextMenu = OperatorKit.Menu(
        [
            EditorKit.Command("Remove Section", () => model?.RemoveArrangementBlock(arrangementId, index)),
        ]);
        pill.PreviewMouseLeftButtonDown += (_, e) => dragStart = e.GetPosition(this);
        pill.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            var moved = e.GetPosition(this) - dragStart;
            if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            dragIndex = index;
            try { DragDrop.DoDragDrop(pill, new DataObject(DragFormat, $"{arrangementId}|{index}"), DragDropEffects.Move); }
            finally
            {
                dragIndex = null;
                ClearDropMark();
            }
        };
    }

    /// <summary>Where a dragged pill would land: before the pill under the pointer (or after it, past its middle).</summary>
    private int? DropBefore(Point point)
    {
        var movable = pills.Children.OfType<Border>().ToList();
        for (var i = 0; i < movable.Count; i++)
        {
            var pill = movable[i];
            var origin = pill.TranslatePoint(new Point(0, 0), this);
            var bounds = new Rect(origin, new Size(pill.ActualWidth + pill.Margin.Right, pill.ActualHeight + pill.Margin.Bottom));
            if (bounds.Contains(point)) return point.X < origin.X + pill.ActualWidth / 2 ? i : i + 1;
        }
        return movable.Count > 0 ? movable.Count : null;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DragFormat) || DropBefore(e.GetPosition(this)) is not { } before)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        e.Effects = DragDropEffects.Move;
        ShowDropMark(before);
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        ClearDropMark();
        if (e.Data.GetData(DragFormat) is not string payload || DropBefore(e.GetPosition(this)) is not { } before) return;
        var parts = payload.Split('|');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var from)) return;
        model?.MoveArrangementBlock(parts[0], from, before);
        e.Handled = true;
    }

    private int? markedBefore;

    private void ShowDropMark(int before)
    {
        if (markedBefore == before && dropMark is not null) return;
        ClearDropMark();
        markedBefore = before;
        var movable = pills.Children.OfType<Border>().ToList();
        if (movable.Count == 0) return;
        dropMark = before < movable.Count ? DropLine.Show(movable[before], DropEdge.Left) : DropLine.Show(movable[^1], DropEdge.Right);
    }

    private void ClearDropMark()
    {
        dropMark?.Remove();
        dropMark = null;
        markedBefore = null;
    }
}
