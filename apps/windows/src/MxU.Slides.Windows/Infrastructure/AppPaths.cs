namespace MxU.Slides.Windows.Infrastructure;

/// <summary>Where the app keeps its library, settings and logs.</summary>
public static class AppPaths
{
    public const string AppFolderName = "MxU Slides";

    /// <summary>Roaming data: the library and settings (backed up with the user profile).</summary>
    public static string DataRoot { get; } = Environment.GetEnvironmentVariable("MXU_SLIDES_DATA")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);

    /// <summary>Machine-local data: logs, caches, crash markers.</summary>
    public static string LocalRoot { get; } = Environment.GetEnvironmentVariable("MXU_SLIDES_LOCAL")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);

    public static string DefaultLibrary => Path.Combine(DataRoot, "Library");
    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public static string SessionFile => Path.Combine(LocalRoot, "session.json");
    public static string LogDirectory => Path.Combine(LocalRoot, "Logs");
    public static string RunningMarker => Path.Combine(LocalRoot, "running.marker");
}
