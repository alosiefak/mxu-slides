using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.Rendering;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The questions moving media and presentations asks, worded as the Mac words them:
/// "already in your library" (Keep Both / Replace / Skip), the duplicate-media prompt (Use
/// Existing / Add as New Copy), "Replace everywhere?", "This file is already in your library"
/// (Replace Anyway), and "Delete? Used in 3 places". Each is a small window in the app's dark
/// style (<see cref="ChoiceWindow"/>), built in code like the other dialogs. Ports of
/// Sources/PresentationFileImport.swift (askPresentationConflict), MediaImportPrompt.swift,
/// ReplaceMediaFileFlow.swift and LibraryView.swift's delete confirmation.
/// </summary>
internal static class LibraryDialogs
{
    /// <summary>Called with each dialog just before it shows: the self-test takes its picture and answers it.</summary>
    internal static Action<ChoiceWindow>? Showing { get; set; }

    /// <summary>Over whichever window the operator is in (a dialog can come up during an import).</summary>
    private static Window? Owner =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w is not ChoiceWindow) ?? Application.Current?.MainWindow;

    public static SlidesPresentationFile.Conflict? AskPresentationConflict(string name)
    {
        var dialog = new ChoiceWindow(Owner, "Import Presentation", $"“{name}” is already in your library",
            "Replace it with the imported copy, or keep both?");
        dialog.AddButton("Skip", isCancel: true);
        dialog.AddButton("Replace");
        dialog.AddButton("Keep Both", isDefault: true, accent: true);
        return dialog.Ask() switch
        {
            "Keep Both" => SlidesPresentationFile.Conflict.KeepBoth,
            "Replace" => SlidesPresentationFile.Conflict.Replace,
            _ => null,
        };
    }

    public static MediaImportAnswer AskMediaImportConflict(MediaImportQuestion question, MediaHub media)
    {
        var incoming = Path.GetFileName(question.IncomingPath);
        var dialog = new ChoiceWindow(Owner, "Import Media",
            question.SameFile ? $"“{incoming}” is already in your library" : $"“{question.Existing.Name}” is already in your library",
            question.SameFile
                ? $"It's the same file as “{question.Existing.Name}”. Use that item, or add another copy as “{question.CopyName}”."
                : $"A different file has this name in your library. Use the one you have, or add the new file as “{question.CopyName}”.");
        var tiles = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        tiles.Children.Add(Tile(question.Existing.Name, "In your library", LibraryPicture(question.Existing, media)));
        if (!question.SameFile) tiles.Children.Add(Tile(incoming, "New file", FilePicture(question.IncomingPath)));
        dialog.AddBody(tiles);
        var forRest = question.OthersLeft > 0 ? dialog.AddCheckBox("Do the same for the rest of this import") : null;
        dialog.AddButton("Add as New Copy");
        dialog.AddButton("Use Existing", isDefault: true, accent: true);
        // Closing the window adds nothing new, the same as Use Existing.
        var answer = dialog.Ask() ?? "Use Existing";
        return new MediaImportAnswer(answer == "Use Existing", forRest?.IsChecked == true);
    }

    public static bool ConfirmSharedFile(MediaItem other, string itemName)
    {
        var dialog = new ChoiceWindow(Owner, "Replace Media File", "This file is already in your library",
            $"It's the file of “{other.Name}”. Replacing anyway gives “{itemName}” the same file, so the two items show the same picture.");
        dialog.AddBody(Tile(other.Name, "In your library", LibraryPicture(other, SceneView.DefaultMedia)));
        dialog.AddButton("Cancel", isCancel: true, isDefault: true);
        dialog.AddButton("Replace Anyway", accent: true);
        return dialog.Ask() == "Replace Anyway";
    }

    public static bool ConfirmReplaceEverywhere(string name, IReadOnlyList<MediaUsage.Group> groups)
    {
        var count = groups.Sum(g => g.Uses.Count);
        var dialog = new ChoiceWindow(Owner, "Replace Media File", $"Replace “{name}” everywhere?",
            (count == 1 ? "The new file takes this item's place in the spot below" : $"The new file takes this item's place in all {count} spots below")
            + ". To change one slide only, give that slide another background instead.", warning: true);
        dialog.AddBody(UsesList(groups));
        dialog.AddButton("Cancel", isCancel: true);
        dialog.AddButton("Replace Everywhere…", isDefault: true, accent: true);
        return dialog.Ask() == "Replace Everywhere…";
    }

    public static bool ConfirmDeleteUsedMedia(string name, IReadOnlyList<MediaUsage.Group> groups)
    {
        var count = groups.Sum(g => g.Uses.Count);
        var dialog = new ChoiceWindow(Owner, "Delete Media", $"Delete “{name}”?",
            $"Used in {count} place{(count == 1 ? "" : "s")}. Each spot shows a missing-media warning until the file is restored or replaced. "
            + "The item moves to the library's Trash folder.", warning: true);
        dialog.AddBody(UsesList(groups));
        dialog.AddButton("Cancel", isCancel: true, isDefault: true);
        dialog.AddButton("Move to Trash", danger: true);
        return dialog.Ask() == "Move to Trash";
    }

    public static void ShowCarriedSettings(string itemName, IReadOnlyList<string> carried)
    {
        if (carried.Count == 0) return;
        var dialog = new ChoiceWindow(Owner, "Replace Media File", "Settings carried over",
            $"“{itemName}” kept {string.Join(", ", carried)} from the old file — worth a check against the new one.");
        dialog.AddButton("OK", isDefault: true, isCancel: true, accent: true);
        dialog.Ask();
    }

    public static string? ChooseMediaFile(string title)
    {
        var extensions = string.Join(";", LibraryStore.SupportedMediaExtensions.Select(e => "*" + e));
        var dialog = new OpenFileDialog { Title = title, Filter = $"Images and videos|{extensions}|All files|*.*" };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public static string? ChooseFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }

    // MARK: Pieces

    /// <summary>The where-used list: one heading per kind ("Presentations (2)"), a row per use, "via Hymns theme" when it's the theme's.</summary>
    private static UIElement UsesList(IReadOnlyList<MediaUsage.Group> groups)
    {
        var list = new StackPanel();
        foreach (var group in groups)
        {
            list.Children.Add(EditorKit.Section($"{group.Title.ToUpperInvariant()} ({group.Uses.Count})", top: list.Children.Count == 0 ? 0 : 10));
            foreach (var use in group.Uses)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
                row.Children.Add(new TextBlock { Text = Glyph(group.Kind), FontFamily = EditorKit.IconFont, FontSize = 12, Width = 22, VerticalAlignment = VerticalAlignment.Center, Foreground = EditorKit.Res<Brush>("MutedText") });
                row.Children.Add(new TextBlock { Text = use.Name.Length == 0 ? "Untitled" : use.Name, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 300 });
                if (use.Via is { } via) row.Children.Add(new TextBlock { Text = $"  via {via} theme", Foreground = EditorKit.Res<Brush>("MutedText") });
                list.Children.Add(row);
            }
        }
        return new Border
        {
            Child = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 240 },
            Background = EditorKit.Res<Brush>("Card"), BorderBrush = EditorKit.Res<Brush>("Border"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 10, 12, 10),
        };
    }

    private static string Glyph(MediaUsage.Kind kind) => kind switch
    {
        MediaUsage.Kind.Theme => "",
        MediaUsage.Kind.Presentation => "",
        MediaUsage.Kind.Overlay => "",
        MediaUsage.Kind.Service => "",
        MediaUsage.Kind.Playlist => "",
        MediaUsage.Kind.ConfidenceLayout => "",
        MediaUsage.Kind.ActionCombo => "",
        MediaUsage.Kind.ScheduleTrigger => "",
        _ => "",
    };

    /// <summary>A picture with its name and a caption under it ("In your library", "New file").</summary>
    private static UIElement Tile(string title, string caption, UIElement picture)
    {
        var tile = new StackPanel { Width = 170, Margin = new Thickness(8, 0, 8, 0) };
        tile.Children.Add(new Border { Width = 160, Height = 90, CornerRadius = new CornerRadius(5), Background = Brushes.Black, ClipToBounds = true, Child = picture });
        tile.Children.Add(new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) });
        tile.Children.Add(new TextBlock { Text = caption, FontSize = 11, Foreground = EditorKit.Res<Brush>("MutedText"), HorizontalAlignment = HorizontalAlignment.Center });
        return tile;
    }

    private static UIElement LibraryPicture(MediaItem item, MediaHub? media) =>
        new SceneView { StaticScene = MediaTileViewModel.PreviewScene(item), PlaysVideo = false, Media = media ?? SceneView.DefaultMedia, Width = 160, Height = 90 };

    /// <summary>The incoming file: the picture itself, or a film icon for a video (posters need a player; this is just a reminder).</summary>
    private static UIElement FilePicture(string path)
    {
        if (LibraryStore.MediaKindOf(path) == MediaKind.Image)
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(path);
                image.DecodePixelWidth = 320;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();
                return new Image { Source = image, Stretch = Stretch.UniformToFill };
            }
            catch (Exception e) when (e is IOException or NotSupportedException or UriFormatException or ArgumentException)
            {
                // Falls through to the icon.
            }
        }
        return new TextBlock { Text = "", FontFamily = EditorKit.IconFont, FontSize = 28, Foreground = EditorKit.Res<Brush>("MutedText"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    }
}

/// <summary>
/// A small question window: a headline, a line of explanation, optional pictures or a list,
/// an optional tick box and a row of buttons. <see cref="Ask"/> shows it and returns the label
/// of the button pressed (null if it was closed).
/// </summary>
internal sealed class ChoiceWindow : Window
{
    private readonly StackPanel body = new() { Margin = new Thickness(20, 18, 20, 0) };
    private readonly StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 14, 20, 18) };

    public ChoiceWindow(Window? owner, string title, string headline, string message, bool warning = false)
    {
        EditorKit.Dress(this, owner, title, double.NaN, double.NaN);
        Headline = headline;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        if (warning)
            heading.Children.Add(new TextBlock { Text = "", FontFamily = EditorKit.IconFont, FontSize = 16, Foreground = EditorKit.Res<Brush>("Warn"), Margin = new Thickness(0, 1, 8, 0) });
        heading.Children.Add(new TextBlock { Text = headline, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxWidth = 400 });
        body.Children.Add(heading);
        body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Foreground = EditorKit.Res<Brush>("MutedText"), Margin = new Thickness(0, 0, 0, 14) });
        var root = new StackPanel { MinWidth = 420 };
        root.Children.Add(body);
        root.Children.Add(buttons);
        Content = root;
    }

    public string Headline { get; }

    /// <summary>The label of the button pressed, or null while open or when closed without one.</summary>
    public string? Answer { get; private set; }

    public void AddBody(UIElement element)
    {
        if (element is FrameworkElement framework) framework.Margin = new Thickness(0, 0, 0, 12);
        body.Children.Add(element);
    }

    public CheckBox AddCheckBox(string text)
    {
        var box = new CheckBox { Content = text, Margin = new Thickness(0, 0, 0, 4) };
        body.Children.Add(box);
        return box;
    }

    public Button AddButton(string label, bool isDefault = false, bool isCancel = false, bool accent = false, bool danger = false)
    {
        var button = new Button { Content = label, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), IsDefault = isDefault, IsCancel = isCancel };
        if (accent) button.Style = EditorKit.Res<Style>("AccentButton");
        if (danger) button.Foreground = EditorKit.Res<Brush>("Live");
        button.Click += (_, _) => Choose(label);
        buttons.Children.Add(button);
        return button;
    }

    /// <summary>Answers as if the button with this label were pressed (the self-test uses it too).</summary>
    public void Choose(string label)
    {
        Answer = label;
        if (IsVisible) Close();
    }

    public string? Ask()
    {
        LibraryDialogs.Showing?.Invoke(this);
        ShowDialog();
        return Answer;
    }
}
