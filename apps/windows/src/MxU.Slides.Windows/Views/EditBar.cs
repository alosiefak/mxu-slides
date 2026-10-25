using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The toolbar's undo and redo buttons and its Edit menu: the Mac's Edit menu, on a window
/// without a menu bar. The buttons' tooltips say what they'll undo ("Undo Move (Ctrl+Z)"),
/// and the menu lists the clipboard, arranging and text commands with their keys.
/// </summary>
public sealed class EditBar : StackPanel
{
    private readonly Button undo;
    private readonly Button redo;
    private MainViewModel? model;

    public EditBar()
    {
        Orientation = Orientation.Horizontal;
        undo = IconButton("", () => model?.UndoEditCommand.Execute(null));
        redo = IconButton("", () => model?.RedoEditCommand.Execute(null));
        var menu = new Button { Margin = new Thickness(0, 0, 0, 0), ToolTip = "Edit: undo, the clipboard, arranging, find and replace" };
        menu.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new TextBlock { Text = "Edit", VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = "", FontFamily = EditorKit.IconFont, FontSize = 9, Margin = new Thickness(6, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center },
            },
        };
        menu.Click += (_, _) => OpenMenu(menu);
        Children.Add(undo);
        Children.Add(redo);
        Children.Add(menu);
        DataContextChanged += (_, _) => Bind(DataContext as MainViewModel);
    }

    private static Button IconButton(string glyph, Action click)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = glyph, FontFamily = EditorKit.IconFont, FontSize = 14 },
            Margin = new Thickness(0, 0, 4, 0),
            Padding = new Thickness(8, 4, 8, 4),
        };
        ToolTipService.SetShowOnDisabled(button, true);
        button.Click += (_, _) => click();
        return button;
    }

    private void Bind(MainViewModel? value)
    {
        if (model is not null) model.PropertyChanged -= OnModelChanged;
        model = value;
        if (model is not null) model.PropertyChanged += OnModelChanged;
        Update();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.UndoMenuText) or nameof(MainViewModel.RedoMenuText)
            or nameof(MainViewModel.CanUndoEdit) or nameof(MainViewModel.CanRedoEdit))
            Update();
    }

    private void Update()
    {
        undo.ToolTip = $"{model?.UndoMenuText ?? "Undo"} (Ctrl+Z)";
        redo.ToolTip = $"{model?.RedoMenuText ?? "Redo"} (Ctrl+Y)";
        undo.IsEnabled = model?.CanUndoEdit == true;
        redo.IsEnabled = model?.CanRedoEdit == true;
    }

    /// <summary>The menu's entries; built each time it opens, so each says what it would do now.</summary>
    public static ContextMenu BuildMenu(MainViewModel model)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item(model.UndoMenuText, "Ctrl+Z", model.CanUndoEdit, () => model.UndoEditCommand.Execute(null)));
        menu.Items.Add(Item(model.RedoMenuText, "Ctrl+Y", model.CanRedoEdit, () => model.RedoEditCommand.Execute(null)));
        menu.Items.Add(new Separator());

        // The clipboard works on the canvas's objects while some are selected in Edit mode, else on the picked slides.
        var target = model.MenuTarget;
        var what = target == EditTarget.Objects
            ? (model.Canvas?.Selection.Count ?? 0) is var n && n > 1 ? $" {n} Objects" : " Object"
            : model.PickedSlideIds.Count is var s && s > 1 ? $" {s} Slides" : " Slide";
        var any = target == EditTarget.Objects ? model.HasCanvasSelection : model.PickedSlideIds.Count > 0;
        menu.Items.Add(Item("Cut" + what, "Ctrl+X", any, () => model.Cut(target)));
        menu.Items.Add(Item("Copy" + what, "Ctrl+C", any, () => model.Copy(target)));
        menu.Items.Add(Item("Paste", "Ctrl+V", model.HasPresentation && Infrastructure.AppClipboard.HasAnything(), () => _ = model.Paste(target)));
        menu.Items.Add(Item("Duplicate" + what, "Ctrl+D", any, () => model.Duplicate(target)));
        menu.Items.Add(Item("Select All Slides", "Ctrl+A", model.IsEditing && model.HasPresentation, () => model.SelectAllSlidesCommand.Execute(null)));
        foreach (var extra in MenuSections) extra(model, menu);
        return menu;
    }

    /// <summary>More of the Edit menu (arranging, text tools), added by their own files.</summary>
    internal static readonly List<Action<MainViewModel, ContextMenu>> MenuSections = [];

    private void OpenMenu(Button anchor)
    {
        if (model is null) return;
        var menu = BuildMenu(model);
        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>A menu entry with its key shown on the right.</summary>
    public static MenuItem Item(string header, string? keys, bool enabled, Action run)
    {
        var item = new MenuItem { Header = header, InputGestureText = keys ?? "", IsEnabled = enabled };
        item.Click += (_, _) => run();
        return item;
    }
}
