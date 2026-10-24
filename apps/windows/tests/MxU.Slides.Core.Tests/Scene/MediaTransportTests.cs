using MxU.Slides.Core.Media;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Tests.Scene;

// Ports of SlideSceneTests/MediaTransportTargetTests.swift (the target rules and the seeds),
// MediaEngineTests/TransportTrackingTests.swift and the arithmetic case of TransportTests.swift
// (the others drive the Mac's Metal player). The pass cases are Windows-only.
public class MediaTransportTests
{
    private static readonly DateTimeOffset T0 = new(2001, 1, 1, 2, 46, 40, TimeSpan.Zero);

    private static TransportCandidate Candidate(
        string id, LayerKind layer = LayerKind.Videos, TransportOrigin origin = TransportOrigin.ForegroundServiceItem,
        bool looping = false, double firedAt = 0) => new(id, layer, origin, looping, T0.AddSeconds(firedAt));

    [Fact]
    public void DefaultViewportIsTheForegroundLayer()
    {
        var background = Candidate("bg", LayerKind.LoopingVideos, TransportOrigin.BackgroundCue, looping: true);
        var foreground = Candidate("fg");
        Assert.Equal("fg", TransportTarget.Resolve([background, foreground])?.MediaId); // nothing off the viewed layer ever auto-shows
        Assert.Null(TransportTarget.Resolve([background])); // a background loop must not leak into the default foreground viewport
    }

    [Fact]
    public void CountdownTargetResolvesPerLayerWithoutThePin()
    {
        var background = Candidate("bg", LayerKind.LoopingVideos, TransportOrigin.BackgroundCue, looping: true);
        var foreground = Candidate("fg");
        Assert.Equal("fg", TransportTarget.CountdownTarget([background, foreground])?.MediaId);
        Assert.Equal("bg", TransportTarget.CountdownTarget([background, foreground], LayerKind.LoopingVideos)?.MediaId); // a layer-targeted box reads its layer's video
        var loopingForeground = Candidate("loop", looping: true);
        Assert.Equal("loop", TransportTarget.CountdownTarget([background, loopingForeground])?.MediaId); // a lone looping foreground counts one pass
        Assert.Null(TransportTarget.CountdownTarget([background])); // the default read is the foreground: an idle foreground is silent
        Assert.Null(TransportTarget.CountdownTarget([background, foreground], LayerKind.Slide)); // an idle layer renders nothing
    }

    [Fact]
    public void EarliestStartWinsAndLaterFiresNeverSteal()
    {
        var first = Candidate("first", firedAt: 0);
        var second = Candidate("second", firedAt: 30);
        Assert.Equal("first", TransportTarget.Resolve([second, first])?.MediaId); // must not retarget when a newer video fires
    }

    [Fact]
    public void SimultaneousStartsBreakByOriginRank()
    {
        var cueVideo = Candidate("cue", LayerKind.Slide, TransportOrigin.ForegroundCue, firedAt: 0);
        var slideVideo = Candidate("slide", LayerKind.Slide, TransportOrigin.Slides, firedAt: 0.5);
        Assert.Equal("slide", TransportTarget.Resolve([cueVideo, slideVideo], LayerKind.Slide)?.MediaId);
        var lateSlide = Candidate("late", LayerKind.Slide, TransportOrigin.Slides, firedAt: 2);
        Assert.Equal("cue", TransportTarget.Resolve([cueVideo, lateSlide], LayerKind.Slide)?.MediaId);
    }

    [Fact]
    public void FullTieFallsToCandidateOrder()
    {
        var a = Candidate("a", LayerKind.Slide, TransportOrigin.Slides);
        var b = Candidate("b", LayerKind.Slide, TransportOrigin.Slides);
        Assert.Equal("a", TransportTarget.Resolve([a, b], LayerKind.Slide)?.MediaId);
        Assert.Equal("b", TransportTarget.Resolve([b, a], LayerKind.Slide)?.MediaId);
    }

    [Fact]
    public void LoopsNeverAutoSelectButExplicitSelectionGetsThem()
    {
        var loop = Candidate("loop", looping: true, firedAt: 0);
        var video = Candidate("video", firedAt: 5);
        Assert.Equal("video", TransportTarget.Resolve([loop, video])?.MediaId);
        Assert.Equal("loop", TransportTarget.Resolve([loop])?.MediaId);
        Assert.Equal("loop", TransportTarget.Resolve([loop, video], preferNonLooping: false)?.MediaId); // explicit selection takes the layer's earliest video, loops included
    }

    [Fact]
    public void PinnedVideoWinsWhileLiveAndFallsBackWhenGone()
    {
        var fg = Candidate("fg");
        var pinnedLoop = Candidate("bg", LayerKind.LoopingVideos, TransportOrigin.BackgroundCue, looping: true);
        Assert.Equal("bg", TransportTarget.Resolve([fg, pinnedLoop], pinnedId: "bg")?.MediaId); // a pin follows the video regardless of viewport
        Assert.Equal("fg", TransportTarget.Resolve([fg], pinnedId: "bg")?.MediaId); // a stale pin falls back to the auto rules
    }

    [Fact]
    public void TransportSeedsCarryLayerAndDirectness()
    {
        var state = new ShowState();
        state.Fire(new CueMedia { MediaId = "direct-fg", Layer = CueMediaLayer.Videos }, LayerKind.Videos);
        state.Fire(new CueMedia { MediaId = "cue-bg", Layer = CueMediaLayer.LoopingVideos }, LayerKind.LoopingVideos);
        var seeds = state.TransportSeeds();
        Assert.Equal(TransportOrigin.ForegroundServiceItem, seeds.First(s => s.MediaId == "direct-fg").Origin);

        state.Clear(LayerKind.Videos);
        seeds = state.TransportSeeds();
        Assert.DoesNotContain(seeds, s => s.MediaId == "direct-fg"); // a cleared layer drops its seed
        Assert.Equal(LayerKind.LoopingVideos, seeds.First(s => s.MediaId == "cue-bg").Layer);
    }

    [Fact]
    public void ASlidesOwnBackgroundIsACueNotADirectFire()
    {
        var slide = new Slide { Id = "s", Name = "S", Objects = [], Background = new CueMedia { MediaId = "bumper", Layer = CueMediaLayer.Videos } };
        var state = new ShowState();
        state.Fire(slide);
        Assert.Equal(TransportOrigin.ForegroundCue, state.TransportSeeds().Single().Origin);
    }

    [Fact]
    public void SlideObjectSeedsCoverFillsAndLegacyMediaObjects()
    {
        var shape = new SlideObject { Id = "s", ObjectKind = SlideObjectKind.Shape, Name = "Panel", Text = "", Fill = new ObjectFill { FillKind = FillKind.Media, MediaId = "fill-video" } };
        var legacy = new SlideObject { Id = "m", ObjectKind = SlideObjectKind.Media, Name = "Pic", Text = "", MediaId = "object-video" };
        var state = new ShowState();
        state.Fire(new Slide { Id = "slide", Name = "One", Objects = [shape, legacy] });
        var ids = state.TransportSeeds().Select(s => s.MediaId).ToList();
        Assert.Contains("fill-video", ids);
        Assert.Contains("object-video", ids);
    }

    // MARK: Reading a transport

    private static MediaTransportState State(double position, DateTimeOffset at, bool playing = true, bool looping = false, double duration = 30, double rate = 1) =>
        new(duration, position, playing, looping, at, rate);

    [Fact]
    public void TransportStatePositionProjectsClampsAndWraps()
    {
        var playing = new MediaTransportState(10, 4, true, false, T0);
        Assert.Equal(6, playing.PositionAt(T0.AddSeconds(2)), 4);
        Assert.Equal(10, playing.PositionAt(T0.AddSeconds(60)), 4); // non-loop must clamp at the end
        Assert.Equal(4, playing.RemainingAt(T0.AddSeconds(2)), 4);
        Assert.Equal(0, playing.RemainingAt(T0.AddSeconds(60)), 4); // remaining must clamp at 0

        var paused = new MediaTransportState(10, 4, false, false, T0);
        Assert.Equal(4, paused.PositionAt(T0.AddSeconds(60)), 4); // a paused playhead must freeze

        var looping = new MediaTransportState(10, 8, true, true, T0);
        Assert.Equal(3, looping.PositionAt(T0.AddSeconds(5)), 4); // a loop wraps through the duration
        Assert.Equal(3, looping.PositionAt(T0.AddSeconds(25)), 4); // and keeps wrapping

        var junk = new MediaTransportState(10, 40, false, false, T0);
        Assert.Equal(10, junk.PositionAt(T0), 4); // an out-of-range position clamps
    }

    [Fact]
    public void APlayingVideoReadAgainTracksItsHeldAnchor()
    {
        var held = State(5, T0);
        var later = T0.AddSeconds(0.25);
        Assert.True(held.Tracks(State(5.26, later), later));
        Assert.True(held.Tracks(State(25.1, T0.AddSeconds(20)), T0.AddSeconds(20)));
        Assert.False(held.Tracks(State(5, T0, duration: 90), T0)); // a different length is news
    }

    [Fact]
    public void ASeekOrStallPastTheToleranceIsNews()
    {
        var held = State(5, T0);
        var later = T0.AddSeconds(1);
        Assert.False(held.Tracks(State(10, later), later)); // seek forward
        Assert.False(held.Tracks(State(5.5, later), later)); // stalled half a second
        Assert.True(held.Tracks(State(5.8, later), later)); // 0.2 s of jitter
    }

    [Fact]
    public void PlayLoopAndRateFlipsAreNews()
    {
        var held = State(5, T0);
        Assert.False(held.Tracks(State(5, T0, playing: false), T0));
        Assert.False(held.Tracks(State(5, T0, looping: true), T0));
        Assert.False(held.Tracks(State(5, T0, rate: 2), T0));
    }

    [Fact]
    public void APausedPlayheadIsExactToAFrame()
    {
        var held = State(5, T0, playing: false);
        var later = T0.AddSeconds(10);
        Assert.True(held.Tracks(State(5, later, playing: false), later)); // holding still
        Assert.False(held.Tracks(State(5.1, later, playing: false), later)); // a paused scrub
    }

    [Fact]
    public void ALoopCrossingItsSeamStillTracks()
    {
        var held = State(29.9, T0, looping: true);
        var later = T0.AddSeconds(0.2);
        Assert.True(held.Tracks(State(0.12, later, looping: true), later));
        Assert.False(held.Tracks(State(15, later, looping: true), later));
    }

    [Fact]
    public void AStoppedPassIsFinishedButALoopNeverIs()
    {
        Assert.True(new MediaTransportState(30, 29.97, false, false, T0).IsFinished(T0));
        Assert.False(new MediaTransportState(30, 29.97, true, false, T0).IsFinished(T0)); // still playing
        Assert.False(new MediaTransportState(30, 29.97, false, true, T0).IsFinished(T0)); // loops never finish
        Assert.False(new MediaTransportState(30, 12, false, false, T0).IsFinished(T0)); // paused part-way
    }

    [Fact]
    public void ATrimmedFileReadsAsItsPass()
    {
        // A 60 s file trimmed to 10–40 s, read 25 s into the file: 15 s into a 30 s pass.
        var pass = MediaTransportState.Pass(25, 60, 10, 40, isPlaying: true, isLooping: false, T0, rate: 2);
        Assert.Equal((30, 15, 2.0), (pass.Duration, pass.Position, pass.Rate));
        Assert.Equal(35, MediaTransportState.FileTime(25, 10)); // seeking 25 s into the pass
        // No trim: the file is the pass; a bad out point is ignored; positions clamp into the pass.
        Assert.Equal(60, MediaTransportState.Pass(5, 60, null, null, true, false, T0).Duration);
        Assert.Equal(50, MediaTransportState.Pass(5, 60, 10, 4, true, false, T0).Duration);
        Assert.Equal(0, MediaTransportState.Pass(5, 60, 10, null, true, false, T0).Position);
        Assert.Equal(1, MediaTransportState.Pass(5, 60, null, null, true, false, T0, rate: 0).Rate);
    }
}
