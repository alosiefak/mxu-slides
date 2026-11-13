using System.Globalization;

namespace MxU.Slides.Assistant;

/// <summary>
/// The Claude models the assistant can use, with their list prices, so the app can say what a job
/// cost. Prices are US dollars per million tokens (a token is roughly three-quarters of a word).
/// </summary>
public static class AssistantModels
{
    /// <param name="CacheRead">The price of input the API had already seen and kept ("prompt caching").</param>
    public sealed record Info(string Id, string Name, decimal Input, decimal Output, decimal CacheRead);

    /// <summary>The careful one: the default.</summary>
    public const string Default = "claude-opus-5-5";

    /// <summary>The fast, cheap one, for live suggestions. Used only when the church chooses it.</summary>
    public const string Quick = "claude-haiku-5-5";

    public static readonly IReadOnlyList<Info> All =
    [
        new(Default, "Claude Opus 5.5 (most careful)", 4m, 20m, 0.20m),
        // Anthropic's price list gives no cache-read price for Haiku 5.5 here, so cached input is
        // counted at the full input price: the estimate can only be too high, never too low.
        new(Quick, "Claude Haiku 5.5 (fastest, cheapest)", 0.10m, 0.50m, 0.10m),
    ];

    /// <summary>The model with this id; the default for anything else (an old or mistyped setting).</summary>
    public static Info Find(string? id) => All.FirstOrDefault(m => m.Id == id) ?? All[0];
}

/// <summary>The tokens one job used, as the API reported them.</summary>
/// <param name="CacheWrite">Input the API kept for next time (charged a quarter more than plain input).</param>
public sealed record AssistantUsage(long Input, long Output, long CacheRead = 0, long CacheWrite = 0)
{
    public static readonly AssistantUsage None = new(0, 0);

    public long Total => Input + Output + CacheRead + CacheWrite;

    public AssistantUsage Plus(AssistantUsage other) =>
        new(Input + other.Input, Output + other.Output, CacheRead + other.CacheRead, CacheWrite + other.CacheWrite);

    /// <summary>What this cost in dollars at the model's list price.</summary>
    public decimal Cost(AssistantModels.Info model) =>
        (Input * model.Input + Output * model.Output + CacheRead * model.CacheRead + CacheWrite * model.Input * 1.25m) / 1_000_000m;

    /// <summary>"1,240 tokens in, 310 out, about $0.01".</summary>
    public string Describe(AssistantModels.Info model) =>
        string.Create(CultureInfo.InvariantCulture, $"{Input + CacheRead + CacheWrite:N0} tokens in, {Output:N0} out, {AssistantCost.Words(Cost(model))}");
}

/// <summary>Costs in words, and a guess at a job's cost before it's sent.</summary>
public static class AssistantCost
{
    /// <summary>"about $0.03"; anything under a cent is "under 1¢", so a tiny job never reads as free.</summary>
    public static string Words(decimal dollars) => dollars <= 0m ? "no cost"
        : dollars < 0.01m ? "under 1¢"
        : string.Create(CultureInfo.InvariantCulture, $"about ${dollars:0.00}");

    /// <summary>
    /// A guess before sending: English runs at about one token for every three and a half
    /// letters, and the reply is guessed at <paramref name="replyTokens"/>. The real figure, from
    /// the API, is shown after the job.
    /// </summary>
    public static decimal Estimate(AssistantModels.Info model, int lettersSent, int replyTokens) =>
        new AssistantUsage((long)Math.Ceiling(lettersSent / 3.5), replyTokens).Cost(model);
}

/// <summary>Why the assistant couldn't answer. Each has calm words for the operator (<see cref="AssistantException"/>).</summary>
public enum AssistantProblem
{
    /// <summary>The PC's switch is off.</summary>
    Off,
    NoKey,
    /// <summary>No internet, or Anthropic can't be reached.</summary>
    Offline,
    /// <summary>The key was refused (401 or 403).</summary>
    BadKey,
    /// <summary>Too many requests (429), still so after the retries.</summary>
    Busy,
    /// <summary>Anthropic's servers failed (5xx), still so after the retries.</summary>
    ServerTrouble,
    TimedOut,
    Cancelled,
    /// <summary>The model declined to answer.</summary>
    Declined,
    /// <summary>The answer wasn't in the shape asked for, or failed a check (changed lyrics).</summary>
    BadReply,
    /// <summary>Anything else: a 400 (a bug here), an unknown model.</summary>
    Other,
}

/// <summary>A failed job. <see cref="Exception.Message"/> is written for the operator and never holds the key.</summary>
public sealed class AssistantException(AssistantProblem problem, string message, Exception? inner = null) : Exception(message, inner)
{
    public AssistantProblem Problem { get; } = problem;

    /// <summary>True when the assistant simply isn't in use (off, or no key): nothing to tell anyone.</summary>
    public bool IsQuiet => Problem is AssistantProblem.Off or AssistantProblem.NoKey or AssistantProblem.Cancelled;

    public static string Words(AssistantProblem problem) => problem switch
    {
        AssistantProblem.Off => "The assistant is switched off (Settings › Assistant).",
        AssistantProblem.NoKey => "The assistant needs the church's Anthropic API key (Settings › Assistant).",
        AssistantProblem.Offline => "The assistant can't reach the internet. Everything else works as usual.",
        AssistantProblem.BadKey => "Anthropic didn't accept the API key. Check it in Settings › Assistant.",
        AssistantProblem.Busy => "Anthropic is busy just now (too many requests). It was tried three times; try again in a minute.",
        AssistantProblem.ServerTrouble => "Anthropic's servers had a problem. It was tried three times; try again in a minute.",
        AssistantProblem.TimedOut => "The assistant took too long and was stopped.",
        AssistantProblem.Cancelled => "Cancelled.",
        AssistantProblem.Declined => "The assistant declined to answer this one.",
        AssistantProblem.BadReply => "The assistant's answer couldn't be used, so nothing was changed.",
        _ => "The assistant couldn't do that.",
    };
}
