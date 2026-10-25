using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using static MxU.Slides.Core.Library.ServiceLinkLogic;

namespace MxU.Slides.Core.Tests.Library;

// Port of the Mac's ServiceLinkLogicTests.swift.
public sealed class ServiceLinkLogicTests
{
    private static ServiceItem InfoItem(string name, string? songTitle = null, int? ccli = null) => new()
    {
        Id = "item-1", ItemKind = ServiceItemKind.Info, Name = name, RefId = "",
        MxuItemHexId = "mxu-1", MxuSongTitle = songTitle, MxuCcliNumber = ccli,
    };

    private static ServiceLinkRules Rules(params ServiceLinkRule[] list) => new() { Id = RulesId, Rules = [.. list] };

    private static Resolution Link(string refId, ServiceItemKind kind = ServiceItemKind.Presentation) => new Resolution.Link(kind, refId);

    [Fact]
    public void SongKeyAppliesOnceToAChordedLinkedSong()
    {
        var row = new ServiceItem { Id = "r", ItemKind = ServiceItemKind.Presentation, Name = "Way Maker", RefId = "deck", MxuSongKey = "A" };
        Assert.Equal("A", PendingSongKey(row));
        row.MxuSongKeyApplied = "A";
        Assert.Null(PendingSongKey(row)); // applied: the operator's own pick is left alone
        row.MxuSongKey = "B";
        Assert.Null(PendingSongKey(row)); // the key moved: the pick stays
        var unlinked = new ServiceItem { Id = "r", ItemKind = ServiceItemKind.Info, Name = "Way Maker", RefId = "", MxuSongKey = "A" };
        Assert.Null(PendingSongKey(unlinked));
        var keyless = new ServiceItem { Id = "r", ItemKind = ServiceItemKind.Presentation, Name = "Way Maker", RefId = "deck" };
        Assert.Null(PendingSongKey(keyless));

        Presentation Chorded(bool chords = true, string? key = "G") => new()
        {
            Id = "deck", Name = "Way Maker", PresentationKind = PresentationKind.Song, ThemeId = "t", MusicKey = key,
            Slides = [new Slide { Id = "s", Name = "", Objects = [new SlideObject
            {
                Id = "o", ObjectKind = SlideObjectKind.Text, Name = "", Text = "Way maker",
                Chords = chords ? [new ChordPlacement { Line = 0, Column = 0, Symbol = "G" }] : null,
            }] }],
        };
        Assert.Equal(new SongKeyUpdate("B", "B"), SongKeyUpdateFor("B", Chorded()));
        // The written key (by relative) stores as null, the header's Original rule.
        Assert.Equal(new SongKeyUpdate(null, "Em"), SongKeyUpdateFor("Em", Chorded()));
        Assert.Null(SongKeyUpdateFor("B", Chorded(key: null))); // no written key: can't transpose
        Assert.Null(SongKeyUpdateFor("B", Chorded(chords: false))); // no chords: nothing to set
    }

    [Fact]
    public void NormalizeStripsCaseAndPunctuation()
    {
        Assert.Equal("way maker", Normalize("  Way Maker!  "));
        Assert.Equal("way maker", Normalize("WAY   MAKER"));
        Assert.Equal("way maker live", Normalize("Way-Maker (Live)"));
    }

    [Fact]
    public void StickyRuleWinsButOnlyWithinItsServiceType()
    {
        var ruleSet = Rules(new ServiceLinkRule
        {
            Id = "r1", NormalizedName = "way maker", ServiceTypeName = "Weekend", ItemKind = ServiceItemKind.Presentation, RefId = "weekend",
        });
        Candidate[] candidates = [new("by-ccli", "Way Maker", CcliNumber: 7)];

        Assert.Equal(Link("weekend"), Resolve(InfoItem("Way Maker!", "Way Maker", 7), "Weekend", ruleSet, candidates));
        Assert.Equal(Link("by-ccli"), Resolve(InfoItem("Way Maker", "Way Maker", 7), "Youth", ruleSet, candidates));
        Assert.Equal(Resolution.NoLink, Resolve(InfoItem("Way Maker"), "Youth", ruleSet, candidates));
    }

    [Fact]
    public void CcliExactMatchLinksOnlyWhenUnambiguous()
    {
        Candidate[] one = [new("p1", "Different Name", CcliNumber: 7_115_744)];
        Assert.Equal(Link("p1"), Resolve(InfoItem("Way Maker", "Way Maker", 7_115_744), null, Rules(), one));

        Candidate[] two = [.. one, new("p2", "Way Maker Alt", CcliNumber: 7_115_744)];
        Assert.Equal(Resolution.NoLink, Resolve(InfoItem("Way Maker", "Way Maker", 7_115_744), null, Rules(), two));
    }

    [Fact]
    public void TitleMatchesNameOrCcliSongTitleUnambiguously()
    {
        Candidate[] candidates = [new("p1", "Way Maker (Live)", CcliSongTitle: "Way Maker"), new("p2", "Great Are You Lord")];
        Assert.Equal(Link("p1"), Resolve(InfoItem("WAY MAKER", "Way Maker"), null, Rules(), candidates));

        Candidate[] ambiguous = [.. candidates, new("p3", "Way Maker")];
        Assert.Equal(Resolution.NoLink, Resolve(InfoItem("Way Maker", "Way Maker"), null, Rules(), ambiguous));
    }

    [Fact]
    public void NonSongRowsNeverAutoMatchByTitle()
    {
        Candidate[] candidates = [new("p1", "Announcements")];
        Assert.Equal(Resolution.NoLink, Resolve(InfoItem("Announcements"), null, Rules(), candidates));
    }

    [Fact]
    public void SuggestionsNameMatchNonSongRowsButNotOnSharedWordsAlone()
    {
        Candidate[] candidates = [new("p1", "Song of Ascent"), new("p2", "One Thing Remains"), new("ann", "Announcements")];
        Assert.Empty(Suggestions(InfoItem("Song One"), null, Rules(), candidates));
        Assert.Equal(["ann"], Suggestions(InfoItem("Announcements"), null, Rules(), candidates).Select(c => c.Id));

        var ruleSet = Rules(new ServiceLinkRule
        {
            Id = "r1", NormalizedName = "song one", ServiceTypeName = null, ItemKind = ServiceItemKind.Presentation, RefId = "p2",
        });
        Assert.Equal(["p2"], Suggestions(InfoItem("Song One"), null, ruleSet, candidates).Select(c => c.Id));
    }

    [Fact]
    public void SuggestionsDropSharedWordOnlyHitsButKeepPrefixAndCcli()
    {
        Candidate[] candidates = [new("live", "Way Maker (Live)"), new("other", "Make Way"), new("ccli", "Different Title", CcliNumber: 7115744)];
        var ranked = Suggestions(InfoItem("Way Maker", "Way Maker", 7115744), null, Rules(), candidates);
        Assert.Equal(["ccli", "live"], ranked.Select(c => c.Id));
    }

    [Fact]
    public void StampRuleUpdatesInPlaceAndRemoveRuleDeletes()
    {
        var ruleSet = Rules();
        StampRule(ruleSet, "Way Maker!", "Weekend", ServiceItemKind.Presentation, "p1", () => "r1");
        StampRule(ruleSet, "way maker", "Weekend", ServiceItemKind.Media, "m1", () => "r2");

        Assert.Single(ruleSet.Rules);
        Assert.Equal("m1", ruleSet.Rules[0].RefId);
        Assert.Equal(ServiceItemKind.Media, ruleSet.Rules[0].ItemKind);

        StampRule(ruleSet, "Way Maker", "Youth", ServiceItemKind.Presentation, "p9", () => "r3");
        RemoveRule(ruleSet, "Way Maker", "Weekend");
        Assert.Equal(["Youth"], ruleSet.Rules.Select(r => r.ServiceTypeName));
    }

    [Fact]
    public void MergedKeepsTheTeamsRulesAndOwnRulesForOtherNames()
    {
        ServiceLinkRule Rule(string id, string name, string? type = null) =>
            new() { Id = id, NormalizedName = name, ServiceTypeName = type, ItemKind = ServiceItemKind.Presentation, RefId = id };
        var merged = Merged([Rule("t1", "way maker")], [Rule("o1", "way maker"), Rule("o2", "gratitude")]);
        Assert.Equal(TeamRulesId, merged.Id);
        Assert.Equal(["t1", "o2"], merged.Rules.Select(r => r.Id));
    }

    [Fact]
    public void MatchArrangementByNormalizedName()
    {
        Arrangement[] arrangements = [new() { Id = "a1", Name = "Default" }, new() { Id = "a2", Name = "Full Band" }];
        Assert.Equal("a2", MatchArrangement("full band", arrangements));
        Assert.Null(MatchArrangement("Acoustic", arrangements));
        Assert.Null(MatchArrangement(null, arrangements));
    }
}
