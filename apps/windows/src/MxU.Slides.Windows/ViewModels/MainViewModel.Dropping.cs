using MxU.Slides.Core.Editing;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// What a drop does (Core/Editing/Dropping.cs has the rules; Views/EditorDragDrop.cs reads the
/// mouse): slides dragged into a new order, media dropped on a slide or the canvas, library items
/// and files dropped into the service. Each drop is one undo step.
/// </summary>
public sealed partial class MainViewModel
{
    // MARK: The slide grid

    /// <summary>
    /// Moves the slides to just before <paramref name="beforeSlideId"/> (null: the end). Only while the
    /// song is shown as written: an arrangement's order is its own, set in Edit lyrics.
    /// </summary>
    public bool MoveSlidesTo(IReadOnlyList<string> slideIds, string? beforeSlideId)
    {
        if (CurrentPresentation is not { } presentation) return false;
        if (SelectedArrangement?.Id is not null)
        {
            Notice = "To drag slides into a new order, show the song \"As written\". An arrangement's order is changed in Edit lyrics.";
            return false;
        }
        FlushPendingSave();
        if (!DropRules.MoveSlides(presentation, [.. slideIds], beforeSlideId)) return false;
        SavePresentation(presentation);
        RebuildSlides();
        PickSlides(slideIds);
        return true;
    }

    /// <summary>Media dropped on a slide's tile: the slide's background.</summary>
    public bool DropMediaOnSlide(string slideId, string mediaId)
    {
        if (CurrentPresentation is not { } presentation || presentation.Slides.FirstOrDefault(s => s.Id == slideId) is not { } slide
            || Library.Get<MediaItem>(mediaId) is not { } media) return false;
        FlushPendingSave();
        DropRules.DropMediaAsBackground(slide, media);
        SavePresentation(presentation);
        RebuildSlides();
        return true;
    }

    /// <summary>Media dropped between tiles: new slides showing it, before <paramref name="beforeSlideId"/> (null: at the end).</summary>
    public void InsertMediaSlides(IReadOnlyList<MediaItem> media, string? beforeSlideId)
    {
        if (CurrentPresentation is not { } presentation || media.Count == 0) return;
        FlushPendingSave();
        var at = beforeSlideId is null ? presentation.Slides.Count : Math.Max(0, presentation.Slides.FindIndex(s => s.Id == beforeSlideId));
        var neighbour = at < presentation.Slides.Count ? presentation.Slides[at] : presentation.Slides.LastOrDefault();
        var slides = media.Select(item => EditorClipboard.MediaSlide(item, neighbour?.SectionId)).ToList();
        presentation.Slides.InsertRange(at, slides);
        SavePresentation(presentation);
        RebuildSlides();
        PickSlides(slides.Select(s => s.Id));
    }

    /// <summary>
    /// Files from Explorer dropped on the grid: pictures and videos come into the library and
    /// become slides there (or, dropped on a tile, that slide's background).
    /// </summary>
    public async Task DropFilesOnGrid(IReadOnlyList<string> files, string? beforeSlideId, string? ontoSlideId)
    {
        var media = files.Where(f => LibraryStore.MediaKindOf(f) is not null).ToList();
        if (media.Count == 0) return;
        var items = await ImportMediaFilesAsync(media);
        if (items.Count == 0) return;
        if (ontoSlideId is not null) DropMediaOnSlide(ontoSlideId, items[0].Id);
        else InsertMediaSlides(items, beforeSlideId);
    }

    // MARK: The canvas

    /// <summary>Media dropped on the canvas: a shape under it takes it as its fill, else a new picture box goes there.</summary>
    public bool DropMediaOnCanvas(string mediaId, EditorGeometry.Point at)
    {
        if (EditedSlide is not { } slide || Library.Get<MediaItem>(mediaId) is not { } media) return false;
        var (target, _) = DropRules.DropMediaOnCanvas(slide, SlideSceneBuilder.ThemeSlide(slide, EditedSlideTheme), media, at,
            SlideSceneBuilder.CanvasSizeFor(CurrentPresentation));
        AfterStructureChange([target.Id]);
        FlushPendingSave();
        return true;
    }

    /// <summary>Files from Explorer dropped on the canvas: imported, then placed where they were dropped.</summary>
    public async Task DropFilesOnCanvas(IReadOnlyList<string> files, EditorGeometry.Point at)
    {
        var media = files.Where(f => LibraryStore.MediaKindOf(f) is not null).ToList();
        if (media.Count == 0 || EditedSlide is not { } slide) return;
        var items = await ImportMediaFilesAsync(media);
        if (EditedSlide != slide) return;
        var size = SlideSceneBuilder.CanvasSizeFor(CurrentPresentation);
        var made = items.Select((item, i) => EditorClipboard.MediaObject(item, size, new EditorGeometry.Point(at.X + i * 24, at.Y + i * 24))).ToList();
        slide.Objects.AddRange(made);
        AfterStructureChange(made.Select(o => o.Id));
        FlushPendingSave();
    }

    // MARK: The service list

    /// <summary>A library item dragged into the service, at row <paramref name="index"/> of its items.</summary>
    public bool DropIntoService(LibraryDrag drag, int index)
    {
        var name = drag.Kind == typeof(Presentation) ? Library.Get<Presentation>(drag.Id)?.Name
            : drag.Kind == typeof(MediaItem) ? Library.Get<MediaItem>(drag.Id)?.Name
            : drag.Kind == typeof(AudioItem) ? Library.Get<AudioItem>(drag.Id)?.Name
            : drag.Kind == typeof(Playlist) ? Library.Get<Playlist>(drag.Id)?.Name
            : null;
        if (name is null || DropRules.ServiceItemFor(drag, name) is not { } item) return false;
        if (drag.Kind == typeof(Presentation)) item.ArrangementId = Library.Get<Presentation>(drag.Id)?.DefaultArrangementId;
        if (SelectedService is null) NewService();
        if (SelectedService is not { } service) return false;
        UpdateService(service.Id, s => DropRules.InsertRow(s, item, index));
        suppressServiceItemOpen = true;
        SelectedServiceItem = ServiceItems.FirstOrDefault(r => r.Item.Id == item.Id);
        suppressServiceItemOpen = false;
        return true;
    }

    /// <summary>A service row dragged to just before another (null: the end).</summary>
    public void MoveServiceItemBefore(string itemId, string? beforeItemId)
    {
        if (SelectedService is not { } service || itemId == beforeItemId) return;
        UpdateService(service.Id, s =>
        {
            var index = s.Items.FindIndex(i => i.Id == itemId);
            if (index < 0) return;
            var item = s.Items[index];
            s.Items.RemoveAt(index);
            var target = beforeItemId is null ? s.Items.Count : s.Items.FindIndex(i => i.Id == beforeItemId);
            s.Items.Insert(target < 0 ? s.Items.Count : target, item);
        });
        suppressServiceItemOpen = true;
        SelectedServiceItem = ServiceItems.FirstOrDefault(r => r.Item.Id == itemId);
        suppressServiceItemOpen = false;
    }

    /// <summary>Files from Explorer dropped into the service: media is imported and added as rows there.</summary>
    public async Task DropFilesIntoService(IReadOnlyList<string> files, int index)
    {
        var media = files.Where(f => LibraryStore.MediaKindOf(f) is not null).ToList();
        if (media.Count == 0) return;
        var items = await ImportMediaFilesAsync(media);
        foreach (var (item, offset) in items.Select((m, i) => (m, i)))
            DropIntoService(new LibraryDrag(typeof(MediaItem), item.Id), index + offset);
    }
}
