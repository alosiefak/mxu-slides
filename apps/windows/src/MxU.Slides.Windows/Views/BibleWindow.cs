using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Scripture;
using MxU.Slides.Windows.Rendering;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The Bible window (Ctrl+B, or Bible in the toolbar): one box to type a reference or words into,
/// the passage (or the search results) on the left, its slides and their preview on the right,
/// and what's live along the bottom. Built in code, like the other tool windows. It only draws
/// <see cref="ScriptureViewModel"/> and hands it keys:
/// <list type="bullet">
/// <item>Enter: on screen now. ↓ / ↑ in the box, ← → ↓ ↑ and Space elsewhere, PageDown / PageUp
/// (what clickers send): the next or previous slide, then verse.</item>
/// <item>Tab takes the book offered; Esc empties the box; typing anywhere starts a new reference.</item>
/// <item>F1–F6 clear, as in the main window.</item>
/// </list>
/// One is open at a time; closing it leaves what's live on screen.
/// </summary>
public sealed class BibleWindow : Window
{
    private static BibleWindow? open;

    private readonly MainViewModel main;
    private readonly ScriptureViewModel model;

    private readonly TextBox box = new()
    {
        FontSize = 20, Padding = new Thickness(10, 7, 10, 7), Tag = "John 3:16, Ps 23, 1 Cor 13:4-8, or words: love is patient",
        VerticalContentAlignment = VerticalAlignment.Center,
    };
    private readonly WrapPanel suggestions = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(2, 6, 0, 0) };
    private readonly StackPanel rows = new();
    private readonly ScrollViewer rowScroller = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly Grid compareHeader = new() { Margin = new Thickness(0, 0, 0, 4) };
    private readonly SceneView preview = new() { PlaysVideo = false, Height = 230, Letterbox = Brushes.Black };
    private readonly ListBox strip = new() { Height = 104, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock previewLabel = new() { FontSize = 11, Margin = new Thickness(0, 0, 0, 4) };
    private readonly Ellipse liveDot = new() { Width = 9, Height = 9, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock liveLine = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly List<ToggleButton> layoutButtons = [];
    private ComboBox? translationPicker;
    private ComboBox? comparePicker;
    private readonly Button goLive;
    private readonly Button addButton;
    private readonly Button saveButton;

    // What the list and strip were last drawn from, so a change of live marks doesn't rebuild them.
    private object? drawnRows;
    private object? drawnPages;
    private bool updating;

    private BibleWindow(Window owner, MainViewModel main)
    {
        this.main = main;
        model = main.Scripture;
        EditorKit.Dress(this, owner, "Bible", 1060, 700);
        MinWidth = 760;
        MinHeight = 520;
        preview.Media = main.Media;

        goLive = EditorKit.Button("Show  ⏎", () => GoLive(null), "Put the slide on screen now (Enter)", accent: true);
        goLive.MinWidth = 96;
        addButton = EditorKit.Button("Add to Service", () => Keep(toService: true), "Save the passage as a presentation and add it to the service");
        saveButton = EditorKit.Button("Save", () => Keep(toService: false), "Save the passage as a presentation in the library");

        Content = Layout();
        box.Text = model.Query;
        box.TextChanged += (_, _) =>
        {
            if (!updating) model.Query = box.Text;
        };
        model.Changed += Redraw;
        main.PropertyChanged += OnMainChanged;
        PreviewKeyDown += OnKey;
        PreviewTextInput += OnTypedElsewhere;
        Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        Closed += (_, _) =>
        {
            model.Changed -= Redraw;
            main.PropertyChanged -= OnMainChanged;
            if (ReferenceEquals(open, this)) open = null;
        };
        model.RefreshNow();
    }

    /// <summary>Opens the Bible window, or brings the open one forward with its box ready to type in.</summary>
    public static BibleWindow Open(Window owner, MainViewModel main)
    {
        if (open is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            existing.box.Focus();
            existing.box.SelectAll();
            return existing;
        }
        open = new BibleWindow(owner, main);
        open.Show();
        return open;
    }

    /// <summary>The box (the self-test types into it).</summary>
    public TextBox Box => box;

    // MARK: Layout

    private UIElement Layout()
    {
        var root = new DockPanel();

        // Top: the box, the translations, then books offered and the status line.
        var translations = EditorKit.Picker(
            [.. model.Translations.Select(t => new EditorChoice(t.Id, $"{t.Abbreviation} · {t.Name}"))],
            model.Translation.Id, id => model.Translation = model.Translations.First(t => t.Id == id), minWidth: 210);
        translations.ToolTip = "The translation passages are shown in";
        translationPicker = translations;
        var compare = EditorKit.Picker(
            [new EditorChoice("", "No second translation"), .. model.Translations.Select(t => new EditorChoice(t.Id, $"Beside it: {t.Abbreviation}"))],
            model.Compare?.Id ?? "", id => model.Compare = model.Translations.FirstOrDefault(t => t.Id == id), minWidth: 170);
        compare.ToolTip = "Show the same passage in a second translation, side by side";
        comparePicker = compare;
        compare.Margin = new Thickness(6, 0, 0, 0);
        var about = OperatorKit.IconButton("", "About these translations and their licences", () => BibleAboutWindow.Make(this).ShowDialog());
        var top = new DockPanel();
        var pickers = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        pickers.Children.Add(translations);
        pickers.Children.Add(compare);
        pickers.Children.Add(about);
        DockPanel.SetDock(pickers, Dock.Right);
        top.Children.Add(pickers);
        top.Children.Add(box);
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        header.Children.Add(top);
        header.Children.Add(suggestions);
        header.Children.Add(status);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        // Bottom: what's live, and Clear.
        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var clear = EditorKit.Button("Clear  F2", () => main.ClearSlideCommand.Execute(null), "Take the slide off the screen (F2)");
        clear.Margin = new Thickness(0);
        DockPanel.SetDock(clear, Dock.Right);
        footer.Children.Add(clear);
        footer.Children.Add(liveDot);
        footer.Children.Add(liveLine);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        // Middle: the passage or results on the left, the slides on the right.
        var middle = new Grid();
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 300 });
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(430) });

        var left = new DockPanel();
        DockPanel.SetDock(compareHeader, Dock.Top);
        left.Children.Add(compareHeader);
        rowScroller.Content = rows;
        left.Children.Add(rowScroller);
        var leftCard = new Border
        {
            Child = left, Background = EditorKit.Res<Brush>("Card"), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8, 6, 8),
            BorderBrush = EditorKit.Res<Brush>("Border"), BorderThickness = new Thickness(1),
        };
        middle.Children.Add(leftCard);

        var right = new StackPanel { Margin = new Thickness(0, 0, 4, 0) };
        right.Children.Add(previewLabel);
        right.Children.Add(new Border { Child = preview, CornerRadius = new CornerRadius(4), ClipToBounds = true, BorderBrush = EditorKit.Res<Brush>("Border"), BorderThickness = new Thickness(1) });
        var buttons = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var previous = EditorKit.Button("◀", () => model.Step(-1), "Previous slide or verse (↑, PageUp)");
        var next = EditorKit.Button("▶", () => model.Step(+1), "Next slide or verse (↓, PageDown)");
        previous.MinWidth = next.MinWidth = 44;
        DockPanel.SetDock(goLive, Dock.Right);
        goLive.Margin = new Thickness(0);
        buttons.Children.Add(goLive);
        buttons.Children.Add(previous);
        buttons.Children.Add(next);
        right.Children.Add(buttons);
        BuildStrip();
        right.Children.Add(strip);
        right.Children.Add(Options());
        // On a small laptop screen the options scroll rather than fall off the bottom.
        var rightScroller = new ScrollViewer { Content = right, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(rightScroller, 2);
        middle.Children.Add(rightScroller);
        root.Children.Add(middle);
        // Padding rather than a margin, so a picture of the window (the self-test's) shows all of it.
        return new Border { Child = root, Padding = new Thickness(16, 14, 16, 12) };
    }

    private void BuildStrip()
    {
        var panel = new FrameworkElementFactory(typeof(VirtualizingStackPanel));
        panel.SetValue(VirtualizingStackPanel.OrientationProperty, Orientation.Horizontal);
        strip.ItemsPanel = new ItemsPanelTemplate(panel);
        ScrollViewer.SetHorizontalScrollBarVisibility(strip, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(strip, ScrollBarVisibility.Disabled);
        strip.SelectionChanged += (_, _) =>
        {
            if (!updating && strip.SelectedIndex >= 0) model.SelectPage(strip.SelectedIndex);
        };
        strip.MouseDoubleClick += (_, _) =>
        {
            if (strip.SelectedIndex >= 0) GoLive(strip.SelectedIndex);
        };
    }

    private StackPanel Options()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        var layouts = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (layout, title) in new[] { (ScriptureLayout.FullScreen, "Full screen"), (ScriptureLayout.LowerThird, "Lower third"), (ScriptureLayout.SideThird, "Side third") })
        {
            var button = new ToggleButton { Content = title, Tag = layout, Margin = new Thickness(0, 0, 4, 0), Padding = new Thickness(10, 3, 10, 3) };
            button.Click += (_, _) => model.Layout = layout;
            layoutButtons.Add(button);
            layouts.Children.Add(button);
        }
        panel.Children.Add(EditorKit.Row("Design", layouts, 92));

        var themes = new ComboBox { MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Left, DisplayMemberPath = "Name" };
        void FillThemes()
        {
            updating = true;
            themes.ItemsSource = model.Themes;
            themes.SelectedItem = model.Themes.FirstOrDefault(t => t.Id == model.Theme?.Id);
            updating = false;
        }
        FillThemes();
        themes.DropDownOpened += (_, _) => FillThemes();
        themes.SelectionChanged += (_, _) =>
        {
            if (!updating && themes.SelectedItem is Core.Model.Theme theme) model.ChooseTheme(theme.Id);
        };
        panel.Children.Add(EditorKit.Row("Theme", themes, 92));

        var perSlide = EditorKit.Picker(
            [new EditorChoice("0", "As many as fit"), .. Enumerable.Range(1, 4).Select(n => new EditorChoice($"{n}", n == 1 ? "1 verse" : $"{n} verses"))],
            $"{model.VersesPerSlide}", id => model.VersesPerSlide = int.Parse(id, System.Globalization.CultureInfo.InvariantCulture), minWidth: 140);
        perSlide.ToolTip = "Long passages are split so nothing overflows; this caps the verses on each slide too";
        panel.Children.Add(EditorKit.Row("Per slide", perSlide, 92));

        var numbers = new CheckBox { Content = "Verse numbers", IsChecked = model.VerseNumbers, Margin = new Thickness(0, 0, 14, 0) };
        numbers.Click += (_, _) => model.VerseNumbers = numbers.IsChecked == true;
        var reference = new CheckBox { Content = "Reference line", IsChecked = model.ReferenceLine };
        reference.Click += (_, _) => model.ReferenceLine = reference.IsChecked == true;
        var checks = new StackPanel { Orientation = Orientation.Horizontal };
        checks.Children.Add(numbers);
        checks.Children.Add(reference);
        panel.Children.Add(EditorKit.Row("Show", checks, 92));

        var keep = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        keep.Children.Add(addButton);
        keep.Children.Add(saveButton);
        panel.Children.Add(keep);
        return panel;
    }

    // MARK: Drawing

    private void Redraw()
    {
        updating = true;
        try
        {
            if (box.Text != model.Query)
            {
                box.Text = model.Query;
                box.CaretIndex = box.Text.Length;
            }
            status.Text = model.Status;
            status.Foreground = model.StatusIsNote ? EditorKit.Res<Brush>("Warn") : EditorKit.Res<Brush>("MutedText");
            DrawSuggestions();
            Sync(translationPicker, model.Translation.Id);
            Sync(comparePicker, model.Compare?.Id ?? "");
            if (model.Mode == ScriptureMode.Search) DrawResults();
            else DrawVerses();
            DrawPages();
            DrawLive();
            foreach (var button in layoutButtons) button.IsChecked = (ScriptureLayout)button.Tag == model.Layout;
            var canShow = model.Mode == ScriptureMode.Search ? model.SelectedResult >= 0 : model.Preview is { Slides.Count: > 0 };
            goLive.IsEnabled = addButton.IsEnabled = saveButton.IsEnabled = canShow;
        }
        finally
        {
            updating = false;
        }
    }

    // A picker shows the choice made elsewhere too (a "John 3:16 WEB" typed, the self-test).
    private static void Sync(ComboBox? picker, string id)
    {
        if (picker?.ItemsSource is IEnumerable<EditorChoice> choices && choices.FirstOrDefault(c => c.Id == id) is { } choice && !Equals(picker.SelectedItem, choice))
            picker.SelectedItem = choice;
    }

    private void DrawSuggestions()
    {
        suggestions.Children.Clear();
        if (model.Alternative is { } other)
        {
            var show = OperatorKit.SmallButton($"Show it in the {other.Abbreviation}", $"Look it up in the {other.Name}", () =>
            {
                model.UseAlternative();
                box.Focus();
                box.CaretIndex = box.Text.Length;
            });
            show.Margin = new Thickness(0, 0, 4, 0);
            suggestions.Children.Add(show);
        }
        foreach (var book in model.Suggestions)
        {
            var chip = OperatorKit.SmallButton(book.Name, "Tab takes the first", () =>
            {
                model.AcceptSuggestion(book);
                box.Focus();
                box.CaretIndex = box.Text.Length;
            });
            chip.Margin = new Thickness(0, 0, 4, 0);
            suggestions.Children.Add(chip);
        }
        suggestions.Visibility = suggestions.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DrawVerses()
    {
        var comparing = model.Compare is { } other && model.Passage is { } passage && other.Id != passage.Translation.Id;
        compareHeader.Children.Clear();
        compareHeader.ColumnDefinitions.Clear();
        if (comparing)
        {
            compareHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            compareHeader.ColumnDefinitions.Add(new ColumnDefinition());
            compareHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            compareHeader.ColumnDefinitions.Add(new ColumnDefinition());
            var mine = OperatorKit.FieldLabel(model.Passage!.Translation.Abbreviation);
            var theirs = OperatorKit.FieldLabel(model.Compare!.Abbreviation);
            Grid.SetColumn(mine, 1);
            Grid.SetColumn(theirs, 3);
            compareHeader.Children.Add(mine);
            compareHeader.Children.Add(theirs);
        }
        compareHeader.Visibility = comparing ? Visibility.Visible : Visibility.Collapsed;

        if (!ReferenceEquals(drawnRows, model.Verses))
        {
            drawnRows = model.Verses;
            rows.Children.Clear();
            foreach (var row in model.Verses) rows.Children.Add(VerseRowView(row, comparing));
            if (model.Verses.Count == 0) rows.Children.Add(EditorKit.Muted(model.Mode == ScriptureMode.Empty ? Hint : "", 12));
            rowScroller.ScrollToTop();
        }
        // Marks: the live slide's verses red-washed, the previewed slide's blue.
        Border? live = null;
        foreach (var view in rows.Children.OfType<Border>())
        {
            if (view.Tag is not VerseRow row) continue;
            view.Background = row.IsLive ? EditorKit.Res<Brush>("LiveWash") : row.IsPreviewed ? EditorKit.Res<Brush>("AccentWash") : Brushes.Transparent;
            view.BorderBrush = row.IsLive ? EditorKit.Res<Brush>("Live") : Brushes.Transparent;
            if (row.IsLive) live ??= view;
        }
        live?.BringIntoView();
    }

    private const string Hint = "Type a reference and press Enter to put it on screen. ↓ and PageDown go to the next slide, and then the next verse.\n\n"
        + "Books can be short (Jn, Ps, 1 Cor, Rev), ranges can cross chapters (John 3:35-4:2), and several can be joined (Rom 8:28, 31-39).\n\n"
        + "Anything that isn't a reference is searched for: try “love is patient”.";

    private Border VerseRowView(VerseRow row, bool comparing)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        if (comparing)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
        }
        var number = new TextBlock
        {
            Text = row.Number, Foreground = EditorKit.Res<Brush>("Accent"), FontSize = 11, FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 2, 8, 0),
        };
        grid.Children.Add(number);
        var text = new TextBlock { Text = row.Text, TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 20 };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (comparing)
        {
            var twin = new TextBlock { Text = row.CompareText ?? "", TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 20, Foreground = EditorKit.Res<Brush>("MutedText") };
            Grid.SetColumn(twin, 3);
            grid.Children.Add(twin);
        }
        UIElement content = grid;
        if (row.Heading is { } heading)
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = heading, Style = EditorKit.Res<Style>("SectionLabel"), Margin = new Thickness(30, 4, 0, 4) });
            stack.Children.Add(grid);
            content = stack;
        }
        var view = new Border
        {
            Child = content, Tag = row, Padding = new Thickness(4, 4, 6, 4), CornerRadius = new CornerRadius(5), Cursor = Cursors.Hand,
            BorderThickness = new Thickness(2, 0, 0, 0), Margin = new Thickness(0, 0, 0, 1),
        };
        view.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) GoLive(row.Page);
            else model.SelectPage(row.Page);
        };
        return view;
    }

    private void DrawResults()
    {
        compareHeader.Visibility = Visibility.Collapsed;
        if (!ReferenceEquals(drawnRows, model.Results))
        {
            drawnRows = model.Results;
            rows.Children.Clear();
            for (var i = 0; i < model.Results.Count; i++) rows.Children.Add(ResultRowView(model.Results[i], i));
            rowScroller.ScrollToTop();
        }
        foreach (var view in rows.Children.OfType<Border>())
        {
            var selected = view.Tag is int index && index == model.SelectedResult;
            view.Background = selected ? EditorKit.Res<Brush>("AccentWash") : Brushes.Transparent;
            if (selected) view.BringIntoView();
        }
    }

    private Border ResultRowView(SearchHit hit, int index)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = PassageReference.Of(hit.Verse.Ref).Display, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = EditorKit.Res<Brush>("Accent"),
        });
        // The verse, with the words that matched in bold and the rest a little quieter.
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 19, Foreground = EditorKit.Res<Brush>("MutedText") };
        var at = 0;
        foreach (var match in hit.Matches.OrderBy(m => m.Start))
        {
            if (match.Start < at) continue;
            if (match.Start > at) text.Inlines.Add(new Run(hit.Verse.Text[at..match.Start]));
            text.Inlines.Add(new Run(hit.Verse.Text.Substring(match.Start, match.Length)) { FontWeight = FontWeights.SemiBold, Foreground = EditorKit.Res<Brush>("Text") });
            at = match.Start + match.Length;
        }
        if (at < hit.Verse.Text.Length) text.Inlines.Add(new Run(hit.Verse.Text[at..]));
        stack.Children.Add(text);
        var view = new Border { Child = stack, Tag = index, Padding = new Thickness(8, 5, 8, 6), CornerRadius = new CornerRadius(5), Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 1) };
        view.MouseLeftButtonDown += (_, e) =>
        {
            model.SelectResult(index);
            if (e.ClickCount == 2) GoLive(null);
        };
        return view;
    }

    private void DrawPages()
    {
        if (!ReferenceEquals(drawnPages, model.Pages))
        {
            drawnPages = model.Pages;
            strip.Items.Clear();
            foreach (var page in model.Pages) strip.Items.Add(PageView(page));
        }
        strip.SelectedIndex = model.Pages.Count == 0 ? -1 : model.SelectedPage;
        foreach (var item in strip.Items.OfType<ListBoxItem>())
        {
            if (item.Content is not Border { Tag: PageTile tile } frame) continue;
            frame.BorderBrush = tile.IsLive ? EditorKit.Res<Brush>("Live") : tile.IsSelected ? EditorKit.Res<Brush>("Accent") : Brushes.Transparent;
        }
        if (strip.SelectedItem is not null) strip.ScrollIntoView(strip.SelectedItem);
        preview.Present(model.PreviewScene, SceneTransition.Cut);
        previewLabel.Text = model.PreviewedResult is { } result ? $"Preview · {result}"
            : model.Pages.Count == 0 ? "Preview"
            : $"Preview · slide {model.SelectedPage + 1} of {model.Pages.Count}{(model.Pages[model.SelectedPage].IsLive ? " · on screen" : "")}";
        previewLabel.Foreground = model.Pages.Count > 0 && model.Pages[model.SelectedPage].IsLive ? EditorKit.Res<Brush>("Live") : EditorKit.Res<Brush>("MutedText");
        strip.Visibility = model.Pages.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private ListBoxItem PageView(PageTile page)
    {
        var stack = new StackPanel();
        stack.Children.Add(new SceneView { StaticScene = page.Scene, PlaysVideo = false, Width = 128, Height = 72, Media = main.Media });
        stack.Children.Add(new TextBlock { Text = page.Reference, FontSize = 9, Foreground = EditorKit.Res<Brush>("FaintText"), TextTrimming = TextTrimming.CharacterEllipsis, Width = 128, Margin = new Thickness(0, 2, 0, 0) });
        var frame = new Border { Child = stack, Tag = page, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(4), Padding = new Thickness(1) };
        return new ListBoxItem { Content = frame, Padding = new Thickness(2), Margin = new Thickness(0, 0, 4, 0), ToolTip = page.Reference };
    }

    private void DrawLive()
    {
        var live = model.IsLive;
        liveDot.Fill = live ? EditorKit.Res<Brush>("Live") : EditorKit.Res<Brush>("FaintText");
        liveLine.Text = live ? $"On screen: {model.LiveLine}" : main.Show.Live is not null ? $"On screen: {main.LiveTitle} (not from the Bible)" : "Nothing on screen";
        liveLine.Foreground = live ? EditorKit.Res<Brush>("Text") : EditorKit.Res<Brush>("MutedText");
    }

    private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Something else went live (a song slide clicked), or the slide was cleared.
        if (e.PropertyName is nameof(MainViewModel.LiveTitle) or nameof(MainViewModel.LiveDetail)) model.LiveChanged();
    }

    // MARK: Doing

    private async void GoLive(int? page)
    {
        // Ready for the next reference: typing replaces what's there.
        box.Focus();
        box.SelectAll();
        await model.GoLive(page);
    }

    private async void Keep(bool toService)
    {
        if (await (toService ? model.AddToService() : model.SaveToLibrary()) is { } name)
            status.Text = $"“{name}” is in the library{(toService ? " and the service" : "")}.";
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        // The window's own focus (it is what keys go to whenever the window is active).
        var focused = FocusManager.GetFocusedElement(this);
        var inBox = ReferenceEquals(focused, box);
        var typing = focused is TextBox or ComboBox;
        switch (key)
        {
            // The clears work here as in the main window: the operator's hands needn't move.
            case Key.F1: main.ClearAllCommand.Execute(null); break;
            case Key.F2: main.ClearSlideCommand.Execute(null); break;
            case Key.F3: main.ClearMediaCommand.Execute(null); break;
            case Key.F4: main.ClearOverlaysCommand.Execute(null); break;
            case Key.F5: main.ClearAudioCommand.Execute(null); break;
            case Key.F6: main.ClearAlertsCommand.Execute(null); break;
            case Key.B when ctrl: Close(); break;
            case Key.Enter when focused is not ComboBox { IsDropDownOpen: true }: GoLive(null); break;
            case Key.PageDown: model.Step(+1); break;
            case Key.PageUp: model.Step(-1); break;
            case Key.Tab when inBox && model.Suggestions.Count > 0:
                model.AcceptSuggestion(model.Suggestions[0]);
                box.CaretIndex = box.Text.Length;
                break;
            case Key.Escape when inBox && box.Text.Length > 0: box.Clear(); break;
            case Key.Down when inBox: Move(+1); break;
            case Key.Up when inBox: Move(-1); break;
            case Key.Right or Key.Down or Key.Space when !typing: model.Step(+1); break;
            case Key.Left or Key.Up when !typing: model.Step(-1); break;
            default: return;
        }
        e.Handled = true;
    }

    // ↓ / ↑ in the box: through the search results while searching, else the next or previous slide.
    private void Move(int by)
    {
        if (model.Mode == ScriptureMode.Search) model.MoveResult(by);
        else model.Step(by);
    }

    // Typing a letter or number while the list has focus starts a new reference in the box.
    private void OnTypedElsewhere(object sender, TextCompositionEventArgs e)
    {
        if (FocusManager.GetFocusedElement(this) is TextBox or ComboBox || e.Text.Length == 0 || !char.IsLetterOrDigit(e.Text[0])) return;
        box.Focus();
        box.Text = e.Text;
        box.CaretIndex = box.Text.Length;
        e.Handled = true;
    }
}

/// <summary>The translations the app carries: their names, licence notes and where they came from.</summary>
internal static class BibleAboutWindow
{
    /// <summary>The window, ready to show (as a dialog from the Bible window; the self-test shows it plainly).</summary>
    public static Window Make(Window owner)
    {
        var window = new Window();
        EditorKit.Dress(window, owner, "About these Bibles", 560, 520);
        var stack = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
        stack.Children.Add(EditorKit.Muted("MxU Slides carries three public-domain English translations, so scripture works with no internet and nothing to buy. "
            + "Each one's text comes from eBible.org, unchanged except as its note says.", 12, 10));
        foreach (var translation in BibleCatalog.BuiltIn.Translations)
        {
            var card = new StackPanel();
            card.Children.Add(new TextBlock { Text = $"{translation.Name} ({translation.Abbreviation})", FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
            card.Children.Add(EditorKit.Muted(translation.Notes, 12, 4));
            card.Children.Add(EditorKit.Faint($"{translation.Verses:N0} verses"));
            card.Children.Add(new TextBox
            {
                Text = translation.Source, IsReadOnly = true, FontSize = 11, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                Foreground = EditorKit.Res<Brush>("FaintText"), Padding = new Thickness(0), ToolTip = $"SHA-256 of the download: {translation.SourceSha256}",
            });
            stack.Children.Add(EditorKit.Card(card, 8));
        }
        var done = EditorKit.Button("Done", window.Close, accent: true);
        done.IsDefault = done.IsCancel = true;
        done.HorizontalAlignment = HorizontalAlignment.Right;
        done.Margin = new Thickness(0, 6, 0, 0);
        stack.Children.Add(done);
        window.Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        return window;
    }
}
