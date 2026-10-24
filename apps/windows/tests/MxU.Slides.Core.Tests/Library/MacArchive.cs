using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace MxU.Slides.Core.Tests.Library;

/// <summary>
/// Writes a zip the way the Mac's <c>ditto -c -k --norsrc</c> writes a .mxuslides, byte for
/// byte in layout: the folder's contents at the top (no parent folder), a directory entry for
/// each folder, files deflated as a stream (general-purpose bit 3: zero sizes in the local
/// header and a data descriptor after the data), "made by Unix" with each entry's permissions in
/// the external attributes, and the extended-timestamp ("UT") and Unix owner ("ux") extra
/// fields. .NET never writes zips like this, so it's the test of reading one.
/// </summary>
internal static class MacArchive
{
    public sealed record Entry(string Name, byte[]? Data);

    public static byte[] Ditto(IEnumerable<Entry> entries)
    {
        var output = new MemoryStream();
        var central = new MemoryStream();
        var count = 0;
        var stamp = new DateTime(2026, 10, 7, 9, 30, 0, DateTimeKind.Utc);
        var dosTime = (ushort)(stamp.Hour << 11 | stamp.Minute << 5 | stamp.Second / 2);
        var dosDate = (ushort)((stamp.Year - 1980) << 9 | stamp.Month << 5 | stamp.Day);
        var unix = (uint)new DateTimeOffset(stamp).ToUnixTimeSeconds();
        foreach (var entry in entries)
        {
            count++;
            var isFolder = entry.Data is null;
            var name = Encoding.UTF8.GetBytes(entry.Name);
            var data = entry.Data ?? [];
            var compressed = isFolder ? [] : Deflate(data);
            var crc = isFolder ? 0u : Crc32(data);
            ushort flags = isFolder ? (ushort)0 : (ushort)0x0008;
            ushort method = isFolder ? (ushort)0 : (ushort)8;
            var localExtra = Extra(unix, local: true);
            var offset = (uint)output.Length;

            Write32(output, 0x04034b50);
            Write16(output, 20);
            Write16(output, flags);
            Write16(output, method);
            Write16(output, dosTime);
            Write16(output, dosDate);
            Write32(output, isFolder ? 0 : 0u); // crc, sizes: in the data descriptor
            Write32(output, 0);
            Write32(output, 0);
            Write16(output, (ushort)name.Length);
            Write16(output, (ushort)localExtra.Length);
            output.Write(name);
            output.Write(localExtra);
            output.Write(compressed);
            if (!isFolder)
            {
                Write32(output, 0x08074b50);
                Write32(output, crc);
                Write32(output, (uint)compressed.Length);
                Write32(output, (uint)data.Length);
            }

            var centralExtra = Extra(unix, local: false);
            Write32(central, 0x02014b50);
            Write16(central, 0x0315); // made by Unix, zip 2.1
            Write16(central, 20);
            Write16(central, flags);
            Write16(central, method);
            Write16(central, dosTime);
            Write16(central, dosDate);
            Write32(central, crc);
            Write32(central, (uint)compressed.Length);
            Write32(central, (uint)data.Length);
            Write16(central, (ushort)name.Length);
            Write16(central, (ushort)centralExtra.Length);
            Write16(central, 0); // comment
            Write16(central, 0); // disk
            Write16(central, 0); // internal attributes
            Write32(central, (isFolder ? 0x41EDu : 0x81A4u) << 16); // drwxr-xr-x / -rw-r--r--
            Write32(central, offset);
            central.Write(name);
            central.Write(centralExtra);
        }
        var centralAt = (uint)output.Length;
        central.WriteTo(output);
        Write32(output, 0x06054b50);
        Write16(output, 0);
        Write16(output, 0);
        Write16(output, (ushort)count);
        Write16(output, (ushort)count);
        Write32(output, (uint)central.Length);
        Write32(output, centralAt);
        Write16(output, 0);
        return output.ToArray();
    }

    private static byte[] Extra(uint unix, bool local)
    {
        var extra = new MemoryStream();
        Write16(extra, 0x5455); // UT: modified (and, in the local header, accessed) time
        Write16(extra, (ushort)(local ? 9 : 5));
        extra.WriteByte(local ? (byte)3 : (byte)1);
        Write32(extra, unix);
        if (local) Write32(extra, unix);
        Write16(extra, 0x7875); // ux: owner and group
        Write16(extra, 11);
        extra.WriteByte(1);
        extra.WriteByte(4);
        Write32(extra, 501);
        extra.WriteByte(4);
        Write32(extra, 20);
        return extra.ToArray();
    }

    private static byte[] Deflate(byte[] data)
    {
        var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true)) deflate.Write(data);
        return output.ToArray();
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }

    private static void Write16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void Write32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }
}

/// <summary>A tiny but real TrueType file: just the tables the font code reads (name and OS/2).</summary>
internal static class TestFonts
{
    public static byte[] Build(string family, string postScriptName, string? typographicFamily = null, ushort fsType = 0)
    {
        var names = new List<(ushort Id, string Text)> { (1, family), (4, family + " Regular"), (6, postScriptName) };
        if (typographicFamily is not null) names.Add((16, typographicFamily));
        var strings = new MemoryStream();
        var records = new MemoryStream();
        foreach (var (id, text) in names)
        {
            var bytes = Encoding.BigEndianUnicode.GetBytes(text);
            Big16(records, 3);      // Windows
            Big16(records, 1);      // Unicode BMP
            Big16(records, 0x0409); // English (US)
            Big16(records, id);
            Big16(records, (ushort)bytes.Length);
            Big16(records, (ushort)strings.Length);
            strings.Write(bytes);
        }
        var name = new MemoryStream();
        Big16(name, 0);
        Big16(name, (ushort)names.Count);
        Big16(name, (ushort)(6 + names.Count * 12));
        records.WriteTo(name);
        strings.WriteTo(name);

        var os2 = new byte[96];
        BinaryPrimitives.WriteUInt16BigEndian(os2.AsSpan(8), fsType);

        var font = new MemoryStream();
        Big32(font, 0x00010000);
        Big16(font, 2);
        Big16(font, 32);
        Big16(font, 1);
        Big16(font, 0);
        var at = 12 + 2 * 16;
        foreach (var (tag, length) in new[] { ("OS/2", os2.Length), ("name", (int)name.Length) })
        {
            font.Write(Encoding.ASCII.GetBytes(tag));
            Big32(font, 0);
            Big32(font, (uint)at);
            Big32(font, (uint)length);
            at += (length + 3) & ~3;
        }
        font.Write(os2);
        name.WriteTo(font);
        return font.ToArray();
    }

    private static void Big16(Stream stream, ushort value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static void Big32(Stream stream, uint value)
    {
        Big16(stream, (ushort)(value >> 16));
        Big16(stream, (ushort)value);
    }
}
