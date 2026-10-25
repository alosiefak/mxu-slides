using System.Text.Json.Nodes;
using MxU.Slides.Sync.Automerge;
using MxU.Slides.Sync.Format;

namespace MxU.Slides.Sync.Engine;

public sealed partial class SyncEngine
{
    /// <summary>How many recent versions of each document are remembered, to find the one a Windows edit was made from.</summary>
    private const int VersionsKept = 6;

    private readonly MacDocuments documents = MacDocuments.Shared;

    /// <summary>Recent versions of each document as sync left it in the Windows library, with the replica's heads then.</summary>
    private readonly Dictionary<string, List<(JsonObject Json, DocHeads Heads)>> versions = new(StringComparer.Ordinal);

    /// <summary>Windows documents that can't go to the Mac (they break the schema), by the fingerprint of the version: logged once.</summary>
    private readonly Dictionary<string, string> notSent = new(StringComparer.Ordinal);

    private enum Outcome
    {
        Done,

        /// <summary>The Windows file or the shared file moved on while sync worked: read both again.</summary>
        Again,
    }

    /// <summary>A shared document this version can't read: it stops the pass to ask, and nothing of it is written anywhere.</summary>
    private sealed class UnreadableException(string reason, string? name = null) : Exception(reason)
    {
        /// <summary>The document's name, when it could be read at all.</summary>
        public string? Name { get; } = name;
    }

    /// <summary>
    /// Brings one document into step on both sides. Tries again (up to three times) when either
    /// side's file moves on in the middle, so nobody's save is ever overwritten unseen.
    /// </summary>
    private void SyncOne(MacKind kind, string id, Pass pass)
    {
        var key = Key(kind, id);
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
                if (SyncOnce(kind, id, key, pass) == Outcome.Done) return;
            Log.Add(SyncDirection.Note, $"'{NameOf(key)}' kept changing while syncing; it's tried again shortly.");
            dirty[key] = 1;
        }
        catch (UnreadableException unreadable)
        {
            var name = unreadable.Name ?? NameOf(key);
            pass.Unreadable.Add(new AttentionItem(key, name, unreadable.Message));
            Log.Add(SyncDirection.Problem, $"Can't read '{name}' ({key}) from the shared folder: {unreadable.Message}");
        }
        catch (SchemaViolationException violation)
        {
            // A Windows document the Mac couldn't read: it isn't sent (once logged per version).
            var hash = windows.Read(kind, id)?.Hash ?? "";
            if (!notSent.TryGetValue(key, out var logged) || logged != hash)
            {
                notSent[key] = hash;
                Log.Add(SyncDirection.Problem, $"Didn't send '{NameOf(key)}': the Mac couldn't read it ({string.Join("; ", violation.Problems.Take(2))}). It stays on this PC.");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The file is busy, or a cloud placeholder that can't be fetched this moment.
            Log.Add(SyncDirection.Note, $"Couldn't reach '{NameOf(key)}' just now ({error.Message}); trying again shortly.");
            dirty[key] = 1;
        }
        catch (Exception error) when (error is AutomergeException or MacFormatException)
        {
            Log.Add(SyncDirection.Problem, $"Couldn't sync '{NameOf(key)}': {error.Message}");
        }
    }

    private Outcome SyncOnce(MacKind kind, string id, string key, Pass pass)
    {
        if (ledger.LeftOut.TryGetValue(key, out var leftOut))
        {
            var current = shared.Read(kind, id);
            if (current is not null && SharedFolder.Fingerprint(current) == leftOut) return Outcome.Done;
            ledger.LeftOut.Remove(key);
        }
        var local = windows.Read(kind, id);
        var sharedBytes = shared.Read(kind, id);
        return ledger.Documents.TryGetValue(key, out var entry)
            ? Follow(kind, id, key, entry, local, sharedBytes, pass)
            : Meet(kind, id, key, local, sharedBytes);
    }

    // MARK: A document sync hasn't seen before

    private Outcome Meet(MacKind kind, string id, string key, LocalDocument? local, byte[]? sharedBytes)
    {
        if (local is null && sharedBytes is null)
        {
            return Outcome.Done;
        }
        if (local is null && ledger.Tombstones.TryGetValue(key, out var buried) && buried.SharedHash == SharedFolder.Fingerprint(sharedBytes!))
        {
            return Outcome.Done;
        }
        if (sharedBytes is null)
        {
            // New on Windows.
            using var made = documents.Create(local!.Json, kind, options.Now());
            if (!WriteShared(kind, id, made, expected: null, out var written)) return Outcome.Again;
            ledger.Tombstones.Remove(key);
            Remember(key, kind, made, local, written);
            Log.Add(SyncDirection.Out, $"Sent '{local.Name}' (new on this PC)");
            return Outcome.Done;
        }

        using var arrived = LoadShared(sharedBytes, kind);
        var decoded = Decoded(arrived, kind);
        if (JsonRead.String(decoded["id"]) is { } inside && inside != id) return ConflictCopy(kind, id, inside, arrived);
        var sharedStamp = new Written(SharedFolder.Fingerprint(sharedBytes), shared.Stamp(kind, id) ?? default);

        if (local is null)
        {
            // New from the Mac, unless it's the old copy of something deleted here.
            if (ledger.Tombstones.TryGetValue(key, out var tombstone) && NothingNewSince(tombstone, arrived))
            {
                tombstone.SharedHash = sharedStamp.Hash;
                return Outcome.Done;
            }
            if (ledger.Tombstones.Remove(key))
                Log.Add(SyncDirection.Note, $"'{Name(decoded)}' was deleted here, but changed on the Mac since, so it's back.");
            if (!Save(key, kind, decoded, expected: null)) return Outcome.Again;
            Remember(key, kind, arrived, windows.Read(kind, id)!, sharedStamp);
            Log.Add(SyncDirection.In, $"Received '{Name(decoded)}' (new from the Mac)");
            return Outcome.Done;
        }

        // Both sides have it, and sync has never seen them together (the first sync).
        if (MacDocuments.Canonical(decoded, kind) is var theirs && theirs.AsSpan().SequenceEqual(MacDocuments.Canonical(local.Json, kind)))
        {
            Remember(key, kind, arrived, local, sharedStamp);
            return Outcome.Done;
        }
        var macChanged = arrived.Changes().Max(c => c.Time);
        var windowsChanged = new DateTimeOffset(local.WriteUtc).ToUnixTimeSeconds();
        if (windowsChanged > macChanged)
        {
            // This PC's copy changed later: it goes on top of the Mac's, keeping the Mac's history.
            documents.Edit(arrived, local.Json, kind, options.Now());
            if (!WriteShared(kind, id, arrived, sharedStamp.Hash, out var written)) return Outcome.Again;
            Remember(key, kind, arrived, local, written);
            Log.Add(SyncDirection.Out, $"Both had '{local.Name}': kept this PC's copy, changed later. The Mac's copy is in the backup.");
        }
        else
        {
            if (!Save(key, kind, decoded, local.File)) return Outcome.Again;
            Remember(key, kind, arrived, windows.Read(kind, id)!, sharedStamp);
            Log.Add(SyncDirection.In, $"Both had '{local.Name}': kept the Mac's copy, changed later. This PC's copy is in the backup.");
        }
        return Outcome.Done;
    }

    /// <summary>
    /// "&lt;id&gt; (MacBook's conflicted copy).automerge": a cloud client keeps both versions when two
    /// computers write one file at once. Automerge needs no such copy: its changes are merged into
    /// the document's own, and the copy goes to the sync trash.
    /// </summary>
    private Outcome ConflictCopy(MacKind kind, string fileId, string realId, AmDocument copy)
    {
        var realKey = Key(kind, realId);
        if (!fileId.StartsWith(realId, StringComparison.Ordinal) || LoadReplica(realKey) is not { } real)
        {
            if (!notSent.ContainsKey(Key(kind, fileId)))
            {
                notSent[Key(kind, fileId)] = "";
                Log.Add(SyncDirection.Note, $"'{kind.DirectoryName}/{fileId}.automerge' holds the document '{realId}'; left alone.");
            }
            return Outcome.Done;
        }
        using (real)
        {
            real.Merge(copy);
            Decoded(real, kind);
            SaveReplica(realKey, real);
        }
        shared.MoveToTrash(kind, fileId, TrashFolder);
        dirty[realKey] = 1;
        Log.Add(SyncDirection.Note, $"Merged a copy the cloud folder made of '{NameOf(realKey)}' back into it.");
        return Outcome.Done;
    }

    // MARK: A document sync has synced before

    private Outcome Follow(MacKind kind, string id, string key, LedgerEntry entry, LocalDocument? local, byte[]? sharedBytes, Pass pass)
    {
        var sharedHash = sharedBytes is null ? null : SharedFolder.Fingerprint(sharedBytes);
        var changedShared = sharedBytes is not null && sharedHash != entry.SharedHash;
        var changedHere = local is not null && local.Hash != entry.LocalHash && local.Hash != entry.TakenLocalHash;
        using var replica = LoadReplica(key) ?? (sharedBytes is not null ? LoadShared(sharedBytes, kind) : null);
        if (replica is null)
        {
            // Nothing left to go on (this PC's copy lost, and the shared one gone): start afresh with what's here.
            Forget(key);
            return local is null ? Outcome.Done : Outcome.Again;
        }

        if (local is null)
        {
            if (sharedBytes is null)
            {
                Forget(key);
                return Outcome.Done;
            }
            if (!changedShared)
            {
                // Deleted on Windows: the shared copy goes to the sync trash, and a note stops an old copy bringing it back.
                shared.MoveToTrash(kind, id, TrashFolder);
                Bury(key, replica);
                Log.Add(SyncDirection.Out, $"Deleted '{entry.Name}' from the shared folder (deleted on this PC). A copy is in the sync trash.");
                return Outcome.Done;
            }
            // Deleted here, but changed on the Mac since: the change wins and the document comes back.
            using (var arrived = LoadShared(sharedBytes, kind)) replica.Merge(arrived);
            var back = Decoded(replica, kind);
            SaveReplica(key, replica);
            if (!Save(key, kind, back, expected: null)) return Outcome.Again;
            Remember(key, kind, replica, windows.Read(kind, id)!, new Written(sharedHash!, shared.Stamp(kind, id) ?? default));
            Log.Add(SyncDirection.In, $"'{entry.Name}' was deleted on this PC but changed on the Mac since, so it's back.");
            return Outcome.Done;
        }

        if (sharedBytes is null)
        {
            if (!pass.ConfirmedMissing.Contains(key) && restoreDeletions?.Contains(key) != true)
            {
                // Missing from the shared folder, not yet for long enough to count: the full pass decides.
                if (changedHere) dirty[key] = 1;
                return Outcome.Done;
            }
            var putBack = restoreDeletions?.Contains(key) == true;
            var madeHere = replica.Changes().All(c => WindowsActor.IsWindows(c.Actor));
            if (changedHere || putBack || (madeHere && approvedDeletions?.Contains(key) != true))
            {
                // The Windows edit wins over the deletion; or someone said keep it; or only Windows
                // ever wrote it, so the Mac may never have had it (a backup made again on the Mac
                // leaves out what it hadn't imported): it goes back in the folder.
                if (changedHere) TakeInLocal(kind, key, entry, replica, local);
                var kept = Decoded(replica, kind);
                SaveReplica(key, replica);
                Taken(entry, changedHere, local);
                if (!WriteShared(kind, id, replica, expected: null, out var written)) return Outcome.Again;
                if (!Project(key, kind, kept, local)) return Outcome.Again;
                Remember(key, kind, replica, windows.Read(kind, id)!, written);
                Log.Add(SyncDirection.Out, changedHere
                    ? $"Put '{local.Name}' back in the shared folder: it was gone from there, but changed on this PC since."
                    : putBack ? $"Put '{local.Name}' back in the shared folder, as asked."
                    : $"Put '{local.Name}' back in the shared folder: it was made on this PC, and the Mac may not have had it yet.");
                return Outcome.Done;
            }
            // Deleted on the Mac: it goes to the Windows library's Trash.
            if (!Delete(key, kind, id, local.File)) return Outcome.Again;
            TrashReplica(key);
            Forget(key);
            Log.Add(SyncDirection.In, $"Deleted '{local.Name}' (deleted on the Mac). It's in the library's Trash.");
            return Outcome.Done;
        }

        // Both sides have it. (This PC's copy can be ahead of both: a cloud folder's conflict copy merged into it.)
        var replicaAhead = !replica.Heads.Equals(new DocHeads(entry.Heads));
        if (!changedShared && !changedHere && !replicaAhead)
        {
            Remember(key, kind, replica, local, new Written(sharedHash!, shared.Stamp(kind, id) ?? default));
            return Outcome.Done;
        }
        if (changedShared)
        {
            using var arrived = LoadShared(sharedBytes, kind);
            replica.Merge(arrived);
        }
        if (changedHere) TakeInLocal(kind, key, entry, replica, local);
        var merged = Decoded(replica, kind);
        SaveReplica(key, replica);
        Taken(entry, changedHere, local);

        var result = new Written(sharedHash!, shared.Stamp(kind, id) ?? default);
        // The shared copy lacks something when Windows changed, or when it didn't hold all the replica had.
        var sharedHeads = changedShared ? SharedHeads(sharedBytes, kind) : new DocHeads(entry.Heads);
        if (!replica.Heads.Equals(sharedHeads) && !WriteShared(kind, id, replica, sharedHash, out result)) return Outcome.Again;
        if (!Project(key, kind, merged, local)) return Outcome.Again;
        Remember(key, kind, replica, windows.Read(kind, id)!, result);
        Log.Add(changedShared && changedHere ? SyncDirection.Note : changedShared ? SyncDirection.In : SyncDirection.Out,
            changedShared && changedHere ? $"Merged changes to '{Name(merged)}' made on both sides"
            : changedShared ? $"Updated '{Name(merged)}' with changes from the Mac"
            : $"Sent changes to '{Name(merged)}'");
        return Outcome.Done;
    }

    /// <summary>
    /// Takes a Windows edit into the replica. The edit is made from the version it was edited
    /// from: the app may have been holding the document as it was before a Mac change came in.
    /// Of the versions sync last left in the library, the one nearest the edit is taken as its
    /// starting point; the edit is made on a copy of the replica as it was then and merged back,
    /// so it's concurrent with whatever came in since, and both survive, as between two Macs.
    /// </summary>
    private void TakeInLocal(MacKind kind, string key, LedgerEntry entry, AmDocument replica, LocalDocument local)
    {
        var candidates = versions.TryGetValue(key, out var known) ? known.ToList() : [];
        // After a restart the remembered versions are gone; the ledger still has two to go on.
        foreach (var heads in new[] { entry.Heads, entry.TakenHeads })
        {
            if (heads is not { Count: > 0 }) continue;
            var recorded = new DocHeads(heads);
            if (candidates.Any(c => c.Heads.Equals(recorded))) continue;
            using var then = replica.ForkAt(recorded);
            if (documents.Decode(then, kind).Value is { } value) candidates.Insert(0, (value, recorded));
        }
        // The nearest version wins; of two as near, the newer.
        var basis = candidates.Count == 0
            ? replica.Heads
            : candidates.Select((c, order) => (c.Heads, Distance: JsonDistance.Between(c.Json, local.Json), Order: order))
                .OrderBy(c => c.Distance).ThenByDescending(c => c.Order).First().Heads;
        using var edit = basis.Equals(replica.Heads) ? replica.Fork() : replica.ForkAt(basis);
        documents.Edit(edit, local.Json, kind, options.Now());
        replica.Merge(edit);
        // The copy now says exactly what Windows says: a version a later edit can start from.
        takenHeads = edit.Heads;
        Version(key, local.Json, takenHeads);
    }

    /// <summary>The heads of the copy the last Windows edit was made on (<see cref="TakeInLocal"/>).</summary>
    private DocHeads takenHeads = DocHeads.None;

    /// <summary>Notes in the ledger, at once, that a Windows edit is in this PC's copy, so it's never taken in twice.</summary>
    private void Taken(LedgerEntry entry, bool changedHere, LocalDocument local)
    {
        if (!changedHere) return;
        entry.TakenLocalHash = local.Hash;
        entry.TakenHeads = takenHeads.Hashes.ToList();
        ledger.Save(LedgerPath);
    }

    private void Version(string key, JsonObject json, DocHeads heads)
    {
        if (!versions.TryGetValue(key, out var list)) versions[key] = list = [];
        list.RemoveAll(v => v.Heads.Equals(heads));
        list.Add((json, heads));
        if (list.Count > VersionsKept) list.RemoveAt(0);
    }

    // MARK: Helpers

    private readonly record struct Written(string Hash, FileStamp Stamp);

    /// <summary>For the tests: runs just before sync looks at a shared file it's about to write (to play a Mac writing it at that moment).</summary>
    internal Action<string>? BeforeSharedWrite { get; set; }

    /// <summary>
    /// Writes the document to the shared folder, as the Mac's DocumentStore.save does, but first
    /// makes sure the file is still the one sync read (<paramref name="expected"/>; null: no file):
    /// if the Mac wrote it meanwhile, this returns false and the document is synced again, taking
    /// the Mac's change in.
    /// </summary>
    private bool WriteShared(MacKind kind, string id, AmDocument document, string? expected, out Written written)
    {
        written = default;
        BeforeSharedWrite?.Invoke(Key(kind, id));
        var now = shared.Read(kind, id);
        if ((now is null ? null : SharedFolder.Fingerprint(now)) != expected) return false;
        var bytes = document.Save();
        var stamp = shared.Write(kind, id, bytes);
        written = new Written(SharedFolder.Fingerprint(bytes), stamp);
        return true;
    }

    /// <summary>Saves the merged document into the Windows library, unless that's what it already says.</summary>
    private bool Project(string key, MacKind kind, JsonObject merged, LocalDocument local) =>
        MacDocuments.Canonical(merged, kind).AsSpan().SequenceEqual(MacDocuments.Canonical(local.Json, kind)) || Save(key, kind, merged, local.File);

    private bool Save(string key, MacKind kind, JsonObject value, byte[]? expected)
    {
        savingKey = key;
        bool saved;
        try
        {
            saved = windows.SaveIfUnchanged(kind, value, expected);
        }
        finally
        {
            savingKey = null;
        }
        if (saved && Parse(key) is { } parsed)
        {
            try { Received?.Invoke(kind.WindowsType!, parsed.Id); }
            catch
            {
                // A listener's failure is its own.
            }
        }
        return saved;
    }

    private bool Delete(string key, MacKind kind, string id, byte[] expected)
    {
        savingKey = key;
        try
        {
            return windows.DeleteIfUnchanged(kind, id, expected);
        }
        finally
        {
            savingKey = null;
        }
    }

    /// <summary>Writes the ledger entry and remembers this version of the document.</summary>
    private void Remember(string key, MacKind kind, AmDocument replica, LocalDocument local, Written written)
    {
        var heads = replica.Heads;
        SaveReplica(key, replica);
        ledger.Documents[key] = new LedgerEntry
        {
            SharedHash = written.Hash,
            SharedLength = written.Stamp.Length,
            SharedWriteUtc = written.Stamp.WriteUtc,
            LocalLength = local.File.Length,
            LocalWriteUtc = local.WriteUtc,
            Heads = heads.Hashes.ToList(),
            LocalHash = local.Hash,
            Name = local.Name,
        };
        notSent.Remove(key);
        Version(key, local.Json, heads);
    }

    private void Forget(string key)
    {
        ledger.Documents.Remove(key);
        versions.Remove(key);
    }

    /// <summary>Remembers a document deleted on Windows, keeping this PC's copy in the sync trash.</summary>
    private void Bury(string key, AmDocument replica)
    {
        var name = NameOf(key);
        var copy = Path.Combine(TrashFolder, "replicas", ReplicaFileName(key));
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.WriteAllBytes(copy, replica.Save());
        DeleteReplica(key);
        Forget(key);
        ledger.Tombstones[key] = new Tombstone { At = options.Now(), Replica = copy, Name = name };
    }

    /// <summary>True when <paramref name="arrived"/> holds no change the deleted document didn't already have.</summary>
    private static bool NothingNewSince(Tombstone tombstone, AmDocument arrived)
    {
        if (!File.Exists(tombstone.Replica)) return false;
        using var old = AmDocument.Load(File.ReadAllBytes(tombstone.Replica));
        using var together = old.Fork();
        together.Merge(arrived);
        return together.Heads.Equals(old.Heads);
    }

    private AmDocument LoadShared(byte[] bytes, MacKind kind)
    {
        try
        {
            return AmDocument.Load(bytes);
        }
        catch (AutomergeException error)
        {
            throw new UnreadableException($"not a {kind.RecordName} Automerge can load ({error.Message})");
        }
    }

    private DocHeads SharedHeads(byte[] bytes, MacKind kind)
    {
        using var document = LoadShared(bytes, kind);
        return document.Heads;
    }

    /// <summary>The document read with the Mac's rules; one this version can't read stops sync to ask.</summary>
    private JsonObject Decoded(AmDocument document, MacKind kind)
    {
        var tree = document.Read();
        var decoded = MacDecoder.Shared.Decode(tree, kind.RecordName);
        return decoded.Value ?? throw new UnreadableException(string.Join("; ", decoded.Errors.Take(3)),
            tree["name"] is AmScalar { Kind: AmScalarKind.String, Text: { Length: > 0 } name } ? name : null);
    }

    private string NameOf(string key) =>
        ledger.Documents.TryGetValue(key, out var entry) && entry.Name.Length > 0 ? entry.Name
        : ledger.Tombstones.TryGetValue(key, out var tombstone) && tombstone.Name.Length > 0 ? tombstone.Name
        : key;

    private static string Name(JsonObject json) => JsonRead.String(json["name"]) ?? "(untitled)";

    // MARK: This PC's copies (replicas)

    private string ReplicaPath(string key) => Path.Combine(stateFolder, "replicas", ReplicaFileName(key));

    /// <summary>"presentations/&lt;id&gt;.automerge", with any character Windows can't put in a file name replaced.</summary>
    private static string ReplicaFileName(string key)
    {
        var slash = key.IndexOf('/');
        var id = key[(slash + 1)..];
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(id.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        if (safe != id) safe += "-" + SharedFolder.Fingerprint(System.Text.Encoding.UTF8.GetBytes(id))[..8];
        return Path.Combine(key[..slash], safe + ".automerge");
    }

    private AmDocument? LoadReplica(string key)
    {
        var path = ReplicaPath(key);
        return File.Exists(path) ? AmDocument.Load(File.ReadAllBytes(path)) : null;
    }

    private void SaveReplica(string key, AmDocument replica)
    {
        var path = ReplicaPath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        SharedFolder.WriteAtomically(path, replica.Save());
    }

    private void DeleteReplica(string key)
    {
        var path = ReplicaPath(key);
        if (File.Exists(path)) File.Delete(path);
    }

    private void TrashReplica(string key)
    {
        var path = ReplicaPath(key);
        if (!File.Exists(path)) return;
        var copy = Path.Combine(TrashFolder, "replicas", ReplicaFileName(key));
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.Move(path, copy, overwrite: true);
    }
}

/// <summary>How far apart two versions of a document are: the number of values that differ.</summary>
internal static class JsonDistance
{
    public static int Between(JsonNode? a, JsonNode? b)
    {
        switch (a, b)
        {
            case (null, null):
                return 0;
            case (JsonObject x, JsonObject y):
                var keys = x.Select(p => p.Key).Union(y.Select(p => p.Key));
                return keys.Sum(k => Between(x[k], y[k]));
            case (JsonArray x, JsonArray y):
                var shared = Math.Min(x.Count, y.Count);
                var sum = Math.Abs(x.Count - y.Count);
                for (var i = 0; i < shared; i++) sum += Between(x[i], y[i]);
                return sum;
            case (JsonValue x, JsonValue y):
                return x.ToJsonString() == y.ToJsonString() ? 0 : 1;
            default:
                return 1;
        }
    }
}
