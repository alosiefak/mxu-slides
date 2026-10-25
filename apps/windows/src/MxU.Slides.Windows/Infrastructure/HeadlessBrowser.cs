using System.Diagnostics;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>
/// The PC's own Edge (or Chrome), run without a window and driven through its DevTools protocol,
/// so the self-test can open the browser remote's pages as a phone would, tap them, and save
/// pictures of them (SelfTest.Connect.cs).
/// <list type="bullet">
/// <item>Nothing is installed or downloaded: it's the browser Windows already has.</item>
/// <item>It runs with a throwaway profile in the temp folder, so the user's own browser (its
/// history, logins and settings) is never touched, and the profile is deleted afterwards.</item>
/// <item>The DevTools protocol is JSON over a WebSocket: send <c>{"id":1,"method":"Page.navigate",
/// "params":{…}}</c>, get <c>{"id":1,"result":{…}}</c> back. Only the few commands below are used.</item>
/// </list>
/// Self-test only. Windows only.
/// </summary>
internal sealed class HeadlessBrowser : IAsyncDisposable
{
    /// <summary>How long one DevTools command may take, so a stuck browser can't hang the self-test.</summary>
    private static readonly TimeSpan CommandWait = TimeSpan.FromSeconds(15);

    private readonly Process process;
    private readonly string profile;
    private readonly ClientWebSocket socket;
    private int lastId;

    private HeadlessBrowser(Process process, string profile, ClientWebSocket socket)
    {
        this.process = process;
        this.profile = profile;
        this.socket = socket;
    }

    /// <summary>Where Edge or Chrome is installed, or null when neither is.</summary>
    public static string? Find()
    {
        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ];
        string[] browsers = [@"Microsoft\Edge\Application\msedge.exe", @"Google\Chrome\Application\chrome.exe"];
        return browsers.SelectMany(b => roots.Where(r => r.Length > 0).Select(r => Path.Combine(r, b))).FirstOrDefault(File.Exists);
    }

    /// <summary>Starts the browser with an empty page and connects to it.</summary>
    public static async Task<HeadlessBrowser> StartAsync(string executable, CancellationToken cancellation)
    {
        var profile = Path.Combine(Path.GetTempPath(), $"MxU self-test browser {Guid.NewGuid():N}");
        Directory.CreateDirectory(profile);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        // Port 0 lets the browser pick a free port (the machine may be running other browsers and tests).
        // The rest keep it quiet: no first-run pages, no extensions, no sync or update checks.
        foreach (var argument in new[]
        {
            "--headless=new", "--remote-debugging-port=0", $"--user-data-dir={profile}",
            "--no-first-run", "--no-default-browser-check", "--disable-extensions", "--disable-sync",
            "--disable-background-networking", "--disable-component-update", "--disable-default-apps",
            "--disable-breakpad", "--disable-crash-reporter", "--mute-audio", "--hide-scrollbars", "about:blank",
        })
            start.ArgumentList.Add(argument);
        var process = Process.Start(start) ?? throw new InvalidOperationException("The browser didn't start.");
        try
        {
            // Once it's listening, the browser writes the port it chose into the profile folder.
            var portFile = Path.Combine(profile, "DevToolsActivePort");
            var port = 0;
            for (var waited = 0; port == 0; waited += 100)
            {
                if (process.HasExited || waited > 20_000) throw new TimeoutException("The browser didn't open its DevTools port.");
                await Task.Delay(100, cancellation);
                try
                {
                    if (File.Exists(portFile) && int.TryParse(File.ReadLines(portFile).FirstOrDefault(), out var chosen)) port = chosen;
                }
                catch (IOException)
                {
                    // Still being written: try again.
                }
            }
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var targets = JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/list", cancellation))?.AsArray() ?? [];
            var page = (string?)targets.FirstOrDefault(t => (string?)t?["type"] == "page")?["webSocketDebuggerUrl"]
                ?? throw new InvalidOperationException("The browser has no page open.");
            var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri(page), cancellation);
            return new HeadlessBrowser(process, profile, socket);
        }
        catch
        {
            await StopAsync(process, profile);
            throw;
        }
    }

    // MARK: What the self-test does with it

    /// <summary>The page's size in CSS pixels, how many screen pixels each one is, and whether it's a phone (touch, mobile viewport).</summary>
    public Task SizeAsync(int width, int height, double scale, bool mobile) =>
        SendAsync("Emulation.setDeviceMetricsOverride",
            new JsonObject { ["width"] = width, ["height"] = height, ["deviceScaleFactor"] = scale, ["mobile"] = mobile });

    /// <summary>Opens an address and waits for the page (and its scripts) to load.</summary>
    public async Task OpenAsync(string url)
    {
        await SendAsync("Page.navigate", new JsonObject { ["url"] = url });
        await WaitForAsync("document.readyState === 'complete'");
    }

    /// <summary>Runs JavaScript in the page and returns its value (a promise is awaited).</summary>
    public async Task<JsonNode?> EvaluateAsync(string expression)
    {
        var result = await SendAsync("Runtime.evaluate",
            new JsonObject { ["expression"] = expression, ["returnByValue"] = true, ["awaitPromise"] = true });
        if (result?["exceptionDetails"] is { } failed)
            throw new InvalidOperationException($"The page's script failed: {(string?)failed["exception"]?["description"] ?? (string?)failed["text"]}");
        return result?["result"]?["value"];
    }

    /// <summary>Waits until a JavaScript condition holds in the page, checking every 100 ms.</summary>
    public async Task<bool> WaitForAsync(string condition, int milliseconds = 10_000)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                if (await EvaluateAsync($"!!({condition})") is JsonValue value && value.TryGetValue<bool>(out var holds) && holds) return true;
            }
            catch (InvalidOperationException)
            {
                // The page is between documents (it's loading): ask again.
            }
            if (clock.ElapsedMilliseconds > milliseconds) return false;
            await Task.Delay(100);
        }
    }

    /// <summary>A picture of what the page shows, as PNG bytes, at the size and scale <see cref="SizeAsync"/> set.</summary>
    public async Task<byte[]> PictureAsync()
    {
        var result = await SendAsync("Page.captureScreenshot", new JsonObject { ["format"] = "png" });
        return Convert.FromBase64String((string?)result?["data"] ?? "");
    }

    // MARK: The protocol

    /// <summary>Sends one command and waits for its answer. Events (none are asked for) would be skipped.</summary>
    private async Task<JsonNode?> SendAsync(string method, JsonObject? parameters = null)
    {
        using var limit = new CancellationTokenSource(CommandWait);
        var id = ++lastId;
        var command = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters ?? [] };
        await socket.SendAsync(Encoding.UTF8.GetBytes(command.ToJsonString()), WebSocketMessageType.Text, true, limit.Token);
        // A screenshot comes back as one large message, in many pieces.
        var chunk = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            message.SetLength(0);
            WebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(chunk, limit.Token);
                if (received.MessageType == WebSocketMessageType.Close) throw new InvalidOperationException("The browser closed its DevTools connection.");
                message.Write(chunk, 0, received.Count);
            }
            while (!received.EndOfMessage);
            var answer = JsonNode.Parse(message.GetBuffer().AsSpan(0, (int)message.Length));
            if (answer?["id"] is not JsonValue answered || !answered.TryGetValue<int>(out var answeredId) || answeredId != id) continue;
            if (answer["error"] is { } error) throw new InvalidOperationException($"{method}: {(string?)error["message"]}");
            return answer["result"];
        }
    }

    public async ValueTask DisposeAsync()
    {
        socket.Dispose();
        await StopAsync(process, profile);
    }

    /// <summary>Ends the browser (all its processes) and deletes its throwaway profile.</summary>
    private static async Task StopAsync(Process process, string profile)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(wait.Token);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            // Already gone, or still closing: the folder below is tried anyway.
        }
        process.Dispose();
        // Its processes can hold files (and the folder itself) for a few seconds after they end.
        for (var attempt = 0; attempt < 20 && Directory.Exists(profile); attempt++)
        {
            try
            {
                Directory.Delete(profile, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(500);
            }
        }
        if (Directory.Exists(profile)) Log.Warn($"Couldn't remove the self-test's browser profile: {profile}");
    }
}
