using MxU.Slides.Core.Control;
using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Infrastructure;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// Song arrangements (Phase 8): the open song's named orders of sections, chosen from the slide
/// grid's header (Views/ArrangementPicker.cs, the pills in Views/ArrangementStrip.cs) and edited in
/// Views/ArrangementEditorWindow.cs. The rules are Core's (<see cref="ArrangementEditing"/>); this
/// saves the song, keeps the header's list and the grid in step, and moves the live position
/// across when the arrangement of the live song changes mid-song.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Raised after the open song's arrangements change (the header's chip and pills redraw).</summary>
    public event Action? ArrangementsChanged;

    /// <summary>The open song's arrangement as the grid plays it: the picked one, or none (the slides as written).</summary>
    public Arrangement? CurrentArrangement =>
        CurrentPresentation is { } p ? ArrangementEditing.Find(p, SelectedArrangement?.Id) : null;

    /// <summary>Changing arrangements is editing the song: locked in volunteer mode (the chip shows the name only, as on the Mac).</summary>
    public bool CanChangeArrangement => !IsVolunteer;

    /// <summary>Plays <paramref name="arrangementId"/> (null: the slides as written) in the grid, and on the service row it came from.</summary>
    public void ChooseArrangement(string? arrangementId)
    {
        if (!Permit(OperatorAction.ChangeArrangement) || CurrentPresentation is not { } p) return;
        // The setter redraws the grid and remembers the pick on the service row (MainViewModel.cs).
        SelectedArrangement = Arrangements.FirstOrDefault(a => a.Id == arrangementId) ?? Arrangements.FirstOrDefault();
        ReanchorLive(p);
        ArrangementsChanged?.Invoke();
    }

    /// <summary>Makes a new arrangement from the song's sections, saves the song and picks it. Null when there's nothing to arrange.</summary>
    public Arrangement? NewArrangement() => EditArrangements(p => ArrangementEditing.Add(p), pick: true);

    public Arrangement? DuplicateArrangement(string arrangementId) => EditArrangements(p => ArrangementEditing.Duplicate(p, arrangementId), pick: true);

    public void RenameArrangement(string arrangementId, string name) => EditArrangements(p => ArrangementEditing.Rename(p, arrangementId, name) ? ArrangementEditing.Find(p, arrangementId) : null);

    public void DeleteArrangement(string arrangementId)
    {
        var picked = SelectedArrangement?.Id == arrangementId;
        EditArrangements(p => ArrangementEditing.Delete(p, arrangementId) ? new Arrangement() : null);
        if (picked) ChooseArrangement(null);
    }

    /// <summary>What the song plays when a service row doesn't pick an arrangement (null: the slides as written).</summary>
    public void SetDefaultArrangement(string? arrangementId) => EditArrangements(p => ArrangementEditing.SetDefault(p, arrangementId) ? new Arrangement() : null);

    public void AddArrangementBlock(string arrangementId, string sectionId) => EditBlocks(p => ArrangementEditing.AddBlock(p, arrangementId, sectionId));

    public void RemoveArrangementBlock(string arrangementId, int index) => EditBlocks(p => ArrangementEditing.RemoveBlock(p, arrangementId, index));

    public void MoveArrangementBlock(string arrangementId, int from, int before) => EditBlocks(p => ArrangementEditing.MoveBlock(p, arrangementId, from, before));

    /// <summary>Sets the blocks from a typed order ("V1, C x2"); false when it named no section (nothing changes).</summary>
    public bool SetArrangementOrder(string arrangementId, string order) => EditBlocks(p => ArrangementEditing.SetOrder(p, arrangementId, order));

    /// <summary>Applies an edit to the open song's arrangements, saves it and refreshes; returns the arrangement the edit made or named.</summary>
    private Arrangement? EditArrangements(Func<Presentation, Arrangement?> edit, bool pick = false)
    {
        if (!Permit(OperatorAction.ChangeArrangement) || CurrentPresentation is not { } p) return null;
        var result = edit(p);
        if (result is null) return null;
        SaveArrangements(p, pick ? result.Id : SelectedArrangement?.Id);
        return result;
    }

    private bool EditBlocks(Func<Presentation, bool> edit)
    {
        if (!Permit(OperatorAction.ChangeArrangement) || CurrentPresentation is not { } p || !edit(p)) return false;
        SaveArrangements(p, SelectedArrangement?.Id);
        return true;
    }

    /// <summary>Saves the song, lists its arrangements again (keeping <paramref name="pick"/>) and redraws the grid.</summary>
    private void SaveArrangements(Presentation p, string? pick)
    {
        try
        {
            SavePresentation(p);
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't save the song's arrangements", ex);
            Problem = $"Couldn't save the arrangement: {ex.Message}";
            return;
        }
        ReloadArrangementChoices(p, pick);
        ReanchorLive(p);
        ArrangementsChanged?.Invoke();
    }

    /// <summary>Lists the song's arrangements in the header again and picks one (saving it to the service row, as a pick does).</summary>
    private void ReloadArrangementChoices(Presentation p, string? pick)
    {
        suppressArrangementChange = true;
        Arrangements.Clear();
        Arrangements.Add(new ArrangementChoice(null, "As written"));
        foreach (var a in p.Arrangements ?? []) Arrangements.Add(new ArrangementChoice(a.Id, a.Name));
        var wanted = Arrangements.FirstOrDefault(a => a.Id == pick) ?? Arrangements[0];
        var changed = SelectedArrangement?.Id != wanted.Id;
        SelectedArrangement = wanted;
        suppressArrangementChange = false;
        if (changed) OnSelectedArrangementChanged(wanted);
        else RebuildSlides();
    }

    /// <summary>
    /// The live song's arrangement changed (picked in the header, or its blocks edited): Next and
    /// Previous now walk the new order from the live slide. The slide stays on screen; the live
    /// position moves to the same slide in the new order (the same repeat of it where it has one).
    /// A live slide the new order doesn't play keeps the old position.
    /// </summary>
    private void ReanchorLive(Presentation p)
    {
        if (Live is not { } live || live.PresentationId != p.Id || Show.Live?.Slide is not { } slide) return;
        // The row the song came from decides its arrangement; a song opened from the library plays what the grid picked.
        var item = live.ItemId is { } itemId ? SelectedService?.Items.FirstOrDefault(i => i.Id == itemId) : null;
        var arrangementId = live.ItemId is null || CurrentContextItemId == live.ItemId
            ? SelectedArrangement?.Id
            : item?.ArrangementId ?? p.DefaultArrangementId;
        if (arrangementId == live.ArrangementId && SlideSceneBuilder.ArrangedSlides(p, arrangementId).ElementAtOrDefault(live.Occurrence)?.Id == slide.Id) return;
        var before = SlideSceneBuilder.ArrangedSlides(p, live.ArrangementId);
        var after = SlideSceneBuilder.ArrangedSlides(p, arrangementId);
        var repeat = before.Take(live.Occurrence + 1).Count(s => s.Id == slide.Id);
        var places = after.Select((s, i) => (s, i)).Where(x => x.s.Id == slide.Id).Select(x => x.i).ToList();
        if (places.Count == 0) return;
        var occurrence = places[Math.Clamp(repeat, 1, places.Count) - 1];
        Live = live with { ArrangementId = arrangementId, Occurrence = occurrence };
        Session.LiveArrangementId = arrangementId;
        Session.Save();
        UpdateLiveLabels(p, slide, occurrence, after.Count);
    }

    /// <summary>The section names the open song's sections can be picked from, with a first line of each (the Mac's SectionPreviewPicker rows).</summary>
    public List<(PresentationSection Section, int Slides, string FirstLine)> SectionChoices()
    {
        if (CurrentPresentation is not { } p) return [];
        return [.. (p.Sections ?? []).Select(section =>
        {
            var own = p.Slides.Where(s => s.SectionId == section.Id).ToList();
            var line = own.SelectMany(s => s.Objects).Select(o => o.Text)
                .Select(text => text.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim())
                .FirstOrDefault(l => !string.IsNullOrEmpty(l)) ?? "";
            return (section, own.Count, line);
        })];
    }
}
