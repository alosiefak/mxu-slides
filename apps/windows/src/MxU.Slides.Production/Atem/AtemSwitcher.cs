using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using MxU.Slides.Production.Devices;

namespace MxU.Slides.Production.Atem;

/// <summary>
/// A Blackmagic ATEM switcher, over its UDP protocol on port 9910 (worked out by the
/// atem-connection and libqatem projects; see <see cref="AtemHeader"/>).
/// <para>
/// UDP can lose packets, so the protocol adds its own delivery: each packet that matters has
/// an id and the other side acks it. One thread (named after the switcher) does the session:
/// </para>
/// <list type="number">
/// <item>It sends a hello, and every second again until the switcher answers.</item>
/// <item>The switcher answers with the session to use, then sends its whole state, packet by
/// packet. Each in-order packet is acked and read (<see cref="AtemState"/>); one that arrives
/// out of order isn't, so the switcher sends it again.</item>
/// <item>From then on the switcher sends changes and, when there are none, an empty "ping"
/// about twice a second. They are all acked, which is what keeps the session alive. If
/// nothing comes for five seconds the switcher has gone (unplugged, rebooted): the thread
/// starts again from the hello, quietly, until it's back.</item>
/// </list>
/// Commands (cut, program input…) are sent from the device's own queue: the packet goes
/// out at once, and it's sent again every 100 ms until the switcher acks it, ten times at
/// most. The command waits for its ack, so commands arrive in the order they were given.
/// </summary>
public sealed class AtemSwitcher : ProductionDevice
{
    /// <summary>How often a hello is sent while the switcher doesn't answer.</summary>
    internal TimeSpan HelloRetry { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How long a sent packet waits for its ack before it's sent again.</summary>
    internal TimeSpan ResendAfter { get; set; } = TimeSpan.FromMilliseconds(100);

    internal const int MaxResends = 10;

    /// <summary>How long the switcher may be silent before the session counts as lost.</summary>
    internal TimeSpan SilenceTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often we re-send our last ack when we have nothing else to say (a keepalive).</summary>
    internal TimeSpan KeepaliveEvery { get; set; } = TimeSpan.FromSeconds(1);

    private enum Phase { Closed, Hello, Syncing, Ready }

    private sealed class InFlight(ushort id, byte[] packet, long sentAt)
    {
        public ushort Id { get; } = id;
        public byte[] Packet { get; } = packet;
        public long SentAt { get; set; } = sentAt;
        public int Resends { get; set; }
        public ManualResetEventSlim Done { get; } = new(false);
        public bool Acked { get; set; }
    }

    private readonly Lock gate = new();
    private readonly List<InFlight> inFlight = [];
    private Socket? socket;
    private Thread? thread;
    private Phase phase = Phase.Closed;
    private ushort session;
    private ushort lastReceived;
    private ushort nextSend = 1;
    private long lastHeard;
    private long lastSent;
    private long helloSent;
    private int helloAttempts;

    public AtemSwitcher(DeviceSettings settings) : base(settings) { }

    /// <summary>What the switcher has told us about itself.</summary>
    public AtemState State { get; } = new();

    private string Where => $"{Settings.Host}:{Settings.EffectivePort.ToString(CultureInfo.InvariantCulture)}";

    public override DeviceCatalog Catalog => DeviceCatalog.Empty with
    {
        Inputs = [.. State.Inputs.Select(i => new CatalogEntry(i.Id, i.LongName.Length > 0 ? i.LongName : $"Source {i.Id}"))],
        Macros = [.. State.Macros.OrderBy(m => m.Key).Select(m => new CatalogEntry(m.Key + 1, m.Value))],
    };

    public override void Start()
    {
        if (thread is not null) return;
        SetStatus(DeviceState.Connecting, $"Connecting to {Where}…");
        thread = new Thread(Run) { IsBackground = true, Name = $"MxU ATEM: {Name}", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    // MARK: Commands (numbers count from 1, as on the switcher's panel)

    public Task<bool> Program(int mixEffect, int source) =>
        Send($"Program: {SourceName(source)}", AtemCommands.ProgramInput(Index(mixEffect), source));

    public Task<bool> Preview(int mixEffect, int source) =>
        Send($"Preview: {SourceName(source)}", AtemCommands.PreviewInput(Index(mixEffect), source));

    public Task<bool> Cut(int mixEffect) => Send("Cut", AtemCommands.Cut(Index(mixEffect)));

    public Task<bool> Auto(int mixEffect) => Send("Auto", AtemCommands.Auto(Index(mixEffect)));

    public Task<bool> FadeToBlack(int mixEffect) => Send("Fade to Black", AtemCommands.FadeToBlack(Index(mixEffect)));

    public Task<bool> RunMacro(int macro) => Send($"Run macro {macro}", AtemCommands.RunMacro(Index(macro)));

    public Task<bool> Aux(int aux, int source) => Send($"Aux {aux}: {SourceName(source)}", AtemCommands.AuxSource(Index(aux), source));

    public Task<bool> UpstreamKey(int mixEffect, int keyer, bool onAir) =>
        Send($"Key {keyer} {(onAir ? "on" : "off")}", AtemCommands.UpstreamKeyOnAir(Index(mixEffect), Index(keyer), onAir));

    public Task<bool> DownstreamKey(int keyer, bool onAir) =>
        Send($"DSK {keyer} {(onAir ? "on" : "off")}", AtemCommands.DownstreamKeyOnAir(Index(keyer), onAir));

    private static int Index(int fromOne) => Math.Clamp(fromOne, 1, 255) - 1;

    private string SourceName(int source) => State.InputName(source) is { Length: > 0 } name ? name : $"source {source}";

    /// <summary>Sends one command as a packet that must be acked, and waits (on the device's queue) for the ack.</summary>
    private Task<bool> Send(string what, byte[] command) => Enqueue(what, () =>
    {
        InFlight sent;
        lock (gate)
        {
            if (phase is not (Phase.Syncing or Phase.Ready)) return NotConnected(what);
            var id = nextSend;
            nextSend = AtemPacket.Next(nextSend);
            sent = new InFlight(id, AtemPacket.Reliable(session, id, command), Stopwatch.GetTimestamp());
            inFlight.Add(sent);
            Transmit(sent.Packet);
        }
        // The receive thread sets Done when the ack comes (or the session is lost). Resends run there too.
        var waited = sent.Done.Wait(ResendAfter * (MaxResends + 2));
        if (!waited || !sent.Acked) Note($"{what}: the switcher didn't confirm it");
        else Note($"sent {what}");
        return waited && sent.Acked;
    });

    // MARK: The session thread

    private void Run()
    {
        var buffer = new byte[2048];
        while (!IsDisposed)
        {
            if (socket is null && !Open()) continue;
            try
            {
                Maintain(Stopwatch.GetTimestamp());
                // Poll rather than a timed Receive: a quiet 20 ms then costs nothing (no exception).
                if (!socket!.Poll(20_000, SelectMode.SelectRead)) continue;
                var length = socket.Receive(buffer);
                Handle(buffer.AsSpan(0, length));
            }
            catch (SocketException ex)
            {
                // A "port unreachable" from an earlier packet, or the network went away: carry on.
                if (ex.SocketErrorCode is not (SocketError.ConnectionReset or SocketError.TimedOut or SocketError.MessageSize))
                    Thread.Sleep(200);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    /// <summary>Makes the socket, aimed at the switcher. False (after a pause) when its address can't be found.</summary>
    private bool Open()
    {
        try
        {
            var address = Network.ResolveIPv4(Settings.Host);
            var made = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            // Without this Windows reports an ICMP "port unreachable" as an error on the next receive.
            made.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
            made.Connect(new IPEndPoint(address, Settings.EffectivePort));
            socket = made;
            return true;
        }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException or ArgumentException)
        {
            SetStatus(DeviceState.Problem, $"Can't find {Settings.Host} on the network");
            Thread.Sleep(HelloRetry * 3);
            return false;
        }
    }

    private void Maintain(long now)
    {
        lock (gate)
        {
            switch (phase)
            {
                case Phase.Closed:
                case Phase.Hello:
                    if (Stopwatch.GetElapsedTime(helloSent, now) < HelloRetry && phase == Phase.Hello) return;
                    BeginHello(now);
                    return;
            }
            if (Stopwatch.GetElapsedTime(lastHeard, now) > SilenceTimeout)
            {
                Lost("The switcher stopped answering");
                return;
            }
            foreach (var sent in inFlight.Where(p => Stopwatch.GetElapsedTime(p.SentAt, now) >= ResendAfter).ToList())
            {
                if (sent.Resends >= MaxResends)
                {
                    Lost("The switcher didn't confirm a command");
                    return;
                }
                sent.Resends++;
                sent.SentAt = now;
                Transmit(AtemPacket.AsRetransmit(sent.Packet));
            }
            if (Stopwatch.GetElapsedTime(lastSent, now) >= KeepaliveEvery) Transmit(AtemPacket.Ack(session, lastReceived));
        }
    }

    private void BeginHello(long now)
    {
        phase = Phase.Hello;
        session = AtemPacket.ClientSession;
        lastReceived = 0;
        nextSend = 1;
        helloSent = now;
        helloAttempts++;
        Transmit(AtemPacket.Hello(session));
        // The first try is "connecting"; after that the light goes red, and the tries go on quietly.
        if (helloAttempts > 2) SetStatus(DeviceState.Problem, $"No answer from {Where}");
    }

    private void Handle(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < AtemHeader.Size) return;
        var header = AtemHeader.Read(packet);
        if (header.Length != packet.Length) return;
        List<(string Name, byte[] Data)>? commands = null;
        lock (gate)
        {
            if (header.Flags.HasFlag(AtemFlags.Hello))
            {
                if (phase != Phase.Hello) return;
                // Accepted: take the session it gives, ack its hello, and the state burst follows.
                session = header.Session;
                lastReceived = header.PacketId;
                lastHeard = Stopwatch.GetTimestamp();
                phase = Phase.Syncing;
                helloAttempts = 0;
                State.Reset();
                Transmit(AtemPacket.Ack(session, lastReceived));
                SetStatus(DeviceState.Connecting, "Reading the switcher's setup…");
                return;
            }
            if (phase is Phase.Closed or Phase.Hello) return;
            lastHeard = Stopwatch.GetTimestamp();
            // The switcher moves to its own session id after the hello: follow whatever it uses.
            session = header.Session;

            if (header.Flags.HasFlag(AtemFlags.RetransmitRequest))
                foreach (var sent in inFlight.Where(p => AtemPacket.Covers(p.Id, header.RetransmitFrom)))
                    Transmit(AtemPacket.AsRetransmit(sent.Packet));

            if (header.Flags.HasFlag(AtemFlags.AckRequest))
            {
                if (header.PacketId == AtemPacket.Next(lastReceived))
                {
                    lastReceived = header.PacketId;
                    Transmit(AtemPacket.Ack(session, lastReceived));
                    if (packet.Length > AtemHeader.Size)
                        commands = [.. AtemPacket.Commands(packet[AtemHeader.Size..].ToArray())];
                }
                else if (AtemPacket.Covers(lastReceived, header.PacketId))
                {
                    // A resend of one we already have: our ack was lost, so say it again.
                    Transmit(AtemPacket.Ack(session, lastReceived));
                }
                // Otherwise one went missing in between: say nothing, and the switcher sends it again.
            }

            if (header.Flags.HasFlag(AtemFlags.Ack))
            {
                foreach (var sent in inFlight.Where(p => AtemPacket.Covers(header.AckId, p.Id)).ToList())
                {
                    inFlight.Remove(sent);
                    sent.Acked = true;
                    sent.Done.Set();
                }
            }
        }
        if (commands is null) return;
        var wasComplete = State.InitComplete;
        foreach (var (name, data) in commands) State.Apply(name, data);
        if (!State.InitComplete) return;
        lock (gate)
        {
            if (phase == Phase.Syncing) phase = Phase.Ready;
        }
        if (!wasComplete || commands.Any(c => c.Name is "InPr" or "_pin"))
            SetStatus(DeviceState.Online, $"{State.ProductName ?? "ATEM"} · {State.Inputs.Count} sources");
    }

    /// <summary>The session has gone: drop what's waiting, tell the light, and start again from the hello.</summary>
    private void Lost(string why)
    {
        phase = Phase.Closed;
        foreach (var sent in inFlight) sent.Done.Set();
        inFlight.Clear();
        State.Reset();
        Note(why);
        SetStatus(DeviceState.Problem, $"{why}; trying again…");
    }

    private void Transmit(byte[] packet)
    {
        try
        {
            socket?.Send(packet);
            lastSent = Stopwatch.GetTimestamp();
        }
        catch (SocketException)
        {
            // The network is down; the timeouts above notice and start again.
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        lock (gate)
        {
            foreach (var sent in inFlight) sent.Done.Set();
            inFlight.Clear();
            phase = Phase.Closed;
        }
        socket?.Dispose();
    }
}
