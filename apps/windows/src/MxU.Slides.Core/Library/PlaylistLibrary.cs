using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Library;

/// <summary>
/// The rules for music playlists: making, naming and deleting them, adding, removing and
/// reordering their tracks, and their Repeat, Shuffle and crossfade. These change a playlist
/// in memory and say whether anything changed; <see cref="Update"/> applies one to a copy of
/// the stored document and saves the copy (the music engine reads playlists from another
/// thread, so the stored one is never changed under it). Port of the Mac's AppModel
/// (createPlaylist, addToPlaylist, insertIntoPlaylist, handlePlaylistDrop,
/// removePlaylistEntry, movePlaylistEntries, updatePlaylist) and Playlist.effectiveKind.
/// </summary>
public static class PlaylistLibrary
{
    /// <summary>What the Mac calls a new music playlist.</summary>
    public const string UntitledMusicName = "Untitled Music Playlist";

    /// <summary>
    /// Music or media. Older playlists don't say; then the first track decides, and an
    /// empty one is music (the Mac's effectiveKind).
    /// </summary>
    public static PlaylistKind EffectiveKind(Playlist playlist) =>
        playlist.PlaylistKind ?? (playlist.Entries.FirstOrDefault()?.RefKind == PlaylistRefKind.Media ? PlaylistKind.Media : PlaylistKind.Audio);

    public static bool IsMusic(Playlist playlist) => EffectiveKind(playlist) == PlaylistKind.Audio;

    /// <summary>The kind of track a playlist takes: songs for a music playlist, media for a media one.</summary>
    public static PlaylistRefKind Accepts(Playlist playlist) =>
        EffectiveKind(playlist) == PlaylistKind.Media ? PlaylistRefKind.Media : PlaylistRefKind.Audio;

    /// <summary>An empty playlist that plays through once (the Mac's createPlaylist).</summary>
    public static Playlist Create(PlaylistKind kind = PlaylistKind.Audio, string? name = null) => new()
    {
        Id = LibraryStore.NewId(),
        Name = string.IsNullOrWhiteSpace(name) ? UntitledMusicName : name.Trim(),
        PlaylistKind = kind,
        Entries = [],
        PlaybackMode = PlaybackMode.PlayAll,
    };

    /// <summary>Renames a playlist; false (and no change) for a blank or unchanged name.</summary>
    public static bool Rename(Playlist playlist, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0 || trimmed == playlist.Name) return false;
        playlist.Name = trimmed;
        return true;
    }

    /// <summary>Adds a track at the end (each entry has its own id, so the same song can be in twice).</summary>
    public static PlaylistEntry? Add(Playlist playlist, string itemId, PlaylistRefKind kind) =>
        Insert(playlist, itemId, kind, playlist.Entries.Count);

    /// <summary>Adds a track at <paramref name="index"/> (clamped). Null when the playlist doesn't take that kind of track.</summary>
    public static PlaylistEntry? Insert(Playlist playlist, string itemId, PlaylistRefKind kind, int index)
    {
        if (kind != Accepts(playlist) || string.IsNullOrEmpty(itemId)) return null;
        var entry = new PlaylistEntry { Id = LibraryStore.NewId(), RefKind = kind, RefId = itemId };
        playlist.Entries.Insert(Math.Clamp(index, 0, playlist.Entries.Count), entry);
        return entry;
    }

    public static bool Remove(Playlist playlist, string entryId) => playlist.Entries.RemoveAll(e => e.Id == entryId) > 0;

    /// <summary>
    /// Moves the tracks at <paramref name="fromOffsets"/> so they sit, in their order, before
    /// the track that was at <paramref name="toOffset"/> (the end when it's the count): Swift's
    /// <c>move(fromOffsets:toOffset:)</c>, which the Mac's list reordering uses.
    /// </summary>
    public static bool Move(Playlist playlist, IEnumerable<int> fromOffsets, int toOffset)
    {
        var entries = playlist.Entries;
        var moving = fromOffsets.Where(i => i >= 0 && i < entries.Count).Distinct().Order().ToList();
        if (moving.Count == 0) return false;
        var target = Math.Clamp(toOffset, 0, entries.Count);
        var before = entries.Select(e => e.Id).ToList();
        var picked = moving.Select(i => entries[i]).ToList();
        var insertAt = target - moving.Count(i => i < target);
        foreach (var index in moving.OrderDescending()) entries.RemoveAt(index);
        entries.InsertRange(insertAt, picked);
        return !before.SequenceEqual(entries.Select(e => e.Id));
    }

    /// <summary>
    /// Moves one track to just before another (null: to the end), the way a track dragged
    /// within its playlist lands (the Mac's handlePlaylistDrop). Dropping a track on itself does nothing.
    /// </summary>
    public static bool MoveBefore(Playlist playlist, string movingId, string? beforeEntryId)
    {
        if (movingId == beforeEntryId) return false;
        var from = playlist.Entries.FindIndex(e => e.Id == movingId);
        if (from < 0) return false;
        var before = playlist.Entries.Select(e => e.Id).ToList();
        var entry = playlist.Entries[from];
        playlist.Entries.RemoveAt(from);
        var index = beforeEntryId is null ? -1 : playlist.Entries.FindIndex(e => e.Id == beforeEntryId);
        playlist.Entries.Insert(index < 0 ? playlist.Entries.Count : index, entry);
        return !before.SequenceEqual(playlist.Entries.Select(e => e.Id));
    }

    /// <summary>Moves a track up (−1) or down (+1) one place; Windows' ↑ ↓ buttons (the Mac drags).</summary>
    public static bool MoveBy(Playlist playlist, string entryId, int delta)
    {
        var from = playlist.Entries.FindIndex(e => e.Id == entryId);
        if (from < 0) return false;
        var to = Math.Clamp(from + delta, 0, playlist.Entries.Count - 1);
        if (to == from) return false;
        var entry = playlist.Entries[from];
        playlist.Entries.RemoveAt(from);
        playlist.Entries.Insert(to, entry);
        return true;
    }

    /// <summary>Off (play through once), Loop Playlist or Loop Single.</summary>
    public static bool SetRepeat(Playlist playlist, PlaybackMode mode)
    {
        if (playlist.PlaybackMode == mode) return false;
        playlist.PlaybackMode = mode;
        return true;
    }

    public static bool SetShuffle(Playlist playlist, bool on)
    {
        if ((playlist.Shuffle ?? false) == on) return false;
        playlist.Shuffle = on;
        return true;
    }

    /// <summary>The playlist's own crossfade (0 = cut), or null to use the room's.</summary>
    public static bool SetCrossfade(Playlist playlist, double? seconds)
    {
        var value = seconds is { } s ? Math.Max(0, s) : (double?)null;
        if (playlist.CrossfadeSeconds == value) return false;
        playlist.CrossfadeSeconds = value;
        return true;
    }

    /// <summary>
    /// Applies <paramref name="change"/> to a copy of the stored playlist and saves the copy
    /// when it says something changed. Returns the saved copy, or null (no such playlist, or no change).
    /// </summary>
    public static Playlist? Update(LibraryStore store, string id, Func<Playlist, bool> change)
    {
        if (store.Get<Playlist>(id) is not { } stored) return null;
        var copy = MxuJson.Clone(stored);
        if (!change(copy)) return null;
        store.Save(copy);
        return copy;
    }

    /// <summary>The music playlists, by name (media playlists left out).</summary>
    public static List<Playlist> MusicPlaylists(LibraryStore store) => [.. store.All<Playlist>().Where(IsMusic)];
}
