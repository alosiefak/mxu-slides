using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MxU.Slides.MotionTemplates.AfterEffects;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// "How to make one…": the short guide to exporting a Lottie from After Effects, the route that
/// plays live with transparency. The words are in MotionTemplates/AfterEffects/AfterEffectsBridge.cs
/// (<see cref="LottieGuide"/>), so the After Effects window shows the same steps.
/// </summary>
internal sealed class LottieGuideWindow : Window
{
    private LottieGuideWindow(Window? owner)
    {
        EditorKit.Dress(this, owner, LottieGuide.Title, 620, 640);
        var done = EditorKit.Button("Done", Close, accent: true);
        done.IsCancel = true;
        done.HorizontalAlignment = HorizontalAlignment.Right;
        done.Margin = new Thickness(0, 10, 16, 14);
        var root = new DockPanel();
        DockPanel.SetDock(done, Dock.Bottom);
        root.Children.Add(done);
        root.Children.Add(new ScrollViewer { Content = Steps(), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(18, 14, 18, 0) });
        Content = root;
    }

    public static LottieGuideWindow Open(Window? owner)
    {
        var window = new LottieGuideWindow(owner);
        window.Show();
        return window;
    }

    /// <summary>The steps, as a panel (the After Effects window shows them too).</summary>
    public static StackPanel Steps()
    {
        var panel = new StackPanel();
        foreach (var (heading, text) in LottieGuide.Steps)
        {
            panel.Children.Add(new TextBlock
            {
                Text = heading, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 8, 0, 2), Foreground = EditorKit.Res<Brush>("Text"),
            });
            panel.Children.Add(EditorKit.Muted(text, 12, 4));
        }
        return panel;
    }
}
