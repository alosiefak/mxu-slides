using System.Net;
using System.Net.Sockets;

namespace MxU.Slides.Production.Devices;

/// <summary>Small network helpers the devices share.</summary>
public static class Network
{
    /// <summary>
    /// The IPv4 address for a host: an address as typed ("192.168.1.20"), or a computer name
    /// looked up (the first IPv4 answer; "localhost" is 127.0.0.1). Production gear on a church
    /// network is almost always reached over IPv4. Throws <see cref="SocketException"/> when it can't be found.
    /// </summary>
    public static IPAddress ResolveIPv4(string host)
    {
        var trimmed = host.Trim();
        if (IPAddress.TryParse(trimmed, out var parsed)) return parsed;
        if (trimmed.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return IPAddress.Loopback;
        return Dns.GetHostAddresses(trimmed).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
            ?? throw new SocketException((int)SocketError.HostNotFound);
    }

    /// <summary>
    /// Opens a TCP connection to <paramref name="host"/>, giving up after <paramref name="timeout"/>.
    /// It's done the old way, on the calling thread: start the connect without waiting, then ask
    /// the socket (select) whether it has finished. The usual ways to put a time limit on a connect
    /// finish through .NET's thread pool, and when that's busy (an import, a busy PC) a connect could
    /// wait far longer than its limit; this never needs the pool. Throws <see cref="TimeoutException"/>
    /// or <see cref="SocketException"/>; the returned socket is blocking again.
    /// </summary>
    public static Socket ConnectTcp(string host, int port, TimeSpan timeout)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            var endpoint = new IPEndPoint(ResolveIPv4(host), port);
            socket.Blocking = false;
            try
            {
                socket.Connect(endpoint);
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.WouldBlock or SocketError.InProgress)
            {
                // Still connecting: wait below.
            }
            // Windows says "connected" by making the socket writable, and "refused" by an error on it.
            var micros = (int)Math.Clamp(timeout.TotalMilliseconds * 1000, 1, int.MaxValue);
            var writable = new List<Socket> { socket };
            var failed = new List<Socket> { socket };
            Socket.Select(null, writable, failed, micros);
            if (failed.Count > 0)
            {
                var code = socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error) is int error && error != 0
                    ? error
                    : (int)SocketError.ConnectionRefused;
                throw new SocketException(code);
            }
            if (writable.Count == 0) throw new TimeoutException($"No answer from {host}:{port}");
            socket.Blocking = true;
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
