using System.Windows;
using System.Windows.Controls;
using MxU.Slides.Core.Lyrics;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The arrangement chip in the slide grid's header: which arrangement plays here ("As written",
/// "Sunday", "Easter"…), picked live while the song is up. Its menu also makes a new arrangement,
/// duplicates, renames or deletes the one picked, makes it the song's default and opens the
/// arrangement editor. Picking it on a service row's song saves the pick on that row. In volunteer
/// mode it shows the name only. Port of the Mac's ArrangementPicker (ServiceContinuousView.swift).
/// </summary>
public sealed class ArrangementPicker : UserControl
{
    private readonly Button chip;
    private MainViewModel? model;

    public ArrangementPicker()
    {
        chip = OperatorKit.Chip("", "As written", "Which arrangement plays here: the song's sections in a named order", OpenMenu);
        chip.Margin = new Thickness(0);
        chip.MaxWidth = 190;
        Content = chip;
        DataContextChanged += (_, _) => Attach(OperatorKit.Model(this));
    }

    private void Attach(MainViewModel? next)
    {
        if (model is not null)
        {
            model.ArrangementsChanged -= Refresh;
            model.PropertyChanged -= OnModelChanged;
        }
        model = next;
        if (model is not null)
        {
            model.ArrangementsChanged += Refresh;
            model.PropertyChanged += OnModelChanged;
        }
        Refresh();
    }

    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.SelectedArrangement) or nameof(MainViewModel.CurrentPresentation)) Refresh();
    }

    /// <summary>The chip's label (the self-test reads it).</summary>
    internal string Text { get; private set; } = "";

    private void Refresh()
    {
        if (model?.CurrentPresentation is not { } p)
        {
            Visibility = Visibility.Collapsed;
            return;
        }
        Visibility = ArrangementEditing.ShowsArrangements(p) ? Visibility.Visible : Visibility.Collapsed;
        var current = model.CurrentArrangement;
        Text = current?.Name ?? "As written";
        if (current is not null && p.DefaultArrangementId == current.Id) Text += " (default)";
        OperatorKit.SetChipText(chip, Text);
    }

    private void OpenMenu(Button under)
    {
        if (BuildMenu() is { } menu) OperatorKit.Open(menu, under);
    }

    /// <summary>Opens the chip's menu as a click does (the self-test pictures it).</summary>
    internal ContextMenu? Open()
    {
        var menu = BuildMenu();
        if (menu is not null) OperatorKit.Open(menu, chip);
        return menu;
    }

    private ContextMenu? BuildMenu()
    {
        if (model?.CurrentPresentation is not { } p) return null;
        if (!model.CanChangeArrangement)
        {
            model.Notice = Core.Control.RunOnly.LockedNotice;
            return null;
        }
        var current = model.CurrentArrangement;
        List<object> items = [OperatorKit.Choice("As written", current is null, () => model.ChooseArrangement(null))];
        if (p.Arrangements is { Count: > 0 } arrangements)
        {
            items.Add(EditorKit.Separator());
            items.AddRange(arrangements.Select(a => (object)OperatorKit.Choice(
                p.DefaultArrangementId == a.Id ? $"{a.Name}  ·  default" : a.Name, current?.Id == a.Id, () => model.ChooseArrangement(a.Id))));
        }
        items.Add(EditorKit.Separator());
        items.Add(EditorKit.Command("New Arrangement", () => model.NewArrangement(), ArrangementEditing.SeedSectionIds(p).Count > 0));
        if (current is not null)
        {
            var id = current.Id;
            items.Add(EditorKit.Command($"Duplicate “{current.Name}”", () => model.DuplicateArrangement(id)));
            items.Add(EditorKit.Command($"Rename “{current.Name}”…", () =>
            {
                if (Window.GetWindow(this) is { } owner && InputDialog.Ask(owner, "Rename arrangement", "Name", current.Name) is { } name)
                    model.RenameArrangement(id, name);
            }));
            items.Add(EditorKit.Command($"Delete “{current.Name}”", () =>
            {
                if (Window.GetWindow(this) is { } owner && MessageBox.Show(owner,
                        $"Delete the arrangement “{current.Name}”? The song's slides stay as they are; service rows that played it play the slides as written.",
                        "Delete arrangement", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                    model.DeleteArrangement(id);
            }));
            items.Add(OperatorKit.Choice("Play by default", p.DefaultArrangementId == id,
                () => model.SetDefaultArrangement(p.DefaultArrangementId == id ? null : id)));
        }
        items.Add(EditorKit.Separator());
        items.Add(EditorKit.Command("Edit Arrangements…", () =>
        {
            if (Window.GetWindow(this) is { } owner) ArrangementEditorWindow.Open(owner, model);
        }));
        return OperatorKit.Menu(items);
    }
}
