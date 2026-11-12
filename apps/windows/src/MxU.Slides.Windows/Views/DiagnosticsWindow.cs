using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using MxU.Slides.Core.Diagnostics;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// Diagnostics: is this PC ready for Sunday? Three pages. <b>This PC</b>: the health checks (each with
/// what to do) and what the PC is: Windows, the displays, the graphics card, sound devices, cameras,
/// NDI and DeckLink. <b>Logs</b>: the latest lines of the log, and any times the app stopped responding.
/// <b>Check this PC</b>: the app's own self-test, run in a separate copy on a throwaway library, with
/// one line per area a volunteer can read. "Report a problem…" makes the zip to send. The Mac's
/// twins are FeedbackView and DiagnosticsStore; the checklist and the PC check are Windows'.
/// </summary>
internal sealed class DiagnosticsWindow : Window
{
    public enum Page
    {
        ThisPc,
        Logs,
        Check,
    }

    private readonly MainViewModel model;
    private readonly ContentControl content = new() { Margin = new Thickness(18, 10, 18, 0) };
    private readonly Dictionary<Page, ToggleButton> tabs = [];
    private readonly StackPanel checkResults = new();
    private readonly TextBlock checkStatus = EditorKit.Muted("", 12, 6);
    private CancellationTokenSource? checkCancel;
    private Page page;

    private DiagnosticsWindow(Window owner, MainViewModel model, Page start)
    {
        this.model = model;
        EditorKit.Dress(this, owner, "Diagnostics", 760, 680);
        var header = new DockPanel { Margin = new Thickness(18, 16, 18, 0) };
        var report = EditorKit.Button("Report a Problem…", () => ReportProblemWindow.Open(this, model));
        DockPanel.SetDock(report, Dock.Right);
        header.Children.Add(report);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (value, title) in new[] { (Page.ThisPc, "This PC"), (Page.Logs, "Logs"), (Page.Check, "Check this PC") })
        {
            var tab = new ToggleButton { Content = title, Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 4, 0) };
            tab.Click += (_, _) => ShowPage(value);
            tabs[value] = tab;
            row.Children.Add(tab);
        }
        header.Children.Add(row);
        var done = EditorKit.Button("Done", Close, accent: true);
        done.IsCancel = true;
        var footer = new DockPanel { Margin = new Thickness(18, 10, 18, 16) };
        DockPanel.SetDock(done, Dock.Right);
        footer.Children.Add(done);
        footer.Children.Add(EditorKit.Muted("Nothing here is sent anywhere. A problem report is a file you choose who to give.", 11, 0));
        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        Closed += (_, _) => checkCancel?.Cancel();
        Loaded += (_, _) => ShowPage(start);
    }

    public static DiagnosticsWindow Open(Window owner, MainViewModel model, Page start = Page.ThisPc)
    {
        var window = new DiagnosticsWindow(owner, model, start);
        Opened?.Invoke(window, EventArgs.Empty);
        window.Show();
        return window;
    }

    /// <summary>Raised with each Diagnostics window as it opens (the self-test pictures it).</summary>
    internal static event EventHandler? Opened;

    /// <summary>The page shown (the self-test turns them).</summary>
    internal Page Current => page;

    /// <summary>Done once the This PC page has gathered and shown everything (the self-test waits for it).</summary>
    internal Task? Gathered { get; private set; }

    internal void ShowPage(Page next)
    {
        page = next;
        foreach (var (value, tab) in tabs) tab.IsChecked = value == next;
        switch (next)
        {
            case Page.Logs:
                content.Content = LogsPage();
                break;
            case Page.Check:
                content.Content = CheckPage();
                break;
            default:
                content.Content = EditorKit.Muted("Looking at this PC…", 12);
                Gathered = GatherAsync();
                break;
        }
    }

    // MARK: This PC

    private async Task GatherAsync()
    {
        var report = await SystemInfo.GatherAsync(model);
        if (page != Page.ThisPc) return;
        var checks = HealthChecks.Run(report.Facts);
        var panel = new StackPanel();
        var overall = HealthChecks.Overall(checks);
        panel.Children.Add(new TextBlock
        {
            Text = overall switch
            {
                HealthStatus.Problem => "Something needs fixing before the service.",
                HealthStatus.Warning => "Ready, with a few things worth a look.",
                _ => "This PC looks ready.",
            },
            FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 8),
        });
        foreach (var check in checks) panel.Children.Add(CheckRow(check));
        panel.Children.Add(EditorKit.Section("THIS PC", top: 14));
        foreach (var group in report.Lines.GroupBy(l => l.Area))
        {
            var block = new StackPanel();
            block.Children.Add(new TextBlock { Text = group.Key, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) });
            foreach (var line in group)
                block.Children.Add(new TextBlock { Text = $"{line.Name}: {line.Value}", TextWrapping = TextWrapping.Wrap, Foreground = EditorKit.Res<Brush>("MutedText"), FontSize = 12 });
            panel.Children.Add(EditorKit.Card(block));
        }
        content.Content = panel;
    }

    private static UIElement CheckRow(HealthCheck check)
    {
        var (glyph, brush) = check.Status switch
        {
            HealthStatus.Problem => ("", EditorKit.Res<Brush>("Live")),
            HealthStatus.Warning => ("", EditorKit.Res<Brush>("Warn")),
            HealthStatus.Note => ("", EditorKit.Res<Brush>("MutedText")),
            _ => ("", EditorKit.Res<Brush>("Good")),
        };
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 4) };
        var icon = new TextBlock { Text = glyph, FontFamily = EditorKit.IconFont, Foreground = brush, Width = 24, FontSize = 14, Margin = new Thickness(0, 1, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = $"{check.Area}: {check.Summary}", TextWrapping = TextWrapping.Wrap });
        if (check.Advice is { } advice && check.Status >= HealthStatus.Note)
            text.Children.Add(new TextBlock { Text = advice, TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = EditorKit.Res<Brush>("MutedText") });
        row.Children.Add(text);
        return row;
    }

    // MARK: Logs

    private UIElement LogsPage()
    {
        var panel = new StackPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 10) };
        buttons.Children.Add(EditorKit.Button("Open the Log Folder", () => MainWindow.OpenFolder(AppPaths.LogDirectory)));
        panel.Children.Add(buttons);
        var hangs = DiagnosticsBundle.Candidates(AppPaths.LogDirectory).Where(c => c.Name.StartsWith("hang-", StringComparison.Ordinal)).OrderByDescending(c => c.Modified).ToList();
        panel.Children.Add(EditorKit.Section("TIMES IT STOPPED RESPONDING", top: 0));
        if (hangs.Count == 0) panel.Children.Add(EditorKit.Muted("None in the last two weeks.", 12));
        foreach (var hang in hangs.Take(10))
        {
            var row = new DockPanel();
            var open = EditorKit.Button("Open", () => OpenFile(hang.Path));
            DockPanel.SetDock(open, Dock.Right);
            row.Children.Add(open);
            row.Children.Add(new TextBlock { Text = $"{hang.Modified:dddd d MMMM, HH:mm:ss}  ·  {hang.Name}", VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(EditorKit.Card(row));
        }
        panel.Children.Add(EditorKit.Section("TODAY'S LOG (THE LATEST LINES)", top: 12));
        var tail = ReadTail(Log.CurrentFile, 60);
        panel.Children.Add(new TextBox
        {
            Text = SecretScrubber.ScrubText(tail, Environment.UserName), IsReadOnly = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 11, Height = 320, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        return panel;
    }

    private static string ReadTail(string path, int lines)
    {
        try
        {
            if (!File.Exists(path)) return "(nothing logged yet today)";
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var all = reader.ReadToEnd().Split('\n');
            return string.Join("\n", all.TakeLast(lines + 1)).TrimEnd();
        }
        catch (IOException ex)
        {
            return $"(couldn't read the log: {ex.Message})";
        }
    }

    private static void OpenFile(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Log.Warn($"Couldn't open {path}: {ex.Message}"); }
    }

    // MARK: Check this PC

    private UIElement CheckPage()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Check this PC", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 4) });
        panel.Children.Add(EditorKit.Muted("Runs MxU Slides' own self-test in a separate copy of the app, on a throwaway library: your songs, services and settings are never touched. "
            + "It puts test pictures on every screen and plays test sounds, so run it when nothing else is happening. A quick check takes about three minutes; the full one ten to fifteen.", 12, 10));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var quick = EditorKit.Button("Quick Check", () => _ = RunCheckAsync(quick: true), accent: true);
        var full = EditorKit.Button("Full Check", () => _ = RunCheckAsync(quick: false));
        full.Margin = new Thickness(8, 0, 0, 0);
        var stop = EditorKit.Button("Stop", () => checkCancel?.Cancel());
        stop.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(quick);
        buttons.Children.Add(full);
        buttons.Children.Add(stop);
        panel.Children.Add(buttons);
        panel.Children.Add(checkStatus);
        panel.Children.Add(checkResults);
        return panel;
    }

    private async Task RunCheckAsync(bool quick)
    {
        if (checkCancel is not null) return;
        if (PcCheckRunner.Blocked(model) is { } reason)
        {
            checkStatus.Text = reason;
            checkStatus.Foreground = EditorKit.Res<Brush>("Warn");
            return;
        }
        checkStatus.Foreground = EditorKit.Res<Brush>("MutedText");
        checkResults.Children.Clear();
        checkCancel = new CancellationTokenSource();
        try
        {
            var progress = new Progress<PcCheckRunner.Progress>(p => checkStatus.Text = p.LastLine is { } last ? $"{p.Phase}\nNow: {last}" : p.Phase);
            ShowCheckResult(await PcCheckRunner.RunAsync(quick, progress, checkCancel.Token));
        }
        catch (OperationCanceledException)
        {
            checkStatus.Text = "Stopped.";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            checkStatus.Text = $"The check couldn't run: {ex.Message}";
        }
        finally
        {
            checkCancel?.Dispose();
            checkCancel = null;
        }
    }

    /// <summary>Shows a finished check: a line per area, then every check, then where the pictures are.</summary>
    internal void ShowCheckResult(PcCheckRunner.Result result)
    {
        if (page != Page.Check) ShowPage(Page.Check);
        checkStatus.Text = !result.Finished ? "The check didn't finish. What it got through is below."
            : result.Passed ? "This PC is ready: every check passed."
            : result.Lines.Count(l => l.Result == PcCheck.Result.Fail) is var failed && failed == 1 ? "1 check failed. See the areas below." : $"{failed} checks failed. See the areas below.";
        checkStatus.Foreground = result.Passed ? EditorKit.Res<Brush>("Good") : EditorKit.Res<Brush>("Warn");
        checkResults.Children.Clear();
        foreach (var area in result.Areas)
        {
            var (glyph, brush) = area.State switch
            {
                PcCheck.AreaState.Ready => ("", EditorKit.Res<Brush>("Good")),
                PcCheck.AreaState.Problem => ("", EditorKit.Res<Brush>("Live")),
                _ => ("", EditorKit.Res<Brush>("MutedText")),
            };
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var icon = new TextBlock { Text = glyph, FontFamily = EditorKit.IconFont, Foreground = brush, Width = 24, FontSize = 14 };
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(new TextBlock { Text = $"{area.Name}: {area.Summary}", TextWrapping = TextWrapping.Wrap });
            checkResults.Children.Add(row);
        }
        var pictures = EditorKit.Button("Open the Pictures and Report", () => MainWindow.OpenFolder(result.Folder));
        pictures.HorizontalAlignment = HorizontalAlignment.Left;
        pictures.Margin = new Thickness(0, 8, 0, 8);
        checkResults.Children.Add(pictures);
        checkResults.Children.Add(EditorKit.Section("EVERY CHECK", top: 4));
        foreach (var line in result.Lines.Where(l => l.Result != PcCheck.Result.Note))
            checkResults.Children.Add(new TextBlock
            {
                Text = $"{line.Result.ToString().ToUpperInvariant(),-5}  {line.Text}", FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Foreground = line.Result switch { PcCheck.Result.Fail => EditorKit.Res<Brush>("Live"), PcCheck.Result.Skip => EditorKit.Res<Brush>("MutedText"), _ => EditorKit.Res<Brush>("Text") },
            });
    }
}
