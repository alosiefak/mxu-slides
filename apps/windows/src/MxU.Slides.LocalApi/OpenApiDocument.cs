using System.Text.Json;
using System.Text.Json.Nodes;

namespace MxU.Slides.LocalApi;

/// <summary>
/// The contract served at /openapi.json: every route plus the wire schemas and the
/// codegen-emitted document schemas (embedded document-schemas.json). Port of OpenAPIDocument.
/// </summary>
public static class OpenApiDocument
{
    public const string ApiVersion = "1.0";

    private const string ResourceName = "MxU.Slides.LocalApi.document-schemas.json";

    private static readonly Lazy<byte[]> Resource = new(() =>
    {
        using var stream = typeof(OpenApiDocument).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null) return [];
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    });

    private static readonly Lazy<int> SchemaVersion = new(() =>
        ParseResource()?["schemaVersion"] is JsonValue value && value.TryGetValue(out int version) ? version : 0);

    public static JsonObject Compose(IEnumerable<ApiRoute> routes, int schemaVersion)
    {
        var paths = new JsonObject();
        foreach (var route in routes)
        {
            var operation = new JsonObject
            {
                ["operationId"] = route.OperationId,
                ["summary"] = route.Summary,
                ["tags"] = new JsonArray(route.Tag),
                ["security"] = new JsonArray(new JsonObject { ["bearerToken"] = new JsonArray() }),
                ["x-required-scope"] = route.Scope.Raw(),
                ["responses"] = new JsonObject
                {
                    ["200"] = Response("Success", route.ResponseSchema.DeepClone()),
                    ["default"] = Response("Error", ApiJson.Ref("Error")),
                },
            };
            if (route.RequestSchema is { } request)
                operation["requestBody"] = new JsonObject { ["required"] = false, ["content"] = JsonContent(request.DeepClone()) };
            var parameters = PathParameters(route.Path);
            if (parameters.Count > 0)
                operation["parameters"] = new JsonArray([.. parameters.Select(name => new JsonObject
                {
                    ["name"] = name, ["in"] = "path", ["required"] = true, ["schema"] = new JsonObject { ["type"] = "string" },
                })]);
            if (paths[route.Path] is not JsonObject item)
            {
                item = [];
                paths[route.Path] = item;
            }
            item[route.Method.ToLowerInvariant()] = operation;
        }

        var schemas = WireSchemas();
        foreach (var (name, schema) in DocumentSchemas()) schemas[name] = schema;

        return new JsonObject
        {
            ["openapi"] = "3.1.0",
            ["info"] = new JsonObject
            {
                ["title"] = "MxU Slides Local API",
                ["version"] = ApiVersion,
                ["description"] = "Local show-control and content API served by the MxU Slides Windows app. "
                    + "Three capability classes: view, control, and create/edit — scoped "
                    + "bearer tokens minted in Settings › API. Document payloads follow the "
                    + $"app's schema (version {schemaVersion}).",
            },
            ["paths"] = paths,
            ["components"] = new JsonObject
            {
                ["securitySchemes"] = new JsonObject
                {
                    ["bearerToken"] = new JsonObject
                    {
                        ["type"] = "http", ["scheme"] = "bearer",
                        ["description"] = "A token minted in Settings › API. Scopes nest: edit ⊃ control ⊃ view.",
                    },
                },
                ["schemas"] = schemas,
            },
        };
    }

    /// <summary>The document schemas, without the server-sync fields (MxU* types, mxu* and excludedTimeHexIds properties).</summary>
    public static Dictionary<string, JsonNode> DocumentSchemas()
    {
        var result = new Dictionary<string, JsonNode>();
        if (ParseResource()?["schemas"] is not JsonObject schemas) return result;
        foreach (var (name, schema) in schemas.ToList())
        {
            if (name.StartsWith("MxU", StringComparison.Ordinal) || schema is null) continue;
            schemas.Remove(name);
            if (schema is JsonObject obj && obj["properties"] is JsonObject properties)
                foreach (var key in properties.Select(p => p.Key)
                             .Where(k => k.StartsWith("mxu", StringComparison.Ordinal) || k == "excludedTimeHexIds").ToList())
                    properties.Remove(key);
            result[name] = schema;
        }
        return result;
    }

    /// <summary>The schema version of the embedded document schemas (packages/schema), 0 when missing.</summary>
    public static int DocumentSchemaVersion() => SchemaVersion.Value;

    private static JsonObject? ParseResource()
    {
        if (Resource.Value.Length == 0) return null;
        try { return JsonNode.Parse(Resource.Value) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static List<string> PathParameters(string template) =>
        template.Split('/').Where(s => s.StartsWith('{') && s.EndsWith('}')).Select(s => s[1..^1]).ToList();

    private static JsonObject Response(string description, JsonNode schema) =>
        new() { ["description"] = description, ["content"] = JsonContent(schema) };

    private static JsonObject JsonContent(JsonNode schema) =>
        new() { ["application/json"] = new JsonObject { ["schema"] = schema } };

    // MARK: Wire schemas

    /// <summary>A fresh copy of the wire models' schemas (JsonNodes can't be shared between documents).</summary>
    internal static JsonObject WireSchemas() => new()
    {
        ["Error"] = Obj(new() { ["status"] = Int, ["code"] = Str, ["message"] = Str }, "status", "code", "message"),
        ["OK"] = Obj(new() { ["ok"] = Bool }, "ok"),
        ["Created"] = Obj(new() { ["id"] = Str }, "id"),
        ["LibrarySummary"] = Obj(new()
        {
            ["counts"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = Int },
        }, "counts"),
        ["LibraryEntry"] = Obj(new()
        {
            ["id"] = Str, ["name"] = Str, ["kind"] = Str, ["folder"] = Str, ["updatedAt"] = Date,
        }, "id", "name", "kind"),
        ["Document"] = new JsonObject
        {
            ["type"] = "object",
            ["description"] = "A schema document — see the per-kind component schemas (Presentation, Service, …).",
        },
        ["ShowStatus"] = Obj(new()
        {
            ["liveSlide"] = Obj(new()
            {
                ["presentationId"] = Str, ["presentationName"] = Str, ["slideId"] = Str,
                ["slideIndex"] = Int, ["slideName"] = Str, ["serviceItemId"] = Str, ["occurrence"] = Int,
                ["stepIndex"] = Int, ["stepCount"] = Int, ["text"] = Str, ["slideCount"] = Int,
            }, "presentationId", "slideId"),
            ["nextSlideText"] = Str,
            ["service"] = Obj(new() { ["id"] = Str, ["name"] = Str }, "id", "name"),
            ["nextSlide"] = Obj(new() { ["text"] = Str, ["presentationId"] = Str, ["slideId"] = Str, ["slideIndex"] = Int }),
            ["position"] = Obj(new() { ["currentItemName"] = Str, ["nextItemName"] = Str }),
            ["mediaLayers"] = new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = Obj(new() { ["mediaId"] = Str, ["mediaName"] = Str, ["loops"] = Bool }, "mediaId"),
            },
            ["overlays"] = ArrayOf(Obj(new() { ["id"] = Str, ["name"] = Str, ["layer"] = Str }, "id", "name")),
            ["alert"] = Obj(new()
            {
                ["id"] = Str, ["message"] = Str, ["behavior"] = Str, ["target"] = Str, ["layer"] = Str,
            }, "id", "message", "behavior", "target"),
            ["currentServiceId"] = Str,
        }, "mediaLayers", "overlays"),
        ["TimerStatus"] = Obj(new()
        {
            ["id"] = Str, ["name"] = Str, ["mode"] = Str, ["running"] = Bool, ["displaySeconds"] = Int, ["overrun"] = Bool,
            ["folderId"] = Str, ["folderName"] = Str, ["runningSince"] = Date, ["bankedSeconds"] = Num, ["durationSeconds"] = Num,
            ["targetTime"] = Date, ["armedAt"] = Date,
            ["warnings"] = ArrayOf(Obj(new() { ["remainingSeconds"] = Num, ["colorHex"] = Str }, "remainingSeconds", "colorHex")),
        }, "id", "name", "mode", "running", "displaySeconds", "overrun"),
        ["SchedulerStatus"] = Obj(new()
        {
            ["enabled"] = Bool,
            ["nextFire"] = Obj(new() { ["triggerId"] = Str, ["name"] = Str, ["at"] = Date }, "triggerId", "name", "at"),
            ["triggers"] = ArrayOf(Obj(new()
            {
                ["id"] = Str, ["name"] = Str, ["enabled"] = Bool, ["folderEnabled"] = Bool, ["folderId"] = Str, ["folderName"] = Str,
                ["nextFire"] = Date, ["lastFired"] = Date, ["archived"] = Bool,
            }, "id", "name", "enabled", "folderEnabled")),
        }, "enabled", "triggers"),
        ["TimecodeStatus"] = Obj(new()
        {
            ["hasInput"] = Bool,
            ["source"] = OneOf("ltc", "mtc"),
            ["follows"] = Bool,
            ["state"] = OneOf("noInput", "locking", "locked", "freewheel", "stopped"),
            ["label"] = Str,
            ["rate"] = OneOf("24", "25", "29.97", "29.97df", "30"),
            ["reverse"] = Bool,
            ["offsetFrames"] = Int,
            ["serviceItemId"] = Str,
            ["anchor"] = Obj(new() { ["seconds"] = Num, ["anchoredAt"] = Date, ["running"] = Bool, ["reverse"] = Bool },
                "seconds", "anchoredAt", "running", "reverse"),
            ["timeline"] = Obj(new()
            {
                ["serviceItemId"] = Str, ["position"] = Num, ["anchoredAt"] = Date, ["running"] = Bool, ["length"] = Num,
            }, "serviceItemId", "position", "anchoredAt", "running", "length"),
        }, "hasInput", "source", "follows", "state", "reverse", "offsetFrames"),
        ["SchedulerEnableCommand"] = Obj(new() { ["enabled"] = Bool }, "enabled"),
        ["VideoInputStatus"] = Obj(new()
        {
            ["id"] = Str, ["name"] = Str, ["kind"] = OneOf("camera", "ndi"), ["sourceId"] = Str, ["audioInputId"] = Str,
            ["delayFrames"] = Int,
        }, "id", "name"),
        ["AudioInputStatus"] = Obj(new()
        {
            ["id"] = Str, ["name"] = Str, ["deviceUid"] = Str, ["live"] = Bool, ["enabled"] = Bool, ["gain"] = Num,
            ["muted"] = Bool, ["mixId"] = Str, ["delayMs"] = Int,
        }, "id", "name", "live", "enabled", "gain", "muted"),
        ["AudioMixStatus"] = Obj(new()
        {
            ["id"] = Str, ["name"] = Str, ["deviceUid"] = Str, ["channelOffset"] = Int, ["gain"] = Num, ["muted"] = Bool,
            ["outputIds"] = ArrayOf(Str),
        }, "id", "name", "channelOffset", "gain", "muted"),
        ["MixerInputCommand"] = Obj(new() { ["enabled"] = Bool, ["gain"] = Num, ["muted"] = Bool, ["delayMs"] = Int }),
        ["MIDIDeviceStatus"] = Obj(new()
        {
            ["id"] = Str, ["name"] = Str, ["enabled"] = Bool, ["direction"] = OneOf("output", "input", "both"),
            ["channel"] = Int, ["connected"] = Bool, ["destinationUid"] = Int, ["sourceUid"] = Int,
        }, "id", "name", "enabled", "direction", "connected"),
        ["MIDIDeviceEnableCommand"] = Obj(new() { ["enabled"] = Bool }, "enabled"),
        ["AudioStatus"] = Obj(new()
        {
            ["buses"] = ArrayOf(Obj(new()
            {
                ["playlistId"] = Str, ["audioItemId"] = Str, ["trackName"] = Str, ["playing"] = Bool,
                ["position"] = Num, ["duration"] = Num,
            }, "playing")),
        }, "buses"),
        ["TransportRow"] = Obj(new()
        {
            ["mediaId"] = Str, ["name"] = Str, ["layer"] = Str, ["playing"] = Bool, ["looping"] = Bool,
            ["position"] = Num, ["duration"] = Num,
        }, "mediaId", "name", "layer", "playing", "looping", "position", "duration"),
        ["OutputsStatus"] = Obj(new()
        {
            ["screens"] = ArrayOf(Obj(new()
            {
                ["id"] = Str, ["name"] = Str, ["role"] = Str, ["backed"] = Bool, ["width"] = Num, ["height"] = Num,
                ["outputs"] = ArrayOf(Obj(new()
                {
                    ["id"] = Str, ["name"] = Str, ["x"] = Num, ["y"] = Num, ["width"] = Num, ["height"] = Num,
                    ["backing"] = Str, ["adjusted"] = Bool,
                }, "id", "x", "y", "width", "height", "backing", "adjusted")),
                ["activeMasks"] = ArrayOf(Str),
            }, "id", "name", "role", "backed", "width", "height", "outputs", "activeMasks")),
            ["presets"] = ArrayOf(Obj(new() { ["id"] = Str, ["name"] = Str, ["active"] = Bool }, "id", "name", "active")),
        }, "screens", "presets"),
        ["FireSlideCommand"] = Obj(new()
        {
            ["presentationId"] = Str, ["slideId"] = Str, ["slideIndex"] = Int,
            ["arrangementId"] = Str, ["serviceItemId"] = Str, ["occurrence"] = Int,
        }),
        ["AdvanceCommand"] = Obj(new() { ["steps"] = Int, ["settled"] = Bool }),
        ["ClearCommand"] = Obj(new() { ["function"] = Str, ["layer"] = Str }),
        ["FireAlertCommand"] = Obj(new()
        {
            ["presetId"] = Str, ["message"] = Str,
            ["tokens"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = Str },
            ["behavior"] = Str, ["target"] = Str, ["themeId"] = Str,
        }),
        ["AudioFireCommand"] = Obj(new() { ["startAtEntryId"] = Str }),
        ["AudioStopCommand"] = Obj(new() { ["playlistId"] = Str, ["audioItemId"] = Str }),
        ["AudioTransportCommand"] = Obj(new() { ["action"] = Str, ["position"] = Num }, "action"),
        ["MediaTransportCommand"] = Obj(new() { ["action"] = Str, ["position"] = Num }, "action"),
        ["AddServiceItemCommand"] = Obj(new() { ["refId"] = Str, ["index"] = Int }, "refId"),
        ["ServerInfo"] = Obj(new() { ["name"] = Str, ["product"] = Str, ["apiVersion"] = Str, ["schemaVersion"] = Int },
            "name", "product", "apiVersion", "schemaVersion"),
    };

    private static JsonObject Str => new() { ["type"] = "string" };
    private static JsonObject Int => new() { ["type"] = "integer" };
    private static JsonObject Num => new() { ["type"] = "number" };
    private static JsonObject Bool => new() { ["type"] = "boolean" };
    private static JsonObject Date => new() { ["type"] = "string", ["format"] = "date-time" };

    private static JsonObject OneOf(params string[] values) =>
        new() { ["type"] = "string", ["enum"] = new JsonArray([.. values.Select(v => (JsonNode)v)]) };

    private static JsonObject ArrayOf(JsonNode items) => new() { ["type"] = "array", ["items"] = items };

    private static JsonObject Obj(JsonObject properties, params string[] required)
    {
        var value = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Length > 0) value["required"] = new JsonArray([.. required.Select(r => (JsonNode)r)]);
        return value;
    }
}

/// <summary>The WebSocket contract served at /asyncapi.json: one channel per topic. Port of AsyncAPIDocument.</summary>
public static class AsyncApiDocument
{
    internal static readonly IReadOnlyDictionary<ApiTopic, (string Name, bool IsList)> PayloadSchemas =
        new Dictionary<ApiTopic, (string, bool)>
        {
            [ApiTopic.Show] = ("ShowStatus", false),
            [ApiTopic.Timers] = ("TimerStatus", true),
            [ApiTopic.Audio] = ("AudioStatus", false),
            [ApiTopic.Transport] = ("TransportRow", true),
            [ApiTopic.Outputs] = ("OutputsStatus", false),
            [ApiTopic.Library] = ("LibrarySummary", false),
            [ApiTopic.Scheduler] = ("SchedulerStatus", false),
            [ApiTopic.Timecode] = ("TimecodeStatus", false),
        };

    internal static string TopicDescription(ApiTopic topic) => topic switch
    {
        ApiTopic.Show => "Snapshot pushed when the live show changes: the live slide (with step counters — stepIndex = click groups consumed so far, stepCount = click groups on the slide; both absent when the slide has no animation steps), next-slide text, media layers, overlays. Same shape as GET /v1/status. POST /v1/show/advance consumes one click group per call and pushes a new snapshot; past the last click one more call plays the exit group (the cue stays live, stepIndex reaches stepCount), and the call after that moves slides.",
        ApiTopic.Timecode => "Snapshot pushed when timecode locks or loses lock, changes song or rate, starts, stops or jumps — never per frame. Count frames from anchor (and the timeline playhead from timeline) yourself. Same shape as GET /v1/timecode.",
        _ => $"Snapshot pushed when {topic.Raw()} state changes; same shape as its REST endpoint.",
    };

    public static JsonObject Compose(int schemaVersion)
    {
        var wire = OpenApiDocument.WireSchemas();
        var channels = new JsonObject();
        var messages = new JsonObject();
        foreach (var topic in ApiTopics.All)
        {
            var messageName = $"{topic.Raw()}Event";
            channels[topic.Raw()] = new JsonObject
            {
                ["description"] = TopicDescription(topic),
                ["subscribe"] = new JsonObject
                {
                    ["message"] = new JsonObject { ["$ref"] = $"#/components/messages/{messageName}" },
                },
            };
            JsonNode data = PayloadSchemas.TryGetValue(topic, out var payload) && wire[payload.Name] is { } schema
                ? payload.IsList
                    ? new JsonObject { ["type"] = "array", ["items"] = schema.DeepClone() }
                    : schema.DeepClone()
                : new JsonObject { ["type"] = "object" };
            messages[messageName] = new JsonObject
            {
                ["name"] = messageName,
                ["payload"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["type"] = new JsonObject { ["type"] = "string", ["const"] = "event" },
                        ["topic"] = new JsonObject { ["type"] = "string", ["const"] = topic.Raw() },
                        ["data"] = data,
                    },
                },
            };
        }
        return new JsonObject
        {
            ["asyncapi"] = "2.6.0",
            ["info"] = new JsonObject
            {
                ["title"] = "MxU Slides Local API — WebSocket",
                ["version"] = OpenApiDocument.ApiVersion,
                ["description"] = "Connect to /v1/ws?token=SECRET. Send {\"type\":\"subscribe\",\"topics\":[…]} "
                    + "to receive state pushes; send {\"type\":\"command\",\"id\":…,\"method\":…,"
                    + "\"path\":…,\"body\":…} to invoke any REST operation over the socket. "
                    + $"Document schema version {schemaVersion}.",
            },
            ["channels"] = channels,
            ["components"] = new JsonObject { ["messages"] = messages },
        };
    }
}
