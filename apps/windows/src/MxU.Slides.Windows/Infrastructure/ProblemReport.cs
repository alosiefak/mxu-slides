using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MxU.Slides.Core.Diagnostics;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>
/// "Report a problem": a zip in Downloads holding the note, this PC's summary and health checks,
/// the last week of logs and hang reports, Windows' crash records for this app, and the settings
/// files, all with secrets taken out (<see cref="SecretScrubber"/>). The app never sends it
/// anywhere: the user decides who gets it. Port of the Mac's FeedbackController (buildZip,
/// saveLocally), with Windows Error Reporting's records in place of macOS crash reports.
/// </summary>
internal static class ProblemReport
{
    /// <summary>The settings files that go in (scrubbed). Keys, tokens and passwords are kept elsewhere, encrypted, and never go.</summary>
    private static readonly string[] SettingsFiles = ["settings.json", "production.json", "video-inputs.json", "audio-inputs.json", "timers.json", "tracks-setup.json"];

    /// <summary>
    /// Builds the zip (on the calling thread: call it off the UI thread) in Downloads, or in
    /// <paramref name="into"/> (the self-test's own folder), and returns where it went.
    /// </summary>
    public static string Build(string message, SystemInfo.Report system, IReadOnlyList<HealthCheck> checks, DateTimeOffset now, string? into = null)
    {
        var user = Environment.UserName;
        var name = $"MxU Slides Diagnostics {now:yyyy-MM-dd HHmmss}.zip";
        var folder = into ?? DownloadsFolder();
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, name);
        var partial = destination + ".partial";
        var included = new List<string>();
        using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            var logs = DiagnosticsBundle.Candidates(AppPaths.LogDirectory);
            var crashes = CrashReports();
            foreach (var candidate in DiagnosticsBundle.Selection(logs.Concat(crashes), now))
            {
                var entry = crashes.Contains(candidate) ? $"crash reports/{Path.GetFileName(Path.GetDirectoryName(candidate.Path))}/{candidate.Name}" : $"logs/{candidate.Name}";
                if (ReadShared(candidate.Path) is not { } text) continue;
                Add(zip, entry, SecretScrubber.ScrubText(text, user));
                included.Add(entry);
            }
            foreach (var settings in SettingsFiles)
            {
                var path = Path.Combine(AppPaths.DataRoot, settings);
                if (ReadShared(path) is not { } json) continue;
                Add(zip, $"settings/{settings}", SecretScrubber.ScrubJson(json, user) ?? "(not readable as JSON, left out)");
                included.Add($"settings/{settings}");
            }
            var health = string.Join(Environment.NewLine, checks.Select(c => $"{c.Status,-8} {c.Area}: {c.Summary}{(c.Advice is { } advice ? "  →  " + advice : "")}"));
            Add(zip, "health checks.txt", health);
            var report = new
            {
                message,
                createdAt = now.ToString("O"),
                systemInfo = system.Lines.Select(l => new { area = l.Area, name = l.Name, value = SecretScrubber.ScrubText(l.Value, user) }),
                files = included,
                note = "Made by MxU Slides for Windows on request. Nothing was sent anywhere; secrets (keys, tokens, passwords, stream keys) were removed.",
            };
            Add(zip, "report.json", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        File.Move(partial, destination, overwrite: true);
        Log.Info($"Saved a problem report: {destination}");
        return destination;
    }

    /// <summary>
    /// Windows Error Reporting's records of this app crashing or hanging (Report.wer in an
    /// AppCrash_MxUSlides.exe_… folder), from the queue and the archive.
    /// </summary>
    public static IReadOnlyList<DiagnosticsBundle.Candidate> CrashReports()
    {
        var wer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "WER");
        var found = new List<DiagnosticsBundle.Candidate>();
        foreach (var place in new[] { "ReportArchive", "ReportQueue" })
        {
            var root = Path.Combine(wer, place);
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var folder in Directory.EnumerateDirectories(root).Where(d => DiagnosticsBundle.IsCrashReport(Path.GetFileName(d))))
                    found.AddRange(DiagnosticsBundle.Candidates(folder).Where(c => c.Name.Equals("Report.wer", StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return found;
    }

    /// <summary>Opens Explorer with the file picked out, as the Mac reveals it in Finder.</summary>
    public static void Reveal(string path)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Log.Warn($"Couldn't show {path}: {ex.Message}"); }
    }

    /// <summary>The user's Downloads folder (wherever they've moved it), else Downloads in their profile.</summary>
    public static string DownloadsFolder()
    {
        var downloads = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        if (SHGetKnownFolderPath(downloads, 0, IntPtr.Zero, out var path) == 0 && path is { Length: > 0 }) return path;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    /// <summary>A file's text, even while the app is writing it (today's log is open).</summary>
    private static string? ReadShared(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Add(ZipArchive zip, string name, string text)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        stream.Write(Encoding.UTF8.GetBytes(text));
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, [MarshalAs(UnmanagedType.LPWStr)] out string path);
}
