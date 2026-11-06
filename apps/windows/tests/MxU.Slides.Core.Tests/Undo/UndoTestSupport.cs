using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Tests.Library;
using MxU.Slides.Core.Undo;

namespace MxU.Slides.Core.Tests.Undo;

/// <summary>A clock the test moves by hand, for the coalescing window.</summary>
internal sealed class StepClock : TimeProvider
{
    private DateTimeOffset now = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => now;

    public void Advance(double seconds) => now = now.AddSeconds(seconds);
}

/// <summary>A library with an undo scope, and small documents to edit.</summary>
public abstract class UndoTest : ScratchFolderTest
{
    internal readonly StepClock Clock = new();

    protected UndoTest()
    {
        Library = NewLibrary();
        Library.Undo.Clock = Clock;
        Scope = Library.Undo.NewScope("Main window");
    }

    protected LibraryStore Library { get; }

    protected UndoScope Scope { get; }

    /// <summary>Saves inside <paramref name="scope"/> (the main scope by default), as an edit made in that window.</summary>
    protected void Edit<T>(T doc, UndoScope? scope = null) where T : class, ILibraryDocument
    {
        using ((scope ?? Scope).Use()) Library.Save(doc);
    }

    /// <summary>Saves outside any scope: set-up, or someone else's edit (a remote, the Mac).</summary>
    protected void SaveQuietly<T>(T doc) where T : class, ILibraryDocument
    {
        using (Library.Undo.Unrecorded()) Library.Save(doc);
    }

    /// <summary>The document as the library holds it now.</summary>
    protected Presentation Held(string id = "deck") => Library.Get<Presentation>(id)!;

    /// <summary>The document as it is on disk now (read afresh).</summary>
    protected Presentation OnDisk(string id = "deck")
    {
        var fresh = new LibraryStore(Library.Root);
        fresh.Load();
        return fresh.Get<Presentation>(id)!;
    }

    public static Presentation Deck(string id = "deck", params string[] slideTexts) => new()
    {
        Id = id,
        Name = "Sunday",
        PresentationKind = PresentationKind.Deck,
        ThemeId = "",
        Slides = [.. (slideTexts.Length == 0 ? ["One", "Two", "Three"] : slideTexts).Select((text, i) => Slide($"s{i + 1}", text))],
    };

    public static Slide Slide(string id, string text) => new()
    {
        Id = id,
        Name = "",
        Objects = [new SlideObject { Id = id + "-text", ObjectKind = SlideObjectKind.Text, Name = "Text", Text = text }],
    };

    public static Service Service(string id = "service", params string[] refs) => new()
    {
        Id = id,
        Name = "Sunday service",
        ServiceDate = "2026-10-25",
        Items = [.. refs.Select(r => new ServiceItem { Id = "item-" + r, ItemKind = ServiceItemKind.Presentation, Name = r, RefId = r })],
    };

    /// <summary>The text of a slide of the held deck.</summary>
    protected string TextOf(string slideId, string deckId = "deck") =>
        Held(deckId).Slides.Single(s => s.Id == slideId).Objects[0].Text;
}
