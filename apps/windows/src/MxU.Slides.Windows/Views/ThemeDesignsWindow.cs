using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.Rendering;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The library's themes with a preview of each, opening onto a theme's designs (its "theme slides")
/// as pictures with sample words. Double-click a design to edit it; drag one onto a slide in the
/// slide grid to give that slide this design (<see cref="ThemeDesignDrop"/>). It stays open beside the
/// main window while you drag. Port of the Mac's LibraryThemeDesigns.swift (with ThemeLook's previews
/// from ThemeExplorerView.swift and its <c>ThemeDesignDrag</c> payload).
/// </summary>
public sealed class ThemeDesignsWindow : Window
{
    private const double TileWidth = 192;
    private static ThemeDesignsWindow? open;

    private readonly MainViewModel model;
    private readonly ListBox themes = new() { Width = 230, BorderThickness = new Thickness(0) };
    private readonly WrapPanel designs = new();
    private readonly TextBlock heading = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private Border? selected;

    private ThemeDesignsWindow(Window owner, MainViewModel model, string? themeId)
    {
        this.model = model;
        EditorKit.Dress(this, owner, "Theme Designs", 980, 640);
        ShowInTaskbar = false;
        themes.SelectionChanged += (_, _) => ShowDesigns();
        model.Library.Changed += OnLibraryChanged;
        Closed += (_, _) =>
        {
            model.Library.Changed -= OnLibraryChanged;
            if (ReferenceEquals(open, this)) open = null;
        };

        var edit = EditorKit.Button("Edit Theme…", () =>
        {
            if (CurrentTheme is { } theme) ThemeEditorWindow.Open(Owner ?? this, model, theme.Id, SelectedDesign?.Name);
        }, "Open the theme editor on this theme (and the design picked)");
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(edit, Dock.Right);
        header.Children.Add(edit);
        header.Children.Add(heading);
        var right = new DockPanel { Margin = new Thickness(14, 0, 0, 0) };
        DockPanel.SetDock(header, Dock.Top);
        right.Children.Add(header);
        var hint = EditorKit.Muted("Drag a design onto a slide to give it this design. Double-click one to edit it.", 12, 0);
        DockPanel.SetDock(hint, Dock.Bottom);
        right.Children.Add(hint);
        right.Children.Add(new ScrollViewer { Content = designs, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var body = new DockPanel { Margin = new Thickness(14) };
        var themesBox = new Border { Background = EditorKit.Res<Brush>("Card"), CornerRadius = new CornerRadius(6), Child = themes };
        DockPanel.SetDock(themesBox, Dock.Left);
        body.Children.Add(themesBox);
        body.Children.Add(right);
        Content = body;
        Fill(themeId);
    }

    /// <summary>Opens the window (or brings it forward) on a theme, or the first one.</summary>
    public static ThemeDesignsWindow Open(Window owner, MainViewModel model, string? themeId)
    {
        if (open is { } existing)
        {
            existing.Fill(themeId ?? existing.CurrentTheme?.Id);
            existing.Activate();
            return existing;
        }
        open = new ThemeDesignsWindow(owner, model, themeId);
        open.Show();
        return open;
    }

    /// <summary>The theme shown (the self-test reads it).</summary>
    internal Theme? CurrentTheme => themes.SelectedItem is ListBoxItem { Tag: Theme theme } ? theme : null;

    /// <summary>The design picked with a click, if any.</summary>
    internal Slide? SelectedDesign { get; private set; }

    /// <summary>The design cards shown, with what each one drags (the self-test reads these).</summary>
    internal IReadOnlyList<(Slide Design, string Payload)> Cards { get; private set; } = [];

    private void OnLibraryChanged(Type type, string id)
    {
        if (type != typeof(Theme)) return;
        Dispatcher.BeginInvoke(() => Fill(CurrentTheme?.Id));
    }

    private void Fill(string? themeId)
    {
        var all = model.Library.All<Theme>().OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        themes.Items.Clear();
        foreach (var theme in all) themes.Items.Add(ThemeRow(theme));
        var index = all.FindIndex(t => t.Id == themeId);
        themes.SelectedIndex = index >= 0 ? index : all.Count > 0 ? 0 : -1;
        if (all.Count == 0)
        {
            heading.Text = "No themes yet. Make one in Edit Theme….";
            designs.Children.Clear();
        }
    }

    private static ListBoxItem ThemeRow(Theme theme)
    {
        var thumb = new Border { Width = 80, Height = 45, Background = Brushes.Black, CornerRadius = new CornerRadius(3), ClipToBounds = true, Margin = new Thickness(0, 0, 8, 0) };
        if (theme.Slides?.FirstOrDefault() is { } first)
            thumb.Child = new SceneView { StaticScene = ThemePickerWindow.DesignScene(theme, first), PlaysVideo = false };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = theme.Name, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 128 });
        var count = theme.Slides?.Count ?? 0;
        text.Children.Add(EditorKit.Faint($"{count} design{(count == 1 ? "" : "s")}"));
        return new ListBoxItem { Tag = theme, Content = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2), Children = { thumb, text } } };
    }

    /// <summary>The theme's designs: those in no group first, then each group under its name (the Mac's ThemeExplorer order).</summary>
    private void ShowDesigns()
    {
        designs.Children.Clear();
        selected = null;
        SelectedDesign = null;
        var cards = new List<(Slide, string)>();
        if (CurrentTheme is not { } theme)
        {
            Cards = cards;
            return;
        }
        heading.Text = $"Themes › {theme.Name}";
        var all = theme.Slides ?? [];
        foreach (var design in all.Where(d => string.IsNullOrEmpty(d.Folder))) designs.Children.Add(Card(theme, design, cards));
        foreach (var group in all.Where(d => !string.IsNullOrEmpty(d.Folder)).GroupBy(d => d.Folder!))
        {
            designs.Children.Add(new TextBlock { Text = group.Key.ToUpperInvariant(), Style = EditorKit.Res<Style>("SectionLabel"), Width = 2000, Margin = new Thickness(0, 6, 0, 4) });
            foreach (var design in group) designs.Children.Add(Card(theme, design, cards));
        }
        if (all.Count == 0) designs.Children.Add(EditorKit.Muted("This theme has no designs yet.", 12, 0));
        Cards = cards;
    }

    private Border Card(Theme theme, Slide design, List<(Slide, string)> cards)
    {
        var payload = ThemeDesignDrag.Payload(theme.Id, design.Id);
        cards.Add((design, payload));
        var view = new SceneView { StaticScene = ThemePickerWindow.DesignScene(theme, design), PlaysVideo = false, Width = TileWidth, Height = TileWidth * 9 / 16 };
        var frame = new Border { Background = Brushes.Black, CornerRadius = new CornerRadius(4), ClipToBounds = true, Child = view, BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent };
        var tile = new StackPanel { Children = { frame, new TextBlock { Text = design.Name, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = TileWidth, Margin = new Thickness(0, 3, 0, 0) } } };
        var card = new Border { Child = tile, Margin = new Thickness(0, 0, 10, 10), Cursor = Cursors.Hand, ToolTip = "Drag onto a slide to give it this design", Background = Brushes.Transparent, Tag = frame };

        Point? pressedAt = null;
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                ThemeEditorWindow.Open(Owner ?? this, model, theme.Id, design.Name);
                return;
            }
            pressedAt = e.GetPosition(card);
            Select(card, design);
        };
        card.MouseLeftButtonUp += (_, _) => pressedAt = null;
        card.MouseMove += (_, e) =>
        {
            if (pressedAt is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
            var now = e.GetPosition(card);
            if (Math.Abs(now.X - start.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(now.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            pressedAt = null;
            DragDrop.DoDragDrop(card, new DataObject(DataFormats.UnicodeText, payload), DragDropEffects.Copy);
        };
        var menu = new ContextMenu();
        var editItem = new MenuItem { Header = "Edit…" };
        editItem.Click += (_, _) => ThemeEditorWindow.Open(Owner ?? this, model, theme.Id, design.Name);
        menu.Items.Add(editItem);
        card.ContextMenu = menu;
        return card;
    }

    private void Select(Border card, Slide design)
    {
        if (selected?.Tag is Border old) old.BorderBrush = Brushes.Transparent;
        selected = card;
        SelectedDesign = design;
        if (card.Tag is Border frame) frame.BorderBrush = EditorKit.Res<Brush>("Accent");
    }
}
