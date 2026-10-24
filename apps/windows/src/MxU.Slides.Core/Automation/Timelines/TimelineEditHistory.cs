using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Automation;

/// <summary>
/// The timeline editor's undo and redo. Before each edit the editor hands over the song as
/// it was (its timelines and its section map); Undo puts that back and keeps the newer
/// version for Redo. Each song has its own history, so switching songs never undoes the
/// wrong one. On the Mac the app's undo manager does this; Windows has no app-wide undo
/// yet, so the editor keeps its own (this class), and it is easy to test without a window.
/// </summary>
public sealed class TimelineEditHistory
{
    /// <summary>A song's timelines and section map at one moment, and the edit that came after.</summary>
    public sealed record Snapshot(string Label, List<PresentationTimeline>? Timelines, SongMap? SongMap);

    private sealed class Song
    {
        public List<Snapshot> Undo { get; } = [];
        public List<Snapshot> Redo { get; } = [];
    }

    private readonly Dictionary<string, Song> songs = new(StringComparer.Ordinal);

    /// <summary>How many steps back each song keeps (the oldest go first).</summary>
    public int Limit { get; init; } = 100;

    /// <summary>A deep copy of the song's timelines and map, so later edits can't change it.</summary>
    public static Snapshot Take(string label, Presentation presentation) => new(
        label,
        presentation.Timelines is null ? null : MxuJson.Clone(presentation.Timelines),
        presentation.SongMap is null ? null : MxuJson.Clone(presentation.SongMap));

    /// <summary>Remembers the song as it was before an edit called <paramref name="label"/>. A new edit clears Redo.</summary>
    public void Record(string label, Presentation before)
    {
        var song = SongFor(before.Id);
        song.Undo.Add(Take(label, before));
        if (song.Undo.Count > Limit) song.Undo.RemoveRange(0, song.Undo.Count - Limit);
        song.Redo.Clear();
    }

    public bool CanUndo(string presentationId) => songs.TryGetValue(presentationId, out var song) && song.Undo.Count > 0;

    public bool CanRedo(string presentationId) => songs.TryGetValue(presentationId, out var song) && song.Redo.Count > 0;

    /// <summary>The edit Undo would take back ("Move Cue").</summary>
    public string? UndoLabel(string presentationId) => songs.TryGetValue(presentationId, out var song) ? song.Undo.LastOrDefault()?.Label : null;

    public string? RedoLabel(string presentationId) => songs.TryGetValue(presentationId, out var song) ? song.Redo.LastOrDefault()?.Label : null;

    /// <summary>
    /// Steps back: returns the snapshot to put back (null when there is nothing to undo) and
    /// keeps <paramref name="current"/> so Redo can return to it.
    /// </summary>
    public Snapshot? Undo(Presentation current)
    {
        if (!songs.TryGetValue(current.Id, out var song) || song.Undo.Count == 0) return null;
        var back = song.Undo[^1];
        song.Undo.RemoveAt(song.Undo.Count - 1);
        song.Redo.Add(Take(back.Label, current));
        return back;
    }

    /// <summary>Steps forward again after an Undo.</summary>
    public Snapshot? Redo(Presentation current)
    {
        if (!songs.TryGetValue(current.Id, out var song) || song.Redo.Count == 0) return null;
        var forward = song.Redo[^1];
        song.Redo.RemoveAt(song.Redo.Count - 1);
        song.Undo.Add(Take(forward.Label, current));
        return forward;
    }

    /// <summary>Puts a snapshot back onto the song (copies, so the history keeps its own).</summary>
    public static void Apply(Snapshot snapshot, Presentation presentation)
    {
        presentation.Timelines = snapshot.Timelines is null ? null : MxuJson.Clone(snapshot.Timelines);
        presentation.SongMap = snapshot.SongMap is null ? null : MxuJson.Clone(snapshot.SongMap);
    }

    /// <summary>Forgets a song's history (for example when the song was changed somewhere else).</summary>
    public void Forget(string presentationId) => songs.Remove(presentationId);

    private Song SongFor(string id)
    {
        if (!songs.TryGetValue(id, out var song)) songs[id] = song = new Song();
        return song;
    }
}
