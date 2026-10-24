using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace MxU.Slides.Broadcast.Tests;

/// <summary>
/// Small real media files written byte by byte, for the import and transcode tests: a PNG,
/// and an uncompressed AVI (raw pictures, which Windows reads without any codec but which the
/// app flags for transcoding, as it would a camera's Motion JPEG or an old MPEG-2 file).
/// </summary>
internal static class TestMediaFiles
{
    /// <summary>A solid-colour PNG of the given size.</summary>
    public static void WritePng(string path, int width, int height, byte red = 0x33, byte green = 0x66, byte blue = 0xCC)
    {
        var raw = new MemoryStream();
        for (var y = 0; y < height; y++)
        {
            raw.WriteByte(0); // no filter
            for (var x = 0; x < width; x++)
            {
                raw.WriteByte(red);
                raw.WriteByte(green);
                raw.WriteByte(blue);
            }
        }
        var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) raw.WriteTo(zlib);

        using var file = File.Create(path);
        file.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bits per channel
        header[9] = 2; // RGB
        Chunk(file, "IHDR", header);
        Chunk(file, "IDAT", compressed.ToArray());
        Chunk(file, "IEND", []);
    }

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        stream.Write(number);
        var typed = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        stream.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc32(typed));
        stream.Write(number);
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

    /// <summary>
    /// An AVI of <paramref name="frames"/> uncompressed 24-bit pictures at 30 fps (a bar that
    /// moves). Width should be a multiple of 4 so rows need no padding.
    /// </summary>
    public static void WriteUncompressedAvi(string path, int width, int height, int frames)
    {
        var frameBytes = width * height * 3;
        using var file = new BinaryWriter(File.Create(path));
        void FourCc(string code) => file.Write(Encoding.ASCII.GetBytes(code));

        var hdrl = new MemoryStream();
        using (var h = new BinaryWriter(hdrl, Encoding.ASCII, leaveOpen: true))
        {
            h.Write(Encoding.ASCII.GetBytes("hdrl"));
            h.Write(Encoding.ASCII.GetBytes("avih"));
            h.Write(56);
            h.Write(33_333);            // microseconds per frame
            h.Write(frameBytes * 30);   // max bytes per second
            h.Write(0);                 // padding granularity
            h.Write(0x10);              // AVIF_HASINDEX
            h.Write(frames);
            h.Write(0);                 // initial frames
            h.Write(1);                 // streams
            h.Write(frameBytes);        // suggested buffer size
            h.Write(width);
            h.Write(height);
            h.Write(new byte[16]);      // reserved

            var strl = new MemoryStream();
            using (var s = new BinaryWriter(strl, Encoding.ASCII, leaveOpen: true))
            {
                s.Write(Encoding.ASCII.GetBytes("strl"));
                s.Write(Encoding.ASCII.GetBytes("strh"));
                s.Write(56);
                s.Write(Encoding.ASCII.GetBytes("vids"));
                s.Write(Encoding.ASCII.GetBytes("DIB "));
                s.Write(0);             // flags
                s.Write((short)0);      // priority
                s.Write((short)0);      // language
                s.Write(0);             // initial frames
                s.Write(1);             // scale
                s.Write(30);            // rate: 30/1 frames a second
                s.Write(0);             // start
                s.Write(frames);        // length
                s.Write(frameBytes);    // suggested buffer size
                s.Write(-1);            // quality
                s.Write(0);             // sample size
                s.Write((short)0);
                s.Write((short)0);
                s.Write((short)width);
                s.Write((short)height);

                s.Write(Encoding.ASCII.GetBytes("strf"));
                s.Write(40);
                s.Write(40);            // BITMAPINFOHEADER size
                s.Write(width);
                s.Write(height);        // positive: rows stored bottom-up
                s.Write((short)1);      // planes
                s.Write((short)24);     // bits per pixel
                s.Write(0);             // BI_RGB
                s.Write(frameBytes);
                s.Write(0);
                s.Write(0);
                s.Write(0);
                s.Write(0);
            }
            h.Write(Encoding.ASCII.GetBytes("LIST"));
            h.Write((int)strl.Length);
            h.Write(strl.ToArray());
        }

        var chunkSize = 8 + frameBytes;
        var moviLength = 4 + frames * chunkSize;
        var idxLength = frames * 16;
        FourCc("RIFF");
        file.Write(4 + (8 + (int)hdrl.Length) + (8 + moviLength) + (8 + idxLength));
        FourCc("AVI ");
        FourCc("LIST");
        file.Write((int)hdrl.Length);
        file.Write(hdrl.ToArray());

        FourCc("LIST");
        file.Write(moviLength);
        FourCc("movi");
        var picture = new byte[frameBytes];
        for (var f = 0; f < frames; f++)
        {
            var bar = f * 3 % width;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var i = (y * width + x) * 3;
                    var lit = Math.Abs(x - bar) < 4;
                    picture[i] = lit ? (byte)0xF0 : (byte)0x80;     // blue
                    picture[i + 1] = lit ? (byte)0xF0 : (byte)0x40; // green
                    picture[i + 2] = lit ? (byte)0xF0 : (byte)0x20; // red
                }
            }
            FourCc("00db");
            file.Write(frameBytes);
            file.Write(picture);
        }

        FourCc("idx1");
        file.Write(idxLength);
        for (var f = 0; f < frames; f++)
        {
            FourCc("00db");
            file.Write(0x10);               // AVIIF_KEYFRAME
            file.Write(4 + f * chunkSize);  // from the "movi" code
            file.Write(frameBytes);
        }
    }
}
