using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MxU.Slides.LocalApi.Tests;

// Port of LocalAPITests.swift.

public sealed class ApiTokenTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"tokens-{Guid.NewGuid()}.json");

    private static ApiTokenStore MakeStore() => new(TempFile());

    [Fact]
    public void MintedSecretVerifiesAndIsStoredHashed()
    {
        var store = MakeStore();
        var (token, secret) = store.Create("Booth", ApiScope.Control);
        Assert.StartsWith("mxu_", secret);
        Assert.DoesNotContain(secret, token.SecretRecord);
        Assert.Equal(token.Id, store.Verify(secret)?.Id);
        Assert.Null(store.Verify("mxu_wrong"));
    }

    [Fact]
    public void RevokedTokenStopsVerifying()
    {
        var store = MakeStore();
        var (token, secret) = store.Create("Old", ApiScope.View);
        store.Revoke(token.Id);
        Assert.Null(store.Verify(secret));
    }

    [Fact]
    public void TokensPersistAcrossReload()
    {
        var file = TempFile();
        var secret = new ApiTokenStore(file).Create("Persisted", ApiScope.Edit).Secret;
        var reloaded = new ApiTokenStore(file);
        Assert.Equal("Persisted", reloaded.Verify(secret)?.Name);
        Assert.DoesNotContain(secret, File.ReadAllText(file));
    }

    [Fact]
    public void EnsureDefaultKeyMintsOperateKeyOnlyWhenEmpty()
    {
        var store = MakeStore();
        var secret = store.EnsureDefaultKey();
        Assert.NotNull(secret);
        Assert.Single(store.All);
        Assert.Equal(ApiTokenStore.DefaultKeyName, store.All[0].Name);
        Assert.Equal(ApiScope.Control, store.All[0].Scope);
        Assert.Equal(store.All[0].Id, store.Verify(secret)?.Id);

        Assert.Equal(secret, store.EnsureDefaultKey());
        Assert.Equal(secret, store.DefaultKeySecret);
        Assert.Single(store.All);
    }

    [Fact]
    public void EnsureDefaultKeySkipsWhenHandMadeKeysExist()
    {
        var store = MakeStore();
        store.Create("Booth", ApiScope.View);
        Assert.Null(store.EnsureDefaultKey());
        Assert.Null(store.DefaultKeySecret);
        Assert.Single(store.All);

        var secret = store.EnsureDefaultKey(evenIfPopulated: true);
        Assert.NotNull(secret);
        Assert.Equal(2, store.All.Count);
        Assert.Equal(secret, store.EnsureDefaultKey(evenIfPopulated: true));
        Assert.Equal(2, store.All.Count);
    }

    [Fact]
    public void RevokingDefaultKeyClearsItsSecret()
    {
        var store = MakeStore();
        var secret = store.EnsureDefaultKey()!;
        store.Revoke(store.All[0].Id);
        Assert.Null(store.DefaultKeySecret);
        Assert.Null(store.Verify(secret));

        var again = store.EnsureDefaultKey();
        Assert.NotNull(again);
        Assert.NotEqual(secret, again);
    }

    [Fact]
    public void DefaultKeySecretSurvivesReloadThroughVault()
    {
        var file = TempFile();
        var vault = ApiDefaultKeyVault.InMemory();
        var secret = new ApiTokenStore(file, vault).EnsureDefaultKey();
        var reloaded = new ApiTokenStore(file, vault);
        Assert.Equal(secret, reloaded.DefaultKeySecret);

        var stale = new ApiTokenStore(file, ApiDefaultKeyVault.InMemory());
        Assert.Null(stale.DefaultKeySecret);
    }

    [Fact]
    public void ScopeNesting()
    {
        Assert.True(ApiScope.Edit.Allows(ApiScope.View));
        Assert.True(ApiScope.Edit.Allows(ApiScope.Control));
        Assert.True(ApiScope.Control.Allows(ApiScope.View));
        Assert.False(ApiScope.View.Allows(ApiScope.Control));
        Assert.False(ApiScope.Control.Allows(ApiScope.Edit));
    }

    [Fact]
    public void TokenFileHasTheMacShape()
    {
        var file = TempFile();
        new ApiTokenStore(file).Create("Booth", ApiScope.View);
        var token = Assert.Single(JsonNode.Parse(File.ReadAllText(file))!.AsArray())!.AsObject();
        Assert.Equal(["createdAt", "id", "name", "prefix", "scope", "secretRecord"], token.Select(p => p.Key));
        Assert.Equal("view", (string?)token["scope"]);
        Assert.Equal(JsonValueKind.Number, token["createdAt"]!.GetValueKind());
        Assert.Matches("^[0-9a-f]{32}\\$[0-9a-f]{64}$", (string?)token["secretRecord"]);
    }
}

public sealed class ApiRouterTests
{
    private static (ApiRouter Router, FakeBridge Bridge) MakeRouter()
    {
        var bridge = new FakeBridge();
        return (new ApiRouter(ApiRouteTable.Build(bridge)), bridge);
    }

    private static byte[] Body(string json) => Encoding.UTF8.GetBytes(json);

    [Fact]
    public void PathTemplateMatchCapturesParameters()
    {
        var (router, _) = MakeRouter();
        var match = router.Match("POST", "/v1/timers/abc/play");
        Assert.Equal("timerPlay", match?.Route.OperationId);
        Assert.Equal("abc", match?.Parameters["id"]);
        Assert.Null(router.Match("POST", "/v1/timers/abc/explode"));
    }

    [Fact]
    public async Task ControlDispatchReachesBridge()
    {
        var (router, bridge) = MakeRouter();
        var match = router.Match("POST", "/v1/show/advance")!;
        var response = await match.Route.Handler(new ApiRequestContext { Body = Body("""{"steps":-1}"""), Scope = ApiScope.Control });
        Assert.Equal(200, response.Status);
        Assert.Equal(["advance:-1"], bridge.Calls);

        await match.Route.Handler(new ApiRequestContext { Body = Body("""{"steps":1,"settled":true}"""), Scope = ApiScope.Control });
        Assert.Equal("advance:1:settled", bridge.Calls[^1]);
    }

    [Fact]
    public async Task SlideFireIntoAColdDeckFiresAfterTheFill()
    {
        var (router, bridge) = MakeRouter();
        bridge.ColdDecks.Add("p9");
        var match = router.Match("POST", "/v1/show/slide")!;
        var response = await match.Route.Handler(new ApiRequestContext
        {
            Body = Body("""{"presentationId":"p9","slideIndex":0}"""), Scope = ApiScope.Control,
        });
        Assert.Equal(200, response.Status);
        Assert.Equal(["fireSlide:p9"], bridge.Calls);
        Assert.Empty(bridge.ColdDecks);
    }

    [Fact]
    public async Task AdvanceIntoAColdWalkRunsAfterTheFillAndItsErrorReachesTheCaller()
    {
        var (router, bridge) = MakeRouter();
        bridge.ColdDecks.Add("advance");
        var match = router.Match("POST", "/v1/show/advance")!;
        var response = await match.Route.Handler(new ApiRequestContext { Body = Body("""{"steps":1}"""), Scope = ApiScope.Control });
        Assert.Equal(200, response.Status);
        Assert.Equal(["advance:1"], bridge.Calls);
        Assert.Empty(bridge.ColdDecks);

        bridge.ColdDecks.Add("advance");
        bridge.AdvanceError = true;
        var error = await Assert.ThrowsAsync<ApiError>(() =>
            match.Route.Handler(new ApiRequestContext { Body = Body("""{"steps":1}"""), Scope = ApiScope.Control }));
        Assert.Equal(400, error.Status);
    }

    [Fact]
    public async Task DocumentCrudDispatch()
    {
        var (router, bridge) = MakeRouter();
        var create = router.Match("POST", "/v1/documents/presentations")!;
        var body = Body("""{"name":"New Song"}""");
        var created = await create.Route.Handler(new ApiRequestContext
        {
            PathParameters = create.Parameters, Body = body, Scope = ApiScope.Edit,
        });
        Assert.Equal(201, created.Status);
        Assert.Equal("new-id", JsonSerializer.Deserialize<ApiCreatedResponse>(created.Body, ApiJson.Options)?.Id);

        var get = router.Match("GET", "/v1/documents/presentations/new-id")!;
        var fetched = await get.Route.Handler(new ApiRequestContext { PathParameters = get.Parameters, Scope = ApiScope.View });
        Assert.Equal(body, fetched.Body);
        Assert.Equal(["create:presentations"], bridge.Calls);
    }

    [Fact]
    public async Task StreamPresetCredentialsRequireManageAccess()
    {
        var (router, bridge) = MakeRouter();
        var body = Body("""{"destinations":[{"streamKey":"test-only-key","url":"https://example.invalid/ingest?key=test"}]}""");
        bridge.Documents["preset"] = body;
        var match = router.Match("GET", "/v1/documents/stream-presets/preset")!;
        foreach (var scope in new[] { ApiScope.View, ApiScope.Control })
        {
            var error = await Assert.ThrowsAsync<ApiError>(() =>
                match.Route.Handler(new ApiRequestContext { PathParameters = match.Parameters, Scope = scope }));
            Assert.Equal(403, error.Status);
        }
        var response = await match.Route.Handler(new ApiRequestContext { PathParameters = match.Parameters, Scope = ApiScope.Edit });
        Assert.Equal(body, response.Body);
    }

    [Fact]
    public async Task UnknownDocumentKindIsBadRequest()
    {
        var (router, _) = MakeRouter();
        var match = router.Match("GET", "/v1/library/nonsense")!;
        var error = await Assert.ThrowsAsync<ApiError>(() =>
            match.Route.Handler(new ApiRequestContext { PathParameters = match.Parameters, Scope = ApiScope.View }));
        Assert.Equal(400, error.Status);
    }

    [Fact]
    public async Task InputsListAndMixerDispatch()
    {
        var (router, bridge) = MakeRouter();
        var video = router.Match("GET", "/v1/inputs/video")!;
        var videoInputs = Decode<List<ApiVideoInputStatus>>(await video.Route.Handler(new ApiRequestContext { Scope = ApiScope.View }));
        Assert.Equal("vin-1", videoInputs[0].Id);
        Assert.Equal("camera", videoInputs[0].Kind);
        Assert.Equal("ain-1", videoInputs[0].AudioInputId);

        var audio = router.Match("GET", "/v1/inputs/audio")!;
        var audioInputs = Decode<List<ApiAudioInputStatus>>(await audio.Route.Handler(new ApiRequestContext { Scope = ApiScope.View }));
        Assert.Equal("ain-1", audioInputs[0].Id);
        Assert.True(audioInputs[0].Live);

        var mixes = router.Match("GET", "/v1/audio/mixes")!;
        var mixStatuses = Decode<List<ApiAudioMixStatus>>(await mixes.Route.Handler(new ApiRequestContext { Scope = ApiScope.View }));
        Assert.Equal(["main", "mix-b"], mixStatuses.Select(m => m.Id));
        Assert.Equal(2, mixStatuses[^1].ChannelOffset);

        var mix = router.Match("POST", "/v1/inputs/audio/ain-1/mixer")!;
        var response = await mix.Route.Handler(new ApiRequestContext
        {
            PathParameters = mix.Parameters, Body = Body("""{"enabled":true,"gain":0.5}"""), Scope = ApiScope.Control,
        });
        Assert.Equal(200, response.Status);
        Assert.Equal(["setMixerInput:ain-1:true:0.5:-"], bridge.Calls);
    }

    [Fact]
    public async Task MixerUnknownInputIs404()
    {
        var (router, _) = MakeRouter();
        var match = router.Match("POST", "/v1/inputs/audio/ghost/mixer")!;
        var error = await Assert.ThrowsAsync<ApiError>(() => match.Route.Handler(new ApiRequestContext
        {
            PathParameters = match.Parameters, Body = Body("""{"muted":true}"""), Scope = ApiScope.Control,
        }));
        Assert.Equal(404, error.Status);
    }

    [Fact]
    public async Task MidiDeviceListAndEnableDispatch()
    {
        var (router, bridge) = MakeRouter();
        var list = router.Match("GET", "/v1/midi/devices")!;
        var devices = Decode<List<ApiMidiDeviceStatus>>(await list.Route.Handler(new ApiRequestContext { Scope = ApiScope.View }));
        Assert.Equal("console-item", devices[0].Id);
        Assert.Equal("both", devices[0].Direction);
        Assert.Equal(42, devices[0].DestinationUid);

        var toggle = router.Match("POST", "/v1/midi/devices/console-item/enabled")!;
        var response = await toggle.Route.Handler(new ApiRequestContext
        {
            PathParameters = toggle.Parameters, Body = Body("""{"enabled":false}"""), Scope = ApiScope.Control,
        });
        Assert.Equal(200, response.Status);
        Assert.Equal(["setMIDIDeviceEnabled:console-item:false"], bridge.Calls);
    }

    [Fact]
    public async Task TimecodeStatusIsAView()
    {
        var (router, _) = MakeRouter();
        var match = router.Match("GET", "/v1/timecode")!;
        Assert.Equal(ApiScope.View, match.Route.Scope);
        var body = JsonNode.Parse((await match.Route.Handler(new ApiRequestContext { Scope = ApiScope.View })).Body)!;
        Assert.Equal("locked", (string?)body["state"]);
        Assert.Equal("mtc", (string?)body["source"]);
        Assert.Equal("item-a", (string?)body["serviceItemId"]);
        Assert.True((bool?)body["anchor"]?["running"]);
        Assert.Equal("TimecodeStatus", AsyncApiDocument.PayloadSchemas[ApiTopic.Timecode].Name);
    }

    [Fact]
    public async Task MidiDeviceEnableUnknownIdIs404()
    {
        var (router, _) = MakeRouter();
        var match = router.Match("POST", "/v1/midi/devices/ghost/enabled")!;
        var error = await Assert.ThrowsAsync<ApiError>(() => match.Route.Handler(new ApiRequestContext
        {
            PathParameters = match.Parameters, Body = Body("""{"enabled":true}"""), Scope = ApiScope.Control,
        }));
        Assert.Equal(404, error.Status);
    }

    [Fact]
    public void EveryRouteHasUniqueOperationIdAndMethodPath()
    {
        var (router, _) = MakeRouter();
        var operationIds = router.Routes.Select(r => r.OperationId).ToList();
        Assert.Equal(operationIds.Count, operationIds.Distinct().Count());
        var pairs = router.Routes.Select(r => $"{r.Method} {r.Path}").ToList();
        Assert.Equal(pairs.Count, pairs.Distinct().Count());
    }

    [Fact]
    public void EncodedPathSeparatorInParameterMatchesNothing()
    {
        var (router, _) = MakeRouter();
        Assert.Null(router.Match("GET", "/v1/documents/presentations/..%2F..%2Fsecrets"));
        Assert.Null(router.Match("DELETE", "/v1/documents/media/evil%5C..%5Cpath"));
        Assert.NotNull(router.Match("GET", "/v1/documents/presentations/.."));
    }

    // Windows-only checks.

    [Fact]
    public void RouteTableMatchesTheMac()
    {
        string[] mac =
        [
            "GET /v1/library view getLibrary", "GET /v1/library/{kind} view listLibraryEntries",
            "GET /v1/documents/{kind}/{id} view getDocument", "GET /v1/status view getShowStatus",
            "GET /v1/timers view listTimers", "GET /v1/audio view getAudioStatus",
            "GET /v1/transport view getTransport", "GET /v1/outputs view getOutputs",
            "GET /v1/scheduler view getSchedulerStatus", "GET /v1/timecode view getTimecodeStatus",
            "POST /v1/scheduler/enabled control setSchedulerEnabled",
            "POST /v1/scheduler/triggers/{id}/enabled control setScheduleTriggerEnabled",
            "POST /v1/scheduler/triggers/{id}/fire control fireScheduleTrigger",
            "GET /v1/inputs/video view listVideoInputs", "GET /v1/inputs/audio view listAudioInputs",
            "GET /v1/audio/mixes view listAudioMixes", "POST /v1/inputs/audio/{id}/mixer control setMixerInput",
            "GET /v1/midi/devices view listMIDIDevices", "POST /v1/midi/devices/{id}/enabled control setMIDIDeviceEnabled",
            "POST /v1/show/slide control fireSlide", "POST /v1/show/advance control advance",
            "POST /v1/show/clear control clear", "POST /v1/show/clear-all control clearAll",
            "POST /v1/media/{id}/fire control fireMedia", "POST /v1/action-combos/{id}/fire control fireActionCombo",
            "POST /v1/overlays/{id}/fire control fireOverlay", "POST /v1/overlays/{id}/dismiss control dismissOverlay",
            "POST /v1/alerts/fire control fireAlert", "POST /v1/alerts/dismiss control dismissAlert",
            "POST /v1/timers/{id}/play control timerPlay", "POST /v1/timers/{id}/pause control timerPause",
            "POST /v1/timers/{id}/reset control timerReset",
            "POST /v1/audio/playlists/{id}/fire control fireAudioPlaylist", "POST /v1/audio/items/{id}/fire control fireAudioItem",
            "POST /v1/audio/stop control stopAudio", "POST /v1/audio/transport control audioTransport",
            "POST /v1/transport/{id} control mediaTransport", "POST /v1/output-presets/{id}/activate control activateOutputPreset",
            "POST /v1/services/{id}/select control selectService", "POST /v1/documents/{kind} edit createDocument",
            "PUT /v1/documents/{kind}/{id} edit updateDocument", "DELETE /v1/documents/{kind}/{id} edit deleteDocument",
            "POST /v1/services/{id}/items edit addServiceItem",
        ];
        var (router, _) = MakeRouter();
        // Windows-only routes (production control) live under /v1/windows/, apart from the Mac's.
        Assert.Equal(mac, router.Routes.Where(r => !r.Path.StartsWith("/v1/windows/", StringComparison.Ordinal))
            .Select(r => $"{r.Method} {r.Path} {r.Scope.Raw()} {r.OperationId}"));
    }

    [Theory]
    [InlineData("GET", "/v1/scheduler", "", "Scheduler unavailable.")]
    [InlineData("POST", "/v1/scheduler/enabled", """{"enabled":true}""", "Scheduler unavailable.")]
    [InlineData("POST", "/v1/scheduler/triggers/t1/enabled", """{"enabled":true}""", "Scheduler unavailable.")]
    [InlineData("POST", "/v1/scheduler/triggers/t1/fire", "", "Scheduler unavailable.")]
    [InlineData("GET", "/v1/timecode", "", "Timecode unavailable.")]
    [InlineData("GET", "/v1/inputs/video", "", "Video inputs unavailable.")]
    [InlineData("GET", "/v1/inputs/audio", "", "Audio inputs unavailable.")]
    [InlineData("GET", "/v1/audio/mixes", "", "Audio mixes unavailable.")]
    [InlineData("POST", "/v1/inputs/audio/a1/mixer", """{"muted":true}""", "Audio inputs unavailable.")]
    [InlineData("GET", "/v1/midi/devices", "", "MIDI unavailable.")]
    [InlineData("POST", "/v1/midi/devices/m1/enabled", """{"enabled":true}""", "MIDI unavailable.")]
    [InlineData("POST", "/v1/action-combos/c1/fire", "", "Actions unavailable.")]
    [InlineData("POST", "/v1/output-presets/none/activate", "", "Output presets unavailable.")]
    [InlineData("POST", "/v1/audio/playlists/p1/fire", "", "Audio unavailable.")]
    [InlineData("POST", "/v1/audio/items/a1/fire", "", "Audio unavailable.")]
    [InlineData("POST", "/v1/audio/stop", "", "Audio unavailable.")]
    [InlineData("POST", "/v1/audio/transport", """{"action":"playPause"}""", "Audio unavailable.")]
    public async Task FeaturesTheBridgeLacksAnswerLikeAMacWithoutThem(string method, string path, string body, string message)
    {
        var router = new ApiRouter(ApiRouteTable.Build(new CoreOnlyBridge()));
        var match = router.Match(method, path)!;
        var error = await Assert.ThrowsAsync<ApiError>(() => match.Route.Handler(new ApiRequestContext
        {
            PathParameters = match.Parameters, Body = Body(body), Scope = ApiScope.Edit,
        }));
        Assert.Equal((404, "not_found", message), (error.Status, error.Code, error.Message));
    }

    [Fact]
    public async Task AudioStatusReadsAsNothingPlayingWithoutAnAudioEngine()
    {
        var router = new ApiRouter(ApiRouteTable.Build(new CoreOnlyBridge()));
        var response = await router.Match("GET", "/v1/audio")!.Route.Handler(new ApiRequestContext { Scope = ApiScope.View });
        Assert.Equal("""{"buses":[]}""", Encoding.UTF8.GetString(response.Body));
    }

    [Theory]
    [InlineData("""{"steps":"one"}""")]
    [InlineData("""[1]""")]
    [InlineData("""not json""")]
    public async Task UndecodableBodyIsBadRequest(string body)
    {
        var (router, bridge) = MakeRouter();
        var match = router.Match("POST", "/v1/show/advance")!;
        var error = await Assert.ThrowsAsync<ApiError>(() =>
            match.Route.Handler(new ApiRequestContext { Body = Body(body), Scope = ApiScope.Control }));
        Assert.Equal(400, error.Status);
        Assert.StartsWith("Could not decode request body:", error.Message);
        Assert.Empty(bridge.Calls);
    }

    [Fact]
    public async Task MissingRequiredFieldIsBadRequest()
    {
        var (router, bridge) = MakeRouter();
        foreach (var (path, body) in new[]
                 {
                     ("/v1/audio/transport", "{}"), ("/v1/scheduler/enabled", "{}"), ("/v1/audio/transport", """{"action":null}"""),
                 })
        {
            var match = router.Match("POST", path)!;
            var error = await Assert.ThrowsAsync<ApiError>(() =>
                match.Route.Handler(new ApiRequestContext { Body = Body(body), Scope = ApiScope.Control }));
            Assert.Equal(400, error.Status);
        }
        Assert.Empty(bridge.Calls);
    }

    [Fact]
    public void ValidatedIdsRejectTraversalAndOddCharacters()
    {
        Assert.Equal("ABC-12_x.y", ApiIds.Validated("ABC-12_x.y"));
        foreach (var bad in new[] { "", "..", "a..b", "a/b", "a\\b", "é", new string('a', 129) })
            Assert.Equal(400, Assert.Throws<ApiError>(() => ApiIds.Validated(bad)).Status);
    }

    private static T Decode<T>(ApiHandlerResponse response) =>
        JsonSerializer.Deserialize<T>(response.Body, ApiJson.Options)!;
}

public sealed class WebSocketFrameTests
{
    [Fact]
    public void ClientControlledStringsAreEscaped()
    {
        const string hostile = "x\",\"status\":200,\"forged\":\"yes";
        var frame = ApiWebSocketSession.FrameText(new JsonObject { ["type"] = "result", ["id"] = hostile, ["status"] = 404 });
        var decoded = JsonNode.Parse(frame)!;
        Assert.Equal(hostile, (string?)decoded["id"]);
        Assert.Equal(404, (int?)decoded["status"]);
        Assert.Null(decoded["forged"]);
    }

    [Fact]
    public void PayloadSplicesAsRawJson()
    {
        var payload = Encoding.UTF8.GetBytes("""{"liveSlide":null,"overlays":[]}""");
        var frame = ApiWebSocketSession.FrameText(new JsonObject { ["type"] = "event", ["topic"] = "show" }, ("data", payload));
        var decoded = JsonNode.Parse(frame)!;
        Assert.NotNull(decoded["data"]?["overlays"]);
        Assert.Equal("show", (string?)decoded["topic"]);
        Assert.Equal("""{"topic":"show","type":"event","data":{"liveSlide":null,"overlays":[]}}""", frame);
    }
}

public sealed class OpenApiDocumentTests
{
    private static List<ApiRoute> MakeRoutes() => ApiRouteTable.Build(new FakeBridge());

    [Fact]
    public void EveryRouteAppearsInComposedDocument()
    {
        var routes = MakeRoutes();
        var document = OpenApiDocument.Compose(routes, schemaVersion: 12);
        var paths = document["paths"]!.AsObject();
        foreach (var route in routes)
        {
            var item = paths[route.Path];
            Assert.NotNull(item);
            Assert.NotNull(item[route.Method.ToLowerInvariant()]);
        }
    }

    [Fact]
    public void DocumentSchemasFromCodegenAreEmbedded()
    {
        var schemas = OpenApiDocument.DocumentSchemas();
        Assert.True(schemas.ContainsKey("Presentation"), "codegen-emitted document schemas missing");
        Assert.True(schemas.ContainsKey("Service"));
        Assert.True(OpenApiDocument.DocumentSchemaVersion() >= 87);
        var properties = schemas["MediaItem"]["properties"]!.AsObject();
        Assert.True(properties.ContainsKey("actions"), "v87 MediaItem.actions missing from API schema");
        Assert.True(properties.ContainsKey("autoAdvance"), "v87 MediaItem.autoAdvance missing from API schema");
    }

    [Fact]
    public void EveryReferencedSchemaResolves()
    {
        var document = OpenApiDocument.Compose(MakeRoutes(), OpenApiDocument.DocumentSchemaVersion());
        var available = document["components"]!["schemas"]!.AsObject().Select(p => p.Key).ToHashSet();
        foreach (var reference in CollectRefs(document))
            Assert.True(available.Contains(reference), $"Unresolved $ref: {reference}");
    }

    [Fact]
    public void ServedDocumentOmitsServerSyncFields()
    {
        var document = OpenApiDocument.Compose(MakeRoutes(), OpenApiDocument.DocumentSchemaVersion());
        var schemas = document["components"]!["schemas"]!.AsObject();
        var itemProperties = schemas["ServiceItem"]!["properties"]!.AsObject();
        var serviceProperties = schemas["Service"]!["properties"]!.AsObject();
        Assert.True(itemProperties.ContainsKey("refId"));
        Assert.False(itemProperties.ContainsKey("mxuItemHexId"));
        Assert.False(itemProperties.ContainsKey("excludedTimeHexIds"));
        Assert.False(serviceProperties.ContainsKey("mxuPlanHexId"));
        Assert.False(schemas.ContainsKey("MxUPlanTime"));
    }

    [Fact]
    public void AsyncApiChannelsCarryTypedPayloads()
    {
        var document = AsyncApiDocument.Compose(schemaVersion: 1);
        var channels = document["channels"]!.AsObject();
        var messages = document["components"]!["messages"]!.AsObject();
        foreach (var topic in ApiTopics.All)
        {
            Assert.True(channels.ContainsKey(topic.Raw()), topic.Raw());
            Assert.True(messages.ContainsKey($"{topic.Raw()}Event"), topic.Raw());
        }
        foreach (var reference in CollectRefs(document))
            Assert.True(messages.ContainsKey(reference), $"Unresolved $ref: {reference}");
        var live = messages["showEvent"]!["payload"]!["properties"]!["data"]!["properties"]!["liveSlide"]!["properties"]!.AsObject();
        Assert.True(live.ContainsKey("stepIndex"));
        Assert.True(live.ContainsKey("stepCount"));
        Assert.Contains("/v1/show/advance", (string?)channels["show"]!["description"]);
    }

    [Fact]
    public void ComposingTwiceGivesTheSameDocument()
    {
        var routes = MakeRoutes();
        Assert.Equal(
            ApiJson.Encoded(OpenApiDocument.Compose(routes, 1)),
            ApiJson.Encoded(OpenApiDocument.Compose(routes, 1)));
    }

    private static HashSet<string> CollectRefs(JsonNode? node, HashSet<string>? refs = null)
    {
        refs ??= [];
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj)
                {
                    if (key == "$ref" && child is JsonValue value && value.TryGetValue(out string? path))
                        refs.Add(path.Split('/')[^1]);
                    else
                        CollectRefs(child, refs);
                }
                break;
            case JsonArray array:
                foreach (var child in array) CollectRefs(child, refs);
                break;
        }
        return refs;
    }
}
