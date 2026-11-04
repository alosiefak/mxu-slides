using MxU.Slides.Core.Model;
using MxU.Slides.Sync.Format;

namespace MxU.Slides.Sync.Tests.Fakes;

/// <summary>Documents the tests use, including the Mac's own test fixtures.</summary>
internal static class Samples
{
    public static readonly MacKind Presentations = MacKind.ForWindowsType(typeof(Presentation))!;
    public static readonly MacKind Services = MacKind.ForWindowsType(typeof(Service))!;
    public static readonly MacKind Playlists = MacKind.ForWindowsType(typeof(Playlist))!;
    public static readonly MacKind Themes = MacKind.ForWindowsType(typeof(Theme))!;
    public static readonly MacKind MediaItems = MacKind.ForWindowsType(typeof(MediaItem))!;

    /// <summary>
    /// <c>AutomergeDecoderEquivalenceTests.richDeck()</c> on the Mac: styled text, an effect, a
    /// rounded rectangle with a gradient, a bare media object, notes with a line break, a slide with
    /// no objects, and whole-number doubles (x: 120) next to fractional ones (y: 80.5).
    /// </summary>
    public static Presentation RichDeck()
    {
        var styled = new SlideObject
        {
            Id = "title", ObjectKind = SlideObjectKind.Text, Name = "Title", Text = "Amazing grace",
            X = 120, Y = 80.5, Width = 1680, Height = 240, Hidden = false, Opacity = 0.5, BlendMode = BlendMode.Multiply,
            Effects = [new Effect { EffectKind = EffectKind.Blur, Radius = 4 }],
            TextStyle = new TextStyle
            {
                FontName = "Georgia", FontSize = 72,
                Fill = new ObjectFill { FillKind = FillKind.Solid, ColorHex = "#FFFFFFFF" },
                HorizontalAlignment = TextHorizontalAlignment.Center,
            },
        };
        var shape = new SlideObject
        {
            Id = "plate", ObjectKind = SlideObjectKind.Shape, Name = "Plate", Text = "",
            ShapeKind = ShapeKind.RoundedRectangle, CornerRadius = 12,
            Fill = new ObjectFill { FillKind = FillKind.LinearGradient, GradientAngleDegrees = 90 },
        };
        var bare = new SlideObject { Id = "bg", ObjectKind = SlideObjectKind.Media, Name = "Background", Text = "" };
        return new Presentation
        {
            Id = "rich", Name = "Rich", PresentationKind = PresentationKind.Deck, ThemeId = "theme-1", Folder = "Songs",
            Slides =
            [
                new Slide { Id = "s1", Name = "One", Objects = [styled, bare], Notes = "Speaker notes\nline two" },
                new Slide { Id = "s2", Name = "Two", Objects = [shape], KeepWords = true },
                new Slide { Id = "s3", Name = "Three", Objects = [] },
            ],
            CanvasWidth = 1920, CanvasHeight = 1080,
        };
    }

    /// <summary>A song with sections, an arrangement (a list of section ids) and five lyric slides.</summary>
    public static Presentation Song(string id = "song", string name = "Amazing Grace")
    {
        var sections = new List<PresentationSection>
        {
            new() { Id = "v1", Name = "Verse 1" },
            new() { Id = "c", Name = "Chorus", ColorHex = "#3B82F6FF" },
            new() { Id = "v2", Name = "Verse 2" },
        };
        return new Presentation
        {
            Id = id, Name = name, PresentationKind = PresentationKind.Song, ThemeId = "theme-1",
            Sections = sections,
            Arrangements = [new Arrangement { Id = "a1", Name = "Default", SectionIds = ["v1", "c", "v2", "c"] }],
            Slides = Enumerable.Range(1, 5).Select(n => new Slide
            {
                Id = $"slide-{n}", Name = $"Slide {n}", SectionId = n <= 2 ? "v1" : n <= 4 ? "c" : "v2",
                Objects = [new SlideObject { Id = $"lyric-{n}", ObjectKind = SlideObjectKind.Text, Name = "Lyrics", Text = $"Line {n}a\nLine {n}b" }],
            }).ToList(),
        };
    }

    public static Service Sunday(string id = "sunday") => new()
    {
        Id = id, Name = "Sunday", ServiceDate = "2026-10-25",
        Items =
        [
            new ServiceItem { Id = "i1", ItemKind = ServiceItemKind.Presentation, Name = "Amazing Grace", RefId = "song" },
            new ServiceItem { Id = "i2", ItemKind = ServiceItemKind.Header, Name = "Sermon", RefId = "" },
        ],
    };
}
