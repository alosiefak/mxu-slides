using CommunityToolkit.Mvvm.Input;
using MxU.Slides.Core.Editing;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Infrastructure;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>What Cut, Copy, Paste and Duplicate work on: the grid's slides, or the canvas's objects.</summary>
public enum EditTarget
{
    Slides,
    Objects,
}

/// <summary>
/// Cut, copy, paste and duplicate for slides and slide objects (Core/Editing/EditorClipboard.cs
/// has the rules; Infrastructure/AppClipboard.cs the Windows clipboard). Slides copied in one
/// presentation paste into another, and objects copied on the canvas paste onto any slide.
/// Each command is one undo step: the presentation and any Windows extras that came with the
/// slides are saved together.
/// </summary>
public sealed partial class MainViewModel
{
    // How many times the clipboard's objects have been pasted back onto their own slide (each steps further).
    private int pasteCascade;

    /// <summary>The canvas is the target when it has the keyboard in Edit mode; otherwise the grid.</summary>
    public EditTarget TargetFor(bool canvasFocused) => IsEditing && canvasFocused && EditedSlide is not null ? EditTarget.Objects : EditTarget.Slides;

    /// <summary>What the Edit menu works on: the canvas's objects while some are selected in Edit mode, else the slides.</summary>
    public EditTarget MenuTarget => IsEditing && HasCanvasSelection ? EditTarget.Objects : EditTarget.Slides;

    public bool Copy(EditTarget target) => target == EditTarget.Objects ? CopyObjects(cut: false) : CopySlides(cut: false);

    public bool Cut(EditTarget target) => target == EditTarget.Objects ? CopyObjects(cut: true) : CopySlides(cut: true);

    public Task Paste(EditTarget target) => target == EditTarget.Objects ? PasteOntoCanvas() : PasteIntoGrid();

    public bool Duplicate(EditTarget target)
    {
        if (target == EditTarget.Objects) DuplicateSelectedObjects();
        else return DuplicatePickedSlides();
        return true;
    }

    // MARK: Slides

    private bool CopySlides(bool cut)
    {
        if (CurrentPresentation is not { } presentation || PickedSlideIds is not { Count: > 0 } ids) return false;
        FlushPendingSave();
        AppClipboard.Put(new SlideClip(presentation.Id, [.. presentation.Slides.Where(s => ids.Contains(s.Id)).Select(MxuJson.Clone)]));
        if (cut) DeleteSlides(ids);
        return true;
    }

    /// <summary>Removes the picked slides (Delete in the grid, or Cut).</summary>
    public bool DeletePickedSlides() => DeleteSlides(PickedSlideIds);

    private bool DeleteSlides(IReadOnlyList<string> ids)
    {
        if (CurrentPresentation is not { } presentation || ids.Count == 0) return false;
        FlushPendingSave();
        var first = presentation.Slides.FindIndex(s => ids.Contains(s.Id));
        if (!EditorClipboard.DeleteSlides(presentation, [.. ids]))
        {
            Notice = "A presentation keeps at least one slide.";
            return false;
        }
        SavePresentation(presentation);
        RebuildSlides();
        var next = presentation.Slides[Math.Clamp(first, 0, presentation.Slides.Count - 1)].Id;
        SelectedSlide = Slides.FirstOrDefault(t => t.Slide.Id == next);
        return true;
    }

    /// <summary>Duplicates the picked slides, each copy straight after its slide, with new ids.</summary>
    public bool DuplicatePickedSlides()
    {
        if (CurrentPresentation is not { } presentation || PickedSlideIds is not { Count: > 0 } ids) return false;
        FlushPendingSave();
        var made = EditorClipboard.DuplicateSlides(presentation, [.. ids]);
        CopyExtras(made);
        SavePresentation(presentation);
        RebuildSlides();
        PickSlides(made.Select(m => m.Copy.Id));
        return true;
    }

    private async Task PasteIntoGrid()
    {
        if (CurrentPresentation is not { } presentation) return;
        FlushPendingSave();
        var after = PickedSlideIds.LastOrDefault();
        if (AppClipboard.Slides() is { } clip)
        {
            PasteSlides(clip.Slides, after);
        }
        else if (IsEditing && EditedSlide is not null && AppClipboard.Objects() is { } objects)
        {
            PasteObjects(objects);
        }
        else if (await ImportFromClipboard() is { Count: > 0 } media)
        {
            // Pictures and videos become slides of their own, as dropping them on the Mac's grid does.
            if (CurrentPresentation != presentation) return;
            var section = presentation.Slides.FirstOrDefault(s => s.Id == after)?.SectionId ?? presentation.Slides.LastOrDefault()?.SectionId;
            var slides = media.Select(item => EditorClipboard.MediaSlide(item, section)).ToList();
            InsertSlides(presentation, slides, after);
        }
        else if (AppClipboard.Text() is { Length: > 0 } text)
        {
            PasteText(presentation, text, after);
        }
    }

    /// <summary>Slides from the clipboard, as fresh copies after <paramref name="afterId"/>, picked.</summary>
    private void PasteSlides(IReadOnlyList<Slide> slides, string? afterId)
    {
        if (CurrentPresentation is not { } presentation) return;
        var pasted = EditorClipboard.InsertSlides(presentation, slides, afterId);
        CopyExtras(pasted);
        SavePresentation(presentation);
        RebuildSlides();
        PickSlides(pasted.Select(p => p.Copy.Id));
    }

    private void InsertSlides(Presentation presentation, IReadOnlyList<Slide> slides, string? afterId)
    {
        var at = afterId is null ? presentation.Slides.Count : presentation.Slides.FindIndex(s => s.Id == afterId) + 1;
        presentation.Slides.InsertRange(at <= 0 ? presentation.Slides.Count : at, slides);
        SavePresentation(presentation);
        RebuildSlides();
        PickSlides(slides.Select(s => s.Id));
    }

    /// <summary>
    /// Text from another app pasted into the grid: lyrics into a song become slides cut the way the
    /// song's are; anything else becomes one slide holding the words.
    /// </summary>
    private void PasteText(Presentation presentation, string text, string? afterId)
    {
        if (presentation.PresentationKind == PresentationKind.Song)
        {
            var made = EditorClipboard.InsertLyrics(presentation, text, afterId);
            if (made.Count == 0) return;
            SavePresentation(presentation);
            RebuildSlides();
            PickSlides(made.Select(s => s.Id));
            return;
        }
        var anchor = presentation.Slides.FirstOrDefault(s => s.Id == afterId) ?? presentation.Slides.LastOrDefault();
        var slide = new Slide
        {
            Id = LibraryStore.NewId(),
            Name = "",
            Objects = [new SlideObject
            {
                Id = LibraryStore.NewId(), ObjectKind = SlideObjectKind.Text,
                Name = anchor?.Objects.FirstOrDefault(o => o.ObjectKind == SlideObjectKind.Text)?.Name ?? "Text",
                Text = text.ReplaceLineEndings("\n").Trim(),
            }],
            SectionId = anchor?.SectionId,
            ThemeSlideName = anchor?.ThemeSlideName ?? CurrentTheme?.Slides?.FirstOrDefault()?.Name,
        };
        InsertSlides(presentation, [slide], anchor?.Id);
    }

    /// <summary>A pasted or duplicated slide keeps its Windows-only additions (a Push transition, camera cuts), and so do its objects.</summary>
    private void CopyExtras(IEnumerable<EditorClipboard.PastedSlide> pasted)
    {
        foreach (var (source, copy) in pasted)
        {
            Library.Extras.CopyOwner(source.Id, copy.Id);
            for (var i = 0; i < Math.Min(source.Objects.Count, copy.Objects.Count); i++)
                Library.Extras.CopyOwner(source.Objects[i].Id, copy.Objects[i].Id);
        }
    }

    // MARK: Objects

    private bool CopyObjects(bool cut)
    {
        if (EditedSlide is not { } slide || canvas is not { Selection.Count: > 0 } editor) return false;
        AppClipboard.Put(EditorClipboard.CopyObjects(slide, editor.Selection));
        pasteCascade = 0;
        if (cut)
        {
            DeleteSelectedObjects();
            FlushPendingSave();
        }
        return true;
    }

    private async Task PasteOntoCanvas()
    {
        if (EditedSlide is not { } slide || canvas is null) return;
        if (AppClipboard.Objects() is { } clip)
        {
            PasteObjects(clip);
        }
        else if (AppClipboard.Slides() is { } slides)
        {
            // Slides pasted in the editor land after the slide being edited (the Mac's paste()).
            FlushPendingSave();
            PasteSlides(slides.Slides, slide.Id);
        }
        else if (await ImportFromClipboard() is { Count: > 0 } media)
        {
            if (EditedSlide != slide) return;
            var size = SlideSceneBuilder.CanvasSizeFor(CurrentPresentation);
            var made = media.Select(item => EditorClipboard.MediaObject(item, size)).ToList();
            slide.Objects.AddRange(made);
            AfterStructureChange(made.Select(o => o.Id));
            FlushPendingSave();
        }
        else if (AppClipboard.Text() is { Length: > 0 } text)
        {
            var box = EditorClipboard.TextBox(text, SlideSceneBuilder.CanvasSizeFor(CurrentPresentation));
            AddObject(box);
            FlushPendingSave();
        }
    }

    private void PasteObjects(ObjectClip clip)
    {
        if (EditedSlide is not { } slide || canvas is null) return;
        pasteCascade = clip.SourceSlideId == slide.Id ? pasteCascade + 1 : 0;
        var pasted = EditorClipboard.PasteObjects(slide, clip, pasteCascade, canvas.FrameOf);
        for (var i = 0; i < pasted.Count; i++) Library.Extras.CopyOwner(clip.Objects[i].Id, pasted[i].Id);
        AfterStructureChange(pasted.Select(o => o.Id));
        // Saved now, so the objects and any extras they brought are one undo step.
        FlushPendingSave();
    }

    /// <summary>Files or a picture on the clipboard, brought into the library (asking about duplicates as an import does).</summary>
    private async Task<List<MediaItem>> ImportFromClipboard()
    {
        var files = AppClipboard.Files().Where(f => LibraryStore.MediaKindOf(f) is not null).ToList();
        if (files.Count == 0 && AppClipboard.Image() is { } picture && AppClipboard.SaveImage(picture) is { } saved) files.Add(saved);
        return files.Count == 0 ? [] : await ImportMediaFilesAsync(files);
    }

    // MARK: Commands (the Edit menu and the grid's right-click menu)

    [RelayCommand]
    private void CopySlidesNow() => CopySlides(cut: false);

    [RelayCommand]
    private void CutSlidesNow() => CopySlides(cut: true);

    [RelayCommand]
    private Task PasteSlidesNow() => PasteIntoGrid();
}
