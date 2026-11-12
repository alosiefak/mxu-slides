using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace MxU.Slides.LocalApi.Tests;

// Ports of the Mac's FollowModeTests.swift and FollowPlumbingTests.swift (MxU PR #12): backing up
// another computer. Same names, same checks, so a Mac and a Windows PC can back each other up.

public sealed class FollowReplayTests
{
    private static ApiFireEvent Event(ApiFireEvent.Kinds kind, int? cause = null) => new() { Kind = kind, Cause = cause };

    private static ApiShowStatus Show(string json) => JsonSerializer.Deserialize<ApiShowStatus>(json, ApiJson.Options)!;

    [Fact]
    public void OnlyARootFireReplaysItsEffectsFollowFromIt()
    {
        Assert.True(FollowReplay.Replays(Event(ApiFireEvent.Kinds.Combo)));
        Assert.False(FollowReplay.Replays(Event(ApiFireEvent.Kinds.OutputPreset, cause: 4))); // a combo's step runs here from the combo
    }

    [Fact]
    public async Task EachFireRunsTheMatchingBridgeCall()
    {
        var bridge = new FakeBridge();
        ApiFireEvent[] fires =
        [
            new() { Kind = ApiFireEvent.Kinds.Slide, Slide = new ApiFireSlideCommand("p1", "s2") },
            new() { Kind = ApiFireEvent.Kinds.Step, Steps = -1 },
            new() { Kind = ApiFireEvent.Kinds.Clear, Clear = new ApiClearCommand(Function: "media") },
            new() { Kind = ApiFireEvent.Kinds.ClearAll },
            new() { Kind = ApiFireEvent.Kinds.Media, Id = "m1" },
            new() { Kind = ApiFireEvent.Kinds.Overlay, Id = "o1" },
            new() { Kind = ApiFireEvent.Kinds.DismissOverlay, Id = "o1" },
            new() { Kind = ApiFireEvent.Kinds.Alert, Alert = new ApiFireAlertCommand(Message: "Parking lot") },
            new() { Kind = ApiFireEvent.Kinds.DismissAlert },
            new() { Kind = ApiFireEvent.Kinds.Timer, Id = "t1", Timer = ApiTimerAction.Play },
            new() { Kind = ApiFireEvent.Kinds.Combo, Id = "c1" },
            new() { Kind = ApiFireEvent.Kinds.OutputPreset, Id = null },
            new() { Kind = ApiFireEvent.Kinds.AudioItem, Id = "a1" },
            new() { Kind = ApiFireEvent.Kinds.AudioPlaylist, Id = "pl1", EntryId = "e2" },
            new() { Kind = ApiFireEvent.Kinds.StopAudio },
            new() { Kind = ApiFireEvent.Kinds.AudioTransport, AudioTransport = new ApiAudioTransportCommand("next") },
            new() { Kind = ApiFireEvent.Kinds.MediaTransport, Id = "m1", MediaTransport = new ApiMediaTransportCommand("pause") },
        ];
        Assert.Equal(
            ApiFireKinds.All.Except([ApiFireEvent.Kinds.Role, ApiFireEvent.Kinds.Capture, ApiFireEvent.Kinds.Quit]).ToHashSet(),
            fires.Select(f => f.Kind).ToHashSet()); // every fire kind replays
        Assert.False(FollowReplay.Replays(new ApiFireEvent { Kind = ApiFireEvent.Kinds.Role, Term = 2, InCharge = "booth" })); // who is in charge is never a fire
        Assert.False(FollowReplay.Replays(new ApiFireEvent { Kind = ApiFireEvent.Kinds.Capture, Id = "stream1" })); // never starts the booth's stream
        Assert.False(FollowReplay.Replays(Event(ApiFireEvent.Kinds.Quit))); // the booth quitting is never a fire
        foreach (var fire in fires) await FollowReplay.Apply(fire, bridge);
        Assert.Equal(
            [
                "fireSlide:p1", "advance:-1", "clear:media", "clearAll", "fireMedia:m1", "fireOverlay:o1", "dismissOverlay:o1",
                "fireAlert:Parking lot", "dismissAlert", "timer:t1:play", "fireActionCombo:c1", "activatePreset:none",
                "fireAudioItem:a1", "firePlaylist:pl1", "stopAudio", "audioTransport:next", "mediaTransport:m1:pause",
            ],
            bridge.Calls);
    }

    [Fact]
    public void CatchUpRunsOnlyWhatDiffersFromTheBooth()
    {
        var booth = Show("""{"mediaLayers":{"background":{"mediaId":"loop"},"foreground":{"mediaId":"walkin"}},"overlays":[{"id":"lower","name":"Lower third"}]}""");
        var here = Show("""{"liveSlide":{"presentationId":"p1","slideId":"s1"},"mediaLayers":{"background":{"mediaId":"loop"},"loopingVideos":{"mediaId":"old"}},"overlays":[{"id":"logo","name":"Logo"}]}""");
        var events = FollowReplay.CatchUp(booth, here);
        Assert.Equal(
            [ApiFireEvent.Kinds.Clear, ApiFireEvent.Kinds.Clear, ApiFireEvent.Kinds.DismissOverlay, ApiFireEvent.Kinds.Media, ApiFireEvent.Kinds.Overlay],
            events.Select(e => e.Kind));
        Assert.Equal(new ApiClearCommand(Function: "slides"), events[0].Clear); // the booth shows no slide
        Assert.Equal(new ApiClearCommand(Layer: "loopingVideos"), events[1].Clear); // a video the booth doesn't show comes down
        Assert.Equal("logo", events[2].Id);
        Assert.Equal("walkin", events[3].Id); // the loop already in step keeps playing
        Assert.Equal("lower", events[4].Id);
        Assert.Empty(FollowReplay.CatchUp(here, here)); // in step: nothing runs
    }

    [Fact]
    public void AStandbyTakeoverPutsUpWhatTheBoothShowedWhereItIsNow()
    {
        var booth = Show("""{"liveSlide":{"presentationId":"p1","slideId":"s2","serviceItemId":"item-9","occurrence":1},"mediaLayers":{"background":{"mediaId":"loop"}},"overlays":[{"id":"lower","name":"Lower third"}]}""");
        var clear = Show("""{"mediaLayers":{},"overlays":[]}""");
        var read = DateTimeOffset.FromUnixTimeSeconds(1000);
        var standby = new FollowStandby(
            booth,
            [
                new ApiTransportRow("loop", "Loop", "background", Playing: true, Looping: true, Position: 8, Duration: 10),
                new ApiTransportRow("held", "Held", "foreground", Playing: false, Looping: false, Position: 30, Duration: 60),
            ],
            new ApiAudioStatus(
            [
                new ApiAudioBus(null, "song", "Song", true, 40, 200),
                new ApiAudioBus("list", null, "Track 3", true, 5, 90, EntryId: "e3"),
                new ApiAudioBus("older", null, "Track 1", true, 5, 90),
            ]),
            read);
        var events = standby.Takeover(clear, read.AddSeconds(5));
        Assert.Equal(
            [
                ApiFireEvent.Kinds.Media, ApiFireEvent.Kinds.Overlay, ApiFireEvent.Kinds.Slide, ApiFireEvent.Kinds.MediaTransport,
                ApiFireEvent.Kinds.MediaTransport, ApiFireEvent.Kinds.MediaTransport, ApiFireEvent.Kinds.AudioItem,
                ApiFireEvent.Kinds.AudioTransport, ApiFireEvent.Kinds.AudioPlaylist, ApiFireEvent.Kinds.AudioTransport,
            ],
            events.Select(e => e.Kind));
        Assert.Equal("s2", events[2].Slide?.SlideId);
        Assert.Equal("item-9", events[2].Slide?.ServiceItemId);
        Assert.Equal(new ApiMediaTransportCommand("seek", 3), events[3].MediaTransport); // 8 s + 5 s round a 10 s loop
        Assert.Equal(new ApiMediaTransportCommand("seek", 30), events[4].MediaTransport); // a paused video stays where it was
        Assert.Equal("pause", events[5].MediaTransport?.Action);
        Assert.Equal("song", events[6].Id);
        Assert.Equal(new ApiAudioTransportCommand("seek", 45), events[7].AudioTransport);
        Assert.Equal("list", events[8].Id);
        Assert.Equal("e3", events[8].EntryId); // the song the booth was on
        Assert.Equal(new ApiAudioTransportCommand("seek", 10), events[9].AudioTransport);
        Assert.DoesNotContain(events, e => e.Id == "older"); // a booth that doesn't say which song
    }

    [Fact]
    public void AVideoNeverRunsPastItsEnd()
    {
        var read = DateTimeOffset.FromUnixTimeSeconds(1000);
        Assert.Equal(60, FollowStandby.Position(50, 60, playing: true, looping: false, read, read.AddSeconds(30)));
        Assert.Equal(80, FollowStandby.Position(50, null, playing: true, looping: false, read, read.AddSeconds(30)));
        Assert.Equal(50, FollowStandby.Position(50, 60, playing: true, looping: false, read, read.AddSeconds(-3))); // a clock that stepped back
    }

    [Fact]
    public void AStandbyClearsOnlyWhenSomethingShows()
    {
        var clear = Show("""{"mediaLayers":{},"overlays":[]}""");
        var silent = new ApiAudioStatus([new ApiAudioBus(null, null, null, false, null, null)]);
        Assert.False(FollowStandby.ShowsAnything(clear, silent));
        var playing = new ApiAudioStatus([silent.Buses[0] with { Playing = true }]);
        Assert.True(FollowStandby.ShowsAnything(clear, playing)); // music playing
    }

    [Fact]
    public void ALibraryFireReplaysBySlideNotAsAServiceItem()
    {
        var library = FollowReplay.Command(new ApiFireSlideCommand("p1", "s2", ServiceItemId: "p1", Occurrence: 3));
        Assert.Null(library.ServiceItemId);
        Assert.Equal("s2", library.SlideId);
        var service = FollowReplay.Command(new ApiFireSlideCommand("p1", "s2", ServiceItemId: "item-9", Occurrence: 3));
        Assert.Equal("item-9", service.ServiceItemId); // a service fire keeps its item and position
    }
}

public sealed class FollowPairingTests
{
    private static byte[] Body(string code) =>
        JsonSerializer.SerializeToUtf8Bytes(new ApiFollowPairRequest(code, "backup1", "Backup Mac"), ApiJson.Options);

    private static string Key(ApiHandlerResponse answer) =>
        JsonSerializer.Deserialize<ApiFollowPairResponse>(answer.Body, ApiJson.Options)!.Token;

    [Fact]
    public void TheRightCodeGetsAnOperateKeyNamedForTheBackup()
    {
        var tokens = TestServer.NewTokens();
        var pairing = new FollowPairing("4817");
        var answer = pairing.Answer(Body("4817"), tokens);
        Assert.Equal(200, answer.Status);
        var key = Key(answer);
        var token = tokens.Verify(key);
        Assert.NotNull(token);
        Assert.Equal(ApiScope.Control, token.Scope);
        Assert.Equal("Backup: Backup Mac", token.Name);
        var second = Key(pairing.Answer(Body("4817"), tokens));
        Assert.Null(tokens.Verify(key)); // pairing again replaces the old key
        Assert.NotNull(tokens.Verify(second));
        Assert.Single(tokens.All, t => t.Name == "Backup: Backup Mac");
    }

    [Fact]
    public async Task TheBoothSeesWhichBackupsArePinging()
    {
        var seen = new ConcurrentQueue<string[]>();
        var presence = new FollowPresence(TimeSpan.FromMilliseconds(150), names => seen.Enqueue([.. names.Order()]));
        presence.Heard("Companion");
        presence.Heard("Backup: Backup Mac");
        presence.Heard("Backup: Backup Mac");
        Assert.Equal([["Backup Mac"]], seen.ToArray()); // a key that isn't a backup's never counts; a second ping changes nothing
        for (var i = 0; i < 50 && seen.Count < 2; i++) await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Equal([["Backup Mac"], []], seen.ToArray()); // quiet past lostAfter: dropped
    }

    [Fact]
    public async Task StopBackingUpForgetsTheKeyItCalledWith()
    {
        var tokens = TestServer.NewTokens();
        var key = tokens.Create("Backup: Backup Mac", ApiScope.Control).Secret;
        using var booth = FollowServer.Start(tokens, new FollowHooks(Unpair: t => tokens.Revoke(t.Id)));
        var (status, _) = await booth.CallAsync(HttpMethod.Post, "/v1/follow/unpair", key);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Null(tokens.Verify(key));
    }

    [Fact]
    public void FiveWrongCodesRetireTheCodeUntilANewOne()
    {
        var tokens = TestServer.NewTokens();
        var pairing = new FollowPairing("4817");
        for (var i = 0; i < FollowPairing.MaxAttempts; i++) Assert.Equal(403, pairing.Answer(Body("0000"), tokens).Status);
        Assert.Null(pairing.Current);
        Assert.Equal(403, pairing.Answer(Body("4817"), tokens).Status); // even the right code, once retired
        var fresh = pairing.Refresh();
        Assert.Equal(200, pairing.Answer(Body(fresh), tokens).Status);
        Assert.Single(tokens.All);
    }
}

public sealed class FollowHoldTests
{
    [Fact]
    public async Task ABackupAnswersReadsAndHoldsEverythingElseOverRestAndPairsThroughItsDoor()
    {
        var tokens = TestServer.NewTokens();
        var secret = tokens.Create("Companion", ApiScope.Control).Secret;
        var pairing = new FollowPairing("1234");
        ApiRoute[] routes =
        [
            new("GET", "/v1/show", ApiScope.View, "show", "", "", null, new System.Text.Json.Nodes.JsonObject(), _ => Task.FromResult(ApiHandlerResponse.Ok)),
            new("POST", "/v1/show/next", ApiScope.Control, "next", "", "", null, new System.Text.Json.Nodes.JsonObject(), _ => Task.FromResult(ApiHandlerResponse.Ok)),
        ];
        using var server = FollowServer.Start(tokens, new FollowHooks(
            Hold: route => FollowHold.Error(route, "Booth Mac"),
            Pair: body => pairing.Answer(body, tokens)), routes);
        Assert.Equal(HttpStatusCode.OK, (await server.CallAsync(HttpMethod.Get, "/v1/show", secret)).Status);
        var next = await server.CallAsync(HttpMethod.Post, "/v1/show/next", secret);
        Assert.Equal(HttpStatusCode.Conflict, next.Status);
        Assert.Null(FollowHold.Error(
            new ApiRoute("POST", "/v1/follow/takeover", ApiScope.Control, "t", "", "", null, new System.Text.Json.Nodes.JsonObject(), _ => Task.FromResult(ApiHandlerResponse.Ok)),
            "Booth Mac")); // Companion can still take over
        Assert.Equal("backing_up", JsonDocument.Parse(next.Body).RootElement.GetProperty("code").GetString());
        var paired = await server.CallAsync(HttpMethod.Post, "/v1/follow/pair", null,
            JsonSerializer.Serialize(new ApiFollowPairRequest("1234", "b", "Lobby Mac"), ApiJson.Options));
        Assert.Equal(HttpStatusCode.OK, paired.Status); // pairing needs no key
    }
}

public sealed class FollowEndToEndTests
{
    [Fact]
    public async Task ABackupPairsByCodeFollowsTheBoothsFiresAndRunsOnlyTheRoots()
    {
        var ct = TestContext.Current.CancellationToken;
        var tokens = TestServer.NewTokens();
        var pairing = new FollowPairing("2468");
        using var booth = FollowServer.Start(tokens, new FollowHooks(Pair: body => pairing.Answer(body, tokens)));
        var (_, data) = await booth.CallAsync(HttpMethod.Post, "/v1/follow/pair", null,
            JsonSerializer.Serialize(new ApiFollowPairRequest("2468", "backup", "Backup Mac"), ApiJson.Options));
        var key = JsonSerializer.Deserialize<ApiFollowPairResponse>(data, ApiJson.Options)!.Token;

        await using var client = new LocalApiClient(new LocalApiClient.Endpoint("127.0.0.1", booth.Port, key), [ApiTopic.Fires]);
        client.Start();
        var bridge = new FakeBridge();
        var combo = new ApiFireEvent { Kind = ApiFireEvent.Kinds.Combo, Id = "c1", Seq = 1 };
        var effect = new ApiFireEvent { Kind = ApiFireEvent.Kinds.OutputPreset, Id = "p1", Seq = 2, Cause = 1 };
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct);
        watchdog.CancelAfter(TimeSpan.FromSeconds(10));
        Task? publisher = null;
        try
        {
            await foreach (var update in client.Updates.ReadAllAsync(watchdog.Token))
            {
                if (update is LocalApiClient.Update.HealthChanged { Health: LocalApiClient.Health.Live } && publisher is null)
                {
                    publisher = Task.Run(async () =>
                    {
                        while (!watchdog.IsCancellationRequested)
                        {
                            foreach (var fire in new[] { combo, effect }) booth.Server.Events.Publish(new ApiEvent(ApiTopic.Fires, ApiFireFeed.Encode(fire)));
                            await Task.Delay(100, CancellationToken.None);
                        }
                    }, CancellationToken.None);
                }
                else if (update is LocalApiClient.Update.Event { Topic: ApiTopic.Fires } fired
                         && ApiFireFeed.Decode(fired.Data) is { } fire && FollowReplay.Replays(fire))
                {
                    await FollowReplay.Apply(fire, bridge);
                }
                if (bridge.Calls.Count > 0) break;
            }
        }
        finally
        {
            await watchdog.CancelAsync();
            if (publisher is not null) await publisher;
        }
        Assert.Equal("fireActionCombo:c1", bridge.Calls.FirstOrDefault()); // the combo replays
        Assert.DoesNotContain("activatePreset:p1", bridge.Calls); // its preset switch follows from it here, never replayed
    }
}

public sealed class FollowFilesTests
{
    private static readonly string Sha = string.Concat(Enumerable.Repeat("ab", 32));

    [Fact]
    public void TheBackupFetchesChangedItemsAndMissingFilesOnly()
    {
        FollowFiles.Item[] items =
        [
            new("presentation", "same", ["h1"]),
            new("presentation", "changed", ["h2"]),
            new("media", "new", ["h3"], File: Sha, Ext: "mp4"),
            new("media", "fileHere", ["h4"], File: "cd", Ext: "mov"),
        ];
        var heads = new Dictionary<string, IReadOnlyList<string>>
        {
            ["presentation/same"] = ["h1"], ["presentation/changed"] = ["h1"], ["media/fileHere"] = ["h4"],
        };
        var (documents, files) = FollowFiles.Wanted(items, heads, new HashSet<string> { "cd" });
        Assert.Equal(["changed", "new"], documents.Select(d => d.Id));
        Assert.Equal([Sha], files);
    }

    [Fact]
    public void CopiesTheBoothNoLongerHoldsGo()
    {
        FollowFiles.Item[] booth = [new("presentation", "kept", ["h1"])];
        Assert.Equal(["media/m1", "presentation/deleted"],
            FollowFiles.Gone(new HashSet<string> { "presentation/kept", "presentation/deleted", "media/m1" }, booth));
        Assert.Empty(FollowFiles.Gone(new HashSet<string>(), booth)); // nothing this backup didn't copy
    }

    [Fact]
    public void AnItemsAreaRidesAlongAndAnOlderBoothsListStillReads()
    {
        var shared = new FollowFiles.Item("presentation", "d1", ["h1"], Area: "team");
        Assert.Equal("team", JsonSerializer.Deserialize<FollowFiles.Item>(ApiJson.Encode(shared), ApiJson.Options)!.Area);
        Assert.Null(JsonSerializer.Deserialize<FollowFiles.Item>("""{"kind":"presentation","id":"d1","heads":["h1"]}""", ApiJson.Options)!.Area);
    }

    [Fact]
    public void ADocumentsBytesGoAsBase64LikeTheMacsData()
    {
        // Swift encodes Data as base64, so a Windows backup reads a Mac booth's document (and back).
        var json = Encoding.UTF8.GetString(ApiJson.Encode(new FollowFiles.Document([1, 2, 3])));
        Assert.Equal("""{"bytes":"AQID"}""", json);
    }

    [Fact]
    public void AFileNameIsAHashNeverAPath()
    {
        Assert.True(FollowFiles.IsHash(Sha));
        Assert.False(FollowFiles.IsHash("../../secrets"));
        Assert.False(FollowFiles.IsHash(Sha.ToUpperInvariant()));
    }

    [Fact]
    public async Task TheBoothStreamsAFileToAnOperateKeyOnly()
    {
        var tokens = TestServer.NewTokens();
        var operate = tokens.Create("Backup Mac", ApiScope.Control).Secret;
        var watch = tokens.Create("Viewer", ApiScope.View).Secret;
        var file = Path.Combine(Path.GetTempPath(), $"{Sha}-{Guid.NewGuid():N}.mp4");
        await File.WriteAllTextAsync(file, "clip", TestContext.Current.CancellationToken);
        try
        {
            using var booth = FollowServer.Start(tokens, new FollowHooks(Blob: hash => hash == Sha ? file : null));
            var (status, data) = await booth.CallAsync(HttpMethod.Get, $"/v1/follow/blobs/{Sha}", operate);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("clip", Encoding.UTF8.GetString(data));
            Assert.Equal(HttpStatusCode.Forbidden, (await booth.CallAsync(HttpMethod.Get, $"/v1/follow/blobs/{Sha}", watch)).Status); // a Watch key never downloads media
            Assert.Equal(HttpStatusCode.NotFound, (await booth.CallAsync(HttpMethod.Get, $"/v1/follow/blobs/{new string('0', 64)}", operate)).Status);
        }
        finally
        {
            File.Delete(file);
        }
    }
}

public sealed class ApiFireFeedTests
{
    private static (ApiFireFeed Feed, ConcurrentQueue<ApiFireEvent> Sent) Feed()
    {
        var sent = new ConcurrentQueue<ApiFireEvent>();
        return (new ApiFireFeed(() => 42) { Sink = sent.Enqueue }, sent);
    }

    [Fact]
    public void EachPublishIsOneStampedEvent()
    {
        var (feed, sent) = Feed();
        foreach (var kind in ApiFireKinds.All) feed.Publish(new ApiFireEvent { Kind = kind, Id = kind.Raw() });
        Assert.Equal(ApiFireKinds.All, sent.Select(e => e.Kind));
        Assert.Equal(Enumerable.Range(1, ApiFireKinds.All.Count), sent.Select(e => e.Seq));
        Assert.Equal([42.0], sent.Select(e => e.At).Distinct());
        Assert.All(sent, e => Assert.Equal(new ApiFireSource(ApiFireSource.Kinds.Ui), e.Source)); // unbound = someone here
        Assert.All(sent, e => Assert.Null(e.Cause));
    }

    [Fact]
    public void TheEntryPointsSourceRidesTheFire()
    {
        var (feed, sent) = Feed();
        ApiFireSource.Kinds[] kinds =
            [ApiFireSource.Kinds.Scheduler, ApiFireSource.Kinds.Midi, ApiFireSource.Kinds.Api, ApiFireSource.Kinds.Timeline, ApiFireSource.Kinds.Timecode, ApiFireSource.Kinds.Keyboard];
        foreach (var kind in kinds)
            ApiFireSource.With(new ApiFireSource(kind, "Companion"), () => feed.Publish(new ApiFireEvent { Kind = ApiFireEvent.Kinds.Slide }));
        Assert.Equal(kinds, sent.Select(e => e.Source.Kind));
        Assert.Equal("Companion", sent.First().Source.Name);
    }

    [Fact]
    public async Task EffectsCarryTheirFiresSeq()
    {
        var (feed, sent) = Feed();
        var slide = feed.Publish(new ApiFireEvent { Kind = ApiFireEvent.Kinds.Slide });
        Task? delayed = null;
        ApiFireFeed.WithCause(slide, () =>
        {
            feed.Publish(new ApiFireEvent { Kind = ApiFireEvent.Kinds.OutputPreset, Id = "stream" });
            delayed = Task.Run(() => feed.Publish(new ApiFireEvent { Kind = ApiFireEvent.Kinds.Media, Id = "bumper" }), TestContext.Current.CancellationToken);
        });
        await delayed!;
        feed.Publish(new ApiFireEvent { Kind = ApiFireEvent.Kinds.ClearAll });
        Assert.Equal([null, slide, slide, null], sent.Select(e => e.Cause));
    }

    [Fact]
    public void WithTheApiOffPublishOnlyCounts()
    {
        var feed = new ApiFireFeed();
        Assert.Equal(1, feed.Publish(new ApiFireEvent { Kind = ApiFireEvent.Kinds.Slide }));
        Assert.Equal(2, feed.Publish(new ApiFireEvent { Kind = ApiFireEvent.Kinds.Slide }));
    }

    [Fact]
    public void TheWireRoundTrips()
    {
        var fire = new ApiFireEvent
        {
            Kind = ApiFireEvent.Kinds.Slide, Slide = new ApiFireSlideCommand("p", "s", ServiceItemId: "i", Occurrence: 2),
            Settled = true, SlideActions = false, Seq = 7, At = 1234.5, Source = new ApiFireSource(ApiFireSource.Kinds.Scheduler), Cause = 3,
        };
        var data = ApiFireFeed.Encode(fire);
        Assert.Equal(fire, ApiFireFeed.Decode(data));
        Assert.DoesNotContain("\"timer\"", Encoding.UTF8.GetString(data)); // absent fields stay off the wire
    }

    [Fact]
    public void AMacsFireReads()
    {
        // As the Mac's JSONEncoder writes one (sorted keys, raw-value enums).
        var mac = """{"at":12.5,"cause":3,"clear":{"layer":"loopingVideos"},"kind":"clear","seq":8,"source":{"kind":"timecode","name":"LTC"}}""";
        var fire = ApiFireFeed.Decode(Encoding.UTF8.GetBytes(mac));
        Assert.NotNull(fire);
        Assert.Equal((ApiFireEvent.Kinds.Clear, 8, 3, ApiFireSource.Kinds.Timecode, "loopingVideos"), (fire.Kind, fire.Seq, fire.Cause, fire.Source.Kind, fire.Clear?.Layer));
        Assert.Equal(mac, Encoding.UTF8.GetString(ApiFireFeed.Encode(fire)));
    }

    [Fact]
    public void TheFiresTopicDocumentsEveryKind()
    {
        var spec = OpenApiDocument.Compose([], schemaVersion: 1);
        var kinds = spec["components"]!["schemas"]!["FireEvent"]!["properties"]!["kind"]!["enum"]!.AsArray().Select(k => (string?)k);
        Assert.Equal(ApiFireKinds.All.Select(k => k.Raw()), kinds);
    }
}

public sealed class DiscoveryTests
{
    [Fact]
    public void TxtCarriesTheIds()
    {
        var info = new ApiServerInfo("Booth Mac", "MxU Slides", "1", 1, StationHexId: "st1", StationName: "Booth");
        Assert.Equal(new Dictionary<string, string> { ["stationHexId"] = "st1", ["stationName"] = "Booth" }, BonjourAdvertiser.Txt(info));
        Assert.Equal([16, .. "stationHexId=st1"u8.ToArray()], BonjourAdvertiser.TxtRecord(new Dictionary<string, string> { ["stationHexId"] = "st1" }));
        Assert.Empty(BonjourAdvertiser.Txt(new ApiServerInfo("Booth Mac", "MxU Slides", "1", 1)));
        Assert.Equal([0], BonjourAdvertiser.TxtRecord(new Dictionary<string, string>()));
    }

    [Fact]
    public async Task PingCarriesTheIdsAndFollowsAnUpdate()
    {
        using var server = FollowServer.Start(TestServer.NewTokens(), new FollowHooks());
        async Task<ApiServerInfo> Ping() =>
            JsonSerializer.Deserialize<ApiServerInfo>((await server.CallAsync(HttpMethod.Get, "/v1/ping", null)).Body, ApiJson.Options)!;
        Assert.Null((await Ping()).StationHexId);
        server.Server.UpdateIdentity("st1", "Booth");
        var info = await Ping();
        Assert.Equal(("st1", "Booth"), (info.StationHexId, info.StationName));
    }

    [Fact]
    public void TheBrowserListsOnlyOtherComputers()
    {
        BonjourBrowser.Candidate? Candidate(Dictionary<string, string> txt) => BonjourBrowser.CandidateFrom("Booth Mac", txt, "me");
        Assert.Equal(new BonjourBrowser.Candidate("Booth Mac", "booth"), Candidate(new() { ["stationHexId"] = "booth" }));
        Assert.Equal("Booth", Candidate(new() { ["stationHexId"] = "booth", ["stationName"] = "Booth" })?.StationName);
        Assert.Null(Candidate(new() { ["stationHexId"] = "me" })); // this computer
        Assert.Null(Candidate([])); // no id yet
    }

    [Fact]
    public async Task LiveBrowseFindsAnAdvertisedComputer()
    {
        // Real DNS-SD on this PC's network, as the Mac's LOCALAPI_LIVE_BONJOUR test.
        Assert.SkipUnless(Environment.GetEnvironmentVariable("LOCALAPI_LIVE_BONJOUR") == "1", "Set LOCALAPI_LIVE_BONJOUR=1 to browse the real network.");
        var info = new ApiServerInfo($"F1 Booth {Guid.NewGuid().ToString()[..4]}", "MxU Slides", "1", 1, StationHexId: "booth");
        using var server = new LocalApiServer(info) { Advertise = true };
        Assert.True(server.Start(0, [], TestServer.NewTokens()), server.LastError);
        using var browser = new BonjourBrowser("backup");
        browser.Start();
        BonjourBrowser.Peer? found = null;
        for (var i = 0; i < 100 && found is null; i++)
        {
            found = browser.Peers.FirstOrDefault(p => p.StationHexId == "booth");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        Assert.Equal(server.Port, found?.Port);
    }
}

public sealed class LocalApiClientTests
{
    private static readonly LocalApiClient.Endpoint Nowhere = new("127.0.0.1", 1, "mxu_x");

    private static async Task<List<LocalApiClient.Health>> HealthUntil(LocalApiClient client, LocalApiClient.Health end)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var seen = new List<LocalApiClient.Health>();
        await foreach (var update in client.Updates.ReadAllAsync(timeout.Token))
        {
            if (update is not LocalApiClient.Update.HealthChanged { Health: var health }) continue;
            seen.Add(health);
            if (health == end) break;
        }
        return seen;
    }

    [Fact]
    public void BackoffDoublesToItsCeilingAndStopsAtTheCap()
    {
        var policy = new LocalApiClient.Policy();
        Assert.Equal(
            [
                TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8),
                TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), null,
            ],
            Enumerable.Range(1, 10).Select(policy.DelayAfterFailures));
    }

    [Fact]
    public async Task AnUnreachableComputerGivesUpAfterTenConnects()
    {
        var connects = 0;
        var slept = new ConcurrentQueue<TimeSpan>();
        await using var client = new LocalApiClient(Nowhere, [ApiTopic.Fires],
            connect: (_, _) =>
            {
                Interlocked.Increment(ref connects);
                throw new HttpRequestException("cannot connect");
            },
            sleep: (delay, _) =>
            {
                slept.Enqueue(delay);
                return Task.CompletedTask;
            });
        client.Start();
        var health = await HealthUntil(client, new LocalApiClient.Health.GaveUp());
        Assert.Equal(new LocalApiClient.Health.GaveUp(), health[^1]);
        Assert.DoesNotContain(new LocalApiClient.Health.Live(), health);
        Assert.Equal(10, connects);
        Assert.Equal(Enumerable.Range(1, 9).Select(n => new LocalApiClient.Policy().DelayAfterFailures(n)!.Value), slept);
    }

    [Fact]
    public async Task ADroppedLiveSessionReconnectsFromTheFirstDelay()
    {
        var connects = 0;
        var slept = new ConcurrentQueue<TimeSpan>();
        await using var client = new LocalApiClient(Nowhere, [ApiTopic.Fires], new LocalApiClient.Policy { PingInterval = TimeSpan.FromSeconds(7) },
            connect: (_, _) => Interlocked.Increment(ref connects) == 1
                ? Task.FromResult<ILocalApiClientSocket>(new FakeSocket("""{"type":"hello","scope":"control"}"""))
                : throw new HttpRequestException("cannot connect"),
            sleep: (delay, _) =>
            {
                slept.Enqueue(delay);
                return Task.CompletedTask;
            });
        client.Start();
        var health = await HealthUntil(client, new LocalApiClient.Health.GaveUp());
        Assert.Equal(
            [new LocalApiClient.Health.Connecting(), new LocalApiClient.Health.Live(), new LocalApiClient.Health.Lost(LocalApiClient.Loss.Closed)],
            health.Take(3));
        Assert.Equal(TimeSpan.FromMilliseconds(500), slept.First(d => d != TimeSpan.FromSeconds(7)));
        Assert.Equal(10, connects); // the live session plus nine more
    }

    [Fact]
    public void AStoppedBeatReadsFrozenAndSilenceReadsLost()
    {
        var monitor = new HeartbeatMonitor(null, 0);
        monitor.Pong(5, 0.5);
        Assert.Equal(HeartbeatMonitor.Reading.Live, monitor.ReadingAt(1));
        monitor.Pong(5, 1.5);
        monitor.Pong(5, 2.4);
        Assert.Equal(HeartbeatMonitor.Reading.Live, monitor.ReadingAt(2.4)); // the beat moved 1.9 s ago
        monitor.Pong(5, 2.6);
        Assert.Equal(HeartbeatMonitor.Reading.Frozen, monitor.ReadingAt(2.6)); // pongs still come; the beat stopped over 2 s ago
        monitor.Pong(6, 2.7);
        Assert.Equal(HeartbeatMonitor.Reading.Live, monitor.ReadingAt(2.7));
        Assert.Equal(HeartbeatMonitor.Reading.NoAnswer, monitor.ReadingAt(5.8)); // no pong for over 3 s

        var cut = new HeartbeatMonitor(null, 0);
        cut.Pong(1, 0.5);
        Assert.Equal(HeartbeatMonitor.Reading.Live, cut.ReadingAt(3.4)); // silence with a moving beat is a link going quiet, not a freeze
        Assert.Equal(HeartbeatMonitor.Reading.NoAnswer, cut.ReadingAt(3.6));

        var beatless = new HeartbeatMonitor(null, 0);
        beatless.Pong(null, 2.9);
        Assert.Equal(HeartbeatMonitor.Reading.Live, beatless.ReadingAt(2.9)); // a sender without beats never reads frozen
    }

    [Fact]
    public async Task EndToEndFiresAndBeat()
    {
        var ct = TestContext.Current.CancellationToken;
        long beat = 1;
        var heard = new ConcurrentQueue<string>();
        var tokens = TestServer.NewTokens();
        var secret = tokens.Create("Backup Mac", ApiScope.Control).Secret;
        using var running = FollowServer.Start(tokens, new FollowHooks(Beat: () => (ulong)Interlocked.Read(ref beat), Heard: heard.Enqueue));
        await using var client = new LocalApiClient(new LocalApiClient.Endpoint("127.0.0.1", running.Port, secret), [ApiTopic.Fires],
            new LocalApiClient.Policy { PingInterval = TimeSpan.FromMilliseconds(50), Heartbeat = new HeartbeatMonitor.Limits(LostAfter: 2, FrozenAfter: 0.3) });
        client.Start();
        var seen = new List<LocalApiClient.Update>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        async Task<LocalApiClient.Update> Next(Func<LocalApiClient.Update, bool> match)
        {
            await foreach (var update in client.Updates.ReadAllAsync(timeout.Token))
            {
                seen.Add(update);
                if (match(update)) return update;
            }
            throw new InvalidOperationException("The updates ended.");
        }

        await Next(u => u is LocalApiClient.Update.HealthChanged { Health: LocalApiClient.Health.Live });
        for (var i = 0; i < 50 && heard.IsEmpty; i++) await Task.Delay(20, ct);
        Assert.Equal("Backup Mac", heard.FirstOrDefault()); // each ping names its key to the booth

        var fire = new ApiFireEvent
        {
            Kind = ApiFireEvent.Kinds.Media, Id = "bumper", ServiceItemId = "item-3", Seq = 11, Source = new ApiFireSource(ApiFireSource.Kinds.Scheduler),
        };
        var payload = ApiFireFeed.Encode(fire);
        using var stopPublishing = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var publisher = Task.Run(async () =>
        {
            while (!stopPublishing.IsCancellationRequested)
            {
                running.Server.Events.Publish(new ApiEvent(ApiTopic.Fires, payload));
                await Task.Delay(50, CancellationToken.None);
            }
        }, CancellationToken.None);
        var arrived = (LocalApiClient.Update.Event)await Next(u => u is LocalApiClient.Update.Event { Topic: ApiTopic.Fires });
        await stopPublishing.CancelAsync();
        await publisher;
        Assert.Equal(fire, ApiFireFeed.Decode(arrived.Data));

        await Next(u => u is LocalApiClient.Update.HealthChanged { Health: LocalApiClient.Health.Lost { Why: LocalApiClient.Loss.Frozen } });
        using var stopTicking = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var ticker = Task.Run(async () =>
        {
            while (!stopTicking.IsCancellationRequested)
            {
                Interlocked.Increment(ref beat);
                await Task.Delay(20, CancellationToken.None);
            }
        }, CancellationToken.None);
        await Next(u => u is LocalApiClient.Update.HealthChanged { Health: LocalApiClient.Health.Live }); // the beat moving again reads live
        await stopTicking.CancelAsync();
        await ticker;
    }

    [Fact]
    public async Task AWrongTokenNeverGoesLive()
    {
        using var running = FollowServer.Start(TestServer.NewTokens(), new FollowHooks());
        await using var client = new LocalApiClient(new LocalApiClient.Endpoint("127.0.0.1", running.Port, "mxu_wrong"), [ApiTopic.Fires],
            new LocalApiClient.Policy { MaxAttempts = 2, BaseDelay = TimeSpan.FromMilliseconds(10) });
        client.Start();
        var health = await HealthUntil(client, new LocalApiClient.Health.GaveUp());
        Assert.Equal(new LocalApiClient.Health.GaveUp(), health[^1]);
        Assert.DoesNotContain(new LocalApiClient.Health.Live(), health);
    }

    private sealed class FakeSocket(params string[] frames) : ILocalApiClientSocket
    {
        private readonly ConcurrentQueue<string> frames = new(frames);

        public Task SendAsync(string text, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string> ReceiveAsync(CancellationToken cancellationToken) =>
            frames.TryDequeue(out var frame) ? Task.FromResult(frame) : throw new System.Net.WebSockets.WebSocketException("connection lost");

        public void Dispose()
        {
        }
    }
}

/// <summary>A server with follow hooks on a free port, as the Mac tests' RunningServer.</summary>
internal sealed class FollowServer : IDisposable
{
    private readonly HttpClient http = new();

    private FollowServer(LocalApiServer server) => Server = server;

    public LocalApiServer Server { get; }
    public int Port => Server.Port;

    public static FollowServer Start(ApiTokenStore tokens, FollowHooks hooks, IReadOnlyList<ApiRoute>? routes = null)
    {
        var server = new LocalApiServer(new ApiServerInfo("Booth Mac", "MxU Slides", "1", 1)) { Follow = hooks };
        Assert.True(server.Start(0, routes ?? [], tokens), server.LastError);
        return new FollowServer(server);
    }

    public async Task<(HttpStatusCode Status, byte[] Body)> CallAsync(HttpMethod method, string path, string? key, string? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri($"http://127.0.0.1:{Port}{path}"));
        if (key is not null) request.Headers.Authorization = new("Bearer", key);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    public void Dispose()
    {
        Server.Dispose();
        http.Dispose();
    }
}
