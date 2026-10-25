using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.PlanningCenter;
using MxU.Slides.LocalApi;
using MxU.Slides.Windows.Views;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>
/// Phase 6, Connect: Planning Center import and the browser remote, driven as an operator would.
/// <list type="bullet">
/// <item>Planning Center is played by a fake (<see cref="FakePlanningCenter"/>) that answers like
/// Services v2 does, including one "slow down" (429). Nothing goes near the internet. The token is
/// connected through Settings' own path, kept encrypted, and checked to be in no log; a plan is
/// imported from the import window, then imported again after the "planner" changed it and the
/// operator renamed a row.</item>
/// <item>The remote is used over real HTTP and a real WebSocket on this PC: the pages, pairing with
/// the code Settings shows, the service, a thumbnail (saved as a picture), going live, the stage
/// message, and a View key that can't control.</item>
/// </list>
/// Pictures 60-… to 66-….
/// </summary>
public sealed partial class SelfTest
{
    private async Task ConnectCheck()
    {
        await PlanningCenterCheck();
        await RemoteCheck();
    }

    // MARK: Planning Center

    private async Task PlanningCenterCheck()
    {
        var fake = new FakePlanningCenter();
        model.PlanningCenterHandler = () => fake;
        model.PlanningCenterBase = FakePlanningCenter.Base;
        string? serviceId = null;
        var songsBefore = model.Library.All<Presentation>().Select(p => p.Id).ToHashSet();
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var church = await model.ConnectPlanningCenterAsync(FakePlanningCenter.AppId, FakePlanningCenter.Secret, limit.Token);
            Check("Planning Center connects with a Personal Access Token", church == "Self-test Church" && model.PlanningCenterConnected);
            var vault = File.ReadAllBytes(PlanningCenterVault.DefaultPath);
            Check("the token is kept encrypted (not readable in the file)", !Contains(vault, FakePlanningCenter.Secret) && !Contains(vault, FakePlanningCenter.AppId));

            var settings = new SettingsWindow(model, model.Outputs!) { Owner = window };
            settings.Show();
            await Delay(700);
            if (Descendant<ConnectSettingsSection>(settings) is { } section) SavePart(section, "60-connect-settings-planning-center");
            Check("Settings has the Planning Center and browser remote section", Descendant<ConnectSettingsSection>(settings) is not null);
            settings.Close();

            var picker = PlanningCenterWindow.Open(window, model);
            await Delay(300);
            await picker.Loading;
            await picker.LoadingPlans;
            await Delay(300);
            Save(picker, "61-planning-center-plans");
            Check("the import window lists the service types and upcoming plans", picker.Select(FakePlanningCenter.PlanId));
            Check("a 429 from Planning Center is waited out, as Retry-After asks", fake.Count("service_types?") == 2);
            var summary = await picker.ImportSelectedAsync();
            await Delay(600);
            serviceId = summary?.ServiceId;
            var service = model.Library.Get<Service>(serviceId);
            var planDate = new DateTimeOffset(2026, 11, 1, 11, 0, 0, TimeSpan.Zero).ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            Check("a plan imports as a service and opens", service is not null && model.SelectedService?.Id == service.Id
                && service.Name == "Sunday Morning · Self-test Sunday" && service.ServiceDate == planDate);
            Check("its rows: header, media to add, a song from the library, a new song, an item",
                service?.Items.Select(i => i.ItemKind).SequenceEqual(
                    [ServiceItemKind.Header, ServiceItemKind.Info, ServiceItemKind.Presentation, ServiceItemKind.Presentation, ServiceItemKind.Info]) == true);
            Check("item lengths become durations", service?.Items.Select(i => i.Duration).SequenceEqual([null, 300, 280, 250, 1500]) == true);
            Check("a song the library has is used (by title)", model.Library.Get<Presentation>(service?.Items[2].RefId) is { Name: "Amazing Grace" } used
                && songsBefore.Contains(used.Id));
            var made = model.Library.Get<Presentation>(service?.Items[3].RefId);
            Check("a song the library lacks is made from Planning Center's lyrics, with its sequence as an arrangement",
                made is { Folder: PlanSongMaker.Folder } && made.Arrangements?.Any(a => a.Name == "Default Arrangement" && a.SectionIds.Count == 4) == true
                && made.Origin?.Source == PresentationOriginSource.PlanningCenterLyrics);
            Check("the media item is listed as to add", model.Notice?.Contains("media to add: Walk-in Loop") == true
                && model.ServiceItems.Any(r => r.Detail?.StartsWith("Media to add", StringComparison.Ordinal) == true));
            await Step("62-planning-center-service", () => { }, settle: 400);
            if (made is not null)
            {
                model.SelectedServiceItem = model.ServiceItems.FirstOrDefault(r => r.Item.RefId == made.Id);
                await Step("63-planning-center-new-song", () => { }, settle: 600);
                Check("the new song's slides show its words", model.Slides.Any(s => s.Slide.Objects.Any(o => o.Text.Contains("Morning light is breaking"))));
            }

            // The planner changes the plan; the operator renames a row here. Importing again keeps both.
            if (service is not null)
            {
                var edited = model.Library.Get<Service>(service.Id)!;
                edited.Items[4].Name = "Sermon";
                model.Library.Save(edited);
                fake.Updated = true;
                var again = await model.ImportPlanAsync("1100001", FakePlanningCenter.PlanId, "Sunday Morning", null, limit.Token);
                var after = model.Library.Get<Service>(service.Id);
                Check("importing again updates the same service (no copy)", again.ServiceId == service.Id && again.Updated
                    && model.Library.All<Service>().Count(s => s.Name.StartsWith("Sunday Morning", StringComparison.Ordinal)) == 1);
                Check("…adds the planner's new item and keeps the operator's rename",
                    after?.Items.Any(i => i.Name == "Closing Prayer") == true && after.Items[4].Name == "Sermon" && after.Items[4].Duration == 1800);
            }

            var logs = Directory.Exists(AppPaths.LogDirectory)
                ? string.Concat(Directory.EnumerateFiles(AppPaths.LogDirectory).Select(ReadShared))
                : "";
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{FakePlanningCenter.AppId}:{FakePlanningCenter.Secret}"));
            Check("the token is never in a log line or a request address",
                logs.Contains("Planning Center: GET") && !logs.Contains(FakePlanningCenter.Secret) && !logs.Contains(basic)
                && fake.Requests.All(r => !r.Uri.Contains(FakePlanningCenter.Secret) && r.Authorization == $"Basic {basic}"));
        }
        catch (Exception ex)
        {
            Check($"Planning Center import works ({ex.Message})", false);
        }
        finally
        {
            model.DisconnectPlanningCenter();
            Check("disconnecting removes the token", !model.PlanningCenterConnected && !File.Exists(PlanningCenterVault.DefaultPath));
            model.PlanningCenterHandler = null;
            model.PlanningCenterBase = null;
            if (serviceId is not null) model.Library.Delete<Service>(serviceId);
            foreach (var song in model.Library.All<Presentation>().Where(p => !songsBefore.Contains(p.Id)).ToList()) model.Library.Delete<Presentation>(song.Id);
            model.SelectedService = model.Services.FirstOrDefault(s => s.Name == "Getting Started") ?? model.SelectedService;
        }
    }

    private static string ReadShared(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return "";
        }
    }

    private static bool Contains(byte[] haystack, string text) => haystack.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0;

    // MARK: The browser remote

    private async Task RemoteCheck()
    {
        var s = model.Settings;
        var (wasOn, wasPort) = (s.LocalApiEnabled, s.LocalApiPort);
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        s.LocalApiEnabled = true;
        s.LocalApiPort = port;
        model.ApplyApiSettings();
        var paired = new List<ApiToken>();
        void OnPaired(ApiToken token) => paired.Add(token);
        model.RemotePaired += OnPaired;
        var timerId = model.Timers.AddCountdown("Self-test sermon", 1500);
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
            var page = await http.GetAsync("/remote");
            Check("the browser remote's page is served at /remote", page.IsSuccessStatusCode
                && (await page.Content.ReadAsStringAsync()).Contains("/remote/assets/remote.js"));
            Check("the stage view is served at /remote/stage", (await http.GetAsync("/remote/stage")).IsSuccessStatusCode);
            Check("without a key the remote gets nothing", (await http.GetAsync("/remote/service")).StatusCode == HttpStatusCode.Unauthorized);

            // Pair as Settings does: press Pair a phone, then the phone types the code it shows.
            var offer = model.BeginRemotePairing(ApiScope.Control);
            var settings = new SettingsWindow(model, model.Outputs!) { Owner = window };
            settings.Show();
            await Delay(700);
            var section = Descendant<ConnectSettingsSection>(settings);
            Check("Settings shows the address, a QR code and the pairing code", section is not null && offer is not null
                && Descendants<System.Windows.Controls.Image>(section).Any()
                && Descendants<System.Windows.Controls.TextBlock>(section).Any(t => t.Text == $"{offer.Code[..3]} {offer.Code[3..]}"));
            if (section is not null) SavePart(section, "64-connect-settings-remote");
            settings.Close();

            var control = await PairAsync(http, offer?.Code ?? "");
            await Delay(200);
            Check("a phone pairs with the code and gets a Control key", control is not null && paired.Any(t => t.Scope == ApiScope.Control)
                && model.ApiTokens.All.Any(t => t.Name == "Browser remote: Self-test phone"));
            http.DefaultRequestHeaders.Authorization = new("Bearer", control ?? "");

            var song = model.Library.All<Presentation>().First(p => p.Name == "Amazing Grace");
            var doc = JsonNode.Parse(await http.GetStringAsync("/remote/service"))!;
            var row = doc["service"]?["items"]?.AsArray().FirstOrDefault(i => (string?)i?["presentation"]?["id"] == song.Id);
            Check("the remote gets the service with each song's slides", (string?)doc["service"]?["service"]?["name"] == "Getting Started"
                && row?["presentation"]?["slides"]?.AsArray().Count >= 5);

            var slideId = (string?)row?["presentation"]?["slides"]?[1]?["id"];
            var thumbnail = await http.GetAsync($"/remote/thumbnails/{song.Id}/{slideId}?width=320");
            var png = await thumbnail.Content.ReadAsByteArrayAsync();
            Check("a slide's thumbnail comes as a PNG", thumbnail.IsSuccessStatusCode && png.Length > 1000 && png.AsSpan().StartsWith((byte[])[0x89, 0x50, 0x4E, 0x47]));
            await File.WriteAllBytesAsync(Path.Combine(folder, "65-remote-thumbnail.png"), png);

            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/remote/ws"), CancellationToken.None);
            await socket.SendAsync(Encoding.UTF8.GetBytes($$"""{"type":"auth","token":"{{control}}"}"""), WebSocketMessageType.Text, true, CancellationToken.None);
            await socket.SendAsync("""{"type":"subscribe","topics":["show"]}"""u8.ToArray(), WebSocketMessageType.Text, true, CancellationToken.None);
            var frames = new List<string>();
            var reading = ReadRemoteFrames(socket, frames);
            await Delay(300);
            var rowId = (string?)row?["id"];
            var fired = await http.PostAsync("/v1/show/slide", new StringContent($$"""{"serviceItemId":"{{rowId}}","occurrence":1}""", Encoding.UTF8, "application/json"));
            await Delay(700);
            Check("tapping a slide on the remote puts it live", fired.IsSuccessStatusCode && AudienceText().Contains("Amazing grace", StringComparison.OrdinalIgnoreCase));
            lock (frames)
                Check("the remote's socket pushes the live slide with its row and place",
                    frames.Any(f => f.Contains("\"topic\":\"show\"") && f.Contains($"\"serviceItemId\":\"{rowId}\"") && f.Contains("\"occurrence\":1")));

            var stage = await http.PostAsync("/remote/stage-message", new StringContent("""{"text":"Two minutes"}""", Encoding.UTF8, "application/json"));
            await Delay(400);
            Check("the remote sends the stage message", stage.IsSuccessStatusCode && StageText().Contains("Two minutes") && model.StageMessageStatus == "On stage: Two minutes");
            await Snapshot("66-remote-stage-message", model.StageScene());
            await http.PostAsync($"/v1/timers/{timerId}/play", null);
            Check("the remote starts a timer", model.Timers.Snapshot(timerId)?.IsRunning == true);
            await http.PostAsync("/remote/stage-message", new StringContent("""{"text":""}""", Encoding.UTF8, "application/json"));
            await Delay(300);
            Check("…and takes the stage message down", model.StageMessageStatus is null);

            var viewOffer = model.BeginRemotePairing(ApiScope.View);
            var view = await PairAsync(http, viewOffer?.Code ?? "");
            using var viewer = new HttpClient { BaseAddress = http.BaseAddress, Timeout = http.Timeout };
            viewer.DefaultRequestHeaders.Authorization = new("Bearer", view ?? "");
            Check("a View pairing can read but not control",
                (await viewer.GetAsync("/remote/service")).IsSuccessStatusCode
                && (await viewer.PostAsync("/v1/show/advance", new StringContent("""{"steps":1}""", Encoding.UTF8, "application/json"))).StatusCode == HttpStatusCode.Forbidden
                && (await viewer.PostAsync("/remote/stage-message", new StringContent("""{"text":"x"}""", Encoding.UTF8, "application/json"))).StatusCode == HttpStatusCode.Forbidden);

            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            await Task.WhenAny(reading, Task.Delay(2000));
        }
        catch (Exception ex)
        {
            Check($"the browser remote works ({ex.Message})", false);
        }
        finally
        {
            model.RemotePaired -= OnPaired;
            model.CancelRemotePairing();
            foreach (var token in model.ApiTokens.All.Where(t => t.Name.StartsWith("Browser remote: Self-test", StringComparison.Ordinal)).ToList())
                model.ApiTokens.Revoke(token.Id);
            model.Timers.Remove(timerId);
            model.ClearAllCommand.Execute(null);
            s.LocalApiEnabled = wasOn;
            s.LocalApiPort = wasPort;
            model.ApplyApiSettings();
        }

        static async Task<string?> PairAsync(HttpClient http, string code)
        {
            var answer = await http.PostAsync("/remote/pair",
                new StringContent($$"""{"code":"{{code}}","name":"Self-test phone"}""", Encoding.UTF8, "application/json"));
            return answer.IsSuccessStatusCode ? (string?)JsonNode.Parse(await answer.Content.ReadAsStringAsync())?["key"] : null;
        }

        static async Task ReadRemoteFrames(ClientWebSocket socket, List<string> frames)
        {
            var buffer = new byte[256 * 1024];
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    lock (frames) frames.Add(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
            }
            catch (WebSocketException)
            {
            }
        }
    }

    /// <summary>A picture of part of a window, even one scrolled out of view (drawn through a VisualBrush).</summary>
    private void SavePart(FrameworkElement element, string name)
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(element);
            var (width, height) = (element.ActualWidth, element.ActualHeight);
            if (width < 1 || height < 1) return;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(EditorKit.Res<Brush>("BasePlane"), null, new Rect(0, 0, width, height));
                dc.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                    null, new Rect(0, 0, width, height));
            }
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * dpi.DpiScaleX), (int)Math.Ceiling(height * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            Write(bitmap, name);
        }
        catch (Exception ex)
        {
            report.AppendLine($"NOTE  couldn't capture {name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Planning Center Services, played back: answers like api.planningcenteronline.com/services/v2
    /// for one church, one service type and one plan (and that plan a week later, once
    /// <see cref="Updated"/>). The first service-types request is told to slow down (429).
    /// </summary>
    private sealed class FakePlanningCenter : HttpMessageHandler
    {
        public const string AppId = "selftest-application-id-0001";
        public const string Secret = "selftest-pat-secret-do-not-log";
        public const string PlanId = "80000001";

        // A host that can't exist, so even a mistake couldn't reach a real server.
        public static readonly Uri Base = new("https://planning-center.selftest.invalid/services/v2/");

        private bool slowedDown;

        public bool Updated { get; set; }

        public List<(string Uri, string? Authorization)> Requests { get; } = [];

        public int Count(string part)
        {
            lock (Requests) return Requests.Count(r => r.Uri.Contains(part, StringComparison.Ordinal));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            lock (Requests) Requests.Add((request.RequestUri.ToString(), request.Headers.Authorization?.ToString()));
            if (path.StartsWith("/services/v2/service_types?", StringComparison.Ordinal) && !slowedDown)
            {
                slowedDown = true;
                var busy = Answer("""{"errors":[{"status":"429","title":"Rate Limit Exceeded"}]}""", HttpStatusCode.TooManyRequests);
                busy.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
                return Task.FromResult(busy);
            }
            JsonNode? body = path switch
            {
                "/services/v2/" => Data(Resource("Organization", "1", new() { ["name"] = "Self-test Church" })),
                _ when path.StartsWith("/services/v2/service_types?", StringComparison.Ordinal) =>
                    Data(new JsonArray(Resource("ServiceType", "1100001", new() { ["name"] = "Sunday Morning", ["frequency"] = "Weekly" }))),
                _ when path.Contains("/plans?filter=future", StringComparison.Ordinal) => Data(new JsonArray(Plan())),
                _ when path.Contains("/plans?filter=past", StringComparison.Ordinal) => Data(new JsonArray()),
                "/services/v2/service_types/1100001/plans/80000001" => Data(Plan()),
                _ when path.StartsWith("/services/v2/service_types/1100001/plans/80000001/items?", StringComparison.Ordinal) => Items(Updated),
                _ => null,
            };
            return Task.FromResult(body is null
                ? Answer("""{"errors":[{"status":"404","title":"Not Found"}]}""", HttpStatusCode.NotFound)
                : Answer(body.ToJsonString(), HttpStatusCode.OK));
        }

        // JSON:API shapes: {"data": …}, and each resource {"type","id","attributes","relationships"}.
        private static JsonObject Data(JsonNode data, JsonArray? included = null) => new() { ["data"] = data, ["included"] = included ?? [] };

        private static JsonObject Resource(string type, string id, JsonObject attributes, JsonObject? relationships = null) =>
            new() { ["type"] = type, ["id"] = id, ["attributes"] = attributes, ["relationships"] = relationships ?? [] };

        private static JsonObject To(string type, string? id) => new() { ["data"] = id is null ? null : new JsonObject { ["type"] = type, ["id"] = id } };

        private static JsonObject Plan() => Resource("Plan", PlanId,
            new() { ["title"] = "Self-test Sunday", ["series_title"] = "Rooted", ["dates"] = "November 1, 2026", ["sort_date"] = "2026-11-01T11:00:00Z", ["items_count"] = 5 },
            new() { ["service_type"] = To("ServiceType", "1100001") });

        private static JsonObject Items(bool updated)
        {
            static JsonObject Item(string id, int sequence, string title, string type, int? length, string? song = null, string? arrangement = null) =>
                Resource("Item", id,
                    new() { ["title"] = title, ["sequence"] = sequence, ["item_type"] = type, ["length"] = length, ["service_position"] = "during" },
                    new()
                    {
                        ["song"] = To("Song", song),
                        ["arrangement"] = To("Arrangement", arrangement),
                        ["key"] = To("Key", null),
                        ["media"] = new JsonObject { ["data"] = type == "media" ? new JsonArray(new JsonObject { ["type"] = "Media", ["id"] = "600000001" }) : new JsonArray() },
                        ["item_notes"] = new JsonObject { ["data"] = new JsonArray() },
                    });
            var items = new JsonArray(
                Item("500000001", 1, "Worship", "header", null),
                Item("500000002", 2, "Walk-in", "media", 300),
                Item("500000003", 3, "Amazing Grace", "song", 280, "30000001", "40000001"),
                Item("500000004", 4, "Morning Light", "song", 250, "30000002", "40000002"),
                Item("500000005", 5, "Message", "item", updated ? 1800 : 1500));
            if (updated) items.Add(Item("500000006", 6, "Closing Prayer", "item", 120));
            var included = new JsonArray(
                Resource("Media", "600000001", new() { ["title"] = "Walk-in Loop", ["media_type"] = "background_video" }),
                Resource("Song", "30000001", new() { ["title"] = "Amazing Grace", ["author"] = "John Newton", ["ccli_number"] = null }),
                Resource("Arrangement", "40000001", new() { ["name"] = "Hymn", ["lyrics"] = null, ["sequence"] = new JsonArray() }),
                Resource("Song", "30000002", new() { ["title"] = "Morning Light", ["author"] = "Sam Rivers", ["ccli_number"] = 9000101 }),
                Resource("Arrangement", "40000002", new()
                {
                    ["name"] = "Default Arrangement",
                    ["lyrics"] = "Verse 1\nHere in the morning light\nWe lift our eyes to see\n\nChorus\nSing, sing it out\nMorning light is breaking now",
                    ["sequence"] = new JsonArray("Verse 1", "Chorus", "Verse 1", "Chorus"),
                    ["length"] = 250,
                }));
            return Data(items, included);
        }

        private static HttpResponseMessage Answer(string json, HttpStatusCode status) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
