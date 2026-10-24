using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.Outputs;
using MxU.Slides.Windows.Rendering;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// The operator's main screen: library, services, the open presentation's
/// slides, and the live show. Split across partial files by area
/// (Live, Services, Media, Editing, Stage).
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel(LibraryStore library, AppSettings settings, AppSession session)
    {
        Library = library;
        Settings = settings;
        Session = session;
        Media = new MediaHub(ResolveMedia) { VideoVolume = settings.VideoVolume, PosterCache = Path.Combine(AppPaths.LocalRoot, "Posters") };
        Media.TrimOf = TrimFor; // each video's in point, out point and play rate (MainViewModel.Rendering.cs)
        SceneView.DefaultMedia = Media;
        Library.Changed += OnLibraryChanged;

        RefreshThemes();
        RefreshLibrary();
        RefreshServices();
        RefreshMedia();
        RefreshOverlays();
        RefreshAudio();
        RefreshCombos();
        InitializeStage();

        if (App.RecoveredFromCrash)
            Notice = "MxU Slides closed unexpectedly last time. Your library is safe and your service has been reopened.";
        foreach (var problem in library.LoadProblems) Problem = problem;

        SelectedService = Services.FirstOrDefault(s => s.Id == session.ServiceId) ?? Services.FirstOrDefault();
        if (session.ServiceItemId is { } itemId && ServiceItems.FirstOrDefault(i => i.Item.Id == itemId) is { } row)
            SelectedServiceItem = row;
        else if (Library.Get<Presentation>(session.PresentationId) is { } presentation)
            Open(presentation, null, null);
        CanRestoreLive = App.RecoveredFromCrash && Library.Get<Presentation>(session.LivePresentationId) is not null;
        ApplyMidiSettings();
        ApplyApiSettings();
        StartScheduler();
        StartInputs();
        StartTimecode();
        _ = RefreshMidiDevicesAsync();
        _ = RefreshMidiOutputsAsync();
        // Combos marked "run on startup" go once the window has settled, as on the Mac.
        var startup = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        startup.Tick += (_, _) =>
        {
            startup.Stop();
            Actions.RunStartupCombos();
        };
        startup.Start();
    }

    public LibraryStore Library { get; }
    public AppSettings Settings { get; }
    public AppSession Session { get; }
    public MediaHub Media { get; }
    public ShowState Show { get; } = new();

    /// <summary>Set by the main window once it exists.</summary>
    public OutputManager? Outputs { get; set; }
    public SceneView? AudiencePreview { get; set; }

    /// <summary>The hidden copy of the audience output that recording, streaming, NDI and DeckLink take (set by the main window).</summary>
    public ProgramFeed? Program
    {
        get => program;
        set
        {
            if (program is not null) program.ActiveChanged -= OnProgramActiveChanged;
            program = value;
            if (program is not null) program.ActiveChanged += OnProgramActiveChanged;
        }
    }

    private ProgramFeed? program;

    // A destination just started listening: give the feed what's live now. (Each recording and
    // stream opens its own sound: see OpenSessionAudio in MainViewModel.Broadcast.cs.)
    private void OnProgramActiveChanged() => RenderOutputs(SceneTransition.Cut);
    public SceneView? StagePreview { get; set; }

    [ObservableProperty]
    public partial string? Notice { get; set; }

    [ObservableProperty]
    public partial string? Problem { get; set; }

    [RelayCommand]
    private void DismissNotice() => Notice = null;

    [RelayCommand]
    private void DismissProblem() => Problem = null;

    private MediaHub.Resolved? ResolveMedia(string mediaId)
    {
        if (Library.Get<MediaItem>(mediaId) is not { } item) return null;
        return Library.BlobPath(item) is { } path ? new MediaHub.Resolved(path, item.MediaKind, item.Name) : null;
    }

    private void OnLibraryChanged(Type type, string id)
    {
        // Imports save from a background thread; the lists on screen belong to the UI thread.
        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => OnLibraryChanged(type, id));
            return;
        }
        if (type == typeof(Presentation)) RefreshLibrary();
        else if (type == typeof(Service)) RefreshServices(keepSelection: true);
        else if (type == typeof(MediaItem)) RefreshMedia();
        else if (type == typeof(Theme)) RefreshThemes();
        else if (type == typeof(ConfidenceLayout)) RefreshStageLayouts();
        else if (type == typeof(AudioItem) || type == typeof(Playlist)) RefreshAudio();
        else if (type == typeof(ActionCombo)) RefreshCombos();
        else if (type == typeof(GroupPalette)) RefreshHotKeys();
        else if (type == typeof(MIDIDevice)) ResolveMidiDevices();
        else if (type == typeof(Overlay) && !suppressOverlayRefresh) RefreshOverlays();
    }

    // MARK: Library

    public ObservableCollection<LibraryEntryViewModel> LibraryItems { get; } = [];

    [ObservableProperty]
    public partial string LibrarySearch { get; set; } = "";

    /// <summary>0 = everything, 1 = songs, 2 = presentations.</summary>
    [ObservableProperty]
    public partial int LibraryFilter { get; set; }

    [ObservableProperty]
    public partial LibraryEntryViewModel? SelectedLibraryEntry { get; set; }

    partial void OnLibrarySearchChanged(string value) => RefreshLibrary();

    partial void OnLibraryFilterChanged(int value) => RefreshLibrary();

    private bool suppressLibrarySelection;

    partial void OnSelectedLibraryEntryChanged(LibraryEntryViewModel? value)
    {
        if (value is null || suppressLibrarySelection) return;
        SelectedServiceItem = null;
        Open(value.Presentation, null, null);
    }

    public void RefreshLibrary()
    {
        var query = LibrarySearch.Trim();
        var selectedId = SelectedLibraryEntry?.Presentation.Id;
        var entries = Library.All<Presentation>()
            .Select(p => new LibraryEntryViewModel(p))
            .Where(e => LibraryFilter switch { 1 => e.IsSong, 2 => !e.IsSong, _ => true })
            .Where(e => query.Length == 0 || Matches(e.Presentation, query))
            .ToList();
        suppressLibrarySelection = true;
        LibraryItems.Clear();
        foreach (var entry in entries) LibraryItems.Add(entry);
        SelectedLibraryEntry = selectedId is null ? null : LibraryItems.FirstOrDefault(e => e.Presentation.Id == selectedId);
        suppressLibrarySelection = false;
    }

    /// <summary>Title, author, CCLI number or any lyric line.</summary>
    private static bool Matches(Presentation p, string query)
    {
        if (p.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)) return true;
        if (p.Ccli?.Author?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true) return true;
        if (p.Ccli?.SongNumber?.ToString() == query) return true;
        return p.Slides.Any(s => s.Objects.Any(o => o.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase)));
    }

    // MARK: Themes

    public ObservableCollection<ThemeChoice> Themes { get; } = [];

    public void RefreshThemes()
    {
        Themes.Clear();
        Themes.Add(new ThemeChoice("", "No theme (slide's own styles)"));
        foreach (var theme in Library.All<Theme>()) Themes.Add(new ThemeChoice(theme.Id, theme.Name));
        OnPropertyChanged(nameof(CurrentThemeChoice));
        if (CurrentPresentation is not null)
        {
            CurrentTheme = Library.Get<Theme>(CurrentPresentation.ThemeId);
            RebuildSlides();
        }
    }

    // MARK: Opening a presentation

    [ObservableProperty]
    public partial Presentation? CurrentPresentation { get; set; }

    [ObservableProperty]
    public partial Theme? CurrentTheme { get; set; }

    /// <summary>The service item the open presentation came from, if any.</summary>
    public string? CurrentContextItemId { get; private set; }

    public ObservableCollection<SlideTileViewModel> Slides { get; } = [];

    public ObservableCollection<ArrangementChoice> Arrangements { get; } = [];

    [ObservableProperty]
    public partial ArrangementChoice? SelectedArrangement { get; set; }

    private bool suppressArrangementChange;

    partial void OnSelectedArrangementChanged(ArrangementChoice? value)
    {
        if (suppressArrangementChange || CurrentPresentation is null) return;
        RebuildSlides();
        // Remember the arrangement on the service item it was opened from.
        if (CurrentContextItemId is { } itemId && SelectedService is { } service
            && service.Items.FirstOrDefault(i => i.Id == itemId) is { } item && item.ArrangementId != value?.Id)
        {
            item.ArrangementId = value?.Id;
            Library.Save(service);
        }
    }

    public ThemeChoice? CurrentThemeChoice
    {
        get => CurrentPresentation is null ? null : Themes.FirstOrDefault(t => t.Id == CurrentPresentation.ThemeId) ?? Themes.FirstOrDefault();
        set
        {
            if (value is null || CurrentPresentation is null || CurrentPresentation.ThemeId == value.Id) return;
            CurrentPresentation.ThemeId = value.Id;
            SavePresentation(CurrentPresentation);
            CurrentTheme = Library.Get<Theme>(value.Id);
            RebuildSlides();
            OnPropertyChanged();
        }
    }

    public bool HasPresentation => CurrentPresentation is not null;

    public string CurrentTitle => CurrentPresentation?.Name ?? "Nothing open";

    public string CurrentSubtitle
    {
        get
        {
            if (CurrentPresentation is not { } p) return "Pick a song or presentation from the library or a service.";
            var parts = new List<string> { $"{Slides.Count} slides" };
            if (p.Ccli?.Author is { Length: > 0 } author) parts.Add(author);
            if (p.Ccli?.SongNumber is { } number) parts.Add($"CCLI {number}");
            if (p.MusicKey is { Length: > 0 } key) parts.Add($"Key {key}");
            return string.Join("  ·  ", parts);
        }
    }

    public void Open(Presentation presentation, string? contextItemId, string? arrangementId)
    {
        CurrentContextItemId = contextItemId;
        CurrentPresentation = presentation;
        CurrentTheme = Library.Get<Theme>(presentation.ThemeId);
        suppressArrangementChange = true;
        Arrangements.Clear();
        Arrangements.Add(new ArrangementChoice(null, "As written"));
        foreach (var a in presentation.Arrangements ?? []) Arrangements.Add(new ArrangementChoice(a.Id, a.Name));
        var wanted = arrangementId ?? presentation.DefaultArrangementId;
        SelectedArrangement = Arrangements.FirstOrDefault(a => a.Id == wanted) ?? Arrangements[0];
        suppressArrangementChange = false;
        RebuildSlides();
        OnPropertyChanged(nameof(CurrentThemeChoice));
        OnPropertyChanged(nameof(HasPresentation));
        Session.PresentationId = presentation.Id;
        Session.ServiceItemId = contextItemId;
        Session.Save();
    }

    /// <summary>Rebuilds the slide grid (thumbnails) for the open presentation.</summary>
    public void RebuildSlides()
    {
        var selectedId = SelectedSlide?.Slide.Id;
        Slides.Clear();
        if (CurrentPresentation is { } p)
        {
            var arrangementId = SelectedArrangement?.Id;
            var arranged = SlideSceneBuilder.ArrangedSlides(p, arrangementId);
            var starts = SlideSceneBuilder.ArrangementBlockStarts(p, arrangementId).ToHashSet();
            for (var i = 0; i < arranged.Count; i++)
            {
                var slide = arranged[i];
                var scene = SlideSceneBuilder.Scene(slide, ThemeFor(slide, p), p, arrangementId, pager: Rendering.TextLayout.Pager);
                Slides.Add(new SlideTileViewModel(i, slide, scene, GroupNames.For(slide, p), GroupNames.ColorFor(slide, p),
                    starts.Contains(i) || i == 0));
            }
        }
        MarkLiveTiles();
        MarkMissingMediaTiles();
        UpdateTimelineBar();
        SelectedSlide = Slides.FirstOrDefault(s => s.Slide.Id == selectedId) ?? (IsEditing ? Slides.FirstOrDefault() : null);
        OnPropertyChanged(nameof(CurrentTitle));
        OnPropertyChanged(nameof(CurrentSubtitle));
    }

    public void SavePresentation(Presentation presentation)
    {
        Library.Save(presentation);
        Show.PresentationEdited(presentation);
        if (Show.Live?.Presentation?.Id == presentation.Id) RenderOutputs(SceneTransition.Cut, refreshOnly: true);
    }
}
