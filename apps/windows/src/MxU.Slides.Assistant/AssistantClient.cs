using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;

namespace MxU.Slides.Assistant;

/// <summary>How hard the model should think. Low for a quick live guess, Medium for a job done before Sunday.</summary>
public enum AssistantEffort { Low, Medium }

/// <summary>
/// Something the model may ask the app to look up while it works ("tool use"): it names the tool
/// and gives the inputs, the app runs <see cref="Run"/> on this PC and sends back the answer.
/// This is how the model works from the church's real data instead of its memory.
/// </summary>
/// <param name="Properties">Each input's JSON Schema, by name.</param>
public sealed record AssistantTool(
    string Name, string Description, IReadOnlyDictionary<string, JsonElement> Properties, IReadOnlyList<string> Required,
    Func<IReadOnlyDictionary<string, JsonElement>, string> Run);

/// <summary>One job for the assistant.</summary>
public sealed record AssistantRequest
{
    /// <summary>The feature asking, for the log: "Make Slides".</summary>
    public required string Feature { get; init; }

    /// <summary>
    /// The part that's the same every time (what the job is, the rules). It's sent marked as
    /// cacheable, so when it's long enough Anthropic keeps it and charges a fraction for it next time.
    /// </summary>
    public required string Instructions { get; init; }

    /// <summary>The part that changes: this note, these lyrics, this sentence.</summary>
    public required string Ask { get; init; }

    /// <summary>
    /// The JSON Schema the answer must fit ("structured outputs"). The API guarantees the answer
    /// fits it, so a schema that lists only real slide types can't produce an invented one.
    /// </summary>
    public required string Schema { get; init; }

    /// <summary>The model's id; null for the default.</summary>
    public string? Model { get; init; }

    public AssistantEffort Effort { get; init; } = AssistantEffort.Medium;

    public IReadOnlyList<AssistantTool> Tools { get; init; } = [];

    /// <summary>The whole job's time limit, look-ups and retries included.</summary>
    public TimeSpan Limit { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>The answer: JSON in the shape asked for, and what it cost.</summary>
public sealed record AssistantReply(JsonElement Json, AssistantUsage Usage, AssistantModels.Info Model)
{
    public string CostLine => Usage.Describe(Model);
}

/// <summary>
/// Sends one job to Claude through Anthropic's C# SDK and returns the answer, writing what was
/// sent and received to the <see cref="AssistantLog"/>.
/// <list type="bullet">
/// <item><b>Adaptive thinking:</b> the model decides how much to think; <see cref="AssistantEffort"/> sets how hard it tries.</item>
/// <item><b>A fixed shape:</b> the answer must fit the request's JSON Schema.</item>
/// <item><b>Look-ups:</b> when the model asks for a tool, the app runs it and asks again, up to six rounds.</item>
/// <item><b>Retries:</b> the SDK tries a request up to three times when Anthropic answers 429 (busy) or 5xx (its own trouble).</item>
/// </list>
/// The key is given to the SDK, which sends it in a header; it is never put in the address, the
/// request's words or the log. This class does no threading of its own: <see cref="AssistantService"/>
/// calls it off the UI thread.
/// </summary>
public sealed class AssistantClient(AssistantLog log, Uri? baseUrl = null)
{
    private const int MaxRounds = 6;

    /// <summary>Where requests go: Anthropic, unless a test points it at a stand-in server on this PC.</summary>
    public Uri? BaseUrl { get; } = baseUrl;

    private AnthropicClient Make(string key, TimeSpan timeout)
    {
        // The key is always given here, so the SDK never goes looking for one in the environment.
        return BaseUrl is null
            ? new AnthropicClient { ApiKey = key, MaxRetries = 2, Timeout = timeout }
            : new AnthropicClient { ApiKey = key, MaxRetries = 2, Timeout = timeout, BaseUrl = BaseUrl.ToString() };
    }

    /// <summary>
    /// Checks a key with one small call that costs nothing: it asks Anthropic to describe the
    /// chosen model, which only works with a valid key that may use that model.
    /// </summary>
    public async Task TestKey(string key, string? model, CancellationToken cancel)
    {
        var info = AssistantModels.Find(model);
        string? problem = null;
        try
        {
            await Guarded(TimeSpan.FromSeconds(20), cancel, token => Make(key, TimeSpan.FromSeconds(15)).Models.Retrieve(info.Id, cancellationToken: token));
        }
        catch (AssistantException e)
        {
            problem = e.Message;
            throw;
        }
        finally
        {
            log.Add(new AssistantLogEntry(DateTimeOffset.Now, "Key test", info.Id, $"Asked Anthropic to describe the model {info.Id} (no words sent).", [], problem is null ? "The key works." : "", AssistantUsage.None, 0m, problem));
        }
    }

    public async Task<AssistantReply> Ask(string key, AssistantRequest request, CancellationToken cancel)
    {
        var model = AssistantModels.Find(request.Model);
        var usage = AssistantUsage.None;
        var lookups = new List<string>();
        var received = "";
        string? problem = null;
        try
        {
            var client = Make(key, request.Limit);
            var schema = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(request.Schema)!;
            List<ToolUnion> tools = [.. request.Tools.Select(tool => (ToolUnion)new Tool
            {
                Name = tool.Name,
                Description = tool.Description,
                InputSchema = new() { Properties = tool.Properties.ToDictionary(p => p.Key, p => p.Value), Required = [.. tool.Required] },
            })];
            List<MessageParam> messages = [new UserMessageParam { Content = request.Ask }];

            return await Guarded(request.Limit, cancel, async token =>
            {
                for (var round = 0; round < MaxRounds; round++)
                {
                    var response = await client.Messages.Create(new MessageCreateParams
                    {
                        Model = model.Id,
                        MaxTokens = 16000,
                        Thinking = new ThinkingConfigAdaptive(),
                        OutputConfig = new OutputConfig
                        {
                            Effort = request.Effort == AssistantEffort.Low ? Effort.Low : Effort.Medium,
                            Format = new JsonOutputFormat { Schema = schema },
                        },
                        // The stable part, marked cacheable. (Anthropic only keeps a prefix above a
                        // minimum length, so short instructions are simply sent in full each time.)
                        System = new List<TextBlockParam> { new() { Text = request.Instructions, CacheControl = new CacheControlEphemeral() } },
                        Tools = tools,
                        Messages = messages,
                    }, cancellationToken: token);
                    usage = usage.Plus(new AssistantUsage(
                        response.Usage.InputTokens, response.Usage.OutputTokens,
                        response.Usage.CacheReadInputTokens ?? 0, response.Usage.CacheCreationInputTokens ?? 0));

                    if (response.StopReason == StopReason.Refusal) throw new AssistantException(AssistantProblem.Declined, AssistantException.Words(AssistantProblem.Declined));

                    // Echo the model's turn back (its thinking included, untouched: the API checks it),
                    // run each look-up it asked for, and send one result per look-up.
                    List<ContentBlockParam> echoed = [];
                    List<ContentBlockParam> results = [];
                    var text = new StringBuilder();
                    foreach (var block in response.Content)
                    {
                        if (block.TryPickText(out var words))
                        {
                            text.Append(words.Text);
                            echoed.Add(new TextBlockParam { Text = words.Text });
                        }
                        else if (block.TryPickThinking(out var thinking))
                        {
                            echoed.Add(new ThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
                        }
                        else if (block.TryPickRedactedThinking(out var hidden))
                        {
                            echoed.Add(new RedactedThinkingBlockParam { Data = hidden.Data });
                        }
                        else if (block.TryPickToolUse(out var use))
                        {
                            echoed.Add(new ToolUseBlockParam { ID = use.ID, Name = use.Name, Input = use.Input });
                            var (answer, failed) = RunTool(request.Tools, use.Name, use.Input);
                            lookups.Add($"{use.Name} {JsonSerializer.Serialize(use.Input)} → {answer}");
                            results.Add(new ToolResultBlockParam { ToolUseID = use.ID, Content = answer, IsError = failed });
                        }
                    }
                    if (results.Count == 0)
                    {
                        received = text.ToString();
                        if (response.StopReason == StopReason.MaxTokens) throw new AssistantException(AssistantProblem.BadReply, "The assistant's answer was cut off, so nothing was changed.");
                        try
                        {
                            using var document = JsonDocument.Parse(received);
                            return new AssistantReply(document.RootElement.Clone(), usage, model);
                        }
                        catch (JsonException e)
                        {
                            throw new AssistantException(AssistantProblem.BadReply, AssistantException.Words(AssistantProblem.BadReply), e);
                        }
                    }
                    messages.Add(new AssistantMessageParam { Content = echoed });
                    messages.Add(new UserMessageParam { Content = results });
                }
                throw new AssistantException(AssistantProblem.BadReply, "The assistant kept looking things up without answering, so it was stopped.");
            });
        }
        catch (AssistantException e)
        {
            problem = e.Message;
            throw;
        }
        finally
        {
            log.Add(new AssistantLogEntry(
                DateTimeOffset.Now, request.Feature, model.Id,
                $"INSTRUCTIONS\n{request.Instructions}\n\nREQUEST\n{request.Ask}", lookups, received, usage, usage.Cost(model), problem));
        }
    }

    private static (string Answer, bool Failed) RunTool(IReadOnlyList<AssistantTool> tools, string name, IReadOnlyDictionary<string, JsonElement> input)
    {
        if (tools.FirstOrDefault(t => t.Name == name) is not { } tool) return ($"There is no tool named {name}.", true);
        try
        {
            return (tool.Run(input), false);
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
        {
            // Inputs that don't fit: tell the model, which can ask again properly.
            return ($"That didn't work: {e.Message}", true);
        }
    }

    /// <summary>
    /// Runs a call under the job's time limit and turns every way it can fail into an
    /// <see cref="AssistantException"/> with words for the operator. Most specific first: a
    /// refused key, busy, Anthropic's own trouble, then no network.
    /// </summary>
    private static async Task<T> Guarded<T>(TimeSpan limit, CancellationToken cancel, Func<CancellationToken, Task<T>> call)
    {
        using var timed = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timed.CancelAfter(limit);
        try
        {
            return await call(timed.Token);
        }
        catch (AssistantException)
        {
            throw;
        }
        catch (Exception e) when (e is AnthropicUnauthorizedException or AnthropicForbiddenException)
        {
            throw Failed(AssistantProblem.BadKey, e);
        }
        catch (AnthropicRateLimitException e)
        {
            throw Failed(AssistantProblem.Busy, e);
        }
        catch (Anthropic5xxException e)
        {
            throw Failed(AssistantProblem.ServerTrouble, e);
        }
        catch (OperationCanceledException e)
        {
            throw Failed(cancel.IsCancellationRequested ? AssistantProblem.Cancelled : AssistantProblem.TimedOut, e);
        }
        catch (Exception e) when (e is AnthropicIOException or HttpRequestException or IOException)
        {
            throw Failed(AssistantProblem.Offline, e);
        }
        catch (AnthropicApiException e)
        {
            // A 400 or 404: something this app sent was wrong (or the model isn't available to this key).
            throw new AssistantException(AssistantProblem.Other, $"Anthropic turned the request down ({(int)e.StatusCode}). Nothing was changed.", e);
        }
        catch (AnthropicException e)
        {
            throw Failed(AssistantProblem.BadReply, e);
        }
    }

    private static AssistantException Failed(AssistantProblem problem, Exception inner) => new(problem, AssistantException.Words(problem), inner);
}
