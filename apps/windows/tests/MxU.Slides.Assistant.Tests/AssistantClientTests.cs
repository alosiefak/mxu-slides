using System.Text.Json;
using MxU.Slides.Assistant.Testing;

namespace MxU.Slides.Assistant.Tests;

/// <summary>
/// The foundation, against a stand-in server on this PC (never the real API): what a request
/// carries, how each kind of failure reads to the operator, and that the key stays out of the
/// log and the address.
/// </summary>
public sealed class AssistantClientTests : IDisposable
{
    internal const string Key = "sk-ant-api03-test-0123456789abcdefghij";

    private const string Schema = """{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"],"additionalProperties":false}""";

    private readonly string folder = Path.Combine(Path.GetTempPath(), $"MxU assistant tests {Guid.NewGuid():N}");
    private readonly FakeAnthropicServer server = new();
    private readonly AssistantLog log;
    private readonly AssistantKeyVault vault;
    private bool on = true;
    private string model = AssistantModels.Default;

    public AssistantClientTests()
    {
        log = new AssistantLog(Path.Combine(folder, "log.jsonl"));
        vault = new AssistantKeyVault(Path.Combine(folder, "key.bin"));
        vault.Save(Key);
    }

    public void Dispose()
    {
        server.Dispose();
        try { Directory.Delete(folder, recursive: true); }
        catch (IOException) { }
    }

    private AssistantService Service(Uri? url = null) => new(vault, log, () => on, () => model, url ?? server.Url);

    private static AssistantRequest Job(params AssistantTool[] tools) => new()
    {
        Feature = "Test", Instructions = "Answer in JSON.", Ask = "What is the answer?", Schema = Schema, Tools = tools,
    };

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ARequestCarriesTheModelAdaptiveThinkingTheSchemaAndACacheMark()
    {
        server.ThenText("""{"answer":"42"}""", inputTokens: 1000, outputTokens: 200);
        using var service = Service();

        var reply = await service.Ask(Job(), Cancel);

        Assert.Equal("42", reply.Json.GetProperty("answer").GetString());
        var sent = Assert.Single(server.Requests);
        Assert.EndsWith("/v1/messages", new Uri(sent.Url).AbsolutePath);
        var body = sent.Json;
        Assert.Equal("claude-opus-5-5", body.GetProperty("model").GetString());
        Assert.Equal("adaptive", body.GetProperty("thinking").GetProperty("type").GetString());
        var format = body.GetProperty("output_config").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.False(format.GetProperty("schema").GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("medium", body.GetProperty("output_config").GetProperty("effort").GetString());
        var system = body.GetProperty("system")[0];
        Assert.Equal("Answer in JSON.", system.GetProperty("text").GetString());
        Assert.Equal("ephemeral", system.GetProperty("cache_control").GetProperty("type").GetString());
        // Opus 5.5 rejects these outright, so they must never be sent.
        Assert.False(body.TryGetProperty("temperature", out _));
        Assert.False(body.TryGetProperty("tool_choice", out _));
        Assert.False(body.GetProperty("thinking").TryGetProperty("budget_tokens", out _));
    }

    [Fact]
    public async Task TheCostComesFromTheTokensTheApiReports()
    {
        server.ThenText("""{"answer":"x"}""", inputTokens: 1_000_000, outputTokens: 100_000);
        using var service = Service();

        var reply = await service.Ask(Job(), Cancel);

        // Opus 5.5: $4 a million in, $20 a million out.
        Assert.Equal(6.00m, reply.Usage.Cost(reply.Model));
        Assert.Equal("1,000,000 tokens in, 100,000 out, about $6.00", reply.CostLine);
        Assert.Equal(6.00m, log.Total(DateTimeOffset.Now.AddMinutes(-1)).Cost);
    }

    [Fact]
    public async Task TheChosenModelIsTheOneAsked()
    {
        model = AssistantModels.Quick;
        server.ThenText("""{"answer":"x"}""", inputTokens: 1_000_000, outputTokens: 0);
        using var service = Service();

        var reply = await service.Ask(Job(), Cancel);

        Assert.Equal("claude-haiku-5-5", server.Requests[0].Json.GetProperty("model").GetString());
        Assert.Equal(0.10m, reply.Usage.Cost(reply.Model));
    }

    [Fact]
    public async Task TheKeyTravelsInAHeaderAndNowhereElse()
    {
        server.ThenText("""{"answer":"x"}""");
        using var service = Service();

        await service.Ask(Job() with { Ask = $"My notes mention {Key} by mistake." }, Cancel);

        var sent = server.Requests[0];
        Assert.Equal($"Bearer {Key}", sent.Headers["authorization"]);
        Assert.DoesNotContain(Key, sent.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-ant", sent.Url, StringComparison.Ordinal);
        // The log on disk never holds a key: not ours, and not one pasted into the notes.
        var written = File.ReadAllText(log.FilePath);
        Assert.DoesNotContain("sk-ant-", written, StringComparison.Ordinal);
        Assert.Contains("[key removed]", written, StringComparison.Ordinal);
        // And the vault's file is encrypted, not the key in plain text.
        Assert.DoesNotContain(Key, System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(folder, "key.bin"))), StringComparison.Ordinal);
        Assert.Equal("sk-ant-…ghij", AssistantKeyVault.Masked(Key));
    }

    [Fact]
    public async Task TheLogHoldsWhatWasSentAndWhatCameBack()
    {
        server.ThenText("""{"answer":"42"}""");
        using var service = Service();

        await service.Ask(Job(), Cancel);

        var entry = Assert.Single(log.Entries);
        Assert.Equal("Test", entry.Feature);
        Assert.Contains("Answer in JSON.", entry.Sent, StringComparison.Ordinal);
        Assert.Contains("What is the answer?", entry.Sent, StringComparison.Ordinal);
        Assert.Equal("""{"answer":"42"}""", entry.Received);
        Assert.Null(entry.Problem);
        // A new log object reads the same entry back from the file.
        Assert.Equal(entry.Received, Assert.Single(new AssistantLog(log.FilePath).Entries).Received);
    }

    [Fact]
    public async Task AToolTheModelAsksForRunsHereAndItsAnswerGoesBack()
    {
        var asked = new List<string>();
        var tool = new AssistantTool("look_up", "Looks a word up.",
            new Dictionary<string, JsonElement> { ["word"] = JsonSerializer.SerializeToElement(new { type = "string" }) }, ["word"],
            input =>
            {
                asked.Add(input["word"].GetString()!);
                return "found it";
            });
        server.ThenToolUse("look_up", """{"word":"grace"}""").ThenText("""{"answer":"grace"}""");
        using var service = Service();

        var reply = await service.Ask(Job(tool), Cancel);

        Assert.Equal("grace", reply.Json.GetProperty("answer").GetString());
        Assert.Equal(["grace"], asked);
        Assert.Equal(2, server.Requests.Count);
        Assert.Equal("look_up", server.Requests[0].Json.GetProperty("tools")[0].GetProperty("name").GetString());
        // The second request carries the model's turn and the tool's answer.
        var messages = server.Requests[1].Json.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());
        var result = messages[2].GetProperty("content")[0];
        Assert.Equal("tool_result", result.GetProperty("type").GetString());
        Assert.Contains("found it", result.GetProperty("content").ToString(), StringComparison.Ordinal);
        // Both rounds' tokens are counted, and the look-up is in the log.
        Assert.Equal(400 + 900, reply.Usage.Input);
        Assert.Contains("look_up", Assert.Single(log.Entries).Tools[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABusyAnswerIsTriedAgain()
    {
        server.ThenError(429, "rate_limit_error").ThenError(529, "overloaded_error").ThenText("""{"answer":"third time"}""");
        using var service = Service();

        var reply = await service.Ask(Job(), Cancel);

        Assert.Equal("third time", reply.Json.GetProperty("answer").GetString());
        Assert.Equal(3, server.Requests.Count);
    }

    [Theory]
    [InlineData(429, AssistantProblem.Busy, 3)]
    [InlineData(500, AssistantProblem.ServerTrouble, 3)]
    [InlineData(401, AssistantProblem.BadKey, 1)]
    [InlineData(400, AssistantProblem.Other, 1)]
    public async Task EachFailureHasItsOwnWords(int status, AssistantProblem expected, int attempts)
    {
        for (var i = 0; i < 3; i++) server.ThenError(status);
        using var service = Service();

        var failure = await Assert.ThrowsAsync<AssistantException>(() => service.Ask(Job(), Cancel));

        Assert.Equal(expected, failure.Problem);
        Assert.Equal(attempts, server.Requests.Count);
        Assert.DoesNotContain("sk-ant", failure.Message, StringComparison.Ordinal);
        // The failure is in the log too, with nothing received.
        Assert.Equal(failure.Message, Assert.Single(log.Entries).Problem);
    }

    [Fact]
    public async Task NoNetworkIsACalmMessageNotACrash()
    {
        // Nothing listens on this port: the same as the internet being down.
        using var service = Service(new Uri("http://127.0.0.1:9/"));

        var failure = await Assert.ThrowsAsync<AssistantException>(() => service.Ask(Job() with { Limit = TimeSpan.FromSeconds(20) }, Cancel));

        Assert.Equal(AssistantProblem.Offline, failure.Problem);
        Assert.Contains("Everything else works as usual", failure.Message, StringComparison.Ordinal);
        Assert.False(service.IsBusy);
    }

    [Fact]
    public async Task SwitchedOffOrWithoutAKeyNothingIsSent()
    {
        using var service = Service();
        on = false;
        Assert.Equal(AssistantProblem.Off, service.Unavailable);
        var off = await Assert.ThrowsAsync<AssistantException>(() => service.Ask(Job(), Cancel));
        Assert.True(off.IsQuiet);

        on = true;
        vault.Clear();
        Assert.Equal(AssistantProblem.NoKey, service.Unavailable);
        var keyless = await Assert.ThrowsAsync<AssistantException>(() => service.Ask(Job(), Cancel));
        Assert.True(keyless.IsQuiet);

        Assert.Empty(server.Requests);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task AnAnswerThatIsNotJsonChangesNothing()
    {
        server.ThenText("Sorry, here are some thoughts instead.");
        using var service = Service();

        var failure = await Assert.ThrowsAsync<AssistantException>(() => service.Ask(Job(), Cancel));

        Assert.Equal(AssistantProblem.BadReply, failure.Problem);
    }

    [Fact]
    public async Task ARefusalIsReportedAsDeclined()
    {
        server.Then(_ => new FakeAnthropicServer.Response(200,
            """{"id":"msg_r","type":"message","role":"assistant","model":"claude-opus-5-5","content":[],"stop_reason":"refusal","stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":0}}"""));
        using var service = Service();

        var failure = await Assert.ThrowsAsync<AssistantException>(() => service.Ask(Job(), Cancel));

        Assert.Equal(AssistantProblem.Declined, failure.Problem);
    }

    [Fact]
    public async Task CancelStopsAJobThatIsWaiting()
    {
        var release = new TaskCompletionSource();
        server.Then(_ =>
        {
            release.Task.Wait(TimeSpan.FromSeconds(20));
            return new FakeAnthropicServer.Response(200, "{}");
        });
        using var service = Service();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Cancel);

        var job = service.Ask(Job(), cancel.Token);
        // Cancel once the request has reached the server (waiting for that, not sleeping a fixed time).
        while (server.Requests.Count == 0) await Task.Delay(20, Cancel);
        Assert.True(service.IsBusy);
        cancel.Cancel();
        var failure = await Assert.ThrowsAsync<AssistantException>(() => job);
        release.SetResult();

        Assert.Equal(AssistantProblem.Cancelled, failure.Problem);
        Assert.True(failure.IsQuiet);
    }

    [Fact]
    public async Task AKeyIsTestedWithOneFreeCall()
    {
        using var service = Service();

        await service.TestKey(Key, Cancel);
        var bad = await Assert.ThrowsAsync<AssistantException>(() => service.TestKey("sk-ant-wrong-0000000000000000", Cancel));

        Assert.Equal(AssistantProblem.BadKey, bad.Problem);
        Assert.All(server.Requests, r => Assert.Equal("GET", r.Method));
        Assert.All(server.Requests, r => Assert.Contains("/v1/models/claude-opus-5-5", r.Url, StringComparison.Ordinal));
        Assert.DoesNotContain("sk-ant-", File.ReadAllText(log.FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void AKeySavedIsReadBackByANewVaultAndGoneWhenCleared()
    {
        var path = Path.Combine(folder, "key.bin");
        Assert.Equal(Key, new AssistantKeyVault(path).Load());
        vault.Clear();
        Assert.Null(new AssistantKeyVault(path).Load());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ACostIsNeverShownAsFree()
    {
        Assert.Equal("under 1¢", AssistantCost.Words(0.0004m));
        Assert.Equal("about $0.12", AssistantCost.Words(0.123m));
        // 7,000 letters is about 2,000 tokens in; with 500 out on Opus 5.5 that is 0.8¢ + 1¢.
        Assert.Equal(0.018m, AssistantCost.Estimate(AssistantModels.Find(null), 7000, 500));
        Assert.Equal(AssistantModels.Default, AssistantModels.Find("gpt-something").Id);
    }
}
