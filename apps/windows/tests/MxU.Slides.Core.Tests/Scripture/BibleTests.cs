using MxU.Slides.Core.Scripture;

namespace MxU.Slides.Core.Tests.Scripture;

public class BibleTests
{
    private static Bible Kjv => BibleCatalog.BuiltIn.Load("kjv");
    private static Bible Web => BibleCatalog.BuiltIn.Load("web");
    private static Bible Asv => BibleCatalog.BuiltIn.Load("asv");

    private static PassageText Look(Bible bible, string reference) => bible.Read(PassageReference.Parse(reference)!);

    private static VerseRef At(string code, int chapter, int verse) => new(BibleBooks.WithCode(code)!.Index, chapter, verse);

    [Fact]
    public void TheThreeTranslationsAreBuiltIn()
    {
        var catalog = BibleCatalog.BuiltIn;
        Assert.Equal(["kjv", "web", "asv"], catalog.Translations.Select(t => t.Id));
        Assert.Equal(["KJV", "WEB", "ASV"], catalog.Translations.Select(t => t.Abbreviation));
        Assert.Same(catalog.Translations[1], catalog.Find("WEB"));
        Assert.Same(catalog.Translations[0], catalog.Default);
        Assert.Contains("trademark", catalog.Find("web")!.Notes);
        Assert.Contains("Crown", catalog.Find("kjv")!.Notes);
        foreach (var translation in catalog.Translations)
        {
            var bible = catalog.Load(translation.Id);
            Assert.Equal(translation.Verses, bible.VerseCount);
            Assert.Same(bible, catalog.Loaded(translation.Id));
        }
    }

    [Fact]
    public async Task LoadingIsOffTheCallersThreadAndHappensOnce()
    {
        var caller = Environment.CurrentManagedThreadId;
        var opened = new List<int>();
        var catalog = new BibleCatalog(BibleCatalog.BuiltIn.Translations, t =>
        {
            lock (opened) opened.Add(Environment.CurrentManagedThreadId);
            return typeof(BibleCatalog).Assembly.GetManifestResourceStream("Bibles/" + t.File)!;
        });
        Assert.Null(catalog.Loaded("asv"));
        var both = await Task.WhenAll(catalog.LoadAsync("asv"), catalog.LoadAsync("ASV"))
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Same(both[0], both[1]);
        Assert.Single(opened);
        Assert.NotEqual(caller, opened[0]);
        var again = catalog.LoadAsync("asv");
        Assert.True(again.IsCompletedSuccessfully); // already in memory: no trip to the Bible thread
        Assert.Same(both[0], await again);
    }

    [Fact]
    public void ChapterAndVerseCountsComeFromTheData()
    {
        foreach (var bible in new[] { Kjv, Web, Asv })
            foreach (var book in BibleBooks.All)
                Assert.True(book.Chapters == bible.ChapterCount(book.Index), $"{bible.Translation.Abbreviation} {book.Name}");
        var psalms = BibleBooks.WithCode("PSA")!.Index;
        Assert.Equal(176, Kjv.VersesIn(psalms, 119));
        Assert.Equal(6, Kjv.VersesIn(psalms, 23));
        Assert.Equal(36, Kjv.VersesIn(BibleBooks.WithCode("JOH")!.Index, 3));
        Assert.Equal(0, Kjv.VersesIn(psalms, 151));
    }

    [Fact]
    public void VersesAndRangesAreLookedUp()
    {
        var john = Look(Kjv, "John 3:16");
        Assert.StartsWith("For God so loved the world", john.Verses.Single().Text);
        Assert.Equal("John 3:16 (KJV)", john.Title);

        var range = Look(Kjv, "John 3:16-18");
        Assert.Equal([16, 17, 18], range.Verses.Select(v => v.Ref.Verse));

        var psalm = Look(Kjv, "Psalm 23");
        Assert.Equal(6, psalm.Verses.Count);
        Assert.Equal("Psalm 23", psalm.Reference.Display); // a whole chapter stays a chapter

        var pieces = Look(Kjv, "Rom 8:28, 31-33");
        Assert.Equal([28, 31, 32, 33], pieces.Verses.Select(v => v.Ref.Verse));

        var two = Look(Web, "John 3:16; Rom 5:8");
        Assert.Equal(["JOH 3:16", "ROM 5:8"], two.Verses.Select(v => v.Ref.ToString()));
    }

    [Fact]
    public void PassagesRunAcrossChapters()
    {
        var across = Look(Kjv, "John 3:35-4:2");
        Assert.Equal(["JOH 3:35", "JOH 3:36", "JOH 4:1", "JOH 4:2"], across.Verses.Select(v => v.Ref.ToString()));
        Assert.Equal("John 3:35–4:2", across.Reference.Display);

        var chapters = Look(Kjv, "Psalms 1-2");
        Assert.Equal(6 + 12, chapters.Verses.Count);
        Assert.Equal("Psalms 1–2", chapters.Reference.Display);
    }

    [Fact]
    public void RangesStopAtTheEndAndMistakesAreSaidCalmly()
    {
        var past = Look(Kjv, "John 3:30-40");
        Assert.Equal(7, past.Verses.Count);
        Assert.Equal("John 3:30–36", past.Reference.Display);
        Assert.Null(past.Problem);

        Assert.Equal("John has 21 chapters.", Look(Kjv, "John 22").Problem);
        Assert.Equal("John 3 has 36 verses.", Look(Kjv, "John 3:40").Problem);
        Assert.Equal("Psalm 23 has 6 verses.", Look(Kjv, "Ps 23:9").Problem);
        Assert.Equal("Jude has only one chapter.", Look(Kjv, "Jude 2:1").Problem);
        Assert.True(Look(Kjv, "John 22").IsEmpty);
    }

    [Fact]
    public void VersesLeftOutByDesignAreExplainedNotFailed()
    {
        var acts = Look(Web, "Acts 8:37");
        Assert.True(acts.IsEmpty);
        Assert.Equal([At("ACT", 8, 37)], acts.Absent);
        Assert.Equal("Acts 8:37 isn't in the WEB: the oldest manuscripts don't have it.", acts.Problem);
        Assert.Equal("Acts 8:37 isn't in the WEB: the oldest manuscripts don't have it. The KJV has it.",
            AbsentVerses.Explain(acts.Absent, Web, [Kjv, Web, Asv]));

        var around = Look(Web, "Acts 8:36-38");
        Assert.Equal([36, 38], around.Verses.Select(v => v.Ref.Verse));
        Assert.Equal([At("ACT", 8, 37)], around.Absent);
        Assert.Null(around.Problem); // some verses were found: the window notes the gap without a problem

        Assert.True(Look(Asv, "Matthew 17:21").IsEmpty);
        Assert.False(Look(Kjv, "Matthew 17:21").IsEmpty);
        Assert.False(Look(Web, "Matthew 17:21").IsEmpty);
        Assert.Equal("Luke 17:36 isn't in the ASV: the oldest manuscripts don't have it.", Look(Asv, "Luke 17:36").Problem);

        var mark = Look(Asv, "Mark 9:43-48");
        Assert.Equal([At("MAR", 9, 44), At("MAR", 9, 46)], mark.Absent);
        Assert.Equal("Mark 9:44 and 46 aren't in the ASV: the oldest manuscripts don't have them. The KJV and WEB have them.",
            AbsentVerses.Explain(mark.Absent, Asv, [Kjv, Web]));

        // Romans' closing words are numbered differently in the WEB.
        Assert.Equal("Romans 16:25 isn't in the WEB, which has these words at Romans 14:24.", Look(Web, "Romans 16:25").Problem);
        var close = Look(Web, "Romans 16:24-27");
        Assert.Equal([24], close.Verses.Select(v => v.Ref.Verse));
        Assert.Equal([25, 26, 27], close.Absent.Select(v => v.Verse));
        Assert.Equal("Romans 16:24–27", close.Reference.Display);
        Assert.Equal("Romans 16:25, 26 and 27 aren't in the WEB, which has them at Romans 14:24, 25 and 26.", AbsentVerses.Explain(close.Absent, Web, null));
    }

    [Fact]
    public void NextAndPreviousVerseCrossChaptersAndBooks()
    {
        Assert.Equal(At("JOH", 3, 17), Kjv.After(At("JOH", 3, 16)));
        Assert.Equal(At("JOH", 4, 1), Kjv.After(At("JOH", 3, 36)));
        Assert.Equal(At("JOH", 3, 36), Kjv.Before(At("JOH", 4, 1)));
        Assert.Equal(At("MAT", 1, 1), Kjv.After(At("MAL", 4, 6)));
        Assert.Equal(At("MAL", 4, 6), Kjv.Before(At("MAT", 1, 1)));
        Assert.Null(Kjv.After(At("REV", 22, 21)));
        Assert.Null(Kjv.Before(At("GEN", 1, 1)));
        // A verse the translation leaves out is stepped over.
        Assert.Equal(At("ACT", 8, 38), Web.After(At("ACT", 8, 36)));
        Assert.Equal(At("ACT", 8, 36), Web.Before(At("ACT", 8, 38)));
        Assert.Equal(At("ACT", 8, 38), Kjv.After(At("ACT", 8, 37)));
    }
}
