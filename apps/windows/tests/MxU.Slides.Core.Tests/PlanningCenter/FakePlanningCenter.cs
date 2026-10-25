using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using MxU.Slides.Core.PlanningCenter;

namespace MxU.Slides.Core.Tests.PlanningCenter;

/// <summary>
/// Planning Center, played back from recorded answers: an HTTP handler that answers each request
/// path with a fixture file (written from Planning Center's documented response shapes) and keeps
/// every request it saw. Nothing goes near the real network.
/// </summary>
internal sealed class FakePlanningCenter : HttpMessageHandler
{
    public const string Plans = "/services/v2/service_types/1100001/plans";
    public const string Items = Plans + "/80000001/items?include=song,arrangement,key,media,item_notes&per_page=100";

    public static readonly PcoCredentials Credentials = new("a1b2c3d4e5f6-application-id", "s3cr3t-pat-value-9z8y7x");

    /// <summary>The request path (and query) → the answer, made fresh for each request.</summary>
    public Dictionary<string, Func<HttpResponseMessage>> Routes { get; } = new(StringComparer.Ordinal);

    /// <summary>Answers to give, in turn, before the routes are consulted (a 429 first, say).</summary>
    public Queue<Func<HttpResponseMessage>> Before { get; } = new();

    /// <summary>Every request: its full address and its Authorization header.</summary>
    public List<(Uri Uri, string? Authorization)> Requests { get; } = [];

    /// <summary>Makes every request wait this long first (to test time limits).</summary>
    public TimeSpan Stall { get; set; }

    public static FakePlanningCenter WithFixtures()
    {
        var fake = new FakePlanningCenter();
        fake.Serve("/services/v2/", "organization.json");
        fake.Serve("/services/v2/service_types?order=sequence&per_page=100", "service_types.json");
        fake.Serve(Plans + "?filter=future&order=sort_date&per_page=25", "plans_future.json");
        fake.Serve(Plans + "?filter=future&offset=2&order=sort_date&per_page=2", "plans_future_page2.json");
        fake.Serve(Plans + "?filter=past&order=-sort_date&per_page=2", "plans_past.json");
        fake.Serve(Plans + "/80000001", "plan.json");
        fake.Serve(Items, "items.json");
        fake.Serve(Items + "&offset=6", "items_page2.json");
        return fake;
    }

    /// <summary>The plan as it reads a week later (UpdatedPlan in the fixtures): one page of items.</summary>
    public void UseUpdatedPlan()
    {
        Serve(Items, "items_updated.json");
        Routes.Remove(Items + "&offset=6");
    }

    public void Serve(string pathAndQuery, string fixture) => Routes[pathAndQuery] = () => Json(Fixture(fixture));

    public PlanningCenterClient Client(Action<string>? log = null, IProgress<string>? progress = null, TimeSpan? timeout = null) =>
        new(Credentials, this, log: log)
        {
            Delay = (wait, _) =>
            {
                Waits.Add(wait);
                return Task.CompletedTask;
            },
            Progress = progress,
            Timeout = timeout ?? PlanningCenterClient.DefaultTimeout,
        };

    /// <summary>The pauses the client asked for (it never really waits in tests).</summary>
    public List<TimeSpan> Waits { get; } = [];

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage TooManyRequests(int? retryAfterSeconds)
    {
        var response = Json(Fixture("error_429.json"), HttpStatusCode.TooManyRequests);
        if (retryAfterSeconds is { } seconds) response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
        return response;
    }

    public static string Fixture(string name) => File.ReadAllText(Path.Combine(FixtureFolder(), name));

    private static string FixtureFolder([CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "Fixtures");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests) Requests.Add((request.RequestUri!, request.Headers.Authorization?.ToString()));
        if (Stall > TimeSpan.Zero) await Task.Delay(Stall, cancellationToken);
        if (Before.TryDequeue(out var first)) return first();
        var key = request.RequestUri!.PathAndQuery;
        return Routes.TryGetValue(key, out var answer)
            ? answer()
            : Json("""{"errors":[{"status":"404","title":"Not Found","detail":"The resource you requested could not be found"}]}""", HttpStatusCode.NotFound);
    }
}
