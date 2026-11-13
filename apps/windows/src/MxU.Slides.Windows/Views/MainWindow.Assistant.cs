using System.Windows;
using MxU.Slides.Windows.Views.Assistant;

namespace MxU.Slides.Windows.Views;

// The toolbar's Assistant button (Phase 12): opens the Assistant window on its Ask page.
public partial class MainWindow
{
    private void Assistant_Click(object sender, RoutedEventArgs e) => AssistantWindow.Open(this, model);
}
