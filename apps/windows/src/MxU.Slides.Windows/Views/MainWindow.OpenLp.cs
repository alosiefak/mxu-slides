using System.Windows;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// "Import from OpenLP…" (Phase 15): the one way in to <see cref="OpenLpImportWindow"/>, used by
/// Screens &amp; Settings, the welcome guide, and a .sqlite or .zip picked in Import or dropped on the library.
/// </summary>
public partial class MainWindow
{
    /// <summary>OpenLP's data as it reaches us: a .sqlite database, or a zip of its data folder.</summary>
    internal static bool IsOpenLpFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".sqlite" or ".zip";

    /// <summary>
    /// Opens the OpenLP import window over <paramref name="owner"/> (this window when null).
    /// With a <paramref name="path"/> it starts importing from it straight away; without, it asks where OpenLP's data is.
    /// </summary>
    internal void ImportFromOpenLp(Window? owner = null, string? path = null)
    {
        if (model.IsImporting) return;
        var window = new OpenLpImportWindow(owner ?? this, model);
        if (path is not null) window.Loaded += (_, _) => _ = window.RunAsync(path);
        window.ShowDialog();
    }
}
