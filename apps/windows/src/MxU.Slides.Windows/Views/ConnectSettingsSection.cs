using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MxU.Slides.Core.PlanningCenter;
using MxU.Slides.LocalApi;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// Settings › Planning Center and Browser remote. Placed in the Settings window by one line of its
/// XAML; it finds the app's view model itself, and everything here takes effect at once (there's
/// nothing for the window's Save button to do).
/// <list type="bullet">
/// <item><b>Planning Center:</b> paste the church's Personal Access Token (application id and
/// secret), press Connect. It's checked against Planning Center before it's kept (encrypted, on
/// this PC). Import a plan from here or from the service menu.</item>
/// <item><b>Browser remote:</b> the address and a QR code to open it, and Pair a phone, which shows
/// a 6-digit code for the phone to type.</item>
/// </list>
/// Windows only.
/// </summary>
public sealed class ConnectSettingsSection : UserControl
{
    /// <summary>Where a church makes a Personal Access Token.</summary>
    public const string TokenPage = "https://api.planningcenteronline.com/oauth/applications";

    private readonly StackPanel planningCenter = new();
    private readonly StackPanel remote = new();
    private readonly DispatcherTimer countdown = new() { Interval = TimeSpan.FromSeconds(1) };
    private MainViewModel? model;

    // The pairing panel's parts, kept to update the countdown in place.
    private TextBlock? codeText;
    private TextBlock? codeCaption;
    private string? pairedLine;

    public ConnectSettingsSection()
    {
        var panel = new StackPanel();
        panel.Children.Add(EditorKit.Section("PLANNING CENTER", 18));
        panel.Children.Add(planningCenter);
        panel.Children.Add(EditorKit.Section("BROWSER REMOTE", 18));
        panel.Children.Add(remote);
        Content = panel;

        countdown.Tick += (_, _) => UpdateCode();
        Loaded += (_, _) =>
        {
            if (Application.Current?.MainWindow?.DataContext is not MainViewModel found) return;
            model = found;
            model.PlanningCenterChanged += DrawPlanningCenter;
            model.RemotePaired += OnPaired;
            DrawPlanningCenter();
            DrawRemote();
        };
        Unloaded += (_, _) =>
        {
            countdown.Stop();
            if (model is null) return;
            model.PlanningCenterChanged -= DrawPlanningCenter;
            model.RemotePaired -= OnPaired;
        };
    }

    // MARK: Planning Center

    private void DrawPlanningCenter()
    {
        if (model is null) return;
        planningCenter.Children.Clear();
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        planningCenter.Children.Add(status);

        if (model.PlanningCenterConnected)
        {
            status.Text = "Connected. Plans can be imported as services, and imported services updated.";
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            buttons.Children.Add(EditorKit.Button("Import a plan…", () => PlanningCenterWindow.Open(Window.GetWindow(this), model), accent: true));
            buttons.Children.Add(EditorKit.Button("Disconnect", () =>
            {
                if (MessageBox.Show(Window.GetWindow(this), "Remove the Planning Center token from this computer? Services already imported stay.",
                        "MxU Slides", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                    model.DisconnectPlanningCenter();
            }));
            planningCenter.Children.Add(buttons);
            planningCenter.Children.Add(EditorKit.Muted("Import from the service menu (⋯ › Import from Planning Center…) too. Importing a plan again updates its service and keeps your changes."));
            return;
        }

        status.Text = "Bring Planning Center Services plans in as services: songs, headers, media and items, with their lengths.";
        var id = new TextBox { Name = "PcoApplicationId", ToolTip = "The Application ID of a Personal Access Token" };
        var secret = new PasswordBox { Name = "PcoSecret", ToolTip = "The Secret of the same token" };
        planningCenter.Children.Add(EditorKit.Row("Application ID", id, 110));
        planningCenter.Children.Add(EditorKit.Row("Secret", secret, 110));
        var result = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 4, 0, 4) };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        Button? connect = null;
        connect = EditorKit.Button("Connect", async () =>
        {
            connect!.IsEnabled = false;
            result.Foreground = EditorKit.Res<Brush>("MutedText");
            result.Text = "Checking with Planning Center…";
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                var church = await model.ConnectPlanningCenterAsync(id.Text, secret.Password, limit.Token);
                secret.Clear();
                Notice($"Connected to {church}.");
            }
            catch (PlanningCenterException e)
            {
                result.Foreground = EditorKit.Res<Brush>("Warn");
                result.Text = e.Message;
                connect.IsEnabled = true;
            }
            catch (OperationCanceledException)
            {
                result.Foreground = EditorKit.Res<Brush>("Warn");
                result.Text = "Planning Center didn't answer. Check the internet connection and try again.";
                connect.IsEnabled = true;
            }
        }, "Checks the token with Planning Center, then keeps it on this computer", accent: true);
        row.Children.Add(connect);
        row.Children.Add(EditorKit.Button("Make a token…", () => OpenInBrowser(TokenPage), "Opens Planning Center's developer page in your browser"));
        planningCenter.Children.Add(row);
        planningCenter.Children.Add(result);
        planningCenter.Children.Add(EditorKit.Muted(
            "At api.planningcenteronline.com, sign in and make a Personal Access Token, then paste its Application ID and Secret here. "
            + "They're kept encrypted for your Windows account on this computer: never in the library, a backup or a log."));
    }

    private void Notice(string text)
    {
        if (model is not null) model.Notice = $"Planning Center: {text}";
    }

    // MARK: Browser remote

    private void DrawRemote()
    {
        if (model is null) return;
        remote.Children.Clear();
        if (!model.RemoteAvailable)
        {
            remote.Children.Add(EditorKit.Muted(
                "Run the show from any phone or tablet's web browser, with nothing to install, and show a stage view on a spare screen. "
                + "Tick “Let phones, tablets and other apps on this network control the show” above, press Save, then open Settings again to pair a phone.", 12));
            return;
        }

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var qr = new Image
        {
            Source = QrCode.Image(model.RemoteAddress), Width = 132, Height = 132, Margin = new Thickness(0, 0, 14, 0),
            ToolTip = "Point a phone's camera here to open the remote", VerticalAlignment = VerticalAlignment.Top,
        };
        RenderOptions.SetBitmapScalingMode(qr, BitmapScalingMode.NearestNeighbor);
        grid.Children.Add(qr);
        var words = new StackPanel();
        Grid.SetColumn(words, 1);
        words.Children.Add(EditorKit.Muted("On a phone or tablet on the same Wi-Fi, scan the code or open:", 12, 2));
        words.Children.Add(Address(model.RemoteAddress));
        words.Children.Add(EditorKit.Muted("For a confidence monitor or the pastor's tablet, the stage view:", 12, 2));
        words.Children.Add(Address(model.StageViewAddress));
        grid.Children.Add(words);
        remote.Children.Add(grid);

        var scope = new ComboBox { Width = 170, Margin = new Thickness(0, 0, 6, 0), ToolTip = "What the paired device may do" };
        scope.Items.Add(new ComboBoxItem { Content = "Control the show", Tag = ApiScope.Control });
        scope.Items.Add(new ComboBoxItem { Content = "View only (stage view)", Tag = ApiScope.View });
        scope.SelectedIndex = 0;
        var pair = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        pair.Children.Add(scope);
        pair.Children.Add(EditorKit.Button("Pair a phone", () =>
        {
            var chosen = (scope.SelectedItem as ComboBoxItem)?.Tag as ApiScope? ?? ApiScope.Control;
            pairedLine = null;
            if (model.BeginRemotePairing(chosen) is null) return;
            DrawCode();
        }, "Shows a code to type on the phone", accent: true));
        remote.Children.Add(pair);
        codeText = new TextBlock { FontSize = 30, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Consolas"), Name = "PairingCode" };
        codeCaption = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = EditorKit.Res<Brush>("MutedText") };
        remote.Children.Add(codeText);
        remote.Children.Add(codeCaption);
        DrawCode();
        remote.Children.Add(EditorKit.Muted(
            "The phone keeps its own key, made by pairing and listed under Manage keys, where it can be revoked. "
            + "Neither the address nor the QR code holds a key.", 11));
    }

    private static TextBox Address(string text) => new()
    {
        Text = text, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
        FontSize = 14, Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(0),
    };

    private void DrawCode()
    {
        UpdateCode();
        if (model?.RemotePairingCode is not null) countdown.Start();
    }

    private void UpdateCode()
    {
        if (codeText is null || codeCaption is null || model is null) return;
        if (model.RemotePairingCode is { } code)
        {
            var left = code.Expires - DateTimeOffset.UtcNow;
            codeText.Text = $"{code.Code[..3]} {code.Code[3..]}";
            codeText.Visibility = Visibility.Visible;
            codeCaption.Text = $"Type this on the phone ({(code.Scope == ApiScope.View ? "view only" : "control")}). It works once, for the next {left:m\\:ss}.";
            return;
        }
        countdown.Stop();
        codeText.Text = "";
        codeText.Visibility = Visibility.Collapsed;
        codeCaption.Text = pairedLine ?? "";
    }

    private void OnPaired(ApiToken token)
    {
        pairedLine = $"Paired “{token.Name}” ({token.Scope.Raw()}).";
        UpdateCode();
    }

    private static void OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show($"Couldn't open the browser. Go to {url}", "MxU Slides");
        }
    }
}
