using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using MxU.Slides.Core.Library;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// Backups: make one now (with or without the media files), choose how often they're made by
/// themselves and how many are kept, and restore one, from the list of those in the backups
/// folder or from any file. Restoring opens <see cref="RestoreWindow"/>, which shows what's in it
/// first. The Mac has File › Export Library Backup… and Import Library Backup… (BackupTransfer.swift);
/// the automatic ones are Windows'.
/// </summary>
internal sealed class BackupWindow : Window
{
    private readonly MainViewModel model;
    private readonly StackPanel list = new();
    private readonly TextBlock status = EditorKit.Muted("", 12, 0);
    private readonly TextBlock folderLine = EditorKit.Muted("", 11, 4);

    private BackupWindow(Window? owner, MainViewModel model)
    {
        this.model = model;
        EditorKit.Dress(this, owner, "Backups", 640, 620);
        var settings = model.Settings;

        var top = new StackPanel { Margin = new Thickness(18, 16, 18, 0) };
        top.Children.Add(new TextBlock { Text = "Backups", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        top.Children.Add(EditorKit.Muted("A backup is one file holding the whole library: songs, services, media items, themes, overlays, the Windows-only additions, "
            + "and this PC's portable settings. Copy one to a USB stick or OneDrive and you can bring the library back on any PC.", 12, 10));

        var now = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        var withMedia = new CheckBox { Content = "Include the media files (much bigger)", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        now.Children.Add(EditorKit.Button("Back Up Now…", () => _ = BackUpNow(withMedia.IsChecked == true), accent: true));
        now.Children.Add(withMedia);
        top.Children.Add(now);

        top.Children.Add(EditorKit.Section("AUTOMATIC BACKUPS", top: 6));
        var frequency = new ComboBox { Width = 180 };
        foreach (var (value, label) in new[] { (BackupFrequency.Off, "Off"), (BackupFrequency.EveryLaunch, "Every time the app opens"), (BackupFrequency.Daily, "Once a day"), (BackupFrequency.Weekly, "Once a week") })
            frequency.Items.Add(new ComboBoxItem { Content = label, Tag = value, IsSelected = settings.AutoBackup == value });
        frequency.SelectionChanged += (_, _) =>
        {
            if (frequency.SelectedItem is ComboBoxItem { Tag: BackupFrequency value })
            {
                settings.AutoBackup = value;
                settings.Save();
            }
        };
        top.Children.Add(EditorKit.Row("Make one", frequency, labelWidth: 110));
        top.Children.Add(EditorKit.Row("Keep the last", EditorKit.NumberBox(settings.AutoBackupKeep, value =>
        {
            settings.AutoBackupKeep = Math.Clamp(value, 1, 365);
            settings.Save();
            return settings.AutoBackupKeep;
        }, display: n => n == 1 ? "backup" : "backups"), labelWidth: 110));
        var media = new CheckBox { Content = "Include the media files (made only while nothing is on screen)", IsChecked = settings.AutoBackupIncludesMedia };
        media.Click += (_, _) =>
        {
            settings.AutoBackupIncludesMedia = media.IsChecked == true;
            settings.Save();
        };
        top.Children.Add(EditorKit.Row("", media, labelWidth: 110));
        var folderButtons = new StackPanel { Orientation = Orientation.Horizontal };
        folderButtons.Children.Add(EditorKit.Button("Change…", ChangeFolder));
        var open = EditorKit.Button("Open Folder", () => MainWindow.OpenFolder(model.BackupFolder));
        open.Margin = new Thickness(6, 0, 0, 0);
        folderButtons.Children.Add(open);
        var reset = EditorKit.Button("Use the Library's Folder", () =>
        {
            settings.AutoBackupFolder = null;
            settings.Save();
            Refresh();
        });
        reset.Margin = new Thickness(6, 0, 0, 0);
        folderButtons.Children.Add(reset);
        top.Children.Add(EditorKit.Row("Keep them in", folderButtons, labelWidth: 110, bottom: 2));
        top.Children.Add(folderLine);

        var listHeader = new DockPanel { Margin = new Thickness(0, 10, 0, 6) };
        var fromFile = EditorKit.Button("Restore from a File…", RestoreFromFile);
        DockPanel.SetDock(fromFile, Dock.Right);
        listHeader.Children.Add(fromFile);
        listHeader.Children.Add(EditorKit.Section("BACKUPS IN THE FOLDER", top: 4));
        top.Children.Add(listHeader);

        var body = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        body.Children.Add(top);
        var done = EditorKit.Button("Done", Close, accent: true);
        done.IsDefault = true;
        done.IsCancel = true;
        var footer = new DockPanel { Margin = new Thickness(18, 10, 18, 16), LastChildFill = true };
        DockPanel.SetDock(done, Dock.Right);
        footer.Children.Add(done);
        footer.Children.Add(status);
        DockPanel.SetDock(footer, Dock.Bottom);
        body.Children.Add(footer);
        body.Children.Add(new ScrollViewer { Content = list, Margin = new Thickness(18, 0, 18, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = body;
        Loaded += (_, _) => Refresh();
    }

    public static BackupWindow Open(Window? owner, MainViewModel model)
    {
        var window = new BackupWindow(owner, model);
        Opened?.Invoke(window, EventArgs.Empty);
        window.Show();
        return window;
    }

    /// <summary>Raised with each Backups window as it opens (the self-test pictures it).</summary>
    internal static event EventHandler? Opened;

    private void Refresh()
    {
        folderLine.Text = model.BackupFolder;
        status.Text = model.LastBackupLine;
        list.Children.Clear();
        var entries = BackupFiles.List(model.BackupFolder);
        if (entries.Count == 0) list.Children.Add(EditorKit.Muted("None yet.", 12));
        foreach (var entry in entries) list.Children.Add(Row(entry));
    }

    private Border Row(BackupFiles.Entry entry)
    {
        var kind = entry.Kind switch
        {
            BackupFiles.Kind.Automatic => "Automatic",
            BackupFiles.Kind.SafetyCopy => "Before a restore",
            BackupFiles.Kind.DailyCopy => "Daily copy (documents only)",
            _ => "Saved",
        };
        var row = new DockPanel();
        var restore = EditorKit.Button("Restore…", () => _ = RestoreWindow.OpenAsync(this, model, entry.Path));
        DockPanel.SetDock(restore, Dock.Right);
        row.Children.Add(restore);
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = entry.Created.ToString("dddd d MMMM yyyy, HH:mm", CultureInfo.CurrentCulture), Foreground = EditorKit.Res<Brush>("Text") });
        text.Children.Add(EditorKit.Muted($"{kind}{(entry.Bytes > 0 ? " · " + Size(entry.Bytes) : "")} · {entry.Name}", 11, 0));
        row.Children.Add(text);
        return EditorKit.Card(row);
    }

    private static string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.0} MB" : $"{Math.Max(1, bytes / 1024)} KB";

    private async Task BackUpNow(bool includeMedia)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Back up the library",
            FileName = BackupFiles.SavedName(DateTimeOffset.Now),
            DefaultExt = "." + LibraryBackup.FileExtension,
            Filter = $"MxU Slides library backup (*.{LibraryBackup.FileExtension})|*.{LibraryBackup.FileExtension}",
            InitialDirectory = Directory.Exists(model.BackupFolder) ? model.BackupFolder : null,
        };
        if (dialog.ShowDialog(this) != true) return;
        status.Text = "Backing up…";
        var progress = new Progress<BackupProgress>(p => status.Text = p.Total is { } total ? $"{p.Phase} {p.Completed} of {total}" : p.Phase);
        var manifest = await model.BackUpNowAsync(dialog.FileName, includeMedia, progress);
        Refresh();
        if (manifest is null) return;
        // The Mac's summary says what's left out on purpose, so nobody looks for it later.
        status.Text = $"Saved “{Path.GetFileName(dialog.FileName)}”: {manifest.DocumentTotal} documents, {manifest.BlobCount} media files. "
            + "Left out by design: stream keys, passwords, and this PC's screens and devices.";
        model.Notice = $"Backed up the library to {dialog.FileName}.";
    }

    private void ChangeFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Where should automatic backups go?", InitialDirectory = Directory.Exists(model.BackupFolder) ? model.BackupFolder : null };
        if (dialog.ShowDialog(this) != true) return;
        model.Settings.AutoBackupFolder = dialog.FolderName;
        model.Settings.Save();
        Refresh();
    }

    private void RestoreFromFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a library backup to restore",
            Filter = $"MxU Slides library backup (*.{LibraryBackup.FileExtension})|*.{LibraryBackup.FileExtension}|All files|*.*",
        };
        if (dialog.ShowDialog(this) == true) _ = RestoreWindow.OpenAsync(this, model, dialog.FileName);
    }
}
