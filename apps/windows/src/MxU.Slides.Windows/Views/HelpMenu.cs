using System.Windows;
using System.Windows.Controls;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The Help button's menu at the top right of the main window: the welcome guide again, the keys,
/// Diagnostics (is this PC ready?), Check this PC…, Report a Problem…, backups and the log. The
/// Mac's Help menu has Show Welcome, Restore Getting Started and Report a Problem… (HelpCommands).
/// </summary>
internal static class HelpMenu
{
    /// <summary>The keys a new operator needs first (MainWindow.xaml.cs, OnPreviewKeyDown, has them all).</summary>
    public static IReadOnlyList<(string Keys, string What)> KeysToKnow { get; } =
    [
        ("→  Space  Enter", "Next (plays the next build first)"),
        ("←", "Previous"),
        ("Alt+→", "The next slide, skipping its builds"),
        ("A S D F, C, B", "Verses 1–4, Chorus, Bridge"),
        ("A number, then Enter", "Go to that slide"),
        ("F1", "Clear everything"),
        ("F2 … F6", "Clear the slide, media, overlays, music, alerts"),
        ("Ctrl+F", "Search the library"),
        ("Ctrl+B", "The Bible"),
        ("Ctrl+N", "A new song from lyrics"),
        ("Ctrl+I", "Import"),
        ("Ctrl+E", "Edit mode on or off"),
    ];

    public static void Open(MainWindow window, MainViewModel model, FrameworkElement under)
    {
        var menu = new ContextMenu();
        menu.Items.Add(EditorKit.Command("Welcome Guide…", () => OnboardingWindow.Open(window, model)));
        menu.Items.Add(EditorKit.Command("Keys…", () => ShowKeys(window)));
        menu.Items.Add(EditorKit.Separator());
        menu.Items.Add(EditorKit.Command("Diagnostics…", () => DiagnosticsWindow.Open(window, model)));
        menu.Items.Add(EditorKit.Command("Check This PC…", () => DiagnosticsWindow.Open(window, model, DiagnosticsWindow.Page.Check)));
        menu.Items.Add(EditorKit.Command("Report a Problem…", () => ReportProblemWindow.Open(window, model)));
        menu.Items.Add(EditorKit.Separator());
        menu.Items.Add(EditorKit.Command("Backups…", () => BackupWindow.Open(window, model)));
        menu.Items.Add(EditorKit.Command("Open the Log Folder", () => MainWindow.OpenFolder(AppPaths.LogDirectory)));
        EditorKit.Open(menu, under);
    }

    public static void ShowKeys(Window owner)
    {
        var dialog = new ChoiceWindow(owner, "Keys", "The keys to know", "In the main window, when you're not typing in a box:");
        var list = new StackPanel();
        foreach (var (keys, what) in KeysToKnow)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var key = new TextBlock { Text = keys, Width = 160, FontWeight = FontWeights.SemiBold };
            DockPanel.SetDock(key, Dock.Left);
            row.Children.Add(key);
            row.Children.Add(new TextBlock { Text = what, Foreground = EditorKit.Res<System.Windows.Media.Brush>("MutedText") });
            list.Children.Add(row);
        }
        dialog.AddBody(list);
        dialog.AddButton("OK", isDefault: true, isCancel: true, accent: true);
        dialog.Ask();
    }
}
