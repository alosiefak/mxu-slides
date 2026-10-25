using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MxU.Slides.Core.Diagnostics;

/// <summary>
/// Which files go into a "Report a problem" bundle: the last week's logs, hang reports and crash
/// reports, newest first, until 25 MB. And which old diagnostics files to throw away (past two
/// weeks). Port of the Mac's DiagnosticsBundle.swift; Windows' crash reports are Windows Error
/// Reporting's ("AppCrash_MxUSlides.exe_…") rather than macOS's ".ips" files.
/// </summary>
public static class DiagnosticsBundle
{
    public sealed record Candidate(string Path, DateTimeOffset Modified, long Bytes)
    {
        public string Name => System.IO.Path.GetFileName(Path);
    }

    public const int ReportWindowDays = 7;
    public const int RetentionDays = 14;
    public const long ByteCap = 25L * 1024 * 1024;

    /// <summary>The newest files within the window that fit under the cap; a file too big to fit is skipped, smaller ones still go in.</summary>
    public static IReadOnlyList<Candidate> Selection(IEnumerable<Candidate> candidates, DateTimeOffset now, int windowDays = ReportWindowDays, long byteCap = ByteCap)
    {
        var since = now.AddDays(-windowDays);
        long total = 0;
        var chosen = new List<Candidate>();
        foreach (var candidate in candidates.Where(c => c.Modified >= since).OrderByDescending(c => c.Modified))
        {
            if (total + candidate.Bytes > byteCap) continue;
            total += candidate.Bytes;
            chosen.Add(candidate);
        }
        return chosen;
    }

    public static IReadOnlyList<Candidate> Expired(IEnumerable<Candidate> candidates, DateTimeOffset now, int retentionDays = RetentionDays) =>
        [.. candidates.Where(c => c.Modified < now.AddDays(-retentionDays))];

    /// <summary>
    /// Windows Error Reporting's record of this app crashing or hanging: a folder named
    /// "AppCrash_MxUSlides.exe_…" or "AppHang_MxUSlides.exe_…" holding a Report.wer, or a crash
    /// dump "MxUSlides.exe.1234.dmp".
    /// </summary>
    public static bool IsCrashReport(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower.StartsWith("appcrash_mxuslides.exe", StringComparison.Ordinal)
            || lower.StartsWith("apphang_mxuslides.exe", StringComparison.Ordinal)
            || (lower.StartsWith("mxuslides.exe.", StringComparison.Ordinal) && lower.EndsWith(".dmp", StringComparison.Ordinal));
    }

    /// <summary>This app's crash reports since the previous launch (none on a first launch: there was no run to blame).</summary>
    public static IReadOnlyList<Candidate> CrashReports(IEnumerable<Candidate> candidates, DateTimeOffset? previousLaunch) =>
        previousLaunch is { } since ? [.. candidates.Where(c => IsCrashReport(c.Name) && c.Modified > since)] : [];

    /// <summary>The files in a folder (none when it's missing), and optionally its subfolders'.</summary>
    public static IReadOnlyList<Candidate> Candidates(string folder, bool recursive = false)
    {
        if (!Directory.Exists(folder)) return [];
        try
        {
            return [.. new DirectoryInfo(folder).EnumerateFiles("*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .Select(f => new Candidate(f.FullName, f.LastWriteTime, f.Length))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>
/// Takes secrets out of what goes into a problem report: the app's API keys ("mxu_…"), bearer
/// tokens, stream keys (the end of an RTMP address), passwords and tokens in web addresses and
/// settings, and the Windows user's own name in file paths. The secrets themselves are kept
/// encrypted elsewhere (DPAPI), so they shouldn't be in the logs or settings at all; this is the
/// belt to that pair of braces. Windows only (the Mac's report holds no settings).
/// </summary>
public static partial class SecretScrubber
{
    public const string Removed = "[removed]";

    /// <summary>Scrubs log text, line by line.</summary>
    public static string ScrubText(string text, string? userName = null)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = ApiKey().Replace(text, "mxu_" + Removed);
        text = Bearer().Replace(text, m => m.Groups[1].Value + Removed);
        text = Rtmp().Replace(text, m => m.Groups[1].Value + Removed);
        text = QuerySecret().Replace(text, m => m.Groups[1].Value + Removed);
        text = NamedSecret().Replace(text, m => m.Groups[1].Value + m.Groups[2].Value + Removed);
        if (!string.IsNullOrEmpty(userName) && userName.Length >= 2)
            text = Regex.Replace(text, @"(?i)(\\Users\\)" + Regex.Escape(userName) + @"(?=\\|$|[\s""'])", "${1}[user]");
        return text;
    }

    /// <summary>
    /// Scrubs a JSON file (settings.json and friends): any property whose name says it's a secret
    /// loses its value, and every string left is scrubbed as text. Returns null when it isn't JSON.
    /// </summary>
    public static string? ScrubJson(string json, string? userName = null)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { return null; }
        if (root is null) return null;
        Walk(root, userName);
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>True for a property name that holds a secret ("Password", "apiKey", "StreamKey", "token"), but not a song's "MusicKey" or a hot key.</summary>
    public static bool IsSecretName(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower is "key" or "pat" or "pin") return true;
        return lower.Contains("password") || lower.Contains("passwd") || lower.Contains("secret") || lower.Contains("token")
            || lower.Contains("credential") || lower.Contains("bearer") || lower.Contains("apikey") || lower.Contains("api_key")
            || lower.Contains("streamkey") || lower.Contains("stream_key") || lower.Contains("privatekey") || lower.EndsWith("pin", StringComparison.Ordinal);
    }

    private static void Walk(JsonNode node, string? userName)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    if (IsSecretName(key) && obj[key] is JsonValue) obj[key] = Removed;
                    else if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text)) obj[key] = ScrubText(text, userName);
                    else if (obj[key] is { } child) Walk(child, userName);
                }
                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue value && value.TryGetValue<string>(out var text)) array[i] = ScrubText(text, userName);
                    else if (array[i] is { } child) Walk(child, userName);
                }
                break;
        }
    }

    [GeneratedRegex(@"mxu_[A-Za-z0-9_\-]{6,}")]
    private static partial Regex ApiKey();

    [GeneratedRegex(@"(?i)(bearer\s+)[A-Za-z0-9._\-+/=]{4,}")]
    private static partial Regex Bearer();

    // rtmp://a.rtmp.youtube.com/live2/<the stream key>: the key is the address's last part.
    [GeneratedRegex(@"(?i)(rtmps?://[^/\s""']+/[^/\s""']+/)[^\s""']+")]
    private static partial Regex Rtmp();

    [GeneratedRegex(@"(?i)([?&](?:key|token|access_token|api[_-]?key|password|pass|secret|sig|signature|auth|pat|stream_?key)=)[^&\s""']+")]
    private static partial Regex QuerySecret();

    [GeneratedRegex(@"(?i)\b(password|passwd|secret|token|api[_-]?key|stream[_ ]?key)(\s*[:=]\s*[""']?)[^""',\s}&]+")]
    private static partial Regex NamedSecret();
}
