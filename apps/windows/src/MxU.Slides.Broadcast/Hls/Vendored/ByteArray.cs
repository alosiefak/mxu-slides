// Ported from HaishinKit (BSD 3-Clause, Copyright (c) 2015, shogo4405); see LICENSE-HaishinKit.txt.
using System.Buffers.Binary;
using System.Text;

namespace MxU.Slides.Broadcast.Hls.Vendored;

/// <summary>
/// A growable byte buffer that reads and writes big-endian ("network order") integers, the
/// byte order MPEG-TS, FLV and RTMP all use. Writes append at the end and return the buffer
/// so calls chain; reads advance <see cref="Position"/> and throw <see cref="EndOfStreamException"/>
/// past the end. Port of the vendored ByteArray.swift (the parts the TS writer uses).
/// </summary>
internal sealed class ByteArray
{
    private byte[] buffer;
    private int length;

    public ByteArray(int capacity = 64) => buffer = new byte[Math.Max(16, capacity)];

    public ByteArray(ReadOnlySpan<byte> data)
    {
        buffer = data.ToArray();
        length = buffer.Length;
    }

    public int Length => length;

    public int Position { get; set; }

    public int BytesAvailable => length - Position;

    public ReadOnlySpan<byte> Span => buffer.AsSpan(0, length);

    public byte[] ToArray() => buffer.AsSpan(0, length).ToArray();

    private Span<byte> Grow(int count)
    {
        if (length + count > buffer.Length)
            Array.Resize(ref buffer, Math.Max(buffer.Length * 2, length + count));
        var span = buffer.AsSpan(length, count);
        length += count;
        return span;
    }

    public ByteArray WriteUInt8(byte value)
    {
        Grow(1)[0] = value;
        return this;
    }

    public ByteArray WriteUInt16(ushort value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(Grow(2), value);
        return this;
    }

    public ByteArray WriteUInt32(uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(Grow(4), value);
        return this;
    }

    public ByteArray WriteInt32(int value)
    {
        BinaryPrimitives.WriteInt32BigEndian(Grow(4), value);
        return this;
    }

    public ByteArray WriteDouble(double value)
    {
        BinaryPrimitives.WriteDoubleBigEndian(Grow(8), value);
        return this;
    }

    public ByteArray WriteBytes(ReadOnlySpan<byte> value)
    {
        value.CopyTo(Grow(value.Length));
        return this;
    }

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || Position + count > length) throw new EndOfStreamException("ByteArray: read past the end");
        var span = buffer.AsSpan(Position, count);
        Position += count;
        return span;
    }

    public byte ReadUInt8() => Take(1)[0];

    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));

    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));

    public double ReadDouble() => BinaryPrimitives.ReadDoubleBigEndian(Take(8));

    public byte[] ReadBytes(int count) => Take(count).ToArray();

    public string ReadUtf8Bytes(int count) => Encoding.UTF8.GetString(Take(count));
}
