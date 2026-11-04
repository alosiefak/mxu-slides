using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MxU.Slides.Core.Diagnostics;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// "Report a problem": a note of what happened, then a zip in Downloads with the note, this PC's
/// summary and health checks, the last week's logs and hang reports, and the settings, all with
/// secrets taken out. It's never sent: Explorer opens on the file, for the user to give to whoever
/// helps them. Port of the Mac's FeedbackView ("What is included", "The report saves to Downloads").
/// </summary>
internal sealed class ReportProblemWindow : Window
{
    private readonly MainViewModel model;
    private readonly TextBox note = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 130, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock status = EditorKit.Muted("", 12, 0);
    private readonly Button save;

    private ReportProblemWindow(Window owner, MainViewModel model)
    {
        this.model = model;
        EditorKit.Dress(this, owner, "Report a Problem", 560, double.NaN);
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        var crashed = App.RecoveredFromCrash;
        var body = new StackPanel { Margin = new Thickness(20, 18, 20, 0) };
        body.Children.Add(new TextBlock { Text = crashed ? "MxU Slides closed unexpectedly last time" : "Report a Problem", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        body.Children.Add(EditorKit.Muted(crashed
            ? "A report with the logs helps track it down. What were you doing when it happened?"
            : "What happened, and what did you expect? The more specific, the faster it can be fixed.", 12, 8));
        body.Children.Add(note);
        var included = new Expander { Header = "What is included", Foreground = EditorKit.Res<Brush>("Text"), Margin = new Thickness(0, 10, 0, 0) };
        included.Content = EditorKit.Muted("Your note; this PC's summary (Windows, the displays, graphics card, sound devices and cameras) and the health checks; "
            + "the last 7 days of MxU Slides' logs and any reports of it hanging or crashing (these can include file and song names); and the settings files. "
            + "Passwords, keys, tokens and stream keys are taken out. No songs, slides or media.", 11, 0);
        body.Children.Add(included);
        body.Children.Add(status);
        status.Margin = new Thickness(0, 10, 0, 0);
        save = EditorKit.Button("Save Report…", () => _ = SaveAsync(), accent: true);
        save.IsDefault = true;
        var cancel = EditorKit.Button("Cancel", Close);
        cancel.IsCancel = true;
        cancel.Margin = new Thickness(0, 0, 8, 0);
        var footer = new DockPanel { Margin = new Thickness(20, 14, 20, 18) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        footer.Children.Add(EditorKit.Muted("The report saves to Downloads.", 11, 0));
        var root = new StackPanel();
        root.Children.Add(body);
        root.Children.Add(footer);
        Content = root;
        Loaded += (_, _) => note.Focus();
    }

    public static ReportProblemWindow Open(Window owner, MainViewModel model)
    {
        var window = new ReportProblemWindow(owner, model);
        Opened?.Invoke(window, EventArgs.Empty);
        window.ShowDialog();
        return window;
    }

    /// <summary>Raised with the window just before it shows (the self-test pictures it).</summary>
    internal static event EventHandler? Opened;

    /// <summary>Where the report goes; null = Downloads (the self-test points it at its own folder).</summary>
    internal string? Folder { get; set; }

    internal string? Saved { get; private set; }

    internal void Type(string text) => note.Text = text;

    internal async Task SaveAsync()
    {
        if (note.Text.Trim().Length == 0)
        {
            status.Text = "Say what happened first: a sentence is enough.";
            return;
        }
        save.IsEnabled = false;
        status.Text = "Collecting diagnostics…";
        try
        {
            var system = await SystemInfo.GatherAsync(model);
            var checks = HealthChecks.Run(system.Facts);
            var message = note.Text;
            var folder = Folder;
            Saved = await Task.Factory.StartNew(() => ProblemReport.Build(message, system, checks, DateTimeOffset.Now, folder),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            status.Text = $"Saved “{Path.GetFileName(Saved)}” to {(folder is null ? "Downloads" : folder)}. Send it to whoever helps you with MxU Slides.";
            if (folder is null) ProblemReport.Reveal(Saved);
            save.Content = "Done";
            save.IsEnabled = true;
            save.Click += (_, _) => Close();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            status.Text = $"Couldn't save: {ex.Message}";
            save.IsEnabled = true;
        }
    }
}
