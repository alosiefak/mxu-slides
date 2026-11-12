using System.Windows;
using System.Windows.Controls;
using MxU.Slides.Core.Library;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// Settings › Backups and diagnostics: how backups are made and the last one, with Backups…, Back
/// Up Now… and Restore…; Diagnostics… and Check this PC…; the welcome guide again. Placed in the
/// Settings window by one line of its XAML, like the About section.
/// </summary>
public sealed class LibraryCareSection : UserControl
{
    public LibraryCareSection()
    {
        Loaded += (_, _) => Build();
    }

    private void Build()
    {
        if ((DataContext ?? Application.Current.MainWindow?.DataContext) is not MainViewModel model || Window.GetWindow(this) is not { } owner) return;
        var main = owner.Owner as MainWindow ?? Application.Current.MainWindow as MainWindow;
        var panel = new StackPanel();
        panel.Children.Add(EditorKit.Section("BACKUPS & DIAGNOSTICS", top: 18));
        // The Mac's caption: how often, where, how many are kept, and the last one (or why it failed).
        panel.Children.Add(EditorKit.Muted(model.LastBackupLine + (model.Settings.AutoBackupIncludesMedia ? " With the media files." : ""), 12, 6));
        var backups = new WrapPanel();
        void Add(Panel to, string title, Action click, bool accent = false)
        {
            var button = EditorKit.Button(title, click, accent: accent);
            button.Margin = new Thickness(0, 0, 8, 6);
            to.Children.Add(button);
        }
        Add(backups, "Backups…", () => BackupWindow.Open(owner, model));
        Add(backups, "Restore from a File…", () =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Choose a library backup to restore", Filter = $"MxU Slides library backup (*.{LibraryBackup.FileExtension})|*.{LibraryBackup.FileExtension}" };
            if (dialog.ShowDialog(owner) == true) _ = RestoreWindow.OpenAsync(owner, model, dialog.FileName);
        });
        panel.Children.Add(backups);
        var help = new WrapPanel();
        Add(help, "Diagnostics…", () => DiagnosticsWindow.Open(owner, model));
        Add(help, "Check This PC…", () => DiagnosticsWindow.Open(owner, model, DiagnosticsWindow.Page.Check));
        Add(help, "Report a Problem…", () => ReportProblemWindow.Open(owner, model));
        if (main is not null) Add(help, "Welcome Guide…", () => OnboardingWindow.Open(main, model));
        panel.Children.Add(help);
        Content = panel;
    }
}
