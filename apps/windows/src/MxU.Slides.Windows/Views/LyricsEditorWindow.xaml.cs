using System.Windows;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

public partial class LyricsEditorWindow : Window
{
    private readonly LyricsEditorViewModel editor;

    public LyricsEditorWindow(MainViewModel main, Presentation? existing)
    {
        editor = new LyricsEditorViewModel(main, existing);
        DataContext = editor;
        InitializeComponent();
        Loaded += (_, _) => TitleBox.Focus();
    }

    /// <summary>The saved song, once the operator presses Save.</summary>
    public Presentation? Result { get; private set; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var song = editor.Build();
            if (string.IsNullOrWhiteSpace(song.Name) || song.Name == "Untitled Song" && string.IsNullOrWhiteSpace(editor.Title))
            {
                MessageBox.Show(this, "Give the song a title first.", "MxU Slides", MessageBoxButton.OK, MessageBoxImage.Information);
                TitleBox.Focus();
                return;
            }
            Result = song;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            Log.Error("Building the song failed", ex);
            MessageBox.Show(this, $"Couldn't build the song: {ex.Message}", "MxU Slides", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
