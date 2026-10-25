using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Tests.Scene;

// The continuous view's rows (ServiceContinuousView.swift: presentRows, the card kinds, firePositions'
// arranged slides) and how a song's slides fall into its played sections.
public class ContinuousRowsTests
{
    private static Slide S(string id, string? section) => new() { Id = id, Name = id, SectionId = section };

    private static Presentation Song() => new()
    {
        Id = "song", Name = "Song", PresentationKind = PresentationKind.Song, ThemeId = "",
        Slides = [S("title", null), S("v1a", "v1"), S("v1b", "v1"), S("c1", "c"), S("v2a", "v2")],
        Sections = [new() { Id = "v1", Name = "Verse 1" }, new() { Id = "c", Name = "Chorus" }, new() { Id = "v2", Name = "Verse 2" }, new() { Id = "b", Name = "Bridge" }],
        Arrangements = [new() { Id = "sunday", Name = "Sunday", SectionIds = ["v1", "c", "b", "v2", "c"] }],
    };

    private static ServiceItem Row(string id, ServiceItemKind kind, string refId, bool hidden = false) =>
        new() { Id = id, ItemKind = kind, Name = id, RefId = refId, HiddenInPresenter = hidden ? true : null };

    [Fact]
    public void ASongFallsIntoItsSectionsAsWritten()
    {
        var sections = ContinuousRows.Sections(Song(), null);
        Assert.Equal([null, "Verse 1", "Chorus", "Verse 2"], sections.Select(s => s.Name)); // the title slide leads, unnamed
        Assert.Equal([0, 1, 3, 4], sections.Select(s => s.Start));
        Assert.Equal(["v1a", "v1b"], sections[1].Slides.Select(s => s.Id));
    }

    [Fact]
    public void AnArrangementRepeatsBlocksAndSkipsEmptyOnes()
    {
        var sections = ContinuousRows.Sections(Song(), "sunday");
        // The Bridge has no slides yet: it plays nothing, so it isn't a section here.
        Assert.Equal(["Verse 1", "Chorus", "Verse 2", "Chorus"], sections.Select(s => s.Name));
        Assert.Equal([0, 2, 3, 4], sections.Select(s => s.Start));
        Assert.Equal(5, new ContinuousRows.Card.Slides(null, Song(), "sunday", sections).SlideCount);
    }

    [Fact]
    public void TheScrollShowsTheRunOfShowWithHiddenHeadersAsRules()
    {
        var service = new Service
        {
            Id = "svc", Name = "Sunday", ServiceDate = "2026-07-19",
            Items =
            [
                Row("worship", ServiceItemKind.Header, "", hidden: true), Row("s1", ServiceItemKind.Presentation, "song"),
                Row("gone", ServiceItemKind.Presentation, "deleted"), Row("skipped", ServiceItemKind.Presentation, "song", hidden: true),
                Row("bumper", ServiceItemKind.Media, "video"), Row("walkout", ServiceItemKind.Playlist, "list"), Row("plan", ServiceItemKind.Info, ""),
            ],
        };
        var song = Song();
        song.DefaultArrangementId = "sunday";
        var cards = ContinuousRows.Cards(service, null, id => id == "song" ? song : null,
            id => id == "video" ? new MediaItem { Id = "video", Name = "Bumper", MediaKind = MediaKind.Video } : null,
            item => item.RefId == "list");
        Assert.Equal(["worship", "s1", "gone", "bumper", "walkout", "plan"], cards.Select(c => c.Item!.Id));
        Assert.False(Assert.IsType<ContinuousRows.Card.Divider>(cards[0]).ShowsName);
        var slides = Assert.IsType<ContinuousRows.Card.Slides>(cards[1]);
        Assert.Equal("sunday", slides.ArrangementId); // a row with no pick plays the song's default
        Assert.IsType<ContinuousRows.Card.Missing>(cards[2]);
        Assert.IsType<ContinuousRows.Card.Media>(cards[3]);
        Assert.IsType<ContinuousRows.Card.Music>(cards[4]);
        Assert.IsType<ContinuousRows.Card.Unlinked>(cards[5]);
    }

    [Fact]
    public void TheCardSaysWhereTheLiveSlideIs()
    {
        Assert.Equal("Slide 3 of 12", ContinuousRows.Position(12, 2));
        Assert.Equal("12 slides", ContinuousRows.Position(12, null));
        Assert.Equal("1 slide", ContinuousRows.Position(1, null));
    }
}
