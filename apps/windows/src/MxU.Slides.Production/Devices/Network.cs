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
}
