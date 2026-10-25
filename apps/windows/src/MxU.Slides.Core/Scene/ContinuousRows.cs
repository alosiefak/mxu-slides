using MxU.Slides.Core.Library;
using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Scene;

/// <summary>
/// The continuous view: the whole service as one long scroll, a card per row, each song's slides
/// in its sections, instead of one presentation at a time. What it lists, and how a song's slides
/// fall into sections. Port of the Mac's ServiceContinuousView.swift (presentRows, firePositions,
/// the card kinds); the drawing is the Windows app's (Views/ContinuousSlidesView.cs).
/// </summary>
public static class ContinuousRows
{
    /// <summary>One card in the scroll.</summary>
    public abstract record Card(ServiceItem? Item)
    {
        /// <summary>A header row: its name over a rule (a hidden header shows the rule alone).</summary>
        public sealed record Divider(ServiceItem Header) : Card(Header)
        {
            public bool ShowsName => !Header.IsHidden();
        }

        /// <summary>A song or presentation, with its slides in sections. <see cref="Card.Item"/> is null for one opened from the library.</summary>
        public sealed record Slides(ServiceItem? Row, Presentation Presentation, string? ArrangementId, IReadOnlyList<Section> Sections) : Card(Row)
        {
            public int SlideCount => Sections.Sum(s => s.Slides.Count);
        }

        public sealed record Media(ServiceItem Row, MediaItem MediaItem) : Card(Row);

        /// <summary>A song file or playlist: it plays when fired.</summary>
        public sealed record Music(ServiceItem Row) : Card(Row);

        /// <summary>A plan row with nothing to play yet (Planning Center's "Not Linked").</summary>
        public sealed record Unlinked(ServiceItem Row) : Card(Row);

        /// <summary>A row whose document has gone from the library.</summary>
        public sealed record Missing(ServiceItem Row) : Card(Row);
    }

    /// <summary>
    /// One run of slides: a section block as played (a chorus played twice is two blocks), with the
    /// position of its first slide in the arranged slides. Slides before any section have no name.
    /// </summary>
    public sealed record Section(string? SectionId, string? Name, int Start, IReadOnlyList<Slide> Slides);

    /// <summary>
    /// The rows the scroll shows, in the order this PC runs them: every row the plan time keeps,
    /// hidden ones left out, except hidden headers, which stay as a bare rule so the groups still
    /// read (the Mac's presentRows).
    /// </summary>
    public static List<ServiceItem> PresentRows(Service service, string? timeHexId = null) =>
        [.. ServicePlanTimes.Rows(service, timeHexId).Where(i => !i.IsHidden() || i.ItemKind == ServiceItemKind.Header)];

    /// <summary>The arrangement a row plays: its own pick, else its song's default.</summary>
    public static string? ArrangementFor(ServiceItem? item, Presentation presentation) => item?.ArrangementId ?? presentation.DefaultArrangementId;

    /// <summary>A song's arranged slides cut into its played blocks.</summary>
    public static List<Section> Sections(Presentation presentation, string? arrangementId)
    {
        var slides = SlideSceneBuilder.ArrangedSlides(presentation, arrangementId);
        var starts = SlideSceneBuilder.ArrangementBlockStarts(presentation, arrangementId);
        var arrangement = ArrangementEditing.Find(presentation, arrangementId);
        var result = new List<Section>();
        // Slides ahead of the first section (a title slide, say) are a block of their own with no name.
        var first = starts.Count == 0 ? slides.Count : starts[0];
        if (first > 0) result.Add(new Section(null, null, 0, [.. slides.Take(first)]));
        for (var i = 0; i < starts.Count; i++)
        {
            var start = starts[i];
            var end = i + 1 < starts.Count ? starts[i + 1] : slides.Count;
            // An arrangement names its blocks itself (an empty one still shows its pill); the slide order reads them off the slides.
            var sectionId = arrangement is not null && i < arrangement.SectionIds.Count ? arrangement.SectionIds[i]
                : start < slides.Count ? slides[start].SectionId : null;
            var name = sectionId is null ? null : presentation.Sections?.FirstOrDefault(s => s.Id == sectionId)?.Name ?? ArrangementEditing.DeletedSection;
            if (end <= start) continue; // a section with no slides shows nothing here
            result.Add(new Section(sectionId, name, start, [.. slides.Skip(start).Take(end - start)]));
        }
        return result;
    }

    /// <summary>The cards for a service, looking each row's document up in the library.</summary>
    public static List<Card> Cards(Service service, string? timeHexId, Func<string, Presentation?> presentation, Func<string, MediaItem?> media,
        Func<ServiceItem, bool> musicExists)
    {
        var cards = new List<Card>();
        foreach (var item in PresentRows(service, timeHexId))
        {
            switch (item.ItemKind)
            {
                case ServiceItemKind.Header:
                    cards.Add(new Card.Divider(item));
                    break;
                case ServiceItemKind.Presentation when presentation(item.RefId) is { } p:
                    var arrangementId = ArrangementFor(item, p);
                    cards.Add(new Card.Slides(item, p, arrangementId, Sections(p, arrangementId)));
                    break;
                case ServiceItemKind.Media when media(item.RefId) is { } m:
                    cards.Add(new Card.Media(item, m));
                    break;
                case ServiceItemKind.Audio or ServiceItemKind.Playlist when musicExists(item):
                    cards.Add(new Card.Music(item));
                    break;
                case ServiceItemKind.Info:
                    cards.Add(new Card.Unlinked(item));
                    break;
                default:
                    cards.Add(new Card.Missing(item));
                    break;
            }
        }
        return cards;
    }

    /// <summary>One presentation opened from the library, as a single card.</summary>
    public static Card.Slides Single(Presentation presentation, string? arrangementId) =>
        new(null, presentation, arrangementId, Sections(presentation, arrangementId));

    /// <summary>"Slide 3 of 12" on the live card, "12 slides" (or "1 slide") on the others (the Mac's CardSlidePosition).</summary>
    public static string Position(int slideCount, int? liveOccurrence) =>
        liveOccurrence is { } live ? $"Slide {live + 1} of {slideCount}" : slideCount == 1 ? "1 slide" : $"{slideCount} slides";
}
