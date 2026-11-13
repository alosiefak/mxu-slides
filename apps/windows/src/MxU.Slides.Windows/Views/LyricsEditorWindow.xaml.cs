using System.Windows;
using MxU.Slides.Assistant;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

public partial class LyricsEditorWindow : Window
{
    private readonly MainViewModel main;
    private readonly LyricsEditorViewModel editor;

    // Clean up with AI: the job in flight (the button then reads Cancel), and the words as they
    // were before the last clean-up (the button then reads Put my text back).
    private CancellationTokenSource? cleaning;
    private string? beforeCleanUp;

    public LyricsEditorWindow(MainViewModel main, Presentation? existing)
    {
        this.main = main;
        editor = new LyricsEditorViewModel(main, existing);
        DataContext = editor;
        InitializeComponent();
        Loaded += (_, _) => TitleBox.Focus();
        DrawCleanUp();
        main.AssistantChanged += DrawCleanUp;
        Closed += (_, _) =>
        {
            main.AssistantChanged -= DrawCleanUp;
            cleaning?.Cancel();
        };
    }

    /// <summary>The saved song, once the operator presses Save.</summary>
    public Presentation? Result { get; private set; }

    /// <summary>The editor behind the window (the self-test fills in its lyrics).</summary>
    public LyricsEditorViewModel Editor => editor;

    private void DrawCleanUp()
    {
        CleanUpButton.Content = cleaning is not null ? "Cancel" : beforeCleanUp is not null ? "Put my text back" : "Clean up with AI";
        CleanUpButton.IsEnabled = cleaning is not null || beforeCleanUp is not null || main.AssistantUnavailable is null;
        CleanUpButton.ToolTip = main.AssistantUnavailable
            ?? "Sorts pasted lyrics into Verse, Chorus, Bridge… with tidy line breaks. It may not change a word: the result is checked against what you pasted.";
    }

    private async void CleanUp_Click(object sender, RoutedEventArgs e) => await CleanUpNow();

    /// <summary>What the button does (the self-test's way in): clean up, cancel a clean-up in flight, or put the old words back.</summary>
    public async Task CleanUpNow()
    {
        if (cleaning is not null)
        {
            cleaning.Cancel();
            return;
        }
        if (beforeCleanUp is { } old)
        {
            editor.Lyrics = old;
            beforeCleanUp = null;
            CleanUpStatus.Text = "Your text is back as it was.";
            DrawCleanUp();
            return;
        }
        var pasted = editor.Lyrics;
        if (string.IsNullOrWhiteSpace(pasted)) return;
        cleaning = new CancellationTokenSource();
        CleanUpStatus.Text = "Asking the assistant… your words stay as they are until it answers.";
        DrawCleanUp();
        try
        {
            var result = await main.CleanUpLyricsAsync(pasted, cleaning.Token);
            // Only if the box still holds what was sent: typing meanwhile wins.
            if (editor.Lyrics == pasted)
            {
                beforeCleanUp = pasted;
                editor.Lyrics = result.Cleaned.Text;
                var leftOut = result.Cleaned.LeftOut.Count == 0 ? "" : $" Left out (not sung words): {string.Join(" · ", result.Cleaned.LeftOut.Select(l => $"“{l}”"))}.";
                CleanUpStatus.Text = $"Sorted into {result.Cleaned.Sections} sections; every word checked against what you pasted.{leftOut} Read it through before saving. ({result.CostLine})";
            }
        }
        catch (AssistantException failure)
        {
            CleanUpStatus.Text = failure.Message;
        }
        finally
        {
            cleaning = null;
            DrawCleanUp();
        }
    }

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
