using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Automation;

// Windows: opening a new music engine waits a fraction of a second for the sound device, on
// the caller's thread (the UI thread, for a timeline). So a timeline with music asks the music
// to get ready when it starts playing, and its first clip (and the slides after it) stay on cue.
public class TimelineMusicWarmTests
{
    private sealed class CountingMusic : ITimelineMusic
    {
        public int Prepared { get; private set; }

        public MusicPlayerState? Player(string audioItemId) => null;

        public MusicPlayerState? ClipPlayer => null;

        public void Seek(string audioItemId, double seconds) { }

        public void Play(TimelineMusicClip clip) { }

        public void Stop(string audioItemId, double? fadeSeconds = null, AudioFadeCurve curve = AudioFadeCurve.Linear) { }

        public void Prepare() => Prepared++;
    }

    [Fact]
    public void ATimelineWithMusicGetsTheMusicReadyWhenItPlays()
    {
        var music = new CountingMusic();
        var host = new TimelineHost { Music = music };
        var deck = Make.Deck("song", ("v1", "v"));
        deck.Timelines = [Make.Timeline("t", cues: [Make.Cue("m", "audio", 5, 3, "track")])];
        host.Decks["song"] = deck;
        var clock = new ManualClock(new DateTimeOffset(2026, 7, 12, 9, 0, 0, TimeSpan.Zero));
        var player = new TimelinePlayer(host, new RecordingShow(), new MemoryLibrary(), clock: clock);
        player.Play("item", deck, null);
        player.Stop();
        Assert.Equal(1, music.Prepared);

        // No music on the timeline: nothing to get ready.
        deck.Timelines = [Make.Timeline("t", cues: [Make.Cue("o", "overlays", 1, 2, overlayId: "x")])];
        player.Play("item", deck, null);
        player.Stop();
        Assert.Equal(1, music.Prepared);
    }
}
