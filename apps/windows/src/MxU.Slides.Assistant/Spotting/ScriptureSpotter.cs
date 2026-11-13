using System.Text.Json;
using MxU.Slides.Core.Scripture;

namespace MxU.Slides.Assistant.Spotting;

/// <summary>One thing the recognizer heard.</summary>
/// <param name="Text">The words (or, from the reference grammar, a reference such as "Romans 8:28").</param>
/// <param name="At">How far into the listening it was said.</param>
/// <param name="Confidence">The recognizer's own 0 to 1 score.</param>
/// <param name="FromGrammar">True when the reference grammar heard it (book, chapter, verse); false for free dictation.</param>
public sealed record Utterance(string Text, TimeSpan At, double Confidence, bool FromGrammar);

/// <summary>How a passage was spotted.</summary>
public enum SpotSource
{
    /// <summary>The reference was spoken outright and read by the parser.</summary>
    Spoken,
    /// <summary>The assistant guessed it from an allusion ("the prodigal son"). Shown as a guess.</summary>
    Assistant,
}

/// <summary>A passage the spotter thinks the preacher just named. It waits for the operator; it never shows itself.</summary>
public sealed record Spot(PassageReference Reference, string Heard, TimeSpan At, SpotSource Source, DateTimeOffset When)
{
    public string Display => Reference.Display;
}

/// <summary>
/// The scripture spotter's brain: it is told what was heard and decides what, if anything, to
/// offer the operator. It never touches the outputs.
/// <list type="number">
/// <item>A reference spoken outright is found on this PC by <see cref="SpokenScripture"/>.</item>
/// <item>Only a sentence with no reference that still sounds like an allusion goes to the
/// assistant, alone, with nothing else of the sermon: at most one question every few seconds,
/// never the same sentence twice, never a sentence naming someone to pray for.</item>
/// </list>
/// Two recognizers listen to the same sound (see <c>SpeechWorker</c>), and when both read the
/// same moment the better half of each is kept (see <c>Offer</c>). Thread-safe; events are
/// raised on the caller's thread or, for an assistant's answer, on whichever thread finished it.
/// </summary>
public sealed class ScriptureSpotter(Func<Bible?> bible, Func<AssistantRequest, CancellationToken, Task<AssistantReply>>? ask = null)
{
    /// <summary>The reference grammar is believed at or above this score (it hears wrong books below it).</summary>
    public const double GrammarConfidence = 0.75;

    /// <summary>The two recognizers hearing the same words report times within this of each other.</summary>
    public static readonly TimeSpan SameMoment = TimeSpan.FromSeconds(6);

    /// <summary>The same passage isn't offered again within this (a preacher repeats a reference).</summary>
    public static readonly TimeSpan RepeatAfter = TimeSpan.FromSeconds(60);

    /// <summary>The least time between two questions to the assistant.</summary>
    public static readonly TimeSpan AskEvery = TimeSpan.FromSeconds(8);

    private readonly Lock gate = new();
    private readonly List<Spot> spots = [];
    private readonly List<(Spot Spot, bool FromGrammar)> origins = [];
    private readonly HashSet<string> asked = new(StringComparer.OrdinalIgnoreCase);
    private TimeSpan? lastAsked;

    /// <summary>A new passage is waiting (the newest is last in <see cref="Spots"/>).</summary>
    public event Action<Spot>? Spotted;

    /// <summary>The passages offered so far, oldest first.</summary>
    public IReadOnlyList<Spot> Spots
    {
        get { lock (gate) return [.. spots]; }
    }

    /// <summary>How many sentences were sent to the assistant (shown, so nothing is hidden).</summary>
    public int Asked { get; private set; }

    public static string Schema => JsonSerializer.Serialize(new
    {
        type = "object",
        properties = new { found = new { type = "boolean" }, reference = new { type = new[] { "string", "null" } } },
        required = new[] { "found", "reference" },
        additionalProperties = false,
    });

    private const string Instructions =
        """
        You are given ONE sentence from a sermon, as a speech recognizer heard it (so it may hold mishearings). If it clearly points at one specific Bible passage (a named story, parable, event or well-known saying), answer found = true with the passage's reference in the usual form ("Luke 15:11-32", "John 6:1-14", "Psalm 23"). If it doesn't, or you aren't sure which passage, answer found = false and reference = null. Do not guess. Never write verse text: the church's own Bible supplies it.
        """;

    /// <summary>Takes in one utterance. Returns straight away; an assistant's answer arrives later through <see cref="Spotted"/>.</summary>
    public void Hear(Utterance heard, CancellationToken cancel = default)
    {
        if (bible() is not { } book) return;
        if (heard.FromGrammar)
        {
            if (heard.Confidence < GrammarConfidence || PassageReference.Parse(heard.Text) is not { } parsed) return;
            var passage = book.Read(parsed);
            if (!passage.IsEmpty) Offer(new Spot(passage.Reference, heard.Text, heard.At, SpotSource.Spoken, DateTimeOffset.Now), fromGrammar: true);
            return;
        }
        var found = SpokenScripture.Find(heard.Text, book);
        foreach (var one in found) Offer(new Spot(one.Reference, one.Heard, heard.At, SpotSource.Spoken, DateTimeOffset.Now), fromGrammar: false);
        if (found.Count > 0 || ask is null || !SpokenScripture.SoundsLikeAnAllusion(heard.Text)) return;
        // Someone's name in a prayer request stays here.
        if (PrayerPrivacy.Strip([heard.Text]).Withheld.Count > 0) return;
        lock (gate)
        {
            if (lastAsked is { } last && heard.At - last < AskEvery && heard.At >= last) return;
            if (!asked.Add(heard.Text.Trim())) return;
            lastAsked = heard.At;
            Asked++;
        }
        _ = AskAssistant(heard, book, cancel);
    }

    private async Task AskAssistant(Utterance heard, Bible book, CancellationToken cancel)
    {
        try
        {
            var reply = await ask!(new AssistantRequest
            {
                Feature = "Scripture spotter", Instructions = Instructions, Ask = heard.Text, Schema = Schema,
                Effort = AssistantEffort.Low, Limit = TimeSpan.FromSeconds(15),
            }, cancel).ConfigureAwait(false);
            if (!reply.Json.TryGetProperty("found", out var yes) || yes.ValueKind != JsonValueKind.True) return;
            if (!reply.Json.TryGetProperty("reference", out var named) || named.ValueKind != JsonValueKind.String) return;
            if (PassageReference.Parse(named.GetString()!) is not { } parsed) return;
            var passage = book.Read(parsed);
            if (!passage.IsEmpty) Offer(new Spot(passage.Reference, heard.Text, heard.At, SpotSource.Assistant, DateTimeOffset.Now), fromGrammar: false);
        }
        catch (AssistantException)
        {
            // Off, no key, no internet, busy: the spotter carries on with what it hears itself.
        }
    }

    private void Offer(Spot spot, bool fromGrammar)
    {
        lock (gate)
        {
            // Said again within a minute: once is enough.
            if (spots.Any(s => s.Display == spot.Display && (spot.At - s.At).Duration() < RepeatAfter)) return;
            if (spot.Source == SpotSource.Spoken)
            {
                // The other recognizer's reading of the same moment. Measured on this PC: the grammar
                // gets chapter and verse right but sometimes the wrong book; dictation gets the book
                // right but muddles the numbers. So where they agree on the book the grammar's
                // numbers stand, and where they name different books dictation's book stands.
                var others = origins.Where(o => o.FromGrammar != fromGrammar && o.Spot.Source == SpotSource.Spoken && (o.Spot.At - spot.At).Duration() < SameMoment).ToList();
                foreach (var other in others)
                {
                    var sameBook = other.Spot.Reference.Spans[0].Book == spot.Reference.Spans[0].Book;
                    var newStands = fromGrammar ? sameBook : !sameBook;
                    if (!newStands) return;
                    spots.Remove(other.Spot);
                    origins.Remove(other);
                }
            }
            spots.Add(spot);
            origins.Add((spot, fromGrammar));
        }
        Spotted?.Invoke(spot);
    }
}
