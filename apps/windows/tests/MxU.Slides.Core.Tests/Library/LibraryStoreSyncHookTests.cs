using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Library;

/// <summary>
/// The library's hook for library sync (Phase 10): sync saves and deletes only over the file it
/// read, so it can never overwrite an edit saved here a moment before.
/// </summary>
public sealed class LibraryStoreSyncHookTests : ScratchFolderTest
{
    private static Presentation Song(string name) => new() { Id = "song", Name = name, PresentationKind = PresentationKind.Song, ThemeId = "" };

    [Fact]
    public void SaveIfUnchangedSavesOverTheFileItWasGiven()
    {
        var library = NewLibrary();
        library.Save(Song("First"));
        var read = File.ReadAllBytes(library.DocumentPath(typeof(Presentation), "song"));
        var heard = 0;
        library.Changed += (_, _) => heard++;
        Assert.True(library.SaveIfUnchanged(Song("From the Mac"), read));
        Assert.Equal("From the Mac", library.Get<Presentation>("song")!.Name);
        Assert.Equal(1, heard);
    }

    [Fact]
    public void SaveIfUnchangedRefusesWhenTheFileMovedOn()
    {
        var library = NewLibrary();
        library.Save(Song("First"));
        var read = File.ReadAllBytes(library.DocumentPath(typeof(Presentation), "song"));
        library.Save(Song("Saved here a moment ago"));
        Assert.False(library.SaveIfUnchanged(Song("From the Mac"), read));
        Assert.Equal("Saved here a moment ago", library.Get<Presentation>("song")!.Name);
        Assert.False(library.SaveIfUnchanged(Song("From the Mac"), expected: null), "null means no file yet");
    }

    [Fact]
    public void SaveIfUnchangedWithNullAddsANewDocument()
    {
        var library = NewLibrary();
        Assert.True(library.SaveIfUnchanged(Song("New from the Mac"), expected: null));
        Assert.Equal("New from the Mac", NewLibraryAgain(library).Get<Presentation>("song")!.Name);
    }

    [Fact]
    public void DeleteIfUnchangedMovesToTrashOnlyTheVersionItRead()
    {
        var library = NewLibrary();
        library.Save(Song("First"));
        var read = File.ReadAllBytes(library.DocumentPath(typeof(Presentation), "song"));
        library.Save(Song("Edited"));
        Assert.False(library.DeleteIfUnchanged<Presentation>("song", read));
        Assert.NotNull(library.Get<Presentation>("song"));

        var now = File.ReadAllBytes(library.DocumentPath(typeof(Presentation), "song"));
        Assert.True(library.DeleteIfUnchanged<Presentation>("song", now));
        Assert.Null(library.Get<Presentation>("song"));
        Assert.Single(Directory.GetFiles(Path.Combine(library.TrashDirectory, "Presentations")));
    }

    private static Core.Library.LibraryStore NewLibraryAgain(Core.Library.LibraryStore library)
    {
        var again = new Core.Library.LibraryStore(library.Root);
        again.Load();
        return again;
    }
}
