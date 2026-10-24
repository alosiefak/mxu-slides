using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>One editable text box of the selected slide (e.g. "Lyrics", "Title", "Body").</summary>
public sealed partial class SlideTextFieldViewModel(SlideObject target, Action changed) : ObservableObject
{
    public SlideObject Object { get; } = target;

    public string Name => string.IsNullOrWhiteSpace(Object.Name) ? "Text" : Object.Name;

    public string Text
    {
        get => Object.Text;
        set
        {
            var normalized = value.Replace("\r\n", "\n");
            if (Object.Text == normalized) return;
            Object.Text = normalized;
            OnPropertyChanged();
            changed();
        }
    }

    /// <summary>Re-reads the text after something else (the canvas inspector) changed it.</summary>
    public void Refresh() => OnPropertyChanged(nameof(Text));
}

public sealed partial class MainViewModel
{
    [ObservableProperty]
    public partial SlideTileViewModel? SelectedSlide { get; set; }

    public ObservableCollection<SlideTextFieldViewModel> SlideTextFields { get; } = [];

    public ObservableCollection<DesignChoice> Designs { get; } = [];

    public bool HasSelectedSlide => SelectedSlide is not null;

    /// <summary>The selected slide as stored in the presentation (tiles hold the same object).</summary>
    private Slide? EditedSlide => SelectedSlide is { } tile ? CurrentPresentation?.Slides.FirstOrDefault(s => s.Id == tile.Slide.Id) : null;

    partial void OnSelectedSlideChanged(SlideTileViewModel? oldValue, SlideTileViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        FlushPendingSave();
        SlideTextFields.Clear();
        Designs.Clear();
        if (EditedSlide is { } slide)
        {
            foreach (var o in slide.Objects.Where(o => o.ObjectKind == SlideObjectKind.Text && o.TextLink is null))
                SlideTextFields.Add(new SlideTextFieldViewModel(o, SlideEdited));
            foreach (var design in CurrentTheme?.Slides ?? []) Designs.Add(new DesignChoice(design.Name, design.Folder));
        }
        OnPropertyChanged(nameof(HasSelectedSlide));
        OnPropertyChanged(nameof(SelectedDesign));
        OnPropertyChanged(nameof(SlideNotes));
        OnPropertyChanged(nameof(SlideHasBackground));
        OnPropertyChanged(nameof(SlideBackgroundColor));
    }

    public DesignChoice? SelectedDesign
    {
        get
        {
            if (EditedSlide is not { } slide || CurrentTheme is null) return null;
            var template = SlideSceneBuilder.ThemeSlide(slide, CurrentTheme);
            return Designs.FirstOrDefault(d => d.Name == template?.Name);
        }
        set
        {
            if (value is null || EditedSlide is not { } slide || slide.ThemeSlideName == value.Name) return;
            slide.ThemeSlideName = value.Name;
            SlideEdited();
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private void ApplyDesignToAllSlides()
    {
        if (SelectedDesign is not { } design || CurrentPresentation is not { } p) return;
        foreach (var slide in p.Slides) slide.ThemeSlideName = design.Name;
        SavePresentation(p);
        RebuildSlides();
        Notice = $"Every slide in {p.Name} now uses the \"{design.Name}\" design.";
    }

    public string SlideNotes
    {
        get => EditedSlide?.Notes ?? "";
        set
        {
            if (EditedSlide is not { } slide) return;
            slide.Notes = string.IsNullOrEmpty(value) ? null : value;
            SlideEdited();
        }
    }

    public bool SlideHasBackground => EditedSlide?.Background is not null;

    [RelayCommand]
    private void ClearSlideBackground()
    {
        if (EditedSlide is not { } slide) return;
        slide.Background = null;
        SlideEdited();
        OnPropertyChanged(nameof(SlideHasBackground));
    }

    // Autosave: typing re-renders the thumbnail at once and writes to disk shortly after.

    private DispatcherTimer? saveTimer;

    private void SlideEdited()
    {
        if (CurrentPresentation is not { } p || SelectedSlide is not { } tile) return;
        tile.Scene = SlideSceneBuilder.Scene(tile.Slide, CurrentTheme, p, SelectedArrangement?.Id, pager: Rendering.TextLayout.Pager);
        foreach (var other in Slides.Where(t => t != tile && t.Slide.Id == tile.Slide.Id))
            other.Scene = SlideSceneBuilder.Scene(other.Slide, CurrentTheme, p, SelectedArrangement?.Id, pager: Rendering.TextLayout.Pager);
        Show.PresentationEdited(p);
        if (Show.Live?.Slide.Id == tile.Slide.Id) RenderOutputs(Rendering.SceneTransition.Cut, refreshOnly: true);
        saveTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) => FlushPendingSave(), Dispatcher.CurrentDispatcher);
        saveTimer.Stop();
        saveTimer.Start();
        pendingSave = p;
    }

    private Presentation? pendingSave;

    public void FlushPendingSave()
    {
        FlushOverlaySave();
        saveTimer?.Stop();
        if (pendingSave is not { } p) return;
        pendingSave = null;
        Library.Save(p);
    }

    // MARK: Slides

    [RelayCommand]
    private void AddSlide()
    {
        if (CurrentPresentation is not { } p) return;
        var after = EditedSlide;
        var slide = new Slide
        {
            Id = LibraryStore.NewId(),
            Name = "",
            Objects = [new SlideObject { Id = LibraryStore.NewId(), ObjectKind = SlideObjectKind.Text, Name = after?.Objects.FirstOrDefault(o => o.ObjectKind == SlideObjectKind.Text)?.Name ?? "Lyrics", Text = "" }],
            SectionId = after?.SectionId,
            ThemeSlideName = after?.ThemeSlideName ?? CurrentTheme?.Slides?.FirstOrDefault()?.Name,
        };
        var index = after is null ? p.Slides.Count : p.Slides.IndexOf(after) + 1;
        p.Slides.Insert(index, slide);
        SavePresentation(p);
        RebuildSlides();
        IsEditing = true;
        SelectedSlide = Slides.FirstOrDefault(t => t.Slide.Id == slide.Id);
    }

    [RelayCommand]
    private void DuplicateSlide()
    {
        if (CurrentPresentation is not { } p || EditedSlide is not { } source) return;
        var copy = MxuJson.Clone(source);
        copy.Id = LibraryStore.NewId();
        foreach (var o in copy.Objects) o.Id = LibraryStore.NewId();
        p.Slides.Insert(p.Slides.IndexOf(source) + 1, copy);
        SavePresentation(p);
        RebuildSlides();
        SelectedSlide = Slides.FirstOrDefault(t => t.Slide.Id == copy.Id);
    }

    [RelayCommand]
    private void DeleteSlide()
    {
        if (CurrentPresentation is not { } p || EditedSlide is not { } slide || p.Slides.Count <= 1) return;
        var index = p.Slides.IndexOf(slide);
        p.Slides.Remove(slide);
        SavePresentation(p);
        RebuildSlides();
        SelectedSlide = Slides.ElementAtOrDefault(Math.Min(index, Slides.Count - 1));
    }

    [RelayCommand]
    private void MoveSlideEarlier() => MoveSlide(-1);

    [RelayCommand]
    private void MoveSlideLater() => MoveSlide(+1);

    private void MoveSlide(int offset)
    {
        if (CurrentPresentation is not { } p || EditedSlide is not { } slide) return;
        var index = p.Slides.IndexOf(slide);
        var target = index + offset;
        if (target < 0 || target >= p.Slides.Count) return;
        p.Slides.RemoveAt(index);
        p.Slides.Insert(target, slide);
        SavePresentation(p);
        RebuildSlides();
        SelectedSlide = Slides.FirstOrDefault(t => t.Slide.Id == slide.Id);
    }

    // MARK: Presentations

    [RelayCommand]
    private void NewPresentation()
    {
        var themeId = Settings.DefaultThemeId;
        var theme = Library.Get<Theme>(themeId);
        var design = theme?.Slides?.FirstOrDefault(s => s.Name == "Points") ?? theme?.Slides?.FirstOrDefault();
        var presentation = new Presentation
        {
            Id = LibraryStore.NewId(),
            Name = "Untitled Presentation",
            PresentationKind = PresentationKind.Deck,
            ThemeId = themeId,
            Origin = new PresentationOrigin { Source = PresentationOriginSource.MadeHere, CreatedAt = DateTimeOffset.Now.ToString("O") },
            Slides =
            [
                new Slide
                {
                    Id = LibraryStore.NewId(),
                    Name = "",
                    ThemeSlideName = design?.Name,
                    Objects = [new SlideObject { Id = LibraryStore.NewId(), ObjectKind = SlideObjectKind.Text, Name = SlideSceneBuilder.ThemeSlide(new Slide { ThemeSlideName = design?.Name }, theme) is { } t ? SlideSceneBuilder.Placeholder(t)?.Name ?? "Text" : "Text", Text = "" }],
                },
            ],
        };
        Library.Save(presentation);
        RefreshLibrary();
        Open(presentation, null, null);
        IsEditing = true;
        SelectedSlide = Slides.FirstOrDefault();
    }

    public void RenamePresentation(Presentation presentation, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        presentation.Name = name.Trim();
        if (presentation.Ccli is { } ccli && presentation.PresentationKind == PresentationKind.Song) ccli.SongTitle = presentation.Name;
        SavePresentation(presentation);
        foreach (var service in Library.All<Service>())
        {
            var changed = false;
            foreach (var item in service.Items.Where(i => i.RefId == presentation.Id && i.ItemKind == ServiceItemKind.Presentation))
            {
                item.Name = presentation.Name;
                changed = true;
            }
            if (changed) Library.Save(service);
        }
        OnPropertyChanged(nameof(CurrentTitle));
    }

    [RelayCommand]
    private void DuplicatePresentation()
    {
        if (CurrentPresentation is not { } p) return;
        var copy = MxuJson.Clone(p);
        copy.Id = LibraryStore.NewId();
        copy.Name = $"{p.Name} (copy)";
        copy.Origin = new PresentationOrigin { Source = PresentationOriginSource.Duplicate, Detail = p.Id, CreatedAt = DateTimeOffset.Now.ToString("O") };
        Library.Save(copy);
        Open(copy, null, null);
    }

    [RelayCommand]
    private void DeletePresentation()
    {
        if (CurrentPresentation is not { } p) return;
        Library.Delete<Presentation>(p.Id);
        CurrentPresentation = null;
        Slides.Clear();
        OnPropertyChanged(nameof(HasPresentation));
        OnPropertyChanged(nameof(CurrentTitle));
        OnPropertyChanged(nameof(CurrentSubtitle));
        RefreshServiceItems();
        Notice = $"Moved \"{p.Name}\" to the library's Trash folder.";
    }
}
