using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MxU.Slides.Core.Automation;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// "Choose media" (or a song, or a music file): a list you can search by typing, for
/// pickers that can hold hundreds of items. Double-click, or press Enter, to choose.
/// The Windows twin of the Mac's LibraryPickerSheet.
/// </summary>
public sealed class LibraryPickerWindow : Window
{
    private readonly IReadOnlyList<EditorChoice> all;
    private readonly ListBox list = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBox search = new() { Tag = "Search…" };
    private readonly TextBlock empty = EditorKit.Muted("Nothing matches.");

    private LibraryPickerWindow(Window? owner, string title, IReadOnlyList<EditorChoice> choices, string? currentId)
    {
        all = choices;
        EditorKit.Dress(this, owner, title, 440, 520);
        search.TextChanged += (_, _) => Fill(null);
        search.PreviewKeyDown += (_, e) =>
        {
            // Arrow keys in the search box move through the list, so the mouse isn't needed.
            if (e.Key == Key.Down && list.Items.Count > 0)
            {
                list.SelectedIndex = Math.Min(list.Items.Count - 1, list.SelectedIndex + 1);
                list.ScrollIntoView(list.SelectedItem);
                e.Handled = true;
            }
            else if (e.Key == Key.Up && list.SelectedIndex > 0)
            {
                list.SelectedIndex--;
                list.ScrollIntoView(list.SelectedItem);
                e.Handled = true;
            }
        };
        list.MouseDoubleClick += (_, _) => Choose();

        var choose = EditorKit.Button("Choose", Choose, accent: true);
        choose.IsDefault = true;
        choose.MinWidth = 90;
        var cancel = EditorKit.Button("Cancel", Close);
        cancel.IsCancel = true;
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        footer.Children.Add(cancel);
        footer.Children.Add(choose);

        var layout = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(search, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        DockPanel.SetDock(empty, Dock.Bottom);
        layout.Children.Add(search);
        layout.Children.Add(footer);
        layout.Children.Add(empty);
        layout.Children.Add(list);
        Content = layout;
        Fill(currentId);
        Loaded += (_, _) => search.Focus();
    }

    /// <summary>The picked choice's id, or null when cancelled.</summary>
    public string? Picked { get; private set; }

    /// <summary>Shows the picker and waits; returns the chosen id, or null when the person cancels.</summary>
    public static string? Pick(Window? owner, string title, IReadOnlyList<EditorChoice> choices, string? currentId)
    {
        var picker = new LibraryPickerWindow(owner, title, choices, currentId);
        picker.ShowDialog();
        return picker.Picked;
    }

    private void Fill(string? selectId)
    {
        var query = search.Text.Trim();
        list.Items.Clear();
        foreach (var choice in all.Where(c => c.Id.Length > 0 && (query.Length == 0 || c.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))))
        {
            var row = new StackPanel();
            row.Children.Add(new TextBlock { Text = choice.Name, TextTrimming = TextTrimming.CharacterEllipsis });
            if (choice.Detail is { Length: > 0 } detail) row.Children.Add(EditorKit.Faint(detail));
            var item = new ListBoxItem { Content = row, Tag = choice.Id };
            list.Items.Add(item);
            if (choice.Id == selectId) list.SelectedItem = item;
        }
        if (list.SelectedItem is null && list.Items.Count > 0 && query.Length > 0) list.SelectedIndex = 0;
        if (list.SelectedItem is not null) list.ScrollIntoView(list.SelectedItem);
        empty.Visibility = list.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Choose()
    {
        if (list.SelectedItem is not ListBoxItem { Tag: string id }) return;
        Picked = id;
        Close();
    }
}
