using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using MxU.Slides.Broadcast.Media;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.MotionTemplates;
using MxU.Slides.MotionTemplates.AfterEffects;
using MxU.Slides.MotionTemplates.Export;

namespace MxU.Slides.MotionTemplates.Tests;

// The After Effects bridge (Phase 11). After Effects isn't installed here, so these use a
// stand-in (tests/MxU.Slides.FakeAfterEffects), copied in as aerender.exe and AfterFX.exe in an
// install folder like Adobe's. It prints aerender's real progress lines and does what the
// scripts ask, so the command lines, progress, cancelling, the copy-only rule and the import are
// all tested for real, as processes.
public sealed class AfterEffectsTests : IDisposable
{
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "AfterEffects-" + Guid.NewGuid().ToString("N"));

    public AfterEffectsTests() => Directory.CreateDirectory(scratch);

    public void Dispose()
    {
        try { Directory.Delete(scratch, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>An After Effects install folder (Adobe's layout) whose aerender.exe and AfterFX.exe are the stand-in.</summary>
    private string FakeInstall(string version = "2026", bool withAfterFx = true, string? root = null)
    {
        var support = Path.Combine(root ?? Path.Combine(scratch, "Program Files", "Adobe"), $"Adobe After Effects {version}", "Support Files");
        Directory.CreateDirectory(support);
        var built = AppContext.BaseDirectory;
        foreach (var file in Directory.EnumerateFiles(built, "MxU.Slides.FakeAfterEffects.*").Where(f => !f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            File.Copy(file, Path.Combine(support, Path.GetFileName(file)), overwrite: true);
        var exe = Path.Combine(built, "MxU.Slides.FakeAfterEffects.exe");
        File.Copy(exe, Path.Combine(support, AfterEffectsInstall.AerenderName), overwrite: true);
        if (withAfterFx) File.Copy(exe, Path.Combine(support, AfterEffectsInstall.AfterFxName), overwrite: true);
        return Path.GetDirectoryName(support)!;
    }

    /// <summary>A stand-in project: the lines tell the stand-in what's in it (see its Program.cs).</summary>
    private string Project(string name, params string[] lines)
    {
        var path = Path.Combine(scratch, name);
        File.WriteAllLines(path, ["FAKE-AEP", $"log={LogPath}", .. lines]);
        return path;
    }

    private string LogPath => Path.Combine(scratch, "runs.log");

    /// <summary>Every run of the stand-in so far: which program, then its arguments.</summary>
    private List<string[]> Runs() => File.Exists(LogPath)
        ? [.. File.ReadAllLines(LogPath).Select(l => JsonSerializer.Deserialize<string[]>(l)!)]
        : [];

    // MARK: Finding it

    [Fact]
    public void InstallsAreFoundNewestFirst()
    {
        var adobe = Path.Combine(scratch, "Program Files", "Adobe");
        FakeInstall("2024", root: adobe);
        FakeInstall("2026", root: adobe);
        FakeInstall("(Beta)", withAfterFx: false, root: adobe);
        Directory.CreateDirectory(Path.Combine(adobe, "Adobe After Effects 2019")); // uninstalled: no aerender left
        Directory.CreateDirectory(Path.Combine(adobe, "Adobe Premiere Pro 2026", "Support Files"));

        var found = AfterEffectsInstall.Find([adobe, Path.Combine(scratch, "nowhere")]);
        Assert.Equal(["2026", "2024", "(Beta)"], found.Select(i => i.Version));
        Assert.Equal("After Effects 2026", found[0].Title);
        Assert.EndsWith(@"Support Files\aerender.exe", found[0].Aerender);
        Assert.EndsWith(@"Support Files\AfterFX.exe", found[0].AfterFx);
        Assert.Null(found[2].AfterFx);

        var picked = AfterEffectsInstall.FromAerender(found[1].Aerender);
        Assert.Equal(("2024", found[1].Folder), (picked!.Version, picked.Folder));
        Assert.Null(AfterEffectsInstall.FromAerender(Path.Combine(scratch, "missing.exe")));
        Assert.Contains(AfterEffectsInstall.UsualRoots(), r => r.EndsWith(@"\Adobe", StringComparison.Ordinal));
    }

    // MARK: The command line

    [Fact]
    public void TheCommandLineIsAerendersOwn()
    {
        var args = Aerender.Arguments(new AerenderRequest(@"C:\Work\copy\Sunday Graphics.aep", "Name Strap", @"C:\Work\out\Name Strap.mp4"));
        Assert.Equal(
        [
            "-project", @"C:\Work\copy\Sunday Graphics.aep",
            "-comp", "Name Strap",
            "-output", @"C:\Work\out\Name Strap.mp4",
            "-RStemplate", "Best Settings",
            "-OMtemplate", "H.264 - Match Render Settings - 15 Mbps",
            "-sound", "OFF", "-v", "ERRORS_AND_PROGRESS", "-close", "DO_NOT_SAVE_CHANGES",
        ], args);
        var trimmed = Aerender.Arguments(new AerenderRequest("p.aep", "c", "o.avi", "Lossless", StartFrame: 10, EndFrame: 99, Sound: true));
        Assert.Equal(["-s", "10", "-e", "99", "-sound", "ON"], trimmed.Skip(10).Take(6));
        Assert.Contains("Lossless", trimmed);
    }

    // MARK: Progress

    [Fact]
    public void ProgressIsReadFromAerendersLines()
    {
        var parser = new AerenderParser();
        foreach (var line in new[]
        {
            "aerender version 23.2x8",
            "PROGRESS:  Launching After Effects...",
            "PROGRESS:  25/10/2026 15:02:11: Starting composition \u201cName Strap\u201d.",
            "PROGRESS:  Start: 0:00:00:00",
            "PROGRESS:  End: 0:00:04:29",
            "PROGRESS:  Duration: 0:00:05:00",
            "PROGRESS:  Frame Rate: 30.00 (comp)",
            "PROGRESS:  0:00:00:00 (1): 0 Seconds",
            "PROGRESS:  0:00:01:14 (45): 2 Seconds",
        }) parser.Feed(line);
        Assert.Equal((45, 150, "Name Strap"), (parser.Status.FramesDone, parser.Status.TotalFrames, parser.Status.Composition));
        Assert.Equal(0.3, parser.Status.Fraction!.Value, 6);
        Assert.False(parser.Finished);
        parser.Feed("PROGRESS:  25/10/2026 15:02:30: Finished composition \u201cName Strap\u201d.");
        Assert.True(parser.Finished);
        Assert.Empty(parser.Status.Errors);
    }

    [Fact]
    public void DropFrameTimecodesFrameCountsAndErrorsAreReadToo()
    {
        var dropFrame = new AerenderParser();
        dropFrame.Feed("PROGRESS:  Frame Rate: 29.97 (comp)");
        dropFrame.Feed("PROGRESS:  Duration: 0;00;10;00");
        dropFrame.Feed("PROGRESS:  0;00;00;12 (13): 0 Seconds");
        Assert.Equal((13, 300), (dropFrame.Status.FramesDone, dropFrame.Status.TotalFrames));

        // A project set to count in frames: no timecodes at all, and no duration line.
        var frames = new AerenderParser();
        frames.Feed("PROGRESS:  Start: 0");
        frames.Feed("PROGRESS:  End: 239");
        frames.Feed("PROGRESS:  120 (121): 3 Seconds");
        Assert.Equal((121, 240), (frames.Status.FramesDone, frames.Status.TotalFrames));

        var failing = new AerenderParser();
        Assert.True(failing.Feed("aerender ERROR: No comp was found with the given name."));
        failing.Feed("ERROR: Missing footage: logo.png");
        failing.Feed("PROGRESS:  -v ERRORS_AND_PROGRESS");
        Assert.Equal(["No comp was found with the given name.", "Missing footage: logo.png"], failing.Status.Errors);
        Assert.Null(failing.Status.Fraction);
    }

    // MARK: Running aerender

    [Fact]
    public void ARenderReportsEveryFrameAndWritesTheFile()
    {
        var install = AfterEffectsInstall.FromFolder(FakeInstall())!;
        var project = Project("graphics.aep", "frames=24");
        var output = Path.Combine(scratch, "out.mp4");
        var reports = new List<AerenderStatus>();
        var result = Aerender.Run(install.Aerender, Aerender.Arguments(new AerenderRequest(project, "Name Strap", output)),
            new SyncProgress<AerenderStatus>(reports.Add), Ct);
        Assert.True(result.Succeeded, result.Problem);
        Assert.True(File.Exists(output));
        Assert.Equal(24, reports[^1].FramesDone);
        Assert.Equal(24, reports[^1].TotalFrames);
        Assert.Equal("Name Strap", reports[^1].Composition);
        // Frames are reported one by one, in order.
        var counts = reports.Select(r => r.FramesDone).Where(n => n > 0).ToList();
        Assert.Equal(Enumerable.Range(1, 24), counts.Distinct());
        // The stand-in was given exactly the arguments built for it.
        var run = Assert.Single(Runs());
        Assert.Equal(["aerender", .. Aerender.Arguments(new AerenderRequest(project, "Name Strap", output))], run);
    }

    [Fact]
    public void AFailedRenderSaysWhy()
    {
        var install = AfterEffectsInstall.FromFolder(FakeInstall())!;
        var project = Project("graphics.aep", "fail=Missing Comp");
        var result = Aerender.Run(install.Aerender, Aerender.Arguments(new AerenderRequest(project, "Missing Comp", Path.Combine(scratch, "x.mp4"))),
            cancellationToken: Ct);
        Assert.False(result.Succeeded);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("No comp was found with the given name.", result.Problem);
    }

    [Fact]
    public void CancellingStopsTheRenderAtOnce()
    {
        var install = AfterEffectsInstall.FromFolder(FakeInstall())!;
        // Two minutes of frames, if it weren't stopped.
        var project = Project("slow.aep", "frames=1200", "delay=100");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var clock = Stopwatch.StartNew();
        var result = Aerender.Run(install.Aerender, Aerender.Arguments(new AerenderRequest(project, "Slow", Path.Combine(scratch, "slow.mp4"))),
            new SyncProgress<AerenderStatus>(s =>
            {
                if (s.FramesDone >= 3) cancel.Cancel();
            }), cancel.Token);
        Assert.True(result.Cancelled);
        Assert.False(result.Succeeded);
        Assert.Equal("Stopped.", result.Problem);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"took {clock.Elapsed}");
        Assert.InRange(result.Status.FramesDone, 3, 600);
        Assert.False(File.Exists(Path.Combine(scratch, "slow.mp4")));
    }

    // MARK: The bridge: copies only, scripts, and the import

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public void ReadingAProjectListsItsCompositionsFromACopy()
    {
        var bridge = new AfterEffectsBridge(AfterEffectsInstall.FromFolder(FakeInstall())!, Path.Combine(scratch, "work"));
        var project = Project("graphics.aep",
            "comp=Name Strap|1920|1080|5|30", "text=Name Strap|1|Speaker Name|Pastor Grace", "colour=Name Strap|2|Plate|Fill 1 Color|1,2,3|#10233F",
            "comp=Bumper|3840|2160|10|60");
        var before = Sha(project);
        var read = bridge.ReadProject(project, Ct);
        Assert.Equal(["Name Strap", "Bumper"], read.Compositions.Select(c => c.Name));
        var strap = read.Compositions[0];
        Assert.Equal((1920.0, 5.0, 30.0), (strap.Width, strap.Duration, strap.FrameRate));
        Assert.Equal(new AeText(1, "Speaker Name", "Pastor Grace"), Assert.Single(strap.Texts));
        var colour = Assert.Single(strap.Colours);
        Assert.Equal((2, "Plate", "Fill 1 Color", "#10233F"), (colour.Layer, colour.LayerName, colour.Name, colour.Hex));
        Assert.Equal([1, 2, 3], colour.Path);
        // After Effects was given a copy in the work folder, never the project itself.
        var run = Assert.Single(Runs());
        Assert.Equal(("AfterFX", "-r"), (run[0], run[1]));
        var job = JsonSerializer.Deserialize<ExtendScripts.Job>(run[3], ExtendScripts.Json)!;
        Assert.Equal(ExtendScripts.ListKind, job.Kind);
        Assert.StartsWith(Path.Combine(scratch, "work"), job.Project);
        Assert.Equal("graphics.aep", Path.GetFileName(job.Project));
        Assert.Equal(before, Sha(project));
        // The copy, the script and its answer are cleared away afterwards.
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(scratch, "work")));
    }

    [Fact]
    public void ATemplateRenderChangesACopyThenRendersTheCopy()
    {
        var source = Path.Combine(scratch, "clip.mp4");
        var strapDocument = StarterMotionTemplates.NameStrap();
        LottieVideoMaker.Render(strapDocument, MotionTimeline.For(strapDocument, MotionPlayback.Loop), new MotionExportOptions(256, 144, 30, HoldSeconds: 0), source,
            cancellationToken: Ct);
        var bridge = new AfterEffectsBridge(AfterEffectsInstall.FromFolder(FakeInstall())!, Path.Combine(scratch, "work"));
        var project = Project("Sunday Graphics.aep", $"source={source}", "frames=10");
        var before = Sha(project);
        var colour = new AeColour(2, "Plate", "Fill 1 Color", [1, 2, 3], "#10233F");
        AeChange[] changes = [AeChange.Words("Name Strap", 1, "Pastor Grace Okafor"), AeChange.Colour("Name Strap", colour, "#7A1F2B")];
        var output = Path.Combine(scratch, "renders", "Name Strap.mp4");

        var result = bridge.Render(project, "Name Strap", changes, output, cancellationToken: Ct);
        Assert.True(result.Succeeded, result.Problem);

        // The user's own project is exactly as it was.
        Assert.Equal(before, Sha(project));
        var runs = Runs();
        Assert.Equal(["AfterFX", "aerender", "aerender-saw"], runs.Select(r => r[0]));
        // aerender rendered the copy, in the work folder, after the script had saved the changes into it.
        var renderedProject = runs[1][Array.IndexOf(runs[1], "-project") + 1];
        Assert.NotEqual(project, renderedProject);
        Assert.Equal("Sunday Graphics.aep", Path.GetFileName(renderedProject));
        Assert.StartsWith(Path.Combine(scratch, "work"), renderedProject);
        Assert.Equal(JsonSerializer.Deserialize<ExtendScripts.Job>(runs[0][3], ExtendScripts.Json)!.Project, renderedProject);
        var saved = runs[2][1..].Select(c => JsonSerializer.Deserialize<AeChange>(c, ExtendScripts.Json)!).ToList();
        Assert.Equal(["Pastor Grace Okafor", null], saved.Select(c => c.Text));
        Assert.Equal([null, "#7A1F2B"], saved.Select(c => c.Hex));
        // The work folder (the copy and its scripts) is cleared away afterwards.
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(scratch, "work")));

        // And the render goes into the library as an ordinary video, ready to play.
        var library = new LibraryStore(Path.Combine(scratch, "library")) { Prober = new MediaImportProbe() };
        var item = AfterEffectsBridge.FileIntoLibrary(library, output, "Name Strap · Pastor Grace", background: false);
        Assert.Equal((MediaKind.Video, MediaFileStatus.Ready, AfterEffectsBridge.MediaFolder, false), (item.MediaKind, item.FileStatus, item.Folder, item.Loops));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void TheChangesThatReachAfterEffectsAreTheOnesAskedFor()
    {
        var bridge = new AfterEffectsBridge(AfterEffectsInstall.FromFolder(FakeInstall())!, Path.Combine(scratch, "work"));
        // The render itself fails (After Effects can't find the composition) after the changes went in.
        var project = Project("g.aep", "fail=Name Strap");
        var colour = new AeColour(3, "Text", "Text fill", [], "#FFFFFF");
        var result = bridge.Render(project, "Name Strap", [AeChange.Words("Name Strap", 1, "Ọlá «Grace» \"Okafor\""), AeChange.Colour("Name Strap", colour, "#000000")],
            Path.Combine(scratch, "x.mp4"), cancellationToken: Ct);
        Assert.False(result.Succeeded);
        Assert.False(File.Exists(Path.Combine(scratch, "x.mp4")));
        // The script's job carried every change, words and all, and asked for the copy to be saved.
        var afterFx = Runs().First(r => r[0] == "AfterFX");
        Assert.EndsWith("apply.jsx", afterFx[2]);
        var job = JsonSerializer.Deserialize<ExtendScripts.Job>(afterFx[3], ExtendScripts.Json)!;
        Assert.Equal(ExtendScripts.ApplyKind, job.Kind);
        Assert.Equal(["Ọlá «Grace» \"Okafor\"", null], job.Changes!.Select(c => c.Text));
        Assert.Equal((AeChange.TextColourKind, "#000000", 3), (job.Changes![1].Kind, job.Changes[1].Hex, job.Changes[1].Layer));
    }

    [Fact]
    public void AProjectWithUnsavedChangesInAfterEffectsIsLeftAlone()
    {
        var bridge = new AfterEffectsBridge(AfterEffectsInstall.FromFolder(FakeInstall())!, Path.Combine(scratch, "work"));
        var project = Project("g.aep", "dirty=true");
        var error = Assert.Throws<IOException>(() => bridge.Render(project, "Name Strap", [AeChange.Words("Name Strap", 1, "x")], Path.Combine(scratch, "x.mp4"),
            cancellationToken: Ct));
        Assert.Contains("unsaved changes", error.Message);
        Assert.DoesNotContain(Runs(), r => r[0] == "aerender");
    }

    [Fact]
    public void WithoutAfterFxItStillRendersButCantChangeAnything()
    {
        var bridge = new AfterEffectsBridge(AfterEffectsInstall.FromFolder(FakeInstall(withAfterFx: false))!, Path.Combine(scratch, "work"));
        var project = Project("g.aep", "frames=3");
        Assert.Throws<IOException>(() => bridge.Render(project, "Comp", [AeChange.Words("Comp", 1, "x")], Path.Combine(scratch, "a.mp4"), cancellationToken: Ct));
        Assert.True(bridge.Render(project, "Comp", [], Path.Combine(scratch, "b.mp4"), cancellationToken: Ct).Succeeded);
    }

    [Fact]
    public void ACancelledRenderLeavesNothing()
    {
        var bridge = new AfterEffectsBridge(AfterEffectsInstall.FromFolder(FakeInstall())!, Path.Combine(scratch, "work"));
        var project = Project("g.aep", "frames=1200", "delay=100");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var output = Path.Combine(scratch, "c.mp4");
        Assert.ThrowsAny<OperationCanceledException>(() => bridge.Render(project, "Comp", [], output,
            new SyncProgress<AerenderStatus>(s => { if (s.FramesDone >= 2) cancel.Cancel(); }), cancel.Token));
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(scratch, "work")));
    }

    // MARK: The scripts

    [Fact]
    public void TheJobLineIsPlainAsciiJavaScriptThatReadsBack()
    {
        var job = new ExtendScripts.Job(ExtendScripts.ApplyKind, @"C:\Work\Grâce ""copy"".aep", @"C:\Work\result.json", Quit: true,
            [AeChange.Words("Name Strap", 1, "Ọlá Okafor\nLead Pastor"), new AeChange("Name Strap", 2, AeChange.ColourKind, Path: [1, 2, 3], Hex: "#7A1F2B")]);
        var line = ExtendScripts.JobLine(job);
        Assert.StartsWith("var MXU_JOB = {", line);
        Assert.EndsWith("};", line);
        Assert.All(line, c => Assert.InRange(c, ' ', '~'));
        Assert.Contains(@"\u1ECC", line, StringComparison.OrdinalIgnoreCase); // Ọ
        var back = ExtendScripts.ReadJob(ExtendScripts.Script(job))!;
        Assert.Equal((job.Kind, job.Project, job.Result, job.Quit), (back.Kind, back.Project, back.Result, back.Quit));
        Assert.Equal("Ọlá Okafor\nLead Pastor", back.Changes![0].Text);
        Assert.Equal([1, 2, 3], back.Changes[1].Path!);
    }

    [Fact]
    public void TheScriptOnlyEverSavesTheCopyAndRefusesUnsavedWork()
    {
        var script = ExtendScripts.Script(new ExtendScripts.Job(ExtendScripts.ListKind, "p.aep", "r.json", Quit: false));
        Assert.Contains("app.project.dirty", script);
        Assert.Contains("CloseOptions.DO_NOT_SAVE_CHANGES", script);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(script, @"\.save\("));
        Assert.Contains("beginSuppressDialogs", script);
        // ExtendScript is an old JavaScript: no JSON object, no arrow functions, no let or const.
        Assert.DoesNotContain("JSON.", script);
        Assert.DoesNotContain("=>", script);
        Assert.DoesNotMatch(@"\b(let|const)\s", script);
    }

    [Fact]
    public void TheLottieGuideCoversTheRoute()
    {
        var text = string.Join("\n", LottieGuide.Steps.Select(s => s.Heading + " " + s.Text));
        foreach (var word in new[] { "Bodymovin", "LottieFiles", "In, Hold and Out", "Glyphs", "Include in json", "Import Lottie" }) Assert.Contains(word, text);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
