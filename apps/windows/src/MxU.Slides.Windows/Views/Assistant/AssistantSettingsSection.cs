using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MxU.Slides.Assistant;
using MxU.Slides.Core.Automation;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views.Assistant;

/// <summary>
/// Settings › Assistant. Placed in the Settings window by one line of its XAML, like the Planning
/// Center section; it finds the app's view model itself and everything takes effect at once.
/// <list type="bullet">
/// <item><b>The key:</b> paste the church's Anthropic API key and press Check and save. It's
/// checked with Anthropic first (a call that costs nothing), then kept encrypted on this PC.</item>
/// <item><b>The switch:</b> off means nothing is ever sent, whatever else is set.</item>
/// <item><b>The model</b>, what's been spent this month, and the log of everything sent and received.</item>
/// </list>
/// Windows only.
/// </summary>
public sealed class AssistantSettingsSection : UserControl
{
    /// <summary>Where a church makes an API key.</summary>
    public const string KeyPage = "https://console.anthropic.com/settings/keys";

    private readonly StackPanel body = new();
    private MainViewModel? model;

    public AssistantSettingsSection()
    {
        var panel = new StackPanel();
        panel.Children.Add(EditorKit.Section("ASSISTANT (AI)", 18));
        panel.Children.Add(body);
        Content = panel;
        Loaded += (_, _) =>
        {
            if (Application.Current?.MainWindow?.DataContext is not MainViewModel found) return;
            model = found;
            model.AssistantChanged += Draw;
            model.Assistant.Log.Changed += DrawLater;
            Draw();
        };
        Unloaded += (_, _) =>
        {
            if (model is null) return;
            model.AssistantChanged -= Draw;
            model.Assistant.Log.Changed -= DrawLater;
        };
    }

    // The log changes on the assistant's thread. Only the saved-key view shows anything from it
    // (this month's use); the paste-a-key form is left alone, so its "not saved" message stays.
    private void DrawLater() => Dispatcher.BeginInvoke(() =>
    {
        if (KeyBox is null) Draw();
    });

    /// <summary>The key box (the self-test types into it) and its button; null once a key is saved.</summary>
    public PasswordBox? KeyBox { get; private set; }

    public Button? SaveButton { get; private set; }

    private void Draw()
    {
        if (model is null) return;
        body.Children.Clear();
        (KeyBox, SaveButton) = (null, null);
        var assistant = model.Assistant;
        if (assistant.Vault.Load() is not { } key)
        {
            DrawNoKey();
            return;
        }

        var keyRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var remove = EditorKit.Button("Remove key", () =>
        {
            if (MessageBox.Show(Window.GetWindow(this), "Remove the API key from this computer? The assistant's features go quiet; nothing else changes.",
                    "MxU Slides", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                model.RemoveAssistantKey();
        });
        remove.Margin = new Thickness(0);
        DockPanel.SetDock(remove, Dock.Right);
        keyRow.Children.Add(remove);
        keyRow.Children.Add(new TextBlock { Text = $"API key: {AssistantKeyVault.Masked(key)}  (kept encrypted on this computer)", VerticalAlignment = VerticalAlignment.Center });
        body.Children.Add(keyRow);

        var on = new CheckBox
        {
            Content = "Use the assistant on this computer", IsChecked = model.Settings.AssistantEnabled, Margin = new Thickness(0, 0, 0, 6),
            ToolTip = "Off: nothing is ever sent to Anthropic, and the AI buttons are greyed. The rest of the app is unchanged.",
        };
        on.Click += (_, _) => model.SetAssistant(enabled: on.IsChecked == true);
        body.Children.Add(on);

        var models = EditorKit.Picker(
            [.. AssistantModels.All.Select(m => new EditorChoice(m.Id, string.Create(CultureInfo.InvariantCulture, $"{m.Name}: ${m.Input:0.##} in, ${m.Output:0.##} out per million tokens")))],
            assistant.Model.Id, id => model.SetAssistant(model: id), minWidth: 420);
        body.Children.Add(EditorKit.Row("Model", models, 60));

        var (usage, cost) = assistant.Log.Total(new DateTimeOffset(DateTime.Now.Year, DateTime.Now.Month, 1, 0, 0, 0, DateTimeOffset.Now.Offset));
        var spent = new DockPanel { Margin = new Thickness(0, 2, 0, 6) };
        var open = EditorKit.Button("What was sent and received…", () => AssistantLogWindow.Open(Window.GetWindow(this)!, model),
            "Every request to Anthropic and its answer, word for word");
        open.Margin = new Thickness(0);
        DockPanel.SetDock(open, Dock.Right);
        spent.Children.Add(open);
        spent.Children.Add(new TextBlock
        {
            // Checking a key sends no words and costs nothing, so it isn't counted.
            Text = usage.Total == 0
                ? "Nothing sent this month."
                : string.Create(CultureInfo.InvariantCulture, $"This month: {usage.Total:N0} tokens, {AssistantCost.Words(cost)}."),
            VerticalAlignment = VerticalAlignment.Center, Name = "AssistantSpent",
        });
        body.Children.Add(spent);
        body.Children.Add(EditorKit.Muted(
            "The assistant suggests; a person decides. Nothing it makes reaches a screen without a click or the Enter key. "
            + "Verse text always comes from the Bible built into this app and song words from what you paste, never from the AI's memory. "
            + "Prayer-request lines are held back before anything is sent, and sermon sound never leaves this computer. "
            + "Costs shown are estimates from Anthropic's list prices; the bill is on your Anthropic account."));
    }

    private void DrawNoKey()
    {
        if (model is null) return;
        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
            Text = "Optional. With the church's Anthropic API key, the assistant can suggest slides from sermon notes, tidy pasted lyrics, "
                + "and name a passage a preacher only alludes to. Without one, the app works exactly as before.",
        });
        var key = new PasswordBox { Name = "AssistantKey", ToolTip = "Paste the API key (it starts sk-ant-)" };
        KeyBox = key;
        body.Children.Add(EditorKit.Row("API key", key, 60));
        var result = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 4, 0, 4) };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        Button? save = null;
        save = EditorKit.Button("Check and save", async () =>
        {
            if (key.Password.Trim().Length == 0) return;
            save!.IsEnabled = false;
            result.Foreground = EditorKit.Res<Brush>("MutedText");
            result.Text = "Checking the key with Anthropic…";
            try
            {
                await model.SaveAssistantKeyAsync(key.Password, CancellationToken.None);
                key.Clear();
                model.Notice = "Assistant: the key works and is saved on this computer.";
            }
            catch (AssistantException e)
            {
                result.Foreground = EditorKit.Res<Brush>("Warn");
                result.Text = $"{e.Message} The key was not saved.";
                save.IsEnabled = true;
            }
        }, "Checks the key with Anthropic (a call that costs nothing), then keeps it on this computer", accent: true);
        SaveButton = save;
        row.Children.Add(save);
        row.Children.Add(EditorKit.Button("Get a key…", OpenKeyPage, "Opens Anthropic's console in your browser"));
        body.Children.Add(row);
        body.Children.Add(result);
        body.Children.Add(EditorKit.Muted(
            "The key is kept encrypted for your Windows account on this computer: never in the library, a backup, a log or a web address. "
            + "Using the assistant costs money on that Anthropic account; each job shows what it used."));
    }

    private static void OpenKeyPage()
    {
        try { Process.Start(new ProcessStartInfo(KeyPage) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }
}

/// <summary>
/// The log of everything sent to Anthropic and everything that came back, newest first: pick a
/// job on the left to read its words on the right. It's the same text as the file
/// (<c>Assistant\log.jsonl</c> in the local data folder).
/// </summary>
public static class AssistantLogWindow
{
    public static Window Open(Window owner, MainViewModel model)
    {
        var window = new Window();
        EditorKit.Dress(window, owner, "Assistant: what was sent and received", 980, 640);
        var log = model.Assistant.Log;
        var list = new ListBox { Width = 300, Margin = new Thickness(0, 0, 10, 0) };
        var detail = new TextBox
        {
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, Padding = new Thickness(10),
        };
        var entries = new List<AssistantLogEntry>();
        void Fill()
        {
            entries = [.. log.Entries.Reverse()];
            list.Items.Clear();
            foreach (var entry in entries)
            {
                var priced = AssistantModels.Find(entry.Model);
                var row = new StackPanel { Margin = new Thickness(2, 3, 2, 3) };
                row.Children.Add(new TextBlock { Text = $"{entry.At.LocalDateTime:d MMM HH:mm:ss}  {entry.Feature}", FontWeight = FontWeights.SemiBold });
                row.Children.Add(new TextBlock
                {
                    Text = entry.Problem ?? (entry.Usage.Total == 0 ? "no words sent" : entry.Usage.Describe(priced)), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = EditorKit.Res<Brush>(entry.Problem is null ? "MutedText" : "Warn"),
                });
                list.Items.Add(row);
            }
            if (entries.Count == 0) detail.Text = "Nothing has been sent from this computer.";
            else list.SelectedIndex = 0;
        }
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedIndex < 0 || list.SelectedIndex >= entries.Count) return;
            var entry = entries[list.SelectedIndex];
            detail.Text = $"SENT TO ANTHROPIC ({entry.Model})\n\n{entry.Sent}"
                + (entry.Tools.Count == 0 ? "" : $"\n\nLOOKED UP ON THIS PC FOR THE MODEL\n\n{string.Join("\n", entry.Tools)}")
                + $"\n\nRECEIVED\n\n{(entry.Received.Length == 0 ? entry.Problem ?? "(nothing)" : entry.Received)}";
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(EditorKit.Button("Show the file", () =>
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{log.FilePath}\"") { UseShellExecute = true }); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }));
        buttons.Children.Add(EditorKit.Button("Clear the log", () =>
        {
            if (MessageBox.Show(window, "Delete the log of what was sent and received?", "MxU Slides", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            log.Clear();
            Fill();
        }));
        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        DockPanel.SetDock(list, Dock.Left);
        root.Children.Add(list);
        root.Children.Add(detail);
        window.Content = new Border { Child = root, Padding = new Thickness(16, 14, 16, 12) };
        Fill();
        window.Show();
        return window;
    }
}
