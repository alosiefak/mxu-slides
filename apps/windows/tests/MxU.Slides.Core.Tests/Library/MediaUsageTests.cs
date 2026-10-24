using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Library;

/// <summary>Ports of MediaUsageTests.swift and MediaMissingTests.referenceScanFindsEveryHome.</summary>
public sealed class MediaUsageTests : ScratchFolderTest
{
    private static MediaItem Item(string hash, double? inPoint = null)
    {
        var item = Media("m1", hash, "Background", MediaKind.Image);
        item.InPoint = inPoint;
        return item;
    }

    [Fact]
    public void TheWhereUsedListGroupsThemesFirstNamesAToZ()
    {
        var groups = MediaUsage.Groups([
            new(MediaUsage.Kind.Presentation, "p2", "Sermon"),
            new(MediaUsage.Kind.Service, "s1", "Sunday"),
            new(MediaUsage.Kind.Theme, "t1", "The Blessed Life"),
            new(MediaUsage.Kind.Presentation, "p1", "Announcements"),
        ]);
        Assert.Equal(["Themes", "Presentations", "Services"], groups.Select(g => g.Title));
        Assert.Equal(["Announcements", "Sermon"], groups[1].Uses.Select(u => u.Name));
        Assert.Empty(MediaUsage.Groups([])); // unused: no confirm, straight to choosing a file
    }

    [Fact]
    public void NamesSortLikeFinderWithNumbersAsNumbers()
    {
        var groups = MediaUsage.Groups([
            new(MediaUsage.Kind.Presentation, "a", "song 10"),
            new(MediaUsage.Kind.Presentation, "b", "Song 2"),
        ]);
        Assert.Equal(["Song 2", "song 10"], groups[0].Uses.Select(u => u.Name));
    }

    [Fact]
    public void ANewFileOrFirstFrameIsAFileChange()
    {
        Assert.True(MediaUsage.FileChanged(Item("a"), Item("b")));
        Assert.True(MediaUsage.FileChanged(Item("a"), Item("a", inPoint: 2)));
        var renamed = Item("a");
        renamed.Name = "Renamed";
        Assert.False(MediaUsage.FileChanged(Item("a"), renamed)); // a rename keeps every decoded picture
        Assert.False(MediaUsage.FileChanged(null, Item("a")));
        Assert.False(MediaUsage.FileChanged(Item("a"), null));
    }

    [Fact]
    public void TheMediaCardSaysWhatThePickIs()
    {
        Assert.Equal("Image · 1920 × 1080", MediaUsage.Detail(MediaKind.Image, 1920, 1080, null, missing: false));
        Assert.Equal("Video · 1:32 · 1280 × 720", MediaUsage.Detail(MediaKind.Video, 1280, 720, 92.4, missing: false));
        Assert.Equal("Video · 1:02:05", MediaUsage.Detail(MediaKind.Video, null, null, 3725, missing: false));
        Assert.Equal("Missing — the file isn't on this PC", MediaUsage.Detail(MediaKind.Image, 10, 10, null, missing: true));
    }

    [Fact]
    public void PresentationsOnAThemeThatUsesTheItemAreListedViaTheTheme()
    {
        MediaUsage.Document Doc(MediaUsage.Kind kind, string id, string name, string[] media, string[]? themes = null) =>
            new(new(kind, id, name), media.ToHashSet(), themes?.ToHashSet());
        var documents = new[]
        {
            Doc(MediaUsage.Kind.Theme, "t1", "Clean Geometric", ["m1"]),
            Doc(MediaUsage.Kind.Theme, "t2", "Other", ["m9"]),
            Doc(MediaUsage.Kind.Presentation, "p1", "QA Song", [], ["t1"]),
            Doc(MediaUsage.Kind.Presentation, "p2", "Own Use", ["m1"], ["t1"]),
            Doc(MediaUsage.Kind.Presentation, "p3", "Elsewhere", [], ["t2"]),
        };
        var uses = MediaUsage.Uses("m1", documents);
        Assert.Equal(new HashSet<string> { "t1", "p1", "p2" }, uses.Select(u => u.Id).ToHashSet());
        Assert.Equal("Clean Geometric", uses.First(u => u.Id == "p1").Via);
        Assert.Null(uses.First(u => u.Id == "p2").Via); // a direct use is listed once, as itself
    }

    [Fact]
    public void ReferenceScanFindsEveryHome()
    {
        var obj = new SlideObject { Id = "o1", ObjectKind = SlideObjectKind.Media, Name = "", Text = "", MediaId = "direct" };
        var filled = new SlideObject { Id = "o2", ObjectKind = SlideObjectKind.Shape, Name = "", Text = "", Fill = new ObjectFill { FillKind = FillKind.Media, MediaId = "fill" } };
        var action = new SlideAction { Id = "a1", Kind = SlideActionKind.FireMedia, MediaId = "fired" };
        var slide = new Slide
        {
            Id = "s1", Name = "", Objects = [obj, filled],
            Background = new CueMedia { MediaId = "cue" },
            BackgroundFill = new ObjectFill { FillKind = FillKind.Media, MediaId = "slideFill" },
            Actions = [action],
        };
        var presentation = new Presentation
        {
            Id = "p1", Name = "", PresentationKind = PresentationKind.Deck, ThemeId = "", Slides = [slide],
            Background = new CueMedia { MediaId = "presCue" },
            BackgroundFill = new ObjectFill { FillKind = FillKind.Media, MediaId = "presFill" },
            Sections = [new PresentationSection { Id = "sec", Name = "", Background = new CueMedia { MediaId = "sectionCue" } }],
        };
        Assert.Equal(new HashSet<string> { "direct", "fill", "fired", "cue", "slideFill", "presCue", "presFill", "sectionCue" },
            MediaReferences.Ids(presentation));

        var playlist = new Playlist
        {
            Id = "pl", Name = "",
            Entries = [new() { Id = "e1", RefKind = PlaylistRefKind.Media, RefId = "walk" }, new() { Id = "e2", RefKind = PlaylistRefKind.Audio, RefId = "song" }],
            PlaybackMode = PlaybackMode.PlayAll, CrossfadeSeconds = 0,
        };
        Assert.Equal(new HashSet<string> { "walk" }, MediaReferences.Ids(playlist));

        var service = new Service
        {
            Id = "sv", Name = "", ServiceDate = "",
            Items = [new() { Id = "i1", ItemKind = ServiceItemKind.Media, Name = "", RefId = "bumper" }, new() { Id = "i2", ItemKind = ServiceItemKind.Presentation, Name = "", RefId = "p1" }],
        };
        Assert.Equal(new HashSet<string> { "bumper" }, MediaReferences.Ids(service));

        var combo = new ActionCombo { Id = "c1", Name = "", Actions = [action] };
        Assert.Equal(new HashSet<string> { "fired" }, MediaReferences.Ids(combo));
    }

    [Fact]
    public void TimelinesAndAServicesOtherVersionsCountToo()
    {
        var presentation = new Presentation
        {
            Id = "p", Name = "", PresentationKind = PresentationKind.Song, ThemeId = "", Slides = [],
            Timelines = [new PresentationTimeline { Id = "t", Cues = [new TimelineCue { Id = "c", MediaId = "loop" }] }],
        };
        Assert.Contains("loop", MediaReferences.Ids(presentation));

        var service = new Service
        {
            Id = "sv", Name = "", ServiceDate = "",
            Items = [new() { Id = "i1", ItemKind = ServiceItemKind.Media, Name = "", RefId = "main" }],
            Versions = [new ServiceVersion
            {
                Id = "v", Name = "11:00",
                Changes = [new ServiceItemChange { ItemId = "i1", RefId = "swapped" }],
                Added = [new ServiceItem { Id = "i9", ItemKind = ServiceItemKind.Media, Name = "", RefId = "extra" }],
            }],
        };
        Assert.Equal(new HashSet<string> { "main", "swapped", "extra" }, MediaReferences.Ids(service));
    }

    [Fact]
    public void TheLibrarySaysWhereAnItemIsUsed()
    {
        var library = NewLibrary();
        library.Save(new Theme { Id = "t1", Name = "Hymns", FontFamily = "Arial", FontSize = 80, TextColorHex = "#FFFFFF", BackgroundColorHex = "#000000",
            Slides = [new Slide { Id = "ts", Name = "Lyrics", Objects = [], Background = new CueMedia { MediaId = "m1" } }] });
        library.Save(new Presentation { Id = "p1", Name = "Amazing Grace", PresentationKind = PresentationKind.Song, ThemeId = "t1", Slides = [] });
        library.Save(new Presentation { Id = "p2", Name = "Welcome", PresentationKind = PresentationKind.Deck, ThemeId = "",
            Slides = [new Slide { Id = "s", Name = "", Objects = [], Background = new CueMedia { MediaId = "m1" } }] });
        library.Save(new Service { Id = "sv", Name = "Sunday", ServiceDate = "", Items = [new() { Id = "i", ItemKind = ServiceItemKind.Media, Name = "", RefId = "m1" }] });

        var groups = MediaUsage.Groups(MediaUsage.Uses(library, "m1"));
        Assert.Equal(["Themes", "Presentations", "Services"], groups.Select(g => g.Title));
        Assert.Equal(["Amazing Grace", "Welcome"], groups[1].Uses.Select(u => u.Name));
        Assert.Equal("Hymns", groups[1].Uses[0].Via);
        Assert.Equal(3, MediaUsage.DocumentCount(MediaUsage.Documents(library), new HashSet<string> { "m1" }));
        Assert.Equal(0, MediaUsage.DocumentCount(MediaUsage.Documents(library), new HashSet<string> { "nobody" }));
    }
}
