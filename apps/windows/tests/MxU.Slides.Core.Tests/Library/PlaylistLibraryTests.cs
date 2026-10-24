using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Library;

// The playlist rules follow the Mac's AppModel (createPlaylist, addToPlaylist,
// insertIntoPlaylist, handlePlaylistDrop, removePlaylistEntry, movePlaylistEntries) and
// Playlist.effectiveKind; the Mac has no tests of its own for them.
public class PlaylistLibraryTests
{
    private static Playlist Music(params string[] songs)
    {
        var playlist = PlaylistLibrary.Create();
        foreach (var song in songs) PlaylistLibrary.Add(playlist, song, PlaylistRefKind.Audio);
        return playlist;
    }

    private static string[] Songs(Playlist playlist) => [.. playlist.Entries.Select(e => e.RefId)];

    [Fact]
    public void ANewPlaylistIsAnEmptyMusicPlaylistThatPlaysThroughOnce()
    {
        var playlist = PlaylistLibrary.Create();
        Assert.Equal(PlaylistLibrary.UntitledMusicName, playlist.Name);
        Assert.Equal(PlaylistKind.Audio, playlist.PlaylistKind);
        Assert.Empty(playlist.Entries);
        Assert.Equal(PlaybackMode.PlayAll, playlist.PlaybackMode);
        Assert.Null(playlist.CrossfadeSeconds);
        Assert.Equal("Walk-in", PlaylistLibrary.Create(name: "  Walk-in ").Name);
    }

    [Fact]
    public void APlaylistThatDoesntSayIsMusicUnlessItsFirstTrackIsMedia()
    {
        var old = new Playlist { Id = "p", Name = "Old", Entries = [], PlaybackMode = PlaybackMode.PlayAll };
        Assert.True(PlaylistLibrary.IsMusic(old));
        old.Entries.Add(new PlaylistEntry { Id = "e", RefKind = PlaylistRefKind.Media, RefId = "m" });
        Assert.Equal(PlaylistKind.Media, PlaylistLibrary.EffectiveKind(old));
        old.PlaylistKind = PlaylistKind.Audio;
        Assert.True(PlaylistLibrary.IsMusic(old));
    }

    [Fact]
    public void TracksAreAddedAtTheEndOrAtAPlaceAndOnlyOfTheRightKind()
    {
        var playlist = Music("a", "b");
        var again = PlaylistLibrary.Add(playlist, "a", PlaylistRefKind.Audio);
        Assert.NotNull(again);
        Assert.Equal(new[] { "a", "b", "a" }, Songs(playlist));
        Assert.Equal(3, playlist.Entries.Select(e => e.Id).Distinct().Count());
        PlaylistLibrary.Insert(playlist, "c", PlaylistRefKind.Audio, 1);
        PlaylistLibrary.Insert(playlist, "d", PlaylistRefKind.Audio, 99);
        Assert.Equal(new[] { "a", "c", "b", "a", "d" }, Songs(playlist));
        Assert.Null(PlaylistLibrary.Add(playlist, "video", PlaylistRefKind.Media));
        Assert.Null(PlaylistLibrary.Add(playlist, "", PlaylistRefKind.Audio));
    }

    [Fact]
    public void RemovingATrackTakesOnlyThatEntry()
    {
        var playlist = Music("a", "b", "a");
        Assert.True(PlaylistLibrary.Remove(playlist, playlist.Entries[2].Id));
        Assert.Equal(new[] { "a", "b" }, Songs(playlist));
        Assert.False(PlaylistLibrary.Remove(playlist, "missing"));
    }

    [Fact]
    public void MovingByOffsetsWorksLikeSwiftsMove()
    {
        var playlist = Music("a", "b", "c", "d", "e");
        // Swift: [a b c d e].move(fromOffsets: [1, 3], toOffset: 0) → [b d a c e]
        Assert.True(PlaylistLibrary.Move(playlist, [1, 3], 0));
        Assert.Equal(new[] { "b", "d", "a", "c", "e" }, Songs(playlist));
        // …toOffset: 5 (the end) → [b a c e d]
        Assert.True(PlaylistLibrary.Move(playlist, [1], 5));
        Assert.Equal(new[] { "b", "a", "c", "e", "d" }, Songs(playlist));
        // Moving a track to where it is changes nothing.
        Assert.False(PlaylistLibrary.Move(playlist, [2], 3));
        Assert.False(PlaylistLibrary.Move(playlist, [9], 0));
    }

    [Fact]
    public void ATrackDroppedBeforeAnotherLandsThereAndOnItselfStays()
    {
        var playlist = Music("a", "b", "c");
        var (a, b, c) = (playlist.Entries[0].Id, playlist.Entries[1].Id, playlist.Entries[2].Id);
        Assert.True(PlaylistLibrary.MoveBefore(playlist, c, a));
        Assert.Equal(new[] { "c", "a", "b" }, Songs(playlist));
        Assert.True(PlaylistLibrary.MoveBefore(playlist, c, null));
        Assert.Equal(new[] { "a", "b", "c" }, Songs(playlist));
        Assert.False(PlaylistLibrary.MoveBefore(playlist, b, b));
        Assert.False(PlaylistLibrary.MoveBefore(playlist, b, c));
    }

    [Fact]
    public void UpAndDownMoveATrackOnePlaceAndStopAtTheEnds()
    {
        var playlist = Music("a", "b", "c");
        var b = playlist.Entries[1].Id;
        Assert.True(PlaylistLibrary.MoveBy(playlist, b, -1));
        Assert.False(PlaylistLibrary.MoveBy(playlist, b, -1));
        Assert.Equal(new[] { "b", "a", "c" }, Songs(playlist));
        Assert.True(PlaylistLibrary.MoveBy(playlist, b, +2));
        Assert.Equal(new[] { "a", "c", "b" }, Songs(playlist));
    }

    [Fact]
    public void RepeatShuffleCrossfadeAndNameSayWhenTheyChange()
    {
        var playlist = Music("a");
        Assert.True(PlaylistLibrary.SetRepeat(playlist, PlaybackMode.LoopPlaylist));
        Assert.False(PlaylistLibrary.SetRepeat(playlist, PlaybackMode.LoopPlaylist));
        Assert.False(PlaylistLibrary.SetShuffle(playlist, false));
        Assert.True(PlaylistLibrary.SetShuffle(playlist, true));
        Assert.True(PlaylistLibrary.SetCrossfade(playlist, -2));
        Assert.Equal(0, playlist.CrossfadeSeconds);
        Assert.True(PlaylistLibrary.SetCrossfade(playlist, null));
        Assert.False(PlaylistLibrary.Rename(playlist, "   "));
        Assert.True(PlaylistLibrary.Rename(playlist, " Offering "));
        Assert.Equal("Offering", playlist.Name);
        Assert.False(PlaylistLibrary.Rename(playlist, "Offering"));
    }

    [Fact]
    public void UpdatingSavesACopyAndLeavesTheOldDocumentAlone()
    {
        var root = Path.Combine(Path.GetTempPath(), "mxu-playlist-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LibraryStore(root);
            store.Load();
            var playlist = Music("a", "b");
            store.Save(playlist);
            var held = store.Get<Playlist>(playlist.Id)!;
            var saved = PlaylistLibrary.Update(store, playlist.Id, p => PlaylistLibrary.Remove(p, p.Entries[0].Id));
            Assert.NotNull(saved);
            Assert.Equal(new[] { "b" }, Songs(store.Get<Playlist>(playlist.Id)!));
            Assert.Equal(new[] { "a", "b" }, Songs(held));
            Assert.Null(PlaylistLibrary.Update(store, playlist.Id, _ => false));
            Assert.Null(PlaylistLibrary.Update(store, "missing", _ => true));
            store.Save(new Playlist { Id = "media", Name = "Loops", PlaylistKind = PlaylistKind.Media, Entries = [], PlaybackMode = PlaybackMode.PlayAll });
            Assert.Equal(new[] { playlist.Id }, PlaylistLibrary.MusicPlaylists(store).Select(p => p.Id));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }
}
