using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MxU.Slides.Core.Library;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// Settings › About: the app's version and the credits for the fonts it ships. The fonts' licence
/// (the SIL Open Font License) asks that their copyright notices and the licence go with every
/// copy, where people can read them; this is that place, and <see cref="FontCreditsWindow"/> shows
/// the full text. Placed in the Settings window by one line of its XAML.
/// </summary>
public sealed class AboutSection : UserControl
{
    public AboutSection()
    {
        var panel = new StackPanel();
        panel.Children.Add(EditorKit.Section("ABOUT", top: 18));
        var version = typeof(AboutSection).Assembly.GetName().Version;
        panel.Children.Add(new TextBlock
        {
            Text = $"MxU Slides for Windows {version?.ToString(3)}", Foreground = EditorKit.Res<Brush>("Text"), Margin = new Thickness(0, 0, 0, 6),
        });
        panel.Children.Add(EditorKit.Muted("Bundled fonts, used by the starter looks, lower thirds and countdowns:"));
        foreach (var credit in BundledFonts.Credits(BundledFonts.ShippedFolder))
        {
            panel.Children.Add(new TextBlock
            {
                Text = credit.Family, FontFamily = Rendering.FontMap.Resolve(credit.Family).Family, FontSize = 15,
                Foreground = EditorKit.Res<Brush>("Text"), Margin = new Thickness(0, 4, 0, 0),
            });
            panel.Children.Add(EditorKit.Muted(credit.Copyright, size: 10, bottom: 2));
        }
        var licences = EditorKit.Button("Font licences…", () => FontCreditsWindow.Open(Window.GetWindow(this)),
            "Each font's copyright notice and the full SIL Open Font License");
        licences.HorizontalAlignment = HorizontalAlignment.Left;
        licences.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(licences);
        Content = panel;
    }
}

/// <summary>The fonts' credits and licence in full (FONT-NOTICES.txt beside the app, or made from the fonts if it's missing).</summary>
public sealed class FontCreditsWindow : Window
{
    private FontCreditsWindow(string text)
    {
        Title = "Font licences";
        Width = 720;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = EditorKit.Res<Brush>("BasePlane");
        var box = new TextBox
        {
            Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0), Padding = new Thickness(14),
            Background = EditorKit.Res<Brush>("Card"), Foreground = EditorKit.Res<Brush>("Text"),
        };
        var done = EditorKit.Button("Done", Close, accent: true);
        done.IsDefault = done.IsCancel = true;
        done.HorizontalAlignment = HorizontalAlignment.Right;
        done.Margin = new Thickness(0, 10, 0, 0);
        done.MinWidth = 90;
        var layout = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(done, Dock.Bottom);
        layout.Children.Add(done);
        layout.Children.Add(box);
        Content = layout;
    }

    /// <summary>The notice text: the shipped file, or the same notice made afresh from the font files.</summary>
    public static string NoticeText()
    {
        var folder = BundledFonts.ShippedFolder;
        var file = Path.Combine(folder, BundledFonts.NoticeFileName);
        try
        {
            if (File.Exists(file)) return File.ReadAllText(file);
            var licence = Path.Combine(folder, BundledFonts.LicenceFileName);
            return BundledFonts.Notice(BundledFonts.Credits(folder), File.Exists(licence) ? File.ReadAllText(licence) : "SIL Open Font License, Version 1.1: https://openfontlicense.org");
        }
        catch (IOException ex)
        {
            return $"Couldn't read the font licences: {ex.Message}";
        }
    }

    public static FontCreditsWindow Open(Window? owner)
    {
        var window = new FontCreditsWindow(NoticeText()) { Owner = owner };
        window.Show();
        return window;
    }
}
