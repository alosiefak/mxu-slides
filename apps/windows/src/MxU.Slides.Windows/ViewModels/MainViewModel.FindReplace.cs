using MxU.Slides.Core.Editing;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// Find and replace (Core/Editing/FindReplace.cs), for Views/FindReplaceWindow.cs: across the open
/// presentation, or every song in the library. A Replace All is one undo step however many songs
/// it touches, and a live slide it changes is re-rendered, as any edit is.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The presentations a search covers.</summary>
    private IReadOnlyList<Presentation> SearchScope(bool everySong) =>
        everySong
            ? [.. Library.All<Presentation>().Where(p => p.PresentationKind == PresentationKind.Song || p.Id == CurrentPresentation?.Id)]
            : CurrentPresentation is { } open ? [open] : [];

    public IReadOnlyList<FindMatch> FindText(FindQuery query, bool everySong)
    {
        FlushPendingSave();
        return FindReplace.Find(SearchScope(everySong), query);
    }

    /// <summary>Replaces every match. Returns how many, and in how many presentations.</summary>
    public (int Replaced, int Presentations) ReplaceAllText(FindQuery query, string replacement, bool everySong)
    {
        FlushPendingSave();
        var replaced = 0;
        var touched = 0;
        using (UndoScope.Use())
        using (UndoScope.BeginGroup("Replace"))
        {
            foreach (var presentation in SearchScope(everySong))
            {
                var count = FindReplace.ReplaceAll(presentation, query, replacement);
                if (count == 0) continue;
                replaced += count;
                touched++;
                SavePresentation(presentation);
            }
        }
        if (replaced > 0) AfterTextReplaced();
        return (replaced, touched);
    }

    /// <summary>Replaces one match (if it's still there).</summary>
    public bool ReplaceText(FindMatch match, FindQuery query, string replacement)
    {
        FlushPendingSave();
        if (Library.Get<Presentation>(match.PresentationId) is not { } presentation || !FindReplace.Replace(presentation, match, query, replacement)) return false;
        using (UndoScope.Use())
        using (UndoScope.BeginGroup("Replace")) SavePresentation(presentation);
        AfterTextReplaced();
        return true;
    }

    /// <summary>Opens a match's presentation at its slide, in Edit mode, so it can be seen and changed.</summary>
    public void ShowMatch(FindMatch match)
    {
        if (Library.Get<Presentation>(match.PresentationId) is not { } presentation) return;
        if (CurrentPresentation?.Id != presentation.Id) Open(presentation, null, null);
        IsEditing = true;
        SelectedSlide = Slides.FirstOrDefault(t => t.Slide.Id == match.SlideId) ?? SelectedSlide;
        if (canvas is not null && SelectedSlide is not null) canvas.Select([match.ObjectId]);
    }

    // The grid, the text boxes and the canvas show the new words.
    private void AfterTextReplaced()
    {
        if (CurrentPresentation is null) return;
        RebuildKeepingPicks();
        LoadCanvas();
        RefreshSlideFields();
        OnCanvasSelectionChanged();
    }
}
