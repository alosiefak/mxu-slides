using System.Text.Json;
using System.Text.Json.Nodes;

namespace MxU.Slides.Assistant;

/// <summary>
/// One thing the app can do for a typed request, taken from a row of the Local API's route table
/// (HOW-IT-WORKS section 15): "fireOverlay", with its path's blanks ("id") and its body's shape.
/// The app builds these from the same table the browser remote and Stream Deck use, so the
/// assistant can do exactly what a remote control can, and nothing else.
/// </summary>
/// <param name="Name">The route's operation id, which is the tool's name: "fireOverlay".</param>
/// <param name="PathParameters">The blanks in the route's path: "id".</param>
/// <param name="Body">The body's JSON Schema (an object with properties), or null when the route takes none.</param>
/// <param name="Run">Does it: the route's own handler, given the path's values and the body. Call on the UI thread.</param>
public sealed record ActionRoute(
    string Name, string Summary, IReadOnlyList<string> PathParameters, JsonObject? Body,
    Func<IReadOnlyDictionary<string, string>, byte[], Task> Run);

/// <summary>
/// An action the assistant has made ready and NOT run. The operator reads <see cref="Summary"/>
/// and presses Enter (<see cref="Run"/>) or Esc.
/// </summary>
public sealed record ReadyAction(ActionRoute Route, IReadOnlyDictionary<string, string> Path, byte[] Body, string Summary)
{
    /// <summary>What will be called, for the small print under the summary: "fireOverlay id=ov-12".</summary>
    public string Detail
    {
        get
        {
            var parts = Path.Select(p => $"{p.Key}={p.Value}").ToList();
            if (Body.Length > 2) parts.Add(System.Text.Encoding.UTF8.GetString(Body));
            return parts.Count == 0 ? Route.Name : $"{Route.Name} {string.Join(' ', parts)}";
        }
    }

    public Task Run() => Route.Run(Path, Body);
}

/// <summary>
/// Typed requests: the operator types "put up the giving lower third" or "next song", and the
/// assistant turns it into one of the app's own actions.
/// <para>
/// <b>Nothing runs when the assistant asks for it.</b> Each action is offered to the model as a
/// tool. When the model calls one, the tool does not do the thing: it checks the call, keeps it
/// (<see cref="Held.Action"/>) and tells the model it is waiting for the operator. The window
/// then shows it as a ready action. Only the operator's Enter calls the route.
/// </para>
/// <para>
/// <b>Nothing invented.</b> An id the model gives must be one a look-up returned from this
/// church's library (<see cref="LibraryFacts.Has"/>); anything else is refused and the model is
/// told to search first.
/// </para>
/// </summary>
public static class TypedRequests
{
    /// <summary>Where a job's held action lands (the tools run on the assistant's thread; the window reads this after the job).</summary>
    public sealed class Held
    {
        private ReadyAction? action;

        public ReadyAction? Action => Volatile.Read(ref action);

        internal bool Hold(ReadyAction ready) => Interlocked.CompareExchange(ref action, ready, null) is null;
    }

    /// <summary>What a typed request came to: an action waiting for Enter, or just words (why it can't).</summary>
    public sealed record Outcome(ReadyAction? Action, string Words);

    public static string Schema => JsonSerializer.Serialize(new
    {
        type = "object",
        properties = new
        {
            summary = new { type = "string" },
        },
        required = new[] { "summary" },
        additionalProperties = false,
    });

    private const string Instructions =
        """
        You help the person running a church's slides during a service. They type a short request; you turn it into ONE of the app's actions by calling that action's tool once. The action is not carried out when you call it: it is shown to the operator, who presses Enter to do it or Esc to cancel.

        How to work:
        - To act on something in the library (a lower third, a song, a video, an alert), find it first with search_library and use the id it returns. Lower thirds and other things shown over the slides are "overlays". Never make up an id.
        - For requests about the service ("next song", "go to the sermon"), read it with read_current_service. To move to a row, call fireSlide with that row's serviceItemId. For plain "next" or "back", call advance (steps 1 or -1).
        - Call exactly one action tool. If the request needs something that doesn't exist, is unclear, or isn't something these tools can do, call no action.

        Then answer with summary: one short plain sentence for the operator saying what will happen when they press Enter ("Show the lower third “Giving”."), or, if you called no action, why not.
        """;

    /// <summary>The job for one typed request, and where its action will be held.</summary>
    public static (AssistantRequest Request, Held Held) Prepare(string typed, IReadOnlyList<ActionRoute> routes, LibraryFacts facts)
    {
        var held = new Held();
        List<AssistantTool> tools = [.. facts.Tools.Take(2), .. routes.Select(route => Tool(route, facts, held))];
        return (new AssistantRequest
        {
            Feature = "Typed request", Instructions = Instructions, Schema = Schema, Tools = tools, Effort = AssistantEffort.Low,
            Ask = typed.Trim(), Limit = TimeSpan.FromSeconds(45),
        }, held);
    }

    /// <summary>Reads the reply: the held action (if the model made one) with the model's sentence, clipped.</summary>
    public static Outcome Read(JsonElement reply, Held held)
    {
        var words = reply.ValueKind == JsonValueKind.Object && reply.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.String
            ? summary.GetString()!.Trim() : "";
        if (words.Length > 200) words = words[..200];
        if (held.Action is not { } action) return new Outcome(null, words.Length > 0 ? words : "The assistant didn't find anything to do for that.");
        // The sentence is the model's; the action's own description is always shown beside it.
        return new Outcome(action with { Summary = words.Length > 0 ? words : action.Summary }, words);
    }

    private static AssistantTool Tool(ActionRoute route, LibraryFacts facts, Held held)
    {
        var properties = new Dictionary<string, JsonElement>();
        foreach (var name in route.PathParameters)
            properties[name] = JsonSerializer.SerializeToElement(new { type = "string", description = "An id from search_library or read_current_service." });
        if (route.Body?["properties"] is JsonObject body)
        {
            foreach (var (name, schema) in body)
                if (schema is not null && !properties.ContainsKey(name)) properties[name] = JsonSerializer.SerializeToElement(schema);
        }
        return new AssistantTool(
            route.Name, route.Summary + " (Held for the operator's Enter; not carried out by this call.)", properties, [.. route.PathParameters],
            input =>
            {
                var path = new Dictionary<string, string>();
                var fields = new JsonObject();
                foreach (var (name, value) in input)
                {
                    if (!properties.ContainsKey(name)) throw new ArgumentException($"{route.Name} has no input named {name}.");
                    if (route.PathParameters.Contains(name)) path[name] = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
                    else if (value.ValueKind != JsonValueKind.Null) fields[name] = JsonNode.Parse(value.GetRawText());
                }
                foreach (var name in route.PathParameters)
                    if (!path.TryGetValue(name, out var given) || given.Length == 0) throw new ArgumentException($"{route.Name} needs {name}.");
                // Every id must be one a look-up could have returned: nothing invented reaches the app.
                var ids = path.Values.Concat(fields.Where(f => f.Key.EndsWith("Id", StringComparison.Ordinal) && f.Value is JsonValue).Select(f => f.Value!.ToString()));
                foreach (var id in ids)
                    if (!facts.Has(id)) throw new KeyNotFoundException($"“{id}” isn't an id in this library. Find the real one with search_library or read_current_service.");
                var named = path.Values.Select(facts.Find).OfType<LibraryFacts.Entry>().FirstOrDefault();
                var ready = new ReadyAction(route, path, JsonSerializer.SerializeToUtf8Bytes(fields), named is null ? route.Summary : $"{route.Summary} “{named.Name}”");
                return held.Hold(ready)
                    ? "Held ready. Nothing has happened yet: the operator will press Enter to do it. Now give your summary."
                    : "Another action is already held for this request; only one can be. Give your summary of the first.";
            });
    }
}
