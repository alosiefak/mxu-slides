using MxU.Slides.Core.Editing;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Stage;

/// <summary>
/// Windows: the stage display draws a song's chords as the shapes its guitarist plays with a capo
/// (Core/Editing/ChordEditing.cs, <see cref="SongCapo"/>): a song in A with capo 2 shows G shapes.
/// Only the stage's texts change; the audience and the song itself keep the sounding key.
/// </summary>
public static partial class ConfidenceSceneBuilder
{
    /// <summary>The capo fret of a presentation (by id), set by the app (it reads the Windows extra). Zero: none.</summary>
    public static Func<string, int>? CapoFor { get; set; }

    /// <summary>The key the stage shows chords in: the song's shown key, down by its capo.</summary>
    public static string? StageKey(Presentation? presentation)
    {
        if (presentation is null) return null;
        var fret = CapoFor?.Invoke(presentation.Id) ?? 0;
        return fret <= 0 ? presentation.DisplayKey : SongCapo.ShapeKey(presentation.DisplayKey ?? presentation.MusicKey, fret) ?? presentation.DisplayKey;
    }
}
