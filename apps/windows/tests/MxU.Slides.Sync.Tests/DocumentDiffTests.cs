using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;
using MxU.Slides.Sync.Automerge;
using MxU.Slides.Sync.Format;
using MxU.Slides.Sync.Tests.Fakes;

namespace MxU.Slides.Sync.Tests;

/// <summary>A Windows edit becomes the smallest Automerge edit: only what differs, lists matched by id.</summary>
public sealed class DocumentDiffTests
{
    private static readonly MacKind Kind = Samples.Presentations;

    private static string Ops(Presentation from, Presentation to)
    {
        using var document = MacSimulator.Create(from, Kind);
        return DocumentDiff.Shared.Edit(document.Read(), MacDocuments.ToJson(to), Kind.RecordName).ToString();
    }

    private static Presentation Edited(Presentation song, Action<Presentation> change)
    {
        var copy = MxuJson.Clone(song);
        change(copy);
        return copy;
    }

    [Fact]
    public void ChangingOneLyricTouchesOnlyThatSlide()
    {
        var song = Samples.Song();
        var ops = Ops(song, Edited(song, s => s.Slides[3].Objects[0].Text = "New words"));
        Assert.Equal("""[{"o":"put","p":["slides",3,"objects",0],"k":"text","v":{"s":"New words"}}]""", ops);
    }

    [Fact]
    public void AnAddedSlideIsInsertedWhereItBelongs()
    {
        var song = Samples.Song();
        var ops = Ops(song, Edited(song, s => s.Slides.Insert(2, new Slide { Id = "new", Name = "New", Objects = [] })));
        Assert.StartsWith("""[{"o":"insObj","p":["slides"],"i":2,"t":"map"},{"o":"put","p":["slides",2],"k":"id","v":{"s":"new"}}""", ops);
        Assert.DoesNotContain("\"del\"", ops);
    }

    [Fact]
    public void ARemovedSlideIsDeletedAndNothingElse()
    {
        var song = Samples.Song();
        Assert.Equal("""[{"o":"del","p":["slides"],"k":1}]""", Ops(song, Edited(song, s => s.Slides.RemoveAt(1))));
    }

    [Fact]
    public void AMovedSlideIsRemovedAndInsertedAgain()
    {
        var song = Samples.Song();
        var moved = Edited(song, s =>
        {
            var slide = s.Slides[4];
            s.Slides.RemoveAt(4);
            s.Slides.Insert(0, slide);
        });
        var ops = Ops(song, moved);
        Assert.StartsWith("""[{"o":"del","p":["slides"],"k":4},{"o":"insObj","p":["slides"],"i":0,"t":"map"}""", ops);
        using var document = MacSimulator.Create(song, Kind);
        MacDocuments.Shared.Edit(document, MacDocuments.ToJson(moved), Kind, DateTimeOffset.UtcNow);
        Assert.True(MxuJson.StructurallyEqual(moved, MacSimulator.Read<Presentation>(document, Kind)));
    }

    [Fact]
    public void AListOfValuesKeepsWhatBothShare()
    {
        var song = Samples.Song();
        // v1, c, v2, c  →  v1, c, c, v2: one value moved, so one deletion and one insertion.
        var ops = Ops(song, Edited(song, s => s.Arrangements![0].SectionIds = ["v1", "c", "c", "v2"]));
        Assert.Equal(2, ops.Split("\"o\"").Length - 1);
    }

    [Fact]
    public void AClearedFieldIsDeletedAndANewOneSet()
    {
        var deck = Samples.RichDeck();
        var ops = Ops(deck, Edited(deck, d =>
        {
            d.Folder = null;
            d.MusicKey = "G";
        }));
        Assert.Contains("""{"o":"del","p":[],"k":"folder"}""", ops);
        Assert.Contains("""{"o":"put","p":[],"k":"musicKey","v":{"s":"G"}}""", ops);
    }

    [Fact]
    public void KeysTheSchemaDoesNotNameAreNeverTouched()
    {
        using var document = MacSimulator.Create(Samples.Song(), Kind);
        document.Apply(new AmEdit().Put(AmPath.Root, "signages", AmWrite.String("from an older Mac")), null, DateTimeOffset.UtcNow);
        var renamed = Edited(Samples.Song(), s => s.Name = "Renamed");
        MacDocuments.Shared.Edit(document, MacDocuments.ToJson(renamed), Kind, DateTimeOffset.UtcNow);
        Assert.Equal(AmScalar.Of("from an older Mac"), document.Read()["signages"]);
    }

    [Fact]
    public void LongestCommonRunFindsTheSharedOrder()
    {
        int[] before = [1, 2, 3, 4, 5];
        int[] after = [2, 9, 3, 5, 1];
        var pairs = DocumentDiff.LongestCommonRun(before.Length, after.Length, (i, j) => before[i] == after[j]);
        Assert.Equal([2, 3, 5], pairs.Select(p => before[p.Old]));
        Assert.All(pairs, p => Assert.Equal(before[p.Old], after[p.New]));
    }

    [Fact]
    public void ManySmallEditsAllReadBack()
    {
        var random = new Random(7);
        var song = Samples.Song();
        using var document = MacSimulator.Create(song, Kind);
        for (var round = 0; round < 40; round++)
        {
            song = Edited(song, s =>
            {
                switch (random.Next(5))
                {
                    case 0: s.Slides.Insert(random.Next(s.Slides.Count + 1), new Slide { Id = $"r{round}", Name = $"R{round}", Objects = [] }); break;
                    case 1 when s.Slides.Count > 1: s.Slides.RemoveAt(random.Next(s.Slides.Count)); break;
                    case 2: s.Slides[random.Next(s.Slides.Count)].Notes = $"note {round}"; break;
                    case 3:
                        var from = random.Next(s.Slides.Count);
                        var slide = s.Slides[from];
                        s.Slides.RemoveAt(from);
                        s.Slides.Insert(random.Next(s.Slides.Count + 1), slide);
                        break;
                    default: s.Arrangements![0].SectionIds.Insert(0, "c"); break;
                }
            });
            MacDocuments.Shared.Edit(document, MacDocuments.ToJson(song), Kind, DateTimeOffset.UtcNow);
            Assert.True(MxuJson.StructurallyEqual(song, MacSimulator.Read<Presentation>(document, Kind)), $"round {round}");
        }
    }
}
