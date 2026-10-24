using System.Windows.Controls;
using System.Windows.Input;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The object inspector as a panel of its own, bound to an
/// <see cref="ViewModels.ObjectInspectorViewModel"/>. The overlay editor uses it;
/// the main window's Edit mode could too (its inspector is the same settings
/// written out in MainWindow.xaml).
/// </summary>
public partial class ObjectInspectorPanel : UserControl
{
    public ObjectInspectorPanel() => InitializeComponent();

    /// <summary>Enter in a number box applies it straight away (otherwise it applies when you leave the box).</summary>
    private void NumberBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        e.Handled = true;
    }

    /// <summary>Puts the cursor in the text box of a selected text object (after a double-click on the canvas).</summary>
    public void FocusText()
    {
        if (InspectorText.IsVisible)
        {
            InspectorText.Focus();
            InspectorText.SelectAll();
        }
    }
}
