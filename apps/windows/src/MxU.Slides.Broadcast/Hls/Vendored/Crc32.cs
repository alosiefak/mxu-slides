// Ported from HaishinKit (BSD 3-Clause, Copyright (c) 2015, shogo4405); see LICENSE-HaishinKit.txt.
namespace MxU.Slides.Broadcast.Hls.Vendored;

/// <summary>
/// The MPEG-2 CRC-32 (polynomial 0x04C11DB7, no reflection, no final XOR) that ends every
/// PAT and PMT section, so a player can tell a damaged table from a good one. Port of CRC32.swift.
/// </summary>
internal sealed class Crc32
{
    public static readonly Crc32 Mpeg2 = new(0x04c11db7);

    private readonly uint[] table = new uint[256];

    private Crc32(uint polynomial)
    {
        for (var i = 0; i < table.Length; i++)
        {
            var crc = (uint)i << 24;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc << 1) ^ ((crc & 0x80000000) == 0x80000000 ? polynomial : 0);
            table[i] = crc;
        }
    }

    public uint Calculate(ReadOnlySpan<byte> data, uint seed = 0xffffffff)
    {
        var crc = seed;
        foreach (var b in data)
            crc = (crc << 8) ^ table[(int)(((crc >> 24) ^ b) & 0xff)];
        return crc;
    }
}
