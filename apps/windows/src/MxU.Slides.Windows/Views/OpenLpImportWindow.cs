using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MxU.Slides.OpenLpImport;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// "Import from OpenLP…" (Phase 15). One small window that does the whole job in three stages:
/// <list type="number">
/// <item><b>choose</b>: find OpenLP on this PC, pick its data folder, or pick a songs.sqlite or zip brought from the church PC;</item>
/// <item><b>working</b>: a progress bar while the songs come in (hundreds take a few seconds);</item>
/// <item><b>summary</b>: how many came in, how many were already here, and a list of anything to check, with a Copy button.</item>
/// </list>
/// The work itself is <see cref="MainViewModel.ImportOpenLpAsync"/>; this window only asks where
/// the data is and shows the answer.
/// </summary>
internal sealed class OpenLpImportWindow : Window
{
    private readonly MainViewModel model;
    private readonly StackPanel choose = new();
    private readonly StackPanel working = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel summary = new() { Visibility = Visibility.Collapsed };
    private readonly ProgressBar bar = new() { Height = 8, Margin = new Thickness(0, 4, 0, 8), IsIndeterminate = true };
    private readonly TextBlock status = EditorKit.Muted("", 12, 0);
    private readonly TextBlock problem = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Orange, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };

    /// <summary>What the last import did (null before one finishes, or when it could not start).</summary>
    internal OpenLpImportResult? Result { get; private set; }

    /// <summary>The text the Copy button puts on the clipboard.</summary>
    internal string ReportText => Result?.Report() ?? "";

    internal OpenLpImportWindow(Window? owner, MainViewModel model)
    {
        this.model = model;
        EditorKit.Dress(this, owner, "Import from OpenLP", 620, double.NaN);
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;

        choose.Children.Add(Heading("Bring your songs across from OpenLP"));
        choose.Children.Add(Body("Songs and custom slides are copied into this PC's library. OpenLP's own files are only read, never changed. "
            + "Close OpenLP first if it is running on this PC. Songs already in the library are left out, so it is safe to run this again."));
        Choice("Find OpenLP on this PC", "Looks in the usual place, %APPDATA%\\openlp\\data.", FindOnThisPc);
        Choice("Choose the OpenLP data folder…", "The folder that holds the songs and custom folders. In OpenLP: Tools › Open Data Folder.", ChooseFolder);
        Choice("Choose a file…", "songs.sqlite or custom.sqlite, or a zip of the data folder brought from another PC.", ChooseFile);
        choose.Children.Add(problem);

        working.Children.Add(Heading("Importing from OpenLP…"));
        working.Children.Add(bar);
        working.Children.Add(status);

        var root = new StackPanel();
        root.Children.Add(choose);
        root.Children.Add(working);
        root.Children.Add(summary);
        // Padding on a border, not a margin on the panel: the window's content then fills the window exactly.
        Content = new Border { Padding = new Thickness(20, 18, 20, 18), Child = root };
        // Closing in the middle would leave the import running with nowhere to show its result.
        Closing += (_, e) => e.Cancel = working.Visibility == Visibility.Visible;
    }

    private static TextBlock Heading(string text) => new()
    {
        Text = text, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = EditorKit.Res<Brush>("Text"), Margin = new Thickness(0, 0, 0, 8),
    };

    private static TextBlock Body(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, Foreground = EditorKit.Res<Brush>("MutedText"), Margin = new Thickness(0, 0, 0, 12), FontSize = 13,
    };

    private void Choice(string title, string detail, Action action)
    {
        var button = EditorKit.Button(title, action);
        button.HorizontalAlignment = HorizontalAlignment.Left;
        button.MinWidth = 280;
        choose.Children.Add(button);
        choose.Children.Add(new TextBlock
        {
            Text = detail, FontSize = 11, Foreground = EditorKit.Res<Brush>("MutedText"), Margin = new Thickness(2, 3, 0, 12), TextWrapping = TextWrapping.Wrap,
        });
    }

    private void Say(string text)
    {
        problem.Text = text;
        problem.Visibility = Visibility.Visible;
    }

    private void FindOnThisPc()
    {
        if (OpenLpImporter.FindOnThisPc() is { } folder) _ = RunAsync(folder);
        else Say("OpenLP's songs were not found in the usual place on this PC. If OpenLP is on another PC, copy its data folder here (in OpenLP: Tools › Open Data Folder), "
            + "or if its data folder was moved, choose that folder.");
    }

    private void ChooseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose the OpenLP data folder (the one holding songs and custom)",
            InitialDirectory = OpenLpImporter.FindOnThisPc() ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(this) == true) _ = RunAsync(dialog.FolderName);
    }

    private void ChooseFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose OpenLP's songs.sqlite, or a zip of its data folder",
            Filter = "OpenLP data (*.sqlite, *.zip)|*.sqlite;*.zip|All files|*.*",
        };
        if (dialog.ShowDialog(this) == true) _ = RunAsync(dialog.FileName);
    }

    /// <summary>Runs the import from a file or folder and shows the summary (or, if it could not start, why).</summary>
    internal async Task RunAsync(string path)
    {
        if (working.Visibility == Visibility.Visible) return;
        choose.Visibility = Visibility.Collapsed;
        summary.Visibility = Visibility.Collapsed;
        working.Visibility = Visibility.Visible;
        bar.IsIndeterminate = true;
        status.Text = "Reading OpenLP's database…";
        // Called on the UI thread: the view model passes each report on from its own Progress.
        var progress = new InlineProgress(p =>
        {
            if (p.Total <= 0) return;
            bar.IsIndeterminate = false;
            bar.Maximum = p.Total;
            bar.Value = p.Completed;
            status.Text = p.Completed < p.Total ? $"{p.Completed + 1} of {p.Total}: {p.Phase}" : "Finishing…";
        });
        Result = await model.ImportOpenLpAsync(path, progress);
        working.Visibility = Visibility.Collapsed;
        if (Result is { } result)
        {
            ShowSummary(result);
        }
        else
        {
            choose.Visibility = Visibility.Visible;
            Say(model.Problem ?? "The import could not start.");
        }
    }

    private sealed class InlineProgress(Action<OpenLpImportProgress> report) : IProgress<OpenLpImportProgress>
    {
        public void Report(OpenLpImportProgress value) => report(value);
    }

    private void ShowSummary(OpenLpImportResult result)
    {
        summary.Children.Clear();
        summary.Children.Add(Heading(result.SongsImported + result.CustomSlidesImported > 0 ? "Imported from OpenLP" : "Nothing new to import"));

        var counts = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        counts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        counts.ColumnDefinitions.Add(new ColumnDefinition());
        void Count(string label, string value)
        {
            var row = counts.RowDefinitions.Count;
            counts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = new TextBlock { Text = label, Foreground = EditorKit.Res<Brush>("MutedText"), Margin = new Thickness(0, 0, 18, 4), FontSize = 13 };
            var number = new TextBlock { Text = value, Foreground = EditorKit.Res<Brush>("Text"), FontSize = 13, FontWeight = FontWeights.SemiBold };
            Grid.SetRow(name, row);
            Grid.SetRow(number, row);
            Grid.SetColumn(number, 1);
            counts.Children.Add(name);
            counts.Children.Add(number);
        }
        Count("Songs imported", $"{result.SongsImported} of {result.SongsFound}");
        Count("Custom slides imported", $"{result.CustomSlidesImported} of {result.CustomSlidesFound}"
            + (result.CustomSlidesImported > 0 ? $"  (in the “{OpenLpSongMapper.CustomFolder}” folder)" : ""));
        Count("Already in the library, left out", result.SkippedDuplicates.Count.ToString());
        Count("Could not be imported", result.NotImported.Count.ToString());
        summary.Children.Add(counts);

        // One list for everything worth a look: the reasons first, then what was left out as a duplicate.
        var lines = result.Warnings.Select(w => w.ToString())
            .Concat(result.SkippedDuplicates.Select(title => $"{title}: already in the library, left out")).ToList();
        if (lines.Count > 0)
        {
            summary.Children.Add(EditorKit.Section($"TO CHECK ({lines.Count})", top: 4));
            summary.Children.Add(new TextBox
            {
                Text = string.Join(Environment.NewLine, lines), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 190, FontSize = 12,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0), Padding = new Thickness(10),
                Background = EditorKit.Res<Brush>("Card"), Foreground = EditorKit.Res<Brush>("Text"),
            });
        }
        else
        {
            summary.Children.Add(Body("Nothing needs checking."));
        }
        var advice = Body("Before Sunday, open five songs you know well and compare them with OpenLP: the words, the slide breaks and the order.");
        advice.Margin = new Thickness(0, 10, 0, 0);
        summary.Children.Add(advice);

        var copied = EditorKit.Muted("", 11, 0);
        copied.VerticalAlignment = VerticalAlignment.Center;
        var copy = EditorKit.Button("Copy", () =>
        {
            try
            {
                Clipboard.SetText(ReportText);
                copied.Text = "Copied: paste it into an email or a note.";
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Another program is holding the clipboard just now.
                copied.Text = "The clipboard is busy; try again.";
            }
        }, "Copies the counts and the list, to paste somewhere or send to whoever helps you");
        var done = EditorKit.Button("Done", Close, accent: true);
        done.IsDefault = done.IsCancel = true;
        done.MinWidth = 90;
        done.Margin = new Thickness(0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(copy);
        buttons.Children.Add(done);
        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        footer.Children.Add(copied);
        summary.Children.Add(footer);
        summary.Visibility = Visibility.Visible;
    }
}
