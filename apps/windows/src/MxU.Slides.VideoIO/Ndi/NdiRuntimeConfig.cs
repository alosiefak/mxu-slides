using System.Runtime.InteropServices;
using System.Text.Json;

namespace MxU.Slides.VideoIO.Ndi;

/// <summary>
/// The NDI runtime's own settings file, used to pin NDI to one network adapter. NDI
/// reads ndi-config.v1.json from the folder in the NDI_CONFIG_DIR environment variable
/// when it initializes, so the app writes the file into its own folder and points the
/// process at it; other NDI apps on the PC keep their settings. Call <see cref="Adopt"/>
/// at startup, before the first NDI input or output, and <see cref="Activate"/> plus
/// <see cref="NdiLibrary.Reinitialize"/> when the operator changes the adapter.
/// Port of NDIKit/NDIRuntimeConfig.swift.
/// </summary>
public static class NdiRuntimeConfig
{
    public const string FileName = "ndi-config.v1.json";
    internal const string EnvironmentKey = "NDI_CONFIG_DIR";

    /// <summary>
    /// %LOCALAPPDATA%\MxU Slides\NDI (machine-local: adapters belong to this PC). The
    /// MXU_SLIDES_LOCAL variable moves it, as it moves the app's other local data.
    /// </summary>
    public static string SharedDirectory => Path.Combine(
        Environment.GetEnvironmentVariable("MXU_SLIDES_LOCAL")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MxU Slides"),
        "NDI");

    /// <summary>
    /// The file's contents: {"ndi":{"adapters":{"allowed":["10.1.20.7"]}}} to pin, or
    /// {"ndi":{}} (restrict nothing) when <paramref name="allowedAdapterIps"/> is empty.
    /// Byte-for-byte what the Mac writes.
    /// </summary>
    internal static byte[] ConfigJson(IReadOnlyList<string> allowedAdapterIps)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteStartObject("ndi");
            if (allowedAdapterIps.Count > 0)
            {
                json.WriteStartObject("adapters");
                json.WriteStartArray("allowed");
                foreach (var ip in allowedAdapterIps) json.WriteStringValue(ip);
                json.WriteEndArray();
                json.WriteEndObject();
            }
            json.WriteEndObject();
            json.WriteEndObject();
        }
        return stream.ToArray();
    }

    /// <summary>Writes the config (atomically) and points this process at it. Throws IOException if the folder can't be written.</summary>
    public static void Activate(IReadOnlyList<string> allowedAdapterIps, string? directory = null)
    {
        directory ??= SharedDirectory;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName);
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, ConfigJson(allowedAdapterIps));
        File.Move(temporary, path, overwrite: true);
        Adopt(directory);
    }

    /// <summary>Points this process's NDI runtime at <paramref name="directory"/> without changing the file.</summary>
    public static void Adopt(string? directory = null) => SetVariable(EnvironmentKey, directory ?? SharedDirectory);

    /// <summary>
    /// Sets the variable for Windows and for the C runtime's own copy of the environment
    /// (a DLL that reads it with getenv sees that copy, not the Windows one).
    /// </summary>
    internal static void SetVariable(string name, string? value)
    {
        Environment.SetEnvironmentVariable(name, value);
        try { _ = _wputenv_s(name, value ?? ""); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // No shared C runtime: nothing else to update.
        }
    }

    [DllImport("ucrtbase.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int _wputenv_s(string name, string value);
}
