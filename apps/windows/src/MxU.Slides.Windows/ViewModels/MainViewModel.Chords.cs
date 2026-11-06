using MxU.Slides.Core.Editing;
using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Stage;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// Chords on lyric slides (Core/Editing/ChordEditing.cs has the rules; Views/ChordEditorWindow.cs is
/// the editor): placing, retyping, moving and removing chords over words, the song's key and the key
/// it's shown in (transposing, the Mac's Display Key), and a capo the stage display follows. Each edit
/// is saved at once, so it undoes like any other ("Undo Chords"), and a live slide re-renders.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly Dictionary<string, int> capos = new(StringComparer.Ordinal);
    private bool caposHooked;

    /// <summary>Hooks the stage display to the songs' capos (once; the stage asks every second, so answers are kept).</summary>
    private void HookCapos()
    {
        if (caposHooked) return;
        caposHooked = true;
        Library.Extras.Changed += (kind, owner) =>
        {
            if (kind != SongCapo.Kind) return;
            lock (capos) capos.Remove(owner);
        };
        ConfidenceSceneBuilder.CapoFor = id =>
        {
            lock (capos)
            {
                if (capos.TryGetValue(id, out var fret)) return fret;
                return capos[id] = SongCapo.For(Library.Extras, id);
            }
        };
    }

    /// <summary>The text box of a slide that holds its lyric (and chords): the first with chords, else the first typed one.</summary>
    public static SlideObject? ChordTarget(Slide slide) =>
        slide.Objects.FirstOrDefault(o => o.ObjectKind == SlideObjectKind.Text && o.TextLink is null && o.Chords is { Count: > 0 })
        ?? slide.Objects.FirstOrDefault(o => o.ObjectKind == SlideObjectKind.Text && o.TextLink is null);

    /// <summary>Changes the chords of a slide's lyric box and saves; returns the edit (to keep a chord picked).</summary>
    public ChordEditing.Edit? EditChords(string slideId, Func<SlideObject, ChordEditing.Edit> edit)
    {
        HookCapos();
        if (CurrentPresentation is not { } presentation || presentation.Slides.FirstOrDefault(s => s.Id == slideId) is not { } slide
            || ChordTarget(slide) is not { } box) return null;
        FlushPendingSave();
        var result = edit(box);
        box.Chords = result.Chords.Count > 0 ? result.Chords : null;
        SavePresentation(presentation);
        RefreshTile(slide.Id);
        return result;
    }

    /// <summary>The lyric with its chords in brackets ("[G]Amazing [C]grace"), to edit as text.</summary>
    public static string BracketedChords(Slide slide) => ChordTarget(slide) is { } box ? ChordMath.Bracketed(box.Text, box.Chords ?? []) : "";

    /// <summary>Takes words and [chords] typed as one text: the words keep their styling, the chords land over them.</summary>
    public void SetBracketedChords(string slideId, string bracketed)
    {
        if (CurrentPresentation is not { } presentation || presentation.Slides.FirstOrDefault(s => s.Id == slideId) is not { } slide
            || ChordTarget(slide) is not { } box) return;
        FlushPendingSave();
        var (text, chords) = ChordMath.Extract(bracketed.ReplaceLineEndings("\n"));
        box.Chords = null;
        Core.Editing.ObjectText.SetText(box, text);
        box.Chords = chords.Count > 0 ? chords : null;
        SavePresentation(presentation);
        RefreshTile(slide.Id);
        RefreshSlideFields();
    }

    /// <summary>The key the song's chords are written in (null: none).</summary>
    public void SetMusicKey(string? key)
    {
        if (CurrentPresentation is not { } presentation || presentation.MusicKey == key) return;
        presentation.MusicKey = string.IsNullOrWhiteSpace(key) ? null : key;
        if (presentation.MusicKey is null) presentation.DisplayKey = null;
        SavePresentation(presentation);
        RebuildKeepingPicks();
        OnPropertyChanged(nameof(CurrentSubtitle));
    }

    /// <summary>
    /// The key the chords are shown in on every screen (the Mac's Transpose). Choosing the song's own
    /// key clears it, as on the Mac.
    /// </summary>
    public void Transpose(string key)
    {
        if (CurrentPresentation is not { MusicKey: { } musicKey } presentation) return;
        var display = string.Equals(key, musicKey, StringComparison.OrdinalIgnoreCase) ? null : key;
        if (presentation.DisplayKey == display) return;
        presentation.DisplayKey = display;
        SavePresentation(presentation);
        RebuildKeepingPicks();
    }

    /// <summary>The open song's capo fret (0: none). Only the stage display's chords follow it.</summary>
    public int Capo
    {
        get => CurrentPresentation is { } p ? SongCapo.For(Library.Extras, p.Id) : 0;
        set
        {
            HookCapos();
            if (CurrentPresentation is not { } p) return;
            SongCapo.Set(Library.Extras, p.Id, value);
            RenderOutputs(Rendering.SceneTransition.Cut, refreshOnly: true);
            OnPropertyChanged();
        }
    }

    /// <summary>"Key: G", "Key: G → A", or "Key: G → A · capo 2" for the header chip.</summary>
    public string SongKeyText
    {
        get
        {
            if (CurrentPresentation is not { } p) return "";
            if (p.MusicKey is not { } key) return ChordMath.HasChords(p) ? "Key: Set…" : "";
            var shown = p.DisplayKey is { } display && !string.Equals(display, key, StringComparison.OrdinalIgnoreCase) ? $"{key} → {display}" : key;
            return Capo is > 0 and var fret ? $"Key: {shown} · capo {fret}" : $"Key: {shown}";
        }
    }

    // Just the one tile's picture, so a chord dragged along doesn't rebuild the whole grid.
    private void RefreshTile(string slideId)
    {
        if (CurrentPresentation is not { } p) return;
        foreach (var tile in Slides.Where(t => t.Slide.Id == slideId))
            tile.Scene = Core.Scene.SlideSceneBuilder.Scene(tile.Slide, ThemeFor(tile.Slide, p), p, SelectedArrangement?.Id, pager: Rendering.TextLayout.Pager);
        if (IsEditing) canvas?.Refresh();
        OnPropertyChanged(nameof(SongKeyText));
    }
}
