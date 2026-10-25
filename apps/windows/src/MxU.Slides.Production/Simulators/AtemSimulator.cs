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
/// It's written straight from the protocol notes, not from the app's own packet code, so
/// a mistake in one shows up as a failed test rather than cancelling out. The unit tests and
/// the app's self-test drive the real code against it; nothing in a show uses it.
/// </summary>
public sealed class AtemSimulator : IDisposable
{
    public const ushort SwitcherSession = 0x8001;

    private readonly Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly Thread thread;
    private readonly Lock gate = new();
    private volatile bool stopped;
    private EndPoint? client;
    private ushort clientSession;
    private ushort nextId = 1;
    private bool established;
    private long lastPing;
    private readonly HashSet<ushort> seen = [];

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

    public int Hellos;
    public int Retransmits;

    /// <summary>Ignore (don't ack) this many command packets, to make the client send them again.</summary>
    public int DropCommandPackets;

    /// <summary>Say nothing at all (a switcher that's switched off).</summary>
    public volatile bool Silent;

    private void Run()
    {
        var buffer = new byte[2048];
        while (!stopped)
        {
            try
            {
                if (established && !Silent && Environment.TickCount64 - lastPing > 400)
                {
                    lastPing = Environment.TickCount64;
                    lock (gate) SendReliable([]);
                }
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

    private void Handle(byte[] packet, EndPoint from)
    {
        if (packet.Length < 12) return;
        var flags = packet[0] >> 3;
        var session = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2));
        var ackId = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4));
        var packetId = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(10));
        lock (gate)
        {
            if ((flags & 0x02) != 0)
            {
                // Hello: answer with the same session, "accepted" (0x02) in the body.
                Interlocked.Increment(ref Hellos);
                client = from;
                clientSession = session;
                established = false;
                nextId = 1;
                seen.Clear();
                var reply = new byte[20];
                BinaryPrimitives.WriteUInt16BigEndian(reply, (ushort)((0x02 << 11) | 20));
                BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(2), clientSession);
                reply[12] = 0x02;
                socket.SendTo(reply, from);
                return;
            }
            if ((flags & 0x10) != 0 && !established && session == clientSession && ackId == 0)
            {
                // The client acked our hello: send the state, then carry on as connected.
                established = true;
                foreach (var chunk in StateCommands().Chunk(2)) SendReliable([.. chunk.SelectMany(c => c)]);
                return;
            }
            if ((flags & 0x01) != 0 && established)
            {
                if ((flags & 0x04) != 0) Interlocked.Increment(ref Retransmits);
                if (packet.Length > 12 && DropCommandPackets > 0)
                {
                    DropCommandPackets--;
                    return;
                }
                if (seen.Add(packetId))
                    foreach (var command in Split(packet.AsSpan(12).ToArray())) Commands.Enqueue(command);
                var ack = new byte[12];
                BinaryPrimitives.WriteUInt16BigEndian(ack, (ushort)((0x10 << 11) | 12));
                BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(2), SwitcherSession);
                BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(4), packetId);
                socket.SendTo(ack, from);
            }
        }
    }

    private void SendReliable(byte[] body)
    {
        if (client is null) return;
        var packet = new byte[12 + body.Length];
        BinaryPrimitives.WriteUInt16BigEndian(packet, (ushort)((0x01 << 11) | packet.Length));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), SwitcherSession);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10), nextId++);
        body.CopyTo(packet, 12);
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
            if (length < 8) yield break;
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
