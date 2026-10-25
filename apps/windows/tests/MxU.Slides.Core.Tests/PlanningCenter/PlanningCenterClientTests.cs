using System.Net;
using System.Text;
using MxU.Slides.Core.PlanningCenter;

namespace MxU.Slides.Core.Tests.PlanningCenter;

public sealed class PlanningCenterClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReadsTheOrganizationAndServiceTypes()
    {
        var fake = FakePlanningCenter.WithFixtures();
        using var client = fake.Client();
        Assert.Equal("Grace Community Church", await client.OrganizationNameAsync(Ct));
        var types = await client.ServiceTypesAsync(Ct);
        Assert.Equal([("1100001", "Sunday Morning", "Weekly"), ("1100002", "Youth Night", "Weekly")],
            types.Select(t => (t.Id, t.Name, t.Frequency)));
    }

    [Fact]
    public async Task ReadsUpcomingAndRecentPlans()
    {
        var fake = FakePlanningCenter.WithFixtures();
        using var client = fake.Client();
        var (upcoming, recent) = await client.PlansAsync("1100001", Ct);
        Assert.Equal(["80000001", "80000002"], upcoming.Select(p => p.Id));
        var first = upcoming[0];
        Assert.Equal("Week 3: Grace", first.Title);
        Assert.Equal("November 1, 2026", first.Dates);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 15, 0, 0, TimeSpan.Zero), first.SortDate);
        Assert.Equal(10, first.ItemsCount);
        // An untitled plan shows its series.
        Assert.Null(upcoming[1].Title);
        Assert.Equal("Rooted", upcoming[1].DisplayTitle);
        Assert.Equal("Week 2: Roots", Assert.Single(recent).Title);
    }

    [Fact]
    public async Task FollowsNextPageLinks()
    {
        var fake = FakePlanningCenter.WithFixtures();
        using var client = fake.Client();
        var all = await client.GetAllAsync("service_types/1100001/plans?filter=future&order=sort_date&per_page=25", Ct);
        Assert.Equal(["80000001", "80000002", "80000003"], all.Data.Select(r => r.Id));
        Assert.Null(all.Next);
    }

    [Fact]
    public async Task ReadsAPlanWithEveryItemAcrossPagesAndWhatEachPointsAt()
    {
        var fake = FakePlanningCenter.WithFixtures();
        using var client = fake.Client();
        var plan = await client.PlanAsync("1100001", "80000001", "Sunday Morning", Ct);
        Assert.Equal("Week 3: Grace", plan.Plan.Title);
        Assert.Equal(10, plan.Items.Count);
        Assert.Equal(Enumerable.Range(1, 10), plan.Items.Select(i => i.Sequence));
        Assert.Equal(
            [PcoItemType.Header, PcoItemType.Media, PcoItemType.Item, PcoItemType.Header, PcoItemType.Song,
             PcoItemType.Song, PcoItemType.Song, PcoItemType.Song, PcoItemType.Item, PcoItemType.Song],
            plan.Items.Select(i => i.Type));

        var morning = plan.Items[4];
        Assert.Equal(312, morning.LengthSeconds);
        Assert.Equal("G", morning.KeyName);
        Assert.Equal(new PcoSong("30000001", "Morning Light", "Sam Rivers and Ada Moss", 9000101, "2024 Example Music Co."), morning.Song);
        Assert.Equal("Default Arrangement", morning.Arrangement!.Name);
        Assert.Equal(["Verse 1", "Chorus 1", "Verse 2", "Chorus", "Bridge", "Chorus"], morning.Arrangement.Sequence);
        Assert.StartsWith("Verse 1\nHere in the morning light", morning.Arrangement.Lyrics);
        Assert.Equal("G", morning.Key!.StartingKey);
        Assert.Equal([new PcoItemNote("Audio", "Acoustic guitar starts alone")], morning.Notes);

        Assert.Equal(["5 Minute Countdown (Dark)"], plan.Items[1].MediaTitles);
        Assert.Equal("Pastor Dan", plan.Items[2].Description);
        // The second page's included songs are found too.
        var steady = plan.Items[9];
        Assert.Equal("Steady Ground", steady.Song!.Title);
        Assert.True(steady.Arrangement!.HasChords);
        Assert.Null(steady.Arrangement.Lyrics);
        Assert.Equal(["V1", "C", "V1", "C"], steady.Arrangement.Sequence);
        Assert.Null(plan.Items[6].Song!.CcliNumber);
    }

    [Fact]
    public async Task WaitsAsLongAsPlanningCenterAsksWhenItSaysTooManyRequests()
    {
        var fake = FakePlanningCenter.WithFixtures();
        fake.Before.Enqueue(() => FakePlanningCenter.TooManyRequests(retryAfterSeconds: 7));
        var said = new List<string>();
        using var client = fake.Client(progress: new SyncProgress(said.Add));
        var types = await client.ServiceTypesAsync(Ct);
        Assert.Equal(2, types.Count);
        Assert.Equal([TimeSpan.FromSeconds(7)], fake.Waits);
        Assert.Equal(2, fake.Requests.Count);
        Assert.Contains(said, s => s.Contains("waiting 7 s"));
    }

    [Fact]
    public async Task WithoutRetryAfterItBacksOffAndGivesUpAfterThreeTries()
    {
        var fake = FakePlanningCenter.WithFixtures();
        for (var i = 0; i < 3; i++) fake.Before.Enqueue(() => FakePlanningCenter.TooManyRequests(retryAfterSeconds: null));
        using var client = fake.Client();
        var error = await Assert.ThrowsAsync<PlanningCenterException>(() => client.ServiceTypesAsync(Ct));
        Assert.Equal(PlanningCenterProblem.RateLimited, error.Problem);
        Assert.Contains("slow down", error.Message);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], fake.Waits);
        Assert.Equal(3, fake.Requests.Count);
    }

    [Fact]
    public async Task AWaitLongerThanAMinuteIsReportedInsteadOfHangingTheImport()
    {
        var fake = FakePlanningCenter.WithFixtures();
        fake.Before.Enqueue(() => FakePlanningCenter.TooManyRequests(retryAfterSeconds: 600));
        using var client = fake.Client();
        var error = await Assert.ThrowsAsync<PlanningCenterException>(() => client.ServiceTypesAsync(Ct));
        Assert.Equal(PlanningCenterProblem.RateLimited, error.Problem);
        Assert.Empty(fake.Waits);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, PlanningCenterProblem.Unauthorized, "Application ID and Secret")]
    [InlineData(HttpStatusCode.Forbidden, PlanningCenterProblem.Forbidden, "access to Services")]
    [InlineData(HttpStatusCode.NotFound, PlanningCenterProblem.NotFound, "couldn't find")]
    [InlineData(HttpStatusCode.Found, PlanningCenterProblem.BadResponse, "credentials stay with Planning Center")]
    [InlineData((HttpStatusCode)422, PlanningCenterProblem.BadResponse, "Unprocessable: bad filter")]
    public async Task ErrorsBecomeClearMessages(HttpStatusCode status, PlanningCenterProblem problem, string words)
    {
        var fake = new FakePlanningCenter();
        var body = status == (HttpStatusCode)422
            ? """{"errors":[{"status":"422","title":"Unprocessable","detail":"bad filter"}]}"""
            : FakePlanningCenter.Fixture("error_401.json");
        fake.Before.Enqueue(() => FakePlanningCenter.Json(body, status));
        using var client = fake.Client();
        var error = await Assert.ThrowsAsync<PlanningCenterException>(() => client.ServiceTypesAsync(Ct));
        Assert.Equal(problem, error.Problem);
        Assert.Contains(words, error.Message);
    }

    [Fact]
    public async Task AServerErrorIsTriedOnceMoreThenReported()
    {
        var fake = new FakePlanningCenter();
        fake.Before.Enqueue(() => FakePlanningCenter.Json("{}", HttpStatusCode.ServiceUnavailable));
        fake.Before.Enqueue(() => FakePlanningCenter.Json("{}", HttpStatusCode.ServiceUnavailable));
        using var client = fake.Client();
        var error = await Assert.ThrowsAsync<PlanningCenterException>(() => client.ServiceTypesAsync(Ct));
        Assert.Equal(PlanningCenterProblem.Server, error.Problem);
        Assert.Contains("error 503", error.Message);
        Assert.Equal(2, fake.Requests.Count);
    }

    [Fact]
    public async Task NoNetworkAndNoAnswerAreToldApart()
    {
        var offline = new FakePlanningCenter();
        offline.Before.Enqueue(() => throw new HttpRequestException("No such host is known.", null, HttpStatusCode.ServiceUnavailable));
        using (var client = offline.Client())
            Assert.Equal(PlanningCenterProblem.Network, (await Assert.ThrowsAsync<PlanningCenterException>(() => client.ServiceTypesAsync(Ct))).Problem);

        var stalled = FakePlanningCenter.WithFixtures();
        stalled.Stall = TimeSpan.FromSeconds(30);
        using (var client = stalled.Client(timeout: TimeSpan.FromMilliseconds(200)))
        {
            var error = await Assert.ThrowsAsync<PlanningCenterException>(() => client.ServiceTypesAsync(Ct));
            Assert.Equal(PlanningCenterProblem.Timeout, error.Problem);
            Assert.Contains("took too long", error.Message);
        }
    }

    [Fact]
    public async Task AnAnswerThatIsntJsonApiIsABadResponse()
    {
        var fake = new FakePlanningCenter();
        fake.Before.Enqueue(() => FakePlanningCenter.Json("<html>maintenance</html>"));
        using var client = fake.Client();
        Assert.Equal(PlanningCenterProblem.BadResponse, (await Assert.ThrowsAsync<PlanningCenterException>(() => client.ServiceTypesAsync(Ct))).Problem);
    }

    [Fact]
    public async Task ANextPageOnAnotherSiteIsNeverRequested()
    {
        var fake = new FakePlanningCenter();
        var page = FakePlanningCenter.Fixture("service_types.json").Replace(
            "\"self\": \"https://api.planningcenteronline.com/services/v2/service_types?order=sequence&per_page=100\"",
            "\"self\": \"x\", \"next\": \"https://collector.example/services/v2/service_types?offset=2\"");
        fake.Routes["/services/v2/service_types?order=sequence&per_page=100"] = () => FakePlanningCenter.Json(page);
        using var client = fake.Client();
        var error = await Assert.ThrowsAsync<PlanningCenterException>(() => client.ServiceTypesAsync(Ct));
        Assert.Equal(PlanningCenterProblem.BadResponse, error.Problem);
        Assert.All(fake.Requests, r => Assert.Equal("api.planningcenteronline.com", r.Uri.Host));
    }

    [Fact]
    public async Task SecretsGoOnlyInTheAuthorizationHeaderNeverInAddressesLogsOrMessages()
    {
        var fake = FakePlanningCenter.WithFixtures();
        var log = new List<string>();
        using (var client = fake.Client(log.Add))
        {
            await client.OrganizationNameAsync(Ct);
            await client.PlanAsync("1100001", "80000001", "Sunday Morning", Ct);
            fake.Before.Enqueue(() => FakePlanningCenter.Json(FakePlanningCenter.Fixture("error_401.json"), HttpStatusCode.Unauthorized));
            var error = await Assert.ThrowsAsync<PlanningCenterException>(() => client.ServiceTypesAsync(Ct));
            log.Add(error.ToString());
        }
        var credentials = FakePlanningCenter.Credentials;
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credentials.ApplicationId}:{credentials.Secret}"));
        string[] secrets = [credentials.ApplicationId, credentials.Secret, basic];

        Assert.NotEmpty(log);
        Assert.All(fake.Requests, r =>
        {
            Assert.Equal($"Basic {basic}", r.Authorization);
            foreach (var secret in secrets) Assert.DoesNotContain(secret, r.Uri.ToString());
        });
        foreach (var line in log)
            foreach (var secret in secrets)
                Assert.DoesNotContain(secret, line);
        // Printed by mistake, the credentials say nothing.
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, credentials.ToString());
            Assert.DoesNotContain(secret, $"{new { credentials }}");
        }
    }

    [Fact]
    public async Task IdsThatArentPlanningCenterIdsNeverReachARequest()
    {
        var fake = FakePlanningCenter.WithFixtures();
        using var client = fake.Client();
        await Assert.ThrowsAsync<PlanningCenterException>(() => client.PlanAsync("1100001", "../../people/v2", "x", Ct));
        await Assert.ThrowsAsync<PlanningCenterException>(() => client.PlansAsync("1100001?x=1", Ct));
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public void APlansDateIsTheChurchsLocalDate()
    {
        var evening = new PcoPlan("1", "2", "Evening", null, null, new DateTimeOffset(2026, 11, 2, 1, 0, 0, TimeSpan.Zero), 0);
        var chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        Assert.Equal("2026-11-01", evening.LocalDate(chicago)); // 7 pm on Sunday, Chicago time
        Assert.Equal("2026-11-02", evening.LocalDate(TimeZoneInfo.Utc));
        Assert.Null((evening with { SortDate = null }).LocalDate(chicago));
    }

    /// <summary>Progress&lt;T&gt; reports on the thread pool; this reports straight away, so the test can read it.</summary>
    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
