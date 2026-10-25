using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Library;

public sealed class WindowsExtrasTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "WindowsExtras-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed record Push(string Direction, double Seconds);

    [Fact]
    public void AnExtraRoundTripsAndIsKeptOutsideTheDocumentFolders()
    {
        var extras = new WindowsExtras(root);
        extras.Save("transitions", "slide-1", new Push("left", 0.8));
        Assert.Equal(new Push("left", 0.8), extras.Get<Push>("transitions", "slide-1"));
        Assert.True(File.Exists(Path.Combine(root, WindowsExtras.FolderName, "transitions", "slide-1.json")));
        Assert.Equal(["slide-1"], extras.Owners("transitions"));
        Assert.Null(extras.Get<Push>("transitions", "slide-2"));
    }

    [Fact]
    public void AnExtraStepsAsideWhenTheSharedPartChanged()
    {
        var extras = new WindowsExtras(root);
        var shared = new Transition { TransitionKind = TransitionKind.Dissolve, DurationSeconds = 0.8 };
        var basis = WindowsExtras.Fingerprint(shared);
        extras.Save("transitions", "slide-1", new Push("left", 0.8), basis);
        Assert.NotNull(extras.Get<Push>("transitions", "slide-1", WindowsExtras.Fingerprint(shared)));

        // A Mac changed the slide's transition to a fade to black: the Mac's choice wins.
        var edited = new Transition { TransitionKind = TransitionKind.FadeBlack, DurationSeconds = 0.8 };
        Assert.Null(extras.Get<Push>("transitions", "slide-1", WindowsExtras.Fingerprint(edited)));
    }

    [Fact]
    public void DeletingAnExtraTellsListeners()
    {
        var extras = new WindowsExtras(root);
        var changes = new List<(string, string)>();
        extras.Changed += (kind, owner) => changes.Add((kind, owner));
        extras.Save("production", "combo-9", new Push("up", 1));
        extras.Delete("production", "combo-9");
        extras.Delete("production", "combo-9"); // already gone: no news
        Assert.Equal([("production", "combo-9"), ("production", "combo-9")], changes);
        Assert.Empty(extras.Owners("production"));
    }

    [Fact]
    public void AnIdWithCharactersAFileNameCantHoldStillWorks()
    {
        var extras = new WindowsExtras(root);
        extras.Save("transitions", "mac:slide/7?", new Push("right", 2));
        Assert.Equal(new Push("right", 2), extras.Get<Push>("transitions", "mac:slide/7?"));
        Assert.Equal(["mac:slide/7?"], extras.Owners("transitions"));
    }

    [Fact]
    public void AnUnreadableExtraIsTreatedAsNone()
    {
        var extras = new WindowsExtras(root);
        var folder = Path.Combine(root, WindowsExtras.FolderName, "transitions");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "slide-1.json"), "{ not json");
        Assert.Null(extras.Get<Push>("transitions", "slide-1"));
    }
}
