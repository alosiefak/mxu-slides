using System.Globalization;
using System.Text.RegularExpressions;

namespace MxU.Slides.Core.Library;

/// <summary>How often automatic backups are made. Per PC (settings.json): another PC may back up the same songs differently.</summary>
public enum BackupFrequency
{
    Off,
    EveryLaunch,
    /// <summary>The Mac's nightly backup: once after each 3 AM (a night the PC was off is caught up the next time it runs).</summary>
    Daily,
    Weekly,
}

/// <summary>
/// The Mac's nightly backup rules (ScheduledBackup.swift): a backup is due once after each 3 AM,
/// so a night the computer was off is made up the next time it runs; each night's file is named
/// "MxU Slides Library 2026-11-01.mxubackup"; the newest seven of those are kept, and nothing
/// else in the folder is touched.
/// </summary>
public static class ScheduledBackup
{
    public const int Hour = 3;
    public const int Keep = 7;
    public const string Prefix = "MxU Slides Library ";

    /// <summary>The latest 3 AM at or before <paramref name="now"/> (this PC's time).</summary>
    public static DateTime LastSlot(DateTime now)
    {
        var today = now.Date.AddHours(Hour);
        return today <= now ? today : today.AddDays(-1);
    }

    public static DateTime NextSlot(DateTime now) => LastSlot(now).AddDays(1);

    public static bool Due(DateTime? lastRun, DateTime now) => lastRun is not { } last || last < LastSlot(now);

    public static string FileName(DateTime date) => $"{Prefix}{date:yyyy-MM-dd}.{LibraryBackup.FileExtension}";

    /// <summary>The nightly files past the newest <paramref name="keep"/> (by name, which is by date); other files never.</summary>
    public static IReadOnlyList<string> Pruned(IEnumerable<string> names, int keep = Keep)
    {
        var ours = names.Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith("." + LibraryBackup.FileExtension, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToList();
        return [.. ours.Take(Math.Max(0, ours.Count - Math.Max(1, keep)))];
    }

    /// <summary>The line under the setting: "Every night at 3 AM to Backups, keeping the last 7. Last: Sun 03:00." Port of <c>caption</c>.</summary>
    public static string Caption(BackupFrequency frequency, string? folder, int keep, DateTime? lastRun, string? problem)
    {
        if (frequency == BackupFrequency.Off || folder is null) return "Off. Choose how often, and a folder or drive.";
        var name = Path.GetFileName(folder.TrimEnd('\\', '/'));
        if (problem is { Length: > 0 }) return $"The last backup to {name} didn't finish: {problem}";
        var when = frequency switch
        {
            BackupFrequency.EveryLaunch => "Each time MxU Slides opens",
            BackupFrequency.Weekly => "Once a week",
            _ => $"Every night at {Hour} AM",
        };
        var last = lastRun is { } at ? $" Last: {at.ToString("ddd HH:mm", CultureInfo.CurrentCulture)}." : "";
        return $"{when} to {name}, keeping the last {keep}.{last}";
    }
}

/// <summary>
/// The backups in a folder, and when the next automatic one is due. Automatic backups are the
/// Mac's nightly files ("MxU Slides Library 2026-11-01.mxubackup", one a day: another the same day
/// replaces it); safety copies taken before a restore are "Before restore 2026-10-25 101200.mxubackup";
/// anything else in the folder (a backup someone saved there by hand) is listed but never pruned. The
/// old daily copies of the documents (folders named "2026-10-24") are listed too, so they can
/// still be restored.
/// </summary>
public static partial class BackupFiles
{
    public const string AutomaticPrefix = ScheduledBackup.Prefix;
    public const string SafetyPrefix = "Before restore ";

    /// <summary>How many safety copies are kept (the newest).</summary>
    public const int SafetyCopiesKept = 5;

    public enum Kind
    {
        Automatic,
        SafetyCopy,
        Saved,
        DailyCopy,
    }

    public sealed record Entry(string Path, Kind Kind, DateTimeOffset Created, long Bytes)
    {
        public string Name => System.IO.Path.GetFileName(Path);
    }

    public static string AutomaticName(DateTimeOffset when) => ScheduledBackup.FileName(when.ToLocalTime().DateTime);

    public static string SafetyCopyName(DateTimeOffset when) => SafetyPrefix + Stamp(when) + "." + LibraryBackup.FileExtension;

    /// <summary>What the Save dialog offers for a backup made by hand ("Library 2026-10-25.mxubackup", as on the Mac).</summary>
    public static string SavedName(DateTimeOffset when) => $"Library {when:yyyy-MM-dd}.{LibraryBackup.FileExtension}";

    // Named in this PC's own time, as people read it ("09:30" is half past nine here).
    private static string Stamp(DateTimeOffset when) => when.ToLocalTime().ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture);

    /// <summary>Every backup in <paramref name="folder"/>, newest first.</summary>
    public static IReadOnlyList<Entry> List(string folder)
    {
        if (!Directory.Exists(folder)) return [];
        var entries = new List<Entry>();
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*." + LibraryBackup.FileExtension))
        {
            var kind = file.Name.StartsWith(AutomaticPrefix, StringComparison.OrdinalIgnoreCase) ? Kind.Automatic
                : file.Name.StartsWith(SafetyPrefix, StringComparison.OrdinalIgnoreCase) ? Kind.SafetyCopy
                : Kind.Saved;
            // A nightly file's name has only its day: when it was written says the time.
            entries.Add(new Entry(file.FullName, kind, kind == Kind.SafetyCopy ? StampIn(file.Name) ?? file.LastWriteTime : file.LastWriteTime, file.Length));
        }
        foreach (var directory in new DirectoryInfo(folder).EnumerateDirectories())
            if (DailyName().IsMatch(directory.Name)
                && DateTime.TryParseExact(directory.Name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var day))
                entries.Add(new Entry(directory.FullName, Kind.DailyCopy, day, 0));
        return [.. entries.OrderByDescending(e => e.Created).ThenBy(e => e.Name, StringComparer.Ordinal)];
    }

    /// <summary>When the newest automatic backup was made, or null when there's none yet.</summary>
    public static DateTimeOffset? LastAutomatic(string folder) =>
        List(folder).Where(e => e.Kind == Kind.Automatic).Select(e => (DateTimeOffset?)e.Created).FirstOrDefault();

    /// <summary>
    /// Whether an automatic backup is due: every launch (at launch only), once after each 3 AM
    /// (the Mac's nightly rule), or once seven days have passed. Never when they're off.
    /// </summary>
    public static bool IsDue(BackupFrequency frequency, DateTimeOffset? last, DateTimeOffset now, bool atLaunch) => frequency switch
    {
        BackupFrequency.EveryLaunch => atLaunch,
        BackupFrequency.Daily => ScheduledBackup.Due(last?.ToLocalTime().DateTime, now.ToLocalTime().DateTime),
        BackupFrequency.Weekly => last is not { } w || now - w >= TimeSpan.FromDays(7),
        _ => false,
    };

    /// <summary>
    /// A backup with the media files reads gigabytes off the disk, which could make a video on
    /// screen stutter, so it waits until nothing is showing. One of just the documents is a moment's
    /// work and goes ahead.
    /// </summary>
    public static bool ShouldWait(bool includeMedia, bool showIsLive) => includeMedia && showIsLive;

    /// <summary>
    /// The automatic backups past the newest <paramref name="keep"/> (the Mac's <c>pruned</c>), and the
    /// safety copies past the newest <see cref="SafetyCopiesKept"/>: the ones to delete. Backups saved
    /// by hand are never in it.
    /// </summary>
    public static IReadOnlyList<Entry> Expired(IEnumerable<Entry> entries, int keep)
    {
        var all = entries.ToList();
        var nightly = ScheduledBackup.Pruned(all.Where(e => e.Kind == Kind.Automatic).Select(e => e.Name), keep).ToHashSet(StringComparer.Ordinal);
        return
        [
            .. all.Where(e => e.Kind == Kind.Automatic && nightly.Contains(e.Name)),
            .. all.Where(e => e.Kind == Kind.SafetyCopy).OrderByDescending(e => e.Created).Skip(SafetyCopiesKept),
        ];
    }

    /// <summary>Deletes what <see cref="Expired"/> lists. Returns how many went.</summary>
    public static int Prune(string folder, int keep)
    {
        var gone = 0;
        foreach (var entry in Expired(List(folder), keep))
        {
            try
            {
                File.Delete(entry.Path);
                gone++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use (being copied to a USB stick, say): next time.
            }
        }
        return gone;
    }

    private static DateTimeOffset? StampIn(string name)
    {
        var match = StampPattern().Match(name);
        return match.Success && DateTime.TryParseExact(match.Value, "yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var at)
            ? new DateTimeOffset(at) : null;
    }

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2} \d{6}")]
    private static partial Regex StampPattern();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex DailyName();
}

/// <summary>
/// The settings a backup carries: only what makes sense on another PC (the MIDI map, transitions,
/// the new-song look, timers…), never what belongs to this one (its screens, devices, library
/// folder, window places, the network API). Port of the Mac's BackupSettings: the app names the
/// keys that may travel, as the Mac's <c>portableKeys</c> does, and a restore stages them to apply
/// at the next launch, so nothing changes under the running show.
/// </summary>
public static class BackupSettings
{
    /// <summary>A settings file's JSON object with only <paramref name="keys"/> in it (null when it isn't a JSON object).</summary>
    public static byte[]? Portable(byte[] settingsJson, IReadOnlySet<string> keys)
    {
        if (Parse(settingsJson) is not { } settings) return null;
        var portable = new System.Text.Json.Nodes.JsonObject();
        foreach (var (key, value) in settings)
            if (keys.Contains(key)) portable[key] = value?.DeepClone();
        return System.Text.Encoding.UTF8.GetBytes(portable.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// This PC's settings with a backup's portable ones laid over them. Keep mine takes only the keys
    /// this PC hasn't set; the other policies take them all. Keys outside <paramref name="keys"/> are
    /// never taken, whatever the backup holds. Null when either isn't a JSON object.
    /// </summary>
    public static byte[]? Apply(byte[] current, byte[] fromBackup, IReadOnlySet<string> keys, BackupPolicy policy)
    {
        if (Parse(current) is not { } settings || Parse(fromBackup) is not { } incoming) return null;
        foreach (var (key, value) in incoming)
            if (keys.Contains(key) && (policy != BackupPolicy.KeepMine || !settings.ContainsKey(key)))
                settings[key] = value?.DeepClone();
        return System.Text.Encoding.UTF8.GetBytes(settings.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private static System.Text.Json.Nodes.JsonObject? Parse(byte[] json)
    {
        try { return System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
