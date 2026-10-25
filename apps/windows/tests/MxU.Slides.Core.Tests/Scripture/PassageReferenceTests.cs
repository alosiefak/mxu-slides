using MxU.Slides.Core.Scripture;

namespace MxU.Slides.Core.Tests.Scripture;

public class PassageReferenceTests
{
    private static string Read(string typed) => PassageReference.Parse(typed)?.Display ?? "(not a reference)";

    [Fact]
    public void WhatPeopleTypeIsRead()
    {
        Assert.Equal("John 3:16", Read("John 3:16"));
        Assert.Equal("John 3:16–18", Read("Jn 3:16-18"));
        Assert.Equal("1 Corinthians 13:4–8", Read("1 Cor 13:4–8"));
        Assert.Equal("Psalm 23", Read("Psalm 23"));
        Assert.Equal("Psalm 23:1–3, 5", Read("Ps 23:1-3, 5"));
        Assert.Equal("Romans 8:28, 31–39", Read("Rom 8:28,31-39"));
        Assert.Equal("Genesis 1", Read("Gen 1"));
    }

    [Fact]
    public void BookNamesCanBeFullShortNumberedAndAnyCase()
    {
        Assert.Equal("John 3:16", Read("JOHN 3:16"));
        Assert.Equal("John 3:16", Read("john 3:16"));
        Assert.Equal("John 3:16", Read("jn3:16"));
        Assert.Equal("John 3:16", Read("Jhn 3.16"));
        foreach (var typed in new[] { "1 John 4:8", "I John 4:8", "1st John 4:8", "First John 4:8", "1jn 4:8", "1 Jn. 4:8", "i jn 4:8" })
            Assert.True(Read(typed) == "1 John 4:8", typed);
        Assert.Equal("2 Kings 2:11", Read("II Kings 2:11"));
        Assert.Equal("2 Kings 2:11", Read("2nd Kgs 2:11"));
        Assert.Equal("3 John 1:4", Read("III John 4"));
        Assert.Equal("Song of Solomon 2:1", Read("Song of Songs 2:1"));
        Assert.Equal("Song of Solomon 2:1", Read("SOS 2:1"));
        Assert.Equal("Philippians 4:13", Read("Philipp 4:13")); // cut short, but only one book starts that way
        Assert.Equal("Isaiah 53:5", Read("Is 53:5"));
        Assert.Equal("Revelation 21:4", Read("Revelations 21:4"));
    }

    [Fact]
    public void RangesTakeHyphensDashesAndWords()
    {
        Assert.Equal("John 3:16–18", Read("John 3:16–18"));
        Assert.Equal("John 3:16–18", Read("John 3:16—18"));
        Assert.Equal("John 3:16–18", Read("John 3 : 16 - 18"));
        Assert.Equal("John 3:16–18", Read("John 3:16 to 18"));
        Assert.Equal("John 3:16–18", Read("John 3:18-16"));
        Assert.Equal("Matthew 3:28", Read("Matthew 3 v 28"));
        Assert.Equal("Matthew 3:28", Read("Matthew 3 verse 28"));
        Assert.Equal("John 3:16", Read("John 3:16a"));
        Assert.Equal("Romans 8:28, 31", Read("Rom 8:28 and 31"));
    }

    [Fact]
    public void ChaptersAndPassagesAcrossChapters()
    {
        Assert.Equal("Genesis 1–2", Read("gen 1-2"));
        Assert.Equal("Psalms 23–24", Read("Psalms 23-24"));
        Assert.Equal("Psalms 23, 25", Read("Ps 23, 25"));
        Assert.Equal("John 3:16–4:2", Read("john 3:16-4:2"));
        Assert.Equal("Genesis 1:1; 2:3", Read("Gen 1:1, 2:3"));
        var across = PassageReference.Parse("John 3:35-4:2")!.Spans.Single();
        Assert.Equal((3, 35, 4, 2), (across.FirstChapter, across.FirstVerse!.Value, across.LastChapter, across.LastVerse!.Value));
        var whole = PassageReference.Parse("Psalm 23")!.Spans.Single();
        Assert.True(whole.IsWholeChapters);
    }

    [Fact]
    public void OneChapterBooksTakeABareVerse()
    {
        Assert.Equal("Jude 1:3", Read("Jude 3"));
        Assert.Equal("Jude 1:3", Read("Jude 1:3"));
        Assert.Equal("Jude 1", Read("Jude 1"));
        Assert.Equal("Philemon 1:4–6", Read("Philemon 4-6"));
        Assert.Equal("Obadiah 1:15", Read("Obad 15"));
    }

    [Fact]
    public void SeveralReferencesAndATranslation()
    {
        Assert.Equal("John 3:16; Romans 5:8", Read("John 3:16; Rom 5:8"));
        Assert.Equal("John 3:16; 4:1", Read("John 3:16; 4:1"));
        Assert.Equal("Isaiah 53:4–6; 1 Peter 2:24", Read("Isaiah 53:4-6; 1 Peter 2:24"));
        Assert.Equal("KJV", PassageReference.Parse("John 3:16 KJV")?.Translation);
        Assert.Equal("WEB", PassageReference.Parse("John 3:16 (web)")?.Translation);
        Assert.Null(PassageReference.Parse("John 3:16")?.Translation);
        Assert.Equal("Job 1", Read("Job")); // a book, not a translation
    }

    [Fact]
    public void AReferenceStillBeingTypedReadsAsFarAsItGoes()
    {
        Assert.Equal("John 3", Read("John 3:"));
        Assert.Equal("Psalm 23:1", Read("Ps 23:1-"));
        Assert.Equal("Psalm 23:1", Read("Ps 23:1,"));
        Assert.Equal("John 1", Read("John"));
        Assert.Equal("John 1", Read("John "));
        Assert.Equal("Mark 1", Read("mark"));
    }

    [Fact]
    public void WordsAreNotReferences()
    {
        foreach (var typed in new[] { "", "   ", "love is patient", "Jo", "judge", "Psalm 23 love", "3:16", "God so loved the world", "Mark my words", "Jobs 3:16" })
            Assert.True(PassageReference.Parse(typed) is null, typed);
    }

    [Fact]
    public void BooksAreOfferedWhileTheNameIsTyped()
    {
        Assert.Equal(["Joshua", "Job", "Joel", "Jonah", "John", "1 John", "2 John", "3 John"], BibleBooks.Complete("jo").Select(b => b.Name));
        Assert.Equal("1 Corinthians", BibleBooks.Complete("1 co")[0].Name);
        Assert.Equal(["1 Chronicles", "1 Corinthians"], BibleBooks.Complete("1 c").Select(b => b.Name));
        Assert.Equal("Psalms", BibleBooks.Complete("ps")[0].Name);
        Assert.Empty(BibleBooks.Complete(""));
        Assert.Empty(BibleBooks.Complete("xyz"));
    }

    [Fact]
    public void EveryBookHasACodeAndUniqueShortForms()
    {
        Assert.Equal(66, BibleBooks.All.Count);
        Assert.Equal("GEN", BibleBooks.All[0].Code);
        Assert.Equal("REV", BibleBooks.All[65].Code);
        Assert.True(BibleBooks.All[39].IsNewTestament && !BibleBooks.All[38].IsNewTestament);
        foreach (var book in BibleBooks.All)
        {
            Assert.Same(book, BibleBooks.WithCode(book.Code));
            Assert.Same(book, BibleBooks.Find(book.Name));
            foreach (var alias in book.Aliases) Assert.True(BibleBooks.Find(alias) == book, alias);
        }
    }
}
