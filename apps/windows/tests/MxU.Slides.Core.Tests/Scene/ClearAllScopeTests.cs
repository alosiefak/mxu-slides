using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Tests.Scene;

// Port of the Clear All cases in SlideSceneTests/ShowStateTests.swift. Windows' ShowState
// has no music cue list (the audio engine knows what plays), so the liveAudio cases are
// covered by the app's Clear Music instead.
public class ClearAllScopeTests
{
    private static CueMedia Cue(string id) => new() { MediaId = id };

    private static Slide Slide(string id) => new() { Id = id, Name = id, Objects = [] };

    private static Presentation Presentation(params Slide[] slides) =>
        new() { Id = "p", Name = "Song", PresentationKind = PresentationKind.Song, ThemeId = "", Slides = [.. slides] };

    [Fact]
    public void ClearAllScopeDefaultsMigratesAndRoundTrips()
    {
        Assert.Equal(new HashSet<ShowFunction> { ShowFunction.Inputs }, ClearAllScope.Skips(null, null));
        Assert.Equal(new HashSet<ShowFunction> { ShowFunction.Inputs }, ClearAllScope.Skips(null, true));
        Assert.Equal(new HashSet<ShowFunction> { ShowFunction.Inputs, ShowFunction.Audio }, ClearAllScope.Skips(null, false)); // the old Music switch carries over
        Assert.Empty(ClearAllScope.Skips("", false)); // a saved checklist wins
        Assert.Equal(new HashSet<ShowFunction> { ShowFunction.Audio, ShowFunction.Alerts },
            ClearAllScope.Skips(ClearAllScope.Encode([ShowFunction.Audio, ShowFunction.Alerts]), null));
        Assert.Equal(new HashSet<ShowFunction> { ShowFunction.Audio }, ClearAllScope.Skips("audio,signage", null)); // a skip saved before Digital Signage left drops
        Assert.Equal(new HashSet<ShowFunction> { ShowFunction.Inputs }, ClearAllScope.Skips("tracks,inputs", null)); // the old Tracks row is gone: Music covers it
        Assert.Equal(ShowFunctions.All.Except([ShowFunction.Inputs]).ToHashSet(), ClearAllScope.Swept([ShowFunction.Inputs]));

        // The Mac stores the checklist in UserDefaults; here Set returns what to store.
        var (stored, includesAudio) = ClearAllScope.Set(ShowFunction.Audio, swept: false, null, null);
        Assert.Equal(ShowFunctions.All.Except([ShowFunction.Inputs, ShowFunction.Audio]).ToHashSet(), ClearAllScope.Swept(ClearAllScope.Skips(stored, includesAudio)));
        Assert.False(includesAudio); // older builds still read Music from the old switch
        (stored, includesAudio) = ClearAllScope.Set(ShowFunction.Inputs, swept: true, stored, includesAudio);
        Assert.Equal(ShowFunctions.All.Except([ShowFunction.Audio]).ToHashSet(), ClearAllScope.Swept(ClearAllScope.Skips(stored, includesAudio)));
    }

    [Fact]
    public void EncodingFollowsTheFunctionsOrder()
    {
        Assert.Equal("audio,alerts", ClearAllScope.Encode([ShowFunction.Alerts, ShowFunction.Audio]));
        Assert.Equal("", ClearAllScope.Encode([]));
    }

    [Fact]
    public void ClearMusicStopsTracksAndOnlyATracksClockedTimeline()
    {
        Assert.True(TracksClear.StopsTracks(new HashSet<ShowFunction> { ShowFunction.Audio })); // Clear Music: popup, menu, action, MIDI, API
        foreach (var function in ShowFunctions.All.Where(f => f != ShowFunction.Audio))
            Assert.False(TracksClear.StopsTracks(new HashSet<ShowFunction> { function }), $"Clear {function.DisplayName()} leaves tracks");
        Assert.True(TracksClear.StopsTracks(ClearAllScope.Swept(ClearAllScope.DefaultSkips)));
        Assert.False(TracksClear.StopsTracks(ClearAllScope.Swept(ClearAllScope.Skips(null, false))));
        Assert.True(TracksClear.StopsTimeline(ownClockPlaying: true, playingTracks: true)); // its clock is its tracks
        Assert.False(TracksClear.StopsTimeline(ownClockPlaying: false, playingTracks: true)); // timecode drives it: keeps following
    }

    [Fact]
    public void ClearAllLeavesSkippedInputsUp()
    {
        var verse = Slide("verse1");
        var show = new ShowState();
        show.Fire(Cue("input::item::cam1"), LayerKind.VideoInput);
        show.Fire(Cue("bumper"), LayerKind.Videos);
        show.Fire(verse, Presentation(verse));
        show.ClearAll(ClearAllScope.Swept(ClearAllScope.DefaultSkips));
        Assert.Null(show.Live);
        Assert.Equal("input::item::cam1", show.LiveMedia[LayerKind.VideoInput].MediaId); // panic Clear All keeps the camera
        Assert.False(show.LiveMedia.ContainsKey(LayerKind.Videos));
        show.ClearAll();
        Assert.Empty(show.LiveMedia); // ticked, Inputs clears with everything else
    }

    [Fact]
    public void ClearLayerVideoInputStillSweepsTheCamera()
    {
        var show = new ShowState();
        show.Fire(Cue("input::item::cam1"), LayerKind.VideoInput);
        show.Clear(LayerKind.VideoInput);
        Assert.Empty(show.LiveMedia);
    }

    [Fact]
    public void FunctionsHaveTheMacsNames()
    {
        Assert.Equal(["Slides", "Media", "Overlays", "Music", "Alerts", "Inputs"], ShowFunctions.All.Select(f => f.DisplayName()));
        Assert.Equal(ShowFunction.Audio, ShowFunctions.FromApiName("tracks"));
        Assert.Equal(ShowFunction.Inputs, ShowFunctions.Parse("inputs"));
        Assert.Null(ShowFunctions.Parse("signage"));
    }
}
