using System.Text.Json;
using System.Text.Json.Nodes;

namespace MxU.Slides.LocalApi.Tests;

// Port of WireModelTests.swift.

public sealed class ShowStatusWireTests
{
    [Fact]
    public void ShowStatusCarriesServiceNextSlideAndPosition()
    {
        var status = new ApiShowStatus(
            new ApiLiveSlide("p1", "Way Maker", "s1", 2, null, "i1", 2, Text: "Way maker", SlideCount: 9),
            "Miracle worker", new Dictionary<string, ApiLayerContent>(), [], null, "svc",
            Service: new ApiServiceRef("svc", "Sunday"),
            NextSlide: new ApiNextSlide("Miracle worker"),
            Position: new ApiServicePosition("Way Maker", "Welcome"));

        var json = JsonNode.Parse(ApiJson.Encode(status))!;
        var live = json["liveSlide"]!;
        Assert.Equal("Way maker", (string?)live["text"]);
        Assert.Equal(9, (int?)live["slideCount"]);
        Assert.Equal("Sunday", (string?)json["service"]?["name"]);
        Assert.Equal("Welcome", (string?)json["position"]?["nextItemName"]);
        Assert.Equal("Miracle worker", (string?)json["nextSlide"]?["text"]);
    }

    [Fact]
    public void TimersDocumentEncodesAnchorsAsIsoDates()
    {
        var document = new ApiTimersDocument(
            [new ApiTimerStatus(
                "t1", "Sermon", "countdown", Running: true, DisplaySeconds: 1799, Overrun: false, FolderId: null, FolderName: null,
                RunningSince: DateTimeOffset.UnixEpoch, BankedSeconds: 1, DurationSeconds: 1800,
                Warnings: [new ApiTimerWarning(30, "#FFAA00")])],
            [new ApiVideoCountdown("videos", "Bumper", 60, 12, DateTimeOffset.UnixEpoch, IsPlaying: true, ServiceItemId: "i2")]);

        var encoded = ApiJson.Encode(document);
        var json = JsonNode.Parse(encoded)!;
        var timer = json["timers"]![0]!;
        Assert.Equal("1970-01-01T00:00:00Z", (string?)timer["runningSince"]);
        Assert.Equal("#FFAA00", (string?)timer["warnings"]![0]!["colorHex"]);
        var countdown = json["videoCountdowns"]![0]!;
        Assert.Equal("videos", (string?)countdown["layer"]);
        Assert.True((bool?)countdown["isPlaying"]);
        Assert.Equal("i2", (string?)countdown["serviceItemId"]);
        Assert.Equal(DateTimeOffset.UnixEpoch, JsonSerializer.Deserialize<ApiTimersDocument>(encoded, ApiJson.Options)!.Timers[0].RunningSince);
    }

    // Windows-only: the encoder's conventions, which every snapshot relies on.

    [Fact]
    public void KeysAreSortedAndNullOptionalsLeftOut()
    {
        var status = new ApiShowStatus(null, null, new Dictionary<string, ApiLayerContent>(), [], null, null);
        Assert.Equal("""{"mediaLayers":{},"overlays":[]}""", System.Text.Encoding.UTF8.GetString(ApiJson.Encode(status)));

        var timecode = new ApiTimecodeStatus(HasInput: false, Source: "ltc", Follows: false, State: "noInput");
        Assert.Equal(
            """{"follows":false,"hasInput":false,"offsetFrames":0,"reverse":false,"source":"ltc","state":"noInput"}""",
            System.Text.Encoding.UTF8.GetString(ApiJson.Encode(timecode)));
    }

    [Fact]
    public void DatesEncodeInUtcToTheSecond()
    {
        var at = new DateTimeOffset(2026, 10, 23, 9, 30, 15, 750, TimeSpan.FromHours(-5));
        var json = JsonNode.Parse(ApiJson.Encode(new ApiSchedulerUpcoming("t", "Doors", at)))!;
        Assert.Equal("2026-10-23T14:30:15Z", (string?)json["at"]);
    }
}

public sealed class ServiceSnapshotWireTests
{
    [Fact]
    public void SnapshotEncodesItemsSlidesAndChecksums()
    {
        var snapshot = new ApiServiceSnapshot(new ApiServiceRef("svc", "Sunday"),
        [
            new ApiServiceSnapshot.Item("i1", "Way Maker", "presentation", null,
                Presentation: new ApiServiceSnapshot.PresentationRef("p1", "Way Maker", null, null,
                [
                    new ApiServiceSnapshot.SlideRef("s1", 0, null, "Verse 1", "#336699", "Way maker", "abc==",
                        new ApiServiceSnapshot.Size(1920, 1080)),
                ])),
            new ApiServiceSnapshot.Item("i2", "Bumper", "media", null,
                Media: new ApiServiceSnapshot.MediaRef("m1", "Bumper", 90, null)),
        ]);

        var json = JsonNode.Parse(ApiJson.Encode(snapshot))!;
        var items = json["items"]!;
        var slide = items[0]!["presentation"]!["slides"]![0]!;
        Assert.Equal("abc==", (string?)slide["thumbnailChecksum"]);
        Assert.Equal("Verse 1", (string?)slide["group"]);
        Assert.Equal(90, (int?)items[1]!["media"]!["durationSeconds"]);
        Assert.Equal("Sunday", (string?)json["service"]?["name"]);
    }
}
