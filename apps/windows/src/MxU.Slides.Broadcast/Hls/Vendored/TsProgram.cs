// Ported from HaishinKit (BSD 3-Clause, Copyright (c) 2015, shogo4405); see LICENSE-HaishinKit.txt.
namespace MxU.Slides.Broadcast.Hls.Vendored;

/// <summary>
/// A program-specific information table (PSI): the small tables at the start of every segment
/// that tell a player which packet ids carry what. Each is one TS packet: a pointer byte, the
/// section header, the table's own data, and a CRC. Port of TSProgram in TSProgram.swift.
/// </summary>
internal abstract class TsProgram
{
    private const byte ReservedBits = 0x03;
    private const ushort DefaultTableIdExtension = 1;

    protected byte TableId;
    private const byte PointerField = 0;
    private const byte VersionNumber = 0;
    private const bool CurrentNextIndicator = true;
    private const byte SectionNumber = 0;
    private const byte LastSectionNumber = 0;

    protected abstract byte[] TableData();

    public byte[] Data()
    {
        var tableData = TableData();
        var sectionLength = (ushort)(tableData.Length + 9);
        var sectionSyntaxIndicator = tableData.Length > 0;
        var buffer = new ByteArray()
            .WriteUInt8(TableId)
            .WriteUInt16((ushort)((sectionSyntaxIndicator ? 0x8000 : 0) | ReservedBits << 12 | sectionLength))
            .WriteUInt16(DefaultTableIdExtension)
            .WriteUInt8((byte)(ReservedBits << 6 | VersionNumber << 1 | (CurrentNextIndicator ? 1 : 0)))
            .WriteUInt8(SectionNumber)
            .WriteUInt8(LastSectionNumber)
            .WriteBytes(tableData);
        var crc32 = Crc32.Mpeg2.Calculate(buffer.Span);
        return new ByteArray().WriteUInt8(PointerField).WriteBytes(buffer.WriteUInt32(crc32).Span).ToArray();
    }

    public TsPacket[] ArrayOfPackets(ushort pid)
    {
        var packet = new TsPacket { PayloadUnitStartIndicator = true, Pid = pid };
        packet.Fill(Data(), useAdaptationField: false);
        return [packet];
    }
}

/// <summary>The PAT (packet id 0): lists the programs and the packet id of each one's PMT.</summary>
internal sealed class TsProgramAssociation : TsProgram
{
    public Dictionary<ushort, ushort> Programs { get; } = [];

    public TsProgramAssociation() => TableId = 0;

    protected override byte[] TableData()
    {
        var buffer = new ByteArray();
        foreach (var (number, programMapPid) in Programs)
            buffer.WriteUInt16(number).WriteUInt16((ushort)(programMapPid | 0xe000));
        return buffer.ToArray();
    }
}

/// <summary>The PMT: which packet id carries the clock, and the type and packet id of each elementary stream.</summary>
internal sealed class TsProgramMap : TsProgram
{
    public ushort PcrPid;
    public readonly List<EsSpecificData> ElementaryStreamSpecificData = [];
    private const ushort ProgramInfoLength = 0;

    public TsProgramMap() => TableId = 2;

    protected override byte[] TableData()
    {
        ElementaryStreamSpecificData.Sort((lhs, rhs) => lhs.ElementaryPid.CompareTo(rhs.ElementaryPid));
        var bytes = new ByteArray();
        foreach (var essd in ElementaryStreamSpecificData) essd.WriteTo(bytes);
        return new ByteArray()
            .WriteUInt16((ushort)(PcrPid | 0xe000))
            .WriteUInt16(ProgramInfoLength | 0xf000)
            .WriteBytes(bytes.Span)
            .ToArray();
    }
}

/// <summary>Elementary stream types a PMT can declare.</summary>
internal enum EsStreamType : byte
{
    Unspecific = 0x00,
    AdtsAac = 0x0F,
    H264 = 0x1B,
    H265 = 0x24,
}

/// <summary>One PMT row: a stream's type and packet id. Port of ESSpecificData.swift.</summary>
internal readonly record struct EsSpecificData(EsStreamType StreamType, ushort ElementaryPid)
{
    public const int FixedHeaderSize = 5;

    public void WriteTo(ByteArray output) =>
        output.WriteUInt8((byte)StreamType)
            .WriteUInt16((ushort)(ElementaryPid | 0xe000))
            .WriteUInt16(0xf000);
}
