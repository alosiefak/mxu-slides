using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MxU.Slides.Production.Simulators;

/// <summary>
/// A pretend ATEM switcher on 127.0.0.1, speaking the UDP protocol the way a real one does:
/// it answers a hello, waits for the client's ack, sends its state as numbered packets
/// (version, product name, input names, a macro, program and preview, "initialisation
/// complete"), acks the client's command packets, and pings twice a second or so.
/// <para>
/// Like a real switcher it holds on to every numbered packet until the client acks it,
/// sends it again (flagged as a resend) every 200 ms until then, and drops a client that
/// stops acking. So the tests can check the client's side of that: that it acks, acks in
/// order, and acks a resend it already had. Knobs make it lose a state packet, lose the
/// client's acks, ignore a command packet, or ask for one to be sent again.
/// </para>
/// It's written straight from the protocol notes, not from the app's own packet code, so
/// a mistake in one shows up as a failed test rather than cancelling out. The unit tests and
/// the app's self-test drive the real code against it; nothing in a show uses it.
/// </summary>
public sealed class AtemSimulator : IDisposable
{
    public const ushort SwitcherSession = 0x8001;

    // The header's flags (the top five bits of the first byte).
    private const int AckRequest = 0x01;
    private const int Hello = 0x02;
    private const int Resend = 0x04;
    private const int RetransmitRequest = 0x08;
    private const int Ack = 0x10;

    /// <summary>How long a numbered packet waits for the client's ack before it's sent again.</summary>
    private const int ResendMilliseconds = 200;

    private sealed class Unacked(ushort id, byte[] packet, long at)
    {
        public ushort Id { get; } = id;
        public byte[] Packet { get; } = packet;
        public long FirstSent { get; } = at;
        public long LastSent { get; set; } = at;
    }

    private readonly Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly Thread thread;
    private readonly Lock gate = new();
    private readonly List<Unacked> unacked = [];
    private readonly HashSet<ushort> seen = [];
    private volatile bool stopped;
    private EndPoint? client;
    private ushort clientSession;
    private ushort nextId = 1;
    private bool established;
    private long lastPing;
    private ushort? gap;

    public AtemSimulator(params string[] inputNames)
    {
        InputNames = inputNames.Length > 0 ? inputNames : ["Camera 1", "Camera 2", "Camera 3"];
        socket.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        thread = new Thread(Run) { IsBackground = true, Name = "Fake ATEM" };
        thread.Start();
    }

    public int Port => ((IPEndPoint)socket.LocalEndPoint!).Port;

    public string[] InputNames { get; }

    /// <summary>Commands the client sent, in order, as (name, data).</summary>
    public ConcurrentQueue<(string Name, byte[] Data)> Commands { get; } = new();

    /// <summary>Hellos the client sent (one per session).</summary>
    public int Hellos;

    /// <summary>Command packets the client sent again (flagged as resends).</summary>
    public int Retransmits;

    /// <summary>Numbered packets this switcher sent again because the client hadn't acked them.</summary>
    public int Resends;

    /// <summary>Times the client stopped acking for so long that the switcher gave up on it.</summary>
    public int SessionsDropped;

    /// <summary>Packets after the hello that didn't carry the session id the switcher gave.</summary>
    public int WrongSession;

    /// <summary>"Send it again" requests this switcher made.</summary>
    public int RetransmitRequests;

    /// <summary>The last packet id the client acked.</summary>
    public int LastClientAck = -1;

    /// <summary>The client acked a packet beyond one it never got: it must only ack in order.</summary>
    public volatile bool AckedPastAGap;

    /// <summary>Numbered packets still waiting for the client's ack.</summary>
    public int Unacknowledged
    {
        get
        {
            lock (gate) return unacked.Count;
        }
    }

    /// <summary>Ignore (don't ack) this many command packets, to make the client send them again.</summary>
    public int DropCommandPackets;

    /// <summary>Answer the next command packet with "send me that again" instead of an ack.</summary>
    public volatile bool AskAgainForNextCommand;

    /// <summary>Lose this state packet (1 is the first) the first time it's sent: the ones after it arrive out of order.</summary>
    public int LoseStatePacket;

    /// <summary>Lose this many of the client's acks, so this switcher sends those packets again.</summary>
    public int LoseClientAcks;

    /// <summary>A client that hasn't acked a packet for this long is dropped (a real switcher gives up in a few seconds).</summary>
    public int DropClientAfterMilliseconds = 5000;

    /// <summary>Off: no pings (so only the client's acks of resends can clear what it hasn't acked).</summary>
    public volatile bool Pinging = true;

    /// <summary>Say nothing at all (a switcher that's switched off).</summary>
    public volatile bool Silent;

    private void Run()
    {
        var buffer = new byte[2048];
        while (!stopped)
        {
            try
            {
                if (!Silent) lock (gate) Maintain();
                if (!socket.Poll(20_000, SelectMode.SelectRead)) continue;
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                var length = socket.ReceiveFrom(buffer, ref from);
                if (Silent) continue;
                Handle(buffer.AsSpan(0, length).ToArray(), from);
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { return; }
        }
    }

    /// <summary>Pings, resends what the client hasn't acked, and gives up on a client that never acks. Called with the lock held.</summary>
    private void Maintain()
    {
        if (!established || client is null) return;
        var now = Environment.TickCount64;
        if (Pinging && now - lastPing > 400)
        {
            lastPing = now;
            SendReliable([]);
        }
        foreach (var waiting in unacked)
        {
            if (now - waiting.FirstSent > DropClientAfterMilliseconds)
            {
                // The client has gone (or never acks): stop talking to it until it says hello again.
                Interlocked.Increment(ref SessionsDropped);
                established = false;
                unacked.Clear();
                return;
            }
            if (now - waiting.LastSent < ResendMilliseconds) continue;
            waiting.LastSent = now;
            var again = (byte[])waiting.Packet.Clone();
            again[0] |= Resend << 3;
            Interlocked.Increment(ref Resends);
            if (gap == waiting.Id) gap = null;
            socket.SendTo(again, client);
        }
    }

    private void Handle(byte[] packet, EndPoint from)
    {
        if (packet.Length < 12) return;
        var flags = packet[0] >> 3;
        var length = BinaryPrimitives.ReadUInt16BigEndian(packet) & 0x07FF;
        if (length != packet.Length) return;
        var session = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2));
        var ackId = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4));
        var packetId = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(10));
        lock (gate)
        {
            if ((flags & Hello) != 0)
            {
                // A hello is 20 bytes with "connect" (0x01) at the start of its body; anything else is ignored.
                if (packet.Length != 20 || packet[12] != 0x01) return;
                // Answer with the same session, "accepted" (0x02) in the body. A new session starts from scratch.
                Interlocked.Increment(ref Hellos);
                client = from;
                clientSession = session;
                established = false;
                nextId = 1;
                gap = null;
                seen.Clear();
                unacked.Clear();
                var reply = new byte[20];
                BinaryPrimitives.WriteUInt16BigEndian(reply, (ushort)((Hello << 11) | 20));
                BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(2), clientSession);
                reply[12] = 0x02;
                socket.SendTo(reply, from);
                return;
            }
            if (!established)
            {
                // The client acks our hello (packet 0) in the hello's session: send the state, then carry on as connected.
                if ((flags & Ack) == 0 || session != clientSession || ackId != 0) return;
                established = true;
                lastPing = Environment.TickCount64;
                var number = 0;
                foreach (var chunk in StateCommands().Chunk(2))
                    SendReliable([.. chunk.SelectMany(c => c)], lose: ++number == LoseStatePacket);
                return;
            }
            if (session != SwitcherSession)
            {
                // A late packet from the hello's session is harmless; any other session is the client's mistake.
                if (session != clientSession) Interlocked.Increment(ref WrongSession);
                return;
            }
            if ((flags & Ack) != 0) TakeAck(ackId);
            if ((flags & AckRequest) != 0) TakeCommands(packet, flags, packetId, from);
        }
    }

    /// <summary>The client has our packets up to <paramref name="ackId"/>. Called with the lock held.</summary>
    private void TakeAck(ushort ackId)
    {
        if (LoseClientAcks > 0)
        {
            LoseClientAcks--;
            return;
        }
        LastClientAck = ackId;
        // An ack covers every packet up to it, counting round the wrap at 32768.
        bool Covered(ushort id) => (ackId - id + 32768) % 32768 < 16384;
        if (gap is { } missing && Covered(missing)) AckedPastAGap = true;
        unacked.RemoveAll(p => Covered(p.Id));
    }

    /// <summary>A numbered packet from the client: ack it (or not, as the knobs say) and keep its commands. Called with the lock held.</summary>
    private void TakeCommands(byte[] packet, int flags, ushort packetId, EndPoint from)
    {
        if ((flags & Resend) != 0) Interlocked.Increment(ref Retransmits);
        if (packet.Length > 12 && DropCommandPackets > 0)
        {
            DropCommandPackets--;
            return;
        }
        if (packet.Length > 12 && AskAgainForNextCommand)
        {
            // "Send me everything again from packet N": flag 0x08, N in bytes 6-7, no ack.
            AskAgainForNextCommand = false;
            Interlocked.Increment(ref RetransmitRequests);
            var request = new byte[12];
            BinaryPrimitives.WriteUInt16BigEndian(request, (ushort)((RetransmitRequest << 11) | 12));
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), SwitcherSession);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(6), packetId);
            socket.SendTo(request, from);
            return;
        }
        if (seen.Add(packetId))
            foreach (var command in Split(packet.AsSpan(12).ToArray())) Commands.Enqueue(command);
        var ack = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(ack, (ushort)((Ack << 11) | 12));
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(2), SwitcherSession);
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(4), packetId);
        socket.SendTo(ack, from);
    }

    /// <summary>Sends a numbered packet and keeps it until the client acks it. Called with the lock held.</summary>
    private void SendReliable(byte[] body, bool lose = false)
    {
        if (client is null) return;
        var id = nextId;
        nextId = (ushort)((nextId + 1) % 32768);
        var packet = new byte[12 + body.Length];
        BinaryPrimitives.WriteUInt16BigEndian(packet, (ushort)((AckRequest << 11) | packet.Length));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), SwitcherSession);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10), id);
        body.CopyTo(packet, 12);
        unacked.Add(new Unacked(id, packet, Environment.TickCount64));
        if (lose)
        {
            // Lost on the way: the client gets the ones after it first, and this one when it's sent again.
            gap = id;
            return;
        }
        socket.SendTo(packet, client);
    }

    private IEnumerable<byte[]> StateCommands()
    {
        yield return Command("_ver", [0, 2, 0, 30]);
        var name = new byte[44];
        Encoding.ASCII.GetBytes("ATEM Test Switcher").CopyTo(name, 0);
        yield return Command("_pin", name);
        for (var i = 0; i < InputNames.Length; i++)
        {
            var input = new byte[36];
            BinaryPrimitives.WriteUInt16BigEndian(input, (ushort)(i + 1));
            Encoding.UTF8.GetBytes(InputNames[i]).CopyTo(input, 2);
            Encoding.UTF8.GetBytes($"CAM{i + 1}").CopyTo(input, 22);
            yield return Command("InPr", input);
        }
        var macroName = Encoding.UTF8.GetBytes("Walk-in");
        var macro = new byte[8 + macroName.Length];
        BinaryPrimitives.WriteUInt16BigEndian(macro, 3);
        macro[2] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(macro.AsSpan(4), (ushort)macroName.Length);
        macroName.CopyTo(macro, 8);
        yield return Command("MPrp", macro);
        yield return Command("PrgI", [0, 0, 0, 1]);
        yield return Command("PrvI", [0, 0, 0, 2, 0, 0, 0, 0]);
        yield return Command("InCm", [1, 0, 0, 0]);
    }

    private static byte[] Command(string name, byte[] data)
    {
        var command = new byte[8 + data.Length];
        BinaryPrimitives.WriteUInt16BigEndian(command, (ushort)command.Length);
        Encoding.ASCII.GetBytes(name).CopyTo(command, 4);
        data.CopyTo(command, 8);
        return command;
    }

    private static IEnumerable<(string, byte[])> Split(byte[] body)
    {
        var at = 0;
        while (at + 8 <= body.Length)
        {
            var length = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(at));
            if (length < 8 || at + length > body.Length) yield break;
            yield return (Encoding.ASCII.GetString(body, at + 4, 4), body[(at + 8)..(at + length)]);
            at += length;
        }
    }

    /// <summary>Waits until a command with this name has arrived (or the time is up).</summary>
    public (string Name, byte[] Data)? WaitFor(string name, int milliseconds = 3000)
    {
        var until = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < until)
        {
            var found = Commands.FirstOrDefault(c => c.Name == name);
            if (found.Name is not null) return found;
            Thread.Sleep(10);
        }
        return null;
    }

    public void Dispose()
    {
        stopped = true;
        socket.Dispose();
    }
}
