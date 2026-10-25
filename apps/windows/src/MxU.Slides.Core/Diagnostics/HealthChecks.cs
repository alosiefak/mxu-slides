namespace MxU.Slides.Core.Diagnostics;

public enum HealthStatus
{
    Good,
    Note,
    Warning,
    Problem,
}

/// <summary>One line of the health check: what was looked at, how it is, and what to do about it.</summary>
public sealed record HealthCheck(string Area, HealthStatus Status, string Summary, string? Advice = null);

/// <summary>
/// What the app found out about this PC and its library, for the health checks. The app gathers
/// these (from Windows, the library and its own state); <see cref="HealthChecks.Run"/> judges them.
/// </summary>
public sealed record HealthFacts
{
    public long? FreeDiskBytes { get; init; }
    public int LibraryLoadProblems { get; init; }
    public int MissingMediaFiles { get; init; }
    public int MediaNeedingTranscode { get; init; }
    public int Displays { get; init; } = 1;
    /// <summary>Screens set up to show on a display that isn't plugged in now (their names).</summary>
    public IReadOnlyList<string> ScreensWithoutDisplay { get; init; } = [];
    /// <summary>WPF's render tier: 0 is no graphics card (everything drawn in software), 2 is a full one.</summary>
    public int RenderTier { get; init; } = 2;
    public bool PixelShader3 { get; init; } = true;
    public int SoundOutputs { get; init; } = 1;
    public bool NdiInstalled { get; init; }
    public int DeckLinkCards { get; init; }
    public bool AutomaticBackups { get; init; } = true;
    public DateTimeOffset? LastBackup { get; init; }
    public int ErrorsToday { get; init; }
    public int HangsThisWeek { get; init; }
    public bool? OnBattery { get; init; }
    public bool PreventsSleep { get; init; } = true;
    public DateTimeOffset Now { get; init; } = DateTimeOffset.Now;
}

/// <summary>
/// The health checks: before a service, is this PC ready? Each fact becomes a line that's good,
/// worth knowing, a warning or a problem, with what to do. Windows only: the Mac's diagnostics
/// collect logs and performance samples but have no checklist.
/// </summary>
public static class HealthChecks
{
    private const long GB = 1024L * 1024 * 1024;

    public static IReadOnlyList<HealthCheck> Run(HealthFacts f)
    {
        var checks = new List<HealthCheck>();

        if (f.FreeDiskBytes is { } free)
            checks.Add(free < 2 * GB
                ? new("Disk space", HealthStatus.Problem, $"Only {Gigabytes(free)} free.", "Recordings and imports need room: delete or move old recordings, or empty the Recycle Bin.")
                : free < 10 * GB
                    ? new("Disk space", HealthStatus.Warning, $"{Gigabytes(free)} free.", "A long recording can use several GB an hour.")
                    : new("Disk space", HealthStatus.Good, $"{Gigabytes(free)} free."));

        checks.Add(f.LibraryLoadProblems > 0
            ? new("Library", HealthStatus.Warning, $"{Count(f.LibraryLoadProblems, "file")} couldn't be read at launch and {(f.LibraryLoadProblems == 1 ? "was" : "were")} put aside in Quarantine.",
                "Restore from a backup if something is missing (Settings › Library › Backups).")
            : new("Library", HealthStatus.Good, "Every document opened."));

        if (f.MissingMediaFiles > 0)
            checks.Add(new("Media", HealthStatus.Warning, $"{Count(f.MissingMediaFiles, "media file")} {(f.MissingMediaFiles == 1 ? "isn't" : "aren't")} on this PC.",
                "Media › right-click a tile › Locate missing file… finds them on a USB stick or another folder."));
        else if (f.MediaNeedingTranscode > 0)
            checks.Add(new("Media", HealthStatus.Warning, $"{Count(f.MediaNeedingTranscode, "video")} may not play well on Windows.",
                "Right-click the tile marked NEEDS TRANSCODE › Transcode now."));
        else
            checks.Add(new("Media", HealthStatus.Good, "Every media file is here."));

        if (f.ScreensWithoutDisplay.Count > 0)
            checks.Add(new("Screens", HealthStatus.Warning, $"Not connected: {string.Join(", ", f.ScreensWithoutDisplay)}.",
                "Plug in the projector or TV (and switch it on), or choose another display in Screens & Settings."));
        else if (f.Displays <= 1)
            checks.Add(new("Screens", HealthStatus.Note, "Only this PC's own screen is connected, so outputs show in preview windows.",
                "Connect a projector or TV and give it the Audience job in Screens & Settings."));
        else
            checks.Add(new("Screens", HealthStatus.Good, $"{f.Displays} displays connected."));

        checks.Add(f.RenderTier == 0
            ? new("Graphics", HealthStatus.Warning, "No graphics card is being used: everything is drawn by the processor.",
                "Transitions play as dissolves and some effects are left out. Check the graphics driver is installed.")
            : !f.PixelShader3
                ? new("Graphics", HealthStatus.Warning, "The graphics card can't run the app's effect shaders.", "Effects and the newer transitions are drawn simply.")
                : new("Graphics", HealthStatus.Good, "The graphics card draws effects and transitions."));

        checks.Add(f.SoundOutputs == 0
            ? new("Sound", HealthStatus.Warning, "No sound output is connected.", "Music and video sound won't be heard: plug in speakers or the sound desk.")
            : new("Sound", HealthStatus.Good, $"{Count(f.SoundOutputs, "sound output")} found."));

        checks.Add(new("NDI", f.NdiInstalled ? HealthStatus.Good : HealthStatus.Note,
            f.NdiInstalled ? "NDI Tools is installed." : "NDI Tools isn't installed, so NDI screens and sources aren't available.",
            f.NdiInstalled ? null : "Only needed to send the picture over the network to OBS, vMix or a streaming PC."));
        checks.Add(new("DeckLink", f.DeckLinkCards > 0 ? HealthStatus.Good : HealthStatus.Note,
            f.DeckLinkCards > 0 ? $"{Count(f.DeckLinkCards, "DeckLink card")} found." : "No Blackmagic DeckLink card found.",
            f.DeckLinkCards > 0 ? null : "Only needed for SDI or HDMI output through a capture card."));

        if (!f.AutomaticBackups)
            checks.Add(new("Backups", HealthStatus.Warning, "Automatic backups are off.", "Turn them on in Settings › Library › Backups."));
        else if (f.LastBackup is not { } last)
            checks.Add(new("Backups", HealthStatus.Warning, "No backup has been made yet.", "Settings › Library › Back up now…"));
        else if (f.Now - last > TimeSpan.FromDays(8))
            checks.Add(new("Backups", HealthStatus.Warning, $"The last backup is {(int)(f.Now - last).TotalDays} days old.", "Settings › Library › Back up now…"));
        else
            checks.Add(new("Backups", HealthStatus.Good, $"Last backup {Ago(f.Now - last)}."));

        if (f.HangsThisWeek > 0)
            checks.Add(new("Responsiveness", HealthStatus.Warning, $"The app stopped responding {Count(f.HangsThisWeek, "time")} this week.",
                "Report a problem: the report includes what it was stuck on."));
        else if (f.ErrorsToday > 0)
            checks.Add(new("Responsiveness", HealthStatus.Note, $"{Count(f.ErrorsToday, "error")} logged today.", "Open the log to see what happened."));
        else
            checks.Add(new("Responsiveness", HealthStatus.Good, "No hangs this week."));

        if (f.OnBattery == true)
            checks.Add(new("Power", HealthStatus.Warning, "Running on battery.", "Plug the laptop in for the service: on battery Windows may slow it down or sleep."));
        else if (!f.PreventsSleep)
            checks.Add(new("Power", HealthStatus.Warning, "The PC may go to sleep during a service.", "Turn on “Keep the computer awake” in Settings."));
        else
            checks.Add(new("Power", HealthStatus.Good, "Plugged in, and kept awake while outputs show."));

        return checks;
    }

    /// <summary>The worst status among the checks: one line for "is this PC ready?".</summary>
    public static HealthStatus Overall(IEnumerable<HealthCheck> checks) => checks.Select(c => c.Status).DefaultIfEmpty(HealthStatus.Good).Max();

    private static string Gigabytes(long bytes) => $"{bytes / (double)GB:0.#} GB";

    private static string Count(int n, string thing) => n == 1 ? $"1 {thing}" : $"{n} {thing}s";

    private static string Ago(TimeSpan span) =>
        span < TimeSpan.FromHours(1) ? "less than an hour ago" : span < TimeSpan.FromDays(1) ? $"{(int)span.TotalHours} h ago" : $"{(int)span.TotalDays} days ago";
}
