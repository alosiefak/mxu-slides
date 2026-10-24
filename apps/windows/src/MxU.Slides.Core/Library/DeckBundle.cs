using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Library;

/// <summary>
/// A presentation with everything it needs to be drawn somewhere else: the themes its slides
/// use and the media (and music) they show, plus which of those the library doesn't have.
/// Port of the Mac's DeckBundle. <see cref="Load"/> takes copies, so the bundle can be written
/// out on another thread while the operator keeps editing.
/// </summary>
public sealed class DeckBundle(Presentation presentation)
{
    public Presentation Presentation { get; } = presentation;

    public Dictionary<string, Theme> Themes { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, MediaItem> Media { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, AudioItem> Audio { get; } = new(StringComparer.Ordinal);

    public HashSet<string> MissingThemes { get; } = new(StringComparer.Ordinal);

    public HashSet<string> MissingMedia { get; } = new(StringComparer.Ordinal);

    /// <summary>The theme a slide is drawn with: none if it's marked unthemed, its own if it has one, else the presentation's.</summary>
    public static string ThemeIdFor(Presentation presentation, Slide slide) =>
        slide.Unthemed == true ? "" : slide.ThemeId is { Length: > 0 } own ? own : presentation.ThemeId;

    public static HashSet<string> ThemeIds(Presentation presentation)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal) { presentation.ThemeId };
        foreach (var slide in presentation.Slides) ids.Add(ThemeIdFor(presentation, slide));
        ids.Remove("");
        return ids;
    }

    /// <summary>The media the presentation and its themes use; built-in sources ("input::item::…") aren't library media.</summary>
    public static HashSet<string> MediaIds(Presentation presentation, IEnumerable<Theme> themes)
    {
        var ids = MediaReferences.Ids(presentation);
        foreach (var theme in themes) ids.UnionWith(MediaReferences.Ids(theme));
        ids.RemoveWhere(id => id.Contains("::", StringComparison.Ordinal));
        return ids;
    }

    /// <summary>
    /// Copies of a presentation, its themes and its media, or null when there's no such
    /// presentation. A media id the library has no image or video for may be music (a slide can
    /// put a song on the media layers); it's looked up as an audio item, as the Mac does.
    /// </summary>
    public static DeckBundle? Load(LibraryStore library, string id)
    {
        if (library.Get<Presentation>(id) is not { } deck) return null;
        var bundle = new DeckBundle(MxuJson.Clone(deck));
        foreach (var themeId in ThemeIds(bundle.Presentation))
        {
            if (library.Get<Theme>(themeId) is { } theme) bundle.Themes[themeId] = MxuJson.Clone(theme);
            else bundle.MissingThemes.Add(themeId);
        }
        foreach (var mediaId in MediaIds(bundle.Presentation, bundle.Themes.Values))
        {
            if (library.Get<MediaItem>(mediaId) is { } item) bundle.Media[mediaId] = MxuJson.Clone(item);
            else if (library.Get<AudioItem>(mediaId) is { } audio) bundle.Audio[mediaId] = MxuJson.Clone(audio);
            else bundle.MissingMedia.Add(mediaId);
        }
        return bundle;
    }
}
