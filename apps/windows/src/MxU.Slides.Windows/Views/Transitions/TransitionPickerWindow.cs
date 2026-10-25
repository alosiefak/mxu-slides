using System.Windows;
using System.Windows.Controls;
using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views.Transitions;

/// <summary>
/// "Transition…" for a slide, a presentation or a media item: a large preview of the choice, the
/// picker, and a note on what a Mac will play. Every change is saved at once (as Slide Actions
/// are), so the next time the slide fires it does the new thing. Opened from the right-click
/// menus (<see cref="TransitionMenus"/>).
/// </summary>
public sealed class TransitionPickerWindow : Window
{
    private readonly TransitionPreview preview;
    private readonly TextBlock summary = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock macNote = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly string defaultLabel;

    /// <summary>The picker (the self-test drives it directly).</summary>
    internal TransitionPicker Picker { get; }

    private TransitionPickerWindow(Window? owner, string title, string help, WindowsTransition? current, string defaultLabel,
        WindowsTransition playedByDefault, Func<WindowsTransition?, bool> save)
    {
        this.defaultLabel = defaultLabel;
        EditorKit.Dress(this, owner, title, 860, 760);
        Picker = new TransitionPicker(current, defaultLabel);
        preview = new TransitionPreview(current ?? playedByDefault, 288, 162);
        Picker.Changed += choice =>
        {
            preview.Transition = choice ?? playedByDefault;
            Describe(choice);
            if (!save(choice)) MessageBox.Show(this, "This transition couldn't be saved: the item is no longer in the library.", Title);
        };
        Describe(current);

        var heading = new StackPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        heading.Children.Add(EditorKit.Muted(help, 12, 8));
        heading.Children.Add(summary);
        heading.Children.Add(macNote);
        var top = new DockPanel { Margin = new Thickness(16, 14, 16, 6) };
        var framed = new Border { Child = preview, CornerRadius = new CornerRadius(6), ClipToBounds = true, BorderBrush = EditorKit.Res<System.Windows.Media.Brush>("Border"), BorderThickness = new Thickness(1) };
        DockPanel.SetDock(framed, Dock.Left);
        top.Children.Add(framed);
        top.Children.Add(heading);

        var done = EditorKit.Button("Done", Close, accent: true);
        done.IsDefault = true;
        done.IsCancel = true;
        done.MinWidth = 90;
        done.Margin = new Thickness(0);
        var footer = new DockPanel { Margin = new Thickness(16, 8, 16, 14), LastChildFill = false };
        DockPanel.SetDock(done, Dock.Right);
        footer.Children.Add(done);

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer
        {
            Content = new Border { Child = Picker, Padding = new Thickness(16, 0, 16, 8) },
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        Content = root;
    }

    private void Describe(WindowsTransition? choice)
    {
        summary.Text = choice is null ? $"No transition of its own: {defaultLabel}" : WindowsTransitions.Describe(choice);
        macNote.Text = choice is { IsShared: false }
            ? $"Only this app plays {WindowsTransitions.Name(choice.Kind)}. A Mac opening this plays {TransitionEdit.Describe(choice.ToShared())} instead."
            : "A Mac opening this plays the same.";
        macNote.Foreground = EditorKit.Res<System.Windows.Media.Brush>("MutedText");
    }

    // MARK: Opening it

    /// <summary>A slide's own transition (null = its presentation's, else the room's). The slide is found in <paramref name="presentation"/> or the library.</summary>
    internal static TransitionPickerWindow? ForSlide(Window? owner, MainViewModel model, Slide slide, Presentation? presentation)
    {
        var deck = presentation?.Slides.Any(s => s.Id == slide.Id) == true ? presentation : model.PresentationContaining(slide.Id);
        if (deck?.Slides.FirstOrDefault(s => s.Id == slide.Id) is not { } stored) return null;
        var name = string.IsNullOrWhiteSpace(stored.Name) ? "this slide" : $"“{stored.Name}”";
        var (deckId, slideId) = (deck.Id, stored.Id);
        return new TransitionPickerWindow(owner, string.IsNullOrWhiteSpace(stored.Name) ? "Slide Transition" : $"Slide Transition — {stored.Name}",
            $"How {name} comes in when it goes live. Its own transition beats its presentation's and the room default.",
            model.SlideTransitionChoice(stored), model.SlideDefaultLabel(deck),
            model.PresentationTransitionChoice(deck) ?? model.RoomTransitionChoice(media: false),
            choice => model.SaveSlideTransition(deckId, slideId, choice));
    }

    /// <summary>A presentation's transition: what its slides without their own come in with (null = the room default).</summary>
    internal static TransitionPickerWindow ForPresentation(Window? owner, MainViewModel model, Presentation presentation)
    {
        var id = presentation.Id;
        return new TransitionPickerWindow(owner, $"Presentation Transition — {presentation.Name}",
            "How this presentation's slides come in, unless a slide has a transition of its own. A background a slide brings follows it too.",
            model.PresentationTransitionChoice(presentation), model.RoomDefaultLabel(media: false), model.RoomTransitionChoice(media: false),
            choice => model.SavePresentationTransition(id, choice));
    }

    /// <summary>A media item's own transition: as it comes on and as it's cleared (null = the default).</summary>
    internal static TransitionPickerWindow? ForMedia(Window? owner, MainViewModel model, MediaItem item)
    {
        if (model.Library.Get<MediaItem>(item.Id) is not { } stored) return null;
        var id = stored.Id;
        return new TransitionPickerWindow(owner, $"Media Transition — {stored.Name}",
            "How this background or video comes on, and goes off when it's cleared. Without one, it follows the slide's presentation, or the room default.",
            model.MediaTransitionChoice(stored), model.RoomDefaultLabel(media: true), model.RoomTransitionChoice(media: true),
            choice => model.SaveMediaTransition(id, choice));
    }
}
