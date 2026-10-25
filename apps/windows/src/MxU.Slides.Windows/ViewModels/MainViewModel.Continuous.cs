using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.Rendering;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>One section of a song in the continuous view: its name, its colour and its slides.</summary>
public sealed class ContinuousSectionViewModel(string? name, Brush brush, IReadOnlyList<SlideTileViewModel> tiles)
{
    public string? Name { get; } = name;
    public bool HasName => !string.IsNullOrEmpty(Name);
    public Brush Brush { get; } = brush;
    public IReadOnlyList<SlideTileViewModel> Tiles { get; } = tiles;
}

/// <summary>One card in the continuous view: a header's rule, a song with its sections, a video, music or a plan row.</summary>
public sealed partial class ContinuousCardViewModel : ObservableObject
{
    public ContinuousCardViewModel(ContinuousRows.Card card, string title, Brush? accent)
    {
        Card = card;
        Title = title;
        Accent = accent;
        if (card is ContinuousRows.Card.Slides slides)
        {
            Presentation = slides.Presentation;
            ArrangementId = slides.ArrangementId;
            ArrangementName = Core.Lyrics.ArrangementEditing.Find(slides.Presentation, slides.ArrangementId)?.Name;
            Position = ContinuousRows.Position(slides.SlideCount, null);
        }
    }

    public ContinuousRows.Card Card { get; }

    /// <summary>The service row it shows (null for a presentation opened from the library).</summary>
    public string? ItemId => Card.Item?.Id;

    public string Title { get; }
    public Brush? Accent { get; }

    public bool IsDivider => Card is ContinuousRows.Card.Divider;
    public bool ShowsDividerName => Card is ContinuousRows.Card.Divider { ShowsName: true };
    public bool IsSlides => Card is ContinuousRows.Card.Slides;
    public bool IsMedia => Card is ContinuousRows.Card.Media;
    public bool IsMusic => Card is ContinuousRows.Card.Music;

    /// <summary>A plan row with nothing to play, or a row whose document has gone.</summary>
    public bool IsPlaceholder => Card is ContinuousRows.Card.Unlinked or ContinuousRows.Card.Missing;

    public string PlaceholderText => Card is ContinuousRows.Card.Unlinked ? "Not Linked" : "Missing from library";

    public string Glyph => Card switch
    {
        ContinuousRows.Card.Media => "",
        ContinuousRows.Card.Music => "",
        _ => "",
    };

    public Presentation? Presentation { get; }
    public string? ArrangementId { get; }
    public string? ArrangementName { get; }

    /// <summary>The song's sections, filled in just after the card appears (so a long service opens at once).</summary>
    public ObservableCollection<ContinuousSectionViewModel> Sections { get; } = [];

    /// <summary>A video or image row's picture.</summary>
    public RenderScene? MediaPreview { get; init; }

    /// <summary>"Slide 3 of 12" while live, else "12 slides".</summary>
    [ObservableProperty]
    public partial string Position { get; set; } = "";

    [ObservableProperty]
    public partial bool IsLive { get; set; }

    /// <summary>Every slide tile on the card.</summary>
    public IEnumerable<SlideTileViewModel> Tiles => Sections.SelectMany(s => s.Tiles);
}

/// <summary>
/// The continuous view (Phase 8): instead of one presentation's grid, the whole service as one
/// long scroll, each row a card and each song's slides in their sections (the Mac's
/// ServiceContinuousView). Clicking a slide takes it live from its row, exactly as the grid does,
/// and Next walks on from there. The cards are Core's (<see cref="ContinuousRows"/>); the view is
/// Views/ContinuousSlidesView.xaml. A song opened from the library shows on its own card.
/// </summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<ContinuousCardViewModel> ContinuousCards { get; } = [];

    /// <summary>The slide area shows the continuous view (a per-PC choice, kept in settings).</summary>
    public bool ShowsContinuous
    {
        get => Settings.ContinuousView;
        set
        {
            if (Settings.ContinuousView == value) return;
            Settings.ContinuousView = value;
            Settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(ContinuousActive));
            RefreshContinuous();
        }
    }

    /// <summary>The cards are kept up to date: the continuous view is showing, or volunteer mode (which always uses it).</summary>
    public bool ContinuousActive => ShowsContinuous || IsVolunteer;

    /// <summary>Raised when the view should scroll to a row's card (a row picked in the service list), or the live slide.</summary>
    public event Action<string?>? ContinuousFocusRequested;

    /// <summary>Raised when a different slide went live: the view keeps it in sight.</summary>
    public event Action? ContinuousLiveMoved;

    private DispatcherTimer? continuousRefresh;
    private int continuousGeneration;
    private bool continuousWired;
    private bool continuousForce;
    private string? continuousSignature;

    /// <summary>
    /// Rebuilds the cards soon (changes come in bursts: an import saves many documents). Only a
    /// change to the rows themselves rebuilds them all (<paramref name="force"/>, or a different
    /// set of rows); a song edited in place redraws its own card (<see cref="RefreshContinuousCard"/>).
    /// </summary>
    public void RefreshContinuous(bool force = false)
    {
        WireContinuous();
        if (!ContinuousActive)
        {
            if (ContinuousCards.Count > 0) ContinuousCards.Clear();
            continuousSignature = null;
            return;
        }
        continuousForce |= force;
        continuousRefresh ??= new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) =>
        {
            continuousRefresh!.Stop();
            var forced = continuousForce;
            continuousForce = false;
            BuildContinuous(forced);
        }, Dispatcher.CurrentDispatcher);
        continuousRefresh.Stop();
        continuousRefresh.Start();
    }

    /// <summary>Follows what changes the cards: the service's rows, the library's documents, and the row picked.</summary>
    private void WireContinuous()
    {
        if (continuousWired) return;
        continuousWired = true;
        ServiceItems.CollectionChanged += (_, _) => { if (ContinuousActive) RefreshContinuous(); };
        Library.Changed += (type, id) =>
        {
            if (!ContinuousActive) return;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (type == typeof(Presentation)) RefreshContinuousCard(id);
                else if (type == typeof(Theme) || type == typeof(MediaItem)) RefreshContinuous(force: true);
                else if (type == typeof(Service)) RefreshContinuous();
            });
        };
        PropertyChanged += (_, e) =>
        {
            if (!ContinuousActive) return;
            if (e.PropertyName == nameof(SelectedServiceItem) && !suppressContinuousFocus) ContinuousFocusRequested?.Invoke(SelectedServiceItem?.Item.Id);
            // A library song opened on its own, or back to the service (the same rows rebuild nothing).
            if (e.PropertyName is nameof(CurrentPresentation) or nameof(SelectedService) or nameof(SelectedArrangement)) RefreshContinuous();
        };
    }

    /// <summary>The cards the view should show now: the selected service's rows, or the open presentation alone when it came from the library.</summary>
    private List<ContinuousRows.Card> ContinuousSource()
    {
        if (SelectedService is { } service && (CurrentContextItemId is not null || CurrentPresentation is null))
            return ContinuousRows.Cards(service, PlanTimeId(service), Library.Get<Presentation>, Library.Get<MediaItem>,
                item => item.ItemKind == ServiceItemKind.Audio ? Library.Get<AudioItem>(item.RefId) is not null : Library.Get<Playlist>(item.RefId) is not null);
        return CurrentPresentation is { } open ? [ContinuousRows.Single(open, SelectedArrangement?.Id)] : [];
    }

    /// <summary>What the cards are made of: their rows, kinds, songs, arrangements and slides (the same means nothing to redraw).</summary>
    private static string Signature(IEnumerable<ContinuousRows.Card> cards) => string.Join(Environment.NewLine, cards.Select(card => card switch
    {
        ContinuousRows.Card.Slides s => $"S|{s.Item?.Id}|{s.Presentation.Id}|{s.ArrangementId}|{string.Join(",", s.Sections.SelectMany(x => x.Slides).Select(x => x.Id))}",
        ContinuousRows.Card.Divider d => $"D|{d.Header.Id}|{d.Header.Name}|{d.Header.ColorHex}|{d.ShowsName}",
        _ => $"{card.GetType().Name}|{card.Item?.Id}|{card.Item?.RefId}|{card.Item?.Name}",
    }));

    private void BuildContinuous(bool force)
    {
        var cards = ContinuousSource();
        var signature = Signature(cards);
        if (!force && signature == continuousSignature)
        {
            MarkContinuousTiles();
            return;
        }
        continuousSignature = signature;
        var generation = ++continuousGeneration;
        ContinuousCards.Clear();
        var dispatcher = Dispatcher.CurrentDispatcher;
        foreach (var card in cards)
        {
            var view = CardView(card);
            ContinuousCards.Add(view);
            if (card is not ContinuousRows.Card.Slides slides) continue;
            // The slides come in card by card at background priority: a long service shows its cards
            // at once and its thumbnails a moment later, without holding up a click.
            dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (generation != continuousGeneration) return;
                FillCardSafely(view, slides);
            });
        }
        MarkContinuousTiles();
    }

    private ContinuousCardViewModel CardView(ContinuousRows.Card card) =>
        new(card, CardTitle(card), card is ContinuousRows.Card.Divider d ? Painting.Brush(d.Header.ColorHex, new SceneColor(0.64, 0.64, 0.64)) : null)
        {
            MediaPreview = card is ContinuousRows.Card.Media m ? MediaTileViewModel.PreviewScene(m.MediaItem) : null,
        };

    private void FillCardSafely(ContinuousCardViewModel view, ContinuousRows.Card.Slides slides)
    {
        try { FillCard(view, slides); }
        catch (Exception ex) { Log.Warn($"Continuous view: couldn't draw \"{view.Title}\": {ex.Message}"); }
        MarkContinuousCard(view);
    }

    /// <summary>A song was saved: its cards redraw with what it says now (the rest of the scroll stays as it is).</summary>
    private void RefreshContinuousCard(string presentationId)
    {
        var fresh = ContinuousSource();
        if (Signature(fresh) != continuousSignature)
        {
            RefreshContinuous();
            return;
        }
        for (var i = 0; i < ContinuousCards.Count && i < fresh.Count; i++)
        {
            if (fresh[i] is not ContinuousRows.Card.Slides slides || slides.Presentation.Id != presentationId) continue;
            var view = CardView(slides);
            FillCardSafely(view, slides);
            ContinuousCards[i] = view;
        }
    }

    private string CardTitle(ContinuousRows.Card card) => card switch
    {
        ContinuousRows.Card.Slides { Item: null } slides => slides.Presentation.Name,
        ContinuousRows.Card.Slides { Item: { } row } => Library.Get<Presentation>(row.RefId)?.Name ?? row.Name,
        ContinuousRows.Card.Media media => media.MediaItem.Name,
        _ => card.Item?.Name ?? "",
    };

    private void FillCard(ContinuousCardViewModel view, ContinuousRows.Card.Slides card)
    {
        var p = card.Presentation;
        foreach (var section in card.Sections)
        {
            var tiles = new List<SlideTileViewModel>();
            for (var i = 0; i < section.Slides.Count; i++)
            {
                var slide = section.Slides[i];
                var scene = SlideSceneBuilder.Scene(slide, ThemeFor(slide, p), p, card.ArrangementId, pager: TextLayout.Pager);
                tiles.Add(new SlideTileViewModel(section.Start + i, slide, scene, GroupNames.For(slide, p), GroupNames.ColorFor(slide, p), i == 0));
            }
            var colour = section.SectionId is { } id ? StarterContent.GroupColor(section.Name, p.Sections?.FirstOrDefault(s => s.Id == id)?.ColorHex) : "#5C5C5CFF";
            view.Sections.Add(new ContinuousSectionViewModel(section.Name, Painting.Brush(colour, new SceneColor(0.36, 0.36, 0.36)), tiles));
        }
    }

    /// <summary>A slide clicked in the continuous view: live from its row (or, for a library song, on its own).</summary>
    public void FireContinuous(ContinuousCardViewModel card, SlideTileViewModel tile)
    {
        if (card.Presentation is not { } p) return;
        // The grid follows the row, as Next does, so the header and its arrangement chip are that song's.
        if (card.ItemId is { } itemId && SelectedServiceItem?.Item.Id != itemId
            && ServiceItems.FirstOrDefault(r => r.Item.Id == itemId) is { } row)
        {
            suppressContinuousFocus = true;
            try { SelectedServiceItem = row; }
            finally { suppressContinuousFocus = false; }
        }
        Fire(p, card.ArrangementId, tile.Index, card.ItemId);
    }

    /// <summary>A media, music or plan row's card clicked: the row fires as it would from the service list.</summary>
    public void FireContinuousRow(ContinuousCardViewModel card)
    {
        if (card.Card.Item is not { } item || item.ItemKind == ServiceItemKind.Header) return;
        if (ServiceItems.FirstOrDefault(r => r.Item.Id == item.Id) is { } row) FireServiceItemCommand.Execute(row);
    }

    private bool suppressContinuousFocus;

    /// <summary>
    /// Called with the grid's live marks (MarkLiveTiles in MainViewModel.Live.cs), whatever took
    /// something live (a click, Next, MIDI, a remote): a live row inside a collapsed header opens
    /// its group, and the continuous view marks the live slide.
    /// </summary>
    private void FollowLiveInViews()
    {
        // Only when a different row goes live: a group collapsed by hand while its row is up stays collapsed.
        var liveRow = ServiceItems.FirstOrDefault(r => r.IsLive);
        if (liveRow is { IsCollapsedAway: true } && liveRow.Item.Id != followedLiveRow) RevealServiceRow(liveRow.Item.Id);
        followedLiveRow = liveRow?.Item.Id;
        MarkContinuousTiles();
    }

    private string? followedLiveRow;

    /// <summary>Marks the live slide and its card.</summary>
    private void MarkContinuousTiles()
    {
        if (ContinuousCards.Count == 0) return;
        foreach (var card in ContinuousCards) MarkContinuousCard(card);
        ContinuousLiveMoved?.Invoke();
    }

    private void MarkContinuousCard(ContinuousCardViewModel card)
    {
        var live = Live is { } context && Show.Live is not null && card.Presentation is { } p && context.PresentationId == p.Id
            && context.ItemId == card.ItemId && context.ArrangementId == card.ArrangementId ? context : null;
        foreach (var tile in card.Tiles) tile.IsLive = live is not null && tile.Index == live.Occurrence;
        if (card.Card is ContinuousRows.Card.Slides slides) card.Position = ContinuousRows.Position(slides.SlideCount, live?.Occurrence);
        card.IsLive = live is not null
            || card.ItemId is { } id && ServiceItems.FirstOrDefault(r => r.Item.Id == id)?.IsLive == true;
    }
}
