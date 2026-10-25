using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace MxU.Slides.LocalApi.Remote;

/// <summary>
/// Everything under /remote: the browser remote and the stage view. Windows only, and kept apart
/// from the Mac's routes (<see cref="ApiRouteTable"/>), which stay exactly as the Mac has them:
/// these aren't in /openapi.json, and the remote runs the show through the Mac's own endpoints
/// (/v1/show/advance, /v1/alerts/fire…), adding only what those can't do.
/// <list type="bullet">
/// <item><b>Pages</b> (no key needed; they hold no data): <c>/remote</c>, <c>/remote/stage</c>,
/// and their scripts and styles under <c>/remote/assets/</c>, built into this DLL. Nothing comes
/// from the internet, so they work on a church network with no outside access.</item>
/// <item><b>Pairing</b>: <c>POST /remote/pair</c> trades the code shown in Settings for a key (<see cref="RemotePairing"/>).</item>
/// <item><b>Data</b>, with a key in the Authorization header (never the address):
/// <c>GET /remote/session</c>, <c>/remote/service</c>, <c>/remote/alerts</c>,
/// <c>/remote/thumbnails/{presentationId}/{slideId}</c> (a PNG), and <c>POST /remote/stage-message</c>.</item>
/// <item><b>Live updates</b>: <c>/remote/ws</c> is the Mac's WebSocket protocol (subscribe, events,
/// commands), except that the key arrives in the first message, not in the address: a browser can't
/// set headers on a WebSocket, and an address ends up in history and logs.</item>
/// </list>
/// No CORS headers are sent here, so a web page from anywhere else can't read the answers.
/// </summary>
internal sealed class RemoteSite(
    IRemoteBridge bridge,
    RemotePairing pairing,
    ApiTokenStore tokens,
    ApiRouter apiRouter,
    ApiEventBus events,
    Func<ApiRoute, ApiRequestContext, Task<ApiHandlerResponse>> invokeApi,
    Func<Func<Task<object?>>, Task<object?>> onBridge,
    CancellationToken stopping)
{
    public const string Prefix = "/remote";

    /// <summary>How long a remote request waits for the app before saying it's busy (a modal dialog, say).</summary>
    private static readonly TimeSpan BridgeWait = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan AuthWait = TimeSpan.FromSeconds(10);

    // Only these files, by name: nothing in a request can reach any other resource.
    private static readonly Dictionary<string, (string Resource, string Type)> Files = new(StringComparer.Ordinal)
    {
        ["/remote"] = ("remote.html", "text/html; charset=utf-8"),
        ["/remote/"] = ("remote.html", "text/html; charset=utf-8"),
        ["/remote/stage"] = ("stage.html", "text/html; charset=utf-8"),
        ["/remote/assets/remote.css"] = ("remote.css", "text/css; charset=utf-8"),
        ["/remote/assets/stage.css"] = ("stage.css", "text/css; charset=utf-8"),
        ["/remote/assets/connect.js"] = ("connect.js", "text/javascript; charset=utf-8"),
        ["/remote/assets/remote.js"] = ("remote.js", "text/javascript; charset=utf-8"),
        ["/remote/assets/stage.js"] = ("stage.js", "text/javascript; charset=utf-8"),
        ["/remote/assets/icon.svg"] = ("icon.svg", "image/svg+xml"),
    };

    private static readonly Dictionary<string, byte[]> Cache = new(StringComparer.Ordinal);

    public static bool Owns(string path) => path == Prefix || path.StartsWith(Prefix + "/", StringComparison.Ordinal);

    /// <summary>The page's own bytes (for tests and the self-test, which compare them).</summary>
    public static byte[] FileBytes(string name)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var bytes)) return bytes;
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MxU.Slides.LocalApi.Remote.Web." + name)
                ?? throw new FileNotFoundException(name);
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return Cache[name] = copy.ToArray();
        }
    }

    public async Task HandleAsync(HttpContext http, string path)
    {
        var request = http.Request;
        var get = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method);
        if (get && Files.TryGetValue(path, out var file))
        {
            await StaticAsync(http, file.Resource, file.Type);
            return;
        }
        if (get && path == Prefix + "/ws")
        {
            await WebSocketAsync(http);
            return;
        }
        try
        {
            var answer = (request.Method.ToUpperInvariant(), path) switch
            {
                ("POST", "/remote/pair") => await PairAsync(request),
                ("GET", "/remote/session") => Session(Authorized(request, ApiScope.View)),
                ("GET", "/remote/service") => await OnBridge(ApiScope.View, request, () => Json(bridge.RemoteService())),
                ("GET", "/remote/alerts") => await OnBridge(ApiScope.View, request, () => Json(bridge.RemoteAlerts())),
                ("POST", "/remote/stage-message") => await StageMessageAsync(request),
                ("GET", _) when path.StartsWith(Prefix + "/thumbnails/", StringComparison.Ordinal) => await ThumbnailAsync(request, path),
                (_, _) when IsKnown(path) => Answer.Error(new ApiError(405, "method_not_allowed", "Method not allowed.")),
                _ => Answer.Error(ApiError.NotFound("No such page.")),
            };
            await WriteAsync(http, answer);
        }
        catch (ApiError e)
        {
            await WriteAsync(http, Answer.Error(e));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await WriteAsync(http, Answer.Error(new ApiError(500, "internal", e.Message)));
        }
    }

    private static bool IsKnown(string path) =>
        path is "/remote/pair" or "/remote/session" or "/remote/service" or "/remote/alerts" or "/remote/stage-message" or "/remote/ws"
        || Files.ContainsKey(path);

    // MARK: Pages

    private async Task StaticAsync(HttpContext http, string resource, string type)
    {
        var bytes = FileBytes(resource);
        var response = http.Response;
        response.ContentType = type;
        response.ContentLength = bytes.Length;
        // Always ask again, so a phone picks up a new version of the app's pages.
        response.Headers.CacheControl = "no-cache";
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
        if (type.StartsWith("text/html", StringComparison.Ordinal))
        {
            // Only this app's own scripts may run, and the page can't be put inside another site's frame.
            response.Headers.ContentSecurityPolicy =
                "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' blob: data:; connect-src 'self' ws: wss:; "
                + "frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
            response.Headers.XFrameOptions = "DENY";
        }
        if (!HttpMethods.IsHead(http.Request.Method)) await response.Body.WriteAsync(bytes, stopping);
    }

    // MARK: Pairing and keys

    private async Task<Answer> PairAsync(HttpRequest request)
    {
        // A JSON body only: a web page elsewhere can't send one without asking first (CORS), and isn't answered.
        if (request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true)
            throw ApiError.BadRequest("Send the code as JSON.");
        var body = Decode<RemotePairRequest>(await ReadBodyAsync(request, 4096));
        var result = pairing.Redeem(body.Code, body.Name, tokens);
        return result.Outcome switch
        {
            RemotePairing.Outcome.Paired => Json(new RemotePairResponse(result.Secret!, result.Token!.Scope.Raw(), result.Token.Name)),
            RemotePairing.Outcome.Wrong => Json(new JsonObject
            {
                ["status"] = 401, ["code"] = "wrong_code", ["triesLeft"] = result.TriesLeft,
                ["message"] = $"That isn't the code on the computer. {result.TriesLeft} {(result.TriesLeft == 1 ? "try" : "tries")} left.",
            }, 401),
            RemotePairing.Outcome.TooManyTries => Answer.Error(new ApiError(429, "too_many_tries",
                "Too many wrong codes. On the computer, press Pair a phone again for a new code.")),
            _ => Answer.Error(ApiError.NotFound("No code is on offer. On the computer, open Screens & Settings › Browser remote and press Pair a phone.")),
        };
    }

    /// <summary>The key from the Authorization header (never the address), with at least <paramref name="scope"/>.</summary>
    private ApiToken Authorized(HttpRequest request, ApiScope scope)
    {
        var header = request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.Ordinal) ? tokens.Verify(header["Bearer ".Length..].Trim()) : null;
        if (token is null) throw ApiError.Unauthorized("This device isn't paired, or its key was revoked. Pair it again.");
        if (!token.Scope.Allows(scope)) throw ApiError.Forbidden(scope);
        return token;
    }

    private static Answer Session(ApiToken token) => Json(new RemoteSession(token.Name, token.Scope.Raw()));

    // MARK: Data

    private async Task<Answer> StageMessageAsync(HttpRequest request)
    {
        Authorized(request, ApiScope.Control);
        var body = Decode<RemoteStageMessage>(await ReadBodyAsync(request, 16 * 1024));
        var text = body.Text?.Trim();
        if (text?.Length > 500) throw ApiError.BadRequest("A stage message can be at most 500 characters.");
        return await OnBridge(ApiScope.Control, request, () =>
        {
            bridge.SetStageMessage(text);
            return Json(new ApiOkResponse());
        });
    }

    private async Task<Answer> ThumbnailAsync(HttpRequest request, string path)
    {
        Authorized(request, ApiScope.View);
        var parts = path[(Prefix + "/thumbnails/").Length..].Split('/');
        if (parts.Length != 2) throw ApiError.NotFound("No such slide.");
        var presentationId = ApiIds.Validated(Uri.UnescapeDataString(parts[0]));
        var slideId = ApiIds.Validated(Uri.UnescapeDataString(parts[1]));
        var width = int.TryParse(request.Query["width"].ToString(), out var w) ? Math.Clamp(w, 80, 960) : 320;
        var png = await WithTimeout(onBridge(async () => await bridge.SlideThumbnailAsync(presentationId, slideId, width)));
        return png is byte[] bytes
            ? new Answer(200, bytes, "image/png", "private, max-age=300")
            : Answer.Error(ApiError.NotFound("No such slide."));
    }

    private async Task<Answer> OnBridge(ApiScope scope, HttpRequest request, Func<Answer> work)
    {
        Authorized(request, scope);
        return await WithTimeout(onBridge(() => Task.FromResult<object?>(work()))) as Answer
            ?? throw new ApiError(500, "internal", "No answer from the app.");
    }

    private async Task<object?> WithTimeout(Task<object?> work)
    {
        try
        {
            return await work.WaitAsync(BridgeWait, stopping);
        }
        catch (TimeoutException)
        {
            throw new ApiError(503, "busy", "The app is busy (a window may be waiting for an answer). Try again in a moment.");
        }
    }

    // MARK: Live updates

    private async Task WebSocketAsync(HttpContext http)
    {
        // A page from another site may not open this socket for its own ends.
        var origin = http.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && (!Uri.TryCreate(origin, UriKind.Absolute, out var from)
            || !string.Equals(from.Authority, http.Request.Host.Value, StringComparison.OrdinalIgnoreCase)))
        {
            await WriteAsync(http, Answer.Error(new ApiError(403, "forbidden", "Not from this computer's remote page.")));
            return;
        }
        if (!http.WebSockets.IsWebSocketRequest)
        {
            await WriteAsync(http, Answer.Error(ApiError.BadRequest("Expected a WebSocket upgrade.")));
            return;
        }
        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        if (await ReadAuthAsync(socket) is not { } token)
        {
            await SendTextAsync(socket, """{"type":"error","code":"unauthorized","message":"This device isn't paired, or its key was revoked. Pair it again."}""");
            await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation);
            return;
        }
        await new ApiWebSocketSession(apiRouter, events, token, tokens, invokeApi).RunAsync(socket, stopping);
    }

    /// <summary>The first message must be <c>{"type":"auth","token":"mxu_…"}</c>, within ten seconds.</summary>
    private async Task<ApiToken?> ReadAuthAsync(WebSocket socket)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        wait.CancelAfter(AuthWait);
        var buffer = new byte[4096];
        var length = 0;
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(length), wait.Token);
                if (result.MessageType != WebSocketMessageType.Text) return null;
                length += result.Count;
                if (result.EndOfMessage) break;
                if (length >= buffer.Length) return null;
            }
            using var json = JsonDocument.Parse(buffer.AsMemory(0, length));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type) || type.GetString() != "auth"
                || !root.TryGetProperty("token", out var secret) || secret.ValueKind != JsonValueKind.String)
                return null;
            return tokens.Verify(secret.GetString() ?? "");
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or JsonException)
        {
            return null;
        }
    }

    private async Task SendTextAsync(WebSocket socket, string text)
    {
        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, stopping);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException)
        {
        }
    }

    private async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status)
    {
        try
        {
            using var grace = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            grace.CancelAfter(TimeSpan.FromSeconds(1));
            await socket.CloseOutputAsync(status, null, grace.Token);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException)
        {
        }
    }

    // MARK: Answers

    /// <summary>An answer: its status, bytes, content type and caching.</summary>
    private sealed record Answer(int Status, byte[] Body, string ContentType = "application/json", string CacheControl = "no-store")
    {
        public static Answer Error(ApiError error) => Json(error.Body, error.Status);
    }

    private static Answer Json<T>(T value, int status = 200) => new(status, ApiJson.Encode(value));

    private static T Decode<T>(byte[] body) where T : class => new ApiRequestContext { Body = body }.Decode<T>();

    private async Task<byte[]> ReadBodyAsync(HttpRequest request, int limit)
    {
        using var copy = new MemoryStream();
        var buffer = new byte[1024];
        int read;
        while ((read = await request.Body.ReadAsync(buffer, stopping)) > 0)
        {
            copy.Write(buffer, 0, read);
            if (copy.Length > limit) throw new ApiError(413, "too_large", "That request is too large.");
        }
        return copy.ToArray();
    }

    private async Task WriteAsync(HttpContext http, Answer answer)
    {
        var response = http.Response;
        response.StatusCode = answer.Status;
        response.ContentType = answer.ContentType;
        response.ContentLength = answer.Body.Length;
        response.Headers.CacheControl = answer.CacheControl;
        response.Headers.XContentTypeOptions = "nosniff";
        await response.Body.WriteAsync(answer.Body, stopping);
    }
}
