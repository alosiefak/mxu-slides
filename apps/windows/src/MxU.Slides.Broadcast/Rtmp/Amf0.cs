using MxU.Slides.Broadcast.Hls.Vendored;

namespace MxU.Slides.Broadcast.Rtmp;

/// <summary>An AMF0 object: named values in the order they were written (order matters to some servers).</summary>
internal sealed class Amf0Object : OrderedDictionary<string, object?>
{
    public Amf0Object() : base(StringComparer.Ordinal) { }
}

/// <summary>An AMF0 "ECMA array": an object with a count in front, used for onMetaData.</summary>
internal sealed class Amf0EcmaArray : OrderedDictionary<string, object?>
{
    public Amf0EcmaArray() : base(StringComparer.Ordinal) { }
}

/// <summary>
/// AMF0, the Flash-era encoding RTMP commands use: each value is a one-byte type marker then
/// its data, big-endian. C# values map as: double/int/long → Number, bool → Boolean,
/// string → String (or Long String past 64 KB), null → Null, <see cref="Undefined"/> →
/// Undefined, <see cref="Amf0Object"/> → Object, <see cref="Amf0EcmaArray"/> → ECMA array,
/// <c>object?[]</c> → Strict array, <see cref="DateTime"/> → Date. Port of HaishinKit's
/// AMF0Serializer (AMF3, which RTMP publishing never needs, is not ported).
/// </summary>
internal static class Amf0
{
    public const byte Number = 0x00;
    public const byte Boolean = 0x01;
    public const byte String = 0x02;
    public const byte Object = 0x03;
    public const byte Null = 0x05;
    public const byte UndefinedMarker = 0x06;
    public const byte Reference = 0x07;
    public const byte EcmaArray = 0x08;
    public const byte ObjectEnd = 0x09;
    public const byte StrictArray = 0x0A;
    public const byte Date = 0x0B;
    public const byte LongString = 0x0C;
    public const byte XmlDocument = 0x0F;
    public const byte TypedObject = 0x10;
    public const byte AvmPlus = 0x11;

    /// <summary>AMF0's "undefined", distinct from null.</summary>
    public static readonly object Undefined = new UndefinedValue();

    private sealed class UndefinedValue
    {
        public override string ToString() => "undefined";
    }

    public static byte[] Encode(params object?[] values)
    {
        var buffer = new ByteArray(128);
        foreach (var value in values) Write(buffer, value);
        return buffer.ToArray();
    }

    public static List<object?> DecodeAll(ReadOnlySpan<byte> data)
    {
        var buffer = new ByteArray(data);
        var values = new List<object?>();
        while (buffer.BytesAvailable > 0) values.Add(Read(buffer));
        return values;
    }

    public static void Write(ByteArray buffer, object? value)
    {
        switch (value)
        {
            case null:
                buffer.WriteUInt8(Null);
                break;
            case UndefinedValue:
                buffer.WriteUInt8(UndefinedMarker);
                break;
            case bool b:
                buffer.WriteUInt8(Boolean).WriteUInt8(b ? (byte)1 : (byte)0);
                break;
            case string s:
                WriteString(buffer, s);
                break;
            case double or float or int or long or uint or ushort or short or byte or ulong:
                buffer.WriteUInt8(Number).WriteDouble(Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture));
                break;
            case Amf0EcmaArray array:
                buffer.WriteUInt8(EcmaArray).WriteUInt32((uint)array.Count);
                WritePairs(buffer, array);
                break;
            case Amf0Object obj:
                buffer.WriteUInt8(Object);
                WritePairs(buffer, obj);
                break;
            case IReadOnlyDictionary<string, object?> dictionary:
                buffer.WriteUInt8(Object);
                WritePairs(buffer, dictionary);
                break;
            case object?[] list:
                buffer.WriteUInt8(StrictArray).WriteUInt32((uint)list.Length);
                foreach (var item in list) Write(buffer, item);
                break;
            case DateTime date:
                buffer.WriteUInt8(Date)
                    .WriteDouble((date.ToUniversalTime() - DateTime.UnixEpoch).TotalMilliseconds)
                    .WriteUInt16(0);
                break;
            default:
                throw new ArgumentException($"AMF0 can't encode {value.GetType().Name}");
        }
    }

    private static void WriteString(ByteArray buffer, string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= ushort.MaxValue)
            buffer.WriteUInt8(String).WriteUInt16((ushort)bytes.Length).WriteBytes(bytes);
        else
            buffer.WriteUInt8(LongString).WriteUInt32((uint)bytes.Length).WriteBytes(bytes);
    }

    private static void WriteKey(ByteArray buffer, string key)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(key);
        buffer.WriteUInt16((ushort)bytes.Length).WriteBytes(bytes);
    }

    private static void WritePairs(ByteArray buffer, IEnumerable<KeyValuePair<string, object?>> pairs)
    {
        foreach (var (key, value) in pairs)
        {
            WriteKey(buffer, key);
            Write(buffer, value);
        }
        buffer.WriteUInt16(0).WriteUInt8(ObjectEnd);
    }

    /// <summary>Reads one value. Throws <see cref="FormatException"/> for markers RTMP servers don't send to publishers.</summary>
    public static object? Read(ByteArray buffer)
    {
        var marker = buffer.ReadUInt8();
        switch (marker)
        {
            case Number:
                return buffer.ReadDouble();
            case Boolean:
                return buffer.ReadUInt8() != 0;
            case String:
                return buffer.ReadUtf8Bytes(buffer.ReadUInt16());
            case LongString or XmlDocument:
                return buffer.ReadUtf8Bytes((int)buffer.ReadUInt32());
            case Object:
                var obj = new Amf0Object();
                ReadPairs(buffer, obj);
                return obj;
            case TypedObject:
                buffer.ReadUtf8Bytes(buffer.ReadUInt16());
                var typed = new Amf0Object();
                ReadPairs(buffer, typed);
                return typed;
            case Null:
                return null;
            case UndefinedMarker:
                return Undefined;
            case EcmaArray:
                buffer.ReadUInt32();
                var array = new Amf0EcmaArray();
                ReadPairs(buffer, array);
                return array;
            case StrictArray:
                var count = (int)buffer.ReadUInt32();
                var list = new object?[count];
                for (var i = 0; i < count; i++) list[i] = Read(buffer);
                return list;
            case Date:
                var milliseconds = buffer.ReadDouble();
                buffer.ReadUInt16();
                return DateTime.UnixEpoch.AddMilliseconds(milliseconds);
            default:
                throw new FormatException($"Unsupported AMF0 marker 0x{marker:X2}");
        }
    }

    private static void ReadPairs(ByteArray buffer, OrderedDictionary<string, object?> into)
    {
        while (true)
        {
            var length = buffer.ReadUInt16();
            if (length == 0)
            {
                // An empty key followed by the end marker closes the object.
                if (buffer.BytesAvailable > 0 && buffer.Span[buffer.Position] == ObjectEnd)
                {
                    buffer.ReadUInt8();
                    return;
                }
            }
            var key = buffer.ReadUtf8Bytes(length);
            into[key] = Read(buffer);
        }
    }
}
