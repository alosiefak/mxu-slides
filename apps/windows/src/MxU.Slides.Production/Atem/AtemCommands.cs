using System.Buffers.Binary;

namespace MxU.Slides.Production.Atem;

/// <summary>
/// The commands the app sends a switcher, each a four-letter name and four bytes of data, laid
/// out as atem-connection and libqatem send them. Numbers here are the switcher's own, which
/// count from 0 (M/E 1 is 0, macro 1 is 0, aux 1 is 0, keyer 1 is 0); video sources keep their
/// switcher numbers (1 = Camera 1, 3010 = Media Player 1, 0 = Black). The production step
/// counts from 1, as the switcher's own panel does, and converts.
/// </summary>
public static class AtemCommands
{
    /// <summary>Puts a source on program (straight cut). "CPgI": M/E, 0, source.</summary>
    public static byte[] ProgramInput(int mixEffect, int source) => MeSource("CPgI", mixEffect, source);

    /// <summary>Puts a source on preview. "CPvI": M/E, 0, source.</summary>
    public static byte[] PreviewInput(int mixEffect, int source) => MeSource("CPvI", mixEffect, source);

    /// <summary>Cut: swaps preview and program at once. "DCut": M/E.</summary>
    public static byte[] Cut(int mixEffect) => AtemPacket.Command("DCut", [(byte)mixEffect, 0, 0, 0]);

    /// <summary>Auto: runs the M/E's transition (mix, wipe…) at its rate. "DAut": M/E.</summary>
    public static byte[] Auto(int mixEffect) => AtemPacket.Command("DAut", [(byte)mixEffect, 0, 0, 0]);

    /// <summary>Fade to black, or back up from it (it toggles, as the FTB button does). "FtbA": M/E, 2.</summary>
    public static byte[] FadeToBlack(int mixEffect) => AtemPacket.Command("FtbA", [(byte)mixEffect, 0x02, 0, 0]);

    /// <summary>Runs a macro. "MAct": the macro's index (two bytes), action 0 = run.</summary>
    public static byte[] RunMacro(int index)
    {
        var data = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(data, (ushort)index);
        return AtemPacket.Command("MAct", data);
    }

    /// <summary>Routes a source to an aux output. "CAuS": mask 1, aux, source.</summary>
    public static byte[] AuxSource(int aux, int source)
    {
        var data = new byte[4];
        data[0] = 0x01;
        data[1] = (byte)aux;
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2), (ushort)source);
        return AtemPacket.Command("CAuS", data);
    }

    /// <summary>Puts an upstream keyer on air or takes it off. "CKOn": M/E, keyer, on.</summary>
    public static byte[] UpstreamKeyOnAir(int mixEffect, int keyer, bool onAir) =>
        AtemPacket.Command("CKOn", [(byte)mixEffect, (byte)keyer, onAir ? (byte)1 : (byte)0, 0]);

    /// <summary>Puts a downstream keyer on air or takes it off. "CDsL": keyer, on.</summary>
    public static byte[] DownstreamKeyOnAir(int keyer, bool onAir) =>
        AtemPacket.Command("CDsL", [(byte)keyer, onAir ? (byte)1 : (byte)0, 0, 0]);

    private static byte[] MeSource(string name, int mixEffect, int source)
    {
        var data = new byte[4];
        data[0] = (byte)mixEffect;
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2), (ushort)source);
        return AtemPacket.Command(name, data);
    }
}
