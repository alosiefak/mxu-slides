using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MxU.Slides.Core.Library;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// Restoring a backup, looking first: when it was made and on which PC, what each part holds next
/// to the library now (new, the same, different, only here), the parts to bring back (the Mac's two
/// columns of tick boxes) and what to do with a document both have. Nothing changes until Restore,
/// and then a safety copy of the library comes first. Port of the Mac's backup options sheet
/// (ImportActivityModel.requestBackupOptions) and BackupTransferRunner.summaryLines.
/// </summary>
internal sealed class RestoreWindow : Window
{
    private readonly MainViewModel model;
    private readonly string path;
    private readonly BackupScan scan;
    private readonly Dictionary<BackupSection, CheckBox> boxes = [];
    private readonly StackPanel body = new() { Margin = new Thickness(18, 16, 18, 0) };
    private readonly TextBlock status = EditorKit.Muted("", 12, 0);
    private readonly Button restore;
    private BackupPolicy policy = BackupPolicy.NewerWins;

    private RestoreWindow(Window? owner, MainViewModel model, string path, BackupScan scan)
    {
        this.model = model;
        this.path = path;
        this.scan = scan;
        EditorKit.Dress(this, owner, "Restore a Backup", 680, 680);

        var manifest = scan.Manifest;
        body.Children.Add(new TextBlock { Text = "Restore a backup", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        var made = manifest.Created is { } created ? created.ToLocalTime().ToString("dddd d MMMM yyyy 'at' HH:mm", CultureInfo.CurrentCulture) : "at an unknown time";
        body.Children.Add(EditorKit.Muted($"“{Path.GetFileName(path)}”, made {made}"
            + (manifest.Computer is { } pc ? $" on {pc}" : "") + (manifest.App is { } app ? $" by {app}" : "") + ".", 12, 2));
        body.Children.Add(EditorKit.Muted(manifest.IncludesMedia || scan.Count(BackupSection.MediaFiles) > 0
            ? $"It holds {manifest.DocumentTotal} documents and {scan.Count(BackupSection.MediaFiles)} media files."
            : $"It holds {manifest.DocumentTotal} documents, without the media files (media this PC hasn't got will show as missing).", 12, 10));
        foreach (var warning in scan.Warnings)
            body.Children.Add(new TextBlock { Text = "⚠  " + warning, Foreground = EditorKit.Res<Brush>("Warn"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });

        body.Children.Add(EditorKit.Section("WHAT TO BRING BACK", top: 6));
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var content = new StackPanel();
        var setup = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(setup, 1);
        foreach (var section in BackupSections.All)
        {
            var count = scan.Count(section);
            if (count == 0) continue;
            var box = new CheckBox { IsChecked = true, Content = Line(section, count), ToolTip = Compare(section) };
            boxes[section] = box;
            (section.IsContent() || section == BackupSection.MediaFiles ? content : setup).Children.Add(box);
        }
        grid.Children.Add(content);
        grid.Children.Add(setup);
        body.Children.Add(grid);

        body.Children.Add(EditorKit.Section("WHEN BOTH HAVE A DOCUMENT", top: 12));
        foreach (var choice in new[] { BackupPolicy.NewerWins, BackupPolicy.KeepMine, BackupPolicy.Replace, BackupPolicy.Mirror })
        {
            var radio = new RadioButton { Content = choice.Title(), GroupName = "policy", IsChecked = choice == policy, Margin = new Thickness(0, 2, 0, 2) };
            radio.Checked += (_, _) => policy = choice;
            body.Children.Add(radio);
        }
        body.Children.Add(EditorKit.Muted("Before anything changes, a copy of your library as it is now is saved in the backups folder (“Before restore …”), "
            + "so a restore can always be undone by restoring that.", 11, 0));

        restore = EditorKit.Button("Restore", () =>
        {
            if (Result is not null) Close(); // after the restore it's the Done button
            else _ = RestoreAsync();
        }, accent: true);
        restore.IsDefault = true;
        var cancel = EditorKit.Button("Cancel", Close);
        cancel.IsCancel = true;
        cancel.Margin = new Thickness(0, 0, 8, 0);
        var footer = new DockPanel { Margin = new Thickness(18, 12, 18, 16) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(cancel);
        buttons.Children.Add(restore);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        footer.Children.Add(status);
        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        if (model.RestoreBlocked() is { } reason)
        {
            status.Text = reason;
            status.Foreground = EditorKit.Res<Brush>("Warn");
        }
    }

    /// <summary>Reads the backup (off the UI thread), then shows what's in it; says so when it isn't one.</summary>
    public static async Task<RestoreWindow?> OpenAsync(Window? owner, MainViewModel model, string path)
    {
        BackupScan scan;
        try
        {
            scan = await model.ScanBackupAsync(path);
        }
        catch (Exception ex) when (ex is BackupException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            var dialog = new ChoiceWindow(owner, "Restore a Backup", "That backup can't be restored.", ex.Message, warning: true);
            dialog.AddButton("OK", isDefault: true, isCancel: true, accent: true);
            dialog.Ask();
            return null;
        }
        var window = new RestoreWindow(owner, model, path, scan);
        Opened?.Invoke(window, EventArgs.Empty);
        window.Show();
        return window;
    }

    /// <summary>Raised with each restore window as it opens (the self-test pictures it and presses Restore).</summary>
    internal static event EventHandler? Opened;

    private string Line(BackupSection section, int count)
    {
        var line = $"{section.Title()} ({count})";
        if (scan.Compared.TryGetValue(section, out var c) && c.InBackup > 0)
        {
            var parts = new List<string>();
            if (c.New > 0) parts.Add($"{c.New} new");
            if (c.Different > 0) parts.Add($"{c.Different} different");
            if (c.Same > 0 && parts.Count > 0) parts.Add($"{c.Same} the same");
            line += parts.Count == 0 ? " — all the same as yours" : " — " + string.Join(", ", parts);
        }
        return line;
    }

    private string? Compare(BackupSection section) =>
        scan.Compared.TryGetValue(section, out var c) && c.OnlyHere > 0 ? $"{c.OnlyHere} of yours aren't in the backup: “Make the library exactly the backup” moves them to the Trash." : null;

    /// <summary>Starts the restore with the choices made; its summary replaces the choices.</summary>
    internal async Task RestoreAsync()
    {
        if (model.RestoreBlocked() is { } reason)
        {
            status.Text = reason;
            return;
        }
        if (policy == BackupPolicy.Mirror)
        {
            var sure = new ChoiceWindow(this, "Restore a Backup", "Make the library exactly the backup?",
                "Songs, services and the rest that aren't in the backup move to the library's Trash folder. A safety copy is kept first.", warning: true);
            sure.AddButton("Cancel", isCancel: true, isDefault: true);
            sure.AddButton("Make It the Backup", danger: true);
            if (sure.Ask() != "Make It the Backup") return;
        }
        var excluded = boxes.Where(pair => pair.Value.IsChecked != true).Select(pair => pair.Key).ToHashSet();
        restore.IsEnabled = false;
        var progress = new Progress<BackupProgress>(p => status.Text = p.Total is { } total ? $"{p.Phase} {p.Completed} of {total}" : p.Phase);
        var result = await model.RestoreBackupAsync(path, new BackupRestoreOptions(policy, excluded), progress);
        if (result is null)
        {
            status.Text = model.Problem ?? "The backup couldn't be restored.";
            restore.IsEnabled = true;
            return;
        }
        body.Children.Clear();
        body.Children.Add(new TextBlock { Text = "Restored", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        foreach (var line in MainViewModel.RestoreSummary(result, result.Settings.Count))
            body.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1), Foreground = EditorKit.Res<Brush>("Text") });
        foreach (var warning in result.Warnings)
            body.Children.Add(new TextBlock { Text = "⚠  " + warning, Foreground = EditorKit.Res<Brush>("Warn"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        status.Text = "";
        Result = result;
        restore.Content = "Done";
        restore.IsEnabled = true;
    }

    /// <summary>What the restore did, once it has (the self-test reads it).</summary>
    internal BackupRestoreResult? Result { get; private set; }

    /// <summary>Unticks a part (the self-test leaves one out).</summary>
    internal void Exclude(BackupSection section)
    {
        if (boxes.TryGetValue(section, out var box)) box.IsChecked = false;
    }
}
