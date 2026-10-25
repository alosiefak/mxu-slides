using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MxU.Slides.Core.Json;
using MxU.Slides.Sync.Automerge;

namespace MxU.Slides.Sync.Format;

/// <summary>An Automerge edit didn't read back as the document Windows meant to write, so it was thrown away.</summary>
public sealed class MacFormatException(string message) : Exception(message);

/// <summary>
/// The actors Windows writes under. An actor is the name Automerge files each change under; the
/// Mac picks a random one each time it opens a document. Windows does too, but starts each with
/// the four bytes "MXUW", so library sync can tell a change made on Windows from one made on a
/// Mac (it uses that to tell "the Mac deleted this" from "the Mac never had it").
/// </summary>
public static class WindowsActor
{
    private static readonly byte[] Prefix = "MXUW"u8.ToArray();

    /// <summary>A new random 16-byte actor that starts with "MXUW".</summary>
    public static byte[] New()
    {
        var actor = new byte[16];
        Prefix.CopyTo(actor, 0);
        RandomNumberGenerator.Fill(actor.AsSpan(Prefix.Length));
        return actor;
    }

    /// <summary>True for an actor (as hex) that <see cref="New"/> made.</summary>
    public static bool IsWindows(string actorHex) => actorHex.StartsWith(Convert.ToHexStringLower(Prefix), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Library documents as Automerge documents the Mac can read: made new, read, and edited, each
/// write checked before it's kept. Every write goes the same way:
/// <list type="number">
/// <item>the JSON is checked against the schema (<see cref="SchemaCheck"/>): no unknown field, no
/// unknown enum value;</item>
/// <item>the change is made as the Mac's encoder would make it (<see cref="MacEncoder"/>, <see cref="DocumentDiff"/>);</item>
/// <item>the document is read back with the Mac's decoding rules (<see cref="MacDecoder"/>), and must
/// say exactly what Windows meant. If it doesn't, the change is thrown away and nothing is written.</item>
/// </list>
/// </summary>
public sealed class MacDocuments(MacSchema schema)
{
    public static readonly MacDocuments Shared = new(MacSchema.Shared);

    private readonly MacEncoder encoder = new(schema);
    private readonly DocumentDiff diff = new(schema);
    private readonly MacDecoder decoder = new(schema);

    /// <summary>Reads a document with the Mac's rules.</summary>
    public MacDecoded Decode(AmDocument document, MacKind kind) => decoder.Decode(document.Read(), kind.RecordName);

    /// <summary>
    /// A new document holding <paramref name="value"/>, made as the Mac's <c>TypedDocument(value)</c>
    /// makes one (every field in one change, with the message "create"), under a Windows actor.
    /// Checked as described above.
    /// </summary>
    public AmDocument Create(JsonObject value, MacKind kind, DateTimeOffset time)
    {
        var document = AmDocument.Create();
        try
        {
            document.Actor = WindowsActor.New();
            document.Apply(encoder.NewDocument(value, kind.RecordName), message: "create", time);
            Verify(document, value, kind);
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Changes <paramref name="document"/> so it says <paramref name="target"/>, writing only what
    /// differs, as one change under a new Windows actor. Use it on a copy (<see cref="AmDocument.Fork"/>
    /// or <see cref="AmDocument.ForkAt"/>): if the check fails it throws, and the copy should be
    /// thrown away. Returns the number of operations written (0 when nothing differed).
    /// </summary>
    public int Edit(AmDocument document, JsonObject target, MacKind kind, DateTimeOffset time)
    {
        var edit = diff.Edit(document.Read(), target, kind.RecordName);
        if (edit.Count == 0) return 0;
        document.Actor = WindowsActor.New();
        var applied = document.Apply(edit, message: null, time);
        Verify(document, target, kind);
        return applied;
    }

    private void Verify(AmDocument document, JsonObject wanted, MacKind kind)
    {
        var read = Decode(document, kind);
        if (read.Value is null)
            throw new MacFormatException($"A {kind.RecordName} written for the Mac wouldn't read back: {string.Join("; ", read.Errors.Take(3))}");
        if (!Canonical(read.Value, kind).AsSpan().SequenceEqual(Canonical(wanted, kind)))
            throw new MacFormatException($"A {kind.RecordName} written for the Mac read back differently from what was meant");
    }

    /// <summary>
    /// The document's JSON exactly as Windows saves it (through its C# model, compact): two
    /// documents that mean the same give the same bytes, however their numbers were spelled.
    /// </summary>
    public static byte[] Canonical(JsonObject value, MacKind kind) =>
        MxuJson.SerializeToUtf8(ToModel(value, kind), indented: false);

    /// <summary>A short fingerprint of <see cref="Canonical"/>, for the sync ledger.</summary>
    public static string Hash(byte[] canonical) => Convert.ToHexStringLower(SHA256.HashData(canonical))[..32];

    /// <summary>The C# model (Presentation, Theme…) for a document's JSON.</summary>
    public static object ToModel(JsonObject value, MacKind kind) =>
        value.Deserialize(kind.WindowsType ?? throw new InvalidOperationException($"Windows doesn't keep {kind.RawValue} documents"), MxuJson.Options)
        ?? throw new JsonException($"Empty {kind.RecordName}");

    /// <summary>A C# model's JSON.</summary>
    public static JsonObject ToJson(object model) =>
        JsonSerializer.SerializeToNode(model, model.GetType(), MxuJson.Options) as JsonObject
        ?? throw new JsonException($"{model.GetType().Name} didn't serialize to an object");
}
