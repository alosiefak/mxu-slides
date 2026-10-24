using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Automation;

// The editor's undo and redo (Windows has no app-wide undo yet; the Mac's undo manager does this there).
public class TimelineEditHistoryTests
{
    private static Presentation Song(string id = "song", params double[] cueTimes)
    {
        var song = Make.Deck(id, ("s1", "verse"));
        song.Timelines = [Make.Timeline("t", cues: cueTimes.Select((at, i) => Make.Cue($"c{i}", "slide", at, slideId: "s1", sectionId: "verse")).ToList())];
        return song;
    }

    private static double[] Times(Presentation song) => song.Timelines![0].Cues.Select(c => c.At).ToArray();

    private static void Edit(TimelineEditHistory history, Presentation song, string label, double newAt)
    {
        history.Record(label, song);
        var timeline = song.Timelines![0].Copy();
        timeline.Cues = [.. timeline.Cues.Select(c => { var moved = c.Copy(); moved.At = newAt; return moved; })];
        song.Timelines = [timeline];
    }

    [Fact]
    public void UndoPutsTheSongBackAndRedoReturns()
    {
        var history = new TimelineEditHistory();
        var song = Song("song", 1);
        Assert.False(history.CanUndo("song"));
        Edit(history, song, "Move Cue", 2);
        Edit(history, song, "Nudge Cue", 3);
        Assert.Equal("Nudge Cue", history.UndoLabel("song"));

        TimelineEditHistory.Apply(history.Undo(song)!, song);
        Assert.Equal([2.0], Times(song));
        Assert.Equal("Nudge Cue", history.RedoLabel("song"));
        TimelineEditHistory.Apply(history.Undo(song)!, song);
        Assert.Equal([1.0], Times(song));
        Assert.Null(history.Undo(song));

        TimelineEditHistory.Apply(history.Redo(song)!, song);
        Assert.Equal([2.0], Times(song));
        TimelineEditHistory.Apply(history.Redo(song)!, song);
        Assert.Equal([3.0], Times(song));
        Assert.False(history.CanRedo("song"));
    }

    [Fact]
    public void ANewEditClearsRedo()
    {
        var history = new TimelineEditHistory();
        var song = Song("song", 1);
        Edit(history, song, "Move Cue", 2);
        TimelineEditHistory.Apply(history.Undo(song)!, song);
        Assert.True(history.CanRedo("song"));
        Edit(history, song, "Move Cue", 5);
        Assert.False(history.CanRedo("song"));
    }

    [Fact]
    public void EachSongHasItsOwnHistory()
    {
        var history = new TimelineEditHistory();
        var a = Song("a", 1);
        var b = Song("b", 7);
        Edit(history, a, "Move Cue", 2);
        Assert.False(history.CanUndo("b"));
        Assert.Null(history.Undo(b));
        Assert.True(history.CanUndo("a"));
        history.Forget("a");
        Assert.False(history.CanUndo("a"));
    }

    [Fact]
    public void ASnapshotIsACopyAndCoversTheSectionMap()
    {
        var history = new TimelineEditHistory();
        var song = Song("song", 1);
        song.SongMap = new SongMap { Recording = [], Sections = [new AudioSection { Id = "m", Name = "Verse", Start = 0 }] };
        history.Record("Edit Section Map", song);
        // Changing the song in place afterwards doesn't reach into the history.
        song.Timelines![0].Cues[0].At = 9;
        song.SongMap.Sections[0].Start = 4;
        song.SongMap = null;
        TimelineEditHistory.Apply(history.Undo(song)!, song);
        Assert.Equal([1.0], Times(song));
        Assert.Equal(0, song.SongMap?.Sections[0].Start);
        // A song with no timelines goes back to none.
        var bare = Make.Deck("bare", ("s1", null));
        history.Record("Add to Timeline", bare);
        bare.Timelines = [Make.Timeline("t")];
        TimelineEditHistory.Apply(history.Undo(bare)!, bare);
        Assert.Null(bare.Timelines);
    }

    [Fact]
    public void OnlyTheNewestStepsAreKept()
    {
        var history = new TimelineEditHistory { Limit = 3 };
        var song = Song("song", 0);
        for (var i = 1; i <= 5; i++) Edit(history, song, $"Step {i}", i);
        var undone = 0;
        while (history.Undo(song) is { } back)
        {
            TimelineEditHistory.Apply(back, song);
            undone++;
        }
        Assert.Equal(3, undone);
        Assert.Equal([2.0], Times(song));
    }
}
