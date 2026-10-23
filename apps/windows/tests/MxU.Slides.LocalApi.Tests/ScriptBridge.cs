using System.Text.Json.Nodes;

namespace MxU.Slides.LocalApi.Tests;

/// <summary>
/// A small working show for the repo's example clients (scripts/*.mjs): documents kept as
/// JSON, services with run orders, a live slide that fires and advances like the Mac
/// adapter's, and an alert. Raises <see cref="Changed"/> so the host can publish events.
/// </summary>
internal sealed class ScriptBridge : IApiBridge
{
    private readonly Lock gate = new();
    private readonly Dictionary<ApiDocumentKind, Dictionary<string, JsonObject>> documents =
        ApiDocumentKinds.All.ToDictionary(k => k, _ => new Dictionary<string, JsonObject>());
    private string? currentServiceId;
    private Live? live;
    private ApiLiveAlert? alert;

    private sealed record Live(string? ItemId, string PresentationId, int Occurrence);

    private sealed record Position(string ItemId, string PresentationId, int Occurrence);

    public event Action<ApiTopic>? Changed;

    private void Notify(ApiTopic topic) => Changed?.Invoke(topic);

    // MARK: Library and documents

    public ApiLibrarySummary LibrarySummary()
    {
        lock (gate) return new(documents.ToDictionary(d => d.Key.Raw(), d => d.Value.Count));
    }

    public IReadOnlyList<ApiLibraryEntry> LibraryEntries(ApiDocumentKind kind)
    {
        lock (gate)
            return documents[kind].Values
                .Select(d => new ApiLibraryEntry((string)d["id"]!, (string?)d["name"] ?? "", kind.Raw(), null, null))
                .ToList();
    }

    public Task<byte[]> DocumentJsonAsync(ApiDocumentKind kind, string id)
    {
        lock (gate)
            return documents[kind].TryGetValue(ApiIds.Validated(id), out var document)
                ? Task.FromResult(ApiJson.Encoded(document))
                : throw ApiError.NotFound($"No document with id '{id}'.");
    }

    public Task<string> CreateDocumentAsync(ApiDocumentKind kind, byte[] body)
    {
        if (Parse(body) is not { } document) throw ApiError.BadRequest("Body must be a JSON object (the document).");
        var id = (string?)document["id"] is { Length: > 0 } supplied ? ApiIds.Validated(supplied) : Guid.NewGuid().ToString().ToUpperInvariant();
        document["id"] = id;
        lock (gate)
        {
            if (documents[kind].ContainsKey(id)) throw new ApiError(409, "conflict", $"A document with id '{id}' exists.");
            documents[kind][id] = document;
        }
        Notify(ApiTopic.Library);
        return Task.FromResult(id);
    }

    public Task UpdateDocumentAsync(ApiDocumentKind kind, string id, byte[] body)
    {
        id = ApiIds.Validated(id);
        if (Parse(body) is not { } document) throw ApiError.BadRequest("Body must be a JSON object (the document).");
        if ((string?)document["id"] != id) throw ApiError.BadRequest($"Body id '{document["id"]}' does not match the path id '{id}'.");
        lock (gate)
        {
            if (!documents[kind].ContainsKey(id)) throw ApiError.NotFound($"No document with id '{id}'.");
            documents[kind][id] = document;
        }
        Notify(ApiTopic.Library);
        return Task.CompletedTask;
    }

    public Task DeleteDocumentAsync(ApiDocumentKind kind, string id)
    {
        id = ApiIds.Validated(id);
        lock (gate)
            if (!documents[kind].Remove(id)) throw ApiError.NotFound($"No document with id '{id}'.");
        Notify(ApiTopic.Library);
        return Task.CompletedTask;
    }

    public void AddServiceItem(string serviceId, string refId, int? index)
    {
        lock (gate)
        {
            if (!documents[ApiDocumentKind.Services].TryGetValue(serviceId, out var service))
                throw ApiError.NotFound($"No service '{serviceId}'.");
            var (kind, entry) = documents
                .Where(d => d.Key is ApiDocumentKind.Presentations or ApiDocumentKind.Media)
                .Select(d => (d.Key, d.Value.GetValueOrDefault(refId)))
                .FirstOrDefault(d => d.Item2 is not null);
            if (entry is null) throw ApiError.NotFound($"No library item '{refId}'.");
            if (service["items"] is not JsonArray items) service["items"] = items = [];
            var item = new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString().ToUpperInvariant(),
                ["itemKind"] = kind == ApiDocumentKind.Media ? "media" : "presentation",
                ["name"] = (string?)entry["name"] ?? "",
                ["refId"] = refId,
            };
            items.Insert(index is { } at && at < items.Count ? Math.Max(0, at) : items.Count, item);
        }
        Notify(ApiTopic.Show);
    }

    public void SelectService(string id)
    {
        lock (gate)
        {
            if (!documents[ApiDocumentKind.Services].ContainsKey(id)) throw ApiError.NotFound($"No service '{id}'.");
            currentServiceId = id;
        }
        Notify(ApiTopic.Show);
    }

    // MARK: Show

    public ApiShowStatus ShowStatus()
    {
        lock (gate)
        {
            ApiLiveSlide? liveSlide = null;
            string? next = null;
            if (live is { } now && Slides(now.PresentationId) is { } slides && now.Occurrence < slides.Count)
            {
                var presentation = documents[ApiDocumentKind.Presentations][now.PresentationId];
                var slide = slides[now.Occurrence];
                liveSlide = new ApiLiveSlide(
                    now.PresentationId, (string?)presentation["name"], (string)slide["id"]!, now.Occurrence, null,
                    now.ItemId, now.Occurrence, Text: Text(slide), SlideCount: slides.Count);
                next = now.Occurrence + 1 < slides.Count ? Text(slides[now.Occurrence + 1]) : null;
            }
            var service = currentServiceId is { } id && documents[ApiDocumentKind.Services].GetValueOrDefault(id) is { } s
                ? new ApiServiceRef(id, (string?)s["name"] ?? "")
                : null;
            return new ApiShowStatus(
                liveSlide, next, new Dictionary<string, ApiLayerContent>(), [], alert, currentServiceId,
                service, new ApiNextSlide(next));
        }
    }

    public Task FireSlideAsync(ApiFireSlideCommand command)
    {
        lock (gate)
        {
            if (command.ServiceItemId is { } itemId)
            {
                var service = documents[ApiDocumentKind.Services].Values.FirstOrDefault(s => Items(s).Any(i => (string?)i["id"] == itemId))
                    ?? throw ApiError.NotFound($"No service contains an item '{itemId}'.");
                var positions = Positions(service).Where(p => p.ItemId == itemId).ToList();
                if (positions.Count == 0) throw ApiError.NotFound($"Service item '{itemId}' has no firable slides.");
                var index = command.Occurrence ?? command.SlideIndex ?? 0;
                if (index < 0 || index >= positions.Count)
                    throw ApiError.BadRequest($"Occurrence {index} is out of range (item has {positions.Count} positions).");
                live = new Live(itemId, positions[index].PresentationId, index);
            }
            else
            {
                if (command.PresentationId is not { } presentationId || Slides(presentationId) is not { } slides)
                    throw ApiError.NotFound("Provide serviceItemId, or a presentationId that exists.");
                var index = command.SlideId is { } slideId
                    ? slides.FindIndex(s => (string?)s["id"] == slideId) is >= 0 and var found
                        ? found
                        : throw ApiError.NotFound($"No slide '{slideId}' in that presentation's arranged order.")
                    : command.SlideIndex ?? 0;
                if (index < 0 || index >= slides.Count)
                    throw ApiError.BadRequest($"Slide index {index} is out of range ({slides.Count} slides).");
                live = new Live(presentationId, presentationId, index);
            }
        }
        Notify(ApiTopic.Show);
        return Task.CompletedTask;
    }

    public Task AdvanceAsync(int steps, bool settled)
    {
        if (steps == 0) return Task.CompletedTask;
        lock (gate)
        {
            var positions = currentServiceId is { } id && documents[ApiDocumentKind.Services].GetValueOrDefault(id) is { } service
                ? Positions(service)
                : [];
            var current = live is { } now ? positions.FindIndex(p => p.ItemId == now.ItemId && p.Occurrence == now.Occurrence) : -1;
            if (current >= 0)
            {
                var next = current + steps;
                if (next < 0 || next >= positions.Count)
                    throw ApiError.BadRequest($"Already at the {(steps > 0 ? "end" : "start")} of the service.");
                live = new Live(positions[next].ItemId, positions[next].PresentationId, positions[next].Occurrence);
            }
            else if (live is { } within && Slides(within.PresentationId) is { } slides)
            {
                var next = within.Occurrence + steps;
                if (next < 0 || next >= slides.Count) throw ApiError.BadRequest($"Already at the {(steps > 0 ? "end" : "start")}.");
                live = within with { Occurrence = next };
            }
            else
            {
                if (positions.Count == 0) throw ApiError.BadRequest("Nothing is live and the current service has no slides.");
                var start = steps > 0 ? positions[0] : positions[^1];
                live = new Live(start.ItemId, start.PresentationId, start.Occurrence);
            }
        }
        Notify(ApiTopic.Show);
        return Task.CompletedTask;
    }

    public void Clear(ApiClearCommand command)
    {
        lock (gate)
        {
            switch (command.Function ?? (command.Layer is { } layer ? "layer:" + layer : null))
            {
                case "slides" or "layer:slide":
                    live = null;
                    break;
                case "alerts" or "layer:alerts":
                    alert = null;
                    break;
                case null:
                    throw ApiError.BadRequest("Provide either 'function' or 'layer'.");
                default:
                    if (command.Function is { } function && function is not ("media" or "overlays" or "audio" or "inputs"))
                        throw ApiError.BadRequest($"Unknown function '{function}'. Functions: slides, media, overlays, audio, alerts, inputs.");
                    break;
            }
        }
        Notify(ApiTopic.Show);
    }

    public void ClearAll()
    {
        lock (gate) (live, alert) = (null, null);
        Notify(ApiTopic.Show);
    }

    public void FireAlert(ApiFireAlertCommand command)
    {
        if (command.PresetId is { } presetId) throw ApiError.NotFound($"No alert preset '{presetId}'.");
        if (command.Message is not { Length: > 0 } message) throw ApiError.BadRequest("Provide 'presetId' or 'message'.");
        lock (gate) alert = new ApiLiveAlert(Guid.NewGuid().ToString().ToUpperInvariant(), message, command.Behavior ?? "persist", command.Target ?? "confidence", null);
        Notify(ApiTopic.Show);
    }

    public void DismissAlert()
    {
        lock (gate) alert = null;
        Notify(ApiTopic.Show);
    }

    public void FireMedia(string id) => throw ApiError.NotFound($"No media item '{id}'.");

    public void FireOverlay(string id) => throw ApiError.NotFound($"No overlay '{id}'.");

    public void DismissOverlay(string id) { }

    // MARK: Timers, transport and outputs

    public IReadOnlyList<ApiTimerStatus> Timers() => [];

    public void TimerAction(string id, ApiTimerAction action) => throw ApiError.NotFound($"No timer '{id}'.");

    public IReadOnlyList<ApiTransportRow> TransportRows() => [];

    public void MediaTransport(string id, ApiMediaTransportCommand command) =>
        throw ApiError.NotFound($"No playing video '{id}'. GET /v1/transport lists them.");

    public ApiOutputsStatus OutputsStatus() => new([], []);

    // MARK: Helpers (call under the lock)

    private static JsonObject? Parse(byte[] body)
    {
        try { return JsonNode.Parse(body) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private List<JsonObject>? Slides(string presentationId) =>
        documents[ApiDocumentKind.Presentations].GetValueOrDefault(presentationId)?["slides"] is JsonArray slides
            ? slides.OfType<JsonObject>().ToList()
            : null;

    private static IEnumerable<JsonObject> Items(JsonObject service) =>
        service["items"] is JsonArray items ? items.OfType<JsonObject>() : [];

    private List<Position> Positions(JsonObject service) =>
        Items(service)
            .Where(i => (string?)i["itemKind"] == "presentation")
            .SelectMany(i => (Slides((string?)i["refId"] ?? "") ?? [])
                .Select((_, index) => new Position((string)i["id"]!, (string)i["refId"]!, index)))
            .ToList();

    private static string Text(JsonObject slide) =>
        string.Join("\n", (slide["objects"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(o => (string?)o["text"]).Where(t => !string.IsNullOrEmpty(t)));
}
