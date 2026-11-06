using CommunityToolkit.Mvvm.Input;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// Picking several slides in the grid: Ctrl+click adds or removes one, Shift+click picks the run
/// from the last one clicked, Ctrl+A picks them all (in Edit mode). Copy, Cut, Duplicate, Delete
/// and a drag then work on all of them, as in the Mac's grid. A plain click still does what it
/// always did (takes the slide live, or selects it to edit) and picks just that one.
/// <para>
/// Picks are kept by slide id. When an arrangement shows a slide twice (a repeated chorus), both
/// tiles light up: they are the same slide, and an edit to one is an edit to both.
/// </para>
/// </summary>
public sealed partial class MainViewModel
{
    private readonly HashSet<string> pickedSlides = new(StringComparer.Ordinal);
    private int? pickAnchor;
    private bool keepingPicks;

    /// <summary>The picked slides' ids (the selected one included), in the presentation's order.</summary>
    public IReadOnlyList<string> PickedSlideIds
    {
        get
        {
            if (CurrentPresentation is not { } presentation) return [];
            var ids = pickedSlides.ToHashSet(StringComparer.Ordinal);
            if (SelectedSlide is { } selected) ids.Add(selected.Slide.Id);
            return [.. presentation.Slides.Where(s => ids.Contains(s.Id)).Select(s => s.Id)];
        }
    }

    /// <summary>
    /// A tile was clicked. With Ctrl or Shift held it's a pick, and nothing goes live: returns true.
    /// Otherwise returns false, and the click does what it always does.
    /// </summary>
    public bool PickTile(SlideTileViewModel tile, bool ctrl, bool shift)
    {
        if (!ctrl && !shift)
        {
            pickAnchor = tile.Index;
            return false;
        }
        keepingPicks = true;
        try
        {
            if (SelectedSlide is { } current) pickedSlides.Add(current.Slide.Id);
            if (shift && pickAnchor is { } anchor)
            {
                if (!ctrl) pickedSlides.Clear();
                var (from, to) = (Math.Min(anchor, tile.Index), Math.Max(anchor, tile.Index));
                foreach (var t in Slides.Where(t => t.Index >= from && t.Index <= to)) pickedSlides.Add(t.Slide.Id);
                SelectedSlide = tile;
            }
            else if (pickedSlides.Contains(tile.Slide.Id) && pickedSlides.Count > 1)
            {
                pickedSlides.Remove(tile.Slide.Id);
                if (SelectedSlide?.Slide.Id == tile.Slide.Id) SelectedSlide = Slides.FirstOrDefault(t => pickedSlides.Contains(t.Slide.Id));
                pickAnchor = tile.Index;
            }
            else
            {
                pickedSlides.Add(tile.Slide.Id);
                SelectedSlide = tile;
                pickAnchor = tile.Index;
            }
        }
        finally
        {
            keepingPicks = false;
        }
        MarkPickedTiles();
        return true;
    }

    /// <summary>Picks exactly these slides (what was just pasted or dropped), selecting the first.</summary>
    public void PickSlides(IEnumerable<string> ids)
    {
        var list = ids.ToList();
        keepingPicks = true;
        try
        {
            pickedSlides.Clear();
            pickedSlides.UnionWith(list);
            if (list.Count > 0) SelectedSlide = Slides.FirstOrDefault(t => t.Slide.Id == list[0]) ?? SelectedSlide;
        }
        finally
        {
            keepingPicks = false;
        }
        MarkPickedTiles();
    }

    /// <summary>Rebuilds the grid without losing the picks (after a paste, a drop, an undo).</summary>
    private void RebuildKeepingPicks()
    {
        var picks = PickedSlideIds;
        keepingPicks = true;
        try { RebuildSlides(); }
        finally { keepingPicks = false; }
        pickedSlides.Clear();
        pickedSlides.UnionWith(picks.Where(id => CurrentPresentation?.Slides.Any(s => s.Id == id) == true));
        MarkPickedTiles();
    }

    [RelayCommand]
    private void SelectAllSlides() => PickSlides(CurrentPresentation?.Slides.Select(s => s.Id) ?? []);

    /// <summary>The selection moved on its own (a plain click, a rebuild): pick just that slide.</summary>
    private void SelectionPicks(SlideTileViewModel? value)
    {
        if (!keepingPicks)
        {
            pickedSlides.Clear();
            if (value is not null) pickedSlides.Add(value.Slide.Id);
        }
        MarkPickedTiles();
    }

    private void MarkPickedTiles()
    {
        foreach (var tile in Slides) tile.IsSelected = ReferenceEquals(tile, SelectedSlide) || pickedSlides.Contains(tile.Slide.Id);
    }
}
