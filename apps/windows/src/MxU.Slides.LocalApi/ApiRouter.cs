using System.Text.Json;
using System.Text.Json.Nodes;

namespace MxU.Slides.LocalApi;

/// <summary>One request as a route handler sees it, whether it came over HTTP or a WebSocket command.</summary>
public sealed class ApiRequestContext
{
    private static readonly IReadOnlyDictionary<string, string> None = new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string> PathParameters { get; init; } = None;
    public IReadOnlyDictionary<string, string> Query { get; init; } = None;
    public byte[] Body { get; init; } = [];
    public ApiScope Scope { get; init; } = ApiScope.Edit;

    /// <summary>The body as <typeparamref name="T"/>; an empty body reads as <c>{}</c>.</summary>
    public T Decode<T>() where T : class
    {
        ReadOnlySpan<byte> json = Body.Length == 0 ? "{}"u8 : Body;
        try
        {
            return JsonSerializer.Deserialize<T>(json, ApiJson.Options) ?? throw new JsonException("The body is null.");
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or FormatException or InvalidOperationException)
        {
            throw ApiError.BadRequest($"Could not decode request body: {e.Message}");
        }
    }

    public string PathParameter(string name) =>
        PathParameters.TryGetValue(name, out var value) && value.Length > 0
            ? value
            : throw ApiError.BadRequest($"Missing path parameter '{name}'.");

    public ApiDocumentKind DocumentKind()
    {
        var raw = PathParameter("kind");
        return ApiDocumentKinds.Parse(raw) ?? throw ApiError.BadRequest(
            $"Unknown document kind '{raw}'. Kinds: {string.Join(", ", ApiDocumentKinds.All.Select(k => k.Raw()))}.");
    }
}

public sealed record ApiHandlerResponse(int Status, byte[] Body)
{
    public static ApiHandlerResponse Ok { get; } = Json(new ApiOkResponse());

    public static ApiHandlerResponse Json<T>(T value, int status = 200) => new(status, ApiJson.Encode(value));

    public static ApiHandlerResponse Raw(byte[] data, int status = 200) => new(status, data);
}

/// <summary>
/// One operation: <paramref name="Path"/> is a template such as <c>/v1/timers/{id}/play</c>;
/// the schemas feed /openapi.json.
/// </summary>
public sealed record ApiRoute(
    string Method,
    string Path,
    ApiScope Scope,
    string OperationId,
    string Summary,
    string Tag,
    JsonNode? RequestSchema,
    JsonNode ResponseSchema,
    Func<ApiRequestContext, Task<ApiHandlerResponse>> Handler);

public sealed record ApiRouteMatch(ApiRoute Route, IReadOnlyDictionary<string, string> Parameters);

/// <summary>Matches a method and a raw (still percent-encoded) path to a route. Port of APIRouter.</summary>
public sealed class ApiRouter(IReadOnlyList<ApiRoute> routes)
{
    public IReadOnlyList<ApiRoute> Routes { get; } = routes;

    /// <summary>
    /// The first route whose template fits. Parameters are percent-decoded one segment at a
    /// time and may not contain a path separator or NUL, so <c>..%2F..</c> matches nothing.
    /// </summary>
    public ApiRouteMatch? Match(string method, string path)
    {
        var request = Segments(path);
        var upper = method.ToUpperInvariant();
        foreach (var route in Routes.Where(r => r.Method == upper))
        {
            var template = Segments(route.Path);
            if (template.Length != request.Length) continue;
            var parameters = new Dictionary<string, string>();
            var matched = true;
            for (var i = 0; i < template.Length && matched; i++)
            {
                if (IsParameter(template[i]))
                {
                    var value = Uri.UnescapeDataString(request[i]);
                    if (value.Contains('/') || value.Contains('\\') || value.Contains('\0')) matched = false;
                    else parameters[template[i][1..^1]] = value;
                }
                else matched = template[i] == request[i];
            }
            if (matched) return new ApiRouteMatch(route, parameters);
        }
        return null;
    }

    /// <summary>Whether any route has this path, whatever the method (a 405 rather than a 404).</summary>
    public bool PathExists(string path)
    {
        var request = Segments(path);
        return Routes.Any(route =>
        {
            var template = Segments(route.Path);
            return template.Length == request.Length
                && template.Zip(request).All(pair => IsParameter(pair.First) || pair.First == pair.Second);
        });
    }

    private static string[] Segments(string path) => path.Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static bool IsParameter(string segment) => segment.StartsWith('{') && segment.EndsWith('}');
}
