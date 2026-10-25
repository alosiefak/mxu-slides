using System.Buffers.Binary;
using System.Text;

namespace MxU.Slides.Production.Atem;

/// <summary>The flags in the first five bits of every ATEM packet.</summary>
[Flags]
public enum AtemFlags : byte
{
    None = 0,

    /// <summary>"Please confirm you got this": the packet carries a packet id and must be acked.</summary>
    AckRequest = 0x01,

    /// <summary>The hello that opens a session (and the switcher's answer to it).</summary>
    Hello = 0x02,

    /// <summary>
    /// This is a resend of a packet sent before. The switcher marks its own resends this way; a
    /// client's resends go out unchanged, as atem-connection sends them.
    /// </summary>
    Retransmit = 0x04,

    /// <summary>"Send me again everything from packet N" (N is in the header).</summary>
    RetransmitRequest = 0x08,

    /// <summary>"I got your packets up to N" (N is in the header).</summary>
    Ack = 0x10,
}

/// <summary>
/// The 12 bytes at the front of every ATEM packet. Blackmagic never published the protocol;
/// this is the layout worked out by the open-source atem-connection (Sofie, NRK) and libqatem
/// projects:
/// <code>
/// bytes 0-1   flags (top 5 bits) and the packet's whole length (low 11 bits)
/// bytes 2-3   session id (the switcher picks it; we copy it from every packet we get)
/// bytes 4-5   ack id: the last of the other side's packets this one acknowledges
/// bytes 6-7   with RetransmitRequest: the first packet id to send again
/// bytes 8-9   unknown (0, but 0x003A in the hello)
/// bytes 10-11 this packet's id, when it asks to be acked
/// </code>
/// All numbers are big-endian. Packet ids count up and wrap at 32768.
/// </summary>
public readonly record struct AtemHeader(AtemFlags Flags, int Length, ushort Session, ushort AckId, ushort RetransmitFrom, ushort PacketId)
{
    public const int Size = 12;

    /// <summary>Packet ids wrap at this (they are 15 bits).</summary>
    public const int PacketIdModulo = 1 << 15;

    public static AtemHeader Read(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < Size) throw new ArgumentException("An ATEM packet is at least 12 bytes", nameof(packet));
        var first = BinaryPrimitives.ReadUInt16BigEndian(packet);
        return new AtemHeader(
            (AtemFlags)(first >> 11),
            first & 0x07FF,
            BinaryPrimitives.ReadUInt16BigEndian(packet[2..]),
            BinaryPrimitives.ReadUInt16BigEndian(packet[4..]),
            BinaryPrimitives.ReadUInt16BigEndian(packet[6..]),
            BinaryPrimitives.ReadUInt16BigEndian(packet[10..]));
    }

    public void Write(Span<byte> packet)
    {
        BinaryPrimitives.WriteUInt16BigEndian(packet, (ushort)(((int)Flags << 11) | (Length & 0x07FF)));
        BinaryPrimitives.WriteUInt16BigEndian(packet[2..], Session);
        BinaryPrimitives.WriteUInt16BigEndian(packet[4..], AckId);
        BinaryPrimitives.WriteUInt16BigEndian(packet[6..], RetransmitFrom);
        BinaryPrimitives.WriteUInt16BigEndian(packet[8..], 0);
        BinaryPrimitives.WriteUInt16BigEndian(packet[10..], PacketId);
    }
}

/// <summary>Building and reading ATEM packets. Pure functions, so every byte can be tested.</summary>
public static class AtemPacket
{
    /// <summary>The session id a client offers in its hello (the switcher answers with the one to use).</summary>
    public const ushort ClientSession = 0x53AB;

    /// <summary>
    /// The 20-byte hello that opens a session: the Hello flag, our session id and an 8-byte body
    /// starting 0x01 ("connect"). Byte-for-byte what atem-connection sends.
    /// </summary>
    public static byte[] Hello(ushort session = ClientSession)
    {
        var packet = new byte[20];
        new AtemHeader(AtemFlags.Hello, packet.Length, session, 0, 0, 0).Write(packet);
        packet[9] = 0x3A;
        packet[12] = 0x01;
        return packet;
    }

    /// <summary>An ack: "I have your packets up to <paramref name="ackId"/>".</summary>
    public static byte[] Ack(ushort session, ushort ackId)
    {
        var packet = new byte[AtemHeader.Size];
        new AtemHeader(AtemFlags.Ack, packet.Length, session, ackId, 0, 0).Write(packet);
        return packet;
    }

    /// <summary>
    /// A packet that must be acked, carrying commands (or none, as a keepalive ping). A resend
    /// is this same packet again, byte for byte: the packet id is what tells the switcher it has
    /// had it before.
    /// </summary>
    public static byte[] Reliable(ushort session, ushort packetId, ReadOnlySpan<byte> payload)
    {
        var packet = new byte[AtemHeader.Size + payload.Length];
        new AtemHeader(AtemFlags.AckRequest, packet.Length, session, 0, 0, packetId).Write(packet);
        payload.CopyTo(packet.AsSpan(AtemHeader.Size));
        return packet;
    }

    /// <summary>
    /// One command inside a packet's body: its length (8 + data, big-endian), two zero bytes, its
    /// four-letter name ("CPgI"), then its data.
    /// </summary>
    public static byte[] Command(string name, ReadOnlySpan<byte> data)
    {
        if (name.Length != 4) throw new ArgumentException("ATEM command names are four letters", nameof(name));
        var command = new byte[8 + data.Length];
        BinaryPrimitives.WriteUInt16BigEndian(command, (ushort)command.Length);
        Encoding.ASCII.GetBytes(name, command.AsSpan(4, 4));
        data.CopyTo(command.AsSpan(8));
        return command;
    }

    /// <summary>The commands in a packet's body, as (name, data). A malformed one ends the list.</summary>
    public static IEnumerable<(string Name, byte[] Data)> Commands(ReadOnlyMemory<byte> body)
    {
        var offset = 0;
        while (offset + 8 <= body.Length)
        {
            var span = body.Span[offset..];
            var length = BinaryPrimitives.ReadUInt16BigEndian(span);
            if (length < 8 || offset + length > body.Length) yield break;
            var name = Encoding.ASCII.GetString(span.Slice(4, 4));
            yield return (name, span[8..length].ToArray());
            offset += length;
        }
    }

    /// <summary>The packet id after <paramref name="id"/> (they wrap at 32768).</summary>
    public static ushort Next(ushort id) => (ushort)((id + 1) % AtemHeader.PacketIdModulo);

    /// <summary>
    /// Whether an ack of <paramref name="ackId"/> covers packet <paramref name="packetId"/>: it does
    /// when the packet is at or before the ack, counting round the wrap (within half the range).
    /// </summary>
    public static bool Covers(ushort ackId, ushort packetId)
    {
        var behind = (ackId - packetId + AtemHeader.PacketIdModulo) % AtemHeader.PacketIdModulo;
        return behind < AtemHeader.PacketIdModulo / 2;
    }
}
