using System.Text.Json.Nodes;
using MxU.Slides.Sync.Automerge;
using MxU.Slides.Sync.Format;

namespace MxU.Slides.Sync.Tests;

/// <summary>
/// The decoder accepts exactly what the Mac's decoder accepts (automerge-swift's keyed, unkeyed and
/// single-value decoding containers over Swift's synthesized Codable), and refuses what it refuses.
/// Ports AutomergeDecoderEquivalenceTests.swift's rule tests on a small schema of their own.
/// </summary>
public sealed class MacDecoderTests
{
    private static readonly MacSchema Schema = MacSchema.Parse("""
        {
          "schemaVersion": 1,
          "types": {
            "Tone": { "kind": "enum", "cases": ["warm", "cool"], "legacyAliases": { "tepid": "warm" } },
            "Inner": { "kind": "record", "fields": [
              { "name": "label", "type": "string" },
              { "name": "weight", "type": "double", "optional": true } ] },
            "Optionals": { "kind": "record", "fields": [
              { "name": "bool", "type": "bool", "optional": true },
              { "name": "string", "type": "string", "optional": true },
              { "name": "double", "type": "double", "optional": true },
              { "name": "int", "type": "int", "optional": true },
              { "name": "tone", "type": "Tone", "optional": true },
              { "name": "inner", "type": "Inner", "optional": true },
              { "name": "list", "type": "list:Inner", "optional": true },
              { "name": "strings", "type": "list:string", "optional": true },
              { "name": "doubles", "type": "list:double", "optional": true },
              { "name": "tones", "type": "list:Tone", "optional": true } ] },
            "NeedsName": { "kind": "record", "fields": [ { "name": "name", "type": "string" } ] }
          }
        }
        """u8);

    private static readonly MacDecoder Decoder = new(Schema);
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);

    /// <summary>A document holding whatever <paramref name="build"/> writes at its root.</summary>
    private static AmMap Document(Action<AmEdit> build)
    {
        using var document = AmDocument.Create();
        var edit = new AmEdit();
        build(edit);
        document.Apply(edit, null, Now);
        return document.Read();
    }

    private static readonly AmPath Root = AmPath.Root;

    [Fact]
    public void EveryKindOfValueDecodes()
    {
        var root = Document(e =>
        {
            e.Put(Root, "bool", AmWrite.Bool(true)).Put(Root, "string", AmWrite.String("grace")).Put(Root, "double", AmWrite.Double(0.1))
                .Put(Root, "int", AmWrite.Int(-42)).Put(Root, "tone", AmWrite.String("cool"))
                .PutObject(Root, "inner", AmObjectKind.Map).Put(Root.Then("inner"), "label", AmWrite.String("inner")).Put(Root.Then("inner"), "weight", AmWrite.Double(2.5))
                .PutObject(Root, "list", AmObjectKind.List).InsertObject(Root.Then("list"), 0, AmObjectKind.Map).Put(Root.Then("list").Then(0), "label", AmWrite.String("a"))
                .PutObject(Root, "strings", AmObjectKind.List).Insert(Root.Then("strings"), 0, AmWrite.String("x")).Insert(Root.Then("strings"), 1, AmWrite.String("y"));
        });
        var decoded = Decoder.Decode(root, "Optionals");
        Assert.True(decoded.Ok, string.Join("; ", decoded.Errors));
        Assert.Equal("""{"bool":true,"string":"grace","double":0.1,"int":-42,"tone":"cool","inner":{"label":"inner","weight":2.5},"list":[{"label":"a"}],"strings":["x","y"]}""",
            decoded.Value!.ToJsonString());
    }

    [Fact]
    public void AnAbsentOrNullOptionalIsNil()
    {
        // optionalsDecodeTheSameWhenPresentAbsentOrNull: decodeIfPresent reads a null as nil.
        var root = Document(e => e.Put(Root, "string", AmWrite.String("x")));
        Assert.Equal("""{"string":"x"}""", Decoder.Decode(root, "Optionals").Value!.ToJsonString());
        var withNulls = new AmMap(new Dictionary<string, AmNode>
        {
            ["bool"] = new AmScalar(AmScalarKind.Null),
            ["inner"] = new AmScalar(AmScalarKind.Null),
            ["list"] = new AmScalar(AmScalarKind.Null),
        });
        Assert.Equal("{}", Decoder.Decode(withNulls, "Optionals").Value!.ToJsonString());
    }

    [Fact]
    public void AMissingRequiredFieldFailsTheDocument()
    {
        var decoded = Decoder.Decode(Document(_ => { }), "NeedsName");
        Assert.False(decoded.Ok);
        Assert.Contains("name is missing", decoded.Errors);
        var nulled = new AmMap(new Dictionary<string, AmNode> { ["name"] = new AmScalar(AmScalarKind.Null) });
        Assert.False(Decoder.Decode(nulled, "NeedsName").Ok);
    }

    [Fact]
    public void AWrongTypeFailsTheDocument()
    {
        // mismatchesAndMissingKeysStillThrow: "count": "three", "inner": "flat", "inner": ["a", "b"].
        Assert.False(Decoder.Decode(Document(e => e.Put(Root, "int", AmWrite.String("three"))), "Optionals").Ok);
        Assert.False(Decoder.Decode(Document(e => e.Put(Root, "inner", AmWrite.String("flat"))), "Optionals").Ok);
        Assert.False(Decoder.Decode(Document(e => e.PutObject(Root, "inner", AmObjectKind.List)), "Optionals").Ok);
        Assert.False(Decoder.Decode(Document(e => e.Put(Root, "bool", AmWrite.Int(1))), "Optionals").Ok);
        Assert.False(Decoder.Decode(Document(e => e.Put(Root, "int", AmWrite.Double(1))), "Optionals").Ok, "a whole number is never an F64");
        Assert.False(Decoder.Decode(Document(e => e.PutObject(Root, "strings", AmObjectKind.Map)), "Optionals").Ok);
    }

    [Fact]
    public void ARecordsDoubleReadsAnIntButAListsDoesNot()
    {
        // Keyed: decodeLosslessStringConvertible takes an F64 or an Int. Unkeyed: decodeBinaryFloatingPoint takes only an F64.
        Assert.Equal("""{"double":3}""", Decoder.Decode(Document(e => e.Put(Root, "double", AmWrite.Int(3))), "Optionals").Value!.ToJsonString());
        var list = Document(e => e.PutObject(Root, "doubles", AmObjectKind.List).Insert(Root.Then("doubles"), 0, AmWrite.Int(3)));
        Assert.False(Decoder.Decode(list, "Optionals").Ok);
        var good = Document(e => e.PutObject(Root, "doubles", AmObjectKind.List).Insert(Root.Then("doubles"), 0, AmWrite.Double(3)));
        Assert.True(Decoder.Decode(good, "Optionals").Ok);
    }

    [Fact]
    public void AWholeNumberReadsAUintToo()
    {
        var root = new AmMap(new Dictionary<string, AmNode> { ["int"] = new AmScalar(AmScalarKind.Uint, Unsigned: 7) });
        Assert.Equal("""{"int":7}""", Decoder.Decode(root, "Optionals").Value!.ToJsonString());
    }

    [Fact]
    public void AWholeNumberTooBigForWindowsIsRefusedNotCutShort()
    {
        var decoded = Decoder.Decode(Document(e => e.Put(Root, "int", AmWrite.Int(5_000_000_000))), "Optionals");
        Assert.False(decoded.Ok);
        Assert.Contains(decoded.Errors, e => e.Contains("too big for Windows"));
    }

    [Fact]
    public void AStringMustBeAStringValueButAnEnumMayBeText()
    {
        var text = new AmMap(new Dictionary<string, AmNode> { ["string"] = new AmText("grace") });
        Assert.False(Decoder.Decode(text, "Optionals").Ok, "keyed decode(String) needs .Scalar(.String)");
        var tone = new AmMap(new Dictionary<string, AmNode> { ["tone"] = new AmText("warm") });
        Assert.Equal("""{"tone":"warm"}""", Decoder.Decode(tone, "Optionals").Value!.ToJsonString());
    }

    [Fact]
    public void AnUnknownEnumValueFailsTheWholeDocument()
    {
        // The shared format's enums are closed: the Mac refuses the whole document.
        var decoded = Decoder.Decode(Document(e => e.Put(Root, "string", AmWrite.String("fine")).Put(Root, "tone", AmWrite.String("hot"))), "Optionals");
        Assert.False(decoded.Ok);
        Assert.Contains(decoded.Errors, e => e.Contains("\"hot\""));
        var inList = Document(e => e.PutObject(Root, "tones", AmObjectKind.List).Insert(Root.Then("tones"), 0, AmWrite.String("hot")));
        Assert.False(Decoder.Decode(inList, "Optionals").Ok);
    }

    [Fact]
    public void ALegacyAliasReadsAsTheCurrentCase()
    {
        // Like PlaybackMode's "shuffle" → playAll.
        Assert.Equal("""{"tone":"warm"}""", Decoder.Decode(Document(e => e.Put(Root, "tone", AmWrite.String("tepid"))), "Optionals").Value!.ToJsonString());
        Assert.Equal("playAll", MacSchema.Shared.Enums["PlaybackMode"].Decode("shuffle"));
    }

    [Fact]
    public void KeysTheSchemaDoesNotNameAreSkippedAndReported()
    {
        // A field an older Mac wrote and the schema later retired, or one a newer Mac added.
        var decoded = Decoder.Decode(Document(e => e.Put(Root, "string", AmWrite.String("x")).Put(Root, "signage", AmWrite.String("old"))), "Optionals");
        Assert.True(decoded.Ok);
        Assert.Equal("""{"string":"x"}""", decoded.Value!.ToJsonString());
        Assert.Equal(new[] { "signage" }, decoded.UnknownKeys);
    }

    [Fact]
    public void ConflictsReadTheSameWinnerEverywhere()
    {
        // batchReadsAgreeWithGetUnderConflicts: two machines set one key at once; every copy reads one winner.
        for (var round = 0; round < 8; round++)
        {
            using var basis = AmDocument.Create();
            basis.Apply(new AmEdit().Put(Root, "string", AmWrite.String("start")), null, Now);
            using var left = basis.Fork();
            using var right = basis.Fork();
            left.Apply(new AmEdit().Put(Root, "string", AmWrite.String($"left {round}")), null, Now);
            right.Apply(new AmEdit().Put(Root, "string", AmWrite.String($"right {round}")), null, Now);
            using var leftFirst = left.Fork();
            leftFirst.Merge(right);
            using var rightFirst = right.Fork();
            rightFirst.Merge(left);
            var a = Decoder.Decode(leftFirst.Read(), "Optionals").Value!.ToJsonString();
            var b = Decoder.Decode(rightFirst.Read(), "Optionals").Value!.ToJsonString();
            Assert.Equal(a, b);
        }
    }

    [Fact]
    public void SchemaCheckRefusesWhatTheMacCouldNotRead()
    {
        Assert.Empty(SchemaCheck.Problems(JsonNode.Parse("""{"tone":"cool","inner":{"label":"x"}}""")!.AsObject(), "Optionals", Schema));
        Assert.NotEmpty(SchemaCheck.Problems(JsonNode.Parse("""{"tone":"hot"}""")!.AsObject(), "Optionals", Schema));
        Assert.NotEmpty(SchemaCheck.Problems(JsonNode.Parse("""{"tone":"tepid"}""")!.AsObject(), "Optionals", Schema)); // the Mac reads an alias but never writes one
        Assert.NotEmpty(SchemaCheck.Problems(JsonNode.Parse("""{"extra":1}""")!.AsObject(), "Optionals", Schema));
        Assert.NotEmpty(SchemaCheck.Problems(JsonNode.Parse("""{"double":"NaN"}""")!.AsObject(), "Optionals", Schema));
        Assert.NotEmpty(SchemaCheck.Problems(JsonNode.Parse("""{"int":1.5}""")!.AsObject(), "Optionals", Schema));
        Assert.NotEmpty(SchemaCheck.Problems(JsonNode.Parse("""{"inner":{}}""")!.AsObject(), "Optionals", Schema));
        Assert.NotEmpty(SchemaCheck.Problems(JsonNode.Parse("{}")!.AsObject(), "NeedsName", Schema));
    }
}
