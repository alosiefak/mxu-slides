using System.Windows;
using System.Windows.Media;
using MxU.Slides.Windows.Rendering;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// A preview of one screen or stage layout in its own window, to drag onto a second monitor in
/// the booth. It shows exactly what the live panel's preview would show for that target, and
/// keeps following it until closed. The Mac's "Open in Window" / "Open Preview Window" (LivePanel.swift).
/// </summary>
public sealed class PreviewPopOutWindow : Window
{
    public PreviewPopOutWindow(MainViewModel model, string? target, string name, Window? owner)
    {
        Target = target;
        Title = $"Preview: {name}";
        Background = Brushes.Black;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        View = new SceneView { Media = model.Media, PlaysVideo = true };
        Content = View;
        // Opens at the target's own shape (a tall layout opens tall), beside the main window.
        var aspect = model.PreviewAspect(target);
        Width = aspect >= 1 ? 640 : 360;
        Height = Width / aspect + SystemParameters.WindowCaptionHeight + 16;
        if (owner is { IsLoaded: true })
        {
            Owner = owner;
            Left = owner.Left + 80;
            Top = owner.Top + 100;
        }
    }

    /// <summary>The screen id or "layout::" target this window shows (null: the program as designed).</summary>
    public string? Target { get; }

    public SceneView View { get; }
}
