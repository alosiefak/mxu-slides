using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Follow;
using MxU.Slides.Windows.Follow;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views.Follow;

/// <summary>
/// Settings › Backup (the Mac's Settings › Backup): how this computer and its backup stand.
/// <list type="bullet">
/// <item><b>On a backup</b>: what it follows and how (show the same thing live, or stay ready with
/// clear screens; take over after 3 seconds, or wait for Take Over), Take Over, Hand Back and Stop
/// Backing Up.</item>
/// <item><b>On a booth</b>: its backups (connected or not, each with Forget), Set Up a Backup
/// (the code a backup types, and the API it needs on), and Make This the Backup to follow
/// another computer instead.</item>
/// </list>
/// Placed by one line of SettingsWindow.xaml; it finds the app's view model itself and every
/// change takes effect at once (nothing waits for Save).
/// </summary>
public sealed class BackupSettingsSection : UserControl
{
    private readonly StackPanel body = new();
    private MainViewModel? model;

    public BackupSettingsSection()
    {
        var panel = new StackPanel();
        panel.Children.Add(EditorKit.Section("BACKUP", 18));
        panel.Children.Add(body);
        Content = panel;
        Loaded += (_, _) =>
        {
            if (Application.Current?.MainWindow?.DataContext is not MainViewModel found) return;
            model = found;
            model.Follow.Changed += Draw;
            Draw();
        };
        Unloaded += (_, _) =>
        {
            if (model is not null) model.Follow.Changed -= Draw;
        };
    }

    /// <summary>For the self-test: the code this computer shows a backup.</summary>
    public string? ShownCode { get; private set; }

    private void Draw()
    {
        if (model is null) return;
        body.Children.Clear();
        ShownCode = null;
        var follow = model.Follow;
        if (follow.Paired is { } pairing) DrawBackup(follow, pairing);
        else DrawBooth(follow);
    }

    private void DrawBackup(FollowController follow, FollowController.Pairing pairing)
    {
        var line = FollowLogic.BackupSettingsLine(pairing.Name, follow.State, follow.InCharge, follow.StandingBy,
            pairing.Automatic ?? true, follow.CopiedItems, follow.BoothQuit);
        body.Children.Add(Status(follow.InCharge ? Color.FromRgb(0x3B, 0x82, 0xF6) : FollowBanner.Dot(follow), line.Title, line.Detail));
        if (follow.CopyProblem is { } copy) body.Children.Add(EditorKit.Muted(copy));
        body.Children.Add(EditorKit.Row($"While {pairing.Name} runs", EditorKit.Picker(
            [new EditorChoice("live", "Show the same thing live"), new EditorChoice("ready", "Stay ready, show nothing")],
            pairing.Mirrors == false ? "ready" : "live", id => _ = follow.SetMirrorsAsync(id == "live")), labelWidth: 170));
        body.Children.Add(EditorKit.Row($"If {pairing.Name} stops", EditorKit.Picker(
            [new EditorChoice("auto", "Take over after 3 seconds"), new EditorChoice("wait", "Wait for Take Over")],
            pairing.Automatic == false ? "wait" : "auto", id => follow.SetAutomatic(id == "auto")), labelWidth: 170));
        var buttons = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        if (follow.InCharge) buttons.Children.Add(Spaced(EditorKit.Button("Hand Back", () => _ = follow.HandBackAsync())));
        else
        {
            if (follow.State is FollowLogic.State.GaveUp) buttons.Children.Add(Spaced(EditorKit.Button("Try Again", follow.TryAgain)));
            buttons.Children.Add(Spaced(EditorKit.Button("Take Over", () => _ = follow.TakeOverAsync())));
        }
        buttons.Children.Add(Spaced(EditorKit.Button("Stop Backing Up", () => _ = follow.UnpairAsync())));
        body.Children.Add(buttons);
    }

    private void DrawBooth(FollowController follow)
    {
        var line = FollowLogic.BoothSettingsLine(follow.Backups, follow.ConnectedBackups, follow.SteppedDownTo?.BackupName);
        var dot = follow.SteppedDownTo is not null ? Colors.Orange : follow.ConnectedBackups.Count == 0 ? Colors.Gray : Color.FromRgb(0x22, 0xC5, 0x5E);
        body.Children.Add(Status(dot, line.Title, line.Detail));
        if (follow.SteppedDownTo is not null) body.Children.Add(Spaced(EditorKit.Button("Take Back", () => follow.TakeBack())));

        foreach (var name in follow.Backups)
        {
            var on = follow.ConnectedBackups.Contains(name);
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 4) };
            var forget = EditorKit.Button($"Forget {name}…", () =>
            {
                if (MessageBox.Show(Window.GetWindow(this)!, "It stops following this computer until it pairs again with this computer's code.",
                        $"Forget {name}?", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                    follow.Forget(name);
            });
            DockPanel.SetDock(forget, Dock.Right);
            row.Children.Add(forget);
            row.Children.Add(new TextBlock
            {
                Text = FollowLogic.BoothBackupLine(name, on, follow.SteppedDownTo?.BackupName == name),
                TextWrapping = TextWrapping.Wrap, FontSize = 11, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
            });
            body.Children.Add(row);
        }

        // Set up a backup: the steps, the code, and the API it needs.
        var steps = new StackPanel();
        steps.Children.Add(new TextBlock { Text = "Set up a backup", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) });
        steps.Children.Add(EditorKit.Muted("A second computer on this network (a PC or a Mac) follows this one and can take over if it stops.", 11, 4));
        steps.Children.Add(EditorKit.Muted("1. On the backup computer, open MxU Slides › Settings › Backup.", 11, 1));
        steps.Children.Add(EditorKit.Muted($"2. Click Make This the Backup… and pick {model!.Computer.Name}.", 11, 1));
        var code = follow.Code.Current ?? follow.Code.Refresh();
        ShownCode = code;
        var codeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 4) };
        codeRow.Children.Add(new TextBlock { Text = "3. Enter this code:  ", FontSize = 11, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center });
        codeRow.Children.Add(new TextBlock { Text = code, FontFamily = new FontFamily("Consolas"), FontSize = 22, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        codeRow.Children.Add(EditorKit.Button("New Code", () =>
        {
            follow.Code.Refresh();
            Draw();
        }));
        steps.Children.Add(codeRow);
        if (!model.Settings.LocalApiEnabled)
        {
            var api = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var turnOn = EditorKit.Button("Turn On the API", () =>
            {
                model.Settings.LocalApiEnabled = true;
                model.Settings.Save();
                model.ApplyApiSettings();
                Draw();
            }, accent: true);
            DockPanel.SetDock(turnOn, Dock.Right);
            api.Children.Add(turnOn);
            api.Children.Add(EditorKit.Muted("The backup reaches this computer through its API (Network & API above), which is off.", 11, 0));
            steps.Children.Add(api);
        }
        // Wrapped, not trimmed: this is the line that explains the firewall prompt, so all of it must show.
        var firewall = EditorKit.Faint(
            "The first time, Windows may ask whether MxU Slides can use the network. Allow it on private networks: the backup "
            + "connects to this computer's API port, and both computers find each other on the network (DNS-SD, as Bonjour on a Mac).");
        firewall.TextWrapping = TextWrapping.Wrap;
        firewall.TextTrimming = TextTrimming.None;
        steps.Children.Add(firewall);
        body.Children.Add(EditorKit.Card(steps));

        var become = EditorKit.Button("Make This the Backup…", () => PairBackupWindow.Open(Window.GetWindow(this)!, model));
        become.HorizontalAlignment = HorizontalAlignment.Left;
        body.Children.Add(become);
    }

    private static UIElement Status(Color color, string title, string detail)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var dot = new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, Fill = new SolidColorBrush(color), Margin = new Thickness(0, 4, 8, 0), VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(dot, Dock.Left);
        row.Children.Add(dot);
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = detail, FontSize = 11, Opacity = 0.75, TextWrapping = TextWrapping.Wrap });
        row.Children.Add(text);
        return row;
    }

    private static T Spaced<T>(T element) where T : FrameworkElement
    {
        element.Margin = new Thickness(0, 0, 8, 4);
        return element;
    }
}
