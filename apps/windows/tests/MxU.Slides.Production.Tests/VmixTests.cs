using System.Diagnostics;
using System.Net.Sockets;
using MxU.Slides.Production.Devices;
using MxU.Slides.Production.Simulators;
using MxU.Slides.Production.Tests.Fakes;
using MxU.Slides.Production.Vmix;

namespace MxU.Slides.Production.Tests;

// vMix's web API: each command is one GET whose URL says it all.
public class VmixTests
{
    [Fact]
    public void EachFunctionIsAQueryWithItsSettings()
    {
        Assert.Equal("/api/?Function=Cut", VmixClient.FunctionPath("Cut"));
        Assert.Equal("/api/?Function=Cut&Input=3", VmixClient.FunctionPath("Cut", ("Input", "3")));
        Assert.Equal("/api/?Function=Fade&Duration=1000", VmixClient.FunctionPath("Fade", ("Input", null), ("Duration", "1000")));
        Assert.Equal("/api/?Function=OverlayInput1In&Input=Camera%202", VmixClient.FunctionPath("OverlayInput1In", ("Input", "Camera 2")));
        Assert.Equal("/api/?Function=ActiveInput&Input=Lower%20third%20%26%20logo", VmixClient.FunctionPath("ActiveInput", ("Input", " Lower third & logo ")));
    }

    [Fact]
    public void TheStateXmlGivesTheInputsAndASummary()
    {
        var state = VmixClient.ReadState(VmixSimulator.StateXml);
        Assert.Equal([new CatalogEntry(1, "Camera 1"), new CatalogEntry(2, "Camera 2"), new CatalogEntry(3, "Lower third")], state.Inputs);
        Assert.Equal("vMix 27.0.0.49 · Output: Camera 1 · streaming", state.Summary);
    }

    private static VmixClient Client(int port, string? user = null, string? password = null)
    {
        var settings = Wait.Device(DeviceKind.Vmix, port);
        settings.User = user;
        return new VmixClient(settings, password) { PollEvery = TimeSpan.FromMilliseconds(200) };
    }

    [Fact]
    public async Task ItSignsInAndEachCommandIsTheRightRequest()
    {
        using var vmix = new VmixSimulator("operator", "secret");
        using var client = Client(vmix.Port, "operator", "secret");
        client.Start();
        await Wait.Until(() => client.Status.IsOnline, what: "vMix to answer");
        Assert.Equal(["Camera 1", "Camera 2", "Lower third"], client.Catalog.Inputs.Select(i => i.Name));

        Assert.True(await client.Cut());
        Assert.True(await client.Fade(null, 1000));
        Assert.True(await client.Overlay(2, "3", on: true));
        Assert.True(await client.Overlay(2, null, on: false));
        Assert.True(await client.Streaming(true));
        Assert.True(await client.Recording(false));
        Assert.True(await client.Preview("2"));
        Assert.True(await client.Active("Camera 1"));
        Assert.Equal(
        [
            "/api/?Function=Cut", "/api/?Function=Fade&Duration=1000", "/api/?Function=OverlayInput2In&Input=3",
            "/api/?Function=OverlayInput2Out", "/api/?Function=StartStreaming", "/api/?Function=StopRecording",
            "/api/?Function=PreviewInput&Input=2", "/api/?Function=ActiveInput&Input=Camera%201",
        ], vmix.Functions);
    }

    [Fact]
    public async Task AWrongPasswordSaysSo()
    {
        using var vmix = new VmixSimulator("operator", "secret");
        using var client = Client(vmix.Port, "operator", "nope");
        client.Start();
        await Wait.Until(() => client.Status.Detail.Contains("user name and password"));
        Assert.Empty(vmix.Functions);
    }

    [Fact]
    public async Task VmixNotRunningNeverHoldsACommandUp()
    {
        using var client = Client(Wait.UnusedPort(SocketType.Stream));
        client.Start();
        await Wait.Until(() => client.Status.State == DeviceState.Problem, what: "the light to go red");
        var watch = Stopwatch.StartNew();
        Assert.False(await client.Cut());
        Assert.True(watch.ElapsedMilliseconds < 500, $"took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task ASlowVmixGivesUpAtTheTimeLimitAndSkipsTheNextUntilItAnswers()
    {
        using var vmix = new VmixSimulator();
        using var client = Client(vmix.Port);
        client.RequestTimeout = TimeSpan.FromSeconds(1);
        client.Start();
        await Wait.Until(() => client.Status.IsOnline);
        vmix.DelayMilliseconds = 6000;
        var watch = Stopwatch.StartNew();
        Assert.False(await client.Cut());
        Assert.True(watch.ElapsedMilliseconds < 4000, $"took {watch.ElapsedMilliseconds} ms");   // its own 1 s limit, not vMix's 6 s
        // The next one doesn't wait out a time limit of its own: it's skipped, never sent, until vMix answers a poll.
        watch.Restart();
        Assert.False(await client.Fade());
        Assert.DoesNotContain(vmix.Functions, f => f.Contains("Fade", StringComparison.Ordinal));
        Assert.True(watch.ElapsedMilliseconds < 1000, $"the next took {watch.ElapsedMilliseconds} ms");
        Assert.Equal(DeviceState.Problem, client.Status.State);
        vmix.DelayMilliseconds = 0;
        await Wait.Until(() => client.Status.IsOnline, 10000, "vMix to answer a poll again");
        Assert.True(await client.Cut());
    }
}
