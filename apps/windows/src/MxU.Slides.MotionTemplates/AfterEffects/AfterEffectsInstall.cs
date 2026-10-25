using System.Text.RegularExpressions;

namespace MxU.Slides.MotionTemplates.AfterEffects;

/// <summary>
/// An After Effects on this PC: its folder, its version as its folder names it ("2026", "CC 2019",
/// "(Beta)"), and the two programs the bridge uses, both in its "Support Files" folder:
/// <list type="bullet">
/// <item><b>aerender.exe</b>, After Effects' command-line renderer: it opens a project, renders one
/// composition to a file and reports each frame as it goes.</item>
/// <item><b>AfterFX.exe</b>, After Effects itself, which runs a script with <c>-r</c>. The bridge uses
/// that to read a project's compositions and to change a copy's text and colours.</item>
/// </list>
/// </summary>
public sealed partial record AfterEffectsInstall(string Folder, string Version, string Aerender, string? AfterFx)
{
    public const string AerenderName = "aerender.exe";
    public const string AfterFxName = "AfterFX.exe";

    /// <summary>The folders After Effects installs under: Program Files\Adobe (and the 32-bit one, for very old versions).</summary>
    public static IReadOnlyList<string> UsualRoots() =>
    [
        .. new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
            .Select(Environment.GetFolderPath).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => Path.Combine(p, "Adobe")),
    ];

    /// <summary>
    /// Every After Effects under <paramref name="roots"/> (default: <see cref="UsualRoots"/>), newest
    /// first: by the year in its folder's name, with a beta after the release of the same year.
    /// </summary>
    public static IReadOnlyList<AfterEffectsInstall> Find(IEnumerable<string>? roots = null)
    {
        var found = new List<AfterEffectsInstall>();
        foreach (var root in roots ?? UsualRoots())
        {
            IEnumerable<string> folders;
            try { folders = Directory.Exists(root) ? Directory.EnumerateDirectories(root, "Adobe After Effects*") : []; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var folder in folders)
                if (FromFolder(folder) is { } install) found.Add(install);
        }
        return [.. found.OrderByDescending(i => Year(i.Version)).ThenBy(i => i.Version.Contains("Beta", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(i => i.Version, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The After Effects in <paramref name="folder"/> ("…\Adobe After Effects 2026"), or null when it has no aerender.</summary>
    public static AfterEffectsInstall? FromFolder(string folder)
    {
        var support = Path.Combine(folder, "Support Files");
        var aerender = Path.Combine(support, AerenderName);
        if (!File.Exists(aerender)) return null;
        var afterFx = Path.Combine(support, AfterFxName);
        var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
        var version = name.StartsWith("Adobe After Effects", StringComparison.OrdinalIgnoreCase) ? name["Adobe After Effects".Length..].Trim() : name;
        return new AfterEffectsInstall(folder, version.Length == 0 ? "?" : version, aerender, File.Exists(afterFx) ? afterFx : null);
    }

    /// <summary>The After Effects an aerender.exe chosen by hand belongs to ("Locate After Effects…"), or null when the file isn't there.</summary>
    public static AfterEffectsInstall? FromAerender(string aerenderPath)
    {
        if (!File.Exists(aerenderPath)) return null;
        var support = Path.GetDirectoryName(Path.GetFullPath(aerenderPath))!;
        var folder = Path.GetFileName(support).Equals("Support Files", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(support)! : support;
        var afterFx = Path.Combine(support, AfterFxName);
        var name = Path.GetFileName(folder);
        var version = name.StartsWith("Adobe After Effects", StringComparison.OrdinalIgnoreCase) ? name["Adobe After Effects".Length..].Trim() : name;
        return new AfterEffectsInstall(folder, version.Length == 0 ? "?" : version, aerenderPath, File.Exists(afterFx) ? afterFx : null);
    }

    /// <summary>"After Effects 2026".</summary>
    public string Title => $"After Effects {Version}";

    private static int Year(string version) => YearPattern().Match(version) is { Success: true } m ? int.Parse(m.Value) : 0;

    [GeneratedRegex(@"(19|20)\d{2}")]
    private static partial Regex YearPattern();
}
