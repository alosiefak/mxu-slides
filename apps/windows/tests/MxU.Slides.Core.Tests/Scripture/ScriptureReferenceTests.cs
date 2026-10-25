using MxU.Slides.Core.Scripture;

namespace MxU.Slides.Core.Tests.Scripture;

/// <summary>Ported from the Mac's PresenterCoreTests/ScriptureReferenceTests.swift.</summary>
public class ScriptureReferenceTests
{
    [Fact]
    public void ReferencesAreFoundInEveryUsualShape()
    {
        (string Line, string Expected, bool HasVerse)[] lines =
        [
            ("John 3:16", "John 3:16", true),
            ("see Matthew 5:3-12 for the list", "Matthew 5:3-12", true),
            ("1 John 4:8", "1 John 4:8", true),
            ("Song of Songs 2:1", "Song of Songs 2:1", true),
            ("Ps. 23:1 (ESV)", "Ps. 23:1 (ESV)", true),
            ("Matthew 3 v 28", "Matthew 3 v 28", true),
            ("Rom 8:28, 31", "Rom 8:28, 31", true),
            ("Psalm 23", "Psalm 23", false),
            ("Isaiah 53:4-6; 1 Peter 2:24", "Isaiah 53:4-6", true),
        ];
        foreach (var (line, expected, hasVerse) in lines)
        {
            var found = ScriptureReference.Matches(line);
            Assert.True(found.FirstOrDefault()?.Text == expected, line);
            Assert.True(found.FirstOrDefault()?.HasVerse == hasVerse, line);
        }
        Assert.Equal(2, ScriptureReference.Matches("Isaiah 53:4-6; 1 Peter 2:24").Count);
    }

    [Fact]
    public void NamesAndClocksAreNotReferences()
    {
        foreach (var trap in new[] { "Matthew and Mark helped", "Chapter 3", "3:16 pm", "Psalm", "John went home", "Jobs 3:16" })
            Assert.True(ScriptureReference.Matches(trap).Count == 0, trap);
        Assert.True(ScriptureReference.IsReferenceLine("John 3:16; Romans 5:8"));
        Assert.True(ScriptureReference.IsReferenceLine("(Psalm 23)"));
        Assert.False(ScriptureReference.IsReferenceLine("John 3:16 says it all"));
        Assert.False(ScriptureReference.IsReferenceLine(""));
    }

    [Fact]
    public void PassagesSplitIntoVerseAndReference()
    {
        Assert.Equal(new ScriptureReference.Split("For God so loved the world", "John 3:16"),
            ScriptureReference.SplitText("For God so loved the world\nJohn 3:16"));
        Assert.Equal(new ScriptureReference.Split("For God so loved the world", "John 3:16"),
            ScriptureReference.SplitText("John 3:16\nFor God so loved the world"));
        Assert.Equal(new ScriptureReference.Split("For God so loved the world", "John 3:16"),
            ScriptureReference.SplitText("For God so loved the world (John 3:16)"));
        Assert.Equal(new ScriptureReference.Split("The Lord is my shepherd", "Psalm 23"),
            ScriptureReference.SplitText("The Lord is my shepherd - Psalm 23"));
        Assert.Equal(new ScriptureReference.Split("For God so loved the world", "John 3:16"),
            ScriptureReference.SplitText("John 3:16: For God so loved the world"));
        Assert.Equal(new ScriptureReference.Split("Trust in the Lord", "Proverbs 3:5"),
            ScriptureReference.SplitText("Trust in the Lord Proverbs 3:5"));
        Assert.Equal(new ScriptureReference.Split("", "Romans 8:28"), ScriptureReference.SplitText("Romans 8:28"));

        Assert.Null(ScriptureReference.SplitText("We read Psalm 23 together"));
        Assert.Null(ScriptureReference.SplitText("Grace upon grace"));
    }
}
