using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Notes;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views.Notes;

/// <summary>
/// Changes from the Notes (the Mac's NoteUpdatesReviewSheet): each change the note's edits
/// propose for its slides, an Update (new words, shown word by word), an Add (a new slide after
/// the one it follows) or a Remove. Apply puts it on the slide, keeping the old words in the
/// slide's history; Skip leaves the slide as it is until the passage changes again.
/// </summary>
public sealed class NoteChangesWindow : Window
{
    private readonly MainViewModel model;
    private readonly NoteDocument note;
    private readonly StackPanel list = new();

    private NoteChangesWindow(Window owner, MainViewModel model, NoteDocument note)
    {
        this.model = model;
        this.note = note;
        EditorKit.Dress(this, owner, $"Changes from the Notes — {note.Name}", 760, 620);
        var root = new DockPanel();
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        bottom.Children.Add(EditorKit.Button("Apply All", ApplyAll, "Apply every change", accent: true));
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        model.NotesChanged += Redraw;
        Closed += (_, _) => model.NotesChanged -= Redraw;
        Redraw();
    }

    public static NoteChangesWindow Open(Window owner, MainViewModel model, NoteDocument note)
    {
        var window = new NoteChangesWindow(owner, model, note);
        window.Show();
        return window;
    }

    private NoteDocument Current => model.Library.Get<NoteDocument>(note.Id) ?? note;

    private void Redraw()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(Redraw);
            return;
        }
        list.Children.Clear();
        var current = Current;
        var deck = model.DeckOf(current);
        var updates = current.PendingUpdates ?? [];
        if (updates.Count == 0 || deck is null)
        {
            list.Children.Add(EditorKit.Muted("The slides match the note: nothing waits.", 13));
            return;
        }
        foreach (var update in updates)
        {
            var card = new DockPanel();
            var buttons = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
            buttons.Children.Add(EditorKit.Button("Apply", () => model.ApplyNoteUpdate(Current, update), "Put this change on the slide", accent: true));
            var skip = EditorKit.Button("Skip", () => model.SkipNoteUpdate(Current, update), "Leave the slide as it is");
            skip.Margin = new Thickness(0, 6, 0, 0);
            buttons.Children.Add(skip);
            DockPanel.SetDock(buttons, Dock.Right);
            card.Children.Add(buttons);
            var scene = update.Proposed is { } proposed ? model.SceneOf(deck, proposed) : update.SlideId is { } id ? model.SlideScene(deck.Id, id) : null;
            var thumb = NoteViews.Thumbnail(model, scene, 176);
            thumb.Margin = new Thickness(0, 0, 10, 0);
            DockPanel.SetDock(thumb, Dock.Left);
            card.Children.Add(thumb);
            var details = new StackPanel();
            var (label, brush) = update.Kind switch
            {
                NotePendingUpdates.Words => ($"Update slide {deck.Slides.FindIndex(s => s.Id == update.SlideId) + 1}", "Warn"),
                NotePendingUpdates.New => (update.AfterSlideId is { } after ? $"Add after slide {deck.Slides.FindIndex(s => s.Id == after) + 1}" : "Add at the start", "Good"),
                _ => ($"Remove slide {deck.Slides.FindIndex(s => s.Id == update.SlideId) + 1}", "Live"),
            };
            details.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Foreground = EditorKit.Res<Brush>(brush) });
            details.Children.Add(Diff(update));
            card.Children.Add(details);
            list.Children.Add(EditorKit.Card(card, 8));
        }
    }

    /// <summary>The words that change, struck out and added, word by word.</summary>
    private static TextBlock Diff(NotePendingUpdate update)
    {
        var before = string.Join("\n", (update.Before ?? []).Select(b => b.Text));
        var after = update.After is { } boxes ? string.Join("\n", boxes.Select(b => b.Text))
            : update.Proposed is { } proposed ? string.Join("\n", NoteSlideMatch.Boxes(proposed).Select(b => b.Text)) : "";
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var (words, change) in NoteLiveUpdates.WordDiff(before, after))
        {
            var run = new Run(words + " ");
            if (change == NoteLiveUpdates.Change.Removed)
            {
                run.TextDecorations = TextDecorations.Strikethrough;
                run.Foreground = EditorKit.Res<Brush>("Live");
            }
            else if (change == NoteLiveUpdates.Change.Added)
            {
                run.Foreground = EditorKit.Res<Brush>("Good");
            }
            text.Inlines.Add(run);
        }
        return text;
    }

    private void ApplyAll()
    {
        // Each apply re-reads the note, whose waiting changes shrink as they're applied.
        for (var guard = 0; guard < 200 && Current.PendingUpdates is { Count: > 0 } waiting; guard++) model.ApplyNoteUpdate(Current, waiting[0]);
    }
}

/// <summary>
/// Present beside the note (the Mac's NoteFollowAlongView): the note's prose, and each passage
/// with the slides it made in a lane beside it, the deck's other slides where they fall. The live
/// slide lights up and the note scrolls to it; click a slide to put it on screen.
/// </summary>
public sealed class NoteFollowAlongWindow : Window
{
    private readonly MainViewModel model;
    private readonly NoteDocument note;
    private readonly StackPanel lines = new();
    private readonly ScrollViewer scroller;
    private readonly Dictionary<string, Border> lineViews = [];
    private Dictionary<string, string> bySlide = [];
    private string? lit;

    private NoteFollowAlongWindow(Window owner, MainViewModel model, NoteDocument note)
    {
        this.model = model;
        this.note = note;
        EditorKit.Dress(this, owner, $"{note.Name} — beside the slides", 860, 820);
        scroller = new ScrollViewer { Content = lines, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Content = scroller;
        model.PropertyChanged += OnMainChanged;
        model.NotesChanged += Rebuild;
        Closed += (_, _) =>
        {
            model.PropertyChanged -= OnMainChanged;
            model.NotesChanged -= Rebuild;
        };
        SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.PreviousSize.Width - e.NewSize.Width) > 30) Rebuild();
        };
        Rebuild();
    }

    public static NoteFollowAlongWindow Open(Window owner, MainViewModel model, NoteDocument note)
    {
        var window = new NoteFollowAlongWindow(owner, model, note);
        window.Show();
        return window;
    }

    /// <summary>The line lit for the live slide (the self-test reads it).</summary>
    public string? LitLine => lit;

    private void OnMainChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.LiveDetail) or nameof(MainViewModel.LiveTitle)) Light();
    }

    private void Rebuild()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(Rebuild);
            return;
        }
        var current = model.Library.Get<NoteDocument>(note.Id) ?? note;
        var body = current.Body ?? new NoteBody { Blocks = [], PlainText = "" };
        var items = model.FollowAlong(current);
        bySlide = NoteFollowAlong.LineIndex(items).BySlide;
        var lane = NoteFollowAlong.LaneWidth(ActualWidth > 0 ? ActualWidth : Width);
        lines.Children.Clear();
        lineViews.Clear();
        foreach (var line in NoteFollowAlong.Lines(items))
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            switch (line.Content)
            {
                case NoteFollowAlong.LineContent.ProseLine prose:
                    var text = new StackPanel { Margin = new Thickness(lane + 12, 0, 0, 0) };
                    foreach (var index in prose.Blocks) text.Children.Add(NoteViews.Text(body.Blocks[index], 15));
                    row.Children.Add(text);
                    break;
                case NoteFollowAlong.LineContent.RowLine passageRow:
                    var slides = new StackPanel { Width = lane, Margin = new Thickness(0, 0, 12, 0) };
                    foreach (var slot in passageRow.Row.Slides) slides.Children.Add(Tile(slot, lane));
                    DockPanel.SetDock(slides, Dock.Left);
                    row.Children.Add(slides);
                    var words = new TextBlock
                    {
                        Text = passageRow.Row.Words ?? "(blank)", TextWrapping = TextWrapping.Wrap, FontSize = 15,
                        Foreground = passageRow.Row.Words is null ? EditorKit.Res<Brush>("FaintText") : EditorKit.Res<Brush>("Text"),
                    };
                    row.Children.Add(new Border
                    {
                        BorderBrush = NoteViews.TypeBrush(passageRow.PassageKind), BorderThickness = new Thickness(3, 0, 0, 0),
                        Padding = new Thickness(8, 0, 0, 0), Child = words,
                    });
                    break;
                case NoteFollowAlong.LineContent.SlideLine slide:
                    var tile = Tile(slide.Slot, lane);
                    DockPanel.SetDock(tile, Dock.Left);
                    row.Children.Add(tile);
                    row.Children.Add(EditorKit.Faint(slide.InNote ? "Placed in the note" : "In the deck"));
                    break;
            }
            var view = new Border { Child = row, Padding = new Thickness(6), CornerRadius = new CornerRadius(4) };
            lineViews[line.Id] = view;
            lines.Children.Add(view);
        }
        lit = null;
        Light();
    }

    private FrameworkElement Tile(NoteFollowAlong.Slot slot, double width)
    {
        var tile = NoteViews.Thumbnail(model, model.SlideScene(slot.PresentationId, slot.SlideId), width);
        tile.Margin = new Thickness(0, 0, 0, 4);
        tile.Cursor = System.Windows.Input.Cursors.Hand;
        tile.ToolTip = "Put this slide on screen";
        tile.MouseLeftButtonUp += (_, _) => model.FireNoteSlide(slot.PresentationId, slot.SlideId);
        return tile;
    }

    /// <summary>Lights the live slide's line and scrolls it into view.</summary>
    private void Light()
    {
        var target = model.LiveSlideIds is { } live && bySlide.TryGetValue(live.SlideId, out var line) ? line : null;
        if (target == lit) return;
        if (lit is not null && lineViews.TryGetValue(lit, out var old)) old.Background = Brushes.Transparent;
        lit = target;
        if (target is not null && lineViews.TryGetValue(target, out var view))
        {
            view.Background = EditorKit.Res<Brush>("LiveWash");
            view.BringIntoView();
        }
    }
}
