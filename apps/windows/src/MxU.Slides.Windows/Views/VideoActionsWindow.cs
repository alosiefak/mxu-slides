using System.Windows;
using System.Windows.Controls;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Media;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// "Video Actions": a video's marks, the named points on its timeline. Playing across a mark
/// runs its actions; a stage countdown can count to it; a Video Action Name text box shows the
/// coming one. Here you add a mark (at the playhead while the video is live, or at a typed
/// time), move it by typing a new time, rename it, give it actions and remove it. Every change
/// is saved at once, as the Mac's media editor does, so a video that is playing uses it straight away.
/// <para>
/// The Mac edits marks on a timeline under the video's preview (VideoActionsTimeline.swift and
/// MediaEditorView.swift). Windows has no media editor yet, so this is that card's list of
/// marks as a window of its own; the rules (time order, staying inside the file, default
/// names) are the same Core ones (<see cref="VideoMarks"/>).
/// </para>
/// </summary>
public sealed class VideoActionsWindow : Window
{
    private readonly MainViewModel model;
    private readonly string mediaId;
    private readonly StackPanel rows = new();
    private readonly TextBox newTime;
    private readonly Button playheadButton;

    internal VideoActionsWindow(Window? owner, MainViewModel model, MediaItem item)
    {
        this.model = model;
        mediaId = item.Id;
        EditorKit.Dress(this, owner, $"Video Actions — {item.Name}", 560, 560);

        newTime = new TextBox { Width = 84, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "Where the new mark goes: seconds, m:ss or h:mm:ss (\"1:02.5\")" };
        playheadButton = EditorKit.Button("Playhead", UsePlayhead, "Use where the video is right now (while it's live)");
        var add = EditorKit.Button("+ Add video action", () => { }, "A mark only, or a mark that runs an action");
        add.Click += (_, _) => EditorKit.Open(AddMenu(), add);
        var adding = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 12) };
        adding.Children.Add(new TextBlock { Text = "New mark at", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Foreground = EditorKit.Res<System.Windows.Media.Brush>("MutedText"), FontSize = 12 });
        adding.Children.Add(newTime);
        playheadButton.Margin = new Thickness(6, 0, 6, 0);
        adding.Children.Add(playheadButton);
        adding.Children.Add(add);

        var done = EditorKit.Button("Done", Close, accent: true);
        done.IsCancel = true;
        done.MinWidth = 90;
        done.Margin = new Thickness(0);
        var footer = new DockPanel { Margin = new Thickness(16, 8, 16, 14), LastChildFill = false };
        DockPanel.SetDock(done, Dock.Right);
        footer.Children.Add(done);

        var body = new StackPanel { Margin = new Thickness(16, 14, 16, 0) };
        body.Children.Add(new TextBlock { Text = Title, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6), TextTrimming = TextTrimming.CharacterEllipsis });
        body.Children.Add(EditorKit.Muted(
            "A mark is a named point in the video. Playing across it runs its actions, a stage countdown can count to it, "
            + "and a Video Action Name text box shows the next one. Scrubbing past a mark doesn't fire it.", 12, 8));
        body.Children.Add(adding);
        body.Children.Add(rows);

        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;

        UsePlayhead();
        Rebuild();
        // The video may have gone live (or been edited on another screen) while another window had focus.
        Activated += (_, _) => { playheadButton.IsEnabled = model.VideoPlayhead(mediaId) is not null; Rebuild(); };
    }

    /// <summary>Opens the window for a video and waits until it's closed. False when the item is gone or isn't a video.</summary>
    public static bool Edit(Window owner, MainViewModel model, MediaItem item)
    {
        if (model.Library.Get<MediaItem>(item.Id) is not { MediaKind: MediaKind.Video } stored) return false;
        new VideoActionsWindow(owner, model, stored).ShowDialog();
        return true;
    }

    // The item as the library has it now: never a copy kept from when the window opened.
    private MediaItem? Item => model.Library.Get<MediaItem>(mediaId);

    /// <summary>The video's marks as saved, in time order (the self-test reads them).</summary>
    internal IReadOnlyList<VideoMark> Marks => Item?.Marks ?? [];

    private double FileLength => Item?.DurationSeconds ?? 0;

    private void UsePlayhead()
    {
        var at = model.VideoPlayhead(mediaId);
        playheadButton.IsEnabled = at is not null;
        newTime.Text = VideoMarks.Clock(at ?? VideoMarks.ParseClock(newTime.Text) ?? 0);
    }

    // MARK: The edits (each saves at once)

    /// <summary>Adds a mark at the time in the box: a mark only, or one that runs <paramref name="action"/>. Returns its id.</summary>
    internal string? AddMark(SlideAction? action = null)
    {
        if (VideoMarks.ParseClock(newTime.Text) is not { } at)
        {
            MessageBox.Show(this, "Type the mark's time as seconds, m:ss or h:mm:ss.", Title);
            return null;
        }
        var id = Guid.NewGuid().ToString().ToUpperInvariant();
        // A mark made from an action is named after what it does ("Band on Stage"); a bare one is "Mark N".
        var name = action is null ? VideoMarks.DefaultName(Marks) : VideoMarks.DefaultName(SlideActions.Label(action, model.ActionLabelNames()));
        if (!Write(VideoMarks.Adding(Marks, id, name, at, action is null ? [] : [action], FileLength))) return null;
        // Fire Media, a timer… still need their target chosen.
        if (action is not null && ActionEditing.NeedsChoice(action)) EditActions(id);
        return id;
    }

    internal void SetNewTime(string typed) => newTime.Text = typed;

    internal bool Move(string id, string typed) =>
        VideoMarks.ParseClock(typed) is { } at && Write(VideoMarks.Moving(Marks, id, at, FileLength));

    internal bool Rename(string id, string name) => Write(VideoMarks.Renaming(Marks, id, name));

    internal bool Remove(string id) => Write(VideoMarks.Removing(Marks, id));

    internal bool SetActions(string id, IReadOnlyList<SlideAction> actions) => Write(VideoMarks.WithActions(Marks, id, actions));

    private void EditActions(string id)
    {
        if (Marks.FirstOrDefault(m => m.Id == id) is not { } mark) return;
        new SlideActionsWindow(this, model, $"Video Action — {mark.Name}",
            "These run, in order, when the video plays across this mark. A delay waits that many seconds first.",
            mark.Actions, actions => SetActions(id, actions)).ShowDialog();
        Rebuild();
    }

    private bool Write(List<VideoMark> marks, bool redraw = true)
    {
        var saved = model.SaveVideoMarks(mediaId, marks);
        if (!saved) MessageBox.Show(this, "The video's actions couldn't be saved. The video may no longer be in the library.", Title);
        if (redraw) Rebuild();
        return saved;
    }

    // MARK: Drawing

    private ContextMenu AddMenu()
    {
        var menu = new ContextMenu();
        var only = EditorKit.Command("Mark Only", () => AddMark());
        only.ToolTip = "A named point with no actions: a timer can count to it";
        menu.Items.Add(only);
        menu.Items.Add(EditorKit.Separator());
        foreach (var entry in EditorKit.MenuEntries(ActionEditing.AddMenu(model.ActionChoices()), action => AddMark(action)))
            menu.Items.Add(entry);
        return menu;
    }

    private void Rebuild()
    {
        rows.Children.Clear();
        var item = Item;
        var marks = item?.Marks ?? [];
        if (marks.Count == 0)
        {
            rows.Children.Add(EditorKit.Muted("No video actions yet. Set a time (or play the video and press Playhead), then press + Add video action.", 12, 8));
            return;
        }
        // The pass the audience sees is the trim; a mark outside it never plays, so say so.
        var inPoint = item?.InPoint ?? 0;
        var outPoint = item?.OutPoint ?? (FileLength > 0 ? FileLength : double.PositiveInfinity);
        var names = model.ActionLabelNames();
        foreach (var mark in marks)
        {
            var id = mark.Id;
            // These two boxes save without redrawing the list (the box already shows the new value):
            // a redraw while the pointer is on its way to another row's button would swallow that click.
            // Only a move that changes the order redraws, once this box has finished committing.
            var time = EditorKit.CommitBox(VideoMarks.Clock(mark.At), typed =>
            {
                var current = Marks.FirstOrDefault(m => m.Id == id)?.At ?? mark.At;
                if (VideoMarks.ParseClock(typed) is not { } at) return VideoMarks.Clock(current);
                var moved = VideoMarks.Moving(Marks, id, at, FileLength);
                var reordered = !moved.Select(m => m.Id).SequenceEqual(Marks.Select(m => m.Id));
                if (!Write(moved, redraw: false)) return VideoMarks.Clock(current);
                if (reordered) Dispatcher.BeginInvoke(Rebuild);
                return VideoMarks.Clock(moved.First(m => m.Id == id).At);
            }, width: 84);
            time.ToolTip = "The mark's time in the video. Type a new one to move it.";
            var name = EditorKit.CommitBox(mark.Name, typed =>
            {
                var current = Marks.FirstOrDefault(m => m.Id == id)?.Name ?? mark.Name;
                return typed.Trim().Length > 0 && Write(VideoMarks.Renaming(Marks, id, typed), redraw: false) ? typed.Trim() : current;
            });
            name.ToolTip = "The mark's name (a countdown to a named mark, and the Video Action Name text, use it)";
            name.Margin = new Thickness(8, 0, 8, 0);

            var remove = EditorKit.Ghost("✕", "Remove this mark", () => Remove(id));
            var edit = EditorKit.Button("Actions…", () => EditActions(id), "What runs when the video plays across this mark");
            edit.Margin = new Thickness(0);
            var line = new DockPanel();
            DockPanel.SetDock(time, Dock.Left);
            DockPanel.SetDock(remove, Dock.Right);
            DockPanel.SetDock(edit, Dock.Right);
            line.Children.Add(time);
            line.Children.Add(remove);
            line.Children.Add(edit);
            line.Children.Add(name);

            var summary = mark.Actions.Count == 0 ? "No actions" : string.Join(" · ", mark.Actions.Select(a => SlideActions.Label(a, names)));
            if (mark.At < inPoint || mark.At > outPoint) summary += "  —  outside the trim, so it won't play";
            var card = new StackPanel();
            card.Children.Add(line);
            var note = EditorKit.Muted(summary, 11, 0);
            note.Margin = new Thickness(0, 5, 0, 2);
            card.Children.Add(note);
            rows.Children.Add(EditorKit.Card(card));
        }
    }
}
