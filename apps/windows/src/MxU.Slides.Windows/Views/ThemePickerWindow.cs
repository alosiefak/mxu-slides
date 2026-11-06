using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Rendering;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// Picking a theme to apply, with a look at its designs: the themes on the left, the chosen theme's
/// designs as pictures on the right (each with sample words). Use the whole theme (each slide keeps
/// its kind of design: a verse stays a verse) or pick one design for every slide. For slides there's
/// Blank (no theme) and Automatic (follow the deck's theme). Port of the Mac's ApplyThemeSheet and
/// SlideThemeSheet (Sources/ApplyThemeMenu.swift) over a simpler ThemeExplorerView.
/// </summary>
public sealed class ThemePickerWindow : Window
{
    private const double TileWidth = 176;

    private readonly MainViewModel model;
    private readonly Action<Theme, string?> pick;
    private readonly ListBox themes = new() { Width = 220, BorderThickness = new Thickness(0) };
    private readonly WrapPanel designs = new();
    private readonly TextBlock heading = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button useWhole = new() { Margin = new Thickness(12, 0, 0, 0), Padding = new Thickness(12, 4, 12, 4) };

    private ThemePickerWindow(Window owner, MainViewModel model, string title, string scope, string? currentThemeId, Action<Theme, string?> pick,
        IReadOnlyList<(string Label, string Tooltip, Action Act)> extras)
    {
        this.model = model;
        this.pick = pick;
        EditorKit.Dress(this, owner, title, 900, 620);
        useWhole.Style = EditorKit.Res<Style>("AccentButton");
        useWhole.ToolTip = "Each slide keeps its kind of design (a verse stays a verse) in this theme. Or pick one design below for every slide.";
        useWhole.Click += (_, _) =>
        {
            if (themes.SelectedItem is ListBoxItem { Tag: Theme theme }) Choose(theme, null);
        };

        var all = model.Library.All<Theme>().OrderBy(t => t.Id == currentThemeId ? 0 : 1).ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        foreach (var theme in all) themes.Items.Add(ThemeRow(theme, theme.Id == currentThemeId));
        themes.SelectionChanged += (_, _) => ShowDesigns();
        themes.SelectedIndex = all.Count > 0 ? 0 : -1;

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(useWhole, Dock.Right);
        header.Children.Add(useWhole);
        header.Children.Add(heading);
        var right = new DockPanel { Margin = new Thickness(14, 0, 0, 0) };
        DockPanel.SetDock(header, Dock.Top);
        right.Children.Add(header);
        right.Children.Add(new ScrollViewer { Content = designs, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var body = new DockPanel { Margin = new Thickness(14) };
        var themesBox = new Border { Background = EditorKit.Res<Brush>("Card"), CornerRadius = new CornerRadius(6), Child = themes };
        DockPanel.SetDock(themesBox, Dock.Left);
        body.Children.Add(themesBox);
        body.Children.Add(right);

        var footer = new DockPanel { Margin = new Thickness(14, 0, 14, 14) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, tooltip, act) in extras)
            buttons.Children.Add(EditorKit.Button(label, () =>
            {
                act();
                Close();
            }, tooltip));
        var cancel = EditorKit.Button("Cancel", Close);
        cancel.IsCancel = true;
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        footer.Children.Add(EditorKit.Muted(scope, 12, 0));
        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(body);
        Content = root;
        if (all.Count == 0) heading.Text = "No themes yet. Make one in Edit Theme….";
    }

    /// <summary>The theme and design picked (the self-test reads them), or null if it was cancelled.</summary>
    internal (Theme Theme, string? Design)? Picked { get; private set; }

    private void Choose(Theme theme, string? design)
    {
        Picked = (theme, design);
        pick(theme, design);
        Close();
    }

    private static ListBoxItem ThemeRow(Theme theme, bool current)
    {
        var thumb = new Border { Width = 64, Height = 36, Background = Brushes.Black, CornerRadius = new CornerRadius(3), ClipToBounds = true, Margin = new Thickness(0, 0, 8, 0) };
        if (theme.Slides?.FirstOrDefault() is { } first)
            thumb.Child = new SceneView { StaticScene = DesignScene(theme, first), PlaysVideo = false };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = theme.Name, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 130 });
        text.Children.Add(EditorKit.Faint(current ? "This deck" : $"{theme.Slides?.Count ?? 0} designs"));
        return new ListBoxItem { Tag = theme, Content = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2), Children = { thumb, text } } };
    }

    /// <summary>A design as a slide using it shows it, with its sample words.</summary>
    public static RenderScene DesignScene(Theme theme, Slide design) =>
        SlideSceneBuilder.Scene(DesignPreview.SampleSlide(design, theme.Id), theme, null, pager: TextLayout.Pager);

    private void ShowDesigns()
    {
        designs.Children.Clear();
        if (themes.SelectedItem is not ListBoxItem { Tag: Theme theme }) return;
        heading.Text = theme.Name;
        useWhole.Content = $"Use “{theme.Name}”";
        string? folder = null;
        foreach (var design in theme.Slides ?? [])
        {
            if (design.Folder != folder && design.Folder is { } heading2)
            {
                designs.Children.Add(new TextBlock { Text = heading2.ToUpperInvariant(), Style = EditorKit.Res<Style>("SectionLabel"), Width = 2000, Margin = new Thickness(0, 6, 0, 4) });
            }
            folder = design.Folder;
            designs.Children.Add(DesignTile(theme, design));
        }
    }

    private Border DesignTile(Theme theme, Slide design)
    {
        var view = new SceneView { StaticScene = DesignScene(theme, design), PlaysVideo = false, Width = TileWidth, Height = TileWidth * 9 / 16 };
        var frame = new Border { Background = Brushes.Black, CornerRadius = new CornerRadius(4), ClipToBounds = true, Child = view, BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent };
        var tile = new StackPanel { Children = { frame, new TextBlock { Text = design.Name, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = TileWidth, Margin = new Thickness(0, 3, 0, 0) } } };
        var border = new Border { Child = tile, Margin = new Thickness(0, 0, 10, 10), Cursor = Cursors.Hand, ToolTip = $"Every slide on “{design.Name}”", Background = Brushes.Transparent };
        border.MouseEnter += (_, _) => frame.BorderBrush = EditorKit.Res<Brush>("Accent");
        border.MouseLeave += (_, _) => frame.BorderBrush = Brushes.Transparent;
        border.MouseLeftButtonUp += (_, _) => Choose(theme, design.Name);
        return border;
    }

    // MARK: Openers

    /// <summary>The deck's theme: every slide (asking first about slides with a theme of their own).</summary>
    public static ThemePickerWindow ForPresentation(Window owner, MainViewModel model, Presentation presentation)
    {
        var scope = $"Applies to every slide in “{presentation.Name}”" + (presentation.HasSlideThemes() ? ", slides with a theme of their own too." : ".");
        return new ThemePickerWindow(owner, model, $"Theme: {presentation.Name}", scope, presentation.ThemeId,
            (theme, design) => { model.ApplyDeckTheme(presentation, theme.Id, design); }, []);
    }

    /// <summary>Apply Theme to some slides (the slide menu's Apply Theme…), with Blank and Automatic.</summary>
    public static ThemePickerWindow? ForSlides(Window owner, MainViewModel model, Presentation presentation, IReadOnlyCollection<string> slideIds)
    {
        var ids = presentation.Slides.Where(s => slideIds.Contains(s.Id)).Select(s => s.Id).ToList();
        if (ids.Count == 0) return null;
        var current = presentation.Slides.First(s => s.Id == ids[0]);
        var scope = ids.Count == 1 ? "Applies to this slide." : $"Applies to these {ids.Count} slides.";
        return new ThemePickerWindow(owner, model, ids.Count == 1 ? "Apply Theme to this Slide" : $"Apply Theme to {ids.Count} Slides", scope,
            presentation.ThemeIdFor(current), (theme, design) => { model.ApplySlidesTheme(presentation, ids, theme.Id, design); },
            [
                ("Blank", "No theme styles these slides: only their own objects show", () => model.SetSlidesBlank(presentation, ids)),
                ("Automatic", "Follow the deck's theme, each with its own kind of design", () => model.SetSlidesAutomatic(presentation, ids)),
            ]);
    }
}
