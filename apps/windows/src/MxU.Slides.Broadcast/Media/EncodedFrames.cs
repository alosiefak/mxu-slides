using System.Buffers.Binary;
using MxU.Slides.Broadcast.Hls.Vendored;

namespace MxU.Slides.Broadcast.Media;

/// <summary>
/// An H.264 stream's sequence and picture parameter sets (SPS and PPS): the profile, size and
/// coding settings a decoder needs before the first picture. Compared by content, so a new
/// set is noticed only when the encoder really changed them.
/// </summary>
internal sealed class AvcParameterSets(byte[] sps, byte[] pps) : IEquatable<AvcParameterSets>
{
    public byte[] Sps { get; } = sps;

    public byte[] Pps { get; } = pps;

    public IEnumerable<byte[]> All => [Sps, Pps];

    /// <summary>
    /// The AVCDecoderConfigurationRecord (ISO 14496-15) that RTMP sends as the AVC sequence
    /// header and MP4 stores as "avcC": version 1, profile, compatibility and level copied from
    /// the SPS, 4-byte NAL lengths, then one SPS and one PPS. Port of HaishinKit's
    /// AVCDecoderConfigurationRecord.
    /// </summary>
    public byte[] DecoderConfigurationRecord()
    {
        var buffer = new ByteArray()
            .WriteUInt8(1)
            .WriteUInt8(Sps.Length > 1 ? Sps[1] : (byte)0)
            .WriteUInt8(Sps.Length > 2 ? Sps[2] : (byte)0)
            .WriteUInt8(Sps.Length > 3 ? Sps[3] : (byte)0)
            .WriteUInt8(0xFC | 3)
            .WriteUInt8(0xE0 | 1)
            .WriteUInt16((ushort)Sps.Length)
            .WriteBytes(Sps)
            .WriteUInt8(1)
            .WriteUInt16((ushort)Pps.Length)
            .WriteBytes(Pps);
        return buffer.ToArray();
    }

    public bool Equals(AvcParameterSets? other) =>
        other is not null && Sps.AsSpan().SequenceEqual(other.Sps) && Pps.AsSpan().SequenceEqual(other.Pps);

    public override bool Equals(object? obj) => Equals(obj as AvcParameterSets);

    public override int GetHashCode() => HashCode.Combine(Sps.Length, Pps.Length, Sps.Length > 3 ? Sps[3] : 0);
}

/// <summary>
/// One encoded picture. <see cref="Avcc"/> holds its NAL units each prefixed with a 4-byte
/// big-endian length (the "AVCC" layout RTMP and MP4 use, and what the Mac's VideoToolbox
/// produces), without the parameter sets, which travel in <see cref="ParameterSets"/>.
/// Times are seconds on the host clock.
/// </summary>
internal sealed class EncodedVideoFrame
{
    public required byte[] Avcc { get; init; }

    public required bool IsKeyframe { get; init; }

    public required double PresentationTime { get; init; }

    /// <summary>Null when it equals the presentation time (no B-frames, which is always the case here).</summary>
    public double? DecodeTime { get; init; }

    public required AvcParameterSets ParameterSets { get; init; }
}

/// <summary>One raw AAC frame (1024 samples per channel) and when its first sample plays, on the host clock.</summary>
internal sealed class EncodedAudioFrame
{
    public required byte[] Data { get; init; }

    public required double PresentationTime { get; init; }

    public required AudioSpecificConfig Config { get; init; }
}

/// <summary>Converts between the two ways H.264 NAL units are framed: Annex B start codes and AVCC lengths.</summary>
internal static class AvcBitstream
{
    public const byte NalSps = 7;
    public const byte NalPps = 8;
    public const byte NalAccessUnitDelimiter = 9;
    public const byte NalIdr = 5;

    /// <summary>Splits an Annex B byte stream (00 00 01 or 00 00 00 01 before each unit) into NAL units.</summary>
    public static List<ReadOnlyMemory<byte>> SplitAnnexB(ReadOnlyMemory<byte> stream)
    {
        var units = new List<ReadOnlyMemory<byte>>();
        var span = stream.Span;
        var start = -1;
        var i = 0;
        while (i + 2 < span.Length)
        {
            if (span[i] == 0 && span[i + 1] == 0 && span[i + 2] == 1)
            {
                if (start >= 0) units.Add(Trim(stream[start..i]));
                i += 3;
                start = i;
                continue;
            }
            i++;
        }
        if (start >= 0 && start < span.Length) units.Add(Trim(stream[start..]));
        else if (start < 0 && span.Length > 0) units.Add(stream);
        units.RemoveAll(u => u.Length == 0);
        return units;
    }

    /// <summary>Drops the trailing zero that belongs to the next 4-byte start code.</summary>
    private static ReadOnlyMemory<byte> Trim(ReadOnlyMemory<byte> unit)
    {
        var end = unit.Length;
        while (end > 0 && unit.Span[end - 1] == 0) end--;
        return unit[..end];
    }

    public static byte TypeOf(ReadOnlySpan<byte> nal) => nal.Length == 0 ? (byte)0 : (byte)(nal[0] & 0x1F);

    /// <summary>Joins NAL units with 4-byte big-endian length prefixes.</summary>
    public static byte[] ToAvcc(IEnumerable<ReadOnlyMemory<byte>> nals)
    {
        var list = nals.ToList();
        var output = new byte[list.Sum(n => n.Length + 4)];
        var offset = 0;
        foreach (var nal in list)
        {
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(offset), (uint)nal.Length);
            nal.Span.CopyTo(output.AsSpan(offset + 4));
            offset += nal.Length + 4;
        }
        return output;
    }

    /// <summary>Reads the NAL units back out of AVCC framing.</summary>
    public static List<ReadOnlyMemory<byte>> SplitAvcc(ReadOnlyMemory<byte> avcc)
    {
        var units = new List<ReadOnlyMemory<byte>>();
        var offset = 0;
        while (offset + 4 <= avcc.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(avcc.Span[offset..]);
            if (length < 0 || offset + 4 + length > avcc.Length) break;
            units.Add(avcc.Slice(offset + 4, length));
            offset += 4 + length;
        }
        return units;
    }
}
