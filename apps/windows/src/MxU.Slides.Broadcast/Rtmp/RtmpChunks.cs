using System.Buffers.Binary;

namespace MxU.Slides.Broadcast.Rtmp;

/// <summary>RTMP message types (the byte in every message header).</summary>
internal static class RtmpMessageType
{
    public const byte SetChunkSize = 1;
    public const byte Abort = 2;
    public const byte Acknowledgement = 3;
    public const byte UserControl = 4;
    public const byte WindowAcknowledgementSize = 5;
    public const byte SetPeerBandwidth = 6;
    public const byte Audio = 8;
    public const byte Video = 9;
    public const byte DataAmf3 = 15;
    public const byte CommandAmf3 = 17;
    public const byte DataAmf0 = 18;
    public const byte CommandAmf0 = 20;
}

/// <summary>The chunk streams this publisher uses, as HaishinKit numbers them.</summary>
internal static class RtmpChunkStreamId
{
    public const int Control = 2;
    public const int Command = 3;
    public const int Audio = 4;
    public const int Video = 5;
    public const int Data = 8;
}

/// <summary>One whole RTMP message: what the chunk stream carries once reassembled.</summary>
internal sealed record RtmpMessage(byte TypeId, uint StreamId, uint Timestamp, byte[] Payload)
{
    public static RtmpMessage Control(byte type, ReadOnlySpan<byte> payload) => new(type, 0, 0, payload.ToArray());

    public static RtmpMessage SetChunkSize(int size) => Control(RtmpMessageType.SetChunkSize, BigEndian((uint)size & 0x7FFFFFFF));

    public static RtmpMessage Acknowledgement(uint sequence) => Control(RtmpMessageType.Acknowledgement, BigEndian(sequence));

    public static RtmpMessage WindowAcknowledgementSize(uint size) => Control(RtmpMessageType.WindowAcknowledgementSize, BigEndian(size));

    public static RtmpMessage SetPeerBandwidth(uint size, byte limit) => Control(RtmpMessageType.SetPeerBandwidth, [.. BigEndian(size), limit]);

    /// <summary>A user-control event (stream begin = 0, ping request = 6, ping response = 7) with a 4-byte value.</summary>
    public static RtmpMessage UserControl(ushort eventType, uint value)
    {
        var payload = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(payload, eventType);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(2), value);
        return Control(RtmpMessageType.UserControl, payload);
    }

    public static RtmpMessage Command(uint streamId, params object?[] values) =>
        new(RtmpMessageType.CommandAmf0, streamId, 0, Amf0.Encode(values));

    public uint ReadUInt32(int offset = 0) =>
        Payload.Length >= offset + 4 ? BinaryPrimitives.ReadUInt32BigEndian(Payload.AsSpan(offset)) : 0;

    private static byte[] BigEndian(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }
}

/// <summary>
/// Splits messages into RTMP chunks. Each chunk stream remembers the last message it sent, so
/// the first message on a stream (or one whose stream id changes or whose time goes backwards)
/// gets a full type-0 header with an absolute timestamp, and later ones a 7-byte type-1 header
/// with the time since the previous one; the rest of a long message follows in type-3 chunks
/// of <see cref="ChunkSize"/> bytes. Timestamps past 24 bits use the 4-byte extended field,
/// repeated on type-3 chunks as librtmp and FFmpeg expect. Not thread-safe. Port of
/// HaishinKit's RTMPChunkBuffer.putMessage, with extended timestamps added.
/// </summary>
internal sealed class RtmpChunkWriter
{
    private const uint MaxTimestamp = 0xFFFFFF;
    private readonly Dictionary<int, (uint StreamId, uint Timestamp)> last = [];

    public int ChunkSize { get; set; } = 128;

    public void Write(Stream output, int chunkStreamId, RtmpMessage message)
    {
        var buffer = new MemoryStream(message.Payload.Length + 32);
        Append(buffer, chunkStreamId, message);
        buffer.Position = 0;
        buffer.CopyTo(output);
    }

    /// <summary>Appends the message's chunks to <paramref name="output"/>.</summary>
    public void Append(Stream output, int chunkStreamId, RtmpMessage message)
    {
        var fresh = !last.TryGetValue(chunkStreamId, out var previous)
            || previous.StreamId != message.StreamId
            || message.Timestamp < previous.Timestamp;
        var type = fresh ? 0 : 1;
        var timeField = fresh ? message.Timestamp : message.Timestamp - previous.Timestamp;
        last[chunkStreamId] = (message.StreamId, message.Timestamp);
        var extended = timeField >= MaxTimestamp;

        Span<byte> header = stackalloc byte[18];
        var payload = message.Payload;
        var offset = 0;
        var first = true;
        do
        {
            var length = 0;
            length += BasicHeader(header, first ? type : 3, chunkStreamId);
            if (first)
            {
                Write24(header[length..], extended ? MaxTimestamp : timeField);
                Write24(header[(length + 3)..], (uint)payload.Length);
                header[length + 6] = message.TypeId;
                length += 7;
                if (type == 0)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(header[length..], message.StreamId);
                    length += 4;
                }
            }
            if (extended)
            {
                BinaryPrimitives.WriteUInt32BigEndian(header[length..], timeField);
                length += 4;
            }
            output.Write(header[..length]);
            var count = Math.Min(ChunkSize, payload.Length - offset);
            output.Write(payload, offset, count);
            offset += count;
            first = false;
        }
        while (offset < payload.Length);
    }

    private static int BasicHeader(Span<byte> header, int type, int chunkStreamId)
    {
        if (chunkStreamId <= 63)
        {
            header[0] = (byte)(type << 6 | chunkStreamId);
            return 1;
        }
        if (chunkStreamId <= 319)
        {
            header[0] = (byte)(type << 6);
            header[1] = (byte)(chunkStreamId - 64);
            return 2;
        }
        header[0] = (byte)(type << 6 | 1);
        header[1] = (byte)((chunkStreamId - 64) & 0xFF);
        header[2] = (byte)((chunkStreamId - 64) >> 8);
        return 3;
    }

    private static void Write24(Span<byte> span, uint value)
    {
        span[0] = (byte)(value >> 16);
        span[1] = (byte)(value >> 8);
        span[2] = (byte)value;
    }
}

/// <summary>
/// Reassembles RTMP messages from the chunks a peer sends (any header type, any chunk size,
/// interleaved chunk streams). The caller must apply a received Set Chunk Size to
/// <see cref="ChunkSize"/> before reading on. Counts every byte read, for acknowledgements.
/// Not thread-safe. Port of HaishinKit's RTMPChunkBuffer/RTMPChunkMessageHeader reading side.
/// </summary>
internal sealed class RtmpChunkReader(Stream input)
{
    private const uint MaxTimestamp = 0xFFFFFF;

    private sealed class ChunkState
    {
        public uint Timestamp;
        public uint Delta;
        public int Length;
        public byte TypeId;
        public uint StreamId;
        public bool Extended;
        public byte[]? Payload;
        public int Received;
    }

    private readonly Dictionary<int, ChunkState> streams = [];
    private readonly byte[] scratch = new byte[16];

    public int ChunkSize { get; set; } = 128;

    public long BytesRead { get; private set; }

    public async Task<RtmpMessage> ReadMessageAsync(CancellationToken cancellation)
    {
        while (true)
        {
            var b0 = await ReadByteAsync(cancellation);
            var type = b0 >> 6;
            int chunkStreamId = b0 & 0x3F;
            if (chunkStreamId == 0) chunkStreamId = await ReadByteAsync(cancellation) + 64;
            else if (chunkStreamId == 1)
            {
                var low = await ReadByteAsync(cancellation);
                var high = await ReadByteAsync(cancellation);
                chunkStreamId = high * 256 + low + 64;
            }
            if (!streams.TryGetValue(chunkStreamId, out var state))
            {
                if (type != 0) throw new InvalidDataException($"RTMP chunk stream {chunkStreamId} began without a full header");
                streams[chunkStreamId] = state = new ChunkState();
            }

            var startsMessage = state.Payload is null;
            switch (type)
            {
                case 0:
                {
                    await ReadExactlyAsync(11, cancellation);
                    var time = Read24(0);
                    state.Length = (int)Read24(3);
                    state.TypeId = scratch[6];
                    state.StreamId = BinaryPrimitives.ReadUInt32LittleEndian(scratch.AsSpan(7));
                    state.Extended = time == MaxTimestamp;
                    if (state.Extended) time = await ReadUInt32Async(cancellation);
                    state.Timestamp = time;
                    state.Delta = 0;
                    startsMessage = true;
                    state.Payload = null;
                    break;
                }
                case 1:
                {
                    await ReadExactlyAsync(7, cancellation);
                    var delta = Read24(0);
                    state.Length = (int)Read24(3);
                    state.TypeId = scratch[6];
                    state.Extended = delta == MaxTimestamp;
                    if (state.Extended) delta = await ReadUInt32Async(cancellation);
                    state.Delta = delta;
                    state.Timestamp += delta;
                    startsMessage = true;
                    state.Payload = null;
                    break;
                }
                case 2:
                {
                    await ReadExactlyAsync(3, cancellation);
                    var delta = Read24(0);
                    state.Extended = delta == MaxTimestamp;
                    if (state.Extended) delta = await ReadUInt32Async(cancellation);
                    state.Delta = delta;
                    state.Timestamp += delta;
                    startsMessage = true;
                    state.Payload = null;
                    break;
                }
                default:
                    if (state.Extended) await ReadUInt32Async(cancellation);
                    if (startsMessage) state.Timestamp += state.Delta;
                    break;
            }

            if (startsMessage)
            {
                if (state.Length > 16 * 1024 * 1024) throw new InvalidDataException("RTMP message too large");
                state.Payload = new byte[state.Length];
                state.Received = 0;
            }
            var count = Math.Min(ChunkSize, state.Length - state.Received);
            if (count > 0)
            {
                await input.ReadExactlyAsync(state.Payload.AsMemory(state.Received, count), cancellation);
                BytesRead += count;
                state.Received += count;
            }
            if (state.Received >= state.Length)
            {
                var message = new RtmpMessage(state.TypeId, state.StreamId, state.Timestamp, state.Payload!);
                state.Payload = null;
                return message;
            }
        }
    }

    private async ValueTask<byte> ReadByteAsync(CancellationToken cancellation)
    {
        await ReadExactlyAsync(1, cancellation);
        return scratch[0];
    }

    private async ValueTask<uint> ReadUInt32Async(CancellationToken cancellation)
    {
        await ReadExactlyAsync(4, cancellation);
        return BinaryPrimitives.ReadUInt32BigEndian(scratch);
    }

    private async ValueTask ReadExactlyAsync(int count, CancellationToken cancellation)
    {
        await input.ReadExactlyAsync(scratch.AsMemory(0, count), cancellation);
        BytesRead += count;
    }

    private uint Read24(int offset) => (uint)(scratch[offset] << 16 | scratch[offset + 1] << 8 | scratch[offset + 2]);
}
