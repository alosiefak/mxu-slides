using System.Globalization;
using System.Net;
using System.Net.Sockets;
using MxU.Slides.Production.Devices;

namespace MxU.Slides.Production.Dmx;

/// <summary>
/// One DMX node on the network (Art-Net or sACN): the show's universes it is sent, and how
/// they get there. The <see cref="DmxEngine"/> hands it every frame (40 a second) from its own
/// thread, and it sends each of its universes as one UDP packet. UDP never waits for an
/// answer, so a node that's switched off costs nothing: the packets go nowhere.
/// <para>
/// The source port is left to Windows rather than bound to 6454: binding Art-Net's port would
/// fight any other lighting program on this PC for it, and nodes take ArtDmx from any port.
/// </para>
/// </summary>
public sealed class DmxOutput : ProductionDevice
{
    private readonly Lock gate = new();
    private readonly Dictionary<int, byte> sequences = [];
    private readonly Guid sourceId;
    private readonly string sourceName;
    private Socket? socket;
    private IPAddress? unicast;
    private bool sending;
    private string? lastProblem;

    /// <param name="sourceId">sACN's CID for this PC (Art-Net has none).</param>
    public DmxOutput(DeviceSettings settings, Guid sourceId, string sourceName) : base(settings)
    {
        this.sourceId = sourceId;
        this.sourceName = sourceName;
    }

    /// <summary>Packets sent so far (the Settings line and the tests).</summary>
    public long PacketsSent { get; private set; }

    public override void Start()
    {
        lock (gate)
        {
            if (socket is not null) return;
            try
            {
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { EnableBroadcast = true };
                // Off: Windows would otherwise report an earlier "port unreachable" as an error on a later send.
                socket.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
                var delivery = Settings.EffectiveDelivery;
                if (delivery == DmxDelivery.Unicast || (delivery == DmxDelivery.Broadcast && Settings.Host.Trim().Length > 0))
                    unicast = Network.ResolveIPv4(Settings.Host);
                else if (delivery == DmxDelivery.Broadcast)
                    unicast = IPAddress.Broadcast;
            }
            catch (Exception ex) when (ex is SocketException or ArgumentException)
            {
                SetStatus(DeviceState.Problem, $"Can't find {Settings.Host} on the network");
                return;
            }
        }
        SetStatus(DeviceState.Idle, "Ready: sends when a lighting step runs");
    }

    /// <summary>Where a show universe goes, as the status line says it.</summary>
    public string Describe(int showUniverse) => Kind == DeviceKind.ArtNet
        ? $"universe {showUniverse} as Art-Net {ArtNetName(ArtNetPacket.PortAddress(showUniverse))}"
        : $"universe {showUniverse} as sACN {SacnPacket.Universe(showUniverse).ToString(CultureInfo.InvariantCulture)}";

    /// <summary>A port-address as lighting people write it: net:sub-net:universe.</summary>
    public static string ArtNetName(int portAddress) =>
        string.Create(CultureInfo.InvariantCulture, $"{portAddress >> 8}:{(portAddress >> 4) & 0xF}:{portAddress & 0xF}");

    /// <summary>Sends this node's universes from a frame (called on the DMX engine's thread).</summary>
    public void Send(IReadOnlyDictionary<int, byte[]> frame)
    {
        if (!Settings.Enabled) return;
        string? problem = null;
        var sentAny = false;
        lock (gate)
        {
            if (socket is null) return;
            foreach (var universe in Settings.EffectiveUniverses)
            {
                if (!frame.TryGetValue(universe, out var levels)) continue;
                try
                {
                    var (packet, to) = Packet(universe, levels);
                    socket.SendTo(packet, to);
                    PacketsSent++;
                    sentAny = true;
                }
                catch (SocketException ex)
                {
                    problem = ex.SocketErrorCode == SocketError.NetworkUnreachable || ex.SocketErrorCode == SocketError.HostUnreachable
                        ? "The network can't reach it"
                        : ex.Message;
                }
            }
        }
        if (problem is not null)
        {
            if (problem != lastProblem) Note($"can't send: {problem}");
            lastProblem = problem;
            SetStatus(DeviceState.Problem, problem);
        }
        else if (sentAny && (!sending || lastProblem is not null))
        {
            sending = true;
            lastProblem = null;
            SetStatus(DeviceState.Online, $"Sending {string.Join(", ", Settings.EffectiveUniverses.Select(Describe))}");
        }
    }

    /// <summary>The packet for one universe and where it goes. Called with the lock held.</summary>
    private (byte[] Packet, IPEndPoint To) Packet(int universe, byte[] levels)
    {
        var port = Settings.EffectivePort;
        var sequence = sequences.TryGetValue(universe, out var last) ? last : (byte)0;
        if (Kind == DeviceKind.ArtNet)
        {
            sequence = ArtNetPacket.NextSequence(sequence);
            sequences[universe] = sequence;
            return (ArtNetPacket.Dmx(ArtNetPacket.PortAddress(universe), sequence, levels), new IPEndPoint(unicast ?? IPAddress.Broadcast, port));
        }
        sequence = (byte)(sequence + 1);
        sequences[universe] = sequence;
        var sacnUniverse = SacnPacket.Universe(universe);
        var to = unicast ?? SacnPacket.MulticastGroup(sacnUniverse);
        var priority = (byte)Math.Clamp(Settings.Priority ?? 100, 0, 200);
        return (SacnPacket.Data(sourceId, sourceName, priority, sacnUniverse, sequence, levels), new IPEndPoint(to, port));
    }

    /// <summary>
    /// sACN: tells receivers this source is going (three packets with "stream terminated", as
    /// E1.31 asks), so a node merging several sources drops ours at once instead of after 2.5 s.
    /// </summary>
    public void Terminate(IReadOnlyCollection<int> universes)
    {
        if (Kind != DeviceKind.Sacn) return;
        lock (gate)
        {
            if (socket is null) return;
            foreach (var universe in Settings.EffectiveUniverses.Where(universes.Contains))
            {
                for (var i = 0; i < 3; i++)
                {
                    var sequence = (byte)((sequences.TryGetValue(universe, out var last) ? last : 0) + 1);
                    sequences[universe] = sequence;
                    var sacnUniverse = SacnPacket.Universe(universe);
                    var packet = SacnPacket.Data(sourceId, sourceName, 100, sacnUniverse, sequence, new byte[DmxMixerChannels], SacnPacket.StreamTerminated);
                    try { socket.SendTo(packet, new IPEndPoint(unicast ?? SacnPacket.MulticastGroup(sacnUniverse), Settings.EffectivePort)); }
                    catch (SocketException) { }
                }
            }
        }
    }

    private const int DmxMixerChannels = 512;

    public override void Dispose()
    {
        base.Dispose();
        lock (gate)
        {
            socket?.Dispose();
            socket = null;
        }
    }
}
