using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MxU.Slides.Production.Simulators;

/// <summary>
/// A pretend vMix web API on 127.0.0.1: it records each request's path and Authorization
/// header, answers /api with a small state document, and anything else with vMix's
/// "Function completed successfully." When it has a user name and password it answers 401
/// to requests without them, as vMix's web controller does. For the unit tests and the app's self-test only.
/// </summary>
public sealed class VmixSimulator : IDisposable
{
    public const string StateXml = """
        <vmix><version>27.0.0.49</version><edition>4K</edition>
        <inputs>
        <input key="aaa" number="1" type="Capture" title="Camera 1" state="Running">Camera 1</input>
        <input key="bbb" number="2" type="Capture" title="Camera 2" state="Running">Camera 2</input>
        <input key="ccc" number="3" type="GT" title="Lower third" state="Paused">Lower third</input>
        </inputs>
        <active>1</active><preview>2</preview><recording>False</recording><streaming>True</streaming>
        </vmix>
        """;

    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly string? expectedAuth;
    private volatile bool stopped;

    public VmixSimulator(string? user = null, string? password = null)
    {
        if (user is not null || password is not null)
            expectedAuth = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
        listener.Start();
        // Threads of its own with blocking sockets, so a busy thread pool in the app can't slow it.
        new Thread(AcceptLoop) { IsBackground = true, Name = "vMix simulator" }.Start();
    }

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    /// <summary>The paths of the function calls (not the state polls), in order, as they arrive (a slow vMix still gets them).</summary>
    public ConcurrentQueue<string> Functions { get; } = new();

    public int Polls;

    /// <summary>Wait this long before answering (a slow vMix).</summary>
    public int DelayMilliseconds;

    private void AcceptLoop()
    {
        while (!stopped)
        {
            TcpClient client;
            try { client = listener.AcceptTcpClient(); }
            catch (Exception) { return; }
            new Thread(() => Serve(client)) { IsBackground = true, Name = "vMix simulator request" }.Start();
        }
    }

    private void Serve(TcpClient client)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream();
            var request = new StringBuilder();
            while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                var next = stream.ReadByte();
                if (next < 0) return;
                request.Append((char)next);
            }
            var lines = request.ToString().Split("\r\n");
            var path = lines[0].Split(' ')[1];
            var auth = lines.FirstOrDefault(l => l.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))?[14..].Trim();
            string status;
            string body;
            if (expectedAuth is not null && auth != expectedAuth)
            {
                (status, body) = ("401 Unauthorized", "Unauthorized");
            }
            else if (path == "/api")
            {
                Interlocked.Increment(ref Polls);
                (status, body) = ("200 OK", StateXml);
            }
            else
            {
                Functions.Enqueue(path);
                (status, body) = ("200 OK", "Function completed successfully.");
            }
            if (DelayMilliseconds > 0) Thread.Sleep(DelayMilliseconds);
            var bytes = Encoding.UTF8.GetBytes(body);
            stream.Write(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status}\r\nContent-Type: text/plain\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"));
            stream.Write(bytes);
        }
        catch (IOException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    public string? WaitForFunction(Func<string, bool> match, int milliseconds = 3000)
    {
        var until = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < until)
        {
            var found = Functions.FirstOrDefault(match);
            if (found is not null) return found;
            Thread.Sleep(10);
        }
        return null;
    }

    public void Dispose()
    {
        stopped = true;
        listener.Stop();
    }
}
