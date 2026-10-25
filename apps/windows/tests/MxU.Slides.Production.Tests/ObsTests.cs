using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using MxU.Slides.Production.Devices;
using MxU.Slides.Production.Obs;
using MxU.Slides.Production.Simulators;
using MxU.Slides.Production.Tests.Fakes;

namespace MxU.Slides.Production.Tests;

// obs-websocket v5: the sign-in sum, the WebSocket client against .NET's own server, then
// requests against a pretend OBS (the simulator, its server half written separately).
public class ObsTests
{
    [Fact]
    public void TheSignInAnswerMatchesOneWorkedOutElsewhere() =>
        // Computed with Python's hashlib: base64(sha256(base64(sha256(password + salt)) + challenge)).
        Assert.Equal("1Ct943GAT+6YQUUX47Ia/ncufilbe6+oD6lY+5kaCu4=", ObsAuth.Response("supersecretpassword", ObsSimulator.Salt, ObsSimulator.Challenge));

    [Fact]
    public void TheWebSocketHandshakeAnswerIsRfc6455s() =>
        Assert.Equal("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", MiniWebSocket.AcceptFor("dGhlIHNhbXBsZSBub25jZQ=="));

    [Fact]
    public async Task TheWebSocketClientSpeaksToDotNetsOwnServer()
    {
        var token = TestContext.Current.CancellationToken;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync(token);
                var stream = client.GetStream();
                var request = new StringBuilder();
                var one = new byte[1];
                while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && await stream.ReadAsync(one, token) > 0)
                    request.Append((char)one[0]);
                var key = request.ToString().Split("\r\n").First(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.Ordinal))[18..].Trim();
                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {MiniWebSocket.AcceptFor(key)}\r\n\r\n"), token);
                using var ws = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.Zero });
                var buffer = new byte[4096];
                var received = await ws.ReceiveAsync(buffer, token);
                // An answer in two fragments, then one too long for the two-byte length, then a close with a code.
                await ws.SendAsync(Encoding.UTF8.GetBytes("echo:"), WebSocketMessageType.Text, false, token);
                await ws.SendAsync(Encoding.UTF8.GetBytes(received.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)), WebSocketMessageType.Text, true, token);
                await ws.SendAsync(Encoding.UTF8.GetBytes(new string('x', 70_000)), WebSocketMessageType.Text, true, token);
                await ws.CloseOutputAsync((WebSocketCloseStatus)4009, "bye", token);
            }, token);
            using var socket = MiniWebSocket.Connect("127.0.0.1", port, TimeSpan.FromSeconds(3));
            socket.SendText(new string('a', 300));    // 300 bytes: the 126 + two-byte length form, masked
            Assert.Equal("echo:300", socket.ReceiveText());
            Assert.Equal(70_000, socket.ReceiveText()!.Length);
            Assert.Null(socket.ReceiveText());
            Assert.Equal(4009, socket.CloseCode);
            await server;
        }
        finally
        {
            listener.Stop();
        }
    }

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
    public async Task AnOBSThatStopsAnsweringCostsOneCommandThenTheRestAreSkipped()
    {
        using var obs = new ObsSimulator();
        using var client = Client(obs.Port, null);
        client.RequestTimeout = TimeSpan.FromSeconds(1);
        client.FirstRetry = TimeSpan.FromSeconds(2);
        client.Start();
        await Wait.Until(() => client.Status.IsOnline);
        await Wait.Until(() => client.Catalog.Scenes.Count == 3, what: "the scene list");

        obs.Hang = true;   // OBS froze: it takes the request and never answers
        var watch = Stopwatch.StartNew();
        Assert.False(await client.SetProgramScene("Worship"));
        Assert.True(watch.ElapsedMilliseconds < 4000, $"the first took {watch.ElapsedMilliseconds} ms");
        // The connection was dropped, so the next one doesn't wait out a time limit of its own: it's never sent.
        watch.Restart();
        Assert.False(await client.SetStreaming(true));
        Assert.DoesNotContain(obs.Requests, r => r.Type == "StartStream");
        Assert.True(watch.ElapsedMilliseconds < 1000, $"the second took {watch.ElapsedMilliseconds} ms");
        Assert.Equal(DeviceState.Problem, client.Status.State);
        Assert.Contains("stopped answering", client.Status.Detail);

        obs.Hang = false;
        while (obs.Requests.TryDequeue(out _)) { }
        // Signed in again, and OBS answered the whole scene read (its last request is only sent once the others are answered).
        await Wait.Until(() => client.Status.IsOnline && obs.Requests.Any(r => r.Type == "GetRecordStatus"), 15000, "OBS to answer again");
        Assert.True(await client.SetStreaming(true));
    }

    [Fact]
    public async Task WhenOBSQuitsTheLightGoesRedAndCommandsAreSkippedAtOnce()
    {
        var obs = new ObsSimulator();
        using var client = Client(obs.Port, null);
        client.Start();
        await Wait.Until(() => client.Status.IsOnline);
        obs.Dispose();   // OBS quits in the middle of the service
        await Wait.Until(() => client.Status.State == DeviceState.Problem, what: "the light to go red");
        var watch = Stopwatch.StartNew();
        Assert.False(await client.SetStreaming(true));
        Assert.True(watch.ElapsedMilliseconds < 1000, $"took {watch.ElapsedMilliseconds} ms");   // skipped, not a time limit
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
