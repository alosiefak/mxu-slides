using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace MxU.Slides.Assistant.Testing;

/// <summary>
/// A stand-in for Anthropic's API that runs on this PC, for the unit tests and the app's
/// self-test: real HTTP on 127.0.0.1, answering with replies a test scripted beforehand and
/// remembering every request it was sent. No test ever calls the real API (it costs money and
/// needs the church's key), so what this proves is that the app sends what it should and handles
/// each kind of answer; it can't prove the real model answers well.
/// </summary>
public sealed class FakeAnthropicServer : IDisposable
{
    /// <summary>One request as it arrived.</summary>
    public sealed record Request(string Method, string Url, IReadOnlyDictionary<string, string> Headers, string Body)
    {
        /// <summary>The body as JSON (a POST to /v1/messages).</summary>
        public JsonElement Json => JsonDocument.Parse(Body).RootElement.Clone();
    }

    /// <summary>What to answer: a status and a JSON body.</summary>
    public sealed record Response(int Status, string Body);

    private readonly HttpListener listener = new();
    private readonly Lock gate = new();
    private readonly Queue<Func<Request, Response>> script = new();
    private readonly List<Request> requests = [];
    private int replies;

    public FakeAnthropicServer()
    {
        // Ask Windows for a free port, then listen there.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Url = new Uri($"http://127.0.0.1:{port}/");
        listener.Prefixes.Add(Url.ToString());
        listener.Start();
        _ = Task.Run(Serve);
    }

    public Uri Url { get; }

    public IReadOnlyList<Request> Requests
    {
        get { lock (gate) return [.. requests]; }
    }

    /// <summary>The next request gets this answer (answers are used in the order they're added).</summary>
    public FakeAnthropicServer Then(Func<Request, Response> answer)
    {
        lock (gate) script.Enqueue(answer);
        return this;
    }

    /// <summary>Forgets answers scripted and not yet used (a test that scripted more than were asked for).</summary>
    public void ClearScript()
    {
        lock (gate) script.Clear();
    }

    /// <summary>Answers with the model's final words: JSON in the asked-for shape.</summary>
    public FakeAnthropicServer ThenText(string text, int inputTokens = 900, int outputTokens = 120) =>
        Then(_ => Message("[{\"type\":\"text\",\"text\":" + JsonSerializer.Serialize(text) + "}]", "end_turn", inputTokens, outputTokens));

    /// <summary>Answers by asking the app to run a tool.</summary>
    public FakeAnthropicServer ThenToolUse(string tool, string inputJson) =>
        Then(_ => Message("[{\"type\":\"tool_use\",\"id\":\"toolu_" + Guid.NewGuid().ToString("N") + "\",\"name\":\"" + tool + "\",\"input\":" + inputJson + ",\"caller\":{\"type\":\"direct\"}}]", "tool_use", 400, 30));

    /// <summary>Answers with an error status (429 busy, 500 trouble, 401 bad key).</summary>
    public FakeAnthropicServer ThenError(int status, string type = "api_error") =>
        Then(_ => new Response(status, "{\"type\":\"error\",\"error\":{\"type\":\"" + type + "\",\"message\":\"scripted " + status + "\"}}"));

    private Response Message(string content, string stop, int input, int output) => new(200,
        "{\"id\":\"msg_fake_" + Interlocked.Increment(ref replies) + "\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-opus-5-5\",\"content\":" + content + ",\"stop_reason\":\"" + stop + "\",\"stop_sequence\":null,\"usage\":{\"input_tokens\":" + input + ",\"output_tokens\":" + output + ",\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0}}");

    private async Task Serve()
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            try
            {
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var request = new Request(
                    context.Request.HttpMethod, context.Request.Url!.ToString(),
                    context.Request.Headers.AllKeys.Where(k => k is not null).ToDictionary(k => k!.ToLowerInvariant(), k => context.Request.Headers[k] ?? ""),
                    await reader.ReadToEndAsync());
                Func<Request, Response>? answer;
                lock (gate)
                {
                    requests.Add(request);
                    // Asking about a model (the key test) is answered without using up the script.
                    answer = request.Method == "GET" ? null : script.Count > 0 ? script.Dequeue() : null;
                }
                var response = request.Method == "GET"
                    ? request.Headers.GetValueOrDefault("authorization") is { Length: > 0 } key && !key.Contains("wrong", StringComparison.Ordinal)
                        ? new Response(200, """{"id":"claude-opus-5-5","type":"model","display_name":"Claude Opus 5.5","created_at":"2026-09-01T00:00:00Z"}""")
                        : new Response(401, """{"type":"error","error":{"type":"authentication_error","message":"invalid api key"}}""")
                    : answer?.Invoke(request) ?? new Response(500, """{"type":"error","error":{"type":"api_error","message":"nothing scripted"}}""");
                var bytes = Encoding.UTF8.GetBytes(response.Body);
                context.Response.StatusCode = response.Status;
                context.Response.ContentType = "application/json";
                // No waiting between retries in a test.
                if (response.Status is 429 or >= 500) context.Response.Headers["retry-after-ms"] = "10";
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
            catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException) { }
        }
    }

    public void Dispose()
    {
        try { listener.Stop(); }
        catch (ObjectDisposedException) { }
        listener.Close();
    }
}
