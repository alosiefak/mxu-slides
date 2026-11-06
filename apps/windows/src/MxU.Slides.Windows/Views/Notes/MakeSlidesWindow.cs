using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Notes;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views.Notes;

/// <summary>
/// Make Slides (the Mac's MakeSlidesSheet): how the passages are picked (Pick for me, Bold lines
/// only, By structure, or only the ones marked by hand), each passage as a row with its slide
/// type, design and a preview of its first slide, the theme, the options the church shares
/// (blanks, reveal, one verse a slide…), and where the slides go: a new presentation or one
/// already in the library. Make plans and builds through MainViewModel.MakeSlides.
/// </summary>
public sealed class MakeSlidesWindow : Window
{
    private readonly MainViewModel model;
    private readonly NoteDocument note;
    private readonly NoteBody basis;
    private NoteBody body;
    private NoteSlideMarks.AutoMark autoMark;
    private static NoteSlideMarks.AutoMark rememberedAutoMark = NoteSlideMarks.AutoMark.PickForMe;
    private string themeId;
    private string? destination;
    private readonly HashSet<string> excluded = [];
    private readonly Dictionary<string, SlideRowOverride> overrides = [];
    private readonly StackPanel rows = new();
    private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock footer = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button make;

    private MakeSlidesWindow(Window owner, MainViewModel model, NoteDocument note)
    {
        this.model = model;
        this.note = note;
        EditorKit.Dress(this, owner, $"Make Slides — {note.Name}", 980, 760);
        MinWidth = 760;
        MinHeight = 520;
        // Earlier suggestions are suggested again in the mode picked here; marks made by hand stay.
        basis = NoteSlideMarks.Mapping(note.Body ?? new NoteBody { Blocks = [], PlainText = "" }, run =>
        {
            if (run.SlideState != NoteSlideMarks.Suggested) return run;
            var clear = run.Copy();
            (clear.SlideGroup, clear.SlideType, clear.SlideId, clear.SlideState) = (null, null, null, null);
            return clear;
        });
        // As the Mac (makeSlides.autoMark): the choice made last time on this computer, Pick for me at first.
        autoMark = rememberedAutoMark;
        themeId = model.DeckOf(note)?.ThemeId ?? model.NotesThemeId;
        body = Marked();
        make = EditorKit.Button("Make Slides", Make, "Make the slides", accent: true);
        Content = Layout();
        Redraw();
    }

    public static MakeSlidesWindow Open(Window owner, MainViewModel model, NoteDocument note)
    {
        var window = new MakeSlidesWindow(owner, model, note);
        window.Show();
        return window;
    }

    private NoteBody Marked() => autoMark.Marking(basis, model.DesignMap.Options.OwnTypes.Select(t => t.Name).ToList());

    private UIElement Layout()
    {
        var root = new DockPanel();
        var top = new StackPanel();
        top.Children.Add(EditorKit.Row("Pick", EditorKit.Picker(
            Enum.GetValues<NoteSlideMarks.AutoMark>().Select(m => new EditorChoice(m.ToString(), m == NoteSlideMarks.AutoMark.Nothing ? "Only what I marked" : m.Title())).ToList(),
            autoMark.ToString(), id =>
            {
                autoMark = rememberedAutoMark = Enum.Parse<NoteSlideMarks.AutoMark>(id);
                body = Marked();
                excluded.Clear();
                overrides.Clear();
                Redraw();
            })));
        var themes = model.Library.All<Theme>().Select(t => new EditorChoice(t.Id, t.Name)).Prepend(new EditorChoice("", "No theme")).ToList();
        top.Children.Add(EditorKit.Row("Theme", EditorKit.Picker(themes, themeId, id =>
        {
            themeId = id;
            overrides.Clear();
            Redraw();
        })));
        var decks = model.Library.All<Presentation>().Where(p => p.PresentationKind == PresentationKind.Deck).OrderBy(p => p.Name)
            .Select(p => new EditorChoice(p.Id, $"Add to “{p.Name}”")).Prepend(new EditorChoice("", "A new presentation")).ToList();
        destination = model.DeckOf(note)?.Id;
        top.Children.Add(EditorKit.Row("Slides go to", EditorKit.Picker(decks, destination ?? "", id =>
        {
            destination = id.Length == 0 ? null : id;
            Redraw();
        })));
        top.Children.Add(Options());
        top.Children.Add(summary);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var cancel = EditorKit.Button("Cancel", Close);
        cancel.Margin = new Thickness(0, 0, 8, 0);
        buttons.Children.Add(cancel);
        buttons.Children.Add(make);
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);
        bottom.Children.Add(footer);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        root.Children.Add(new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 0) });
        return root;
    }

    /// <summary>The options the church shares in its design map (Settings › Slides on the Mac).</summary>
    private Expander Options()
    {
        var map = model.DesignMap;
        var panel = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        void Save(Func<SlideOptions, SlideOptions> change)
        {
            var current = model.DesignMap;
            current.Options = change(current.Options);
            model.SaveDesignMap(current);
            Redraw();
        }
        CheckBox Check(string label, bool value, Func<SlideOptions, bool, SlideOptions> change)
        {
            var box = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 0, 16, 6) };
            box.Click += (_, _) => Save(o => change(o, box.IsChecked == true));
            return box;
        }
        panel.Children.Add(Check("Points come in one at a time", map.Options.RevealPoints, (o, v) => o with { RevealPoints = v }));
        panel.Children.Add(Check("One verse a slide", map.Options.ScriptureSplit == SlideOptions.ScriptureSplits.OneVerse,
            (o, v) => o with { ScriptureSplit = v ? SlideOptions.ScriptureSplits.OneVerse : SlideOptions.ScriptureSplits.FitToBox }));
        panel.Children.Add(Check("Verse numbers", map.Options.VerseNumbers, (o, v) => o with { VerseNumbers = v }));
        panel.Children.Add(Check("Keep underlines", map.Options.KeepUnderlines, (o, v) => o with { KeepUnderlines = v }));
        panel.Children.Add(Check("(Asides) go to the slide notes", map.Options.ParenthesesToNotes, (o, v) => o with { ParenthesesToNotes = v }));
        panel.Children.Add(Check("Pictures together", map.Options.PicturesTogether, (o, v) => o with { PicturesTogether = v }));
        var blanks = EditorKit.Picker([new("None", "No blank slides"), new("AfterPassage", "A blank after each passage"), new("AfterSlide", "A blank after each slide")],
            map.Blanks.ToString(), id =>
            {
                var current = model.DesignMap;
                current.Blanks = Enum.Parse<BlankPlacement>(id);
                model.SaveDesignMap(current);
                Redraw();
            }, minWidth: 190);
        blanks.Margin = new Thickness(0, 0, 12, 6);
        panel.Children.Add(blanks);
        var scale = EditorKit.Picker(Enum.GetValues<MediaScaleMode>().Select(m => new EditorChoice(m.ToString(), m == MediaScaleMode.ScaleAndBlur ? "Pictures: Scale and Blur" : $"Pictures: {m}")).ToList(),
            map.Options.PictureScale.ToString(), id => Save(o => o with { PictureScale = Enum.Parse<MediaScaleMode>(id) }), minWidth: 170);
        scale.Margin = new Thickness(0, 0, 12, 6);
        panel.Children.Add(scale);
        panel.Children.Add(EditorKit.Picker(Enum.GetValues<SlideOptions.VideoEnds>().Select(e => new EditorChoice(e.ToString(), $"Video ends: {SlideOptions.Title(e)}")).ToList(),
            map.Options.VideoEnd.ToString(), id => Save(o => o with { VideoEnd = Enum.Parse<SlideOptions.VideoEnds>(id) }), minWidth: 190));
        return new Expander { Header = "Options (shared with every computer)", Content = panel, Margin = new Thickness(0, 6, 0, 0) };
    }

    private sealed record Preview(NoteSlideBuilder.Built Built, List<SlideRow> Rows);

    /// <summary>The slides as they'd be made now, with the passages left out removed.</summary>
    private Preview Plan()
    {
        var accepted = NoteSlideMarks.AcceptingSuggestions(body);
        var groups = NoteSlideBuilder.Groups(accepted).Where(g => !excluded.Contains(g.Id)).ToList();
        var map = model.DesignMap;
        var planner = model.NotePlanner(map.Options, MainViewModel.NotePictureAspects(accepted));
        var deckTheme = destination is { } id && model.Library.Get<Presentation>(id) is { } deck ? deck.ThemeId : themeId;
        var built = NoteLiveUpdates.NewSlides(groups, deckTheme, map, model.ThemeById, planner, overrides: overrides);
        var rowsPlanned = groups.Select(g => planner.Plan([g], model.ThemeById(deckTheme), overrides).Rows[0]).ToList();
        return new Preview(built, rowsPlanned);
    }

    private void Redraw()
    {
        rows.Children.Clear();
        var preview = Plan();
        var groups = NoteSlideMarks.Groups(body);
        var theme = model.ThemeById(destination is { } id && model.Library.Get<Presentation>(id) is { } deck ? deck.ThemeId : themeId);
        var deckForScenes = new Presentation { Id = "preview", Name = "", PresentationKind = PresentationKind.Deck, ThemeId = theme?.Id ?? "", Slides = preview.Built.Slides };
        foreach (var group in groups)
        {
            var row = preview.Rows.FirstOrDefault(r => r.GroupId == group.Id);
            var slides = preview.Built.Slides.Where(s => preview.Built.GroupBySlideId.GetValueOrDefault(s.Id) == group.Id).ToList();
            var card = new DockPanel { Margin = new Thickness(0, 0, 0, 8), Opacity = excluded.Contains(group.Id) ? 0.45 : 1 };
            var include = new CheckBox { IsChecked = !excluded.Contains(group.Id), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 8, 0), ToolTip = "Make this passage a slide" };
            include.Click += (_, _) =>
            {
                if (include.IsChecked == true) excluded.Remove(group.Id);
                else excluded.Add(group.Id);
                Redraw();
            };
            DockPanel.SetDock(include, Dock.Left);
            card.Children.Add(include);
            var thumb = slides.Count > 0
                ? NoteViews.Thumbnail(model, model.SceneOf(deckForScenes, slides[0]), 176)
                : new Border { Width = 176, Height = 99, Background = Brushes.Black };
            thumb.Margin = new Thickness(0, 0, 10, 0);
            DockPanel.SetDock(thumb, Dock.Left);
            card.Children.Add(thumb);
            var details = new StackPanel();
            var line = new WrapPanel();
            var kinds = new List<EditorChoice> { new("", $"Auto ({(row is null ? "—" : Label(row.Kind))})") };
            kinds.AddRange(new[] { SlideKind.Title, SlideKind.Points, SlideKind.Scripture, SlideKind.Quote, SlideKind.Lyrics, SlideKind.Body, SlideKind.Picture, SlideKind.Video, SlideKind.Media }
                .Select(k => new EditorChoice(k.Raw(), Label(k))));
            var kindPicker = EditorKit.Picker(kinds, overrides.GetValueOrDefault(group.Id)?.Kind?.Raw() ?? "", id =>
            {
                var current = overrides.GetValueOrDefault(group.Id) ?? new SlideRowOverride();
                overrides[group.Id] = current with { Kind = SlideKinds.Parse(id), ThemeSlideName = null };
                Redraw();
            }, minWidth: 150);
            kindPicker.Margin = new Thickness(0, 0, 8, 4);
            line.Children.Add(kindPicker);
            var designs = new List<EditorChoice> { new("", $"Design: {(row?.ThemeSlideName ?? "the theme's first")}") };
            designs.AddRange((theme?.Slides ?? []).Select(s => new EditorChoice(s.Name, s.Name)).DistinctBy(c => c.Id));
            var designPicker = EditorKit.Picker(designs, overrides.GetValueOrDefault(group.Id)?.ThemeSlideName ?? "", id =>
            {
                var current = overrides.GetValueOrDefault(group.Id) ?? new SlideRowOverride();
                overrides[group.Id] = current with { ThemeSlideName = id.Length == 0 ? null : id };
                Redraw();
            }, minWidth: 210);
            designPicker.Margin = new Thickness(0, 0, 8, 4);
            line.Children.Add(designPicker);
            line.Children.Add(new TextBlock
            {
                Text = slides.Count == 1 ? "1 slide" : $"{slides.Count} slides", VerticalAlignment = VerticalAlignment.Center,
                Foreground = EditorKit.Res<Brush>("MutedText"),
            });
            if (group.State == NoteSlideMarks.Suggested)
                line.Children.Add(new TextBlock { Text = "  · suggested", VerticalAlignment = VerticalAlignment.Center, Foreground = EditorKit.Res<Brush>("FaintText") });
            details.Children.Add(line);
            var words = group.Text.Length > 0 ? group.Text : string.Join(", ", group.Media.Select(Path.GetFileName));
            details.Children.Add(new TextBlock { Text = words, TextWrapping = TextWrapping.Wrap, MaxHeight = 80, TextTrimming = TextTrimming.CharacterEllipsis });
            if (row?.Warning is { } warning)
                details.Children.Add(new TextBlock { Text = warning, Foreground = EditorKit.Res<Brush>("Warn"), FontSize = 11 });
            card.Children.Add(details);
            rows.Children.Add(new Border
            {
                Child = card, Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 6), CornerRadius = new CornerRadius(6),
                BorderBrush = NoteViews.TypeBrush(row?.TypeKey), BorderThickness = new Thickness(3, 0, 0, 0), Background = EditorKit.Res<Brush>("Card"),
            });
        }
        var count = preview.Built.Slides.Count;
        summary.Text = groups.Count == 0
            ? "No passages are marked. Pick how to choose them above, or close this and mark lines in the note."
            : $"{groups.Count - groups.Count(g => excluded.Contains(g.Id))} passages make {count} slide{(count == 1 ? "" : "s")}.";
        var media = NoteMedia.Importable(NoteSlideMarks.AcceptingSuggestions(body), note.MediaImports ?? [], i => model.Library.Get<MediaItem>(i) is not null)
            .Where(b => b.Url is { } url && NoteMedia.Resolve(url) is { IsFile: true }).ToList();
        footer.Text = NoteMedia.FooterLine(media) ?? "";
        make.IsEnabled = count > 0;
    }

    private static string Label(SlideKind kind) => kind == SlideKind.Body ? "Statement" : kind.ToString();

    /// <summary>The note as made: passages left out are declined (a suggestion) or unmarked (marked by hand).</summary>
    private NoteBody Chosen()
    {
        var chosen = body;
        foreach (var group in NoteSlideMarks.Groups(body).Where(g => excluded.Contains(g.Id)))
        {
            chosen = group.State == NoteSlideMarks.Suggested
                ? NoteSlideMarks.Mapping(chosen, r =>
                {
                    if (r.SlideGroup != group.Id) return r;
                    var declined = r.Copy();
                    declined.SlideState = NoteSlideMarks.Declined;
                    return declined;
                })
                : NoteSlideMarks.Unmarking(group.Id, chosen);
        }
        return chosen;
    }

    private void Make()
    {
        var deck = model.MakeSlides(new MainViewModel.MakeSlidesRequest(
            note, Chosen(), themeId, overrides, new Dictionary<string, NoteSlideBuilder.SwitchOverride>(), destination, autoMark));
        model.Notice = $"Made {deck.Slides.Count} slides in “{deck.Name}” from the note.";
        Close();
    }

    /// <summary>Makes the slides as the button would (the self-test's way in).</summary>
    public void MakeNow() => Make();
}
