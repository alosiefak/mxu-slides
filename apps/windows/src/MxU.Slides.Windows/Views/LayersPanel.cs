using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MxU.Slides.Core.Editing;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The slide editor's layers list, top first, as the Mac's objects panel (SlideEditorView.swift,
/// objectsPanel): click a row to pick an object (Ctrl and Shift add), double-click or F2 to rename
/// it, the eye hides it from every screen, the padlock locks it on the canvas, and dragging a
/// row moves the object up or down the stack (a line shows where it will land; Esc cancels). The
/// theme's own decor is listed greyed, where it's drawn, and can't be picked. Right-click for the
/// arranging commands. Built in code, like the operator panels.
/// </summary>
public sealed class LayersPanel : DockPanel
{
    private const string DragFormat = "mxu/layer";

    private readonly StackPanel rows = new();
    private readonly Border insertLine = new() { Height = 2, Background = EditorKit.Res<Brush>("Accent"), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly Canvas lineHost = new() { IsHitTestVisible = false };
    private MainViewModel? model;
    private string? renaming;
    private string? lastClicked;
    private Point pressAt;
    private string? pressed;

    public LayersPanel()
    {
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(Small("", "Bring forward", () => model?.MoveLayers(LayerMove.BringForward)));
        buttons.Children.Add(Small("", "Send backward", () => model?.MoveLayers(LayerMove.SendBackward)));
        DockPanel.SetDock(buttons, Dock.Right);
        header.Children.Add(buttons);
        header.Children.Add(new TextBlock { Text = "Layers (top first)", Style = EditorKit.Res<Style>("Muted"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        SetDock(header, Dock.Top);
        Children.Add(header);

        var scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = rows };
        var stack = new Grid();
        stack.Children.Add(new Border
        {
            Background = EditorKit.Res<Brush>("Raised"), CornerRadius = new CornerRadius(4), Child = scroller,
        });
        lineHost.Children.Add(insertLine);
        stack.Children.Add(lineHost);
        Children.Add(stack);

        AllowDrop = true;
        DragOver += OnDragOver;
        DragLeave += (_, _) => insertLine.Visibility = Visibility.Collapsed;
        Drop += OnDrop;
        Focusable = true;
        KeyDown += OnKeyDown;
        DataContextChanged += (_, _) => Bind(DataContext as MainViewModel);
        IsVisibleChanged += (_, _) => Rebuild();
    }

    private static Button Small(string glyph, string tip, Action click)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = glyph, FontFamily = EditorKit.IconFont, FontSize = 10 },
            ToolTip = tip, Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(3, 0, 0, 0), MinHeight = 18,
        };
        button.Click += (_, _) => click();
        return button;
    }

    private void Bind(MainViewModel? value)
    {
        if (model is not null)
        {
            model.LayersChanged -= Rebuild;
            model.PropertyChanged -= OnModelChanged;
        }
        model = value;
        if (model is not null)
        {
            model.LayersChanged += Rebuild;
            model.PropertyChanged += OnModelChanged;
        }
        Rebuild();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsEditing) or nameof(MainViewModel.SelectedSlide)) Rebuild();
    }

    /// <summary>The rows, drawn again from the model (cheap: a slide has a handful of objects).</summary>
    public void Rebuild()
    {
        if (!IsVisible && rows.Children.Count > 0) return;
        rows.Children.Clear();
        var list = model?.LayerRows ?? [];
        if (list.Count == 0)
        {
            rows.Children.Add(new TextBlock { Text = "No objects", Style = EditorKit.Res<Style>("Muted"), Margin = new Thickness(8, 6, 8, 6) });
            return;
        }
        foreach (var row in list) rows.Children.Add(RowFor(row));
    }

    private FrameworkElement RowFor(LayerRow row)
    {
        var line = new DockPanel { Margin = new Thickness(6, 2, 4, 2), LastChildFill = true };
        var icon = new TextBlock
        {
            Text = Glyph(row), FontFamily = EditorKit.IconFont, FontSize = 12, Width = 18, VerticalAlignment = VerticalAlignment.Center,
            Foreground = EditorKit.Res<Brush>("MutedText"),
        };
        SetDock(icon, Dock.Left);
        line.Children.Add(icon);
        if (!row.FromTheme)
        {
            var lockButton = Toggle(row.Locked ? "" : "", row.Locked ? "Locked on the canvas: click to unlock" : "Lock it on the canvas", row.Locked,
                () => model?.ToggleLocked(row.Id));
            var eye = Toggle(row.Hidden ? "" : "", row.Hidden ? "Hidden on every screen: click to show" : "Hide it on every screen", row.Hidden,
                () => model?.ToggleHidden(row.Id));
            SetDock(lockButton, Dock.Right);
            SetDock(eye, Dock.Right);
            line.Children.Add(lockButton);
            line.Children.Add(eye);
        }
        else
        {
            var palette = new TextBlock { Text = "", FontFamily = EditorKit.IconFont, FontSize = 11, Foreground = EditorKit.Res<Brush>("FaintText"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
            SetDock(palette, Dock.Right);
            line.Children.Add(palette);
        }
        if (renaming == row.Id && !row.FromTheme) line.Children.Add(RenameBox(row));
        else line.Children.Add(new TextBlock
        {
            Text = row.Grouped ? $"{row.Name}  ·  grouped" : row.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            Foreground = EditorKit.Res<Brush>(row.FromTheme ? "FaintText" : "Text"),
        });

        var frame = new Border
        {
            Child = line, CornerRadius = new CornerRadius(3), Margin = new Thickness(2, 1, 2, 1), Tag = row,
            Background = row.Selected ? EditorKit.Res<Brush>("AccentWash") : Brushes.Transparent,
            Opacity = row.Hidden ? 0.5 : 1,
            ToolTip = row.FromTheme ? "From the theme: change it in the theme's design" : null,
        };
        if (row.FromTheme) return frame;
        frame.MouseLeftButtonDown += (_, e) =>
        {
            Focus();
            if (e.ClickCount == 2)
            {
                BeginRename(row.Id);
                e.Handled = true;
                return;
            }
            pressAt = e.GetPosition(this);
            pressed = row.Id;
            Pick(row);
        };
        frame.MouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || pressed != row.Id) return;
            var moved = e.GetPosition(this) - pressAt;
            if (Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            pressed = null;
            // Esc while dragging cancels it (DoDragDrop's own rule).
            DragDrop.DoDragDrop(frame, new DataObject(DragFormat, row.Id), DragDropEffects.Move);
            insertLine.Visibility = Visibility.Collapsed;
        };
        frame.MouseLeftButtonUp += (_, _) => pressed = null;
        frame.MouseRightButtonUp += (_, e) =>
        {
            if (model?.LayerRows.FirstOrDefault(r => r.Id == row.Id) is { Selected: false }) model.PickLayers([row.Id]);
            OpenMenu(row, frame);
            e.Handled = true;
        };
        return frame;
    }

    private static Button Toggle(string glyph, string tip, bool on, Action click)
    {
        var button = new Button
        {
            // The colour goes on the glyph itself: the ghost button's own style sets the button's.
            Content = new TextBlock { Text = glyph, FontFamily = EditorKit.IconFont, FontSize = 11, Foreground = EditorKit.Res<Brush>(on ? "Warn" : "MutedText") },
            ToolTip = tip, Style = EditorKit.Res<Style>("GhostButton"), Padding = new Thickness(4, 0, 4, 0), MinHeight = 18,
            Margin = new Thickness(2, 0, 0, 0),
        };
        button.Click += (_, e) =>
        {
            click();
            e.Handled = true;
        };
        return button;
    }

    private static string Glyph(LayerRow row) => row switch
    {
        { Kind: SlideObjectKind.Text } => "",   // Font
        { ShowsMedia: true } => "",             // Pictures
        { Kind: SlideObjectKind.Shape } => "",  // a square
        _ => "",                               // a screen (live inputs, mirrors)
    };

    private void Pick(LayerRow row)
    {
        if (model is null) return;
        var current = model.LayerRows.Where(r => r.Selected).Select(r => r.Id).ToList();
        IEnumerable<string> next;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && lastClicked is { } anchor)
        {
            var all = model.LayerRows.Where(r => !r.FromTheme).Select(r => r.Id).ToList();
            var (a, b) = (all.IndexOf(anchor), all.IndexOf(row.Id));
            next = a < 0 || b < 0 ? [row.Id] : all.Skip(Math.Min(a, b)).Take(Math.Abs(a - b) + 1);
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            next = current.Contains(row.Id) ? current.Where(id => id != row.Id) : current.Append(row.Id);
            lastClicked = row.Id;
        }
        else
        {
            next = [row.Id];
            lastClicked = row.Id;
        }
        model.PickLayers(next);
    }

    // MARK: Renaming

    public void BeginRename(string id)
    {
        renaming = id;
        Rebuild();
    }

    private TextBox RenameBox(LayerRow row)
    {
        var box = new TextBox { Text = row.Name, MinWidth = 60, Padding = new Thickness(2, 0, 2, 0) };
        var done = false;
        void Finish(bool keep)
        {
            if (done) return;
            done = true;
            renaming = null;
            if (keep) model?.RenameObject(row.Id, box.Text);
            Dispatcher.BeginInvoke(Rebuild);
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Finish(keep: true);
            else if (e.Key == Key.Escape) Finish(keep: false);
            else return;
            e.Handled = true;
        };
        box.LostKeyboardFocus += (_, _) => Finish(keep: true);
        box.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        return box;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2 && model?.LayerRows.FirstOrDefault(r => r.Selected) is { } row)
        {
            BeginRename(row.Id);
            e.Handled = true;
        }
    }

    // MARK: Dragging a row

    /// <summary>The row a drop at <paramref name="point"/> lands above (null: the bottom), and where the line goes.</summary>
    private (string? BeforeId, double LineY) DropSpot(Point point)
    {
        Border? first = null;
        foreach (var child in rows.Children.OfType<Border>())
        {
            first ??= child;
            var top = child.TranslatePoint(new Point(0, 0), lineHost).Y;
            if (point.Y < top + child.ActualHeight / 2) return (((LayerRow)child.Tag).Id, top - 1);
        }
        var last = rows.Children.OfType<Border>().LastOrDefault();
        return (null, last is null ? 0 : last.TranslatePoint(new Point(0, last.ActualHeight), lineHost).Y);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DragFormat))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        var (_, y) = DropSpot(e.GetPosition(lineHost));
        insertLine.Width = Math.Max(0, lineHost.ActualWidth - 8);
        Canvas.SetLeft(insertLine, 4);
        Canvas.SetTop(insertLine, y);
        insertLine.Visibility = Visibility.Visible;
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        insertLine.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(DragFormat) is not string id || model is null) return;
        var (before, _) = DropSpot(e.GetPosition(lineHost));
        if (before != id) model.MoveLayer(id, before);
        e.Handled = true;
    }

    // MARK: The right-click menu

    private void OpenMenu(LayerRow row, FrameworkElement under)
    {
        if (model is null) return;
        var menu = new ContextMenu { PlacementTarget = under };
        menu.Items.Add(EditBar.Item("Rename…", "F2", true, () => BeginRename(row.Id)));
        menu.Items.Add(EditBar.Item(row.Hidden ? "Show" : "Hide", null, true, () => model.ToggleHidden(row.Id)));
        menu.Items.Add(EditBar.Item(row.Locked ? "Unlock" : "Lock", null, true, () => model.ToggleLocked(row.Id)));
        menu.Items.Add(EditBar.Item("Duplicate", "Ctrl+D", true, () => model.Duplicate(EditTarget.Objects)));
        menu.Items.Add(new Separator());
        foreach (var move in Enum.GetValues<LayerMove>())
            menu.Items.Add(EditBar.Item(Layers.Title(move), null, model.CanMoveLayers(move), () => model.MoveLayers(move)));
        menu.Items.Add(new Separator());
        menu.Items.Add(EditBar.Item("Group", "Ctrl+G", model.CanGroupObjects, () => model.GroupObjectsCommand.Execute(null)));
        menu.Items.Add(EditBar.Item("Ungroup", "Ctrl+Shift+G", model.CanUngroupObjects, () => model.UngroupObjectsCommand.Execute(null)));
        menu.Items.Add(new Separator());
        menu.Items.Add(EditBar.Item("Delete", "Delete", true, () => model.DeleteSelectedObjectsCommand.Execute(null)));
        menu.IsOpen = true;
    }
}
