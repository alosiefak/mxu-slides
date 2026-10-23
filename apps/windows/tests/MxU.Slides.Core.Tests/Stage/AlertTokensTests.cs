using MxU.Slides.Core.Stage;

namespace MxU.Slides.Core.Tests.Stage;

// Port of PresenterCoreTests/AlertTokensTests.swift.
public class AlertTokensTests
{
    [Fact]
    public void NamesParseInOrderAndDedupe()
    {
        Assert.Equal(new[] { "plate", "lot" }, AlertTokens.TokenNames("Car {plate} in lot {lot}"));
        Assert.Equal(new[] { "Room" }, AlertTokens.TokenNames("{Room} … {room} again"));
        Assert.Empty(AlertTokens.TokenNames("No tokens here"));
        Assert.Empty(AlertTokens.TokenNames("Empty {} braces"));
    }

    [Fact]
    public void ComposeFillsInPlaceKeepingCopy() =>
        Assert.Equal(
            "Car with plate 7ABC123 — please move it",
            AlertTokens.Compose("Car with plate {plate} — please move it", new Dictionary<string, string> { ["plate"] = "7ABC123" }));

    [Fact]
    public void ComposeIsCaseInsensitiveAndRepeats() =>
        Assert.Equal(
            "042 to room B. Repeat: 042 to room B.",
            AlertTokens.Compose(
                "{Child} to room {room}. Repeat: {child} to room {ROOM}.",
                new Dictionary<string, string> { ["child"] = "042", ["Room"] = "B" }));

    [Fact]
    public void MissingValuesRemoveTheSlot() =>
        Assert.Equal("Hi !", AlertTokens.Compose("Hi {name}!", new Dictionary<string, string>()));

    [Fact]
    public void MalformedBracesPassThrough()
    {
        Assert.Equal("a X {c", AlertTokens.Compose("a { b } {c", new Dictionary<string, string> { ["b"] = "X", [" b "] = "X" }));
        Assert.Equal("{}", AlertTokens.Compose("{}", new Dictionary<string, string>()));
    }
}
