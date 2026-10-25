using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace MxU.Slides.Core.PlanningCenter;

/// <summary>Why a Planning Center request failed, so the app can say what to do about it.</summary>
public enum PlanningCenterProblem { Unauthorized, Forbidden, NotFound, RateLimited, Server, Network, Timeout, BadResponse }

/// <summary>A Planning Center request that failed, with a message written for the operator.</summary>
public sealed class PlanningCenterException(PlanningCenterProblem problem, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public PlanningCenterProblem Problem { get; } = problem;
}

/// <summary>
/// Talks to Planning Center Services (api.planningcenteronline.com/services/v2), a JSON:API.
/// <list type="bullet">
/// <item>Every request carries the church's Personal Access Token as HTTP Basic, in the header.
/// Nothing secret is ever put in a URL, and the log callback is only given the path and status.</item>
/// <item>Requests only go to the address it was made with. Redirects aren't followed and a "next
/// page" link pointing anywhere else is refused, so the credentials can't be sent to another site.</item>
/// <item>Planning Center allows about 100 requests per 20 seconds. When it answers 429 it says how
/// long to wait (Retry-After); this waits that long (at most a minute) and tries again, twice.</item>
/// <item>Lists come a page at a time; <see cref="GetAllAsync"/> follows links.next.</item>
/// </list>
/// All methods are async and never touch the UI thread; each request times out after
/// <see cref="Timeout"/>. Windows only: the Mac has no twin.
/// </summary>
public sealed class PlanningCenterClient : IDisposable
{
    public static readonly Uri DefaultBase = new("https://api.planningcenteronline.com/services/v2/");

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    /// <summary>The longest Retry-After honoured; a longer one fails with a "try again later" message.</summary>
    public static readonly TimeSpan LongestWait = TimeSpan.FromSeconds(60);

    private const int MaxAttempts = 3;
    private const int MaxPages = 40;
    private const int MaxBodyBytes = 16 * 1024 * 1024;

    private readonly HttpClient http;
    private readonly Uri baseUri;
    private readonly Action<string>? log;

    /// <param name="handler">The HTTP stack; tests pass a fake that answers from recorded JSON.</param>
    /// <param name="baseUri">Where Services v2 lives (ends with a slash); tests and the self-test point it at a fake.</param>
    /// <param name="log">Told about each request as "GET service_types → 200". Never given credentials.</param>
    public PlanningCenterClient(PcoCredentials credentials, HttpMessageHandler? handler = null, Uri? baseUri = null, Action<string>? log = null)
    {
        this.baseUri = baseUri ?? DefaultBase;
        this.log = log;
        // Not following redirects matters: a redirect would carry the Authorization header to wherever it points.
        http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        }, disposeHandler: handler is null)
        {
            // The per-request limit below (Timeout) does the work; this only stops HttpClient's own 100 s default getting in first.
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
        http.DefaultRequestHeaders.Authorization = credentials.BasicHeader();
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MxUSlidesWindows", "1.0"));
    }

    /// <summary>How long one request (and reading its answer) may take.</summary>
    public TimeSpan Timeout { get; init; } = DefaultTimeout;

    /// <summary>How waits happen; tests replace it so a 429 doesn't really pause them.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <summary>Told when Planning Center asks for a pause ("Planning Center is busy: waiting 5 s"), for the status line.</summary>
    public IProgress<string>? Progress { get; init; }

    public void Dispose() => http.Dispose();

    // MARK: What the import asks for

    /// <summary>The organization's name ("First Church"): proves the token works, for Settings.</summary>
    public async Task<string> OrganizationNameAsync(CancellationToken cancellation)
    {
        var document = await GetAsync("", cancellation);
        return document.Data.FirstOrDefault()?.Trimmed("name") ?? "Planning Center";
    }

    /// <summary>Every service type the token can see, in the church's own order.</summary>
    public async Task<IReadOnlyList<PcoServiceType>> ServiceTypesAsync(CancellationToken cancellation)
    {
        var document = await GetAllAsync("service_types?order=sequence&per_page=100", cancellation);
        return [.. document.Data.Where(r => r.Type == "ServiceType").Select(PcoServiceType.From)];
    }

    /// <summary>A service type's plans from today on (soonest first), then its last two, newest first.</summary>
    public async Task<(IReadOnlyList<PcoPlan> Upcoming, IReadOnlyList<PcoPlan> Recent)> PlansAsync(string serviceTypeId, CancellationToken cancellation)
    {
        var id = PcoIds.Validated(serviceTypeId);
        var future = await GetAsync($"service_types/{id}/plans?filter=future&order=sort_date&per_page=25", cancellation);
        var past = await GetAsync($"service_types/{id}/plans?filter=past&order=-sort_date&per_page=2", cancellation);
        return (
            [.. future.Data.Where(r => r.Type == "Plan").Select(r => PcoPlan.From(r, id))],
            [.. past.Data.Where(r => r.Type == "Plan").Select(r => PcoPlan.From(r, id))]);
    }

    /// <summary>One plan with all its items, each with its song, arrangement, key, media and notes.</summary>
    public async Task<PcoPlanContents> PlanAsync(string serviceTypeId, string planId, string serviceTypeName, CancellationToken cancellation)
    {
        var type = PcoIds.Validated(serviceTypeId);
        var id = PcoIds.Validated(planId);
        var planDocument = await GetAsync($"service_types/{type}/plans/{id}", cancellation);
        var plan = planDocument.Data.FirstOrDefault(r => r.Type == "Plan") is { } resource
            ? PcoPlan.From(resource, type)
            : throw new PlanningCenterException(PlanningCenterProblem.BadResponse, "Planning Center's answer had no plan in it.");
        var items = await GetAllAsync($"service_types/{type}/plans/{id}/items?include=song,arrangement,key,media,item_notes&per_page=100", cancellation);
        var rows = items.Data.Where(r => r.Type == "Item")
            .Select(r => PcoPlanItem.From(r, items))
            .OrderBy(i => i.Sequence)
            .ToList();
        return new PcoPlanContents(plan, serviceTypeName, rows);
    }

    // MARK: Requests

    /// <summary>Every page of a list, following links.next (at most <see cref="MaxPages"/> pages).</summary>
    public async Task<PcoDocument> GetAllAsync(string path, CancellationToken cancellation)
    {
        var document = await GetAsync(path, cancellation);
        for (var page = 1; document.Next is { } next && page < MaxPages; page++)
            document.Append(await GetAsync(Checked(next), cancellation));
        return document;
    }

    /// <summary>One request, relative to the base address, with the 429 and error handling described above.</summary>
    public Task<PcoDocument> GetAsync(string path, CancellationToken cancellation) => GetAsync(new Uri(baseUri, path), cancellation);

    private async Task<PcoDocument> GetAsync(Uri uri, CancellationToken cancellation)
    {
        for (var attempt = 1; ; attempt++)
        {
            // The time limit covers reading the answer too, not just its headers, so a stalled download can't hang the import.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(Timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            }
            catch (OperationCanceledException e) when (!cancellation.IsCancellationRequested)
            {
                throw TimedOut(e);
            }
            catch (HttpRequestException e)
            {
                log?.Invoke($"Planning Center: GET {Describe(uri)} failed: {e.HttpRequestError}");
                throw new PlanningCenterException(PlanningCenterProblem.Network,
                    "Couldn't reach Planning Center. Check this computer's internet connection, then try again.", e);
            }
            using (response)
            {
                var status = (int)response.StatusCode;
                log?.Invoke($"Planning Center: GET {Describe(uri)} → {status}");
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var wait = RetryAfter(response) ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    if (attempt >= MaxAttempts || wait > LongestWait)
                        throw new PlanningCenterException(PlanningCenterProblem.RateLimited,
                            "Planning Center is busy and asked this app to slow down. Wait a minute, then try again.");
                    Progress?.Report($"Planning Center is busy: waiting {Math.Ceiling(wait.TotalSeconds):0} s…");
                    await Delay(wait, cancellation);
                    continue;
                }
                if (status >= 500 && attempt < 2)
                {
                    // One quick retry for a passing hiccup (a 502 from their load balancer, say).
                    await Delay(TimeSpan.FromSeconds(1), cancellation);
                    continue;
                }
                byte[] body;
                try
                {
                    body = await ReadBodyAsync(response, timeout.Token);
                }
                catch (OperationCanceledException e) when (!cancellation.IsCancellationRequested)
                {
                    throw TimedOut(e);
                }
                catch (Exception e) when (e is HttpRequestException or IOException)
                {
                    throw new PlanningCenterException(PlanningCenterProblem.Network, "The connection to Planning Center dropped. Try again.", e);
                }
                if (response.IsSuccessStatusCode)
                {
                    try
                    {
                        return PcoDocument.Parse(body);
                    }
                    catch (JsonException e)
                    {
                        throw new PlanningCenterException(PlanningCenterProblem.BadResponse, "Planning Center sent an answer this app couldn't read.", e);
                    }
                }
                throw Failure(status, body);
            }
        }
    }

    private static PlanningCenterException TimedOut(Exception e) =>
        new(PlanningCenterProblem.Timeout, "Planning Center took too long to answer. Try again in a moment.", e);

    /// <summary>A next-page link, only if it's on the same site and under the same API as the base address.</summary>
    private Uri Checked(string next)
    {
        if (!Uri.TryCreate(next, UriKind.Absolute, out var uri)
            || uri.Scheme != baseUri.Scheme || !string.Equals(uri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase)
            || uri.Port != baseUri.Port || !uri.AbsolutePath.StartsWith(baseUri.AbsolutePath, StringComparison.Ordinal))
            throw new PlanningCenterException(PlanningCenterProblem.BadResponse,
                "Planning Center's answer pointed to another site for the next page, so the import stopped there.");
        return uri;
    }

    /// <summary>The part of a request address that's safe and useful in a log: the path under the API and its query.</summary>
    private string Describe(Uri uri) =>
        uri.AbsoluteUri.StartsWith(baseUri.AbsoluteUri, StringComparison.Ordinal) ? uri.AbsoluteUri[baseUri.AbsoluteUri.Length..] : uri.AbsolutePath;

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is not { } retry) return null;
        if (retry.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (retry.Date is { } date) return date - DateTimeOffset.UtcNow is { } left && left > TimeSpan.Zero ? left : TimeSpan.Zero;
        return null;
    }

    private static async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellation)
    {
        if (response.Content.Headers.ContentLength > MaxBodyBytes)
            throw new PlanningCenterException(PlanningCenterProblem.BadResponse, "Planning Center's answer was too large to read.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        using var copy = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellation)) > 0)
        {
            copy.Write(buffer, 0, read);
            if (copy.Length > MaxBodyBytes)
                throw new PlanningCenterException(PlanningCenterProblem.BadResponse, "Planning Center's answer was too large to read.");
        }
        return copy.ToArray();
    }

    /// <summary>An error answer as a message for the operator, with Planning Center's own detail when it gives one.</summary>
    private static PlanningCenterException Failure(int status, byte[] body)
    {
        var detail = ErrorDetail(body);
        return status switch
        {
            401 => new(PlanningCenterProblem.Unauthorized,
                "Planning Center didn't accept the Application ID and Secret. Check them in Settings › Planning Center, or make a new Personal Access Token."),
            403 => new(PlanningCenterProblem.Forbidden,
                "This Planning Center login can't see that. Ask your Planning Center administrator for access to Services."),
            >= 300 and < 400 => new(PlanningCenterProblem.BadResponse,
                "Planning Center sent the request somewhere else. It wasn't followed, so the credentials stay with Planning Center."),
            404 => new(PlanningCenterProblem.NotFound,
                "Planning Center couldn't find that. The plan may have been deleted or moved to another service type."),
            >= 500 => new(PlanningCenterProblem.Server, $"Planning Center had a problem (error {status}). Try again in a few minutes."),
            _ => new(PlanningCenterProblem.BadResponse,
                detail is null ? $"Planning Center refused the request (error {status})." : $"Planning Center refused the request (error {status}): {detail}"),
        };
    }

    /// <summary>The first error's title and detail from a JSON:API error body (<c>{"errors":[{"title","detail"}]}</c>).</summary>
    private static string? ErrorDetail(byte[] body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            if (!json.RootElement.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() == 0)
                return null;
            var first = errors[0];
            var title = first.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var detail = first.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            var text = string.Join(": ", new[] { title, detail }.Where(s => !string.IsNullOrWhiteSpace(s)));
            return text.Length == 0 ? null : text.Length > 200 ? text[..200] + "…" : text;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Planning Center ids are numbers; anything else is refused before it goes into a request path.</summary>
public static class PcoIds
{
    public static string Validated(string id) =>
        id.Length is > 0 and <= 20 && id.All(char.IsAsciiDigit)
            ? id
            : throw new PlanningCenterException(PlanningCenterProblem.NotFound, "That isn't a Planning Center id.");
}
