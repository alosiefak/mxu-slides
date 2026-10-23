using System.Text.Json;
using System.Text.Json.Serialization;
using MxU.Slides.Core.Library;

namespace MxU.Slides.Windows.Infrastructure;

public enum OutputTransition { Cut, Dissolve, FadeThroughBlack }

/// <summary>Per-computer preferences, saved as settings.json next to the library.</summary>
public sealed class AppSettings
{
    public string LibraryPath { get; set; } = AppPaths.DefaultLibrary;

    /// <summary>The display (Windows device name, e.g. \\.\DISPLAY2) used for the audience, or null for none.</summary>
    public string? AudienceDisplay { get; set; }
    public string? AudienceDisplayName { get; set; }
    public string? StageDisplay { get; set; }
    public string? StageDisplayName { get; set; }

    /// <summary>Turns the audience and stage outputs on at launch when their displays are connected.</summary>
    public bool OutputsOnAtLaunch { get; set; } = true;

    public OutputTransition Transition { get; set; } = OutputTransition.Dissolve;
    public double TransitionSeconds { get; set; } = 0.4;

    public int LinesPerSlide { get; set; } = 2;
    public string DefaultThemeId { get; set; } = StarterPack.CleanGeometric.ThemeId();
    public string? StageLayoutId { get; set; }

    /// <summary>Plays foreground video sound; background loops are always silent.</summary>
    public double VideoVolume { get; set; } = 1.0;

    /// <summary>Keeps the computer and displays awake while any output is showing.</summary>
    public bool PreventSleep { get; set; } = true;

    public double ThumbnailSize { get; set; } = 200;

    /// <summary>Lets MIDI note-ons run commands. Off by default so a keyboard on the same PC can't clear the screen.</summary>
    public bool MidiInputEnabled { get; set; }

    public MxU.Slides.Core.Control.MidiCommandMap MidiMap { get; set; } = new();

    /// <summary>Lets phones, tablets and other apps on the network control the show (the Local API). Off by default.</summary>
    public bool LocalApiEnabled { get; set; }

    public int LocalApiPort { get; set; } = MxU.Slides.LocalApi.LocalApiServer.DefaultPort;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Json) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Error("Settings were unreadable; using defaults", ex);
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataRoot);
            LibraryStore.AtomicWrite(AppPaths.SettingsFile, JsonSerializer.SerializeToUtf8Bytes(this, Json));
        }
        catch (Exception ex)
        {
            Log.Error("Could not save settings", ex);
        }
    }
}

/// <summary>What was open when the app last ran, for recovery after a crash or restart.</summary>
public sealed class AppSession
{
    public string? ServiceId { get; set; }
    public string? ServiceItemId { get; set; }
    public string? PresentationId { get; set; }
    public string? LivePresentationId { get; set; }
    public string? LiveSlideId { get; set; }
    public string? LiveArrangementId { get; set; }
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; } = true;

    public static AppSession Load()
    {
        try
        {
            if (File.Exists(AppPaths.SessionFile))
                return JsonSerializer.Deserialize<AppSession>(File.ReadAllText(AppPaths.SessionFile)) ?? new AppSession();
        }
        catch (Exception ex)
        {
            Log.Warn($"Session file unreadable: {ex.Message}");
        }
        return new AppSession();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LocalRoot);
            LibraryStore.AtomicWrite(AppPaths.SessionFile, JsonSerializer.SerializeToUtf8Bytes(this));
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not save session: {ex.Message}");
        }
    }
}
