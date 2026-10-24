namespace MxU.Slides.Audio.Tests;

/// <summary>Port of PlaylistQueueTests.swift (seeded <see cref="Random"/> stands in for SplitMix64).</summary>
public sealed class PlaylistQueueTests
{
    private static readonly string[] Tracks = ["a", "b", "c"];

    [Fact]
    public void PlayAllStopsAtTheEnd()
    {
        var queue = new PlaylistQueue(Tracks);
        queue.Start();
        Assert.Equal("a", queue.CurrentEntryId);
        Assert.Equal("b", queue.AdvanceAfterNaturalEnd());
        Assert.Equal("c", queue.AdvanceAfterNaturalEnd());
        Assert.Null(queue.AdvanceAfterNaturalEnd());
        queue.Start();
        Assert.Equal("a", queue.CurrentEntryId);
    }

    [Fact]
    public void LoopPlaylistWraps()
    {
        var queue = new PlaylistQueue(Tracks, PlaylistQueue.RepeatBehavior.Playlist);
        queue.Start();
        Assert.Equal("b", queue.AdvanceAfterNaturalEnd());
        Assert.Equal("c", queue.AdvanceAfterNaturalEnd());
        Assert.Equal("a", queue.AdvanceAfterNaturalEnd());
    }

    [Fact]
    public void LoopSingleRepeatsOnNaturalEndButNextAdvances()
    {
        var queue = new PlaylistQueue(Tracks, PlaylistQueue.RepeatBehavior.Single);
        queue.Start();
        Assert.Equal("a", queue.AdvanceAfterNaturalEnd());
        Assert.Equal("a", queue.AdvanceAfterNaturalEnd());
        Assert.Equal("b", queue.Next());
        Assert.Equal("b", queue.AdvanceAfterNaturalEnd());
    }

    [Fact]
    public void ExplicitNextAtEndStopsWhenRepeatOff()
    {
        var queue = new PlaylistQueue(Tracks);
        queue.Start();
        queue.Next();
        queue.Next();
        Assert.Equal("c", queue.CurrentEntryId);
        Assert.Null(queue.Next());
    }

    [Fact]
    public void PreviousStepsBackAndClampsWithoutRepeat()
    {
        var queue = new PlaylistQueue(Tracks);
        queue.Start();
        queue.Next();
        Assert.Equal("a", queue.Previous());
        Assert.Equal("a", queue.Previous());
    }

    [Fact]
    public void PreviousWrapsUnderRepeat()
    {
        var queue = new PlaylistQueue(Tracks, PlaylistQueue.RepeatBehavior.Playlist);
        queue.Start();
        Assert.Equal("c", queue.Previous());
    }

    [Fact]
    public void ShufflePlaysEveryTrackOncePerPass()
    {
        var random = new Random(7);
        var queue = new PlaylistQueue(["a", "b", "c", "d", "e"], PlaylistQueue.RepeatBehavior.Playlist, shuffled: true);
        queue.Start(random: random);
        var pass = new HashSet<string> { queue.CurrentEntryId! };
        for (var index = 0; index < 4; index++) pass.Add(queue.AdvanceAfterNaturalEnd(random)!);
        Assert.Equal(["a", "b", "c", "d", "e"], pass.Order());
    }

    [Fact]
    public void ShuffleWrapReshuffles()
    {
        var random = new Random(42);
        var queue = new PlaylistQueue(Tracks, PlaylistQueue.RepeatBehavior.Playlist, shuffled: true);
        queue.Start(random: random);
        var firstPass = new List<string> { queue.CurrentEntryId! };
        for (var index = 0; index < 2; index++) firstPass.Add(queue.AdvanceAfterNaturalEnd(random)!);
        var secondPass = new List<string>();
        for (var index = 0; index < 3; index++) secondPass.Add(queue.AdvanceAfterNaturalEnd(random)!);
        Assert.Equal(Tracks, firstPass.Order());
        Assert.Equal(Tracks, secondPass.Order());
    }

    [Fact]
    public void ShuffleStartAtChosenTrackLeadsThePass()
    {
        var queue = new PlaylistQueue(Tracks, PlaylistQueue.RepeatBehavior.Playlist, shuffled: true);
        queue.Start("b", new Random(1));
        Assert.Equal("b", queue.CurrentEntryId);
    }

    [Fact]
    public void ShuffleWithRepeatOffStopsAfterOnePass()
    {
        var random = new Random(3);
        var queue = new PlaylistQueue(Tracks, PlaylistQueue.RepeatBehavior.None, shuffled: true);
        queue.Start(random: random);
        var played = new List<string> { queue.CurrentEntryId! };
        while (queue.AdvanceAfterNaturalEnd(random) is { } id) played.Add(id);
        Assert.Equal(3, played.Count);
        Assert.Equal(Tracks, played.Order());
    }

    [Fact]
    public void ToggleShuffleOnKeepsCurrentTrackLeading()
    {
        var random = new Random(9);
        var queue = new PlaylistQueue(Tracks, PlaylistQueue.RepeatBehavior.Playlist);
        queue.Start();
        queue.Next();
        Assert.Equal("b", queue.CurrentEntryId);
        queue.SetShuffled(true, random);
        Assert.Equal("b", queue.CurrentEntryId);
        var rest = new[] { queue.AdvanceAfterNaturalEnd(random)!, queue.AdvanceAfterNaturalEnd(random)! };
        Assert.Equal(["a", "c"], rest.Order());
    }

    [Fact]
    public void ToggleShuffleOffRestoresListOrder()
    {
        var random = new Random(11);
        var queue = new PlaylistQueue(Tracks, PlaylistQueue.RepeatBehavior.Playlist, shuffled: true);
        queue.Start("b", random);
        queue.SetShuffled(false, random);
        Assert.Equal("b", queue.CurrentEntryId);
        Assert.Equal("c", queue.AdvanceAfterNaturalEnd(random));
    }

    [Fact]
    public void UpdateEntriesKeepsCurrentAndAppendsNew()
    {
        var queue = new PlaylistQueue(Tracks);
        queue.Start();
        queue.Next();
        Assert.Equal("b", queue.CurrentEntryId);
        queue.UpdateEntries(["a", "b", "c", "d"]);
        Assert.Equal("b", queue.CurrentEntryId);
        Assert.Equal("c", queue.AdvanceAfterNaturalEnd());
        Assert.Equal("d", queue.AdvanceAfterNaturalEnd());
    }

    [Fact]
    public void UpdateEntriesRemovingCurrentParksTheQueue()
    {
        var queue = new PlaylistQueue(Tracks);
        queue.Start();
        queue.Next();
        queue.UpdateEntries(["a", "c"]);
        Assert.Null(queue.CurrentEntryId);
    }

    [Fact]
    public void EmptyQueueAnswersNilEverywhere()
    {
        var queue = new PlaylistQueue([], PlaylistQueue.RepeatBehavior.Playlist, shuffled: true);
        queue.Start();
        Assert.Null(queue.CurrentEntryId);
        Assert.Null(queue.Next());
        Assert.Null(queue.Previous());
        Assert.Null(queue.AdvanceAfterNaturalEnd());
    }

    [Fact]
    public void DuplicateTracksAreDistinctPlays()
    {
        var queue = new PlaylistQueue(["a", "b", "a"]);
        queue.Start();
        Assert.Equal("b", queue.AdvanceAfterNaturalEnd());
        Assert.Equal("a", queue.AdvanceAfterNaturalEnd());
        Assert.Null(queue.AdvanceAfterNaturalEnd());
    }

    [Fact]
    public void ShuffleWrapNeverPlaysTheSameEntryBackToBack()
    {
        var random = new Random(5);
        var queue = new PlaylistQueue(Tracks, PlaylistQueue.RepeatBehavior.Playlist, shuffled: true);
        queue.Start(random: random);
        var previous = queue.CurrentEntryId;
        for (var index = 0; index < 60; index++)
        {
            var next = queue.AdvanceAfterNaturalEnd(random);
            Assert.NotEqual(previous, next);
            previous = next;
        }
    }

    [Fact]
    public void PositionInPassAdvancesEvenAcrossDuplicateTracks()
    {
        var queue = new PlaylistQueue(["s", "h", "s"], PlaylistQueue.RepeatBehavior.Playlist);
        queue.Start();
        Assert.Equal((1, 3), queue.PositionInPass);
        queue.Next();
        Assert.Equal((2, 3), queue.PositionInPass);
        queue.Next();
        Assert.Equal((3, 3), queue.PositionInPass);
    }

    [Fact]
    public void CrossfadeEndpointsAreCleanHandoffs()
    {
        var start = CrossfadeCurve.Gains(0);
        var end = CrossfadeCurve.Gains(1);
        Assert.True(start.Incoming == 0 && MathF.Abs(start.Outgoing - 1) < 0.001);
        Assert.True(MathF.Abs(end.Incoming - 1) < 0.001 && end.Outgoing == 0);
    }

    [Fact]
    public void CrossfadeOutgoingRecedesAudiblyEarly()
    {
        var quarter = CrossfadeCurve.Gains(0.25);
        Assert.True(quarter.Outgoing < 0.65);
        Assert.True(quarter.Incoming < 0.2);
    }

    [Fact]
    public void CrossfadeCombinedLevelNeverExceedsOneSong()
    {
        for (var step = 0; step <= 100; step++)
        {
            var gains = CrossfadeCurve.Gains(step / 100.0);
            var power = gains.Incoming * gains.Incoming + gains.Outgoing * gains.Outgoing;
            Assert.True(power <= 1.001);
            Assert.True(power >= 0.32, "dip stays gentler than ~-5dB");
        }
    }

    [Fact]
    public void CrossfadeBothSidesAreMonotonic()
    {
        var previous = CrossfadeCurve.Gains(0);
        for (var step = 1; step <= 100; step++)
        {
            var gains = CrossfadeCurve.Gains(step / 100.0);
            Assert.True(gains.Incoming >= previous.Incoming);
            Assert.True(gains.Outgoing <= previous.Outgoing);
            previous = gains;
        }
    }
}
