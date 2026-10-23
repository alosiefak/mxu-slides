using System.Security.Cryptography;
using System.Windows.Media;

namespace MxU.Slides.PptxImport;

/// <summary>
/// Port of the parts of the Mac's PresenterCore FontActivator the importer uses:
/// de-obfuscating and storing a deck's embedded fonts in the library's fonts
/// folder (refusing restricted-licence faces), and asking whether a family is installed.
/// </summary>
internal static class FontActivator
{
    public const string FolderName = "fonts";

    private static readonly Lazy<HashSet<string>> InstalledFamilies = new(() =>
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var family in Fonts.SystemFontFamilies)
        {
            names.Add(family.Source);
            foreach (var name in family.FamilyNames.Values) names.Add(name);
        }
        return names;
    });

    public static bool IsInstalled(string family) => InstalledFamilies.Value.Contains(family);

    /// <summary>Copies an embedded font into the library as fonts/&lt;sha256&gt;.&lt;ext&gt;; null when it is unreadable or restricted.</summary>
    public static string? Store(string dataPath, string libraryRoot)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(dataPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        if (SniffExtension(data) is null)
        {
            if (data.Length <= 32 || GuidBytes(Path.GetFileName(dataPath)) is not { } key) return null;
            var reversedKey = key.Reverse().ToArray();
            for (var index = 0; index < 32; index++) data[index] ^= reversedKey[index % 16];
        }
        if (SniffExtension(data) is not { } extension || !IsEmbeddingAllowed(data)) return null;

        var hash = Convert.ToHexStringLower(SHA256.HashData(data));
        var directory = Path.Combine(libraryRoot, FolderName);
        var path = Path.Combine(directory, $"{hash}.{extension}");
        if (!File.Exists(path))
        {
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllBytes(path, data);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
        return path;
    }

    public static string? SniffExtension(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) return null;
        var magic = data[..4];
        if (magic.SequenceEqual(new byte[] { 0x00, 0x01, 0x00, 0x00 }) || magic.SequenceEqual("true"u8)) return "ttf";
        if (magic.SequenceEqual("OTTO"u8)) return "otf";
        if (magic.SequenceEqual("ttcf"u8)) return "ttc";
        return null;
    }

    public static bool IsEmbeddingAllowed(byte[] data) => FsType(data) is not { } fsType || (fsType & 0x000F) != 0x0002;

    public static ushort? FsType(byte[] data)
    {
        ushort? U16(long offset) => offset >= 0 && offset + 2 <= data.Length ? (ushort)(data[offset] << 8 | data[offset + 1]) : null;
        uint? U32(long offset) => offset >= 0 && offset + 4 <= data.Length
            ? (uint)data[offset] << 24 | (uint)data[offset + 1] << 16 | (uint)data[offset + 2] << 8 | data[offset + 3]
            : null;

        long start = 0;
        if (data.AsSpan().StartsWith("ttcf"u8))
        {
            if (U32(12) is not { } first) return null;
            start = first;
        }
        if (U16(start + 4) is not { } numTables) return null;
        for (var index = 0; index < numTables; index++)
        {
            var record = start + 12 + index * 16;
            if (record + 16 > data.Length) return null;
            if (!data.AsSpan((int)record, 4).SequenceEqual("OS/2"u8)) continue;
            if (U32(record + 8) is not { } offset) return null;
            return U16(offset + 8);
        }
        return null;
    }

    public static byte[]? GuidBytes(string partName)
    {
        var digits = new string(Path.GetFileNameWithoutExtension(partName).Where(Uri.IsHexDigit).ToArray());
        return digits.Length == 32 ? Convert.FromHexString(digits) : null;
    }
}
