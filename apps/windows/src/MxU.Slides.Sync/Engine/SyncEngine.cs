using System.Collections.Concurrent;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Threading;
using MxU.Slides.Sync.Automerge;
using MxU.Slides.Sync.Format;

namespace MxU.Slides.Sync.Engine;

public enum SyncState
{
    Starting,
    Syncing,
    UpToDate,
    Paused,

    /// <summary>The shared folder isn't there (an unplugged drive, OneDrive offline): sync waits, deleting nothing, and carries on when it's back.</summary>
    WaitingForFolder,

    /// <summary>Stopped until someone decides: a document this version can't read, or many documents gone at once.</summary>
    NeedsAttention,
    Failed,
}

public sealed record SyncStatus(SyncState State, string Text, DateTimeOffset? LastSync, int Documents);

/// <summary>Why sync stopped to ask.</summary>
public enum AttentionKind
{
    /// <summary>Documents in the shared folder this version can't read (a newer Mac, or a damaged file).</summary>
    Unreadable,

    /// <summary>Many documents disappeared at once (deletions are held, not made).</summary>
    HeldDeletions,

    /// <summary>The folder's format is newer than this version: nothing to choose but updating.</summary>
    NewerFormat,
}

public sealed record AttentionItem(string Key, string Name, string Reason);

public sealed record SyncAttention(AttentionKind Kind, string Message, IReadOnlyList<AttentionItem> Items);

public sealed record SyncOptions
{
    /// <summary>How often the shared folder is looked through even without a change notice (network shares often send none).</summary>
    public TimeSpan ScanInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long after a Windows save sync waits, so a burst of saves goes out as one change.</summary>
    public TimeSpan LocalDelay { get; init; } = TimeSpan.FromMilliseconds(700);

    /// <summary>How long after a change notice from the folder sync waits, so a cloud client's burst of writes is read once.</summary>
    public TimeSpan FolderDelay { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>A document missing from the shared folder is taken as deleted only once it's still missing this much later.</summary>
    public TimeSpan MissingFor { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>At least this many documents gone at once (and <see cref="MassDeletionShare"/> of them) holds the deletions to ask first.</summary>
    public int MassDeletionMinimum { get; init; } = 10;

    public double MassDeletionShare { get; init; } = 0.25;

    /// <summary>Watch the folder for changes (off in tests, which sync by hand).</summary>
    public bool Watch { get; init; } = true;

    /// <summary>Where the ledger, this PC's copies, backups and the sync trash go (default: "Library sync" in the library folder).</summary>
    public string? StateFolder { get; init; }

    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.Now;

    /// <summary>The app's name and version, written into the shared folder's note.</summary>
    public string AppName { get; init; } = "MxU Slides for Windows";
}

/// <summary>
/// Library sync with a Mac library folder (Phase 10). The Windows JSON library stays the app's
/// working store; this keeps it and a shared folder of Mac documents (<see cref="SharedFolder"/>)
/// in step, both ways:
/// <list type="bullet">
/// <item><b>From the Mac:</b> a changed .automerge file is merged into this PC's copy of the
/// document (its replica), read with the Mac's rules, and saved into the Windows library, which
/// raises <see cref="LibraryStore.Changed"/> so the screens update.</item>
/// <item><b>From Windows:</b> a saved document becomes the smallest Automerge change
/// (<see cref="DocumentDiff"/>), made from the version it was edited from, merged into the
/// replica and written to the shared folder.</item>
/// <item><b>Noticing:</b> the library's Changed event, the folder's change notices, and a look
/// through the folder every <see cref="SyncOptions.ScanInterval"/>.</item>
/// <item><b>Media files</b> are copied both ways (<see cref="BlobMirror"/>).</item>
/// </list>
/// Safety: both sides are backed up before the first sync; nothing is written that the Mac's
/// decoder wouldn't read back exactly (<see cref="MacDocuments"/>); a newer format, an unreadable
/// document or many documents vanishing at once stops sync to ask (<see cref="Attention"/>); a
/// folder that goes away pauses it. All the work is on its own <see cref="WorkQueue"/>, never the
/// UI thread. See docs/phase10/sync.md.
/// </summary>
public sealed partial class SyncEngine : IDisposable
{
    private readonly WindowsLibrary windows;
    private readonly SharedFolder shared;
    private readonly SyncOptions options;
    private readonly WorkQueue queue = new("Library sync");
    private readonly BlobMirror media;
    private readonly ConcurrentDictionary<string, byte> dirty = new(StringComparer.Ordinal);
    private readonly string stateFolder;
    private SyncLedger ledger = new();
    private FileSystemWatcher? watcher;

    private volatile SyncStatus status;
    private volatile SyncAttention? attention;
    private volatile bool paused;
    private volatile bool disposed;
    private int localScheduled;
    private int fullScheduled;
    private int fullWanted = 1;
    private int scanScheduled;
    private bool checkedEveryLocalDocument;

    /// <summary>The key of the document sync itself is saving into the library this moment (its Changed is its own echo).</summary>
    private string? savingKey;

    public SyncEngine(LibraryStore store, string sharedFolder, SyncOptions? options = null)
    {
        this.options = options ?? new SyncOptions();
        windows = new WindowsLibrary(store);
        shared = new SharedFolder(sharedFolder);
        stateFolder = this.options.StateFolder ?? Path.Combine(store.Root, "Library sync");
        Log = new SyncLog(Path.Combine(stateFolder, "sync-log.txt"), this.options.Now);
        media = new BlobMirror(windows, shared, Log);
        status = new SyncStatus(SyncState.Starting, "Starting…", null, 0);
    }

    public string SharedFolderPath => shared.Root;

    public string StateFolder => stateFolder;

    public SyncStatus Status => status;

    /// <summary>What sync stopped to ask about, or null.</summary>
    public SyncAttention? Attention => attention;

    public SyncLog Log { get; }

    /// <summary>The status or <see cref="Attention"/> changed (raised on sync's own thread).</summary>
    public event Action? StatusChanged;

    /// <summary>
    /// A document changed by the Mac was just saved into the Windows library (raised on sync's own
    /// thread, after <see cref="LibraryStore.Changed"/>): the app reopens it if it's on screen.
    /// </summary>
    public event Action<Type, string>? Received;

    private string LedgerPath => Path.Combine(stateFolder, "state.json");

    private string TrashFolder => Path.Combine(stateFolder, "trash", options.Now().LocalDateTime.ToString("yyyy-MM-dd"));

    /// <summary>
    /// Starts syncing (or starts paused). Reads the ledger; if it belongs to another folder, it's
    /// kept aside and this folder starts afresh, with a first sync (and its backups).
    /// </summary>
    public void Start(bool startPaused = false)
    {
        paused = startPaused;
        queue.Async(() =>
        {
            ledger = SyncLedger.Load(LedgerPath);
            if (ledger.Folder.Length > 0 && !string.Equals(ledger.Folder, shared.Root, StringComparison.OrdinalIgnoreCase))
            {
                File.Move(LedgerPath, LedgerPath + $".{DateTime.Now:yyyyMMdd-HHmmss}.old", overwrite: true);
                ledger = new SyncLedger();
            }
            ledger.Folder = shared.Root;
            if (paused) SetStatus(SyncState.Paused, "Paused");
        });
        windows.Store.Changed += OnLibraryChanged;
        if (options.Watch) queue.Async(EnsureWatcher);
        if (!startPaused) RequestFull(TimeSpan.Zero);
    }

    public void Pause()
    {
        paused = true;
        queue.Async(() => SetStatus(SyncState.Paused, "Paused"));
    }

    public void Resume()
    {
        paused = false;
        RequestFull(TimeSpan.Zero);
    }

    /// <summary>Looks through everything now.</summary>
    public void SyncNow() => RequestFull(TimeSpan.Zero);

    /// <summary>Runs a whole pass on sync's thread and waits for it: for the self-test and the unit tests.</summary>
    public void SyncNowAndWait(TimeSpan? timeout = null)
    {
        Interlocked.Exchange(ref fullWanted, 1);
        queue.Sync(RunPass, timeout ?? TimeSpan.FromMinutes(2));
    }

    /// <summary>Waits until sync has nothing queued (the media copies included).</summary>
    public bool WaitIdle(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (queue.Pending == 0 && Volatile.Read(ref localScheduled) == 0 && Volatile.Read(ref fullScheduled) == 0 && dirty.IsEmpty
                && queue.Sync(() => true) && media.IsIdle)
                return true;
            Thread.Sleep(20);
        }
        return false;
    }

    /// <summary>Leaves the documents this version can't read out of sync (untouched in the shared folder and here) and carries on.</summary>
    public void LeaveOutUnreadable() => queue.Async(() =>
    {
        if (attention is not { Kind: AttentionKind.Unreadable } asked) return;
        foreach (var item in asked.Items)
            if (Parse(item.Key) is { } key && shared.Read(key.Kind, key.Id) is { } bytes)
                ledger.LeftOut[item.Key] = SharedFolder.Fingerprint(bytes);
        ledger.Save(LedgerPath);
        Log.Add(SyncDirection.Note, $"Left {Count(asked.Items.Count, "document")} out of sync, as asked. Each is looked at again when it changes.");
        ClearAttention();
    });

    /// <summary>Makes the held deletions here too (each document goes to the library's Trash).</summary>
    public void ApplyHeldDeletions() => queue.Async(() =>
    {
        if (attention is not { Kind: AttentionKind.HeldDeletions } asked) return;
        approvedDeletions = asked.Items.Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
        Log.Add(SyncDirection.Note, $"Deleting {Count(asked.Items.Count, "document")} here too, as asked.");
        ClearAttention();
    });

    /// <summary>Keeps the documents that disappeared, and puts them back in the shared folder.</summary>
    public void KeepHeldDeletions() => queue.Async(() =>
    {
        if (attention is not { Kind: AttentionKind.HeldDeletions } asked) return;
        restoreDeletions = asked.Items.Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
        Log.Add(SyncDirection.Note, $"Putting {Count(asked.Items.Count, "document")} back in the shared folder, as asked.");
        ClearAttention();
    });

    private HashSet<string>? approvedDeletions;
    private HashSet<string>? restoreDeletions;

    private void ClearAttention()
    {
        attention = null;
        Interlocked.Exchange(ref fullWanted, 1);
        RunPass();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        windows.Store.Changed -= OnLibraryChanged;
        try
        {
            queue.Sync(() =>
            {
                watcher?.Dispose();
                watcher = null;
                if (ledger.Folder.Length > 0) ledger.Save(LedgerPath);
            }, TimeSpan.FromSeconds(20));
        }
        catch (TimeoutException)
        {
            // A big pass is still going; it stops at the next document (disposed), and its ledger is saved as it goes.
        }
        media.Dispose();
        queue.Dispose();
    }

    // MARK: Noticing

    private void OnLibraryChanged(Type type, string id)
    {
        if (disposed || MacKind.ForWindowsType(type) is not { Synced: true } kind) return;
        var key = Key(kind, id);
        // Sync's own save into the library: nothing new to send.
        if (queue.IsCurrent && savingKey == key) return;
        dirty[key] = 1;
        if (!paused && Interlocked.Exchange(ref localScheduled, 1) == 0)
            queue.AsyncAfter(options.LocalDelay, () =>
            {
                Volatile.Write(ref localScheduled, 0);
                RunPass();
            });
    }

    private void RequestFull(TimeSpan delay)
    {
        Interlocked.Exchange(ref fullWanted, 1);
        if (disposed || Interlocked.Exchange(ref fullScheduled, 1) == 1) return;
        void Go()
        {
            Volatile.Write(ref fullScheduled, 0);
            RunPass();
        }
        if (delay <= TimeSpan.Zero) queue.Async(Go);
        else queue.AsyncAfter(delay, Go);
    }

    private void ScheduleScan()
    {
        if (disposed || Interlocked.Exchange(ref scanScheduled, 1) == 1) return;
        queue.AsyncAfter(options.ScanInterval, () =>
        {
            Volatile.Write(ref scanScheduled, 0);
            RequestFull(TimeSpan.Zero);
        });
    }

    private void EnsureWatcher()
    {
        if (watcher is not null || disposed || !shared.IsReachable()) return;
        try
        {
            var created = new FileSystemWatcher(shared.Root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            FileSystemEventHandler changed = (_, e) => OnFolderEvent(e.FullPath);
            created.Created += changed;
            created.Changed += changed;
            created.Deleted += changed;
            created.Renamed += (_, e) => OnFolderEvent(e.FullPath);
            // Too many changes at once, or the folder went away: look through everything, and watch afresh.
            created.Error += (_, _) => queue.Async(() =>
            {
                watcher?.Dispose();
                watcher = null;
                RequestFull(options.FolderDelay);
            });
            created.EnableRaisingEvents = true;
            watcher = created;
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // Some network shares can't be watched; the regular look-through still finds every change.
        }
    }

    private void OnFolderEvent(string path)
    {
        if (path.EndsWith(".mxu-sync", StringComparison.Ordinal) || path.EndsWith(SharedFolder.MarkerName, StringComparison.Ordinal)) return;
        RequestFull(options.FolderDelay);
    }

    // MARK: A pass

    /// <summary>One look at what changed, on sync's thread: the dirty Windows documents, and, when asked for, everything.</summary>
    private void RunPass()
    {
        if (disposed || paused) return;
        var full = Interlocked.Exchange(ref fullWanted, 0) == 1;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in dirty.Keys)
            if (dirty.TryRemove(key, out _)) keys.Add(key);
        if (!full && keys.Count == 0) return;
        try
        {
            if (attention is not null)
            {
                // Stopped to ask; Windows edits wait (they're kept as dirty) until it's answered.
                foreach (var key in keys) dirty[key] = 1;
                return;
            }
            if (!shared.IsReachable())
            {
                foreach (var key in keys) dirty[key] = 1;
                Interlocked.Exchange(ref fullWanted, 1);
                SetStatus(SyncState.WaitingForFolder,
                    "Waiting for the shared folder: it isn't there (an unplugged drive, or OneDrive offline?). Nothing is deleted; sync carries on when it's back.");
                watcher?.Dispose();
                watcher = null;
                return;
            }
            if (VersionProblem() is { } newer)
            {
                attention = new SyncAttention(AttentionKind.NewerFormat, newer, []);
                Log.Add(SyncDirection.Problem, newer);
                SetStatus(SyncState.NeedsAttention, "Stopped: the folder's format is newer than this version knows");
                return;
            }
            if (ledger.FirstSyncAt is null && !PrepareFirstSync()) return;
            if (options.Watch) EnsureWatcher();
            SetStatus(SyncState.Syncing, full ? "Syncing…" : "Sending changes…");
            var pass = new Pass(this);
            if (full)
            {
                if (!pass.Survey(keys)) return;
            }
            var waiting = new Queue<string>(keys.Order(StringComparer.Ordinal));
            while (waiting.TryDequeue(out var key))
            {
                if (disposed || paused || pass.Unreadable.Count > 0)
                {
                    // Stopped part way: what's left waits for the next pass.
                    dirty[key] = 1;
                    continue;
                }
                if (Parse(key) is { } parsed) SyncOne(parsed.Kind, parsed.Id, pass);
            }
            if (full)
            {
                approvedDeletions = null;
                restoreDeletions = null;
            }
            if (pass.Unreadable.Count > 0)
            {
                var message = $"Sync stopped: {Count(pass.Unreadable.Count, "document")} in the shared folder can't be read by this version of MxU Slides. "
                    + "A newer Mac may have made them; update MxU Slides for Windows. Or leave them out, and sync carries on without them.";
                attention = new SyncAttention(AttentionKind.Unreadable, message, pass.Unreadable);
                SetStatus(SyncState.NeedsAttention, $"Stopped: {Count(pass.Unreadable.Count, "document")} this version can't read");
            }
            ledger.LastSyncAt = options.Now();
            ledger.FirstSyncAt ??= ledger.LastSyncAt;
            ledger.Save(LedgerPath);
            if (attention is null)
            {
                SetStatus(SyncState.UpToDate, $"Up to date · {Count(ledger.Documents.Count, "document")}");
                if (full) media.Request();
            }
        }
        catch (Exception error)
        {
            Log.Add(SyncDirection.Problem, $"Sync couldn't finish: {error.Message}");
            SetStatus(SyncState.Failed, $"Sync couldn't finish: {error.Message} It tries again shortly.");
            try { ledger.Save(LedgerPath); } catch (IOException) { }
            foreach (var key in keys) dirty[key] = 1;
            Interlocked.Exchange(ref fullWanted, 1);
        }
        finally
        {
            if (!disposed) ScheduleScan();
        }
    }

    /// <summary>A backup, a library or the note in the folder made by a newer version: a message, or null when all's well.</summary>
    private string? VersionProblem()
    {
        try
        {
            if (shared.BackupFormatVersion() is > SharedFolder.BackupFormat and var backup)
                return $"Sync stopped: this folder is a backup made by a newer MxU Slides for Mac (format {backup}; this version reads format {SharedFolder.BackupFormat}). Update MxU Slides for Windows.";
        }
        catch (System.Text.Json.JsonException)
        {
            return "Sync stopped: the folder's manifest.json can't be read, so its format is unknown. Nothing was changed.";
        }
        if (shared.WindowsSchemaVersion() is { } version && version > MacSchema.Shared.Version)
            return $"Sync stopped: another PC synced this folder with a newer document format ({version}; this version knows {MacSchema.Shared.Version}). Update MxU Slides for Windows.";
        return null;
    }

    /// <summary>Before the very first sync with a folder: back up both sides, make the folder a library if it's empty, and leave the note.</summary>
    private bool PrepareFirstSync()
    {
        if (!shared.LooksLikeAMacLibrary())
        {
            if (!shared.IsEmpty() && shared.WindowsSchemaVersion() is null)
            {
                var message = $"Sync stopped: {shared.Root} isn't empty, and isn't a Mac library or backup. Choose an empty folder, or the Mac's library or backup folder.";
                attention = new SyncAttention(AttentionKind.NewerFormat, message, []);
                SetStatus(SyncState.NeedsAttention, "Stopped: the folder isn't a library");
                return false;
            }
            shared.MakeLibrary();
            Log.Add(SyncDirection.Note, $"Made a new Mac library in {shared.Root}.");
        }
        var backup = SyncBackup.Make(windows.Store, shared, Path.Combine(stateFolder, "backups"), options.Now());
        ledger.BackupFolder = backup;
        ledger.Save(LedgerPath);
        Log.Add(SyncDirection.Note, $"Backed up both libraries before the first sync, to {backup}.");
        shared.WriteMarker(MacSchema.Shared.Version, options.AppName);
        return true;
    }

    private void SetStatus(SyncState state, string text)
    {
        status = new SyncStatus(state, text, ledger.LastSyncAt, ledger.Documents.Count);
        try { StatusChanged?.Invoke(); }
        catch
        {
            // A listener's failure is its own; sync carries on.
        }
    }

    /// <summary>
    /// Why <paramref name="folder"/> can't be the shared folder, or null when it can: it must be
    /// there, outside this PC's own library, and either empty (a new shared library), a Mac library
    /// or backup, or a folder a Windows PC already syncs.
    /// </summary>
    public static string? CheckFolder(string folder, string libraryRoot)
    {
        if (string.IsNullOrWhiteSpace(folder)) return "Choose a folder.";
        string full, library;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            library = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "That isn't a folder path.";
        }
        if (!Directory.Exists(full)) return "That folder isn't there.";
        static bool Inside(string inner, string outer) =>
            inner.Equals(outer, StringComparison.OrdinalIgnoreCase) || inner.StartsWith(outer + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (Inside(full, library) || Inside(library, full)) return "Choose a folder outside this PC's own library folder.";
        var shared = new SharedFolder(full);
        try
        {
            if (!shared.LooksLikeAMacLibrary() && !shared.IsEmpty() && shared.WindowsSchemaVersion() is null)
                return "That folder has other things in it. Choose an empty folder (to start a shared library), or the Mac's library or backup folder.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return $"That folder can't be read: {error.Message}";
        }
        return null;
    }

    // MARK: Keys

    internal static string Key(MacKind kind, string id) => $"{kind.DirectoryName}/{id}";

    internal static (MacKind Kind, string Id)? Parse(string key)
    {
        var slash = key.IndexOf('/');
        return slash > 0 && MacKind.ForDirectory(key[..slash]) is { Synced: true } kind ? (kind, key[(slash + 1)..]) : null;
    }

    internal static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n:N0} {noun}s";
}
