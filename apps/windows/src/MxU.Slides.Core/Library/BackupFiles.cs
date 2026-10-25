using System.Globalization;
using System.Text.RegularExpressions;

namespace MxU.Slides.Core.Library;

/// <summary>How often automatic backups are made. Per PC (settings.json): another PC may back up the same songs differently.</summary>
public enum BackupFrequency
{
    Off,
    EveryLaunch,
    Daily,
    Weekly,
}

/// <summary>
/// The backups in a folder, and when the next automatic one is due. Automatic backups are named
/// "MxU Slides backup 2026-10-25 0930.mxubackup"; safety copies taken before a restore are
/// "Before restore 2026-10-25 1012.mxubackup"; anything else in the folder (a backup someone saved
/// there by hand) is listed but never pruned. The old daily copies (folders named
/// "2026-10-24") are listed too, so they can still be restored. Windows only: the Mac makes
/// backups only when asked (File › Export Library Backup…).
/// </summary>
public static partial class BackupFiles
{
    public const string AutomaticPrefix = "MxU Slides backup ";
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

    public static string AutomaticName(DateTimeOffset when) => AutomaticPrefix + Stamp(when) + "." + LibraryBackup.FileExtension;

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
            entries.Add(new Entry(file.FullName, kind, StampIn(file.Name) ?? file.LastWriteTime, file.Length));
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
    /// Whether an automatic backup is due: every launch (at launch only), once a calendar day, or
    /// once seven days have passed. Never when they're off.
    /// </summary>
    public static bool IsDue(BackupFrequency frequency, DateTimeOffset? last, DateTimeOffset now, bool atLaunch) => frequency switch
    {
        BackupFrequency.EveryLaunch => atLaunch,
        BackupFrequency.Daily => last is not { } l || l.LocalDateTime.Date < now.LocalDateTime.Date,
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
    /// The automatic backups past the newest <paramref name="keep"/>, and the safety copies past the
    /// newest <see cref="SafetyCopiesKept"/>: the ones to delete. Backups saved by hand are never in it.
    /// </summary>
    public static IReadOnlyList<Entry> Expired(IEnumerable<Entry> entries, int keep)
    {
        var all = entries.OrderByDescending(e => e.Created).ToList();
        return
        [
            .. all.Where(e => e.Kind == Kind.Automatic).Skip(Math.Max(1, keep)),
            .. all.Where(e => e.Kind == Kind.SafetyCopy).Skip(SafetyCopiesKept),
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
