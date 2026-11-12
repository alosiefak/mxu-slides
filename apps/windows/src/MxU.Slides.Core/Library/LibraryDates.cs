using System.Text.Json;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Threading;

namespace MxU.Slides.Core.Library;

/// <summary>
/// The library's own background thread for chores nobody waits on: reading file dates, saving the
/// last-used list, making backups. One thread, below normal priority, never the shared thread
/// pool (HOW-IT-WORKS section 26), so a big backup can't hold up anything the show needs.
/// </summary>
public static class LibraryChores
{
    private static readonly Lazy<WorkQueue> queue = new(() => new WorkQueue("MxU library chores", ThreadPriority.BelowNormal));

    public static WorkQueue Queue => queue.Value;
}

/// <summary>
/// When each song, presentation, media item and piece of music was last saved and first added to
/// this library, for sorting by Date Modified and Date Added. They're the document files' own
/// dates: the last write, and the creation (which <c>File.Replace</c>, used by every save, keeps).
/// <para>
/// Reading a thousand files' dates on the UI thread would stall it, so <see cref="LoadAsync"/>
/// lists each folder once on the chores thread (a directory listing carries every file's dates at
/// no extra cost), and later saves just note "now". Until that first listing lands the dates are
/// unknown, which sorts as oldest; <see cref="Loaded"/> says when to sort again. The Mac keeps the
/// same dates in its SQLite index (<c>updatedAt</c>, and <c>mediaAddedAt</c> for media).
/// </para>
/// </summary>
public sealed class LibraryDates
{
    private static readonly Type[] Kinds = [typeof(Presentation), typeof(MediaItem), typeof(AudioItem)];

    private readonly LibraryStore store;
    private readonly Lock gate = new();
    private readonly Dictionary<string, (DateTimeOffset Updated, DateTimeOffset Added)> byFile = new(StringComparer.OrdinalIgnoreCase);

    public LibraryDates(LibraryStore store)
    {
        this.store = store;
        store.Changed += OnChanged;
    }

    /// <summary>The first listing has landed (raised on the chores thread).</summary>
    public event Action? Loaded;

    public bool IsLoaded { get; private set; }

    /// <summary>Lists the document folders on the chores thread; completes when done.</summary>
    public Task LoadAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LibraryChores.Queue.Async(() =>
        {
            try
            {
                Load();
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });
        return done.Task;
    }

    /// <summary>Lists the document folders now (tests, and <see cref="LoadAsync"/> on its thread).</summary>
    public void Load()
    {
        var found = new Dictionary<string, (DateTimeOffset, DateTimeOffset)>(StringComparer.OrdinalIgnoreCase);
        foreach (var kind in Kinds)
        {
            var folder = new DirectoryInfo(Path.Combine(store.Root, LibraryStore.FolderFor(kind)));
            if (!folder.Exists) continue;
            foreach (var file in folder.EnumerateFiles("*.json"))
                found[Key(kind, Path.GetFileNameWithoutExtension(file.Name))] = (file.LastWriteTimeUtc, file.CreationTimeUtc);
        }
        lock (gate)
        {
            // A save that landed while the folders were being listed is newer than the listing.
            foreach (var (key, dates) in found) byFile.TryAdd(key, dates);
            IsLoaded = true;
        }
        Loaded?.Invoke();
    }

    public DateTimeOffset? UpdatedAt(Type kind, string id)
    {
        lock (gate) return byFile.TryGetValue(KeyFor(kind, id), out var dates) ? dates.Updated : null;
    }

    public DateTimeOffset? AddedAt(Type kind, string id)
    {
        lock (gate) return byFile.TryGetValue(KeyFor(kind, id), out var dates) ? dates.Added : null;
    }

    /// <summary>
    /// When a media item came into the library: the time its import wrote into it (a Mac's import
    /// writes it too, so this survives moving between computers), else its file's creation.
    /// </summary>
    public DateTimeOffset? AddedAt(MediaItem item) =>
        item.ImportedAt is { } seconds && seconds > 0 ? DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000)) : AddedAt(typeof(MediaItem), item.Id);

    private void OnChanged(Type kind, string id)
    {
        if (!Kinds.Contains(kind)) return;
        var key = KeyFor(kind, id);
        var now = DateTimeOffset.UtcNow;
        var exists = File.Exists(store.DocumentPath(kind, id));
        lock (gate)
        {
            if (!exists) byFile.Remove(key);
            else byFile[key] = (now, byFile.TryGetValue(key, out var old) ? old.Added : now);
        }
    }

    // Keyed by the file's name (an id with characters Windows forbids in file names is stored under a safe version of it).
    private string KeyFor(Type kind, string id) => Key(kind, Path.GetFileNameWithoutExtension(store.DocumentPath(kind, id)));

    private static string Key(Type kind, string stem) => kind.Name + "/" + stem;
}

/// <summary>
/// When each item was last used on this PC: a song or presentation going live, media fired, an
/// item added to a service. It orders the library's Recently Used sort and breaks ties in search.
/// Kept in the library's <c>usage.json</c> as id → seconds since 1970, the shape of the Mac's
/// <c>usage.json</c> in a backup bundle (BackupBundle.swift), which a restore merges in
/// (<see cref="Merge"/>). The Mac's twin is the usage table in its index (<c>touchUsage</c>).
/// Saved a moment after the last change, on the chores thread.
/// </summary>
public sealed class LibraryUsage
{
    public const string FileName = "usage.json";

    private static readonly TimeSpan SaveAfter = TimeSpan.FromSeconds(2);

    private readonly string path;
    private readonly Lock gate = new();
    private Dictionary<string, double> stamps = new(StringComparer.Ordinal);
    private bool saveQueued;

    public LibraryUsage(string libraryRoot)
    {
        path = Path.Combine(libraryRoot, FileName);
    }

    /// <summary>Reads usage.json (missing or damaged reads as nothing used yet). Small: call it on the chores thread.</summary>
    public void Load()
    {
        var read = ReadFile(path);
        lock (gate)
            foreach (var (id, at) in read)
                stamps[id] = Math.Max(at, stamps.GetValueOrDefault(id));
    }

    /// <summary>usage.json's stamps, or none when it's missing or isn't one (a restore reads a backup's this way too).</summary>
    public static Dictionary<string, double> ReadFile(string file)
    {
        try
        {
            return File.Exists(file)
                ? JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllBytes(file)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>Notes that <paramref name="id"/> was used now (or at <paramref name="at"/>).</summary>
    public void Touch(string id, DateTimeOffset? at = null)
    {
        if (string.IsNullOrEmpty(id)) return;
        var seconds = (at ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds() / 1000.0;
        lock (gate) stamps[id] = Math.Max(seconds, stamps.GetValueOrDefault(id));
        Changed?.Invoke(id);
        QueueSave();
    }

    /// <summary>Something was used (raised on the caller's thread).</summary>
    public event Action<string>? Changed;

    public DateTimeOffset? LastUsed(string id)
    {
        lock (gate) return stamps.TryGetValue(id, out var seconds) ? FromSeconds(seconds) : null;
    }

    public IReadOnlyDictionary<string, double> All()
    {
        lock (gate) return new Dictionary<string, double>(stamps, StringComparer.Ordinal);
    }

    /// <summary>Takes in stamps from elsewhere (a restored backup): the later of the two wins for each item.</summary>
    public void Merge(IReadOnlyDictionary<string, double> others)
    {
        lock (gate)
            foreach (var (id, at) in others)
                stamps[id] = Math.Max(at, stamps.GetValueOrDefault(id));
        QueueSave();
    }

    /// <summary>Writes usage.json now (tests; the app lets the queued save do it).</summary>
    public void SaveNow()
    {
        Dictionary<string, double> copy;
        lock (gate)
        {
            saveQueued = false;
            copy = new Dictionary<string, double>(stamps, StringComparer.Ordinal);
        }
        try
        {
            // A library folder that's gone (a test's, thrown away) isn't made again just for this.
            if (!Directory.Exists(Path.GetDirectoryName(path))) return;
            LibraryStore.AtomicWrite(path, MxuJson.SerializeToUtf8(copy, indented: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing depends on it: the next use tries again.
        }
    }

    private void QueueSave()
    {
        lock (gate)
        {
            if (saveQueued) return;
            saveQueued = true;
        }
        LibraryChores.Queue.AsyncAfter(SaveAfter, SaveNow);
    }

    private static DateTimeOffset FromSeconds(double seconds) => DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));
}
