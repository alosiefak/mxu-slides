using MxU.Slides.Core.Editing;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Tests.Library;

namespace MxU.Slides.Core.Tests.Editing;

/// <summary>Ported from the Mac's ChordEditingTests.swift, plus the capo (a Windows extra).</summary>
public sealed class ChordEditingTests
{
    private const string Text = "Amazing grace\nhow sweet";

    private static ChordPlacement C(int line, int column, string symbol) => new() { Line = line, Column = column, Symbol = symbol };

    private static List<ChordPlacement> Chords() => [C(0, 0, "G"), C(0, 8, "C"), C(1, 4, "D")];

    private static string Show(IEnumerable<ChordPlacement> chords) => string.Join(" ", chords.Select(c => $"{c.Symbol}@{c.Line}:{c.Column}"));

    [Fact]
    public void RevealShowsLetterChordsOnUnlinkedChordedTextOnly()
    {
        var chorded = new SlideObject { Id = "a", ObjectKind = SlideObjectKind.Text, Name = "Lyrics", Text = Text, Chords = Chords(), TextStyle = new TextStyle { ChordNotation = ChordNotation.Numbers } };
        var plain = new SlideObject { Id = "b", ObjectKind = SlideObjectKind.Text, Name = "Plain", Text = "Hi" };
        var linked = new SlideObject { Id = "c", ObjectKind = SlideObjectKind.Text, Name = "Next", Text = "", TextLink = new TextLink { Source = TextSourceKind.NextSlide }, Chords = Chords() };
        var revealed = ChordEditing.Revealed([chorded, plain, linked]);
        Assert.True(revealed[0].TextStyle?.ShowChords);
        Assert.Equal(ChordNotation.Chords, revealed[0].TextStyle?.ChordNotation); // edit what's stored, not a transform
        Assert.True(revealed[0].TextStyle?.AutoShrink); // chord rows fit the box
        Assert.Same(plain, revealed[1]);
        Assert.Same(linked, revealed[2]);
        Assert.Equal(ChordNotation.Numbers, chorded.TextStyle!.ChordNotation); // the original is untouched
    }

    [Fact]
    public void MovingAlongAndAcrossLinesKeepsOrderAndTracksTheChord()
    {
        var along = ChordEditing.Moving(0, 0, 10, Chords(), Text);
        Assert.Equal("C@0:8 G@0:10 D@1:4", Show(along.Chords));
        Assert.Equal(1, along.Index);

        var down = ChordEditing.Moving(1, 1, 0, Chords(), Text);
        Assert.Equal("G@0:0 C@1:0 D@1:4", Show(down.Chords));
        Assert.Equal(1, down.Index);

        var clamped = ChordEditing.Moving(2, 7, 40, Chords(), Text);
        Assert.Equal("D@1:9", Show([clamped.Chords[2]]));
    }

    [Fact]
    public void RetypingAddingAndRemoving()
    {
        var retyped = ChordEditing.Setting(" Cmaj7 ", 1, Chords());
        Assert.Equal("Cmaj7", retyped.Chords[1].Symbol);
        Assert.Equal(1, retyped.Index);

        var emptied = ChordEditing.Setting("", 1, Chords());
        Assert.Equal(["G", "D"], emptied.Chords.Select(c => c.Symbol)); // an emptied chord is removed
        Assert.Null(emptied.Index);

        var added = ChordEditing.Adding("Em", 1, 0, Chords(), Text);
        Assert.Equal(["G", "C", "Em", "D"], added.Chords.Select(c => c.Symbol));
        Assert.Equal(2, added.Index);

        var blank = ChordEditing.Adding("  ", 0, 3, Chords(), Text);
        Assert.Equal(Show(Chords()), Show(blank.Chords));
        Assert.Null(blank.Index);

        Assert.Equal(["C", "D"], ChordEditing.Removing(0, Chords()).Select(c => c.Symbol));
    }

    [Theory]
    [InlineData("A", 2, "G")]
    [InlineData("A", 0, "A")]
    [InlineData("Eb", 1, "D")]
    [InlineData("Bm", 2, "Am")]
    [InlineData("C", 5, "G")]
    public void ACapoTurnsTheSoundingKeyIntoTheShapesPlayed(string sounding, int fret, string shapes) =>
        Assert.Equal(shapes, SongCapo.ShapeKey(sounding, fret));

    [Fact]
    public void ThereIsNoShapeKeyWithoutAKey() => Assert.Null(SongCapo.ShapeKey(null, 2));
}

public sealed class SongCapoStoreTests : ScratchFolderTest
{
    [Fact]
    public void ACapoIsKeptBesideTheLibraryAndUndoes()
    {
        var library = NewLibrary();
        var scope = library.Undo.NewScope("Main window");
        Assert.Equal(0, SongCapo.For(library.Extras, "song"));
        using (scope.Use()) SongCapo.Set(library.Extras, "song", 3);
        Assert.Equal(3, SongCapo.For(library.Extras, "song"));
        scope.Undo();
        Assert.Equal(0, SongCapo.For(library.Extras, "song"));
        SongCapo.Set(library.Extras, "song", 40);
        Assert.Equal(SongCapo.MaxFret, SongCapo.For(library.Extras, "song"));
        SongCapo.Set(library.Extras, "song", 0);
        Assert.Empty(library.Extras.Owners(SongCapo.Kind));
    }
}
