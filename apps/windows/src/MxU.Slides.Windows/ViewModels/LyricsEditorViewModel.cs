using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// The song editor: title and credits, the lyrics as labelled plain text, slide
/// size, theme and an optional order. The preview rebuilds as you type, using
/// exactly the code that will make the real slides.
/// </summary>
public sealed partial class LyricsEditorViewModel : ObservableObject
{
    private readonly MainViewModel main;
    private readonly Presentation? existing;
    private readonly DispatcherTimer debounce;

    public LyricsEditorViewModel(MainViewModel main, Presentation? existing)
    {
        this.main = main;
        this.existing = existing;
        Themes = main.Themes.Where(t => t.Id.Length > 0).ToList();
        if (existing is null)
        {
            Title = "";
            Lyrics = "Verse 1\n\n\nChorus\n\n";
            LinesPerSlide = main.Settings.LinesPerSlide;
            Theme = Themes.FirstOrDefault(t => t.Id == main.Settings.DefaultThemeId) ?? Themes.FirstOrDefault();
        }
        else
        {
            Title = existing.Name;
            Author = existing.Ccli?.Author ?? "";
            CcliNumber = existing.Ccli?.SongNumber?.ToString() ?? "";
            Copyright = existing.Ccli?.Copyright ?? "";
            MusicKey = existing.MusicKey ?? "";
            Lyrics = SongEditing.LyricsText(existing).ReplaceLineEndings("\r\n");
            LinesPerSlide = SongEditing.LinesPerSlide(existing, main.Settings.LinesPerSlide);
            Theme = Themes.FirstOrDefault(t => t.Id == existing.ThemeId) ?? Themes.FirstOrDefault();
            Order = SongEditing.OrderText(existing, existing.Arrangements?.FirstOrDefault(a => a.Id == existing.DefaultArrangementId && a.Name == "Custom")?.Id);
        }
        debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Background, (_, _) =>
        {
            debounce!.Stop();
            RebuildPreview();
        }, Dispatcher.CurrentDispatcher);
        debounce.Stop();
        RebuildPreview();
    }

    public string WindowTitle => existing is null ? "New Song" : $"Edit {existing.Name}";

    public IReadOnlyList<ThemeChoice> Themes { get; }

    [ObservableProperty]
    public partial string Title { get; set; } = "";

    [ObservableProperty]
    public partial string Author { get; set; } = "";

    [ObservableProperty]
    public partial string CcliNumber { get; set; } = "";

    [ObservableProperty]
    public partial string Copyright { get; set; } = "";

    [ObservableProperty]
    public partial string MusicKey { get; set; } = "";

    [ObservableProperty]
    public partial string Lyrics { get; set; } = "";

    [ObservableProperty]
    public partial int LinesPerSlide { get; set; } = 2;

    [ObservableProperty]
    public partial ThemeChoice? Theme { get; set; }

    /// <summary>Optional play order, e.g. "V1, C, V2, C, B, C x2".</summary>
    [ObservableProperty]
    public partial string Order { get; set; } = "";

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    public ObservableCollection<SlideTileViewModel> Preview { get; } = [];

    partial void OnLyricsChanged(string value) => Schedule();
    partial void OnLinesPerSlideChanged(int value) => Schedule();
    partial void OnThemeChanged(ThemeChoice? value) => Schedule();
    partial void OnOrderChanged(string value) => Schedule();
    partial void OnTitleChanged(string value) => Schedule();

    private void Schedule()
    {
        if (debounce is null) return;
        debounce.Stop();
        debounce.Start();
    }

    /// <summary>The song these fields describe.</summary>
    public Presentation Build()
    {
        int? number = int.TryParse(CcliNumber.Trim(), out var n) && n > 0 ? n : null;
        return SongEditing.Build(existing, Lyrics, Math.Clamp(LinesPerSlide, 1, 12),
            new SongEditing.Details(Title, Author, number, Copyright, MusicKey),
            Theme?.Id ?? main.Settings.DefaultThemeId, Order);
    }

    private void RebuildPreview()
    {
        Preview.Clear();
        try
        {
            var song = Build();
            var arranged = SlideSceneBuilder.ArrangedSlides(song, song.DefaultArrangementId);
            var starts = SlideSceneBuilder.ArrangementBlockStarts(song, song.DefaultArrangementId).ToHashSet();
            for (var i = 0; i < arranged.Count; i++)
            {
                var slide = arranged[i];
                Preview.Add(new SlideTileViewModel(i, slide, SlideSceneBuilder.Scene(slide, main.ThemeFor(slide, song), song, song.DefaultArrangementId, pager: Rendering.TextLayout.Pager),
                    GroupNames.For(slide, song), GroupNames.ColorFor(slide, song), starts.Contains(i)));
            }
            var sections = song.Sections?.Count ?? 0;
            Summary = $"{song.Slides.Count} slides in {sections} section{(sections == 1 ? "" : "s")}"
                + (arranged.Count != song.Slides.Count ? $" · {arranged.Count} played in this order" : "");
        }
        catch (Exception ex)
        {
            Summary = $"Can't preview: {ex.Message}";
        }
    }
}
