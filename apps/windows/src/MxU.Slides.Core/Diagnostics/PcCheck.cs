namespace MxU.Slides.Core.Diagnostics;

/// <summary>
/// "Check this PC…": the app's own self-test run in a separate copy of the app, on a throwaway
/// library, and its report turned into a list a volunteer can read. This is the reading part: the
/// report's lines, and one line per area (screens; the graphics card and transitions; capture and
/// streaming; sound; NDI and DeckLink) saying whether this laptop is ready. Windows only.
/// </summary>
public static class PcCheck
{
    public enum Result
    {
        Pass,
        Fail,
        Skip,
        Note,
    }

    public sealed record Line(Result Result, string Text);

    public enum AreaState
    {
        Ready,
        NotAvailable,
        Problem,
        NotChecked,
    }

    public sealed record Area(string Name, AreaState State, string Summary, IReadOnlyList<Line> Lines);

    /// <summary>The self-test sections a quick check runs (the basics always run first): about three minutes.</summary>
    public static IReadOnlyList<string> QuickSections { get; } = ["Screens", "Effects", "Transitions", "Recording", "Music", "Sound"];

    /// <summary>
    /// Each area, and the words in a check's line that put it there. A check in no area still
    /// counts towards the overall result.
    /// </summary>
    private static readonly (string Name, string[] Words)[] Areas =
    [
        ("NDI and DeckLink", ["ndi", "decklink"]),
        ("Capture and streaming", ["record", "stream", "program feed", "capture", "rtmp", "hls", "encoder", "mp4", "broadcast"]),
        ("Sound", ["sound", "audio", "music", "playlist", "song plays", "speaker"]),
        ("Graphics card and transitions", ["transition", "shader", "graphics", "effect", "gpu", "60 fps", "dissolve", "push", "keystone", "blur"]),
        ("Screens", ["screen", "display", "output", "audience", "stage", "projector", "preview window", "slice", "edge blend"]),
    ];

    public static IReadOnlyList<Line> Parse(string report)
    {
        var lines = new List<Line>();
        foreach (var raw in report.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            Result? result = line.StartsWith("PASS", StringComparison.Ordinal) ? Result.Pass
                : line.StartsWith("FAIL", StringComparison.Ordinal) || line.StartsWith("CRASH", StringComparison.Ordinal) ? Result.Fail
                : line.StartsWith("SKIP", StringComparison.Ordinal) ? Result.Skip
                : line.StartsWith("NOTE", StringComparison.Ordinal) ? Result.Note
                : null;
            if (result is { } r) lines.Add(new Line(r, line[(line.IndexOf(' ') is var space and > 0 ? space : 4)..].Trim()));
        }
        return lines;
    }

    /// <summary>True when the report says it finished and passed.</summary>
    public static bool Passed(string report) => report.Contains("RESULT: PASS", StringComparison.Ordinal);

    public static bool Finished(string report) => report.Contains("RESULT:", StringComparison.Ordinal);

    /// <summary>
    /// One line per area: Ready (every check there passed), Not available on this PC (all of them
    /// skipped: no such device), Problem (any failed), or Not checked (the run had none).
    /// </summary>
    public static IReadOnlyList<Area> Summarize(IReadOnlyList<Line> lines)
    {
        var areas = new List<Area>();
        var claimed = new HashSet<Line>();
        foreach (var (name, words) in Areas)
        {
            var mine = lines.Where(l => l.Result != Result.Note && !claimed.Contains(l) && words.Any(w => l.Text.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
            claimed.UnionWith(mine);
            var failed = mine.Count(l => l.Result == Result.Fail);
            var passed = mine.Count(l => l.Result == Result.Pass);
            var skipped = mine.Where(l => l.Result == Result.Skip).ToList();
            areas.Add(mine.Count == 0
                ? new Area(name, AreaState.NotChecked, "Not checked in this run.", mine)
                : failed > 0
                    ? new Area(name, AreaState.Problem, $"{failed} of {mine.Count} checks failed: {mine.First(l => l.Result == Result.Fail).Text}", mine)
                    : passed == 0
                        ? new Area(name, AreaState.NotAvailable, $"Not available on this PC ({Reason(skipped[0].Text)}).", mine)
                        : new Area(name, AreaState.Ready,
                            skipped.Count == 0 ? $"Ready: {passed} checks passed." : $"Ready: {passed} checks passed; {skipped.Count} skipped ({Reason(skipped[0].Text)}).", mine));
        }
        return areas;
    }

    /// <summary>"NDI output (NDI runtime not installed)" → "NDI runtime not installed".</summary>
    private static string Reason(string skip)
    {
        var open = skip.LastIndexOf('(');
        return open >= 0 && skip.EndsWith(')') ? skip[(open + 1)..^1] : skip;
    }
}

/// <summary>
/// The self-test's safety rule: it runs only on a throwaway library. It drives the real app (makes
/// songs, deletes things, changes settings), so pointed at a church's real library it would make a
/// mess of it. The script (self-test.ps1) and Check this PC set <c>MXU_SLIDES_DATA</c> and
/// <c>MXU_SLIDES_LOCAL</c> to fresh folders, and say so with <c>MXU_SLIDES_SELFTEST_SANDBOX=1</c>.
/// Run by hand without them, it refuses, saying why.
/// </summary>
public static class SelfTestSandbox
{
    public const string MarkerVariable = "MXU_SLIDES_SELFTEST_SANDBOX";

    /// <summary>Null when it's safe to run; otherwise what's wrong.</summary>
    /// <param name="data">MXU_SLIDES_DATA (null when unset).</param>
    /// <param name="local">MXU_SLIDES_LOCAL.</param>
    /// <param name="marker">MXU_SLIDES_SELFTEST_SANDBOX.</param>
    /// <param name="realData">Where the real library and settings live (AppData\Roaming\MxU Slides).</param>
    /// <param name="realLocal">Where the real logs and caches live (AppData\Local\MxU Slides).</param>
    public static string? Problem(string? data, string? local, string? marker, string realData, string realLocal, string tempFolder)
    {
        const string how = "Run it with scripts/self-test.ps1, or from Diagnostics › Check this PC…, which use a throwaway library.";
        if (string.IsNullOrWhiteSpace(data) || string.IsNullOrWhiteSpace(local))
            return "The self-test only runs on a throwaway library: MXU_SLIDES_DATA and MXU_SLIDES_LOCAL aren't set, so it would use your real library. " + how;
        if (Same(data, realData) || Same(local, realLocal) || Same(data, realLocal) || Same(local, realData))
            return "The self-test only runs on a throwaway library, but MXU_SLIDES_DATA or MXU_SLIDES_LOCAL points at the real one. " + how;
        if (marker != "1" && !(Under(data, tempFolder) && Under(local, tempFolder)))
            return "The self-test only runs on a throwaway library in the temporary folder (or with MXU_SLIDES_SELFTEST_SANDBOX=1 set by the script). " + how;
        return null;
    }

    private static string Full(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');

    private static bool Same(string a, string b) => string.Equals(Full(a), Full(b), StringComparison.OrdinalIgnoreCase);

    private static bool Under(string path, string folder) =>
        Full(path).StartsWith(Full(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
