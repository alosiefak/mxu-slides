// Ported from HaishinKit (BSD 3-Clause, Copyright (c) 2015, shogo4405); see LICENSE-HaishinKit.txt.
namespace MxU.Slides.Broadcast.Hls.Vendored;

/// <summary>
/// The optional part of a TS packet between header and payload. Here it carries the program
/// clock reference, the random-access (keyframe) flag, and the 0xFF stuffing that pads a
/// short last packet to 188 bytes. Port of TSAdaptationField in TSField.swift (writing only;
/// the adaptation extension field is never written and is left out).
/// </summary>
internal sealed class TsAdaptationField
{
    public const int PcrSize = 6;
    private const int FixedSectionSize = 2;

    /// <summary>Bytes after the length byte itself.</summary>
    public byte Length;
    public bool DiscontinuityIndicator;
    public bool RandomAccessIndicator;
    public bool PcrFlag;
    public byte[] Pcr = [];
    private byte[] stuffingBytes = [];

    public void Compute()
    {
        Length = (byte)(FixedSectionSize + Pcr.Length + stuffingBytes.Length - 1);
    }

    public void Stuffing(int size)
    {
        stuffingBytes = new byte[size];
        Array.Fill(stuffingBytes, (byte)0xff);
        Length += (byte)size;
    }

    public void WriteTo(ByteArray output)
    {
        byte flags = 0;
        flags |= (byte)(DiscontinuityIndicator ? 0x80 : 0);
        flags |= (byte)(RandomAccessIndicator ? 0x40 : 0);
        flags |= (byte)(PcrFlag ? 0x10 : 0);
        output.WriteUInt8(Length).WriteUInt8(flags);
        if (PcrFlag) output.WriteBytes(Pcr);
        output.WriteBytes(stuffingBytes);
    }
}
