using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MxU.Slides.Core.Library;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The questions the library's batch actions ask, in the same small dark windows as the other
/// library questions (<see cref="ChoiceWindow"/>): deleting several songs or media items, and
/// what's still using them. Worded as the Mac's delete confirmation ("Delete 3 Items").
/// </summary>
internal static class LibraryDepthDialogs
{
    public static bool ConfirmDeletePresentations(IReadOnlyList<string> names, IReadOnlyList<LibraryBatch.InService> used)
    {
        var headline = names.Count == 1 ? $"Delete “{names[0]}”?" : $"Delete {names.Count} items?";
        var message = used.Count == 0
            ? "They move to the library's Trash folder."
            : (used.Count == 1 && names.Count == 1 ? "It's in a service. " : $"{used.Count} of them are in services. ")
              + "Those service rows will say the presentation is missing. They move to the library's Trash folder.";
        var dialog = new ChoiceWindow(LibraryDialogs.Owner, "Delete", headline, message, warning: used.Count > 0);
        var list = new StackPanel();
        foreach (var name in names.Take(12))
        {
            var services = used.FirstOrDefault(u => u.PresentationName == name)?.Services;
            list.Children.Add(new TextBlock
            {
                Text = services is null ? name : $"{name}  —  in {string.Join(", ", services)}",
                TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 400, Margin = new Thickness(0, 1, 0, 1),
                Foreground = services is null ? EditorKit.Res<Brush>("Text") : EditorKit.Res<Brush>("Warn"),
            });
        }
        if (names.Count > 12) list.Children.Add(EditorKit.Muted($"and {names.Count - 12} more"));
        dialog.AddBody(Card(list));
        dialog.AddButton("Cancel", isCancel: true, isDefault: true);
        dialog.AddButton(names.Count == 1 ? "Move to Trash" : $"Delete {names.Count} Items", danger: true);
        return dialog.Ask() is { } answer && answer != "Cancel";
    }

    public static bool ConfirmDeleteMedia(IReadOnlyList<string> names, IReadOnlyList<MediaUsage.Group> groups)
    {
        var count = groups.Sum(g => g.Uses.Count);
        var dialog = new ChoiceWindow(LibraryDialogs.Owner, "Delete Media", $"Delete {names.Count} media items?",
            count == 0
                ? "They move to the library's Trash folder, with each file no other item uses."
                : $"Used in {count} place{(count == 1 ? "" : "s")}. Each spot shows a missing-media warning until the file is restored or replaced. They move to the library's Trash folder, with each file no other item uses.",
            warning: count > 0);
        if (count > 0) dialog.AddBody(LibraryDialogs.UsesList(groups));
        dialog.AddButton("Cancel", isCancel: true, isDefault: true);
        dialog.AddButton($"Delete {names.Count} Items", danger: true);
        return dialog.Ask() is { } answer && answer != "Cancel";
    }

    private static Border Card(UIElement child) => new()
    {
        Child = new ScrollViewer { Content = child, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 240 },
        Background = EditorKit.Res<Brush>("Card"), BorderBrush = EditorKit.Res<Brush>("Border"), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 10, 12, 10),
    };
}
