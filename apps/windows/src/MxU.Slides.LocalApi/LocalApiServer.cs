using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MxU.Slides.LocalApi.Remote;

namespace MxU.Slides.LocalApi;

/// <summary>
/// The Local API server: REST and WebSocket on one port, on every network interface, so
/// phones and Companion on the LAN can reach it. Start and Stop never throw and can be
/// called repeatedly; a failure to bind (port in use) is returned and kept in
/// <see cref="LastError"/>. Port of LocalAPIServer plus the start/stop and publishing
/// parts of the Mac's LocalAPIController. Bonjour advertising is not ported.
/// </summary>
public sealed class LocalApiServer(ApiServerInfo? info = null) : IDisposable
{
    public const int DefaultPort = 6980;

    private readonly Lock gate = new();
    private volatile Running? current;

    private sealed record Running(WebApplication App, CancellationTokenSource Stopping, IApiBridge? Bridge, int Port);

    public ApiServerInfo Info { get; } = info ?? new ApiServerInfo(
        Environment.MachineName, "MxU Slides", OpenApiDocument.ApiVersion, OpenApiDocument.DocumentSchemaVersion());

    /// <summary>The event bus sockets subscribe to; see <see cref="Publish(ApiTopic)"/>.</summary>
    public ApiEventBus Events { get; } = new();

    /// <summary>Windows: the pairing codes the browser remote (/remote) trades for keys. Served when the bridge is an <see cref="IRemoteBridge"/>.</summary>
    public RemotePairing Pairing { get; } = new();

    public bool IsRunning => current is not null;

    /// <summary>The port being served (the actual one when started on port 0), or 0 when stopped.</summary>
    public int Port => current?.Port ?? 0;

    /// <summary>Why the last Start failed, e.g. "Failed to bind to address http://[::]:6980: address already in use."</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Serves <see cref="ApiRouteTable"/> for <paramref name="bridge"/>. Bridge calls are posted to
    /// the <see cref="SynchronizationContext"/> this is called on (the dispatcher, from the UI
    /// thread), or run on the thread pool when there is none. Restarts when already running.
    /// Returns false, with <see cref="LastError"/> set, when the server can't start.
    /// </summary>
    public bool Start(int port, IApiBridge bridge, ApiTokenStore tokens) =>
        Start(port, ApiRouteTable.Build(bridge), tokens, bridge);

    /// <summary>Serves a custom route table (tests, tools).</summary>
    public bool Start(int port, IReadOnlyList<ApiRoute> routes, ApiTokenStore tokens) => Start(port, routes, tokens, null);

    private bool Start(int port, IReadOnlyList<ApiRoute> routes, ApiTokenStore tokens, IApiBridge? bridge)
    {
        var bridgeContext = SynchronizationContext.Current;
        lock (gate)
        {
            StopLocked();
            LastError = null;
            var stopping = new CancellationTokenSource();
            WebApplication? app = null;
            try
            {
                var handler = new ApiHttpHandler(new ApiRouter(routes), Events, tokens, Info, bridgeContext, stopping.Token,
                    bridge as IRemoteBridge, Pairing);
                var built = Build(port, handler.HandleAsync);
                app = built;
                Task.Run(() => built.StartAsync()).GetAwaiter().GetResult();
                current = new Running(built, stopping, bridge, BoundPort(built) ?? port);
                return true;
            }
            catch (Exception e)
            {
                LastError = (e is AggregateException { InnerException: { } inner } ? inner : e).Message;
                stopping.Cancel();
                stopping.Dispose();
                if (app is not null) Shutdown(app);
                return false;
            }
        }
    }

    /// <summary>Stops serving and closes every socket. Safe to call when stopped.</summary>
    public void Stop()
    {
        lock (gate) StopLocked();
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Pushes a topic's snapshot, read from the bridge, to subscribed sockets: show is
    /// ShowStatus, timers is Timers, and so on, as the Mac publishes. Call it on the bridge's
    /// thread after the state changes; topics the app doesn't have are skipped.
    /// </summary>
    public void Publish(ApiTopic topic)
    {
        if (current?.Bridge is not { } bridge) return;
        object snapshot;
        try
        {
            snapshot = topic switch
            {
                ApiTopic.Show => bridge.ShowStatus(),
                ApiTopic.Timers => bridge.Timers(),
                ApiTopic.Audio => bridge.AudioStatus(),
                ApiTopic.Transport => bridge.TransportRows(),
                ApiTopic.Outputs => bridge.OutputsStatus(),
                ApiTopic.Library => bridge.LibrarySummary(),
                ApiTopic.Scheduler => bridge.SchedulerStatus(),
                _ => bridge.TimecodeStatus(),
            };
        }
        catch (ApiError)
        {
            return;
        }
        Publish(topic, snapshot);
    }

    /// <summary>Pushes <paramref name="snapshot"/>, encoded like its REST endpoint, to sockets subscribed to <paramref name="topic"/>.</summary>
    public void Publish(ApiTopic topic, object snapshot) => Publish(new ApiEvent(topic, ApiJson.Encode(snapshot)));

    public void Publish(ApiEvent e)
    {
        if (IsRunning) Events.Publish(e);
    }

    /// <summary>
    /// This PC's LAN IPv4 address for Settings' "Connect at" and the docs link, preferring an
    /// adapter with a gateway; null when offline. Port of the Mac's NetworkInfo.lanIPv4.
    /// </summary>
    public static string? LanIPv4() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .Select(n => n.GetIPProperties())
            .OrderByDescending(p => p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
            .SelectMany(p => p.UnicastAddresses.Select(u => u.Address))
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && a.GetAddressBytes() is not [169, 254, ..])
            ?.ToString();

    private void StopLocked()
    {
        if (current is not { } running) return;
        current = null;
        running.Stopping.Cancel();
        Events.CloseAll();
        Shutdown(running.App);
        running.Stopping.Dispose();
    }

    private static void Shutdown(WebApplication app)
    {
        try
        {
            Task.Run(async () =>
            {
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
                {
                    try { await app.StopAsync(timeout.Token); }
                    catch (OperationCanceledException) { }
                }
                await app.DisposeAsync();
            }).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A server that fails to stop cleanly has still released its port.
        }
    }

    private static WebApplication Build(int port, RequestDelegate handler)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        // Not ListenAnyIP: when [::] is taken it quietly falls back to 0.0.0.0, so a second
        // server on a busy port would "start" and answer only some clients.
        builder.WebHost.UseKestrelCore().ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Listen(IPAddress.IPv6Any, port);
        });
        builder.WebHost.UseSockets(options => options.CreateBoundListenSocket = BindListenSocket);
        builder.Services.AddLogging();
        builder.Services.AddSingleton<IHostLifetime, EmbeddedLifetime>();
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(1));
        var app = builder.Build();
        app.UseWebSockets();
        app.Run(handler);
        return app;
    }

    /// <summary>
    /// Every interface, IPv4 and IPv6 on one dual-mode socket (IPv4 only without IPv6), bound
    /// exclusively: Windows otherwise lets it share a port another app holds on 0.0.0.0.
    /// </summary>
    private static Socket BindListenSocket(EndPoint endpoint)
    {
        var ip = (IPEndPoint)endpoint;
        Socket socket;
        try
        {
            socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressFamilyNotSupported)
        {
            ip = new IPEndPoint(IPAddress.Any, ip.Port);
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        }
        try
        {
            if (ip.AddressFamily == AddressFamily.InterNetworkV6) socket.DualMode = true;
            if (OperatingSystem.IsWindows()) socket.ExclusiveAddressUse = true;
            socket.Bind(ip);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static int? BoundPort(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses
            .Select(address => (int?)BindingAddress.Parse(address).Port)
            .FirstOrDefault();

    /// <summary>The host lives inside the app: no console Ctrl+C or process-exit hooks.</summary>
    private sealed class EmbeddedLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

/// <summary>Answers one HTTP request: the fixed endpoints, then the router. Port of LocalAPIServer's routes and handle(_:).</summary>
internal sealed class ApiHttpHandler
{
    private readonly ApiRouter router;
    private readonly ApiEventBus events;
    private readonly ApiTokenStore tokens;
    private readonly ApiServerInfo info;
    private readonly SynchronizationContext? bridgeContext;
    private readonly CancellationToken stopping;
    private readonly int schemaVersion = OpenApiDocument.DocumentSchemaVersion();

    // Windows: the browser remote, when the app's bridge offers it (Remote/RemoteSite.cs).
    private readonly RemoteSite? remote;

    public ApiHttpHandler(
        ApiRouter router, ApiEventBus events, ApiTokenStore tokens, ApiServerInfo info, SynchronizationContext? bridgeContext,
        CancellationToken stopping, IRemoteBridge? remoteBridge = null, RemotePairing? pairing = null)
    {
        (this.router, this.events, this.tokens, this.info, this.bridgeContext, this.stopping) = (router, events, tokens, info, bridgeContext, stopping);
        if (remoteBridge is not null)
            remote = new RemoteSite(remoteBridge, pairing ?? new RemotePairing(), tokens, router, events, InvokeAsync, OnBridgeAsync, stopping);
    }

    public async Task HandleAsync(HttpContext http)
    {
        try
        {
            var request = http.Request;
            var path = RawPath(http);
            if (remote is not null && RemoteSite.Owns(path))
            {
                await remote.HandleAsync(http, path);
                return;
            }
            if (HttpMethods.IsGet(request.Method))
            {
                switch (path)
                {
                    case "/v1/ping":
                        await WriteJsonAsync(http, ApiHandlerResponse.Json(info));
                        return;
                    case "/openapi.json":
                        await WriteJsonAsync(http, ApiHandlerResponse.Raw(ApiJson.Encoded(OpenApiDocument.Compose(router.Routes, schemaVersion))));
                        return;
                    case "/asyncapi.json":
                        await WriteJsonAsync(http, ApiHandlerResponse.Raw(ApiJson.Encoded(AsyncApiDocument.Compose(schemaVersion))));
                        return;
                    case "/docs":
                        var host = request.Headers.Host.ToString() is { Length: > 0 } value ? value : "localhost";
                        var page = Encoding.UTF8.GetBytes(DocsPage.Html(router.Routes, host, schemaVersion));
                        http.Response.ContentType = "text/html; charset=utf-8";
                        http.Response.ContentLength = page.Length;
                        await http.Response.Body.WriteAsync(page, stopping);
                        return;
                    case "/v1/ws":
                        await WebSocketAsync(http);
                        return;
                }
            }
            if (HttpMethods.IsOptions(request.Method))
            {
                AddCorsHeaders(http.Response.Headers);
                http.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }
            await WriteJsonAsync(http, await DispatchAsync(http, path));
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested || http.RequestAborted.IsCancellationRequested)
        {
            http.Abort();
        }
    }

    private async Task<ApiHandlerResponse> DispatchAsync(HttpContext http, string path)
    {
        var request = http.Request;
        if (router.Match(request.Method, path) is not { } match)
        {
            var missing = router.PathExists(path)
                ? new ApiError(405, "method_not_allowed", "Method not allowed.")
                : ApiError.NotFound("No such endpoint. The contract is served at /openapi.json.");
            return Error(missing);
        }
        if (Authenticate(request) is not { } token) return Error(ApiError.Unauthorized());
        if (!token.Scope.Allows(match.Route.Scope)) return Error(ApiError.Forbidden(match.Route.Scope));

        var query = request.Query.ToDictionary(q => q.Key, q => q.Value.Count > 0 ? q.Value[^1] ?? "" : "");
        using var body = new MemoryStream();
        await request.Body.CopyToAsync(body, stopping);
        var context = new ApiRequestContext
        {
            PathParameters = match.Parameters, Query = query, Body = body.ToArray(), Scope = token.Scope,
        };
        try
        {
            return await InvokeAsync(match.Route, context);
        }
        catch (ApiError e)
        {
            return Error(e);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Error(new ApiError(500, "internal", e.Message));
        }
    }

    private async Task WebSocketAsync(HttpContext http)
    {
        if (!http.Request.Query.TryGetValue("token", out var secret) || tokens.Verify(secret[0] ?? "") is not { } token)
        {
            await WriteJsonAsync(http, Error(ApiError.Unauthorized()));
            return;
        }
        if (!http.WebSockets.IsWebSocketRequest)
        {
            await WriteJsonAsync(http, Error(ApiError.BadRequest("Expected a WebSocket upgrade.")));
            return;
        }
        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        await new ApiWebSocketSession(router, events, token, tokens, InvokeAsync).RunAsync(socket, stopping);
    }

    /// <summary>Runs a route on the bridge's context, like the Mac's @MainActor bridge; abandoned when the server stops.</summary>
    private Task<ApiHandlerResponse> InvokeAsync(ApiRoute route, ApiRequestContext context) => OnBridgeAsync(() => route.Handler(context));

    /// <summary>Runs any work on the bridge's context (the UI thread), as <see cref="InvokeAsync"/> runs a route.</summary>
    private Task<T> OnBridgeAsync<T>(Func<Task<T>> work)
    {
        if (bridgeContext is null) return work().WaitAsync(stopping);
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        bridgeContext.Post(async _ =>
        {
            try
            {
                done.TrySetResult(await work());
            }
            catch (Exception e)
            {
                done.TrySetException(e);
            }
        }, null);
        return done.Task.WaitAsync(stopping);
    }

    private ApiToken? Authenticate(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.Ordinal)) return tokens.Verify(header["Bearer ".Length..]);
        if (request.Query.TryGetValue("token", out var secret)) return tokens.Verify(secret[0] ?? "");
        return null;
    }

    /// <summary>The path as sent, still percent-encoded, so the router decodes each segment itself.</summary>
    private static string RawPath(HttpContext http)
    {
        var raw = http.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (string.IsNullOrEmpty(raw) || raw[0] != '/') return http.Request.PathBase + http.Request.Path;
        var query = raw.IndexOf('?');
        return query < 0 ? raw : raw[..query];
    }

    private static ApiHandlerResponse Error(ApiError error) => ApiHandlerResponse.Json(error.Body, error.Status);

    private async Task WriteJsonAsync(HttpContext http, ApiHandlerResponse payload)
    {
        var response = http.Response;
        AddCorsHeaders(response.Headers);
        response.StatusCode = payload.Status;
        response.ContentType = "application/json";
        response.ContentLength = payload.Body.Length;
        await response.Body.WriteAsync(payload.Body, stopping);
    }

    private static void AddCorsHeaders(IHeaderDictionary headers)
    {
        headers.AccessControlAllowOrigin = "*";
        headers.AccessControlAllowMethods = "GET, POST, PUT, DELETE, OPTIONS";
        headers.AccessControlAllowHeaders = "Authorization, Content-Type";
    }
}
