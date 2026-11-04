using MxU.Slides.Sync.Format;

namespace MxU.Slides.Sync.Engine;

public sealed partial class SyncEngine
{
    /// <summary>When each synced document was first seen missing from the shared folder.</summary>
    private readonly Dictionary<string, DateTimeOffset> missingSince = new(StringComparer.Ordinal);

    /// <summary>What one pass found, and what it stopped for.</summary>
    private sealed class Pass(SyncEngine engine)
    {
        /// <summary>Shared documents this version can't read: sync stops to ask about them.</summary>
        public readonly List<AttentionItem> Unreadable = [];

        /// <summary>Synced documents that have been gone from the shared folder long enough to count as deleted there.</summary>
        public readonly HashSet<string> ConfirmedMissing = new(StringComparer.Ordinal);

        /// <summary>
        /// Looks through both libraries and adds to <paramref name="keys"/> every document that
        /// changed on either side since the ledger was written: shared files whose size or time
        /// moved, Windows documents sync hasn't seen, and documents now missing on one side.
        /// A document missing from the shared folder counts as deleted there only once it has been
        /// missing a few seconds (cloud folders replace a file by deleting and re-creating it), and
        /// never when its whole kind folder has gone. Many going at once holds them all to ask.
        /// Returns false when it stopped to ask.
        /// </summary>
        public bool Survey(HashSet<string> keys)
        {
            var ledger = engine.ledger;
            var now = engine.options.Now();
            var missing = new List<string>();
            foreach (var kind in MacKind.SyncedKinds)
            {
                Dictionary<string, FileStamp>? listing;
                try { listing = engine.shared.List(kind); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { listing = null; }
                var local = engine.windows.Ids(kind).ToHashSet(StringComparer.Ordinal);
                if (listing is not null)
                    foreach (var (id, stamp) in listing)
                    {
                        var key = Key(kind, id);
                        engine.missingSince.Remove(key);
                        if (!ledger.Documents.TryGetValue(key, out var entry) || entry.SharedLength != stamp.Length || entry.SharedWriteUtc != stamp.WriteUtc)
                            keys.Add(key);
                    }
                foreach (var id in local)
                {
                    var key = Key(kind, id);
                    if (!ledger.Documents.TryGetValue(key, out var entry))
                    {
                        keys.Add(key);
                    }
                    else if (!engine.checkedEveryLocalDocument)
                    {
                        // Once after starting, every Windows file is looked at: it may have been edited
                        // while sync wasn't running. After that, the library's Changed event says.
                        if (engine.windows.Stamp(kind, id) is not { } stamp || stamp.Length != entry.LocalLength || stamp.WriteUtc != entry.LocalWriteUtc)
                            keys.Add(key);
                    }
                }
                var prefix = kind.DirectoryName + "/";
                foreach (var key in ledger.Documents.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    var id = key[prefix.Length..];
                    if (!local.Contains(id)) keys.Add(key);
                    else if (listing is not null && !listing.ContainsKey(id)) missing.Add(key);
                }
            }
            engine.checkedEveryLocalDocument = true;

            var confirmed = new List<string>();
            var decided = 0;
            foreach (var key in missing)
            {
                if (engine.approvedDeletions?.Contains(key) == true || engine.restoreDeletions?.Contains(key) == true)
                {
                    confirmed.Add(key);
                    decided++;
                }
                else if (!engine.missingSince.TryGetValue(key, out var since))
                {
                    engine.missingSince[key] = now;
                    engine.RequestFull(engine.options.MissingFor + TimeSpan.FromMilliseconds(500));
                }
                else if (now - since >= engine.options.MissingFor)
                {
                    confirmed.Add(key);
                }
            }
            var undecided = confirmed.Count - decided;
            var many = Math.Max(engine.options.MassDeletionMinimum, (int)Math.Ceiling(engine.options.MassDeletionShare * ledger.Documents.Count));
            if (undecided >= many)
            {
                var items = confirmed
                    .Where(k => engine.approvedDeletions?.Contains(k) != true && engine.restoreDeletions?.Contains(k) != true)
                    .Select(k => new AttentionItem(k, ledger.Documents[k].Name, "missing from the shared folder"))
                    .ToList();
                var message = $"Sync stopped: {Count(items.Count, "document")} disappeared from the shared folder at once. "
                    + "If they were deleted on the Mac, delete them here too; if not (a folder half-synced, a drive swapped), keep them, and they're put back.";
                engine.attention = new SyncAttention(AttentionKind.HeldDeletions, message, items);
                engine.Log.Add(SyncDirection.Problem, message);
                engine.SetStatus(SyncState.NeedsAttention, $"Stopped: {Count(items.Count, "document")} disappeared from the shared folder at once");
                return false;
            }
            foreach (var key in confirmed)
            {
                ConfirmedMissing.Add(key);
                keys.Add(key);
                engine.missingSince.Remove(key);
            }
            return true;
        }
    }
}
