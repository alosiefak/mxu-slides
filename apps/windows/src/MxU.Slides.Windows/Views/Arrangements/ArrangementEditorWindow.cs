using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Rendering;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The arrangement editor (Phase 8): the open song's arrangements on the left (New, Duplicate,
/// Delete, and which one plays by default), and the picked one's name, its order as text
/// ("V1, C x2, B, C") and its blocks on the right. Blocks are added from the song's sections,
/// moved by dragging or with ↑ ↓, and removed with ✕; typing the order does the same in one go.
/// Each change saves the song at once (a Mac sees it). When the song has a section map for its
/// multitrack, sections with no audio are named. Port of the Mac's ArrangementPopover.swift and
/// SectionPreviewPicker.swift, as a window.
/// </summary>
public sealed class ArrangementEditorWindow : Window
{
    private const string DragFormat = "mxu/arrangement-editor-block";

    private static ArrangementEditorWindow? open;

    private readonly MainViewModel model;
    private readonly ListBox arrangements = new() { Margin = new Thickness(0, 6, 0, 6) };
    private readonly StackPanel detail = new();
    private string? picked;
    private bool refreshing;
    private DropLine? dropLine;

    public static ArrangementEditorWindow? Open(Window owner, MainViewModel model)
    {
        if (!model.Permit(Core.Control.OperatorAction.ChangeArrangement) || model.CurrentPresentation is null) return null;
        if (open is null)
        {
            open = new ArrangementEditorWindow(owner, model);
            open.Closed += (_, _) => open = null;
            open.Show();
        }
        open.Activate();
        return open;
    }

    private ArrangementEditorWindow(Window owner, MainViewModel model)
    {
        this.model = model;
        EditorKit.Dress(this, owner, $"Arrangements · {model.CurrentPresentation?.Name}", 760, 560);
        picked = model.CurrentArrangement?.Id;

        var left = new DockPanel { Width = 230, Margin = new Thickness(0, 0, 14, 0) };
        var buttons = new WrapPanel();
        buttons.Children.Add(EditorKit.Button("+ New", () => Pick(model.NewArrangement()?.Id), "A new arrangement: each section once, in slide order"));
        buttons.Children.Add(EditorKit.Button("Duplicate", () => { if (picked is { } id) Pick(model.DuplicateArrangement(id)?.Id); }));
        buttons.Children.Add(EditorKit.Button("Delete", DeletePicked));
        DockPanel.SetDock(buttons, Dock.Bottom);
        left.Children.Add(buttons);
        var title = EditorKit.Section("ARRANGEMENTS", 0);
        DockPanel.SetDock(title, Dock.Top);
        left.Children.Add(title);
        left.Children.Add(arrangements);
        arrangements.SelectionChanged += (_, _) =>
        {
            if (refreshing || arrangements.SelectedItem is not ListBoxItem { Tag: string id }) return;
            picked = id;
            model.ChooseArrangement(id);
            ShowDetail();
        };

        var body = new DockPanel();
        DockPanel.SetDock(left, Dock.Left);
        body.Children.Add(left);
        body.Children.Add(new ScrollViewer { Content = detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        // Padding on a border, not a margin on the panel, so a picture of the window (the self-test's) isn't shifted.
        Content = new Border { Padding = new Thickness(18, 14, 18, 14), Child = body };
        model.ArrangementsChanged += OnChanged;
        Closed += (_, _) => model.ArrangementsChanged -= OnChanged;
        Refresh();
    }

    private Presentation? Song => model.CurrentPresentation;

    private void OnChanged() => Dispatcher.BeginInvoke(Refresh);

    private void Pick(string? id)
    {
        if (id is null) return;
        picked = id;
        Refresh();
    }

    private void Refresh()
    {
        if (Song is not { } p)
        {
            Close();
            return;
        }
        Title = $"Arrangements · {p.Name}";
        refreshing = true;
        arrangements.Items.Clear();
        foreach (var arrangement in p.Arrangements ?? [])
        {
            var row = new DockPanel();
            if (p.DefaultArrangementId == arrangement.Id)
            {
                var star = new TextBlock { Text = "default", FontSize = 10, Foreground = EditorKit.Res<Brush>("FaintText"), VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(star, Dock.Right);
                row.Children.Add(star);
            }
            row.Children.Add(new TextBlock { Text = arrangement.Name, TextTrimming = TextTrimming.CharacterEllipsis });
            arrangements.Items.Add(new ListBoxItem { Content = row, Tag = arrangement.Id, Padding = new Thickness(6, 4, 6, 4) });
        }
        if (picked is null || ArrangementEditing.Find(p, picked) is null) picked = p.Arrangements?.FirstOrDefault()?.Id;
        arrangements.SelectedItem = arrangements.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == picked);
        refreshing = false;
        ShowDetail();
    }

    /// <summary>The picked arrangement's blocks as shown (the self-test reads these).</summary>
    internal List<string> BlockNames { get; } = [];

    internal string OrderText { get; private set; } = "";

    private void ShowDetail()
    {
        detail.Children.Clear();
        BlockNames.Clear();
        if (Song is not { } p) return;
        if ((p.Sections ?? []).Count == 0)
        {
            detail.Children.Add(EditorKit.Muted("Group the slides into sections first (Edit lyrics names them: Verse 1, Chorus…). Then an arrangement can repeat a chorus without copying its slides.", 12));
            return;
        }
        if (picked is null || ArrangementEditing.Find(p, picked) is not { } arrangement)
        {
            detail.Children.Add(EditorKit.Muted("The song plays its slides as written. Make an arrangement to play its sections in another order (repeat a chorus, leave a verse out), then pick it for each service.", 12));
            return;
        }
        var id = arrangement.Id;

        var name = EditorKit.CommitBox(arrangement.Name, typed =>
        {
            model.RenameArrangement(id, typed);
            return ArrangementEditing.Find(p, id)?.Name ?? typed;
        }, "Name");
        detail.Children.Add(EditorKit.Row("Name", name));

        OrderText = ArrangementEditing.OrderText(p, id);
        var order = EditorKit.CommitBox(OrderText, typed =>
        {
            if (!model.SetArrangementOrder(id, typed) && typed.Trim().Length > 0 && SongEditing.ParseOrder(typed, p.Sections ?? []).Count == 0)
                model.Notice = "That order doesn't name any of this song's sections (try V1, C, V2, C x2).";
            return ArrangementEditing.OrderText(p, id);
        }, "V1, C, V2, C x2, B, C");
        order.ToolTip = "Type the order: section names or V1, C, B, PC, T… and x2 to repeat. Enter applies it.";
        detail.Children.Add(EditorKit.Row("Order", order));

        var isDefault = new CheckBox { Content = "Play this by default", IsChecked = p.DefaultArrangementId == id, Margin = new Thickness(96, 0, 0, 8),
            ToolTip = "What the song plays when a service row doesn't pick an arrangement" };
        isDefault.Click += (_, _) => model.SetDefaultArrangement(isDefault.IsChecked == true ? id : null);
        detail.Children.Add(isDefault);

        detail.Children.Add(EditorKit.Section("BLOCKS"));
        var list = new StackPanel { AllowDrop = true, Background = Brushes.Transparent };
        for (var i = 0; i < arrangement.SectionIds.Count; i++) list.Children.Add(BlockRow(p, id, arrangement.SectionIds[i], i, arrangement.SectionIds.Count));
        list.DragOver += (_, e) => OnDragOver(list, e);
        list.DragLeave += (_, _) => ClearLine();
        list.Drop += (_, e) => OnDrop(list, id, e);
        detail.Children.Add(list);

        var foot = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var count = EditorKit.Faint($"{ArrangementEditing.ArrangedSlideCount(p, id)} slides", 11);
        DockPanel.SetDock(count, Dock.Right);
        foot.Children.Add(count);
        var add = EditorKit.Button("+ Add Section", () => { });
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Click += (_, _) => OperatorKit.Open(ArrangementStrip.SectionMenu(model, id), add);
        foot.Children.Add(add);
        detail.Children.Add(foot);

        // The multitrack's section map: name the played sections that have no audio.
        if (p.SongMap is { } map)
        {
            var names = (p.Sections ?? []).GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First().Name);
            var missing = SongMaps.Missing(map, PresentationTimelines.Blocks(p, id), names);
            if (missing.Count > 0)
                detail.Children.Add(new TextBlock
                {
                    Text = $"⚠ {string.Join(", ", missing)} {(missing.Count == 1 ? "has" : "have")} no audio in this song's tracks",
                    Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x95, 0x00)), FontSize = 11, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap,
                });
        }
    }

    private Border BlockRow(Presentation p, string arrangementId, string sectionId, int index, int count)
    {
        var section = p.Sections?.FirstOrDefault(s => s.Id == sectionId);
        var sectionName = ArrangementEditing.SectionName(p, sectionId);
        BlockNames.Add(sectionName);
        var row = new DockPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var up = EditorKit.Ghost("↑", "Move earlier", () => model.MoveArrangementBlock(arrangementId, index, index - 1));
        up.IsEnabled = index > 0;
        var down = EditorKit.Ghost("↓", "Move later", () => model.MoveArrangementBlock(arrangementId, index, index + 2));
        down.IsEnabled = index < count - 1;
        buttons.Children.Add(up);
        buttons.Children.Add(down);
        buttons.Children.Add(EditorKit.Ghost("✕", "Remove this block", () => model.RemoveArrangementBlock(arrangementId, index)));
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(new TextBlock { Text = "", FontFamily = EditorKit.IconFont, FontSize = 11, Foreground = EditorKit.Res<Brush>("FaintText"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        row.Children.Add(new Border
        {
            Width = 9, Height = 9, CornerRadius = new CornerRadius(4.5), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = Painting.Brush(StarterContent.GroupColor(section?.Name, section?.ColorHex), new SceneColor(0.5, 0.5, 0.5)),
        });
        var empty = ArrangementEditing.SectionIsEmpty(p, sectionId);
        row.Children.Add(new TextBlock
        {
            Text = empty ? $"{sectionName}  (no slides yet)" : sectionName, VerticalAlignment = VerticalAlignment.Center,
            Foreground = empty ? EditorKit.Res<Brush>("FaintText") : EditorKit.Res<Brush>("Text"),
        });
        var card = new Border
        {
            Child = row, Padding = new Thickness(8, 3, 4, 3), Margin = new Thickness(0, 0, 0, 3), CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF)), Tag = index, Cursor = Cursors.SizeAll,
        };
        var start = new Point();
        card.PreviewMouseLeftButtonDown += (_, e) => start = e.GetPosition(this);
        card.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || e.OriginalSource is DependencyObject source && IsInButton(source)) return;
            var moved = e.GetPosition(this) - start;
            if (Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            DragDrop.DoDragDrop(card, new DataObject(DragFormat, index), DragDropEffects.Move);
            ClearLine();
        };
        return card;
    }

    private static bool IsInButton(DependencyObject node)
    {
        for (var current = node; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is Button) return true;
        return false;
    }

    /// <summary>The block a drop lands before: the row under the pointer (or the one after it, past its middle).</summary>
    private static int DropBefore(StackPanel list, DragEventArgs e, out Border? over, out bool after)
    {
        over = null;
        after = false;
        var cards = list.Children.OfType<Border>().ToList();
        for (var i = 0; i < cards.Count; i++)
        {
            var point = e.GetPosition(cards[i]);
            if (point.Y < 0 || point.Y > cards[i].ActualHeight + cards[i].Margin.Bottom) continue;
            over = cards[i];
            after = point.Y > cards[i].ActualHeight / 2;
            return after ? i + 1 : i;
        }
        over = cards.LastOrDefault();
        after = true;
        return cards.Count;
    }

    private void OnDragOver(StackPanel list, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
        if (e.Effects == DragDropEffects.None) return;
        DropBefore(list, e, out var over, out var after);
        ClearLine();
        if (over is not null) dropLine = DropLine.Show(over, after ? DropEdge.Bottom : DropEdge.Top);
    }

    private void OnDrop(StackPanel list, string arrangementId, DragEventArgs e)
    {
        ClearLine();
        if (e.Data.GetData(DragFormat) is not int from) return;
        var before = DropBefore(list, e, out _, out _);
        model.MoveArrangementBlock(arrangementId, from, before);
        e.Handled = true;
    }

    private void ClearLine()
    {
        dropLine?.Remove();
        dropLine = null;
    }

    private void DeletePicked()
    {
        if (picked is not { } id || Song is not { } p || ArrangementEditing.Find(p, id) is not { } arrangement) return;
        if (MessageBox.Show(this, $"Delete the arrangement “{arrangement.Name}”? The song's slides stay as they are.", "Delete arrangement",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        picked = null;
        model.DeleteArrangement(id);
    }
}
