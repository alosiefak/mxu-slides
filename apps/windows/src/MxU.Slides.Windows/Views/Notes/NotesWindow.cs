using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Notes;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views.Notes;

/// <summary>
/// The notes window (Notes in the library's header): the library's notes on the left (New,
/// Paste, Import Notes…, or drop a Word, RTF, ODT, PDF, PowerPoint or text file), the note on
/// the right with its slide passages marked in their type's colour. Select blocks to mark them
/// as a slide (or unmark them); Make Slides… plans and makes the deck; a note that made slides
/// shows the changes its edits propose, and Present opens it beside the slides. Built in code,
/// like the other tool windows; the work is in MainViewModel.Notes.cs.
/// </summary>
public sealed class NotesWindow : Window
{
    private static NotesWindow? open;

    private readonly MainViewModel model;
    private readonly ListBox list = new() { MinWidth = 220 };
    private readonly ListBox blocks = new() { SelectionMode = SelectionMode.Extended, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox editor = new()
    {
        AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap, FontSize = 14, Visibility = Visibility.Collapsed,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(10),
    };
    private readonly TextBlock title = new() { FontSize = 18, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock status = new() { FontSize = 11, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly WrapPanel markBar = new() { Margin = new Thickness(0, 8, 0, 8) };
    private readonly Button editButton;
    private readonly Button makeButton;
    private readonly Button changesButton;
    private readonly Button presentButton;
    private NoteDocument? note;
    private bool editing;

    private NotesWindow(Window owner, MainViewModel model)
    {
        this.model = model;
        EditorKit.Dress(this, owner, "Notes", 1100, 760);
        MinWidth = 760;
        MinHeight = 480;
        editButton = EditorKit.Button("Edit Text", ToggleEditing, "Edit the note as lines of text (# heading, - point, 1. numbered)");
        makeButton = EditorKit.Button("Make Slides…", MakeSlides, "Plan slides from the note's marked passages and make them", accent: true);
        changesButton = EditorKit.Button("Changes", ReviewChanges, "The changes this note's edits propose for the slides it made");
        presentButton = EditorKit.Button("Present", Present, "Show the note beside its slides while presenting");
        Content = Layout();
        AllowDrop = true;
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        Drop += async (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
            foreach (var file in files.Where(MainViewModel.IsNoteFile))
            {
                if (await model.ImportNoteAsync(file) is { } imported) Select(imported.Id);
            }
        };
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is ListBoxItem { Tag: NoteDocument picked }) Show(picked);
        };
        blocks.SelectionChanged += (_, _) => DrawMarkBar();
        model.NotesChanged += OnNotesChanged;
        Closed += (_, _) =>
        {
            CommitEdit();
            model.NotesChanged -= OnNotesChanged;
            if (ReferenceEquals(open, this)) open = null;
        };
        RefreshList();
        if (model.Notes.FirstOrDefault() is { } first) Select(first.Id);
        else Show(null);
    }

    public static NotesWindow Open(Window owner, MainViewModel model)
    {
        if (open is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return existing;
        }
        open = new NotesWindow(owner, model);
        open.Show();
        return open;
    }

    /// <summary>The note on show (the self-test reads it).</summary>
    public NoteDocument? Note => note;

    public void Select(string id)
    {
        RefreshList();
        list.SelectedItem = list.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is NoteDocument n && n.Id == id);
    }

    /// <summary>Selects the blocks of a passage (the self-test marks this way).</summary>
    public void SelectBlocks(IEnumerable<int> indexes)
    {
        blocks.SelectedItems.Clear();
        foreach (var index in indexes)
        {
            if (index >= 0 && index < blocks.Items.Count) blocks.SelectedItems.Add(blocks.Items[index]);
        }
    }

    private UIElement Layout()
    {
        var root = new DockPanel();

        var left = new DockPanel { Width = 250, Margin = new Thickness(0, 0, 12, 0) };
        var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        actions.Children.Add(EditorKit.Button("New", () => Select(model.NewNote().Id), "A new, empty note"));
        actions.Children.Add(Spaced(EditorKit.Button("Paste", Paste, "A new note from the text on the clipboard")));
        actions.Children.Add(Spaced(EditorKit.Button("Import Notes…", Import, "A note from a Word, RTF, ODT, PDF, PowerPoint or text file")));
        DockPanel.SetDock(actions, Dock.Top);
        left.Children.Add(actions);
        var hint = EditorKit.Faint("Or drop a file here.");
        hint.Margin = new Thickness(0, 0, 0, 6);
        DockPanel.SetDock(hint, Dock.Top);
        left.Children.Add(hint);
        list.ContextMenu = new ContextMenu();
        list.ContextMenu.Items.Add(EditorKit.Command("Rename…", Rename));
        list.ContextMenu.Items.Add(EditorKit.Command("Delete", Delete));
        left.Children.Add(list);
        DockPanel.SetDock(left, Dock.Left);
        root.Children.Add(left);

        var right = new DockPanel();
        var header = new DockPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var button in new[] { editButton, changesButton, presentButton, makeButton }) buttons.Children.Add(Spaced(button));
        DockPanel.SetDock(buttons, Dock.Right);
        header.Children.Add(buttons);
        header.Children.Add(title);
        DockPanel.SetDock(header, Dock.Top);
        right.Children.Add(header);
        DockPanel.SetDock(status, Dock.Top);
        right.Children.Add(status);
        DockPanel.SetDock(markBar, Dock.Top);
        right.Children.Add(markBar);
        var body = new Grid();
        body.Children.Add(blocks);
        body.Children.Add(editor);
        right.Children.Add(body);
        root.Children.Add(right);
        return root;
    }

    private static FrameworkElement Spaced(FrameworkElement element)
    {
        element.Margin = new Thickness(6, 0, 0, 0);
        return element;
    }

    private void OnNotesChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(OnNotesChanged);
            return;
        }
        var id = note?.Id;
        RefreshList(keep: true);
        if (id is not null && model.Library.Get<NoteDocument>(id) is { } fresh && !editing) Show(fresh);
    }

    private bool refreshing;

    private void RefreshList(bool keep = false)
    {
        refreshing = true;
        var selected = note?.Id;
        list.Items.Clear();
        foreach (var n in model.Notes)
        {
            var detail = model.DeckOf(n) is { } deck
                ? $"Slides in “{deck.Name}”" + (n.PendingUpdates is { Count: > 0 } pending ? $" · {pending.Count} change{(pending.Count == 1 ? "" : "s")}" : "")
                : $"{(n.Body?.Blocks.Count ?? 0)} blocks";
            var item = new ListBoxItem { Tag = n, Content = new StackPanel { Children = { new TextBlock { Text = n.Name, TextTrimming = TextTrimming.CharacterEllipsis }, EditorKit.Faint(detail) } } };
            list.Items.Add(item);
            if (keep && n.Id == selected) list.SelectedItem = item;
        }
        refreshing = false;
    }

    private void Show(NoteDocument? shown)
    {
        if (refreshing && shown is not null && note?.Id == shown.Id && ReferenceEquals(note, shown)) return;
        CommitEdit();
        note = shown;
        title.Text = shown?.Name ?? "No notes yet";
        var deck = shown is null ? null : model.DeckOf(shown);
        var pending = shown?.PendingUpdates?.Count ?? 0;
        status.Text = shown is null
            ? "Make a new note, paste one in, or import a Word, RTF, ODT, PDF, PowerPoint or text file."
            : deck is null
                ? "Select lines and mark them as slides, or let Make Slides pick them for you."
                : $"Made slides in “{deck.Name}”." + (pending > 0 ? $" Your edits propose {pending} change{(pending == 1 ? "" : "s")} to them." : " The slides match the note.");
        status.Foreground = EditorKit.Res<Brush>(pending > 0 ? "Warn" : "MutedText");
        foreach (var button in new[] { editButton, makeButton }) button.IsEnabled = shown is not null;
        changesButton.IsEnabled = pending > 0;
        changesButton.Content = pending > 0 ? $"Changes ({pending})" : "Changes";
        presentButton.IsEnabled = deck is not null;
        DrawBlocks();
        DrawMarkBar();
    }

    // MARK: The note's blocks

    private void DrawBlocks()
    {
        blocks.Items.Clear();
        if (note?.Body is not { } body) return;
        var groups = NoteSlideMarks.Groups(body);
        var groupOf = new Dictionary<int, NoteSlideMarks.Group>();
        foreach (var group in groups)
        {
            foreach (var index in group.BlockIndexes) groupOf.TryAdd(index, group);
        }
        var updated = (note.PendingUpdates ?? []).GroupBy(u => u.GroupId).ToDictionary(g => g.Key, g => g.ToList());
        var deck = model.DeckOf(note);
        var made = note.MadePassages?.ToDictionary(p => p.GroupId) ?? [];
        for (var index = 0; index < body.Blocks.Count; index++)
        {
            var block = body.Blocks[index];
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var bar = new Border { Width = 4, Margin = new Thickness(0, 0, 10, 0), CornerRadius = new CornerRadius(2) };
            DockPanel.SetDock(bar, Dock.Left);
            row.Children.Add(bar);
            var content = new StackPanel();
            if (groupOf.TryGetValue(index, out var group))
            {
                var suggested = group.State == NoteSlideMarks.Suggested;
                bar.Background = NoteViews.TypeBrush(group.Type);
                bar.Opacity = suggested ? 0.45 : 1;
                if (group.BlockIndexes[0] == index)
                {
                    var label = NoteSlideMarks.SelectionBarLabel(group.Type);
                    var badges = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
                    badges.Children.Add(new TextBlock
                    {
                        Text = (suggested ? "Suggested · " : "") + label.ToUpperInvariant(), FontSize = 10, FontWeight = FontWeights.SemiBold,
                        Foreground = NoteViews.TypeBrush(group.Type),
                    });
                    if (made.TryGetValue(group.Id, out var passage) && deck is not null)
                    {
                        var numbers = passage.Slides.Select(s => deck.Slides.FindIndex(d => d.Id == s.SlideId) + 1).Where(n => n > 0);
                        if (NotePanels.Title(numbers) is { } slides) badges.Children.Add(Badge(slides, "MutedText"));
                    }
                    if (updated.TryGetValue(group.Id, out var changes))
                    {
                        if (changes.Any(c => c.Kind == NotePendingUpdates.Words)) badges.Children.Add(Badge("Update", "Warn"));
                        if (changes.Any(c => c.Kind == NotePendingUpdates.New)) badges.Children.Add(Badge("Add", "Good"));
                        if (changes.Any(c => c.Kind == NotePendingUpdates.Remove)) badges.Children.Add(Badge("Remove", "Live"));
                    }
                    content.Children.Add(badges);
                }
            }
            if (block.Type == NoteBlockType.SlideRef && block.Slide is { PresentationId: { } presentationId, SlideId: { } slideId })
            {
                var card = new StackPanel { Orientation = Orientation.Horizontal };
                card.Children.Add(NoteViews.Thumbnail(model, model.SlideScene(presentationId, slideId), 160));
                card.Children.Add(new TextBlock { Text = block.Slide.SlideText ?? "", Margin = new Thickness(8, 0, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 360, VerticalAlignment = VerticalAlignment.Center });
                content.Children.Add(card);
            }
            else if (NoteViews.Picture(block, 320) is { } picture)
            {
                content.Children.Add(picture);
            }
            else
            {
                content.Children.Add(NoteViews.Text(block));
            }
            row.Children.Add(content);
            blocks.Items.Add(new ListBoxItem { Content = row, Tag = index, Padding = new Thickness(6, 3, 6, 3) });
        }
    }

    private static Border Badge(string text, string brush) => new()
    {
        Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(5, 0, 5, 0), CornerRadius = new CornerRadius(3),
        BorderBrush = EditorKit.Res<Brush>(brush), BorderThickness = new Thickness(1),
        Child = new TextBlock { Text = text, FontSize = 10, Foreground = EditorKit.Res<Brush>(brush) },
    };

    private List<int> SelectedIndexes() =>
        blocks.SelectedItems.OfType<ListBoxItem>().Select(i => (int)i.Tag).Order().ToList();

    /// <summary>The selection bar: the guessed type first, then the usual types, More, and Unmark or Accept / Decline.</summary>
    private void DrawMarkBar()
    {
        markBar.Children.Clear();
        if (note?.Body is not { } body || editing) return;
        var indexes = SelectedIndexes().Where(i => i < body.Blocks.Count && NoteSlideMarks.IsPassageType(body.Blocks[i]) || IsMedia(body, i)).ToList();
        if (indexes.Count == 0)
        {
            markBar.Children.Add(EditorKit.Faint("Select lines (Shift or Ctrl for several) to mark them as a slide."));
            return;
        }
        var groups = NoteSlideMarks.Groups(body).Where(g => g.BlockIndexes.Intersect(indexes).Any()).ToList();
        var words = string.Join("\n", indexes.Select(i => NoteReading.Text(body.Blocks[i])));
        var ownTypes = model.DesignMap.Options.OwnTypes.Select(t => t.Name).ToList();
        var guess = indexes.All(i => IsMedia(body, i))
            ? (NoteMedia.IsVideo(body.Blocks[indexes[0]]) ? "video" : "picture")
            : NoteSlideMarks.GuessedType(words, NoteSlideMarks.ShapeOf(body.Blocks[indexes[0]]), ownTypes);
        var (shown, more) = NoteSlideMarks.SelectionBarKinds(guess, ownTypes);
        markBar.Children.Add(new TextBlock { Text = "Mark as", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Foreground = EditorKit.Res<Brush>("MutedText") });
        foreach (var type in shown)
        {
            var button = EditorKit.Button(NoteSlideMarks.SelectionBarLabel(type), () => Mark(indexes, type));
            button.Margin = new Thickness(0, 0, 6, 0);
            button.BorderBrush = NoteViews.TypeBrush(type);
            markBar.Children.Add(button);
        }
        var moreButton = EditorKit.Button("More ▾", () => { });
        moreButton.Click += (_, _) =>
        {
            var menu = new ContextMenu();
            foreach (var type in more) menu.Items.Add(EditorKit.Command(NoteSlideMarks.SelectionBarLabel(type), () => Mark(indexes, type)));
            EditorKit.Open(menu, moreButton);
        };
        markBar.Children.Add(moreButton);
        if (groups.Count > 0)
        {
            if (groups.Any(g => g.State == NoteSlideMarks.Suggested))
            {
                markBar.Children.Add(Spaced(EditorKit.Button("Accept", () => Change(b => groups.Aggregate(b, (x, g) => Accepting(x, g.Id))), "Keep the suggestion as a slide")));
                markBar.Children.Add(Spaced(EditorKit.Button("Decline", () => Change(b => groups.Aggregate(b, (x, g) => Declining(x, g.Id))), "Not a slide: it isn't suggested again")));
            }
            markBar.Children.Add(Spaced(EditorKit.Button("Unmark", () => Change(b => groups.Aggregate(b, (x, g) => NoteSlideMarks.Unmarking(g.Id, x))), "Not a slide")));
        }
    }

    private static bool IsMedia(NoteBody body, int index) =>
        index < body.Blocks.Count && NoteMedia.Blocks(new NoteBody { Blocks = [body.Blocks[index]], PlainText = "" }).Count > 0;

    /// <summary>Marks the blocks as one passage of the type (null: a statement, left to the planner), replacing any marks they had.</summary>
    private void Mark(IReadOnlyList<int> indexes, string? type) => Change(body =>
    {
        foreach (var group in NoteSlideMarks.Groups(body).Where(g => g.BlockIndexes.Intersect(indexes).Any()))
            body = NoteSlideMarks.Unmarking(group.Id, body);
        var id = NoteSlideMarks.MintGroupId();
        var marked = body.Copy();
        // A "Slide" (no type) leaves the kind to the planner, as the Mac's does.
        foreach (var index in indexes) marked.Blocks[index] = NoteSlideMarks.Marking(marked.Blocks[index], id, type);
        return marked;
    });

    private static NoteBody Accepting(NoteBody body, string id) =>
        NoteSlideMarks.Mapping(body, r => r.SlideGroup == id && r.SlideState == NoteSlideMarks.Suggested ? WithState(r, null) : r);

    private static NoteBody Declining(NoteBody body, string id) =>
        NoteSlideMarks.Mapping(body, r => r.SlideGroup == id ? WithState(r, NoteSlideMarks.Declined) : r);

    private static NoteRun WithState(NoteRun run, string? state)
    {
        var copy = run.Copy();
        copy.SlideState = state;
        return copy;
    }

    private void Change(Func<NoteBody, NoteBody> change)
    {
        if (note?.Body is not { } body) return;
        var selected = SelectedIndexes();
        note.Body = change(body);
        model.SaveNote(note);
        Show(note);
        SelectBlocks(selected);
    }

    /// <summary>Marks blocks as one passage (the self-test's way in, as the selection bar's).</summary>
    public void MarkSelected(string? type) => Mark(SelectedIndexes(), type);

    // MARK: Editing

    private void ToggleEditing()
    {
        if (note?.Body is not { } body) return;
        if (editing)
        {
            CommitEdit();
            Show(note);
            return;
        }
        editing = true;
        editor.Text = NoteTextEditing.EditableText(body);
        editor.Visibility = Visibility.Visible;
        blocks.Visibility = Visibility.Collapsed;
        editButton.Content = "Done";
        DrawMarkBar();
        editor.Focus();
    }

    private void CommitEdit()
    {
        if (!editing) return;
        editing = false;
        editor.Visibility = Visibility.Collapsed;
        blocks.Visibility = Visibility.Visible;
        editButton.Content = "Edit Text";
        if (note?.Body is not { } body) return;
        var edited = NoteTextEditing.Edited(body, editor.Text);
        if (edited.Equals(body)) return;
        note.Body = edited;
        model.SaveNote(note);
    }

    /// <summary>Replaces the note's text as the editor would (the self-test edits this way).</summary>
    public void EditText(Func<string, string> edit)
    {
        if (note?.Body is not { } body) return;
        note.Body = NoteTextEditing.Edited(body, edit(NoteTextEditing.EditableText(body)));
        model.SaveNote(note);
        Show(note);
    }

    // MARK: Actions

    private void Paste()
    {
        var text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
        if (text.Trim().Length == 0)
        {
            status.Text = "The clipboard has no text to make a note from.";
            return;
        }
        Select(model.PasteNote(text).Id);
    }

    private async void Import()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import Notes",
            Filter = "Notes (Word, RTF, ODT, PDF, PowerPoint, text)|" + string.Join(";", NoteFileReader.FileExtensions.Select(e => "*." + e)),
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var file in dialog.FileNames)
        {
            if (await model.ImportNoteAsync(file) is { } imported) Select(imported.Id);
        }
    }

    private void Rename()
    {
        if (note is null || InputDialog.Ask(this, "Rename Note", "Name", note.Name) is not { } name || name.Trim().Length == 0) return;
        note.Name = name.Trim();
        model.SaveNote(note);
        Select(note.Id);
    }

    private void Delete()
    {
        if (note is null) return;
        if (MessageBox.Show(this, $"Delete the note “{note.Name}”? The slides it made stay.", "Delete Note", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        model.DeleteNote(note);
        note = null;
        if (model.Notes.FirstOrDefault() is { } first) Select(first.Id);
        else Show(null);
    }

    private void MakeSlides()
    {
        CommitEdit();
        if (note is null) return;
        MakeSlidesWindow.Open(this, model, note);
    }

    private void ReviewChanges()
    {
        if (note is not null) NoteChangesWindow.Open(this, model, note);
    }

    private void Present()
    {
        if (note is not null) NoteFollowAlongWindow.Open(this, model, note);
    }
}
