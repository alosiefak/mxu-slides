using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The Animation tab (see AnimationTab.xaml): the gallery, presets, steps and the selected step's
/// settings. This file only does what XAML can't: dragging a step onto another to reorder, scrolling
/// the whole tab with the wheel, asking for a preset's name, and naming the "Move to" buttons for an
/// overlay (which goes up and is taken down) or a slide (which has clicks).
/// </summary>
public partial class AnimationTab : UserControl
{
    private const string DragFormat = "mxu/animation-step";

    private Point dragStart;
    private StepRowViewModel? dragRow;

    public AnimationTab()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach(e.NewValue as AnimationTabViewModel);
    }

    private AnimationTabViewModel? Model => DataContext as AnimationTabViewModel;

    private void Attach(AnimationTabViewModel? model)
    {
        if (model is null) return;
        model.AskPresetName = () => Window.GetWindow(this) is { } owner
            ? InputDialog.Ask(owner, "Save as preset", "A name for the animation preset:", "My preset")
            : null;
        MoveUpButton.Content = model.IsOverlay ? "Going up" : "The start";
        MoveDownButton.Content = model.IsOverlay ? "Taken down" : "The end";
        MoveClickButton.Visibility = model.IsOverlay ? Visibility.Collapsed : Visibility.Visible;
        // Picking a step scrolls its settings into view, as the Mac's inspector does.
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AnimationTabViewModel.StepEditor) && model.StepEditor is not null)
                Dispatcher.BeginInvoke(() => StepEditorPanel.BringIntoView(), DispatcherPriority.Loaded);
        };
    }

    /// <summary>The steps list grows to fit, so the mouse wheel scrolls the whole tab instead.</summary>
    private void StepList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        AnimationScroll.ScrollToVerticalOffset(AnimationScroll.VerticalOffset - e.Delta / 3.0);
        e.Handled = true;
    }

    /// <summary>Enter in a one-line box applies it straight away.</summary>
    private void NumberBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        e.Handled = true;
    }

    // MARK: Dragging a step onto another

    private static StepRowViewModel? RowAt(DependencyObject? element)
    {
        while (element is not null and not ListBoxItem) element = VisualTreeHelper.GetParent(element);
        return (element as ListBoxItem)?.DataContext as StepRowViewModel;
    }

    private static ListBoxItem? ItemAt(DependencyObject? element)
    {
        while (element is not null and not ListBoxItem) element = VisualTreeHelper.GetParent(element);
        return element as ListBoxItem;
    }

    private void StepList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        dragStart = e.GetPosition(this);
        dragRow = RowAt(e.OriginalSource as DependencyObject);
    }

    private void StepList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || dragRow is null) return;
        var delta = e.GetPosition(this) - dragStart;
        if (Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var row = dragRow;
        dragRow = null;
        DragDrop.DoDragDrop(StepList, new DataObject(DragFormat, row.StepId), DragDropEffects.Move);
    }

    private void StepList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void StepList_Drop(object sender, DragEventArgs e)
    {
        if (Model is not { } model || e.Data.GetData(DragFormat) is not string stepId) return;
        if (ItemAt(e.OriginalSource as DependencyObject) is { DataContext: StepRowViewModel onto } item)
            model.DropStep(stepId, onto, after: e.GetPosition(item).Y > item.ActualHeight / 2);
        else
            model.DropStepAtEnd(stepId);
        e.Handled = true;
    }
}
