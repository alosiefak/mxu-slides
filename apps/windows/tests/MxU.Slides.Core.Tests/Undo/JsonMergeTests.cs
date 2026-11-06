using System.Text.Json.Nodes;
using MxU.Slides.Core.Undo;

namespace MxU.Slides.Core.Tests.Undo;

public sealed class JsonMergeTests
{
    private static JsonNode? J(string json) => JsonNode.Parse(json);

    private static string Merge(string basis, string current, string wanted) =>
        JsonMerge.Merge(J(basis), J(current), J(wanted))!.ToJsonString();

    private static string List(params string[] ids) => "[" + string.Join(",", ids.Select(id => $"{{\"id\":\"{id}\"}}")) + "]";

    private static string[] Ids(string json) => [.. JsonNode.Parse(json)!.AsArray().Select(n => (string)n!["id"]!)];

    [Fact]
    public void APartNobodyTouchedSinceGoesBack()
    {
        // The step renamed it; nothing changed since: undo puts the old name back.
        Assert.Equal("""{"name":"Old","x":1}""", Merge("""{"name":"New","x":1}""", """{"name":"New","x":1}""", """{"name":"Old","x":1}"""));
    }

    [Fact]
    public void APartTheStepDidntChangeKeepsItsNewValue()
    {
        // The step renamed it; someone moved it since: the name goes back and the move stays.
        Assert.Equal("""{"name":"Old","x":9}""", Merge("""{"name":"New","x":1}""", """{"name":"New","x":9}""", """{"name":"Old","x":1}"""));
    }

    [Fact]
    public void WhenBothChangedTheSameThingTheLaterEditWins()
    {
        Assert.Equal("""{"name":"Later"}""", Merge("""{"name":"New"}""", """{"name":"Later"}""", """{"name":"Old"}"""));
    }

    [Fact]
    public void APropertyTheStepAddedGoesAndOneItRemovedComesBack()
    {
        Assert.Equal("""{"a":1}""", Merge("""{"a":1,"b":2}""", """{"a":1,"b":2}""", """{"a":1}"""));
        Assert.Equal("""{"a":1,"b":2,"c":3}""", Merge("""{"a":1,"c":3}""", """{"a":1,"c":3}""", """{"a":1,"b":2,"c":3}"""));
    }

    [Fact]
    public void ASlideTheStepDeletedReturnsToItsPlace()
    {
        Assert.Equal(["a", "b", "c", "d"], Ids(Merge(List("a", "c"), List("a", "c"), List("a", "b", "c", "d"))));
    }

    [Fact]
    public void PositionsClampWhenTheListShrankSince()
    {
        // ListRemoval.restore on the Mac: "c" was deleted by someone else since, and stays deleted.
        Assert.Equal(["a", "b", "d"], Ids(Merge(List("a", "c"), List("a"), List("a", "b", "c", "d"))));
    }

    [Fact]
    public void OneAlreadyBackIsLeftAlone()
    {
        Assert.Equal(["a", "b", "c", "d"], Ids(Merge(List("a", "c"), List("a", "c", "d"), List("a", "b", "c", "d"))));
    }

    [Fact]
    public void ASlideSomeoneAddedSinceStays()
    {
        // The step deleted "b"; another window added "x" since.
        Assert.Equal(["a", "b", "x", "c"], Ids(Merge(List("a", "c"), List("a", "x", "c"), List("a", "b", "c"))));
    }

    [Fact]
    public void AnAddedSlideGoesUnlessSomeoneEditedItSince()
    {
        var added = """[{"id":"a"},{"id":"new","text":"hi"}]""";
        Assert.Equal(["a"], Ids(Merge(added, added, """[{"id":"a"}]""")));
        var edited = """[{"id":"a"},{"id":"new","text":"edited since"}]""";
        Assert.Equal(["a", "new"], Ids(Merge(added, edited, """[{"id":"a"}]""")));
    }

    [Fact]
    public void AnOrderPutBackRanksWhatItKnowsAndLeavesTheRestAfter()
    {
        // The Mac's listOrderAppliesTolerantly: the step moved "c" from the front to the end.
        Assert.Equal(
            ["c", "a", "b", "new1", "new2"],
            Ids(Merge(List("a", "b", "c"), List("a", "new1", "b", "c", "new2"), List("c", "a", "b"))));
        Assert.Equal(["c", "a", "b", "new1", "new2"], JsonMerge.ListOrder(["c", "gone", "a", "b"], ["a", "new1", "b", "c", "new2"]));
    }

    [Fact]
    public void SomeoneElsesReorderStandsAndASlideComingBackGoesToItsOldPlace()
    {
        // The step deleted "b" from a,b,c,d; since then someone put "d" first.
        Assert.Equal(["d", "b", "a", "c"], Ids(Merge(List("a", "c", "d"), List("d", "a", "c"), List("a", "b", "c", "d"))));
    }

    [Fact]
    public void ElementsMergeByIdWhereverTheyAre()
    {
        var basis = """[{"id":"a","text":"new"},{"id":"b","text":"b"}]""";
        var current = """[{"id":"b","text":"b2"},{"id":"a","text":"new"}]""";
        var wanted = """[{"id":"a","text":"old"},{"id":"b","text":"b"}]""";
        Assert.Equal("""[{"id":"b","text":"b2"},{"id":"a","text":"old"}]""", Merge(basis, current, wanted));
    }

    [Fact]
    public void AListWithoutIdsIsOneValue()
    {
        Assert.Equal("""{"ids":["v","c","c"]}""", Merge("""{"ids":["v","c"]}""", """{"ids":["v","c","c"]}""", """{"ids":["v"]}"""));
    }

    [Fact]
    public void WholeDocuments()
    {
        byte[] B(string s) => System.Text.Encoding.UTF8.GetBytes(s);
        Assert.Null(JsonMerge.Apply(B("{}"), B("{}"), null)); // undoing an add: it goes
        Assert.Equal("{}", System.Text.Encoding.UTF8.GetString(JsonMerge.Apply(null, null, B("{}"))!)); // undoing a delete: it's back
        Assert.Equal("""{"a":2}""", System.Text.Encoding.UTF8.GetString(JsonMerge.Apply(B("{}"), B("""{"a":2}"""), null)!)); // edited since: it stays
        Assert.Null(JsonMerge.Apply(B("""{"a":1}"""), null, B("""{"a":0}"""))); // deleted since: it stays gone
        Assert.True(JsonMerge.SameJson(B("""{ "a" : 1 }"""), B("""{"a":1}""")));
    }
}
