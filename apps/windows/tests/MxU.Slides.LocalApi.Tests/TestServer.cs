using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;

namespace MxU.Slides.LocalApi.Tests;

/// <summary>The real server on a free port (port 0), with its own token file, an HTTP client and WebSocket helpers.</summary>
internal sealed class TestServer : IDisposable
{
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(10);

    private TestServer(ApiTokenStore tokens) => Tokens = tokens;

    public LocalApiServer Server { get; } = new();
    public ApiTokenStore Tokens { get; }
    public HttpClient Http { get; } = new();
    public int Port => Server.Port;

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static ApiTokenStore NewTokens() => new(Path.Combine(Path.GetTempPath(), $"ws-tokens-{Guid.NewGuid()}.json"));

    public static TestServer Start(IApiBridge bridge, ApiTokenStore? tokens = null)
    {
        var host = new TestServer(tokens ?? NewTokens());
        Assert.True(host.Server.Start(0, bridge, host.Tokens), host.Server.LastError);
        return host;
    }

    public static TestServer Start(IReadOnlyList<ApiRoute> routes, ApiTokenStore? tokens = null)
    {
        var host = new TestServer(tokens ?? NewTokens());
        Assert.True(host.Server.Start(0, routes, host.Tokens), host.Server.LastError);
        return host;
    }

    public string Mint(ApiScope scope) => Tokens.Create("Test", scope).Secret;

    public Uri Url(string path) => new($"http://127.0.0.1:{Port}{path}");

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? secret = null, string? body = null)
    {
        var request = new HttpRequestMessage(method, Url(path));
        if (secret is not null) request.Headers.Authorization = new("Bearer", secret);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return Http.SendAsync(request, Ct);
    }

    public async Task<ClientWebSocket> ConnectAsync(string secret)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{Port}/v1/ws?token={secret}"), Ct);
        return socket;
    }

    public static Task SendTextAsync(WebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, Ct);

    /// <summary>The next text frame, or null once the server closes the socket (the Mac tests' <c>next() == nil</c>).</summary>
    public static async Task<string?> ReceiveAsync(WebSocket socket)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(ReceiveTimeout);
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, timeout.Token);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage) return Encoding.UTF8.GetString(message.ToArray());
            }
        }
        catch (WebSocketException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
        {
            throw new TimeoutException("No frame and no close from the server.");
        }
    }

    public void Dispose()
    {
        Server.Dispose();
        Http.Dispose();
    }
}

/// <summary>A one-thread context standing in for the WPF dispatcher.</summary>
internal sealed class SingleThreadContext : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = [];

    public SingleThreadContext()
    {
        Thread = new Thread(() =>
        {
            SetSynchronizationContext(this);
            foreach (var (callback, state) in queue.GetConsumingEnumerable()) callback(state);
        }) { IsBackground = true };
        Thread.Start();
    }

    public Thread Thread { get; }

    public override void Post(SendOrPostCallback d, object? state) => queue.Add((d, state));

    public Task<T> RunAsync<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(_ =>
        {
            try { done.SetResult(work()); }
            catch (Exception e) { done.SetException(e); }
        }, null);
        return done.Task;
    }

    public void Dispose() => queue.CompleteAdding();
}

/// <summary>A dispatcher that never gets round to anything: a UI thread stuck in a modal loop.</summary>
internal sealed class StalledContext : SynchronizationContext
{
    public override void Post(SendOrPostCallback d, object? state) { }
}
