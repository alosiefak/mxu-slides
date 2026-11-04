using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;
using MxU.Slides.Sync.Automerge;
using MxU.Slides.Sync.Engine;
using MxU.Slides.Sync.Format;
using MxU.Slides.Sync.Tests.Fakes;

namespace MxU.Slides.Sync.Tests;

/// <summary>
/// A Mac newer than this build. The shared schema grows: MxU's "make slides from notes" release
/// (#13) took it from 149 to 161, adding optional fields and enum cases. Whatever version this
/// build's models are, a Mac can always be one ahead, so these tests play a Mac on schema
/// <see cref="FutureVersion"/> (one above this build's) that writes fields and an enum case no
/// real schema will ever have. Windows must never drop what it doesn't know, and never guess at a
/// value it can't read.
/// </summary>
public sealed class NewerMacTests
{
    private static readonly MacKind Songs = Samples.Presentations;

    /// <summary>The schema version the pretend Mac is on: always one above this build's.</summary>
    private static readonly int FutureVersion = MacSchema.Shared.Version + 1;

    /// <summary>A field and an enum case no schema will ever have (checked by <see cref="TheFutureNamesAreNotInThisSchema"/>).</summary>
    private const string FutureField = "windowsTestFutureField";
    private const string FutureStyle = "windowsTestFutureStyle";
    private const string FutureCase = "windowsTestFutureCase";

    /// <summary>A song as that newer Mac writes it: new fields on the background cue and on slide 2's text, one a whole map.</summary>
    private static AmDocument NewerMacSong()
    {
        var song = Samples.Song();
        song.Background = new CueMedia { MediaId = "picture", Loops = true };
        var document = MacSimulator.Create(song, Songs);
        var root = AmPath.Root;
        var spot = root.Then("slides").Then(1).Then("objects").Then(0);
        document.Apply(new AmEdit()
            .Put(root.Then("background"), FutureField, AmWrite.String($"from schema {FutureVersion}"))
            .Put(spot, FutureField, AmWrite.Bool(true))
            .PutObject(spot, FutureStyle, AmObjectKind.Map)
            .Put(spot.Then(FutureStyle), "depth", AmWrite.Double(0.25)), null, DateTimeOffset.UtcNow);
        return document;
    }

    private static AmMap SlideObject(AmMap root, string slideId)
    {
        var slide = ((AmList)root["slides"]!).Items.Cast<AmMap>().Single(s => s["id"] is AmScalar { Text: var id } && id == slideId);
        return (AmMap)((AmList)slide["objects"]!).Items[0];
    }

    private static void AssertNewerFieldsKept(AmMap root)
    {
        Assert.Equal(AmScalar.Of($"from schema {FutureVersion}"), ((AmMap)root["background"]!)[FutureField]);
        var spot = SlideObject(root, "slide-2");
        Assert.Equal(AmScalar.Of(true), spot[FutureField]);
        Assert.Equal(AmScalar.Of(0.25), ((AmMap)spot[FutureStyle]!)["depth"]);
    }

    [Fact]
    public void TheFutureNamesAreNotInThisSchema()
    {
        foreach (var record in MacSchema.Shared.Records.Values)
        {
            Assert.Null(record.Field(FutureField));
            Assert.Null(record.Field(FutureStyle));
        }
        Assert.All(MacSchema.Shared.Enums.Values, e => Assert.Null(e.Decode(FutureCase)));
    }

    [Fact]
    public void FieldsANewerMacAddedAreSkippedWhenReadAndReported()
    {
        using var document = NewerMacSong();
        var decoded = MacDocuments.Shared.Decode(document, Songs);
        Assert.True(decoded.Ok);
        Assert.Contains($"background.{FutureField}", decoded.UnknownKeys);
        Assert.Contains($"slides[1].objects[0].{FutureField}", decoded.UnknownKeys);
        Assert.Contains($"slides[1].objects[0].{FutureStyle}", decoded.UnknownKeys);
        Assert.True(MxuJson.StructurallyEqual(MacSimulator.Read<Presentation>(document, Songs).Slides, Samples.Song().Slides));
    }

    [Fact]
    public void WindowsEditsAroundFieldsANewerMacAdded()
    {
        using var document = NewerMacSong();
        var song = MacSimulator.Read<Presentation>(document, Songs);
        song.Name = "Renamed on Windows";
        song.Slides[1].Objects[0].Text = "Edited on Windows";
        song.Background!.Loops = false;
        // slide-2 (the one with the photo spot) moves to the end; a new slide goes first; slide-4 goes.
        var moved = song.Slides[1];
        song.Slides.RemoveAt(1);
        song.Slides.Add(moved);
        song.Slides.Insert(0, new Slide { Id = "new", Name = "New", Objects = [] });
        song.Slides.RemoveAll(s => s.Id == "slide-4");
        MacDocuments.Shared.Edit(document, MacDocuments.ToJson(song), Songs, DateTimeOffset.UtcNow);

        Assert.True(MxuJson.StructurallyEqual(song, MacSimulator.Read<Presentation>(document, Songs)));
        var root = document.Read();
        AssertNewerFieldsKept(root);
        Assert.Equal(AmScalar.Of("Edited on Windows"), SlideObject(root, "slide-2")["text"]);
        Assert.Equal(AmScalar.Of(false), ((AmMap)root["background"]!)["loops"]);
    }

    [Fact]
    public void ThroughTheEngineTheMacsCopyKeepsItsNewerFields()
    {
        using var fixture = new SyncFixture();
        using (var mac = NewerMacSong()) fixture.MacSave(mac, Songs, "song");
        fixture.Sync();
        Assert.NotNull(fixture.Store.Get<Presentation>("song"));
        Assert.Contains(fixture.Engine.Log.Entries, e => e.Text.Contains(FutureField) && e.Text.Contains("kept as they are"));

        fixture.WindowsEdit<Presentation>("song", s =>
        {
            var slide = s.Slides[1];
            s.Slides.RemoveAt(1);
            s.Slides.Insert(3, slide);
            slide.Notes = "Windows";
        });
        // The Mac edits slide 1's notes from its slide grid (a write of just that slide: a newer
        // Mac's whole-document write would keep these fields too, as it knows them).
        using (var mac = fixture.MacOpen(Songs, "song"))
        {
            var value = MacSimulator.Read<Presentation>(mac, Songs);
            value.Slides[0].Notes = "Mac";
            MacSimulator.UpdateSlideList(mac, value);
            fixture.MacSave(mac, Songs, "song");
        }
        fixture.Sync();
        using var after = fixture.MacOpen(Songs, "song");
        AssertNewerFieldsKept(after.Read());
        var merged = fixture.Store.Get<Presentation>("song")!;
        Assert.Equal("Mac", merged.Slides[0].Notes);
        Assert.Equal("Windows", merged.Slides.Single(s => s.Id == "slide-2").Notes);
    }

    [Fact]
    public void AtTheFirstSyncAWindowsCopyChangedLaterKeepsTheMacsNewerFields()
    {
        using var fixture = new SyncFixture();
        using (var mac = NewerMacSong()) fixture.MacSave(mac, Songs, "song");
        var windows = Samples.Song(name: "Windows title");
        windows.Background = new CueMedia { MediaId = "picture", Loops = true };
        fixture.Store.Save(windows);
        File.SetLastWriteTimeUtc(fixture.Store.DocumentPath(typeof(Presentation), "song"), DateTime.UtcNow.AddMinutes(5));
        fixture.Sync();
        Assert.Equal("Windows title", fixture.MacRead<Presentation>(Songs, "song").Name);
        using var after = fixture.MacOpen(Songs, "song");
        AssertNewerFieldsKept(after.Read());
    }

    [Fact]
    public void AnEnumCaseANewerMacAddedStopsSyncAndIsNeverWritten()
    {
        // A newer Mac's MediaScaleMode case: this version can't hold it, so it can't guess.
        using var fixture = new SyncFixture();
        using (var mac = MacSimulator.Create(Samples.Song(), Songs))
        {
            mac.Apply(new AmEdit().Put(AmPath.Root.Then("slides").Then(0).Then("objects").Then(0), "mediaScaleMode", AmWrite.String(FutureCase)), null, DateTimeOffset.UtcNow);
            fixture.MacSave(mac, Songs, "song");
        }
        var before = fixture.MacBytes(Songs, "song");
        fixture.Sync();
        Assert.Equal(AttentionKind.Unreadable, fixture.Engine.Attention?.Kind);
        Assert.Contains(FutureCase, fixture.Engine.Attention!.Items.Single().Reason);
        Assert.Equal("Amazing Grace", fixture.Engine.Attention.Items.Single().Name);
        Assert.Null(fixture.Store.Get<Presentation>("song"));
        Assert.Equal(before, fixture.MacBytes(Songs, "song"));
    }

    [Fact]
    public void KindsWindowsDoesNotKeepAreLeftExactlyAsTheyAre()
    {
        // Notes (which upstream #13 builds slides from), font files, settings: not synced, never touched.
        using var fixture = new SyncFixture();
        var note = Path.Combine(fixture.Shared, "notes", "sermon.automerge");
        using (var document = AmDocument.Create())
        {
            document.Apply(new AmEdit().Put(AmPath.Root, "id", AmWrite.String("sermon")).Put(AmPath.Root, "name", AmWrite.String("Sermon")).Put(AmPath.Root, "buildMode", AmWrite.String("byStructure")), "create", DateTimeOffset.UtcNow);
            File.WriteAllBytes(note, document.Save());
        }
        var before = File.ReadAllBytes(note);
        fixture.Store.Save(Samples.Song());
        fixture.Sync(twice: true);
        Assert.Equal(before, File.ReadAllBytes(note));
        Assert.Single(Directory.GetFiles(Path.Combine(fixture.Shared, "notes")));
    }

    [Fact]
    public void AMovedItemThatCannotBeCopiedExactlyIsNotMoved()
    {
        // A value of a kind the Mac's encoder never writes (here a counter) can't be copied exactly:
        // rather than lose it, the edit is refused and the document stays as it was.
        using var document = MacSimulator.Create(Samples.Song(), Songs);
        var heads = document.Heads;
        var tree = document.Read();
        var slides = (AmList)tree["slides"]!;
        var withCounter = new Dictionary<string, AmNode>(((AmMap)slides.Items[1]).Entries) { ["views"] = new AmScalar(AmScalarKind.Counter, Integer: 3) };
        var current = new AmMap(new Dictionary<string, AmNode>(tree.Entries)
        {
            ["slides"] = new AmList(slides.Items.Select((s, i) => i == 1 ? new AmMap(withCounter) : s).ToList()),
        });
        var song = Samples.Song();
        var moved = song.Slides[1];
        song.Slides.RemoveAt(1);
        song.Slides.Add(moved);
        Assert.Throws<MacFormatException>(() => DocumentDiff.Shared.Edit(current, MacDocuments.ToJson(song), Songs.RecordName));
        Assert.Equal(heads, document.Heads);
    }
}
