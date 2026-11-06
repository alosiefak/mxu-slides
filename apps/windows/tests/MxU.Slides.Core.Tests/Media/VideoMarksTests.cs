using MxU.Slides.Core.Json;
using MxU.Slides.Core.Media;
using MxU.Slides.Core.Model;
using V = MxU.Slides.Core.Media.VideoMarks;

namespace MxU.Slides.Core.Tests.Media;

// Port of the Mac's PresenterCoreTests/VideoMarksTests.swift.
public class VideoMarksTests
{
    private static VideoMark Mark(string id, string name, double at, string? color = null) =>
        new() { Id = id, Name = name, At = at, Actions = [], ColorHex = color };

    private static readonly VideoMark[] Marks =
        [Mark("c", "Band walk-on", 40), Mark("a", "Show QR", 5), Mark("b", "Hide QR", 20), Mark("d", "Show QR", 50)];

    private static readonly List<V.Point> Points = V.Points(Marks, null, 60);

    private static string[] Ids(IEnumerable<V.Point> points) => points.Select(p => p.Id).ToArray();

    [Fact]
    public void V160MediaItemDecodesWithNoMarks()
    {
        const string json = """
            {"id":"m","name":"Clip","mediaKind":"video","classification":"foreground","fileHash":"h","fileName":"c.mov",
            "fileStatus":"ready","statusDetail":"","tags":[],"favorite":false,"collections":[],"loops":false}
            """;
        Assert.Null(MxuJson.Deserialize<MediaItem>(json).Marks);
    }

    [Fact]
    public void PointsAreWindowSecondsInTimeOrderAndDropMarksOutsideTheTrim()
    {
        Assert.Equal(["a", "b", "c", "d"], Ids(Points));
        var red = V.Points([Mark("r", "Red", 3, "#FF3B30")], null, 60);
        Assert.Equal("#FF3B30", red[0].ColorHex);
        var trimmed = V.Points(Marks, 10, 35);
        Assert.Equal(["b", "c"], Ids(trimmed));
        Assert.Equal([10.0, 30.0], trimmed.Select(p => p.At));
    }

    [Fact]
    public void PlayingAcrossAMarkFiresIt()
    {
        Assert.Equal(["a"], Ids(V.Crossed(Points, 4.9, 5.1, 0.2, 1, 60, false)));
        Assert.Equal(["a"], Ids(V.Crossed(Points, 4.8, 5, 0.2, 1, 60, false)));
        Assert.Empty(V.Crossed(Points, 5, 5.2, 0.2, 1, 60, false));
    }

    [Fact]
    public void AJumpFiresNothing()
    {
        Assert.Empty(V.Crossed(Points, 10, 45, 0.25, 1, 60, false));
        Assert.Empty(V.Crossed(Points, 45, 30, 0.25, 1, 60, false));
    }

    [Fact]
    public void PlayRateWidensWhatTimeCanExplain()
    {
        Assert.Equal(["b"], Ids(V.Crossed(Points, 19.5, 20.4, 0.45, 2, 60, false)));
        Assert.Empty(V.Crossed(Points, 19.5, 20.4, 0.45, 1, 60, false));
    }

    [Fact]
    public void AFreshStartFiresFromTheTopButAMidVideoStartSkips()
    {
        var top = V.Points([Mark("z", "Top", 0), .. Marks], null, 60);
        Assert.Equal(["z"], Ids(V.Crossed(top, null, 0.3, 0, 1, 60, false)));
        Assert.Empty(V.Crossed(top, null, 30, 0, 1, 60, false));
        Assert.Equal(["z"], Ids(V.Crossed(top, 60, 0.2, 0.25, 1, 60, false)));
    }

    [Fact]
    public void ALoopFiresItsMarksEveryPass()
    {
        Assert.Equal(["a"], Ids(V.Crossed(Points, 59.9, 5.05, 5.2, 1, 60, true)));
        Assert.Empty(V.Crossed(Points, 59.9, 5.05, 5.2, 1, 60, false));
    }

    [Fact]
    public void TargetsPickTheNextOrTheNamedMarkElseTheEnd()
    {
        Assert.Equal("b", V.Target(Points, 6, VideoCountdownTarget.NextMark, null)?.Id);
        Assert.Equal("d", V.Target(Points, 6, VideoCountdownTarget.NamedMark, "show qr")?.Id);
        Assert.Null(V.Target(Points, 50, VideoCountdownTarget.NextMark, null));
        Assert.Null(V.Target(Points, 0, VideoCountdownTarget.End, null));
    }

    [Fact]
    public void CountUpStartsFromThePreviousMarkOfTheSameRule()
    {
        Assert.Equal(20, V.CountUpStart(Points, 30, VideoCountdownTarget.NextMark, null));
        Assert.Equal(5, V.CountUpStart(Points, 30, VideoCountdownTarget.NamedMark, "Show QR"));
        Assert.Equal(0, V.CountUpStart(Points, 30, VideoCountdownTarget.End, null));
        Assert.Equal(0, V.CountUpStart(Points, 2, VideoCountdownTarget.NextMark, null));
    }

    [Fact]
    public void UntilNextWrapsOnlyWhenLooping()
    {
        Assert.Equal(10, V.UntilNext(Points, 10, 60, false));
        Assert.Null(V.UntilNext(Points, 55, 60, false));
        Assert.Equal(10, V.UntilNext(Points, 55, 60, true));
    }

    [Fact]
    public void EditsKeepTimeOrderAndStayInsideTheFile()
    {
        Assert.Equal("Mark 1", V.DefaultName((IReadOnlyList<VideoMark>?)null));
        Assert.Equal("Mark 3", V.DefaultName([Mark("x", "Mark 2", 1)]));
        var added = V.Adding(Marks, "e", "Mark 5", 12.34567, [], 60);
        Assert.Equal(["a", "e", "b", "c", "d"], added.Select(m => m.Id));
        Assert.Equal(12.346, added[1].At);
        Assert.Equal(60, V.Adding(null, "f", "End", 99, [], 60)[0].At);
        Assert.Equal(["b", "c", "a", "d"], V.Moving(Marks, "a", 45, 60).Select(m => m.Id));
        Assert.Equal(0, V.Moving(Marks, "a", -3, 60).Single(m => m.Id == "a").At);
    }

    [Fact]
    public void MarksAtTheSameMomentAllFireInTheListsOrder()
    {
        var same = V.Adding(V.Adding(null, "y", "Lights", 30, [], 60), "x", "Alert", 30, [], 60);
        var points = V.Points(same, null, 60);
        Assert.Equal(same.Select(m => m.Id), Ids(V.Crossed(points, 29.9, 30.1, 0.2, 1, 60, false)));
        Assert.Equal(["x", "y"], same.Select(m => m.Id));
    }

    [Fact]
    public void ActionMarksAreNamedAfterTheirTarget()
    {
        Assert.Equal("Band on Stage", V.DefaultName("Fire Alert: Band on Stage"));
        Assert.Equal("Clear All", V.DefaultName("Clear All"));
        Assert.Equal("Fire Overlay: ", V.DefaultName("Fire Overlay: "));
    }
}
