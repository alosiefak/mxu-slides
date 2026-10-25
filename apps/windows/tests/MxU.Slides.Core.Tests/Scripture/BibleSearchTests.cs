using MxU.Slides.Core.Scripture;

namespace MxU.Slides.Core.Tests.Scripture;

public class BibleSearchTests
{
    private static IReadOnlyList<SearchHit> Search(string translation, string query, int limit = 20) =>
        BibleCatalog.BuiltIn.Index(translation).Search(query, limit, TestContext.Current.CancellationToken);

    private static string First(string translation, string query) => Search(translation, query).FirstOrDefault()?.Verse.Ref.ToString() ?? "(nothing)";

    [Fact]
    public void ThePhraseItselfComesFirst()
    {
        Assert.Equal("1CO 13:4", First("web", "love is patient"));
        Assert.Equal("JOH 3:16", First("kjv", "God so loved the world"));
        Assert.Equal("PSA 23:1", First("kjv", "the lord is my shepherd"));
        Assert.Equal("PSA 23:1", First("asv", "Jehovah is my shepherd"));
        Assert.Equal("JOH 11:35", First("kjv", "Jesus wept"));
    }

    [Fact]
    public void OldAndNewFormsOfAWordMeet()
    {
        Assert.Equal("lov", BibleSearch.Stem("loved"));
        Assert.Equal("lov", BibleSearch.Stem("loveth"));
        Assert.Equal("lov", BibleSearch.Stem("loving"));
        Assert.Equal("lov", BibleSearch.Stem("love"));
        Assert.Equal(BibleSearch.Stem("believeth"), BibleSearch.Stem("believes"));
        Assert.Equal("is", BibleSearch.Stem("is"));
        Assert.Equal("bless", BibleSearch.Stem("bless"));
        // A modern query finds the KJV's wording.
        Assert.Equal("JOH 3:16", First("kjv", "God so loves the world"));
    }

    [Fact]
    public void RareWordsCountMoreThanCommonOnes()
    {
        // Every hit has "shepherd": "the" and "is" alone never make a verse a candidate.
        var hits = Search("kjv", "the shepherd is");
        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.Contains("shepherd", h.Verse.Text, StringComparison.OrdinalIgnoreCase));
        // All the words beat some of them.
        var some = Search("web", "faith hope love", 50);
        Assert.Equal("1CO 13:13", some[0].Verse.Ref.ToString());
    }

    [Fact]
    public void TheLastWordCountsAsABeginningWhileItIsTyped()
    {
        Assert.Equal("1CO 13:4", First("web", "love is pati"));
        Assert.Equal("JOH 11:35", First("kjv", "jesus wep"));
        // Once a space follows, the word is whole: "wep" is no word, so nothing is found.
        Assert.NotEmpty(Search("kjv", "wep"));
        Assert.Empty(Search("kjv", "wep "));
    }

    [Fact]
    public void MatchedWordsAreMarkedForBold()
    {
        var hit = Search("kjv", "loved world").First(h => h.Verse.Ref.ToString() == "JOH 3:16");
        var marked = hit.Matches.Select(m => hit.Verse.Text.Substring(m.Start, m.Length)).ToList();
        Assert.Contains("loved", marked);
        Assert.Contains("world", marked);
        Assert.DoesNotContain("God", marked);
        // Common words aren't bolded beside telling ones.
        var patient = Search("web", "love is patient")[0];
        Assert.DoesNotContain(patient.Matches, m => patient.Verse.Text.Substring(m.Start, m.Length) == "is");
    }

    [Fact]
    public void NothingFoundIsAnEmptyList()
    {
        Assert.Empty(Search("kjv", "zzyzx qwerty"));
        Assert.Empty(Search("kjv", "   "));
        Assert.Empty(Search("kjv", "!!!"));
    }

    [Fact]
    public async Task SearchRunsOnTheBibleThread()
    {
        var hits = await BibleCatalog.BuiltIn.SearchAsync("web", "love is patient", 5, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.Equal("1CO 13:4", hits[0].Verse.Ref.ToString());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BibleCatalog.BuiltIn.SearchAsync("web", "love", 5, cancelled.Token));
    }

    [Fact]
    public void WordsIgnoreCaseApostrophesAndPossessives()
    {
        var words = BibleSearch.Words("The LORD’s day; doesn’t it?").Select(w => w.Word).ToList();
        Assert.Equal(["the", "lord", "day", "doesnt", "it"], words);
        var lord = BibleSearch.Words("The LORD’s day").ElementAt(1);
        Assert.Equal((4, 6), (lord.Start, lord.Length));
    }
}
