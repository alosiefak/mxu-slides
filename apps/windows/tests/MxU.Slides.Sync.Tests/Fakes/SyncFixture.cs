using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Sync.Automerge;
using MxU.Slides.Sync.Engine;
using MxU.Slides.Sync.Format;

namespace MxU.Slides.Sync.Tests.Fakes;

/// <summary>
/// A Windows library and a shared "Mac" folder in a scratch folder, and a sync engine between them
/// that syncs only when told (no folder watching), with a clock the test moves.
/// </summary>
internal sealed class SyncFixture : IDisposable
{
    private readonly List<SyncEngine> engines = [];

    public SyncFixture(bool makeMacLibrary = true)
    {
        Root = Path.Combine(Path.GetTempPath(), "MxU sync tests", Guid.NewGuid().ToString("N"));
        Shared = Path.Combine(Root, "Shared Library");
        Directory.CreateDirectory(Shared);
        if (makeMacLibrary) MacSimulator.MakeLibrary(Shared);
        Store = new LibraryStore(Path.Combine(Root, "Windows Library"));
        Store.Load();
    }

    public string Root { get; }
    public string Shared { get; }
    public LibraryStore Store { get; }
    public DateTimeOffset Clock { get; set; } = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);

    private SyncEngine? engine;

    public SyncEngine Engine => engine ??= NewEngine();

    public SyncEngine NewEngine(string? shared = null)
    {
        var made = new SyncEngine(Store, shared ?? Shared, new SyncOptions
        {
            Watch = false,
            ScanInterval = TimeSpan.FromHours(1),
            LocalDelay = TimeSpan.FromHours(1),
            MissingFor = TimeSpan.FromSeconds(5),
            Now = () => Clock,
        });
        made.Start();
        engines.Add(made);
        return made;
    }

    /// <summary>One pass, then (when the clock should have passed for a deletion to count) a later one.</summary>
    public void Sync(bool twice = false)
    {
        Engine.SyncNowAndWait();
        if (!twice) return;
        Clock += TimeSpan.FromSeconds(10);
        Engine.SyncNowAndWait();
    }

    public void MacSave(AmDocument document, MacKind kind, string id) => MacSimulator.Save(document, Shared, kind, id);

    public AmDocument MacOpen(MacKind kind, string id) => MacSimulator.Open(Shared, kind, id);

    public bool MacHas(MacKind kind, string id) => File.Exists(Path.Combine(Shared, kind.DirectoryName, id + ".automerge"));

    public byte[] MacBytes(MacKind kind, string id) => File.ReadAllBytes(Path.Combine(Shared, kind.DirectoryName, id + ".automerge"));

    /// <summary>The Mac reads its copy of a document.</summary>
    public T MacRead<T>(MacKind kind, string id)
    {
        using var document = MacOpen(kind, id);
        return MacSimulator.Read<T>(document, kind);
    }

    /// <summary>The Mac edits a document: opens it, changes it, saves it (merging the folder's copy first).</summary>
    public void MacEdit<T>(MacKind kind, string id, Action<T> change) where T : class
    {
        using var document = MacOpen(kind, id);
        var value = MacSimulator.Read<T>(document, kind);
        change(value);
        MacSimulator.Update(document, value, kind);
        MacSave(document, kind, id);
    }

    /// <summary>Edits a Windows document the way the app does: a copy of what's stored, changed, saved.</summary>
    public void WindowsEdit<T>(string id, Action<T> change) where T : class, ILibraryDocument
    {
        var copy = MxuJson.Clone(Store.Get<T>(id)!);
        change(copy);
        Store.Save(copy);
    }

    public void Dispose()
    {
        foreach (var made in engines) made.Dispose();
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
