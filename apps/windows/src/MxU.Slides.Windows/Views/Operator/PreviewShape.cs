using System.Windows;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The live panel's preview keeps the shape of what it shows: a 4:3 projector previews 4:3, a tall
/// stage layout previews tall (up to a height that leaves the column usable), and anything else
/// 16:9. Pop-out preview windows already open at their target's shape; this does the same for the
/// main one. Port of the aspect ratio in the Mac's LivePanel.swift (PreviewTargets.aspect).
/// Set <c>v:PreviewShape.FollowsTarget="True"</c> on the preview.
/// </summary>
public static class PreviewShape
{
    /// <summary>The tallest the preview gets, as a share of its width (a tall layout shows letterboxed below this).</summary>
    public const double TallestRatio = 1.25;

    public static readonly DependencyProperty FollowsTargetProperty = DependencyProperty.RegisterAttached(
        "FollowsTarget", typeof(bool), typeof(PreviewShape), new PropertyMetadata(false, OnFollowsTargetChanged));

    public static bool GetFollowsTarget(DependencyObject element) => (bool)element.GetValue(FollowsTargetProperty);

    public static void SetFollowsTarget(DependencyObject element, bool value) => element.SetValue(FollowsTargetProperty, value);

    /// <summary>The height for a width and a target's aspect (width ÷ height).</summary>
    public static double HeightFor(double width, double aspect) =>
        width <= 0 ? 0 : Math.Min(width / (aspect > 0 ? aspect : 16.0 / 9.0), width * TallestRatio);

    private static void OnFollowsTargetChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not FrameworkElement view || e.NewValue is not true) return;
        MainViewModel? model = null;
        void Fit()
        {
            var target = model?.PreviewTarget?.Id;
            view.Height = HeightFor(view.ActualWidth, model?.PreviewAspect(target) ?? 16.0 / 9.0);
        }
        void Attach()
        {
            if (model is not null)
            {
                model.OutputsChanged -= OnOutputs;
                model.StageLayoutsChanged -= OnOutputs;
            }
            model = view.DataContext as MainViewModel;
            if (model is not null)
            {
                model.OutputsChanged += OnOutputs;
                model.StageLayoutsChanged += OnOutputs;
            }
            Fit();
        }
        void OnOutputs()
        {
            if (view.Dispatcher.CheckAccess()) Fit();
            else view.Dispatcher.BeginInvoke(Fit);
        }
        view.SizeChanged += (_, args) =>
        {
            if (args.WidthChanged) Fit();
        };
        view.DataContextChanged += (_, _) => Attach();
        view.Loaded += (_, _) => Attach();
    }
}
