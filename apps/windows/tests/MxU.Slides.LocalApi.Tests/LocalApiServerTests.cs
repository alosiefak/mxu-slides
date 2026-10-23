using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json.Nodes;

namespace MxU.Slides.LocalApi.Tests;

/// <summary>The server end to end over HTTP, as LocalAPIServer.swift answers.</summary>
public sealed class LocalApiServerTests
{
    private static CancellationToken Ct => TestServer.Ct;

    private static async Task<(HttpStatusCode Status, JsonNode? Json)> ReadAsync(Task<HttpResponseMessage> sending)
    {
        using var response = await sending;
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    [Fact]
    public async Task PingAnswersWithoutAToken()
    {
        using var host = TestServer.Start(new FakeBridge());
        using var response = await host.SendAsync(HttpMethod.Get, "/v1/ping");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        var info = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
        Assert.Equal("MxU Slides", (string?)info["product"]);
        Assert.Equal("1.0", (string?)info["apiVersion"]);
        Assert.Equal(OpenApiDocument.DocumentSchemaVersion(), (int?)info["schemaVersion"]);
    }

    [Fact]
    public async Task RequestsNeedAValidToken()
    {
        using var host = TestServer.Start(new FakeBridge());
        var (status, json) = await ReadAsync(host.SendAsync(HttpMethod.Get, "/v1/status"));
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("""{"code":"unauthorized","message":"Missing or invalid token.","status":401}""", json!.ToJsonString());

        Assert.Equal(HttpStatusCode.Unauthorized, (await ReadAsync(host.SendAsync(HttpMethod.Get, "/v1/status", "mxu_wrong"))).Status);
        var secret = host.Mint(ApiScope.View);
        Assert.Equal(HttpStatusCode.OK, (await ReadAsync(host.SendAsync(HttpMethod.Get, "/v1/status", secret))).Status);
        Assert.Equal(HttpStatusCode.OK, (await ReadAsync(host.SendAsync(HttpMethod.Get, $"/v1/status?token={secret}"))).Status);
    }

    [Fact]
    public async Task ScopeIsCheckedBeforeTheBridge()
    {
        var bridge = new FakeBridge();
        using var host = TestServer.Start(bridge);
        var (status, json) = await ReadAsync(host.SendAsync(HttpMethod.Post, "/v1/show/clear-all", host.Mint(ApiScope.View)));
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("insufficient_scope", (string?)json!["code"]);
        Assert.Equal("This endpoint requires the control scope.", (string?)json["message"]);
        Assert.Empty(bridge.Calls);

        Assert.Equal(HttpStatusCode.OK, (await ReadAsync(host.SendAsync(HttpMethod.Post, "/v1/show/clear-all", host.Mint(ApiScope.Control)))).Status);
        Assert.Equal(["clearAll"], bridge.Calls);
    }

    [Fact]
    public async Task UnknownPathsAre404AndWrongMethods405BeforeAuthentication()
    {
        using var host = TestServer.Start(new FakeBridge());
        var (status, json) = await ReadAsync(host.SendAsync(HttpMethod.Get, "/v1/nothing-here"));
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("No such endpoint. The contract is served at /openapi.json.", (string?)json!["message"]);

        (status, json) = await ReadAsync(host.SendAsync(HttpMethod.Delete, "/v1/status"));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, status);
        Assert.Equal("method_not_allowed", (string?)json!["code"]);
    }

    [Fact]
    public async Task PreflightAnswersWithCorsHeaders()
    {
        using var host = TestServer.Start(new FakeBridge());
        using var response = await host.SendAsync(HttpMethod.Options, "/v1/show/slide");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("GET, POST, PUT, DELETE, OPTIONS", response.Headers.GetValues("Access-Control-Allow-Methods").Single());
        Assert.Equal("Authorization, Content-Type", response.Headers.GetValues("Access-Control-Allow-Headers").Single());
    }

    [Fact]
    public async Task ContractAndDocsAreServed()
    {
        using var host = TestServer.Start(new FakeBridge());
        var (_, openapi) = await ReadAsync(host.SendAsync(HttpMethod.Get, "/openapi.json"));
        Assert.Equal("3.1.0", (string?)openapi!["openapi"]);
        Assert.NotNull(openapi["paths"]!["/v1/show/slide"]);
        Assert.NotNull(openapi["components"]!["schemas"]!["Presentation"]);

        var (_, asyncapi) = await ReadAsync(host.SendAsync(HttpMethod.Get, "/asyncapi.json"));
        Assert.NotNull(asyncapi!["channels"]!["show"]);

        using var docs = await host.SendAsync(HttpMethod.Get, "/docs");
        Assert.Equal("text/html; charset=utf-8", docs.Content.Headers.ContentType?.ToString());
        var html = await docs.Content.ReadAsStringAsync(Ct);
        Assert.Contains($"http://127.0.0.1:{host.Port}/v1/show/slide", html);
        Assert.Contains("<span class=\"scope scope-edit\">Manage</span>", html);
    }

    [Fact]
    public async Task BridgeErrorsKeepTheirStatusAndOthersAre500()
    {
        using var host = TestServer.Start([
            new ApiRoute("GET", "/v1/boom", ApiScope.View, "boom", "Boom", "Test", null, new JsonObject(),
                _ => throw new InvalidOperationException("kaboom")),
        ]);
        var (status, json) = await ReadAsync(host.SendAsync(HttpMethod.Get, "/v1/boom", host.Mint(ApiScope.View)));
        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Equal("""{"code":"internal","message":"kaboom","status":500}""", json!.ToJsonString());

        using var fake = TestServer.Start(new FakeBridge());
        (status, json) = await ReadAsync(fake.SendAsync(HttpMethod.Get, "/v1/documents/presentations/ghost", fake.Mint(ApiScope.View)));
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("not_found", (string?)json!["code"]);
    }

    [Fact]
    public async Task FeaturesTheAppLacksAnswerUnavailableAfterTheScopeCheck()
    {
        using var host = TestServer.Start(new CoreOnlyBridge());
        var (status, json) = await ReadAsync(host.SendAsync(HttpMethod.Get, "/v1/scheduler", host.Mint(ApiScope.View)));
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("""{"code":"not_found","message":"Scheduler unavailable.","status":404}""", json!.ToJsonString());

        (status, _) = await ReadAsync(host.SendAsync(HttpMethod.Post, "/v1/scheduler/enabled", host.Mint(ApiScope.View), """{"enabled":true}"""));
        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    [Fact]
    public async Task EncodedTraversalNeverReachesADocument()
    {
        var bridge = new FakeBridge();
        bridge.Documents["../../secrets"] = """{"secret":"leaked"}"""u8.ToArray();
        using var host = TestServer.Start(bridge);
        using var response = await host.SendAsync(HttpMethod.Get, "/v1/documents/presentations/..%2F..%2Fsecrets", host.Mint(ApiScope.Edit));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode); // as on the Mac: the template exists, nothing matches
        Assert.DoesNotContain("leaked", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public void PortInUseIsReportedNotThrown()
    {
        using var first = TestServer.Start(new FakeBridge());
        using var second = new LocalApiServer();
        Assert.False(second.Start(first.Port, new FakeBridge(), TestServer.NewTokens()));
        Assert.False(second.IsRunning);
        Assert.Equal(0, second.Port);
        Assert.Contains("address already in use", second.LastError, StringComparison.OrdinalIgnoreCase);

        var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        try
        {
            var taken = ((IPEndPoint)listener.LocalEndpoint).Port;
            Assert.False(second.Start(taken, new FakeBridge(), TestServer.NewTokens()));
            Assert.NotNull(second.LastError);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task StopsAndStartsRepeatedlyOnOnePort()
    {
        var tokens = TestServer.NewTokens();
        var secret = tokens.Create("Test", ApiScope.View).Secret;
        using var server = new LocalApiServer();
        using var http = new HttpClient();
        server.Stop(); // stopping a stopped server is a no-op

        Assert.True(server.Start(0, new FakeBridge(), tokens), server.LastError);
        var port = server.Port;
        for (var round = 0; round < 3; round++)
        {
            server.Stop();
            server.Stop();
            Assert.False(server.IsRunning);
            Assert.True(server.Start(port, new FakeBridge(), tokens), server.LastError);
            Assert.True(server.Start(port, new FakeBridge(), tokens), server.LastError); // a restart
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/v1/status");
            request.Headers.Authorization = new("Bearer", secret);
            using var response = await http.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        Assert.Null(server.LastError);
    }

    [Fact]
    public async Task BridgeCallsRunOnTheContextThatStartedTheServer()
    {
        using var context = new SingleThreadContext();
        var bridge = new ThreadRecordingBridge();
        var tokens = TestServer.NewTokens();
        var secret = tokens.Create("Test", ApiScope.View).Secret;
        using var server = new LocalApiServer();
        Assert.True(await context.RunAsync(() => server.Start(0, bridge, tokens)), server.LastError);

        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{server.Port}/v1/status");
        request.Headers.Authorization = new("Bearer", secret);
        using var response = await http.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(context.Thread.ManagedThreadId, bridge.ThreadId);
    }

    [Fact]
    public async Task StopDoesNotWaitForABridgeThatNeverAnswers()
    {
        var tokens = TestServer.NewTokens();
        var secret = tokens.Create("Test", ApiScope.View).Secret;
        using var server = new LocalApiServer();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new StalledContext());
        try
        {
            Assert.True(server.Start(0, new FakeBridge(), tokens), server.LastError);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{server.Port}/v1/status");
        request.Headers.Authorization = new("Bearer", secret);
        var pending = http.SendAsync(request, Ct);
        await Task.Delay(200, Ct);
        Assert.False(pending.IsCompleted);

        var clock = Stopwatch.StartNew();
        server.Stop();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"Stop took {clock.Elapsed}");
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => pending);
    }

    [Fact]
    public async Task PublishPushesTheBridgesSnapshot()
    {
        var bridge = new LiveBridge();
        using var host = TestServer.Start(bridge);
        using var socket = await host.ConnectAsync(host.Mint(ApiScope.View));
        Assert.Contains("\"hello\"", await TestServer.ReceiveAsync(socket));
        await TestServer.SendTextAsync(socket, """{"type":"subscribe","topics":["show","scheduler"]}""");
        Assert.Equal("""{"topics":["show","scheduler"],"type":"subscribed"}""", await TestServer.ReceiveAsync(socket));

        host.Server.Publish(ApiTopic.Scheduler); // not on Windows: nothing is sent
        host.Server.Publish(ApiTopic.Timers); // not subscribed
        host.Server.Publish(ApiTopic.Show);
        var frame = JsonNode.Parse((await TestServer.ReceiveAsync(socket))!)!;
        Assert.Equal("event", (string?)frame["type"]);
        Assert.Equal("show", (string?)frame["topic"]);
        Assert.Equal("s1", (string?)frame["data"]!["liveSlide"]!["slideId"]);
    }

    [Fact]
    public async Task WebSocketFramesAnswerLikeTheMac()
    {
        using var host = TestServer.Start(new FakeBridge());
        using var socket = await host.ConnectAsync(host.Mint(ApiScope.Control));
        Assert.Equal("""{"scope":"control","type":"hello"}""", await TestServer.ReceiveAsync(socket));

        async Task<string?> Exchange(string text)
        {
            await TestServer.SendTextAsync(socket, text);
            return await TestServer.ReceiveAsync(socket);
        }

        Assert.Equal("""{"type":"pong"}""", await Exchange("""{"type":"ping"}"""));
        Assert.Equal("""{"message":"Unparseable frame.","type":"error"}""", await Exchange("not json"));
        Assert.Equal("""{"message":"Unparseable frame.","type":"error"}""", await Exchange("""{"type":"command","id":7}"""));
        Assert.Equal("""{"message":"Unknown frame type 'dance'.","type":"error"}""", await Exchange("""{"type":"dance"}"""));
        Assert.Equal("""{"topics":["timecode"],"type":"subscribed"}""",
            await Exchange("""{"type":"subscribe","topics":["timecode","nonsense"]}"""));
        Assert.Equal(
            """{"topics":["show","timers","audio","transport","outputs","library","scheduler","timecode"],"type":"subscribed"}""",
            await Exchange("""{"type":"subscribe"}"""));
        Assert.Equal("""{"topics":["audio"],"type":"subscribed"}""",
            await Exchange("""{"type":"unsubscribe","topics":["show","timers","transport","outputs","library","scheduler","timecode"]}"""));

        var missing = JsonNode.Parse((await Exchange("""{"type":"command","id":"c1","method":"GET","path":"/v1/nope"}"""))!)!;
        Assert.Equal("c1", (string?)missing["id"]);
        Assert.Equal(404, (int?)missing["status"]);
        Assert.Equal("No such operation.", (string?)missing["data"]!["message"]);

        var fired = JsonNode.Parse((await Exchange(
            """{"type":"command","id":"c2","method":"POST","path":"/v1/show/advance","body":{"steps":2}}"""))!)!;
        Assert.Equal("""{"id":"c2","status":200,"type":"result","data":{"ok":true}}""", fired.ToJsonString());
    }

    [Fact]
    public async Task WebSocketNeedsAValidTokenInTheQuery()
    {
        using var host = TestServer.Start(new FakeBridge());
        var (status, json) = await ReadAsync(host.SendAsync(HttpMethod.Get, "/v1/ws?token=mxu_wrong"));
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("unauthorized", (string?)json!["code"]);
        (status, _) = await ReadAsync(host.SendAsync(HttpMethod.Get, "/v1/ws", host.Mint(ApiScope.Edit)));
        Assert.Equal(HttpStatusCode.Unauthorized, status); // the socket takes the key only in the query, as on the Mac
        await Assert.ThrowsAsync<WebSocketException>(() => host.ConnectAsync("mxu_wrong"));
    }

    [Fact]
    public void LanAddressIsARoutableIPv4WhenOnline()
    {
        if (LocalApiServer.LanIPv4() is not { } address) return;
        var ip = IPAddress.Parse(address);
        Assert.Equal(AddressFamily.InterNetwork, ip.AddressFamily);
        Assert.False(IPAddress.IsLoopback(ip));
    }

    private sealed class ThreadRecordingBridge : CoreOnlyBridge
    {
        public int? ThreadId { get; private set; }

        public override ApiShowStatus ShowStatus()
        {
            ThreadId = Environment.CurrentManagedThreadId;
            return base.ShowStatus();
        }
    }

    private sealed class LiveBridge : CoreOnlyBridge
    {
        public override ApiShowStatus ShowStatus() => base.ShowStatus() with
        {
            LiveSlide = new ApiLiveSlide("p1", "Way Maker", "s1", 0, null, null, 0),
        };
    }
}
