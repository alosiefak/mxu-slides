using System.Text;
using System.Text.Json;

namespace MxU.Slides.Sync.Automerge;

/// <summary>Automerge refused something, or automerge.dll is missing or the wrong version.</summary>
public sealed class AutomergeException(string message) : Exception(message);

/// <summary>
/// A document's heads: the hashes of its newest changes, as lower-case hex, sorted (the Mac's
/// <c>SyncLedger.hex</c> lists them the same way). Two documents with the same heads hold the
/// same changes. Compared by value.
/// </summary>
public sealed class DocHeads : IEquatable<DocHeads>
{
    public static readonly DocHeads None = new([]);

    public DocHeads(IEnumerable<string> hashes) => Hashes = hashes.Select(h => h.ToLowerInvariant()).Order(StringComparer.Ordinal).ToArray();

    public IReadOnlyList<string> Hashes { get; }

    public bool IsEmpty => Hashes.Count == 0;

    public static DocHeads FromBytes(ReadOnlySpan<byte> bytes)
    {
        var hashes = new List<string>();
        for (var at = 0; at + 32 <= bytes.Length; at += 32) hashes.Add(Convert.ToHexStringLower(bytes.Slice(at, 32)));
        return new DocHeads(hashes);
    }

    public byte[] ToBytes() => Hashes.SelectMany(Convert.FromHexString).ToArray();

    public bool Equals(DocHeads? other) => other is not null && Hashes.SequenceEqual(other.Hashes, StringComparer.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as DocHeads);

    public override int GetHashCode() => Hashes.Aggregate(17, (h, s) => HashCode.Combine(h, s));

    public override string ToString() => string.Join(",", Hashes.Select(h => h[..8]));
}

/// <summary>One change in a document's history: who made it (the actor), its number in that actor's
/// run (seq), when (seconds since 1970, as the maker's clock said), its message and what it followed.</summary>
public sealed record AmChange(string Hash, string Actor, long Seq, long Time, string? Message, IReadOnlyList<string> Deps);

/// <summary>
/// One Automerge document, held in automerge.dll. This is the Windows twin of automerge-swift's
/// <c>Document</c>, with just what library sync needs: load, save, read, edit, fork, merge and
/// history. Safe to use from several threads (calls take turns). Dispose it when done; a
/// forgotten one is still freed, later, by the garbage collector.
/// </summary>
public sealed class AmDocument : IDisposable
{
    private static readonly Lazy<string> Loaded = new(CheckLibrary);

    private static long made;

    private readonly DocHandle handle;
    private readonly Lock gate = new();

    // A number per document, so two documents are always locked in the same order (see Merge).
    private readonly long serial = Interlocked.Increment(ref made);

    private AmDocument(DocHandle handle) => this.handle = handle;

    /// <summary>
    /// "mxu-automerge-ffi 1 (automerge 0.7.2)". Throws <see cref="AutomergeException"/> if automerge.dll
    /// is missing or isn't the version this code was written for.
    /// </summary>
    public static string LibraryVersion => Loaded.Value;

    /// <summary>A new, empty document (a map at the root), with a random actor.</summary>
    public static AmDocument Create()
    {
        _ = Loaded.Value;
        Check(AutomergeNative.New(out var handle));
        return new AmDocument(handle);
    }

    /// <summary>Loads a saved document (a whole .automerge file). A damaged or unknown file throws.</summary>
    public static AmDocument Load(ReadOnlySpan<byte> bytes)
    {
        _ = Loaded.Value;
        Check(AutomergeNative.Load(bytes, (nuint)bytes.Length, out var handle));
        return new AmDocument(handle);
    }

    /// <summary>The whole document, compressed, as the Mac saves it.</summary>
    public byte[] Save()
    {
        lock (gate)
        {
            Check(AutomergeNative.Save(handle, out var bytes));
            return AutomergeNative.Take(bytes);
        }
    }

    /// <summary>A copy, with its own random actor.</summary>
    public AmDocument Fork()
    {
        lock (gate)
        {
            Check(AutomergeNative.Fork(handle, out var copy));
            return new AmDocument(copy);
        }
    }

    /// <summary>
    /// A copy of the document as it was at <paramref name="heads"/>, with its own random actor. An
    /// edit made on it and merged back is concurrent with everything since those heads: Automerge
    /// combines the two, as it does two Macs' edits.
    /// </summary>
    public AmDocument ForkAt(DocHeads heads)
    {
        var bytes = heads.ToBytes();
        lock (gate)
        {
            Check(AutomergeNative.ForkAt(handle, bytes, (nuint)bytes.Length, out var copy));
            return new AmDocument(copy);
        }
    }

    /// <summary>Takes in every change <paramref name="other"/> has that this lacks.</summary>
    public void Merge(AmDocument other)
    {
        if (ReferenceEquals(other, this)) return;
        // Always lock in the same order, so two merges the other way round can't deadlock.
        var (first, second) = serial < other.serial ? (this, other) : (other, this);
        lock (first.gate)
        lock (second.gate)
            Check(AutomergeNative.Merge(handle, other.handle));
    }

    /// <summary>The newest changes' hashes.</summary>
    public DocHeads Heads
    {
        get
        {
            lock (gate)
            {
                Check(AutomergeNative.Heads(handle, out var bytes));
                return DocHeads.FromBytes(AutomergeNative.Take(bytes));
            }
        }
    }

    /// <summary>Every change, oldest first.</summary>
    public IReadOnlyList<AmChange> Changes()
    {
        byte[] json;
        lock (gate)
        {
            Check(AutomergeNative.Changes(handle, out var bytes));
            json = AutomergeNative.Take(bytes);
        }
        using var parsed = JsonDocument.Parse(json);
        return parsed.RootElement.EnumerateArray().Select(c => new AmChange(
            c.GetProperty("hash").GetString()!,
            c.GetProperty("actor").GetString()!,
            c.GetProperty("seq").GetInt64(),
            c.GetProperty("time").GetInt64(),
            c.GetProperty("message").ValueKind == JsonValueKind.String ? c.GetProperty("message").GetString() : null,
            c.GetProperty("deps").EnumerateArray().Select(d => d.GetString()!).ToList())).ToList();
    }

    /// <summary>The actor this document's next changes are made under.</summary>
    public byte[] Actor
    {
        get
        {
            lock (gate)
            {
                Check(AutomergeNative.GetActor(handle, out var bytes));
                return AutomergeNative.Take(bytes);
            }
        }
        set
        {
            lock (gate) Check(AutomergeNative.SetActor(handle, value, (nuint)value.Length));
        }
    }

    /// <summary>The whole document, with each value's Automerge type (see <see cref="AmNode"/>).</summary>
    public AmMap Read()
    {
        byte[] json;
        lock (gate)
        {
            Check(AutomergeNative.Read(handle, out var bytes));
            json = AutomergeNative.Take(bytes);
        }
        return AmTreeReader.Parse(json) as AmMap ?? throw new AutomergeException("The document's root isn't a map");
    }

    /// <summary>
    /// Carries out <paramref name="edit"/> in one transaction and commits it as one change, with
    /// <paramref name="message"/> and <paramref name="time"/> (recorded to the second, as the Mac
    /// does). If any operation fails, none happen and this throws. An empty edit commits nothing.
    /// Returns the number of operations applied.
    /// </summary>
    public int Apply(AmEdit edit, string? message, DateTimeOffset time)
    {
        if (edit.Count == 0) return 0;
        var ops = edit.ToUtf8();
        var text = message is null ? [] : Encoding.UTF8.GetBytes(message);
        lock (gate)
        {
            Check(AutomergeNative.Apply(handle, ops, (nuint)ops.Length, text, (nuint)text.Length,
                message is null ? 0 : 1, time.ToUnixTimeSeconds(), out var applied));
            return (int)applied;
        }
    }

    public void Dispose() => handle.Dispose();

    private static void Check(int status)
    {
        if (status != AutomergeNative.Ok)
            throw new AutomergeException((status == AutomergeNative.Panicked ? "Automerge failed inside: " : "") + AutomergeNative.LastErrorMessage());
    }

    private static string CheckLibrary()
    {
        uint version;
        try
        {
            version = AutomergeNative.FfiVersion();
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new AutomergeException($"automerge.dll couldn't be loaded ({error.Message}). Reinstall MxU Slides.");
        }
        if (version != AutomergeNative.ExpectedFfiVersion)
            throw new AutomergeException($"automerge.dll is interface version {version}; this app needs {AutomergeNative.ExpectedFfiVersion}. Reinstall MxU Slides.");
        Check(AutomergeNative.Version(out var bytes));
        return Encoding.UTF8.GetString(AutomergeNative.Take(bytes));
    }
}
