using System.Text.Json;
using System.Text.Json.Serialization;
using MxU.Slides.Core.Library;

namespace MxU.Slides.Sync.Engine;

/// <summary>
/// What this PC remembers about one document it has synced: the shared file as it last saw or
/// wrote it, the Automerge heads both sides then agreed on, and the Windows document as it then
/// was. Comparing those with what's there now is how sync tells which side changed.
/// </summary>
public sealed class LedgerEntry
{
    /// <summary>A fingerprint of the shared .automerge file's bytes, as last read or written.</summary>
    public string SharedHash { get; set; } = "";

    /// <summary>The file's size and time then: unchanged, the file needn't be read again.</summary>
    public long SharedLength { get; set; }
    public DateTime SharedWriteUtc { get; set; }

    /// <summary>The Automerge heads of this PC's copy (the replica) after the last sync.</summary>
    public List<string> Heads { get; set; } = [];

    /// <summary>A fingerprint of the Windows document (its canonical JSON) after the last sync.</summary>
    public string LocalHash { get; set; } = "";

    /// <summary>The Windows file's size and time then: unchanged, it needn't be read when sync starts.</summary>
    public long LocalLength { get; set; }
    public DateTime LocalWriteUtc { get; set; }

    /// <summary>The document's name, for the log.</summary>
    public string Name { get; set; } = "";
}

/// <summary>A document deleted on Windows: kept so the Mac's old copy, if it turns up again, doesn't bring it back.</summary>
public sealed class Tombstone
{
    public DateTimeOffset At { get; set; }

    /// <summary>Where this PC's copy (the replica) went, in the sync trash.</summary>
    public string Replica { get; set; } = "";

    public string Name { get; set; } = "";
}

/// <summary>
/// The sync ledger: this PC's memory of a shared folder, saved as "Library sync/state.json" in the
/// Windows library. It belongs to one folder; choosing another starts afresh (the old ledger is
/// kept beside it). Keys are "&lt;kind&gt;/&lt;id&gt;", the shared folder's own layout.
/// Only the sync engine's thread touches it.
/// </summary>
public sealed class SyncLedger
{
    /// <summary>The ledger's own format, so a later version can tell an older ledger.</summary>
    public int Format { get; set; } = 1;

    /// <summary>The shared folder this ledger belongs to.</summary>
    public string Folder { get; set; } = "";

    /// <summary>When the first sync finished (null: it hasn't yet, so the next sync backs up both sides first).</summary>
    public DateTimeOffset? FirstSyncAt { get; set; }

    public DateTimeOffset? LastSyncAt { get; set; }

    /// <summary>Where the first sync's backups are.</summary>
    public string? BackupFolder { get; set; }

    public Dictionary<string, LedgerEntry> Documents { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, Tombstone> Tombstones { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Shared documents someone chose to leave out (they couldn't be read here), by the fingerprint of the file then: a new version is looked at again.</summary>
    public Dictionary<string, string> LeftOut { get; set; } = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Reads the ledger at <paramref name="path"/>, or a fresh one (also when it's damaged: the damaged file is kept aside).</summary>
    public static SyncLedger Load(string path)
    {
        if (!File.Exists(path)) return new SyncLedger();
        try
        {
            return JsonSerializer.Deserialize<SyncLedger>(File.ReadAllBytes(path), Json) ?? new SyncLedger();
        }
        catch (JsonException)
        {
            File.Copy(path, path + $".damaged-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
            return new SyncLedger();
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        LibraryStore.AtomicWrite(path, JsonSerializer.SerializeToUtf8Bytes(this, Json));
    }
}
