using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// "Locate Missing Media": every media item whose file isn't on this PC, and every deleted
/// item something still uses. <b>Scan Folder…</b> looks through a folder (a USB stick, the
/// church's shared drive) for exact copies by content, so names don't matter, and relinks
/// them all at once; <b>Choose File…</b> on a row takes any file instead. A file in the scanned
/// folder with the original's name but other content is offered on its row ("Use …"), never
/// used by itself. Port of the Mac's LocateMissingMediaView.
/// </summary>
internal sealed class LocateMissingMediaWindow : Window
{
    private readonly MainViewModel model;
    private readonly StackPanel list = new();
    private readonly TextBlock summary = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    private readonly Button scan;
    private MissingMedia.Targets targets = new([], []);
    private readonly Dictionary<string, IReadOnlyList<string>> carriedNotes = new(StringComparer.Ordinal);
    private Dictionary<string, MissingMedia.NameMatch> suggestions = new(StringComparer.Ordinal);
    private bool scanning;

    private LocateMissingMediaWindow(Window? owner, MainViewModel model)
    {
        this.model = model;
        EditorKit.Dress(this, owner, "Locate Missing Media", 640, 500);

        var body = new DockPanel { Margin = new Thickness(18, 16, 18, 0) };
        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = "Locate Missing Media", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        top.Children.Add(EditorKit.Muted("Scan a folder and exact copies relink automatically — matching is by file content, so names don't matter. "
            + "Anything unmatched can be restored by hand with any file.", 12, 12));
        DockPanel.SetDock(top, Dock.Top);
        body.Children.Add(top);
        body.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        scan = EditorKit.Button("Scan Folder…", () => _ = PickFolderAndScan());
        var done = EditorKit.Button("Done", Close, accent: true);
        done.IsDefault = true;
        done.IsCancel = true;
        done.MinWidth = 90;
        done.Margin = new Thickness(0);
        var footer = new DockPanel { Margin = new Thickness(18, 10, 18, 16), LastChildFill = false };
        DockPanel.SetDock(done, Dock.Right);
        footer.Children.Add(done);
        footer.Children.Add(scan);
        footer.Children.Add(summary);

        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(body);
        Content = root;
        Loaded += async (_, _) => await RefreshAsync();
    }

    /// <summary>Opens the window (or brings the open one forward).</summary>
    public static LocateMissingMediaWindow Open(Window? owner, MainViewModel model)
    {
        if (Application.Current.Windows.OfType<LocateMissingMediaWindow>().FirstOrDefault() is { } open)
        {
            open.Activate();
            _ = open.RefreshAsync();
            return open;
        }
        var window = new LocateMissingMediaWindow(owner, model);
        window.Show();
        return window;
    }

    /// <summary>What's listed right now, one line per row (the self-test reads it).</summary>
    internal IReadOnlyList<MediaItem> Missing => targets.Missing;

    internal IReadOnlyList<MediaItem> Tombstones => targets.Tombstones;

    internal string Summary => summary.Text;

    internal async Task RefreshAsync()
    {
        targets = await model.FindMissingMediaAsync();
        Draw();
    }

    private async Task PickFolderAndScan()
    {
        if (LibraryDialogs.ChooseFolder("Scan a folder for the missing files") is { } folder) await ScanAsync(folder);
    }

    /// <summary>Scans a folder and relinks what it finds (the Scan Folder button, or the self-test).</summary>
    internal async Task ScanAsync(string folder)
    {
        scanning = true;
        summary.Text = "Scanning…";
        Draw();
        var (relinked, restored, byName) = await model.LocateMissingMediaAsync(folder);
        scanning = false;
        var total = relinked + restored;
        summary.Text = total == 0 ? "No exact matches in that folder." : $"Relinked {total} item{(total == 1 ? "" : "s")}.";
        suggestions = byName.ToDictionary(m => m.Item.Id, StringComparer.Ordinal);
        if (byName.Count > 0) summary.Text += $" {byName.Count} more with the same name but different content: check them below.";
        await RefreshAsync();
    }

    /// <summary>Gives one row's item a file (Choose File…, or a same-name suggestion).</summary>
    internal async Task UseFileAsync(MediaItem item, bool isTombstone, string path)
    {
        if (await model.UseFileForAsync(item, isTombstone, path) is { } carried)
        {
            carriedNotes[item.Id] = carried;
            suggestions.Remove(item.Id);
        }
        await RefreshAsync();
    }

    private void Draw()
    {
        list.Children.Clear();
        scan.IsEnabled = !scanning && (targets.Missing.Count > 0 || targets.Tombstones.Count > 0);
        if (targets.Missing.Count == 0 && targets.Tombstones.Count == 0)
        {
            list.Children.Add(EditorKit.Muted("Nothing is missing.", 12));
            foreach (var (id, carried) in carriedNotes.Where(n => n.Value.Count > 0))
                if (model.Library.Get<MediaItem>(id) is { } done)
                    list.Children.Add(Note($"“{done.Name}”: check {string.Join(", ", carried)} — carried over from the old file."));
            return;
        }
        if (targets.Missing.Count > 0)
        {
            list.Children.Add(EditorKit.Section("FILES MISSING ON THIS PC", top: 0));
            foreach (var item in targets.Missing) list.Children.Add(Row(item, isTombstone: false));
        }
        if (targets.Tombstones.Count > 0)
        {
            list.Children.Add(EditorKit.Section("DELETED ITEMS STILL IN USE"));
            foreach (var item in targets.Tombstones) list.Children.Add(Row(item, isTombstone: true));
        }
    }

    private UIElement Row(MediaItem item, bool isTombstone)
    {
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = item.Name, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock
        {
            Text = isTombstone ? $"{(item.FileName.Length > 0 ? item.FileName : "No file")} — deleted, still used somewhere" : item.FileName,
            FontSize = 11, Foreground = EditorKit.Res<Brush>("MutedText"), TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (carriedNotes.TryGetValue(item.Id, out var carried) && carried.Count > 0)
            text.Children.Add(Note($"Check {string.Join(", ", carried)} — carried over from the old file."));

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (suggestions.TryGetValue(item.Id, out var match))
        {
            var use = EditorKit.Button($"Use “{Path.GetFileName(match.Path)}”", () => _ = UseFileAsync(item, isTombstone, match.Path),
                $"Same name, different content: {match.Path}");
            actions.Children.Add(use);
        }
        actions.Children.Add(EditorKit.Button("Choose File…", () =>
        {
            if (LibraryDialogs.ChooseMediaFile($"Choose a file for “{item.Name}”") is { } path) _ = UseFileAsync(item, isTombstone, path);
        }));

        var row = new DockPanel();
        DockPanel.SetDock(actions, Dock.Right);
        row.Children.Add(actions);
        row.Children.Add(text);
        return EditorKit.Card(row);
    }

    private static TextBlock Note(string text) => new()
    {
        Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = EditorKit.Res<Brush>("Warn"), Margin = new Thickness(0, 2, 0, 0),
    };
}
