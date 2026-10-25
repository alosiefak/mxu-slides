using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace MxU.Slides.Production.Dmx;

/// <summary>
/// Art-Net's ArtDmx packet (Art-Net 4, Artistic Licence): one universe of DMX levels over
/// UDP port 6454. Layout:
/// <code>
/// 0-7    "Art-Net" and a zero byte
/// 8-9    OpCode 0x5000 (ArtDmx), low byte first, the one little-endian field
/// 10-11  protocol version 14, high byte first
/// 12     sequence 1-255, so a node can put packets back in order (0 turns that off)
/// 13     physical input port (information only: 0)
/// 14     SubUni: the low 8 bits of the 15-bit port-address (sub-net and universe)
/// 15     Net: the top 7 bits of the port-address
/// 16-17  how many levels follow, high byte first: even, from 2 to 512
/// 18-    the levels, channel 1 first
/// </code>
/// </summary>
public static class ArtNetPacket
{
    public const int Port = 6454;
    public const ushort OpDmx = 0x5000;
    public const int ProtocolVersion = 14;

    private static readonly byte[] Id = "Art-Net\0"u8.ToArray();

    /// <summary>
    /// The Art-Net port-address for one of the show's universes. Show universe 1 is Art-Net
    /// universe 0 (Net 0, Sub-Net 0, Universe 0), as most lighting software numbers them.
    /// </summary>
    public static int PortAddress(int showUniverse) => Math.Clamp(showUniverse - 1, 0, 0x7FFF);

    public static byte[] Dmx(int portAddress, byte sequence, ReadOnlySpan<byte> levels)
    {
        // The spec asks for an even length of at least 2.
        var length = Math.Clamp(levels.Length + (levels.Length & 1), 2, 512);
        var packet = new byte[18 + length];
        Id.CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(8), OpDmx);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10), ProtocolVersion);
        packet[12] = sequence;
        packet[13] = 0;
        packet[14] = (byte)(portAddress & 0xFF);
        packet[15] = (byte)((portAddress >> 8) & 0x7F);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(16), (ushort)length);
        levels[..Math.Min(levels.Length, 512)].CopyTo(packet.AsSpan(18));
        return packet;
    }

    /// <summary>The next sequence number: 1 to 255 and round again (0 means "no sequence", so it's skipped).</summary>
    public static byte NextSequence(byte sequence) => sequence >= 255 ? (byte)1 : (byte)(sequence + 1);
}

/// <summary>
/// sACN (ANSI E1.31-2018, "Streaming ACN"): one universe of DMX levels over UDP port 5568, to
/// the universe's multicast group 239.255.hi.lo or straight to a node. A data packet is 638
/// bytes for a full universe, in three nested layers, each with a "flags and length" field
/// (0x7 in the top four bits, the layer's length from there to the end in the low twelve):
/// <code>
/// Root layer     0-1 preamble size 0x0010, 2-3 post-amble 0, 4-15 "ASC-E1.17\0\0\0",
///                16-17 flags+length, 18-21 vector 4 (E1.31 data), 22-37 CID (the source's UUID)
/// Framing layer  38-39 flags+length, 40-43 vector 2 (DMX data), 44-107 source name (UTF-8, zero padded),
///                108 priority (0-200), 109-110 sync address (0), 111 sequence, 112 options, 113-114 universe
/// DMP layer      115-116 flags+length, 117 vector 2 (set property), 118 address and data type 0xA1,
///                119-120 first address 0, 121-122 increment 1, 123-124 count (513), 125 start code 0,
///                126-637 the 512 levels, channel 1 first
/// </code>
/// All numbers are big-endian.
/// </summary>
public static class SacnPacket
{
    public const int Port = 5568;
    public const int Length = 638;

    private static readonly byte[] AcnId = "ASC-E1.17\0\0\0"u8.ToArray();

    /// <summary>Bit 6 of the options: "this source is going away" (sent three times when stopping).</summary>
    public const byte StreamTerminated = 0x40;

    /// <summary>The multicast group a universe is sent to: 239.255.(universe ÷ 256).(universe mod 256).</summary>
    public static IPAddress MulticastGroup(int universe) =>
        new([239, 255, (byte)((universe >> 8) & 0xFF), (byte)(universe & 0xFF)]);

    /// <summary>sACN numbers universes from 1 to 63999, so show universe N is sACN universe N.</summary>
    public static int Universe(int showUniverse) => Math.Clamp(showUniverse, 1, 63999);

    public static byte[] Data(Guid cid, string sourceName, byte priority, int universe, byte sequence, ReadOnlySpan<byte> levels, byte options = 0)
    {
        var packet = new byte[Length];
        var span = packet.AsSpan();
        // Root layer
        BinaryPrimitives.WriteUInt16BigEndian(span, 0x0010);
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], 0x0000);
        AcnId.CopyTo(span[4..]);
        FlagsAndLength(span[16..], Length - 16);
        BinaryPrimitives.WriteUInt32BigEndian(span[18..], 0x00000004);
        cid.TryWriteBytes(span.Slice(22, 16), bigEndian: true, out _);
        // Framing layer
        FlagsAndLength(span[38..], Length - 38);
        BinaryPrimitives.WriteUInt32BigEndian(span[40..], 0x00000002);
        var name = Encoding.UTF8.GetBytes(sourceName);
        name.AsSpan(0, Math.Min(name.Length, 63)).CopyTo(span[44..]);
        packet[108] = Math.Min(priority, (byte)200);
        BinaryPrimitives.WriteUInt16BigEndian(span[109..], 0);
        packet[111] = sequence;
        packet[112] = options;
        BinaryPrimitives.WriteUInt16BigEndian(span[113..], (ushort)universe);
        // DMP layer
        FlagsAndLength(span[115..], Length - 115);
        packet[117] = 0x02;
        packet[118] = 0xA1;
        BinaryPrimitives.WriteUInt16BigEndian(span[119..], 0x0000);
        BinaryPrimitives.WriteUInt16BigEndian(span[121..], 0x0001);
        BinaryPrimitives.WriteUInt16BigEndian(span[123..], 513);
        packet[125] = 0x00;
        levels[..Math.Min(levels.Length, 512)].CopyTo(span[126..]);
        return packet;
    }

    private static void FlagsAndLength(Span<byte> at, int length) =>
        BinaryPrimitives.WriteUInt16BigEndian(at, (ushort)(0x7000 | (length & 0x0FFF)));
}
