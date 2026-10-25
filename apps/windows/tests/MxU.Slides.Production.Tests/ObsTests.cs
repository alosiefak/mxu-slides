using System.Diagnostics;
using System.Net.Sockets;
using MxU.Slides.Production.Devices;
using MxU.Slides.Production.Obs;
using MxU.Slides.Production.Simulators;
using MxU.Slides.Production.Tests.Fakes;

namespace MxU.Slides.Production.Tests;

// obs-websocket v5: the sign-in sum, then requests against a pretend OBS built on .NET's own
// (server-side) WebSocket, so the hand-written client is checked against a real implementation.
public class ObsTests
{
    [Fact]
    public void TheSignInAnswerMatchesOneWorkedOutElsewhere() =>
        // Computed with Python's hashlib: base64(sha256(base64(sha256(password + salt)) + challenge)).
        Assert.Equal("1Ct943GAT+6YQUUX47Ia/ncufilbe6+oD6lY+5kaCu4=", ObsAuth.Response("supersecretpassword", ObsSimulator.Salt, ObsSimulator.Challenge));

    [Fact]
    public void TheWebSocketHandshakeAnswerIsRfc6455s() =>
        Assert.Equal("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", MiniWebSocket.AcceptFor("dGhlIHNhbXBsZSBub25jZQ=="));

    private static ObsClient Client(int port, string? password) => new(Wait.Device(DeviceKind.Obs, port), password)
    {
        FirstRetry = TimeSpan.FromMilliseconds(200),
    };

    [Fact]
    public async Task ItSignsInWithThePasswordAndSendsEachRequest()
    {
        using var obs = new ObsSimulator("hunter2");
        using var client = Client(obs.Port, "hunter2");
        client.Start();
        await Wait.Until(() => client.Status.IsOnline, what: "OBS to connect");
        await Wait.Until(() => client.Catalog.Scenes.Count == 3, what: "the scene list");
        Assert.Equal(["Announcements", "Sermon", "Worship"], client.Catalog.Scenes);
        Assert.Equal(["Fade", "Cut"], client.Catalog.Transitions);
        Assert.Equal(["Lower third"], client.Catalog.SceneSources["Sermon"]);
        Assert.True(client.StudioMode);

        while (obs.Requests.TryDequeue(out _)) { }
        Assert.True(await client.SetProgramScene("Sermon", "Fade", 750));
        Assert.Equal(["SetCurrentSceneTransition", "SetCurrentSceneTransitionDuration", "SetCurrentProgramScene"], obs.Requests.Select(r => r.Type));
        Assert.Equal("Fade", obs.Requests.First().Data["transitionName"]!.GetValue<string>());
        Assert.Equal(750, obs.Requests.ElementAt(1).Data["transitionDuration"]!.GetValue<int>());
        Assert.Equal("Sermon", obs.Requests.Last().Data["sceneName"]!.GetValue<string>());
        // OBS's event says what's on program now.
        await Wait.Until(() => client.ProgramScene == "Sermon", what: "the program scene event");
        Assert.Contains("Program: Sermon", client.Status.Detail);

        Assert.True(await client.SetPreviewScene("Worship"));
        Assert.True(await client.Transition());
        Assert.True(await client.SetStreaming(true));
        Assert.True(await client.SetRecording(false));
        Assert.True(await client.SetSourceVisible("Sermon", "Lower third", false));
        var types = obs.Requests.Select(r => r.Type).Skip(3).ToList();
        Assert.Equal(["SetCurrentPreviewScene", "TriggerStudioModeTransition", "StartStream", "StopRecord", "GetSceneItemId", "SetSceneItemEnabled"], types);
        var shown = obs.Requests.Last().Data;
        Assert.Equal((7, false), (shown["sceneItemId"]!.GetValue<int>(), shown["sceneItemEnabled"]!.GetValue<bool>()));
    }

    [Fact]
    public async Task ARequestOBSRefusesReportsFailure()
    {
        using var obs = new ObsSimulator();
        obs.Failing.Add("SetCurrentProgramScene");
        using var client = Client(obs.Port, null);
        client.Start();
        await Wait.Until(() => client.Status.IsOnline);
        Assert.False(await client.SetProgramScene("Nowhere"));
    }

    [Fact]
    public async Task AWrongPasswordTurnsTheLightRed()
    {
        using var obs = new ObsSimulator("right");
        using var client = Client(obs.Port, "wrong");
        client.Start();
        await Wait.Until(() => client.Status.Detail.Contains("refused the password"), what: "the refusal");
        Assert.Equal(DeviceState.Problem, client.Status.State);
        Assert.Equal(0, obs.Identified);
    }

    [Fact]
    public async Task NoPasswordWhenOBSWantsOneSaysSo()
    {
        using var obs = new ObsSimulator("right");
        using var client = Client(obs.Port, null);
        client.Start();
        await Wait.Until(() => client.Status.Detail.Contains("asks for a password"));
    }

    [Fact]
    public async Task OBSNotRunningNeverHoldsACommandUp()
    {
        using var client = Client(Wait.UnusedPort(SocketType.Stream), null);
        client.Start();
        await Wait.Until(() => client.Status.State == DeviceState.Problem, what: "the light to go red");
        var watch = Stopwatch.StartNew();
        Assert.False(await client.SetProgramScene("Worship"));
        Assert.True(watch.ElapsedMilliseconds < 500, $"took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task WhenOBSComesBackItReconnects()
    {
        var port = Wait.UnusedPort(SocketType.Stream);
        using var client = Client(port, null);
        client.Start();
        await Wait.Until(() => client.Status.State == DeviceState.Problem);
        using var obs = new ObsSimulator(port: port);
        await Wait.Until(() => client.Status.IsOnline, 8000, "OBS to reconnect");
        Assert.True(await client.SetStreaming(false));
    }
}
