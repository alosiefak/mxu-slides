using System.Diagnostics;
using MxU.Slides.Core.Diagnostics;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>
/// "Check this PC…": runs the app's own self-test in a separate copy of the app, on a throwaway
/// library, and reads its report back, so a volunteer can tell before Sunday whether a projection
/// laptop is ready. The rules for reading the report are Core's (<see cref="PcCheck"/>).
/// <list type="bullet">
/// <item><b>Never the real library.</b> The copy is started with MXU_SLIDES_DATA and MXU_SLIDES_LOCAL
/// set to new folders in the temporary folder (for it alone, not for this app), exactly as
/// scripts/self-test.ps1 does, and MXU_SLIDES_SELFTEST_SANDBOX=1 to say so. The self-test refuses
/// to start without them (<see cref="SelfTestSandbox"/>).</item>
/// <item><b>Never during a show.</b> It refuses while anything is on an output (<see cref="Blocked"/>).</item>
/// <item><b>One heavy test at a time.</b> It waits for the same lock the scripts use
/// (scripts/test-lock.ps1, "Local\MxU Slides heavy tests"), so it never runs beside another test run.</item>
/// </list>
/// Windows only.
/// </summary>
internal sealed class PcCheckRunner
{
    /// <summary>The scripts' lock's name (scripts/test-lock.ps1): one heavy test run on a PC at a time.</summary>
    public const string HeavyTestLock = @"Local\MxU Slides heavy tests";

    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(45);

    public sealed record Progress(string Phase, IReadOnlyList<PcCheck.Line> Lines, string? LastLine);

    public sealed record Result(string Folder, string Report, IReadOnlyList<PcCheck.Line> Lines, IReadOnlyList<PcCheck.Area> Areas, bool Passed, bool Finished);

    /// <summary>Why it can't run now (something on an output, music, a recording or stream), or null.</summary>
    public static string? Blocked(ViewModels.MainViewModel model) =>
        !model.Show.IsEmpty ? "Something is on the screens. Clear them first (F1): the check puts its own test pictures on every output."
        : model.IsMusicPlaying ? "Music is playing. Stop it first: the check plays its own test sounds."
        : model.BroadcastButtonText.StartsWith("REC", StringComparison.Ordinal) || model.BroadcastButtonText.StartsWith("LIVE", StringComparison.Ordinal)
            ? "A recording or stream is running. Stop it first: the check records and streams its own test."
        : null;

    /// <summary>
    /// The environment the copy runs in: this app's own, with its data and local folders pointed at
    /// throwaway ones. Only the copy gets these: this app's own environment is never changed.
    /// </summary>
    public static Dictionary<string, string?> ChildEnvironment(string sandbox, IReadOnlyList<string>? sections) => new()
    {
        ["MXU_SLIDES_DATA"] = Path.Combine(sandbox, "data"),
        ["MXU_SLIDES_LOCAL"] = Path.Combine(sandbox, "local"),
        [SelfTestSandbox.MarkerVariable] = "1",
        ["MXU_SLIDES_SELFTEST_ONLY"] = sections is { Count: > 0 } ? string.Join(',', sections) : null,
    };

    /// <summary>Runs the check (quick: the basics and the device sections; full: everything) and reads the result.</summary>
    public static async Task<Result> RunAsync(bool quick, IProgress<Progress> progress, CancellationToken cancel)
    {
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var root = Path.Combine(Path.GetTempPath(), $"MxU Slides PC check {stamp}");
        var pictures = Directory.CreateDirectory(Path.Combine(root, "report")).FullName;
        var sandbox = Path.Combine(root, "throwaway library");
        Directory.CreateDirectory(Path.Combine(sandbox, "data"));
        Directory.CreateDirectory(Path.Combine(sandbox, "local"));

        using var turn = new LockTurn();
        if (!turn.TryTake())
        {
            progress.Report(new Progress("Waiting for another test run on this PC to finish…", [], null));
            await turn.TakeAsync(cancel);
        }

        progress.Report(new Progress("Starting a separate copy of MxU Slides on a throwaway library…", [], null));
        var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("The app's own file can't be found."))
        {
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        start.ArgumentList.Add("--self-test");
        start.ArgumentList.Add(pictures);
        foreach (var (name, value) in ChildEnvironment(sandbox, quick ? PcCheck.QuickSections : null))
        {
            if (value is null) start.Environment.Remove(name);
            else start.Environment[name] = value;
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The check couldn't be started.");
        Log.Info($"Check this PC: started the self-test (process {process.Id}) in {root}");
        var partial = Path.Combine(pictures, "report.partial.txt");
        var finalReport = Path.Combine(pictures, "report.txt");
        var deadline = DateTime.Now + Limit;
        try
        {
            while (!process.HasExited)
            {
                if (DateTime.Now > deadline) break;
                await Task.Delay(TimeSpan.FromSeconds(1), cancel);
                if (ReadShared(partial) is { } text)
                {
                    var lines = PcCheck.Parse(text);
                    progress.Report(new Progress($"Checking… {lines.Count(l => l.Result == PcCheck.Result.Pass)} passed, {lines.Count(l => l.Result == PcCheck.Result.Fail)} failed so far",
                        lines, lines.LastOrDefault()?.Text));
                }
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                Log.Warn("Check this PC: stopped the self-test (cancelled or out of time)");
            }
        }

        var report = ReadShared(finalReport) ?? ReadShared(partial) ?? "The check stopped before it wrote its report.";
        // Keep the copy's log with the pictures, then throw the throwaway library away.
        try
        {
            var logs = Path.Combine(sandbox, "local", "Logs");
            if (Directory.Exists(logs))
                foreach (var file in Directory.EnumerateFiles(logs))
                    File.Copy(file, Path.Combine(Directory.CreateDirectory(Path.Combine(pictures, "Logs")).FullName, Path.GetFileName(file)), overwrite: true);
            Directory.Delete(sandbox, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Check this PC: couldn't tidy up {sandbox}: {ex.Message}");
        }
        var parsed = PcCheck.Parse(report);
        return new Result(pictures, report, parsed, PcCheck.Summarize(parsed), PcCheck.Passed(report), PcCheck.Finished(report));
    }

    private static string? ReadShared(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// A turn on the PC-wide test lock. A named mutex belongs to the thread that took it, and must
    /// be let go by that same thread, so one thread of its own takes it, holds it while the check
    /// runs, and lets it go at the end.
    /// </summary>
    private sealed class LockTurn : IDisposable
    {
        private readonly ManualResetEventSlim taken = new();
        private readonly ManualResetEventSlim release = new();
        private readonly Thread holder;
        private volatile bool stop;

        public LockTurn()
        {
            holder = new Thread(Hold) { IsBackground = true, Name = "MxU PC check lock" };
            holder.Start();
        }

        private void Hold()
        {
            using var mutex = new Mutex(false, HeavyTestLock);
            var held = false;
            while (!held && !stop)
            {
                try { held = mutex.WaitOne(TimeSpan.FromMilliseconds(250)); }
                catch (AbandonedMutexException) { held = true; } // a run that crashed: Windows hands it on
            }
            if (!held) return;
            taken.Set();
            release.Wait();
            mutex.ReleaseMutex();
        }

        /// <summary>True when the lock was free (taken at once).</summary>
        public bool TryTake() => taken.Wait(TimeSpan.FromMilliseconds(600));

        public async Task TakeAsync(CancellationToken cancel)
        {
            while (!taken.IsSet)
            {
                cancel.ThrowIfCancellationRequested();
                await Task.Delay(500, cancel);
            }
        }

        public void Dispose()
        {
            stop = true;
            release.Set();
            holder.Join(TimeSpan.FromSeconds(2));
            taken.Dispose();
            release.Dispose();
        }
    }
}
