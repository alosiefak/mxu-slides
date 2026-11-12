using System.Windows;
using MxU.Slides.Windows.Views.Notes;

namespace MxU.Slides.Windows.Views;

/// <summary>The library's Notes button (opens Views/Notes/NotesWindow.cs), and the slide grid's note badges.</summary>
public partial class MainWindow
{
    private void Notes_Click(object sender, RoutedEventArgs e) => NotesWindow.Open(this, model);

    /// <summary>A slide tile's Update, Remove or Add badge: review what the note proposes for the open deck.</summary>
    private void NoteBadge_Click(object? sender, EventArgs e)
    {
        if (model.NoteWithChangesForOpenDeck is { } note) NoteChangesWindow.Open(this, model, note);
    }
}
