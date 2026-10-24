// Ported from HaishinKit (BSD 3-Clause, Copyright (c) 2015, shogo4405); see LICENSE-HaishinKit.txt.
namespace MxU.Slides.Broadcast.Hls.Vendored;

/// <summary>
/// One 188-byte MPEG transport-stream packet: a 4-byte header (sync byte 0x47, the packet id
/// saying which stream it belongs to, a 4-bit continuity counter), an optional adaptation field
/// (timing and padding) and payload. Port of TSPacket.swift (the writing half; Windows never
/// reads TS back).
/// </summary>
internal sealed class TsPacket
{
    public const int Size = 188;
    public const int HeaderSize = 4;
    public const byte SyncByte = 0x47;

    public bool PayloadUnitStartIndicator;
    public ushort Pid;
    public bool AdaptationFieldFlag;
    public bool PayloadFlag;
    public byte ContinuityCounter;
    public TsAdaptationField? AdaptationField;
    private readonly ByteArray payload = new(Size);

    /// <summary>Bytes still free in this packet once the header and adaptation field are counted.</summary>
    private int Remain
    {
        get
        {
            var adaptationFieldSize = 0;
            if (AdaptationField is { } adaptation && AdaptationFieldFlag)
            {
                adaptation.Compute();
                adaptationFieldSize = adaptation.Length + 1;
            }
            return Size - HeaderSize - adaptationFieldSize - payload.Length;
        }
    }

    /// <summary>
    /// Copies as much of <paramref name="data"/> as fits and returns how many bytes it took.
    /// The rest of the packet is padded: with adaptation-field stuffing for PES packets, or
    /// with 0xFF payload bytes for tables (PAT/PMT).
    /// </summary>
    public int Fill(ReadOnlySpan<byte> data, bool useAdaptationField)
    {
        PayloadFlag = true;
        var length = Math.Min(Math.Min(data.Length, Remain), 183);
        payload.WriteBytes(data[..length]);
        if (Remain == 0) return length;
        if (useAdaptationField)
        {
            AdaptationFieldFlag = true;
            if (length == 183) return length;
            AdaptationField ??= new TsAdaptationField();
            AdaptationField.Stuffing(Remain);
            AdaptationField.Compute();
            return length;
        }
        for (var i = Remain; i > 0; i--) payload.WriteUInt8(0xff);
        return length;
    }

    /// <summary>Copies a full 184-byte middle slice in (no adaptation field).</summary>
    public void SetPayload(ReadOnlySpan<byte> data)
    {
        PayloadFlag = true;
        payload.WriteBytes(data);
    }

    public void WriteTo(ByteArray output)
    {
        byte b1 = (byte)((PayloadUnitStartIndicator ? 0x40 : 0) | (Pid >> 8));
        byte b2 = (byte)(Pid & 0x00FF);
        byte b3 = (byte)((AdaptationFieldFlag ? 0x20 : 0) | (PayloadFlag ? 0x10 : 0) | ContinuityCounter);
        output.WriteUInt8(SyncByte).WriteUInt8(b1).WriteUInt8(b2).WriteUInt8(b3);
        if (AdaptationFieldFlag)
        {
            if (AdaptationField is { } field) field.WriteTo(output);
            else output.WriteUInt8(0);
        }
        output.WriteBytes(payload.Span);
    }
}

/// <summary>Presentation/decode timestamps: 33 bits at 90 kHz, spread over 5 bytes with marker bits.</summary>
internal static class TsTimestamp
{
    public const double Resolution = 90 * 1000;
    public const int DataSize = 5;

    public static byte[] Encode(long b, byte m)
    {
        var data = new byte[DataSize];
        data[0] = (byte)((byte)(b >> 29) | 0x01 | m);
        data[1] = (byte)(b >> 22);
        data[2] = (byte)((byte)(b >> 14) | 0x01);
        data[3] = (byte)(b >> 7);
        data[4] = (byte)((byte)(b << 1) | 0x01);
        return data;
    }
}

/// <summary>The program clock reference: the 27 MHz clock players lock to, as a 33-bit base and 9-bit extension.</summary>
internal static class TsProgramClockReference
{
    public static byte[] Encode(ulong b, ushort e)
    {
        var data = new byte[6];
        data[0] = (byte)(b >> 25);
        data[1] = (byte)(b >> 17);
        data[2] = (byte)(b >> 9);
        data[3] = (byte)(b >> 1);
        data[4] = 0xff;
        if ((b & 1) == 1) data[4] |= 0x80;
        else data[4] &= 0x7f;
        // HaishinKit's original test here is always false, so the low bit is always cleared.
        data[4] &= 0xfe;
        data[5] = (byte)e;
        return data;
    }
}
