using System.Runtime.InteropServices;
using System.Text;

namespace MxU.Slides.LocalApi;

/// <summary>
/// Advertises this computer's Local API on the church network as "_mxu-slides._tcp", the
/// service type the Mac's Bonjour uses, with its station id and name in the TXT record, so a
/// backup (Mac or Windows) finds it. Windows 10 and 11 have DNS-SD built in (DnsServiceRegister
/// in dnsapi.dll), which speaks the same multicast DNS as Bonjour: no extra install. Port of
/// BonjourAdvertiser.swift.
/// </summary>
public sealed class BonjourAdvertiser : IDisposable
{
    public const string ServiceType = "_mxu-slides._tcp";
    public const string StationKey = "stationHexId";
    public const string NameKey = "stationName";

    private readonly Lock gate = new();
    private IntPtr request;
    private IntPtr instance;
    private DnsSd.RegisterComplete? callback;
    private (string Name, int Port)? registered;

    /// <summary>Starts (or restarts) advertising. Does nothing on a system without DNS-SD.</summary>
    public void Start(string name, int port, IReadOnlyDictionary<string, string> txt)
    {
        lock (gate)
        {
            StopLocked();
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return;
            try
            {
                var keys = txt.Keys.Order(StringComparer.Ordinal).ToArray();
                var values = keys.Select(k => txt[k]).ToArray();
                // An instance name is one DNS label: no dots, at most 63 bytes.
                var label = new string(name.Select(c => c == '.' ? '-' : c).ToArray());
                if (Encoding.UTF8.GetByteCount(label) > 60) label = label[..Math.Min(label.Length, 40)];
                var host = Environment.MachineName + ".local";
                instance = DnsSd.DnsServiceConstructInstance($"{label}.{ServiceType}.local", host, IntPtr.Zero, IntPtr.Zero,
                    (ushort)port, 0, 0, (uint)keys.Length, keys, values);
                if (instance == IntPtr.Zero) return;
                callback = (_, _, _) => { };
                var registration = new DnsSd.RegisterRequest
                {
                    Version = DnsSd.RequestVersion1, InterfaceIndex = 0, ServiceInstance = instance,
                    Complete = Marshal.GetFunctionPointerForDelegate(callback), Context = IntPtr.Zero,
                };
                request = Marshal.AllocHGlobal(Marshal.SizeOf<DnsSd.RegisterRequest>());
                Marshal.StructureToPtr(registration, request, false);
                var status = DnsSd.DnsServiceRegister(request, IntPtr.Zero);
                if (status != DnsSd.RequestPending)
                {
                    StopLocked();
                    return;
                }
                registered = (name, port);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                StopLocked();
            }
        }
    }

    /// <summary>New TXT values (the station id arrives once the app knows it): advertised again with them.</summary>
    public void Update(IReadOnlyDictionary<string, string> txt)
    {
        (string Name, int Port)? current;
        lock (gate) current = registered;
        if (current is { } r) Start(r.Name, r.Port, txt);
    }

    public void Stop()
    {
        lock (gate) StopLocked();
    }

    public void Dispose() => Stop();

    private void StopLocked()
    {
        if (request != IntPtr.Zero)
        {
            if (registered is not null)
            {
                try
                {
                    DnsSd.DnsServiceDeRegister(request, IntPtr.Zero);
                }
                catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
                {
                }
            }
            Marshal.FreeHGlobal(request);
            request = IntPtr.Zero;
        }
        if (instance != IntPtr.Zero)
        {
            DnsSd.DnsServiceFreeInstance(instance);
            instance = IntPtr.Zero;
        }
        registered = null;
    }

    /// <summary>The TXT pairs for a server: its station id and name, when it has them.</summary>
    public static Dictionary<string, string> Txt(ApiServerInfo info)
    {
        var pairs = new Dictionary<string, string>();
        if (info.StationHexId is { } id) pairs[StationKey] = id;
        if (info.StationName is { } name) pairs[NameKey] = name;
        return pairs;
    }

    /// <summary>The TXT record's bytes as Bonjour writes them: each "key=value" after its length, sorted; one zero byte for none.</summary>
    public static byte[] TxtRecord(IReadOnlyDictionary<string, string> pairs)
    {
        var data = new List<byte>();
        foreach (var key in pairs.Keys.Order(StringComparer.Ordinal))
        {
            var entry = Encoding.UTF8.GetBytes($"{key}={pairs[key]}").Take(255).ToArray();
            data.Add((byte)entry.Length);
            data.AddRange(entry);
        }
        return data.Count == 0 ? [0] : [.. data];
    }
}

/// <summary>
/// Lists the other MxU Slides computers on the network (Mac or Windows), resolved to an address
/// and port, leaving out this one and any that don't say who they are. Browses with Windows'
/// DnsServiceBrowse and resolves each with DnsServiceResolve. Port of BonjourBrowser.swift.
/// </summary>
public sealed class BonjourBrowser(string? ownStationHexId) : IDisposable
{
    public sealed record Peer(string Name, string StationHexId, string? StationName, string Host, int Port);

    public sealed record Candidate(string Name, string StationHexId, string? StationName = null);

    /// <summary>A computer worth listing: it has a station id, and it isn't this one.</summary>
    public static Candidate? CandidateFrom(string name, IReadOnlyDictionary<string, string> txt, string? own)
    {
        if (!txt.TryGetValue(BonjourAdvertiser.StationKey, out var station) || station.Length == 0 || station == own) return null;
        return new Candidate(name, station, txt.TryGetValue(BonjourAdvertiser.NameKey, out var n) && n.Length > 0 ? n : null);
    }

    private readonly Lock gate = new();
    private readonly Dictionary<string, Peer> peers = [];
    private readonly HashSet<string> resolving = [];
    private readonly List<(IntPtr Request, IntPtr Cancel, Delegate Callback)> pending = [];
    private IntPtr browseRequest;
    private IntPtr browseCancel;
    private DnsSd.BrowseCallback? browseCallback;

    /// <summary>Raised (on a DNS-SD thread) with the whole list each time it changes.</summary>
    public event Action<IReadOnlyList<Peer>>? Changed;

    public IReadOnlyList<Peer> Peers
    {
        get
        {
            lock (gate) return peers.Values.OrderBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.StationHexId, StringComparer.Ordinal).ToList();
        }
    }

    public void Start()
    {
        lock (gate)
        {
            if (browseRequest != IntPtr.Zero || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return;
            try
            {
                browseCallback = OnBrowse;
                var query = Marshal.StringToHGlobalUni($"{BonjourAdvertiser.ServiceType}.local");
                var request = new DnsSd.BrowseRequest
                {
                    Version = DnsSd.RequestVersion1, InterfaceIndex = 0, QueryName = query,
                    Callback = Marshal.GetFunctionPointerForDelegate(browseCallback), Context = IntPtr.Zero,
                };
                browseRequest = Marshal.AllocHGlobal(Marshal.SizeOf<DnsSd.BrowseRequest>());
                Marshal.StructureToPtr(request, browseRequest, false);
                browseCancel = Marshal.AllocHGlobal(IntPtr.Size);
                if (DnsSd.DnsServiceBrowse(browseRequest, browseCancel) != DnsSd.RequestPending) StopLocked();
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                StopLocked();
            }
        }
    }

    public void Stop()
    {
        lock (gate) StopLocked();
    }

    public void Dispose() => Stop();

    private void StopLocked()
    {
        if (browseCancel != IntPtr.Zero)
        {
            try
            {
                DnsSd.DnsServiceBrowseCancel(browseCancel);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
            }
        }
        foreach (var (request, cancel, _) in pending)
        {
            try
            {
                DnsSd.DnsServiceResolveCancel(cancel);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
            }
        }
        // The requests' memory stays until the process ends: a callback may still be on its way.
        pending.Clear();
        browseRequest = IntPtr.Zero;
        browseCancel = IntPtr.Zero;
        resolving.Clear();
        peers.Clear();
    }

    private void OnBrowse(uint status, IntPtr context, IntPtr records)
    {
        if (records == IntPtr.Zero) return;
        try
        {
            for (var record = records; record != IntPtr.Zero; record = Marshal.ReadIntPtr(record))
            {
                var type = (ushort)Marshal.ReadInt16(record, 2 * IntPtr.Size);
                if (type != DnsSd.TypePtr) continue;
                var ttl = Marshal.ReadInt32(record, 2 * IntPtr.Size + 8);
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(record, 2 * IntPtr.Size + 16));
                if (name is null) continue;
                if (ttl == 0)
                {
                    // A goodbye: that computer has gone.
                    lock (gate) peers.Remove(InstanceLabel(name));
                    Changed?.Invoke(Peers);
                    continue;
                }
                Resolve(name);
            }
        }
        finally
        {
            DnsSd.DnsRecordListFree(records, 1);
        }
    }

    private void Resolve(string fullName)
    {
        lock (gate)
        {
            if (browseRequest == IntPtr.Zero || !resolving.Add(fullName)) return;
            DnsSd.ResolveComplete callback = null!;
            callback = (status, _, instance) => OnResolved(fullName, status, instance);
            var name = Marshal.StringToHGlobalUni(fullName);
            var request = new DnsSd.ResolveRequest
            {
                Version = DnsSd.RequestVersion1, InterfaceIndex = 0, QueryName = name,
                Complete = Marshal.GetFunctionPointerForDelegate(callback), Context = IntPtr.Zero,
            };
            var memory = Marshal.AllocHGlobal(Marshal.SizeOf<DnsSd.ResolveRequest>());
            Marshal.StructureToPtr(request, memory, false);
            var cancel = Marshal.AllocHGlobal(IntPtr.Size);
            pending.Add((memory, cancel, callback));
            if (DnsSd.DnsServiceResolve(memory, cancel) != DnsSd.RequestPending) resolving.Remove(fullName);
        }
    }

    private void OnResolved(string fullName, uint status, IntPtr instance)
    {
        lock (gate) resolving.Remove(fullName);
        if (instance == IntPtr.Zero) return;
        try
        {
            var info = Marshal.PtrToStructure<DnsSd.ServiceInstance>(instance);
            var txt = new Dictionary<string, string>();
            for (var i = 0; i < info.PropertyCount; i++)
            {
                var key = Marshal.PtrToStringUni(Marshal.ReadIntPtr(info.Keys, i * IntPtr.Size));
                var value = Marshal.PtrToStringUni(Marshal.ReadIntPtr(info.Values, i * IntPtr.Size));
                if (key is not null) txt[key] = value ?? "";
            }
            var label = InstanceLabel(fullName);
            if (CandidateFrom(label, txt, ownStationHexId) is not { } candidate) return;
            string? host = null;
            if (info.Ip4Address != IntPtr.Zero)
            {
                var address = (uint)Marshal.ReadInt32(info.Ip4Address);
                host = new System.Net.IPAddress(address).ToString();
            }
            else if (info.Ip6Address != IntPtr.Zero)
            {
                var bytes = new byte[16];
                Marshal.Copy(info.Ip6Address, bytes, 0, 16);
                host = $"[{new System.Net.IPAddress(bytes)}]";
            }
            host ??= Marshal.PtrToStringUni(info.HostName);
            if (host is null) return;
            lock (gate) peers[label] = new Peer(label, candidate.StationHexId, candidate.StationName, host, info.Port);
            Changed?.Invoke(Peers);
        }
        finally
        {
            DnsSd.DnsServiceFreeInstance(instance);
        }
    }

    /// <summary>"Booth Mac._mxu-slides._tcp.local" is "Booth Mac".</summary>
    internal static string InstanceLabel(string fullName)
    {
        var at = fullName.IndexOf("." + BonjourAdvertiser.ServiceType, StringComparison.OrdinalIgnoreCase);
        return at > 0 ? fullName[..at] : fullName;
    }
}

/// <summary>Windows' DNS-SD functions (dnsapi.dll, Windows 10 1809 and later).</summary>
internal static class DnsSd
{
    public const uint RequestVersion1 = 1;
    public const int RequestPending = 9506;
    public const ushort TypePtr = 12;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate void RegisterComplete(uint status, IntPtr context, IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate void BrowseCallback(uint status, IntPtr context, IntPtr records);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate void ResolveComplete(uint status, IntPtr context, IntPtr instance);

    [StructLayout(LayoutKind.Sequential)]
    public struct RegisterRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr ServiceInstance;
        public IntPtr Complete;
        public IntPtr Context;
        public IntPtr Credentials;
        public int UnicastEnabled;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BrowseRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr QueryName;
        public IntPtr Callback;
        public IntPtr Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ResolveRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr QueryName;
        public IntPtr Complete;
        public IntPtr Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ServiceInstance
    {
        public IntPtr InstanceName;
        public IntPtr HostName;
        public IntPtr Ip4Address;
        public IntPtr Ip6Address;
        public ushort Port;
        public ushort Priority;
        public ushort Weight;
        public uint PropertyCount;
        public IntPtr Keys;
        public IntPtr Values;
        public uint InterfaceIndex;
    }

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr DnsServiceConstructInstance(
        string serviceName, string hostName, IntPtr ip4, IntPtr ip6, ushort port, ushort priority, ushort weight,
        uint propertiesCount, string[] keys, string[] values);

    [DllImport("dnsapi.dll")]
    public static extern void DnsServiceFreeInstance(IntPtr instance);

    [DllImport("dnsapi.dll")]
    public static extern int DnsServiceRegister(IntPtr request, IntPtr cancel);

    [DllImport("dnsapi.dll")]
    public static extern int DnsServiceDeRegister(IntPtr request, IntPtr cancel);

    [DllImport("dnsapi.dll")]
    public static extern int DnsServiceBrowse(IntPtr request, IntPtr cancel);

    [DllImport("dnsapi.dll")]
    public static extern int DnsServiceBrowseCancel(IntPtr cancel);

    [DllImport("dnsapi.dll")]
    public static extern int DnsServiceResolve(IntPtr request, IntPtr cancel);

    [DllImport("dnsapi.dll")]
    public static extern int DnsServiceResolveCancel(IntPtr cancel);

    [DllImport("dnsapi.dll")]
    public static extern void DnsRecordListFree(IntPtr records, int freeType);
}
