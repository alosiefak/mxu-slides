using MxU.Slides.Audio.Library;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Audio.Tests;

/// <summary>"Replace Music File…" (the Mac's AppModel.replaceAudioFile), checked on a real library folder.</summary>
public sealed class MusicLibraryTests : IDisposable
{
    private readonly string root = Path.Combine(TestFiles.RunFolder, $"library-{Guid.NewGuid():N}");
    private readonly LibraryStore store;

    public MusicLibraryTests()
    {
        store = new LibraryStore(root);
        store.Load();
    }

    [Fact]
    public void ReplacingTheFileKeepsTheSongItsNameTrimAndPlaylistPlaces()
    {
        var importer = new AudioImporter(store);
        var song = importer.Import(TestAudio.Tone("short", 1, 440), name: "Walk-in")!;
        song.InPoint = 0.2;
        store.Save(song);
        var playlist = PlaylistLibrary.Create();
        PlaylistLibrary.Add(playlist, song.Id, PlaylistRefKind.Audio);
        store.Save(playlist);
        var oldFile = importer.FilePath(song)!;

        var longer = TestAudio.Tone("longer", 3, 880);
        var replaced = importer.ReplaceFile(store.Get<AudioItem>(song.Id)!, longer);

        var stored = store.Get<AudioItem>(song.Id)!;
        Assert.Same(replaced, stored);
        Assert.Equal(("Walk-in", 0.2), (stored.Name, stored.InPoint!.Value));
        Assert.NotEqual(song.FileHash, stored.FileHash);
        Assert.Equal(Path.GetFileName(longer), stored.FileName);
        Assert.InRange(stored.DurationSeconds!.Value, 2.9, 3.1);
        Assert.NotEqual(oldFile, importer.FilePath(stored));
        Assert.True(File.Exists(oldFile), "the old file stays: another song may use it");
        Assert.Equal(song.Id, store.Get<Playlist>(playlist.Id)!.Entries[0].RefId);
    }

    [Fact]
    public void AMissingOrNonMusicFileIsRefused()
    {
        var importer = new AudioImporter(store);
        var song = importer.Import(TestAudio.Tone("song", 1, 440))!;
        Assert.Throws<FileNotFoundException>(() => importer.ReplaceFile(song, Path.Combine(root, "gone.mp3")));
        var text = Path.Combine(root, "notes.txt");
        File.WriteAllText(text, "not music");
        Assert.Throws<InvalidOperationException>(() => importer.ReplaceFile(song, text));
        Assert.Equal(song.FileHash, store.Get<AudioItem>(song.Id)!.FileHash);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
