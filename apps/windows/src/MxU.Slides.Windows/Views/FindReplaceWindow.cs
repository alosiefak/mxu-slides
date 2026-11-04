using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MxU.Slides.Core.Editing;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// Find and replace (Ctrl+H): words in the open presentation, or in every song in the library.
/// The results list each match with the words round it; double-click one to open its slide.
/// Replace changes the selected match, Replace All every one (one undo step). The window belongs
/// to the main window, so its changes undo there (Ctrl+Z in the main window).
/// Windows only: the Mac has no find and replace.
/// </summary>
public sealed class FindReplaceWindow : Window
{
    private static FindReplaceWindow? open;

    private readonly MainViewModel model;
    private readonly TextBox find = new() { MinWidth = 260 };
    private readonly TextBox replace = new() { MinWidth = 260 };
    private readonly CheckBox matchCase = new() { Content = "Match case", Margin = new Thickness(0, 0, 12, 0) };
    private readonly CheckBox wholeWords = new() { Content = "Whole words", Margin = new Thickness(0, 0, 12, 0) };
    private readonly RadioButton here = new() { Content = "This presentation", IsChecked = true, Margin = new Thickness(0, 0, 12, 0) };
    private readonly RadioButton everySong = new() { Content = "Every song in the library" };
    private readonly ListBox results = new() { MinHeight = 200 };
    private readonly TextBlock status = new() { Margin = new Thickness(0, 6, 0, 0) };
    private IReadOnlyList<FindMatch> matches = [];

    private FindReplaceWindow(Window owner, MainViewModel model)
    {
        this.model = model;
        EditorKit.Dress(this, owner, "Find and Replace", 620, 520);
        status.Style = EditorKit.Res<Style>("Muted");

        var grid = new Grid { Margin = new Thickness(14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < 6; i++) grid.RowDefinitions.Add(new RowDefinition { Height = i == 4 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        Place(grid, EditorKit.Muted("Find", 12, 0), 0, 0);
        Place(grid, find, 0, 1);
        Place(grid, EditorKit.Muted("Replace with", 12, 0), 1, 0);
        Place(grid, replace, 1, 1);
        var options = new WrapPanel { Margin = new Thickness(0, 8, 0, 4) };
        foreach (var option in new UIElement[] { matchCase, wholeWords, here, everySong }) options.Children.Add(option);
        Place(grid, options, 2, 1);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 8) };
        buttons.Children.Add(EditorKit.Button("Find", Search, "List every match (Enter)", accent: true));
        buttons.Children.Add(EditorKit.Button("Replace", ReplaceSelected, "Replace the selected match"));
        buttons.Children.Add(EditorKit.Button("Replace All", ReplaceAll, "Replace every match (one undo step)"));
        foreach (FrameworkElement b in buttons.Children) b.Margin = new Thickness(0, 0, 6, 0);
        Place(grid, buttons, 3, 1);
        Place(grid, results, 4, 0, span: 2);
        Place(grid, status, 5, 0, span: 2);
        Content = grid;

        find.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Search();
            e.Handled = true;
        };
        results.MouseDoubleClick += (_, _) =>
        {
            if (results.SelectedIndex >= 0 && results.SelectedIndex < matches.Count) model.ShowMatch(matches[results.SelectedIndex]);
        };
        foreach (var box in new[] { matchCase, wholeWords }) box.Click += (_, _) => Search();
        here.Checked += (_, _) => Search();
        everySong.Checked += (_, _) => Search();
        Loaded += (_, _) => find.Focus();
        Closed += (_, _) => open = null;
    }

    private static void Place(Grid grid, UIElement element, int row, int column, int span = 1)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        Grid.SetColumnSpan(element, span);
        if (element is FrameworkElement fe && column == 0 && span == 1)
        {
            fe.VerticalAlignment = VerticalAlignment.Center;
            fe.Margin = new Thickness(0, 0, 10, 6);
        }
        else if (element is TextBox box)
        {
            box.Margin = new Thickness(0, 0, 0, 6);
        }
        grid.Children.Add(element);
    }

    /// <summary>Opens the window (or brings it forward), with the words to find filled in if given.</summary>
    public static FindReplaceWindow Open(Window owner, MainViewModel model, string? words = null)
    {
        open ??= new FindReplaceWindow(owner, model);
        if (!string.IsNullOrEmpty(words)) open.find.Text = words;
        open.Show();
        open.Activate();
        return open;
    }

    private FindQuery Query => new(find.Text, matchCase.IsChecked == true, wholeWords.IsChecked == true);

    private bool EverySong => everySong.IsChecked == true;

    /// <summary>Lists every match.</summary>
    public void Search()
    {
        matches = Query.IsEmpty ? [] : model.FindText(Query, EverySong);
        results.Items.Clear();
        foreach (var m in matches)
            results.Items.Add(new ListBoxItem { Content = $"{m.PresentationName}  ·  slide {m.SlideNumber}:  {m.Context}", ToolTip = "Double-click to open this slide" });
        status.Text = Query.IsEmpty ? "Type the words to find."
            : matches.Count == 0 ? "No matches."
            : $"{matches.Count} match{(matches.Count == 1 ? "" : "es")} in {matches.Select(m => m.PresentationId).Distinct().Count()} presentation(s).";
        if (matches.Count > 0) results.SelectedIndex = 0;
    }

    private void ReplaceSelected()
    {
        if (results.SelectedIndex < 0 || results.SelectedIndex >= matches.Count) return;
        var index = results.SelectedIndex;
        if (!model.ReplaceText(matches[index], Query, replace.Text)) status.Text = "That match has changed since: find again.";
        Search();
        if (results.Items.Count > 0) results.SelectedIndex = Math.Min(index, results.Items.Count - 1);
    }

    private void ReplaceAll()
    {
        if (Query.IsEmpty) return;
        var (replaced, presentations) = model.ReplaceAllText(Query, replace.Text, EverySong);
        Search();
        status.Text = replaced == 0 ? "Nothing to replace."
            : $"Replaced {replaced} in {presentations} presentation(s). Ctrl+Z in the main window undoes it.";
    }

    /// <summary>For the self-test: fills in the boxes as typing would.</summary>
    internal void Fill(string words, string replacement, bool library, bool whole = false)
    {
        find.Text = words;
        replace.Text = replacement;
        wholeWords.IsChecked = whole;
        (library ? everySong : here).IsChecked = true;
        Search();
    }

    internal void ReplaceAllNow() => ReplaceAll();

    internal int MatchCount => matches.Count;
}
