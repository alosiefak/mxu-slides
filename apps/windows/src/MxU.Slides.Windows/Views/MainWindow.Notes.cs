using System.Windows;
using MxU.Slides.Windows.Views.Notes;

namespace MxU.Slides.Windows.Views;

/// <summary>The library's Notes button: opens the notes window (Views/Notes/NotesWindow.cs).</summary>
public partial class MainWindow
{
    private void Notes_Click(object sender, RoutedEventArgs e) => NotesWindow.Open(this, model);
}
