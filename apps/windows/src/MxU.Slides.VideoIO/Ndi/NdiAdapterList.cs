using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MxU.Slides.VideoIO.Ndi;

/// <summary>
/// A network adapter NDI could be pinned to. <see cref="Id"/> is Windows' stable adapter
/// id (a GUID string; the Mac uses the BSD name, en0), <see cref="DisplayName"/> the
/// connection name the operator sees ("Ethernet 2", "Wi-Fi").
/// </summary>
public sealed record NdiAdapter(string Id, string DisplayName, string Ipv4);

/// <summary>
/// The adapters that are up, not loopback, and have an IPv4 address: the choices for
/// "send NDI on this network". Port of NDIKit/NDIAdapterList.swift (getifaddrs and
/// SystemConfiguration there, System.Net.NetworkInformation here).
/// </summary>
public static class NdiAdapterList
{
    public static IReadOnlyList<NdiAdapter> Current()
    {
        var adapters = new List<NdiAdapter>();
        NetworkInterface[] interfaces;
        try { interfaces = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { return adapters; }
        foreach (var adapter in interfaces)
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (adapters.Any(a => a.Id == adapter.Id)) continue;
            var ipv4 = adapter.GetIPProperties().UnicastAddresses
                .Select(u => u.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a));
            if (ipv4 is null) continue;
            adapters.Add(new NdiAdapter(adapter.Id, string.IsNullOrWhiteSpace(adapter.Name) ? adapter.Description : adapter.Name, ipv4.ToString()));
        }
        return [.. adapters.OrderBy(a => a.Id, StringComparer.Ordinal)];
    }

    /// <summary>
    /// One string for "which adapters have which addresses", the same whatever order the
    /// system lists them in. When it changes (a cable moved, DHCP gave a new address), the
    /// app re-applies the NDI network pin.
    /// </summary>
    public static string Fingerprint(IEnumerable<NdiAdapter> adapters) =>
        string.Join(",", adapters.Select(a => $"{a.Id}={a.Ipv4}").Order(StringComparer.Ordinal));
}
