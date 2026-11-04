using MxU.Slides.Core.Library;
using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Editing;

/// <summary>
/// Placing chords over words. A chord sits at a (line, column) of the lyric, counted in characters,
/// and the list is kept in reading order. A port of the Mac's ChordEditing.swift: adding, retyping
/// (an emptied chord is removed), removing and moving chords, each returning where the chord being
/// edited ended up so the editor keeps it selected.
/// </summary>
public static class ChordEditing
{
    /// <summary>The list after an edit, and the index of the chord being edited (null: it's gone).</summary>
    public readonly record struct Edit(List<ChordPlacement> Chords, int? Index);

    /// <summary>
    /// The text boxes as the chord editor shows them: a box with chords shows them, as letter chords
    /// (what's stored, not a transform), shrinking to fit so the chord rows don't push words out.
    /// </summary>
    public static List<SlideObject> Revealed(IEnumerable<SlideObject> objects) => [.. objects.Select(o =>
    {
        if (o.TextLink is not null || o.Chords is not { Count: > 0 }) return o;
        var revealed = Json.MxuJson.Clone(o);
        var style = revealed.TextStyle ??= new TextStyle();
        style.ShowChords = true;
        style.ChordNotation = ChordNotation.Chords;
        style.AutoShrink = true;
        return revealed;
    })];

    /// <summary>A position kept inside the text: a real line, and a column no further than its end.</summary>
    public static (int Line, int Column) Clamped(int line, int column, string text)
    {
        var lines = text.Split('\n');
        var clampedLine = Math.Clamp(line, 0, Math.Max(lines.Length - 1, 0));
        var length = clampedLine < lines.Length ? lines[clampedLine].CharacterCount() : 0;
        return (clampedLine, Math.Clamp(column, 0, length));
    }

    public static Edit Adding(string symbol, int line, int column, IReadOnlyList<ChordPlacement> chords, string text)
    {
        var trimmed = symbol.Trim();
        if (trimmed.Length == 0) return new Edit([.. chords], null);
        var (l, c) = Clamped(line, column, text);
        return SortedTracking([.. chords, new ChordPlacement { Line = l, Column = c, Symbol = trimmed }], chords.Count);
    }

    public static Edit Setting(string symbol, int index, IReadOnlyList<ChordPlacement> chords)
    {
        var trimmed = symbol.Trim();
        if (index < 0 || index >= chords.Count) return new Edit([.. chords], null);
        if (trimmed.Length == 0) return new Edit(Removing(index, chords), null);
        var edited = chords.Select(Copy).ToList();
        edited[index].Symbol = trimmed;
        return new Edit(edited, index);
    }

    public static List<ChordPlacement> Removing(int index, IReadOnlyList<ChordPlacement> chords)
    {
        var edited = chords.Select(Copy).ToList();
        if (index >= 0 && index < edited.Count) edited.RemoveAt(index);
        return edited;
    }

    public static Edit Moving(int index, int line, int column, IReadOnlyList<ChordPlacement> chords, string text)
    {
        if (index < 0 || index >= chords.Count) return new Edit([.. chords], null);
        var (l, c) = Clamped(line, column, text);
        var edited = chords.Select(Copy).ToList();
        edited[index].Line = l;
        edited[index].Column = c;
        return SortedTracking(edited, index);
    }

    /// <summary>Reading order (line, then column; ties keep their order), following one chord through the sort.</summary>
    public static Edit SortedTracking(List<ChordPlacement> chords, int index)
    {
        var order = Enumerable.Range(0, chords.Count).OrderBy(i => chords[i].Line).ThenBy(i => chords[i].Column).ThenBy(i => i).ToList();
        var position = order.IndexOf(index);
        return new Edit([.. order.Select(i => chords[i])], position < 0 ? null : position);
    }

    private static ChordPlacement Copy(ChordPlacement c) => new() { Line = c.Line, Column = c.Column, Symbol = c.Symbol };
}

/// <summary>
/// A capo for a song: the band's guitarist puts a capo on fret 2 and plays G shapes for a song in A.
/// The audience never sees chords that way, so a capo only changes the chords the stage display
/// shows: they're drawn as the shapes to play, transposed down by the capo (the sounding key, A,
/// stays the song's key everywhere else).
/// <para>
/// The Mac has no capo and the shared format has no field for one, so it's a Windows extra
/// (WindowsExtras.cs, kind "song capo", one per presentation). A Mac sees the song's real chords.
/// </para>
/// </summary>
public static class SongCapo
{
    public const string Kind = "song capo";

    public const int MaxFret = 11;

    internal sealed record Capo(int Fret);

    /// <summary>The song's capo fret (0: none).</summary>
    public static int For(WindowsExtras extras, string presentationId) => extras.Get<Capo>(Kind, presentationId)?.Fret ?? 0;

    public static void Set(WindowsExtras extras, string presentationId, int fret)
    {
        fret = Math.Clamp(fret, 0, MaxFret);
        if (For(extras, presentationId) == fret) return;
        if (fret == 0) extras.Delete(Kind, presentationId);
        else extras.Save(Kind, presentationId, new Capo(fret));
    }

    /// <summary>
    /// The key whose chords a player with this capo plays: the sounding key down by the capo.
    /// A in G-shapes for capo 2. Null when there's no key to work from.
    /// </summary>
    public static string? ShapeKey(string? soundingKey, int fret)
    {
        if (soundingKey is null || ChordMath.ParseKey(soundingKey) is not { } key) return null;
        if (fret <= 0) return soundingKey;
        var pitch = (key.PitchClass - fret % 12 + 12) % 12;
        return ChordMath.KeyChoices(soundingKey).FirstOrDefault(choice => ChordMath.ParseKey(choice)?.PitchClass == pitch);
    }
}
