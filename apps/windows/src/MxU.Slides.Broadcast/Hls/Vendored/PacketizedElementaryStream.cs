// Ported from HaishinKit (BSD 3-Clause, Copyright (c) 2015, shogo4405); see LICENSE-HaishinKit.txt.
using MxU.Slides.Broadcast.Media;

namespace MxU.Slides.Broadcast.Hls.Vendored;

/// <summary>
/// The optional PES header: flags plus the presentation (and decode) timestamp. PTS is
/// pushed 0.1 s later than the stream's first sample so the player's clock (PCR, which starts
/// at 0) runs a little ahead of what it must show. Port of PESOptionalHeader.
/// </summary>
internal sealed class PesOptionalHeader
{
    private const byte DefaultMarkerBits = 2;
    private const double Offset = 3.0 / 30.0;

    public bool DataAlignmentIndicator;
    private byte ptsDtsIndicator;
    private byte pesHeaderLength;
    private readonly ByteArray optionalFields = new();

    /// <param name="timestamp">The stream's first timestamp: PTS and DTS are written relative to it.</param>
    public void SetTimestamp(double timestamp, double? presentationTimeStamp, double? decodeTimeStamp)
    {
        if (presentationTimeStamp is not null) ptsDtsIndicator |= 0x02;
        if (decodeTimeStamp is not null) ptsDtsIndicator |= 0x01;
        if ((ptsDtsIndicator & 0x02) == 0x02)
        {
            var pts = (long)((presentationTimeStamp!.Value + Offset - timestamp) * TsTimestamp.Resolution);
            optionalFields.WriteBytes(TsTimestamp.Encode(pts, (byte)(ptsDtsIndicator << 4)));
        }
        if ((ptsDtsIndicator & 0x01) == 0x01)
        {
            var dts = (long)((decodeTimeStamp!.Value - timestamp) * TsTimestamp.Resolution);
            optionalFields.WriteBytes(TsTimestamp.Encode(dts, 0x01 << 4));
        }
        pesHeaderLength = (byte)optionalFields.Length;
    }

    public int Length => 3 + optionalFields.Length;

    public void WriteTo(ByteArray output)
    {
        var b0 = (byte)(DefaultMarkerBits << 6 | (DataAlignmentIndicator ? 1 : 0) << 2);
        var b1 = (byte)(ptsDtsIndicator << 6);
        output.WriteUInt8(b0).WriteUInt8(b1).WriteUInt8(pesHeaderLength).WriteBytes(optionalFields.Span);
    }
}

/// <summary>
/// A PES packet: one whole video picture or audio frame with its timestamp, which the writer
/// then slices into 188-byte TS packets. Port of PacketizedElementaryStream.swift.
/// </summary>
internal sealed class PacketizedElementaryStream
{
    private static readonly byte[] StartCode = [0x00, 0x00, 0x01];
    private static readonly byte[] SyncAccessUnitDelimiter = [0x00, 0x00, 0x00, 0x01, 0x09, 0x10];
    private static readonly byte[] AccessUnitDelimiter = [0x00, 0x00, 0x00, 0x01, 0x09, 0x30];
    private static readonly byte[] NalStartCode = [0x00, 0x00, 0x00, 0x01];

    public byte StreamId;
    private ushort packetLength;
    private readonly PesOptionalHeader optionalPesHeader = new() { DataAlignmentIndicator = true };
    private readonly ByteArray data = new(1024);

    private PacketizedElementaryStream() { }

    /// <summary>
    /// A video picture as Annex B: an access-unit delimiter, the parameter sets in front of
    /// keyframes, then the picture's NAL units with start codes.
    /// </summary>
    public static PacketizedElementaryStream Video(EncodedVideoFrame frame, double timeStamp)
    {
        var pes = new PacketizedElementaryStream();
        if (frame.IsKeyframe)
        {
            pes.data.WriteBytes(SyncAccessUnitDelimiter);
            foreach (var set in frame.ParameterSets.All)
                pes.data.WriteBytes(NalStartCode).WriteBytes(set);
        }
        else
        {
            pes.data.WriteBytes(AccessUnitDelimiter);
        }
        pes.data.WriteBytes(IsoTypeBufferUtil.ToByteStream(frame.Avcc));
        pes.optionalPesHeader.SetTimestamp(timeStamp, frame.PresentationTime, frame.DecodeTime);
        var length = pes.data.Length + pes.optionalPesHeader.Length;
        // Video PES packets over 64 KB say "unbounded" (0), which the spec allows for video.
        pes.packetLength = length < ushort.MaxValue ? (ushort)length : (ushort)0;
        return pes;
    }

    /// <summary>An AAC frame with its ADTS header. Null when it doesn't fit a PES packet.</summary>
    public static PacketizedElementaryStream? Audio(EncodedAudioFrame frame, double timeStamp)
    {
        var pes = new PacketizedElementaryStream();
        pes.data.WriteBytes(frame.Config.Adts(frame.Data));
        pes.optionalPesHeader.SetTimestamp(timeStamp, frame.PresentationTime, null);
        var length = pes.data.Length + pes.optionalPesHeader.Length;
        if (length >= ushort.MaxValue) return null;
        pes.packetLength = (ushort)length;
        return pes;
    }

    private byte[] Payload()
    {
        var output = new ByteArray(data.Length + 32).WriteBytes(StartCode).WriteUInt8(StreamId).WriteUInt16(packetLength);
        optionalPesHeader.WriteTo(output);
        return output.WriteBytes(data.Span).ToArray();
    }

    /// <summary>
    /// Slices the packet into TS packets on <paramref name="pid"/>: the first starts the unit
    /// (and carries the clock reference when given), full 184-byte packets follow, and the last
    /// is padded with adaptation-field stuffing.
    /// </summary>
    public List<TsPacket> ArrayOfPackets(ushort pid, ulong? pcr)
    {
        var payload = Payload();
        var packets = new List<TsPacket>(payload.Length / 184 + 2);

        var first = new TsPacket { Pid = pid };
        if (pcr is { } clock)
        {
            first.AdaptationFieldFlag = true;
            first.AdaptationField = new TsAdaptationField { PcrFlag = true, Pcr = TsProgramClockReference.Encode(clock, 0) };
            first.AdaptationField.Compute();
        }
        first.PayloadUnitStartIndicator = true;
        var position = first.Fill(payload, useAdaptationField: true);
        packets.Add(first);

        var r = (payload.Length - position) % 184;
        for (var index = position; index < payload.Length - r; index += 184)
        {
            var packet = new TsPacket { Pid = pid };
            packet.SetPayload(payload.AsSpan(index, 184));
            packets.Add(packet);
        }

        if (r != 0)
        {
            var packet = new TsPacket
            {
                Pid = pid,
                AdaptationFieldFlag = true,
                AdaptationField = r == 183 ? null : new TsAdaptationField(),
            };
            packet.AdaptationField?.Compute();
            packet.Fill(payload.AsSpan(payload.Length - r), useAdaptationField: true);
            packets.Add(packet);
        }
        return packets;
    }
}

/// <summary>Port of ISOTypeBufferUtil.toByteStream: AVCC length prefixes become 4-byte start codes.</summary>
internal static class IsoTypeBufferUtil
{
    public static byte[] ToByteStream(ReadOnlySpan<byte> avcc)
    {
        var buffer = new ByteArray(avcc);
        var result = new ByteArray(avcc.Length + 16);
        while (buffer.BytesAvailable > 0)
        {
            try
            {
                var length = (int)buffer.ReadUInt32();
                result.WriteBytes([0x00, 0x00, 0x00, 0x01]).WriteBytes(buffer.ReadBytes(length));
            }
            catch (EndOfStreamException)
            {
                BroadcastLog.Error("hls-ts", "a video sample's NAL lengths run past its end");
                break;
            }
        }
        return result.ToArray();
    }
}
