using System.Text;
using System.Text.Json.Nodes;

namespace MxU.Slides.LocalApi.Tests;

// Port of WebSocketAuthorizationTests.swift, over a real socket to the real server.

public sealed class WebSocketAuthorizationTests
{
    private static ApiRoute Route(string method, string path, ApiScope scope, Func<ApiRequestContext, Task<ApiHandlerResponse>> handler) =>
        new(method, path, scope, "command", "Command", "Test", null, new JsonObject(), handler);

    [Fact]
    public async Task RevokedConnectionCannotExecuteCommands()
    {
        var reached = false;
        var tokens = TestServer.NewTokens();
        var (token, secret) = tokens.Create("Remote", ApiScope.Control);
        using var host = TestServer.Start([Route("POST", "/command", ApiScope.Control, _ =>
        {
            reached = true;
            return Task.FromResult(ApiHandlerResponse.Ok);
        })], tokens);
        using var socket = await host.ConnectAsync(secret);
        Assert.NotNull(await TestServer.ReceiveAsync(socket)); // hello confirms the session was open
        tokens.Revoke(token.Id);
        await TestServer.SendTextAsync(socket, """{"type":"command","method":"POST","path":"/command"}""");
        Assert.Null(await TestServer.ReceiveAsync(socket));
        Assert.False(reached, "A revoked WebSocket reached the command handler");
    }

    [Fact]
    public async Task RevokedConnectionCannotReceiveEvents()
    {
        var tokens = TestServer.NewTokens();
        var (token, secret) = tokens.Create("Display", ApiScope.View);
        using var host = TestServer.Start([], tokens);
        using var socket = await host.ConnectAsync(secret);
        Assert.NotNull(await TestServer.ReceiveAsync(socket));
        await TestServer.SendTextAsync(socket, """{"type":"subscribe","topics":["show"]}""");
        Assert.NotNull(await TestServer.ReceiveAsync(socket)); // subscription acknowledgement
        host.Server.Publish(new ApiEvent(ApiTopic.Show, "{}"u8.ToArray()));
        Assert.NotNull(await TestServer.ReceiveAsync(socket)); // active tokens still receive events
        tokens.Revoke(token.Id);
        host.Server.Publish(new ApiEvent(ApiTopic.Show, "{}"u8.ToArray()));
        Assert.Null(await TestServer.ReceiveAsync(socket));
    }

    [Fact]
    public async Task ViewConnectionCannotRunControlCommands()
    {
        var reached = false;
        var tokens = TestServer.NewTokens();
        var secret = tokens.Create("Display", ApiScope.View).Secret;
        using var host = TestServer.Start([Route("POST", "/command", ApiScope.Control, _ =>
        {
            reached = true;
            return Task.FromResult(ApiHandlerResponse.Ok);
        })], tokens);
        using var socket = await host.ConnectAsync(secret);
        Assert.NotNull(await TestServer.ReceiveAsync(socket));
        await TestServer.SendTextAsync(socket, """{"type":"command","method":"POST","path":"/command"}""");
        var frame = await TestServer.ReceiveAsync(socket);
        Assert.NotNull(frame);
        Assert.Equal(403, (int?)JsonNode.Parse(frame)!["status"]);
        Assert.False(reached, "A view-only WebSocket reached the command handler");
    }

    [Fact]
    public async Task StreamCredentialsRequireManageOverWebSocket()
    {
        foreach (var scope in ApiScopes.All)
        {
            var bridge = new FakeBridge();
            bridge.Documents["preset"] = Encoding.UTF8.GetBytes("""{"destinations":[{"streamKey":"test-credential"}]}""");
            using var host = TestServer.Start(bridge);
            using var socket = await host.ConnectAsync(host.Mint(scope));
            Assert.NotNull(await TestServer.ReceiveAsync(socket));
            await TestServer.SendTextAsync(socket, """{"type":"command","method":"GET","path":"/v1/documents/stream-presets/preset"}""");
            var frame = await TestServer.ReceiveAsync(socket);
            Assert.NotNull(frame);
            Assert.Equal(scope == ApiScope.Edit ? 200 : 403, (int?)JsonNode.Parse(frame)!["status"]);
            Assert.Equal(scope == ApiScope.Edit, frame.Contains("test-credential"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevocationDuringCommandSuppressesResponse(bool fails)
    {
        var tokens = TestServer.NewTokens();
        var (token, secret) = tokens.Create("Test", ApiScope.View);
        using var host = TestServer.Start([Route("GET", "/document", ApiScope.View, _ =>
        {
            tokens.Revoke(token.Id);
            if (fails) throw ApiError.BadRequest("sensitive error detail");
            return Task.FromResult(ApiHandlerResponse.Raw("""{"sensitive":"content"}"""u8.ToArray()));
        })], tokens);
        using var socket = await host.ConnectAsync(secret);
        Assert.NotNull(await TestServer.ReceiveAsync(socket));
        await TestServer.SendTextAsync(socket, """{"type":"command","method":"GET","path":"/document"}""");
        Assert.Null(await TestServer.ReceiveAsync(socket));
    }
}
