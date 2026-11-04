using System.Text.Json.Nodes;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Sync.Automerge;
using MxU.Slides.Sync.Format;
using MxU.Slides.Sync.Tests.Fakes;

namespace MxU.Slides.Sync.Tests;

/// <summary>
/// Documents Windows writes are the documents the Mac writes: the same Automerge types in the same
/// places, readable by the Mac's decoding rules, and back again. Ports
/// AutomergeDecoderEquivalenceTests.swift where it applies.
/// </summary>
public sealed class MacFormatTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);

    [Fact]
    public void TheDllIsTheAutomergeTheMacBuilds()
    {
        Assert.Equal("mxu-automerge-ffi 1 (automerge 0.7.2)", AmDocument.LibraryVersion);
    }

    [Fact]
    public void TheDllNeedsNothingButWindowsItself()
    {
        // Built with Microsoft's C runtime linked in (native/automerge-ffi/.cargo/config.toml): a DLL
        // that imports VCRUNTIME140.dll wouldn't load on a PC without the Visual C++ Redistributable.
        var dll = Path.Combine(AppContext.BaseDirectory, "automerge.dll");
        var text = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(dll));
        Assert.DoesNotContain("VCRUNTIME", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("KERNEL32.dll", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheEmbeddedSchemaIsTheOneTheModelsWereGeneratedFrom()
    {
        Assert.Equal(SchemaInfo.SchemaVersion, MacSchema.Shared.Version);
        foreach (var kind in MacKind.All) Assert.True(MacSchema.Shared.Records.ContainsKey(kind.RecordName), kind.RecordName);
    }

    [Fact]
    public void EveryModelPropertyIsASchemaField()
    {
        // A property that isn't in the schema would be a field the Mac doesn't know.
        foreach (var kind in MacKind.All.Where(k => k.WindowsType is not null))
        {
            var json = MacDocuments.ToJson(Activator.CreateInstance(kind.WindowsType!)!);
            var record = MacSchema.Shared.Record(kind.RecordName);
            foreach (var (key, _) in json) Assert.True(record.Field(key) is not null, $"{kind.RecordName}.{key}");
        }
    }

    [Fact]
    public void TheMacsFixturesRoundTripThroughWindows()
    {
        // everyFixtureDecodesToTheValueItWasEncodedFrom and everyFixtureRoundTripsThroughTypedDocument.
        var fixtures = new List<(object Model, MacKind Kind)> { (Samples.RichDeck(), Samples.Presentations), (StarterContent.WelcomePresentation(), Samples.Presentations) };
        fixtures.AddRange(StarterPacks.All.Select(p => ((object)p.MakeTheme(), Samples.Themes)));
        fixtures.Add((Samples.Sunday(), Samples.Services));
        foreach (var (model, kind) in fixtures)
        {
            var json = MacDocuments.ToJson(model);
            using var made = MacDocuments.Shared.Create(json, kind, Now);
            using var loaded = AmDocument.Load(made.Save());
            var read = MacSimulator.Read(loaded, kind);
            Assert.Equal(MacDocuments.Canonical(json, kind), MacDocuments.Canonical(read, kind));
        }
    }

    [Fact]
    public void AWindowsDocumentHasTheTypesTheMacWrites()
    {
        using var made = MacDocuments.Shared.Create(MacDocuments.ToJson(Samples.RichDeck()), Samples.Presentations, Now);
        var root = made.Read();
        var slide = (AmMap)((AmList)root["slides"]!).Items[0];
        var title = (AmMap)((AmList)slide["objects"]!).Items[0];
        // A string is a string value, never a text object.
        Assert.Equal(AmScalar.Of("Rich"), root["name"]);
        Assert.Equal(AmScalar.Of("Speaker notes\nline two"), slide["notes"]);
        // An enum is its case's name.
        Assert.Equal(AmScalar.Of("deck"), root["presentationKind"]);
        Assert.Equal(AmScalar.Of("multiply"), title["blendMode"]);
        // A whole number is an Int; a double is an F64 even when it's whole.
        Assert.Equal(AmScalar.Of(1920L), root["canvasWidth"]);
        Assert.Equal(AmScalar.Of(120.0), title["x"]);
        Assert.Equal(AmScalar.Of(80.5), title["y"]);
        Assert.Equal(AmScalar.Of(false), title["hidden"]);
        // A record is a map, a list a list; an empty list is still there; an absent optional isn't.
        Assert.IsType<AmMap>(title["textStyle"]);
        Assert.Empty(((AmList)((AmMap)((AmList)root["slides"]!).Items[2])["objects"]!).Items);
        Assert.Null(root["background"]);
        Assert.Null(title["rotationDegrees"]);
    }

    [Fact]
    public void AWindowsDocumentIsOneChangeNamedCreate()
    {
        using var made = MacDocuments.Shared.Create(MacDocuments.ToJson(Samples.Song()), Samples.Presentations, Now);
        var change = Assert.Single(made.Changes());
        Assert.Equal("create", change.Message);
        Assert.Equal(Now.ToUnixTimeSeconds(), change.Time);
        Assert.True(WindowsActor.IsWindows(change.Actor));
    }

    [Fact]
    public void ADocumentTheMacMadeComesIntoWindows()
    {
        using var mac = MacSimulator.Create(Samples.RichDeck(), Samples.Presentations);
        using var loaded = AmDocument.Load(mac.Save());
        var decoded = MacDocuments.Shared.Decode(loaded, Samples.Presentations);
        Assert.True(decoded.Ok, string.Join("; ", decoded.Errors));
        var deck = (Presentation)MacDocuments.ToModel(decoded.Value!, Samples.Presentations);
        Assert.True(MxuJson.StructurallyEqual(Samples.RichDeck(), deck));
        Assert.False(WindowsActor.IsWindows(Assert.Single(loaded.Changes()).Actor));
    }

    [Fact]
    public void TheMacCanGoOnEditingWhatWindowsWrote()
    {
        // The Mac's next write checks every value's type first (cautiousWrite); the simulator throws
        // where the Mac would. A Mac edit on a Windows document must go through.
        using var made = MacDocuments.Shared.Create(MacDocuments.ToJson(Samples.RichDeck()), Samples.Presentations, Now);
        var deck = MacSimulator.Read<Presentation>(made, Samples.Presentations);
        deck.Slides[0].Objects[0].Text = "Amazing grace, how sweet";
        deck.Slides.Add(new Slide { Id = "s4", Name = "Four", Objects = [] });
        MacSimulator.Update(made, deck, Samples.Presentations);
        Assert.True(MxuJson.StructurallyEqual(deck, MacSimulator.Read<Presentation>(made, Samples.Presentations)));
    }

    [Fact]
    public void WindowsCanGoOnEditingWhatTheMacWrote()
    {
        using var mac = MacSimulator.Create(Samples.Song(), Samples.Presentations);
        var song = MacSimulator.Read<Presentation>(mac, Samples.Presentations);
        song.Name = "Amazing Grace (My Chains Are Gone)";
        song.Slides.RemoveAt(1);
        song.Arrangements![0].SectionIds = ["v1", "c", "c", "v2"];
        var applied = MacDocuments.Shared.Edit(mac, MacDocuments.ToJson(song), Samples.Presentations, Now);
        Assert.True(applied > 0);
        Assert.True(MxuJson.StructurallyEqual(song, MacSimulator.Read<Presentation>(mac, Samples.Presentations)));
        Assert.True(WindowsActor.IsWindows(mac.Changes()[^1].Actor));
    }

    [Fact]
    public void NothingChangedWritesNothing()
    {
        using var mac = MacSimulator.Create(Samples.RichDeck(), Samples.Presentations);
        var before = mac.Heads;
        Assert.Equal(0, MacDocuments.Shared.Edit(mac, MacDocuments.ToJson(Samples.RichDeck()), Samples.Presentations, Now));
        Assert.Equal(before, mac.Heads);
    }

    [Fact]
    public void SavedBytesLoadToTheSameHeads()
    {
        using var made = MacDocuments.Shared.Create(MacDocuments.ToJson(Samples.Song()), Samples.Presentations, Now);
        using var loaded = AmDocument.Load(made.Save());
        Assert.Equal(made.Heads, loaded.Heads);
        Assert.Single(loaded.Heads.Hashes);
        Assert.Equal(64, loaded.Heads.Hashes[0].Length);
    }

    [Fact]
    public void ADamagedFileIsRefusedNotHalfRead()
    {
        using var made = MacDocuments.Shared.Create(MacDocuments.ToJson(Samples.Song()), Samples.Presentations, Now);
        var bytes = made.Save();
        Assert.Throws<AutomergeException>(() => AmDocument.Load(bytes.AsSpan(0, bytes.Length / 2)));
        Assert.Throws<AutomergeException>(() => AmDocument.Load("not automerge"u8));
    }

    [Fact]
    public void AnEditThatFailsHalfWayChangesNothing()
    {
        using var document = AmDocument.Create();
        var heads = document.Heads;
        var edit = new AmEdit().Put(AmPath.Root, "name", AmWrite.String("x")).Put(AmPath.Root.Then("missing"), "name", AmWrite.String("y"));
        Assert.Throws<AutomergeException>(() => document.Apply(edit, null, Now));
        Assert.Equal(heads, document.Heads);
        Assert.Empty(document.Read().Entries);
    }

    [Fact]
    public void ForkAtGoesBackAndMergeBringsBothSidesTogether()
    {
        using var document = MacDocuments.Shared.Create(MacDocuments.ToJson(Samples.Song()), Samples.Presentations, Now);
        var start = document.Heads;
        using (var later = document.Fork())
        {
            later.Apply(new AmEdit().Put(AmPath.Root, "name", AmWrite.String("Later")), null, Now);
            document.Merge(later);
        }
        using var back = document.ForkAt(start);
        Assert.Equal(AmScalar.Of("Amazing Grace"), back.Read()["name"]);
        back.Apply(new AmEdit().Put(AmPath.Root, "musicKey", AmWrite.String("G")), null, Now);
        document.Merge(back);
        Assert.Equal(AmScalar.Of("Later"), document.Read()["name"]);
        Assert.Equal(AmScalar.Of("G"), document.Read()["musicKey"]);
        Assert.Equal(2, document.Heads.Hashes.Count);
    }

    [Fact]
    public void CanonicalJsonIgnoresHowNumbersWereSpelled()
    {
        var text = MxuJson.Serialize(Samples.RichDeck(), indented: false);
        Assert.Contains("\"x\":120,", text);
        var a = JsonNode.Parse(text)!.AsObject();
        var b = JsonNode.Parse(text.Replace("\"x\":120,", "\"x\":1.2E2,"))!.AsObject();
        Assert.Equal(MacDocuments.Canonical(a, Samples.Presentations), MacDocuments.Canonical(b, Samples.Presentations));
    }
}
