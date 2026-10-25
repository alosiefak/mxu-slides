using System.Net;
using System.Net.Sockets;
using MxU.Slides.Production.Devices;

namespace MxU.Slides.Production.Tests.Fakes;

internal static class Wait
{
    /// <summary>Waits until <paramref name="done"/> is true (polling), or fails the test after <paramref name="milliseconds"/>.</summary>
    public static async Task Until(Func<bool> done, int milliseconds = 5000, string? what = null)
    {
        var until = Environment.TickCount64 + milliseconds;
        while (!done())
        {
            if (Environment.TickCount64 > until) Assert.Fail($"Timed out waiting for {what ?? "the condition"}");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>A port nothing listens on (bound for a moment, then let go).</summary>
    public static int UnusedPort(SocketType type = SocketType.Dgram)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, type, type == SocketType.Dgram ? ProtocolType.Udp : ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    public static DeviceSettings Device(DeviceKind kind, int port, string host = "127.0.0.1") =>
        new() { Kind = kind, Name = $"Test {kind}", Host = host, Port = port };

    /// <summary>A throwaway folder, deleted when disposed.</summary>
    public sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"MxU production test {Guid.NewGuid():N}");

        public TempFolder() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
