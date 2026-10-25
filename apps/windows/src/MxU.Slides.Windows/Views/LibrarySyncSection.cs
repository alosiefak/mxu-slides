using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MxU.Slides.Sync.Engine;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// Settings › Library sync (Phase 10): sharing this library with a Mac through a folder both can
/// reach. Choose the folder; see the status, when it last synced, and a log of what came in and
/// went out; pause, sync now, or turn it off. When sync stops to ask (a document this version
/// can't read, many documents gone at once, a newer format), the question and its answers are
/// here. Placed in the Settings window by one line of its XAML; it finds the app's view model
/// itself, and everything takes effect at once (the window's Save button has nothing to do).
/// </summary>
public sealed class LibrarySyncSection : UserControl
{
    private readonly StackPanel body = new();
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromSeconds(20) };
    private MainViewModel? model;

    public LibrarySyncSection()
    {
        var panel = new StackPanel();
        panel.Children.Add(EditorKit.Section("LIBRARY SYNC", 18));
        panel.Children.Add(body);
        Content = panel;
        // "Last synced 2 minutes ago" moves on by itself.
        clock.Tick += (_, _) => Draw();
        Loaded += (_, _) =>
        {
            if (Application.Current?.MainWindow?.DataContext is not MainViewModel found) return;
            model = found;
            model.LibrarySyncChanged += Draw;
            clock.Start();
            Draw();
        };
        Unloaded += (_, _) =>
        {
            clock.Stop();
            if (model is not null) model.LibrarySyncChanged -= Draw;
        };
    }

    private void Draw()
    {
        if (model is null) return;
        body.Children.Clear();
        body.Children.Add(EditorKit.Muted(
            "Share this library with a Mac, or another PC, through a folder both can reach: OneDrive, Dropbox, iCloud for Windows or a network share. "
            + "Edits made on either side are merged, as the Mac merges them between Macs. Both libraries are backed up before the first sync.", 12));

        if (model.LibrarySync is not { } engine)
        {
            var start = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            start.Children.Add(EditorKit.Button("Choose a shared folder…", Choose, "Pick the Mac's library folder or backup, or an empty folder to start a shared library", accent: true));
            body.Children.Add(start);
            return;
        }

        var folder = new TextBox
        {
            Text = engine.SharedFolderPath, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            Foreground = EditorKit.Res<Brush>("Text"), Padding = new Thickness(0), Name = "SyncFolder",
        };
        body.Children.Add(EditorKit.Row("Folder", folder, 70));

        var status = engine.Status;
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 6) };
        line.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = Dot(status.State), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
        line.Children.Add(new TextBlock
        {
            Text = status.Text + (status.LastSync is { } last ? $"  ·  last synced {Ago(last)}" : ""),
            TextWrapping = TextWrapping.Wrap, MaxWidth = 560, Name = "SyncStatus", Foreground = EditorKit.Res<Brush>("Text"),
        });
        body.Children.Add(line);

        if (engine.Attention is { } attention) body.Children.Add(Question(engine, attention));

        var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        buttons.Children.Add(EditorKit.Button("Sync now", engine.SyncNow, "Look through both libraries now"));
        var paused = status.State == SyncState.Paused;
        buttons.Children.Add(EditorKit.Button(paused ? "Resume" : "Pause", () => model.PauseLibrarySync(!paused),
            paused ? "Carry on syncing" : "Stop syncing for now: edits wait on each side until you resume"));
        buttons.Children.Add(EditorKit.Button("Choose another folder…", Choose));
        buttons.Children.Add(EditorKit.Button("Open folder", () => OpenInExplorer(engine.SharedFolderPath)));
        buttons.Children.Add(EditorKit.Button("Turn off", () =>
        {
            if (MessageBox.Show(Window.GetWindow(this),
                    "Stop syncing this library? Nothing is deleted on either side. Choose the folder again later to carry on where it left off.",
                    "Library sync", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                model.TurnOffLibrarySync();
        }));
        body.Children.Add(buttons);

        body.Children.Add(new TextBlock { Text = "WHAT CAME IN AND WENT OUT", Style = EditorKit.Res<Style>("SectionLabel"), Margin = new Thickness(0, 4, 0, 4) });
        body.Children.Add(LogView(engine));
        var backups = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var open = EditorKit.Button("Open sync folder", () => OpenInExplorer(engine.StateFolder), "This PC's sync records: the backups, the log and the sync trash");
        DockPanel.SetDock(open, Dock.Right);
        backups.Children.Add(open);
        backups.Children.Add(EditorKit.Muted("↓ came in from the shared folder, ↑ went out to it. Deleted documents go to the library's Trash, or the sync trash, never away for good.", 11, 0));
        body.Children.Add(backups);
    }

    /// <summary>What sync stopped to ask, and the answers.</summary>
    private UIElement Question(SyncEngine engine, SyncAttention attention)
    {
        var panel = new StackPanel { Name = "SyncQuestion" };
        panel.Children.Add(new TextBlock { Text = attention.Message, TextWrapping = TextWrapping.Wrap, Foreground = EditorKit.Res<Brush>("Warn"), Margin = new Thickness(0, 0, 0, 4) });
        foreach (var item in attention.Items.Take(6))
            panel.Children.Add(EditorKit.Muted($"•  {item.Name}: {item.Reason}", 11, 1));
        if (attention.Items.Count > 6) panel.Children.Add(EditorKit.Muted($"…and {attention.Items.Count - 6} more (the log lists them all).", 11, 1));
        var answers = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        switch (attention.Kind)
        {
            case AttentionKind.Unreadable:
                answers.Children.Add(EditorKit.Button("Leave them out and carry on", engine.LeaveOutUnreadable,
                    "They stay as they are in the shared folder and aren't brought here; each is looked at again when it changes", accent: true));
                break;
            case AttentionKind.HeldDeletions:
                answers.Children.Add(EditorKit.Button("Keep them", engine.KeepHeldDeletions, "Keep them here and put them back in the shared folder", accent: true));
                answers.Children.Add(EditorKit.Button("Delete them here too", () =>
                {
                    if (MessageBox.Show(Window.GetWindow(this),
                            $"Delete {attention.Items.Count} documents from this library too? Each goes to the library's Trash.",
                            "Library sync", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
                        engine.ApplyHeldDeletions();
                }, "They were deleted on the Mac"));
                break;
            default:
                answers.Children.Add(EditorKit.Button("Try again", engine.SyncNow, "After updating, or after fixing the folder"));
                break;
        }
        panel.Children.Add(answers);
        return EditorKit.Card(panel, 8);
    }

    private static UIElement LogView(SyncEngine engine)
    {
        var lines = new StackPanel { Name = "SyncLog" };
        var entries = engine.Log.Entries;
        if (entries.Count == 0) lines.Children.Add(EditorKit.Faint("Nothing yet.", 11));
        foreach (var entry in entries.Reverse().Take(80))
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
            var when = new TextBlock
            {
                Text = entry.At.LocalDateTime.Date == DateTime.Today ? $"{entry.At.LocalDateTime:HH:mm}" : $"{entry.At.LocalDateTime:d MMM HH:mm}",
                Width = entry.At.LocalDateTime.Date == DateTime.Today ? 40 : 82, FontSize = 11, Foreground = EditorKit.Res<Brush>("FaintText"),
            };
            var arrow = new TextBlock
            {
                Text = entry.Arrow, Width = 16, FontSize = 11, FontWeight = FontWeights.SemiBold,
                Foreground = entry.Direction switch
                {
                    SyncDirection.Problem => EditorKit.Res<Brush>("Warn"),
                    SyncDirection.In or SyncDirection.Out => EditorKit.Res<Brush>("Accent"),
                    _ => EditorKit.Res<Brush>("MutedText"),
                },
            };
            DockPanel.SetDock(when, Dock.Left);
            DockPanel.SetDock(arrow, Dock.Left);
            row.Children.Add(when);
            row.Children.Add(arrow);
            row.Children.Add(new TextBlock
            {
                Text = entry.Text, TextWrapping = TextWrapping.Wrap, FontSize = 11,
                Foreground = EditorKit.Res<Brush>(entry.Direction == SyncDirection.Problem ? "Warn" : "Text"),
            });
            lines.Children.Add(row);
        }
        return new Border
        {
            BorderBrush = EditorKit.Res<Brush>("Border"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Background = EditorKit.Res<Brush>("Card"), Padding = new Thickness(8, 6, 8, 6),
            Child = new ScrollViewer { Content = lines, MaxHeight = 190, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        };
    }

    private void Choose()
    {
        if (model is null) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose the shared library folder: the Mac's library or backup, or an empty folder",
            InitialDirectory = model.LibrarySync?.SharedFolderPath ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        if (SyncEngine.CheckFolder(dialog.FolderName, model.Library.Root) is { } problem)
        {
            MessageBox.Show(Window.GetWindow(this), problem, "Library sync", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var ask = $"Sync this library with\n{dialog.FolderName}?\n\n"
            + "Both libraries are backed up first. Then what's on either side appears on the other, and edits are merged as they're made. "
            + "Where both have the same document already, the copy changed more recently is kept (the other stays in the backup).";
        if (MessageBox.Show(Window.GetWindow(this), ask, "Library sync", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        if (model.ChooseLibrarySyncFolder(dialog.FolderName) is { } refused)
            MessageBox.Show(Window.GetWindow(this), refused, "Library sync", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static Brush Dot(SyncState state) => state switch
    {
        SyncState.UpToDate => EditorKit.Res<Brush>("Good"),
        SyncState.NeedsAttention or SyncState.WaitingForFolder or SyncState.Failed => EditorKit.Res<Brush>("Warn"),
        SyncState.Syncing or SyncState.Starting => EditorKit.Res<Brush>("Accent"),
        _ => EditorKit.Res<Brush>("MutedText"),
    };

    private static string Ago(DateTimeOffset when)
    {
        var gone = DateTimeOffset.Now - when;
        return gone.TotalSeconds < 60 ? "just now"
            : gone.TotalMinutes < 60 ? $"{(int)gone.TotalMinutes} minute{((int)gone.TotalMinutes == 1 ? "" : "s")} ago"
            : when.LocalDateTime.Date == DateTime.Today ? $"at {when.LocalDateTime:HH:mm}"
            : $"on {when.LocalDateTime:d MMM} at {when.LocalDateTime:HH:mm}";
    }

    private static void OpenInExplorer(string folder)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }
}
