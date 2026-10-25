using System.Buffers.Binary;
using System.Text;

namespace MxU.Slides.Production.Atem;

/// <summary>One of the switcher's video sources, as it names them ("Camera 2", short "CAM2").</summary>
public sealed record AtemInput(int Id, string LongName, string ShortName);

/// <summary>
/// What the app keeps of the switcher's state. On connecting, a switcher sends its whole state
/// as a burst of commands (hundreds of them), ending with "InCm" (initialisation complete),
/// then sends changes as they happen. Only the few needed here are read; the rest are
/// skipped by name. Thread-safe: the receive thread writes, the editors read.
/// </summary>
public sealed class AtemState
{
    private readonly Lock gate = new();
    private readonly Dictionary<int, AtemInput> inputs = [];
    private readonly Dictionary<int, string> macros = [];
    private readonly Dictionary<int, int> program = [];
    private readonly Dictionary<int, int> preview = [];

    private string? productName;
    private (int Major, int Minor)? version;
    private bool initComplete;

    /// <summary>The model, e.g. "ATEM Mini Pro" ("_pin").</summary>
    public string? ProductName
    {
        get
        {
            lock (gate) return productName;
        }
    }

    /// <summary>The protocol version ("_ver"): the firmware's, roughly.</summary>
    public (int Major, int Minor)? Version
    {
        get
        {
            lock (gate) return version;
        }
    }

    /// <summary>The first burst has all arrived ("InCm").</summary>
    public bool InitComplete
    {
        get
        {
            lock (gate) return initComplete;
        }
    }

    public IReadOnlyList<AtemInput> Inputs
    {
        get
        {
            lock (gate) return [.. inputs.Values.OrderBy(i => i.Id)];
        }
    }

    /// <summary>The recorded macros, by index (0 is "macro 1").</summary>
    public IReadOnlyDictionary<int, string> Macros
    {
        get
        {
            lock (gate) return new Dictionary<int, string>(macros);
        }
    }

    /// <summary>The source on program on an M/E (0 is M/E 1), if known.</summary>
    public int? Program(int mixEffect)
    {
        lock (gate) return program.TryGetValue(mixEffect, out var source) ? source : null;
    }

    public int? Preview(int mixEffect)
    {
        lock (gate) return preview.TryGetValue(mixEffect, out var source) ? source : null;
    }

    /// <summary>A source's name, or null when the switcher hasn't said.</summary>
    public string? InputName(int source)
    {
        lock (gate) return inputs.TryGetValue(source, out var input) ? input.LongName : null;
    }

    /// <summary>Forgets everything (a new session sends it all again).</summary>
    public void Reset()
    {
        lock (gate)
        {
            inputs.Clear();
            macros.Clear();
            program.Clear();
            preview.Clear();
            productName = null;
            version = null;
            initComplete = false;
        }
    }

    /// <summary>Takes in one command from the switcher. Unknown ones, and ones too short to read, are ignored.</summary>
    public void Apply(string name, ReadOnlySpan<byte> data)
    {
        lock (gate)
        {
            switch (name)
            {
                case "_ver" when data.Length >= 4:
                    version = (BinaryPrimitives.ReadUInt16BigEndian(data), BinaryPrimitives.ReadUInt16BigEndian(data[2..]));
                    break;
                case "_pin" when data.Length >= 1:
                    productName = Text(data, 0, Math.Min(40, data.Length));
                    break;
                case "InPr" when data.Length >= 26:
                {
                    // Input properties: id (2 bytes), long name (20 bytes), short name (4 bytes), then ports.
                    var id = BinaryPrimitives.ReadUInt16BigEndian(data);
                    inputs[id] = new AtemInput(id, Text(data, 2, 20), Text(data, 22, 4));
                    break;
                }
                case "MPrp" when data.Length >= 8:
                {
                    // Macro properties: index, used?, has unsupported ops?, name length, description length, name…
                    var index = BinaryPrimitives.ReadUInt16BigEndian(data);
                    var used = data[2] != 0;
                    var nameLength = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
                    var macroName = nameLength > 0 && 8 + nameLength <= data.Length ? Text(data, 8, nameLength) : "";
                    if (!used) macros.Remove(index);
                    else macros[index] = macroName.Length > 0 ? macroName : $"Macro {index + 1}";   // a recorded macro may have no name
                    break;
                }
                case "PrgI" when data.Length >= 4:
                    program[data[0]] = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
                    break;
                case "PrvI" when data.Length >= 4:
                    preview[data[0]] = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
                    break;
                case "InCm":
                    initComplete = true;
                    break;
            }
        }
    }

    /// <summary>A fixed-size, zero-padded UTF-8 text field.</summary>
    private static string Text(ReadOnlySpan<byte> data, int offset, int length)
    {
        var field = data.Slice(offset, length);
        var end = field.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? field : field[..end]).Trim();
    }
}
