using System.Text.Json;
using MxU.Slides.Core.Library;

namespace MxU.Slides.Windows.Infrastructure;

// The library browser's and the backups' preferences on this computer (Phase 8).
public sealed partial class AppSettings
{
    /// <summary>How each library list is sorted ("presentations", "media"), as the Mac keeps "library.sort.&lt;section&gt;".</summary>
    public Dictionary<string, LibrarySort>? LibrarySorts { get; set; }

    /// <summary>Folders made and still empty, per list ("presentations" → ["Easter"]). The Mac keeps the same per computer ("pendingLibraryFolders").</summary>
    public Dictionary<string, List<string>>? LibraryEmptyFolders { get; set; }

    /// <summary>How often a backup is made by itself.</summary>
    public BackupFrequency AutoBackup { get; set; } = BackupFrequency.Daily;

    /// <summary>How many automatic backups are kept (the oldest go). Seven, as the Mac keeps.</summary>
    public int AutoBackupKeep { get; set; } = ScheduledBackup.Keep;

    /// <summary>When the last automatic backup finished (the Mac's "backup.scheduled.lastRun").</summary>
    public DateTimeOffset? AutoBackupLastRun { get; set; }

    /// <summary>Why the last automatic backup didn't finish, until one does (the Mac's "backup.scheduled.lastProblem").</summary>
    public string? AutoBackupProblem { get; set; }

    /// <summary>Whether automatic backups carry the media files too (much bigger; made only while nothing is on screen).</summary>
    public bool AutoBackupIncludesMedia { get; set; }

    /// <summary>Where automatic backups go; null = the library's own Backups folder.</summary>
    public string? AutoBackupFolder { get; set; }

    /// <summary>When the last "Report a problem"/crash look happened: the previous launch, for spotting a crash since.</summary>
    public DateTimeOffset? LastLaunchAt { get; set; }

    /// <summary>The export panel's last choices (the Mac remembers "export.format", "export.pdfColumns", "export.includeMedia").</summary>
    public Core.Export.PresentationExport.Format ExportFormat { get; set; } = Core.Export.PresentationExport.Format.Pdf;

    public Core.Export.PresentationExport.PdfKind ExportPdfKind { get; set; } = Core.Export.PresentationExport.PdfKind.SlideSheet;

    public int ExportColumns { get; set; } = 3;

    public Core.Export.PresentationExport.ImageType ExportImageType { get; set; } = Core.Export.PresentationExport.ImageType.Png;

    public bool ExportIncludesMedia { get; set; } = true;

    public LibrarySort SortFor(string list) => LibrarySorts?.GetValueOrDefault(list) ?? LibrarySort.Name;

    public IReadOnlyList<string> EmptyFolders(string list) => LibraryEmptyFolders?.GetValueOrDefault(list) ?? [];

    /// <summary>
    /// The settings that may travel in a backup to another PC: the Mac's <c>portableKeys</c>, by
    /// Windows' names. What belongs to this PC (its screens, devices, library folder, network API,
    /// window places, sound devices) stays behind.
    /// </summary>
    public static readonly IReadOnlySet<string> PortableKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(LinesPerSlide), nameof(DefaultThemeId), nameof(MidiMap),
        nameof(Transition), nameof(TransitionSeconds), nameof(MediaTransition), nameof(MediaTransitionSeconds),
        nameof(SlideTransitionOptions), nameof(MediaTransitionOptions),
        nameof(ClearAllSkips), nameof(TransportSkipToEndSeconds), "present.emptyRepeatsOnce", "confidence.nextSkipsBlankSlides",
        nameof(ScriptureTranslation), nameof(ScriptureCompare), nameof(ScriptureCompareOnSlides), nameof(ScriptureLayout),
        nameof(ScriptureVersesPerSlide), nameof(ScriptureVerseNumbers), nameof(ScriptureReferenceLine), nameof(ScriptureThemeId),
        nameof(LibrarySorts), nameof(LibraryEmptyFolders), nameof(AutoBackup), nameof(AutoBackupKeep),
    };

    /// <summary>The other files in this PC's data folder that travel whole (the timers and their board).</summary>
    public static readonly IReadOnlyList<string> PortableFiles = ["timers.json", "timer-board.json"];

    private const string PendingSuffix = ".from-backup";

    /// <summary>
    /// This PC's settings as a backup carries them: settings.json trimmed to <see cref="PortableKeys"/>,
    /// and the timers files. (Read on whichever thread makes the backup: they're small.)
    /// </summary>
    public static Dictionary<string, byte[]> PortableForBackup()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(AppPaths.SettingsFile) && BackupSettings.Portable(File.ReadAllBytes(AppPaths.SettingsFile), PortableKeys) is { } portable)
                files["settings.json"] = portable;
            foreach (var name in PortableFiles)
            {
                var path = Path.Combine(AppPaths.DataRoot, name);
                if (File.Exists(path)) files[name] = File.ReadAllBytes(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Backup: this PC's settings couldn't be read: {ex.Message}");
        }
        return files;
    }

    /// <summary>
    /// A restore's settings, put aside to take effect at the next launch (as the Mac stages them),
    /// so nothing the running show depends on changes under it. Returns how many were staged.
    /// </summary>
    public static int StageFromBackup(IReadOnlyDictionary<string, byte[]> files, BackupPolicy policy)
    {
        var staged = 0;
        foreach (var (name, bytes) in files)
        {
            if (!name.Equals("settings.json", StringComparison.OrdinalIgnoreCase) && !PortableFiles.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            // Keep mine: a timers file this PC already has stays.
            if (policy == BackupPolicy.KeepMine && !name.Equals("settings.json", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(AppPaths.DataRoot, name))) continue;
            try
            {
                Directory.CreateDirectory(AppPaths.DataRoot);
                LibraryStore.AtomicWrite(Path.Combine(AppPaths.DataRoot, name + PendingSuffix), bytes);
                if (name.Equals("settings.json", StringComparison.OrdinalIgnoreCase))
                    File.WriteAllText(Path.Combine(AppPaths.DataRoot, "settings.policy" + PendingSuffix), policy.ToString());
                staged++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Restore: couldn't stage {name}: {ex.Message}");
            }
        }
        return staged;
    }

    /// <summary>
    /// At launch, before the settings are read: lays settings staged by a restore over this PC's
    /// own (only the portable keys), and puts staged timers files in place.
    /// </summary>
    private static void ApplyStagedFromBackup()
    {
        try
        {
            var staged = Path.Combine(AppPaths.DataRoot, "settings.json" + PendingSuffix);
            if (File.Exists(staged))
            {
                var policyFile = Path.Combine(AppPaths.DataRoot, "settings.policy" + PendingSuffix);
                var policy = File.Exists(policyFile) && Enum.TryParse<BackupPolicy>(File.ReadAllText(policyFile), out var p) ? p : BackupPolicy.NewerWins;
                var current = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : "{}"u8.ToArray();
                if (BackupSettings.Apply(current, File.ReadAllBytes(staged), PortableKeys, policy) is { } merged)
                    LibraryStore.AtomicWrite(AppPaths.SettingsFile, merged);
                File.Delete(staged);
                File.Delete(policyFile);
                Log.Info("Applied the settings from a restored backup");
            }
            foreach (var name in PortableFiles)
            {
                var pending = Path.Combine(AppPaths.DataRoot, name + PendingSuffix);
                if (File.Exists(pending)) File.Move(pending, Path.Combine(AppPaths.DataRoot, name), overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn($"The settings from a restored backup couldn't be applied: {ex.Message}");
        }
    }
}
