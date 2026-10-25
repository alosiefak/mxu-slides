using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MxU.Slides.MotionTemplates.AfterEffects;

/// <summary>What to render: one composition of a project to one file.</summary>
/// <param name="OutputModule">
/// The output module template, which picks the file format. After Effects 2023 and later have
/// H.264 built in (<see cref="Aerender.H264"/>), which Windows plays; older ones render "Lossless"
/// to an AVI, which the app can turn into H.264 when it's imported (Phase 5's Transcode).
/// </param>
/// <param name="StartFrame">First frame to render, or null for the composition's work area.</param>
public sealed record AerenderRequest(string Project, string Composition, string Output,
    string OutputModule = Aerender.H264, string RenderSettings = "Best Settings",
    int? StartFrame = null, int? EndFrame = null, bool Sound = false);

/// <summary>How a render is going: frames done of how many, the composition, and any errors After Effects reported.</summary>
public sealed record AerenderStatus(int FramesDone, int? TotalFrames, string? Composition, IReadOnlyList<string> Errors, string? LastLine)
{
    public double? Fraction => TotalFrames is > 0 ? Math.Clamp((double)FramesDone / TotalFrames.Value, 0, 1) : null;
}

/// <summary>How a render ended.</summary>
public sealed record AerenderResult(int ExitCode, bool Cancelled, AerenderStatus Status, IReadOnlyList<string> Log)
{
    public bool Succeeded => !Cancelled && ExitCode == 0 && Status.Errors.Count == 0;

    /// <summary>A line for the operator saying why it failed: After Effects' own error, else the end of its log.</summary>
    public string Problem => Cancelled ? "Stopped." : Status.Errors.FirstOrDefault()
        ?? Log.LastOrDefault(l => l.Trim().Length > 0)?.Trim()
        ?? $"aerender stopped with exit code {ExitCode}";
}

/// <summary>
/// Running After Effects' command-line renderer, aerender.exe. It's Adobe's supported way to render
/// a project from outside: <c>aerender -project p.aep -comp "Name Strap" -output out.mp4</c>. It opens
/// After Effects without its window, renders, and writes a line per frame, which is how the bridge
/// shows progress. The project is opened with "don't save changes" on closing, and the bridge always
/// gives it a copy anyway, so the user's own .aep is never touched.
/// </summary>
public static partial class Aerender
{
    /// <summary>After Effects 2023 and later's built-in H.264 output module.</summary>
    public const string H264 = "H.264 - Match Render Settings - 15 Mbps";

    /// <summary>The arguments for <paramref name="request"/>, one per entry (Windows quotes each as it starts the program).</summary>
    public static IReadOnlyList<string> Arguments(AerenderRequest request)
    {
        var args = new List<string>
        {
            "-project", request.Project,
            "-comp", request.Composition,
            "-output", request.Output,
            "-RStemplate", request.RenderSettings,
            "-OMtemplate", request.OutputModule,
        };
        if (request.StartFrame is { } start) args.AddRange(["-s", start.ToString(CultureInfo.InvariantCulture)]);
        if (request.EndFrame is { } end) args.AddRange(["-e", end.ToString(CultureInfo.InvariantCulture)]);
        args.AddRange(["-sound", request.Sound ? "ON" : "OFF", "-v", "ERRORS_AND_PROGRESS", "-close", "DO_NOT_SAVE_CHANGES"]);
        return args;
    }

    /// <summary>
    /// Runs aerender (<paramref name="executable"/>) with <paramref name="arguments"/> and waits for it,
    /// blocking the calling thread (use one of your own). Its output is read on two threads of their
    /// own, never the shared pool. Cancelling stops it and the After Effects it started
    /// (the whole process tree); the caller removes any half-written file.
    /// </summary>
    public static AerenderResult Run(string executable, IReadOnlyList<string> arguments, IProgress<AerenderStatus>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // aerender writes in the PC's code page; anything it can't map shows as "?" in the log, which only the log sees.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? "",
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        var parser = new AerenderParser();
        var log = new List<string>();
        var gate = new Lock();
        using var process = Process.Start(start) ?? throw new IOException($"{Path.GetFileName(executable)} didn't start");
        void Read(StreamReader reader)
        {
            while (reader.ReadLine() is { } line)
            {
                AerenderStatus? changed;
                lock (gate)
                {
                    log.Add(line);
                    if (log.Count > 400) log.RemoveAt(0);
                    changed = parser.Feed(line) ? parser.Status : null;
                }
                if (changed is not null) progress?.Report(changed);
            }
        }
        var output = new Thread(() => Read(process.StandardOutput)) { IsBackground = true, Name = "aerender output" };
        var errors = new Thread(() => Read(process.StandardError)) { IsBackground = true, Name = "aerender errors" };
        output.Start();
        errors.Start();

        var cancelled = false;
        using (cancellationToken.Register(() =>
        {
            cancelled = true;
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }))
        {
            process.WaitForExit();
        }
        output.Join(TimeSpan.FromSeconds(5));
        errors.Join(TimeSpan.FromSeconds(5));
        lock (gate) return new AerenderResult(process.ExitCode, cancelled || cancellationToken.IsCancellationRequested, parser.Status, [.. log]);
    }
}

/// <summary>
/// Reads aerender's report line by line. It looks like this (After Effects 2022 and later):
/// <code>
/// PROGRESS:  25/10/2026 15:02:11: Starting composition “Name Strap”.
/// PROGRESS:  Duration: 0:00:05:00
/// PROGRESS:  Frame Rate: 30.00 (comp)
/// PROGRESS:  0:00:00:00 (1): 0 Seconds
/// PROGRESS:  0:00:00:01 (2): 0 Seconds
/// …
/// PROGRESS:  25/10/2026 15:02:30: Finished composition “Name Strap”.
/// </code>
/// The number in brackets is how many frames are done. The total comes from the duration and the
/// frame rate (a timecode, with ";" between the parts for drop-frame rates, or a plain frame count
/// when the project counts in frames), or from the start and end frames. Errors are the lines
/// After Effects marks "ERROR".
/// </summary>
public sealed partial class AerenderParser
{
    private readonly List<string> errors = [];
    private string? durationText;
    private string? startText;
    private string? endText;
    private double? frameRate;
    private int framesDone;
    private string? composition;
    private string? lastLine;

    public bool Finished { get; private set; }

    public AerenderStatus Status => new(framesDone, TotalFrames, composition, [.. errors], lastLine);

    public int? TotalFrames
    {
        get
        {
            if (durationText is not null && Frames(durationText) is { } duration && duration > 0) return duration;
            if (startText is not null && endText is not null && Frames(startText) is { } s && Frames(endText) is { } e && e >= s) return e - s + 1;
            return null;
        }
    }

    /// <summary>Takes one line of output; true when the progress shown should change.</summary>
    public bool Feed(string line)
    {
        var text = line.Trim();
        if (text.Length == 0) return false;
        lastLine = text;
        if (text.Contains("ERROR", StringComparison.Ordinal) && !text.Contains("ERRORS_AND_PROGRESS", StringComparison.Ordinal))
        {
            errors.Add(ErrorPrefix().Replace(text, "").Trim());
            return true;
        }
        var body = text.StartsWith("PROGRESS:", StringComparison.OrdinalIgnoreCase) ? text["PROGRESS:".Length..].Trim() : text;
        if (FramePattern().Match(body) is { Success: true } frame)
        {
            framesDone = int.Parse(frame.Groups["count"].Value, CultureInfo.InvariantCulture);
            return true;
        }
        if (FieldPattern().Match(body) is { Success: true } field)
        {
            var value = field.Groups["value"].Value.Trim();
            switch (field.Groups["name"].Value.ToLowerInvariant())
            {
                case "duration": durationText = value; break;
                case "start": startText = value; break;
                case "end": endText = value; break;
                case "frame rate":
                    if (double.TryParse(value.Split(' ')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) && rate > 0) frameRate = rate;
                    break;
            }
            return true;
        }
        if (CompositionPattern().Match(body) is { Success: true } comp)
        {
            if (comp.Groups["verb"].Value.Equals("Finished", StringComparison.OrdinalIgnoreCase)) Finished = true;
            else composition = comp.Groups["name"].Value;
            return true;
        }
        return false;
    }

    /// <summary>A timecode (h:mm:ss:ff, or with ";" for drop-frame) or a frame count, in frames; null when it can't be read yet.</summary>
    internal int? Frames(string text)
    {
        var value = text.Split(' ')[0];
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var plain)) return plain;
        var parts = value.Split(':', ';');
        if (parts.Length != 4 || frameRate is not { } rate) return null;
        if (!parts.All(p => int.TryParse(p, out _))) return null;
        var n = parts.Select(int.Parse).ToArray();
        var fps = (int)Math.Round(rate);
        return ((n[0] * 60 + n[1]) * 60 + n[2]) * fps + n[3];
    }

    [GeneratedRegex(@"^(?<tc>\d+([:;]\d+){3}|\d+)\s+\((?<count>\d+)\)\s*:")]
    private static partial Regex FramePattern();

    [GeneratedRegex(@"^(?<name>Duration|Start|End|Frame Rate)\s*:\s*(?<value>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex FieldPattern();

    [GeneratedRegex(@"(?<verb>Starting|Finished) composition\s*[“""'](?<name>.*?)[”""']", RegexOptions.IgnoreCase)]
    private static partial Regex CompositionPattern();

    [GeneratedRegex(@"^(aerender\s+)?ERROR\s*:?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorPrefix();
}
