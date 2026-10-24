using System.Text.Json;
using MxU.Slides.Core.Library;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>
/// Who this computer is, for things that are chosen per computer rather than per library: which
/// version of a service it runs (the 9:00 booth runs "9am", the overflow room "11am"). Kept in the
/// machine-local folder, never the roaming one, so two PCs that share a Windows account (or a
/// synced settings folder) still count as two computers. The Mac keeps the same in UserDefaults
/// ("machine.id", "serviceVersion.lastName").
/// </summary>
public sealed class ThisComputer
{
    private static string FilePath => Path.Combine(AppPaths.LocalRoot, "this-computer.json");

    /// <summary>A stable id, made the first time the app runs here.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("D").ToUpperInvariant();

    /// <summary>The name other computers see beside the version it runs ("Version 2 — BOOTH-PC").</summary>
    public string Name => Environment.MachineName;

    /// <summary>The name of the version picked last, so next week's service runs its "11am" too until another is picked.</summary>
    public string? RememberedVersionName { get; set; }

    public static ThisComputer Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<ThisComputer>(File.ReadAllText(FilePath)) is { Id.Length: > 0 } saved)
                return saved;
        }
        catch (Exception ex)
        {
            Log.Warn($"This computer's identity was unreadable; making a new one: {ex.Message}");
        }
        var made = new ThisComputer();
        made.Save();
        return made;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LocalRoot);
            LibraryStore.AtomicWrite(FilePath, JsonSerializer.SerializeToUtf8Bytes(this));
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't save this computer's identity: {ex.Message}");
        }
    }
}
