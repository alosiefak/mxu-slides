using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using MxU.Slides.LocalApi.Remote;

namespace MxU.Slides.LocalApi.Tests;

/// <summary>The app as the browser remote sees it: a bridge that also offers <see cref="IRemoteBridge"/>.</summary>
internal sealed class FakeRemoteBridge : CoreOnlyBridge, IRemoteBridge
{
    public static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0x49, 0x48, 0x44, 0x52];

    public List<string> Calls { get; } = [];

    public RemoteServiceDocument RemoteService() => new(
        new ApiServiceSnapshot(new ApiServiceRef("svc-1", "Sunday Morning"),
        [
            new ApiServiceSnapshot.Item("row-1", "Worship", "header", null),
            new ApiServiceSnapshot.Item("row-2", "Morning Light", "presentation", "#3B82F6FF", new ApiServiceSnapshot.PresentationRef(
                "song-1", "Morning Light", null, null,
                [new ApiServiceSnapshot.SlideRef("slide-1", 0, null, "Verse 1", "#3B82F6FF", "Here in the morning light", "abc", new(1920, 1080))])),
        ]),
        null);

    public Task<byte[]?> SlideThumbnailAsync(string presentationId, string slideId, int width)
    {
        Calls.Add($"thumbnail:{presentationId}:{slideId}:{width}");
        return Task.FromResult(presentationId == "song-1" && slideId == "slide-1" ? Png : null);
    }

    public IReadOnlyList<RemoteAlert> RemoteAlerts() => [new("alert-1", "Car park", "Car {plate}, your lights are on", ["plate"])];

    public void SetStageMessage(string? text) => Calls.Add($"stage:{text}");
}

public sealed class RemoteTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<(int Status, JsonNode? Body)> PairAsync(TestServer host, string code, string name = "Test phone")
    {
        var response = await host.Http.PostAsync(host.Url("/remote/pair"), JsonBody($$"""{"code":"{{code}}","name":"{{name}}"}"""), Ct);
        return ((int)response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct)));
    }

    [Fact]
    public async Task ThePagesServeWithoutAKeyAndUseOnlyTheirOwnFiles()
    {
        using var host = TestServer.Start(new FakeRemoteBridge());
        foreach (var path in new[] { "/remote", "/remote/", "/remote/stage" })
        {
            var page = await host.Http.GetAsync(host.Url(path), Ct);
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
            var csp = page.Headers.GetValues("Content-Security-Policy").Single();
            Assert.Contains("script-src 'self'", csp);
            Assert.Contains("frame-ancestors 'none'", csp);
            var html = await page.Content.ReadAsStringAsync(Ct);
            Assert.Contains("/remote/assets/connect.js", html);
            // Nothing from the internet: a church network with no outside access still works.
            Assert.DoesNotContain("src=\"http", html);
            Assert.DoesNotContain("href=\"http", html);
        }
        foreach (var asset in new[] { "remote.js", "stage.js", "connect.js", "remote.css", "stage.css", "icon.svg" })
        {
            var response = await host.Http.GetAsync(host.Url("/remote/assets/" + asset), Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain("://cdn", await response.Content.ReadAsStringAsync(Ct));
        }
        Assert.Equal(HttpStatusCode.NotFound, (await host.Http.GetAsync(host.Url("/remote/assets/..%2Fdocs"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Http.GetAsync(host.Url("/remote/assets/MxU.Slides.LocalApi.document-schemas.json"), Ct)).StatusCode);
    }

    [Fact]
    public async Task TheKeyNeverTravelsInAnAddress()
    {
        // The pages keep it in the browser's storage and send it in a header or the socket's first message.
        var js = Encoding.UTF8.GetString(RemoteSite.FileBytes("connect.js"))
            + Encoding.UTF8.GetString(RemoteSite.FileBytes("remote.js"))
            + Encoding.UTF8.GetString(RemoteSite.FileBytes("stage.js"));
        Assert.DoesNotContain("token=", js);
        Assert.DoesNotContain("?key", js);
        Assert.Contains("Authorization", js);
        Assert.Contains("type: 'auth'", js);

        // And a key put in an address anyway is refused by the remote's own routes.
        using var host = TestServer.Start(new FakeRemoteBridge());
        var key = host.Mint(ApiScope.Control);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Http.GetAsync(host.Url($"/remote/session?token={key}"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Http.GetAsync(host.Url($"/remote/service?token={key}"), Ct)).StatusCode);
    }

    [Fact]
    public async Task PairingTradesTheCodeForAKeyWithTheChosenScope()
    {
        using var host = TestServer.Start(new FakeRemoteBridge());
        ApiToken? paired = null;
        host.Server.Pairing.Paired += token => paired = token;
        var offer = host.Server.Pairing.Begin(ApiScope.View);
        Assert.Matches("^[0-9]{6}$", offer.Code);

        var (status, body) = await PairAsync(host, offer.Code[..3] + " " + offer.Code[3..]); // typed with a space
        Assert.Equal(200, status);
        var key = (string)body!["key"]!;
        Assert.Equal("view", (string?)body["scope"]);
        Assert.Equal("Browser remote: Test phone", (string?)body["name"]);
        var token = host.Tokens.Verify(key)!;
        Assert.Equal(ApiScope.View, token.Scope);
        Assert.Equal(token.Id, paired?.Id);
        Assert.Contains(host.Tokens.All, t => t.Name == "Browser remote: Test phone"); // listed in Settings, revocable

        // The code is used up.
        Assert.Equal(404, (await PairAsync(host, offer.Code)).Status);
        Assert.Null(host.Server.Pairing.Current);

        var session = await host.SendAsync(HttpMethod.Get, "/remote/session", key);
        Assert.Equal("""{"name":"Browser remote: Test phone","scope":"view"}""", await session.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task FiveWrongCodesBurnTheCode()
    {
        using var host = TestServer.Start(new FakeRemoteBridge());
        var offer = host.Server.Pairing.Begin(ApiScope.Control);
        var wrong = offer.Code == "000000" ? "111111" : "000000";
        for (var left = 4; left >= 1; left--)
        {
            var (status, body) = await PairAsync(host, wrong);
            Assert.Equal(401, status);
            Assert.Equal(left, (int?)body!["triesLeft"]);
        }
        Assert.Equal(429, (await PairAsync(host, wrong)).Status);
        Assert.Equal(404, (await PairAsync(host, offer.Code)).Status);
        Assert.DoesNotContain(host.Tokens.All, t => t.Name.StartsWith("Browser remote", StringComparison.Ordinal));
    }

    [Fact]
    public void ACodeRunsOutAfterFiveMinutes()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero));
        var pairing = new RemotePairing { Clock = clock };
        var offer = pairing.Begin(ApiScope.Control);
        clock.Now += TimeSpan.FromMinutes(4.9);
        Assert.Equal(offer, pairing.Current);
        clock.Now += TimeSpan.FromMinutes(0.1);
        Assert.Null(pairing.Current);
        Assert.Equal(RemotePairing.Outcome.NoCode, pairing.Redeem(offer.Code, "x", TestServer.NewTokens()).Outcome);
    }

    [Fact]
    public async Task PairingOnlyAcceptsJsonAndOffersNoEditKeys()
    {
        using var host = TestServer.Start(new FakeRemoteBridge());
        var offer = host.Server.Pairing.Begin(ApiScope.Edit);
        Assert.Equal(ApiScope.Control, offer.Scope);
        var form = await host.Http.PostAsync(host.Url("/remote/pair"), new StringContent($"code={offer.Code}", Encoding.UTF8, "text/plain"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, form.StatusCode);
        Assert.NotNull(host.Server.Pairing.Current); // a refused request doesn't use a try
    }

    [Fact]
    public async Task AViewKeyCanReadButNotRunTheShow()
    {
        var bridge = new FakeRemoteBridge();
        using var host = TestServer.Start(bridge);
        var view = host.Mint(ApiScope.View);
        var control = host.Mint(ApiScope.Control);

        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Get, "/remote/service", view)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Get, "/remote/alerts", view)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendAsync(HttpMethod.Post, "/remote/stage-message", view, """{"text":"Wrap up"}""")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendAsync(HttpMethod.Post, "/v1/show/advance", view, """{"steps":1}""")).StatusCode);
        Assert.Empty(bridge.Calls);

        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Post, "/remote/stage-message", control, """{"text":" Wrap up "}""")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Post, "/v1/show/advance", control, """{"steps":1}""")).StatusCode);
        Assert.Equal(["stage:Wrap up"], bridge.Calls);

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendAsync(HttpMethod.Get, "/remote/service")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendAsync(HttpMethod.Get, "/remote/service", "mxu_not-a-key")).StatusCode);
    }

    [Fact]
    public async Task TheServiceAndAlertsComeInTheirWireShapes()
    {
        using var host = TestServer.Start(new FakeRemoteBridge());
        var key = host.Mint(ApiScope.View);
        var service = JsonNode.Parse(await (await host.SendAsync(HttpMethod.Get, "/remote/service", key)).Content.ReadAsStringAsync(Ct))!;
        Assert.Equal("Sunday Morning", (string?)service["service"]!["service"]!["name"]);
        Assert.Equal("Here in the morning light", (string?)service["service"]!["items"]![1]!["presentation"]!["slides"]![0]!["text"]);
        var alerts = JsonNode.Parse(await (await host.SendAsync(HttpMethod.Get, "/remote/alerts", key)).Content.ReadAsStringAsync(Ct))!;
        Assert.Equal("plate", (string?)alerts[0]!["blanks"]![0]);
    }

    [Fact]
    public async Task TheThumbnailRouteReturnsAPng()
    {
        var bridge = new FakeRemoteBridge();
        using var host = TestServer.Start(bridge);
        var key = host.Mint(ApiScope.View);
        var response = await host.SendAsync(HttpMethod.Get, "/remote/thumbnails/song-1/slide-1?width=200&v=abc", key);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(FakeRemoteBridge.Png, await response.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal("thumbnail:song-1:slide-1:200", bridge.Calls.Single());

        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Get, "/remote/thumbnails/song-1/nope", key)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Get, "/remote/thumbnails/song-1/..%2F..", key)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendAsync(HttpMethod.Get, "/remote/thumbnails/song-1/slide-1")).StatusCode);
        // An absurd size is clamped, so a request can't make the app draw a huge picture.
        await host.SendAsync(HttpMethod.Get, "/remote/thumbnails/song-1/slide-1?width=99999", key);
        Assert.Equal("thumbnail:song-1:slide-1:960", bridge.Calls.Last());
    }

    [Fact]
    public async Task RemoteAnswersCarryNoCorsHeaders()
    {
        using var host = TestServer.Start(new FakeRemoteBridge());
        var key = host.Mint(ApiScope.View);
        var response = await host.SendAsync(HttpMethod.Get, "/remote/service", key);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        var preflight = await host.SendAsync(HttpMethod.Options, "/remote/pair");
        Assert.False(preflight.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task TheRemoteSocketTakesItsKeyInTheFirstMessageAndPushesEvents()
    {
        using var host = TestServer.Start(new FakeRemoteBridge());
        var key = host.Mint(ApiScope.View);
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{host.Port}/remote/ws"), Ct);
        await TestServer.SendTextAsync(socket, $$"""{"type":"auth","token":"{{key}}"}""");
        var hello = JsonNode.Parse((await TestServer.ReceiveAsync(socket))!)!;
        Assert.Equal(("hello", "view"), ((string?)hello["type"], (string?)hello["scope"]));

        await TestServer.SendTextAsync(socket, """{"type":"subscribe","topics":["show"]}""");
        Assert.NotNull(await TestServer.ReceiveAsync(socket));
        host.Server.Publish(new ApiEvent(ApiTopic.Show, """{"currentServiceId":"svc-1"}"""u8.ToArray()));
        var frame = JsonNode.Parse((await TestServer.ReceiveAsync(socket))!)!;
        Assert.Equal("svc-1", (string?)frame["data"]!["currentServiceId"]);

        // The Mac's command frames work too, within the key's scope.
        await TestServer.SendTextAsync(socket, """{"type":"command","id":"1","method":"POST","path":"/v1/show/clear-all"}""");
        Assert.Equal(403, (int?)JsonNode.Parse((await TestServer.ReceiveAsync(socket))!)!["status"]);
    }

    [Fact]
    public async Task TheRemoteSocketClosesWithoutAGoodFirstMessage()
    {
        using var host = TestServer.Start(new FakeRemoteBridge());
        foreach (var first in new[] { """{"type":"auth","token":"mxu_wrong"}""", """{"type":"subscribe","topics":["show"]}""", "not json" })
        {
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{host.Port}/remote/ws"), Ct);
            await TestServer.SendTextAsync(socket, first);
            var error = JsonNode.Parse((await TestServer.ReceiveAsync(socket))!)!;
            Assert.Equal(("error", "unauthorized"), ((string?)error["type"], (string?)error["code"]));
            Assert.Null(await TestServer.ReceiveAsync(socket));
        }
    }

    [Fact]
    public async Task APageOnAnotherSiteCantOpenTheRemoteSocket()
    {
        using var host = TestServer.Start(new FakeRemoteBridge());
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", "http://collector.example");
        await Assert.ThrowsAnyAsync<WebSocketException>(() => socket.ConnectAsync(new Uri($"ws://127.0.0.1:{host.Port}/remote/ws"), Ct));
    }

    [Fact]
    public async Task TheMacsRoutesAndContractAreUnchanged()
    {
        var bridge = new FakeRemoteBridge();
        Assert.DoesNotContain(ApiRouteTable.Build(bridge), r => r.Path.StartsWith("/remote", StringComparison.Ordinal));
        using var host = TestServer.Start(bridge);
        Assert.DoesNotContain("/remote", await host.Http.GetStringAsync(host.Url("/openapi.json"), Ct));

        // A bridge without the remote (as the Mac's protocol alone) doesn't serve it.
        using var plain = TestServer.Start(new CoreOnlyBridge());
        Assert.Equal(HttpStatusCode.NotFound, (await plain.Http.GetAsync(plain.Url("/remote"), Ct)).StatusCode);
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
