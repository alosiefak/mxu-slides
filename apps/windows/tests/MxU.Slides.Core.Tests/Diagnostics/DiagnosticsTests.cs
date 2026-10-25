using MxU.Slides.Core.Diagnostics;

namespace MxU.Slides.Core.Tests.Diagnostics;

/// <summary>
/// The diagnostics: what a problem report holds (a port of DiagnosticsBundleTests.swift), the
/// secrets taken out of it, hang detection (with PerfLineLimiterTests.swift), the health checks,
/// reading Check this PC's report, and the self-test's throwaway-library rule.
/// </summary>
public sealed class DiagnosticsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    private static DiagnosticsBundle.Candidate File(string name, double daysAgo, long bytes = 10) =>
        new($@"C:\logs\{name}", Now.AddDays(-daysAgo), bytes);

    // MARK: The bundle (DiagnosticsBundleTests.swift)

    [Fact]
    public void TheSelectionIsTheLastWeekNewestFirstUnderTheCap()
    {
        var files = new[] { File("old.log", 9), File("b.log", 2, 40), File("huge-hang.txt", 1, 90), File("a.log", 0.5, 50) };
        var chosen = DiagnosticsBundle.Selection(files, Now, byteCap: 100);
        Assert.Equal(["a.log", "b.log"], chosen.Select(c => c.Name)); // the file that would pass the cap is skipped, smaller ones still fit
    }

    [Fact]
    public void PruningTakesOnlyWhatIsPastRetention()
    {
        var files = new[] { File("keep.log", 13), File("drop.log", 15) };
        Assert.Equal(["drop.log"], DiagnosticsBundle.Expired(files, Now).Select(c => c.Name));
    }

    [Fact]
    public void CrashReportsAreThisAppsAndNewerThanTheLastLaunch()
    {
        Assert.True(DiagnosticsBundle.IsCrashReport("AppCrash_MxUSlides.exe_8c2d1f_cab_12345"));
        Assert.True(DiagnosticsBundle.IsCrashReport("AppHang_MxUSlides.exe_8c2d1f_cab_12345"));
        Assert.True(DiagnosticsBundle.IsCrashReport("MxUSlides.exe.10412.dmp"));
        Assert.False(DiagnosticsBundle.IsCrashReport("AppCrash_msedge.exe_123"));
        Assert.False(DiagnosticsBundle.IsCrashReport("MxUSlides notes.txt"));

        var files = new[] { File("AppCrash_MxUSlides.exe_new", 0.1), File("AppCrash_MxUSlides.exe_old", 3), File("AppCrash_notepad.exe_new", 0.1) };
        Assert.Equal(["AppCrash_MxUSlides.exe_new"], DiagnosticsBundle.CrashReports(files, Now.AddDays(-1)).Select(c => c.Name));
        Assert.Empty(DiagnosticsBundle.CrashReports(files, null)); // a first launch has no previous run to blame
    }

    [Fact]
    public void CandidatesToleratesAMissingFolder()
    {
        Assert.Empty(DiagnosticsBundle.Candidates(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
    }

    // MARK: Secrets

    [Fact]
    public void SecretsAreTakenOutOfLogText()
    {
        var log = "12:00 key mxu_ab12cd34ef56gh78 created; Authorization: Bearer eyJhbGciOi.abc; "
            + "streaming to rtmp://a.rtmp.youtube.com/live2/abcd-efgh-ijkl-mnop; GET https://api.example.com/v1?a=1&token=SECRET123&b=2; "
            + "password=hunter2; file C:\\Users\\oluwa\\Music\\song.mp3";
        var scrubbed = SecretScrubber.ScrubText(log, userName: "oluwa");
        Assert.DoesNotContain("ab12cd34ef56gh78", scrubbed);
        Assert.DoesNotContain("eyJhbGciOi", scrubbed);
        Assert.DoesNotContain("abcd-efgh", scrubbed);
        Assert.DoesNotContain("SECRET123", scrubbed);
        Assert.DoesNotContain("hunter2", scrubbed);
        Assert.DoesNotContain("oluwa", scrubbed);
        Assert.Contains("rtmp://a.rtmp.youtube.com/live2/[removed]", scrubbed); // where it streams to is still useful
        Assert.Contains("a=1&token=[removed]&b=2", scrubbed);
        Assert.Contains(@"C:\Users\[user]\Music\song.mp3", scrubbed);
    }

    [Fact]
    public void SecretSettingsLoseTheirValuesButAMusicKeyDoesnt()
    {
        var json = "{\"LibraryPath\":\"C:\\\\Users\\\\oluwa\\\\Library\",\"ApiToken\":\"abc\",\"Production\":{\"ObsPassword\":\"pw\",\"Host\":\"10.0.0.2\"},"
            + "\"MusicKey\":\"G\",\"RunOnlyPin\":\"1234\",\"Keys\":[\"mxu_ab12cd34ef56\"]}";
        var scrubbed = SecretScrubber.ScrubJson(json, "oluwa")!;
        Assert.DoesNotContain("\"abc\"", scrubbed);
        Assert.DoesNotContain("pw", scrubbed);
        Assert.DoesNotContain("1234", scrubbed);
        Assert.DoesNotContain("ab12cd34ef56", scrubbed);
        Assert.Contains("10.0.0.2", scrubbed);
        Assert.Contains("\"MusicKey\": \"G\"", scrubbed);
        Assert.Contains("[user]", scrubbed);
        Assert.Null(SecretScrubber.ScrubJson("not json"));
    }

    // MARK: Hangs (and PerfLineLimiterTests.swift)

    [Fact]
    public void TheLimiterAdmitsItsBudgetPerKindThenCountsWhatItDrops()
    {
        var limiter = new PerfLineLimiter(3);
        for (var second = 0; second < 3; second++) Assert.Equal(0, limiter.Admit("storm", second));
        Assert.Null(limiter.Admit("storm", 10));
        Assert.Null(limiter.Admit("storm", 20));
        Assert.Equal(0, limiter.Admit("slowOpen", 20));
        Assert.Equal(2, limiter.Admit("storm", 60));
        Assert.Equal(0, limiter.Admit("storm", 61));
        Assert.Equal("x", PerfLineLimiter.Annotate("x", 0));
        Assert.Equal("x (+4 dropped)", PerfLineLimiter.Annotate("x", 4));
    }

    [Fact]
    public void AQuickAnswerIsNothingAndASlowOneIsAHitch()
    {
        var detector = new HangDetector();
        detector.PingSent(10);
        Assert.Null(detector.Answered(10.05));
        detector.PingSent(11);
        var hitch = Assert.IsType<HangDetector.Event.Hitch>(detector.Answered(11.4));
        Assert.Equal(400, hitch.Blocked.TotalMilliseconds, 3);
        Assert.Null(detector.Answered(12)); // no ping waiting: nothing
    }

    [Fact]
    public void FiveSecondsWithoutAnAnswerIsAHangReportedOnceThenItsEnd()
    {
        var detector = new HangDetector();
        detector.PingSent(100);
        Assert.Null(detector.Check(104.9));
        var began = Assert.IsType<HangDetector.Event.HangBegan>(detector.Check(105.1));
        Assert.Equal(100, began.Since);
        Assert.True(detector.IsHung);
        Assert.Null(detector.Check(110)); // once per hang
        detector.PingSent(110); // the watcher doesn't send another while one waits: this one is ignored
        var ended = Assert.IsType<HangDetector.Event.HangEnded>(detector.Answered(112));
        Assert.Equal(12, ended.Lasted.TotalSeconds, 3); // from when it got stuck
        Assert.False(detector.IsHung);
        Assert.False(detector.Waiting);
    }

    [Fact]
    public void HitchesAreRateLimited()
    {
        var detector = new HangDetector();
        var reported = 0;
        for (var i = 0; i < 20; i++)
        {
            detector.PingSent(i);
            if (detector.Answered(i + 0.5) is HangDetector.Event.Hitch) reported++;
        }
        Assert.Equal(HangDetector.HitchesPerMinute, reported);
    }

    // MARK: Health checks

    [Fact]
    public void AReadyPcIsAllGoodOrNotes()
    {
        var checks = HealthChecks.Run(new HealthFacts
        {
            FreeDiskBytes = 50L * 1024 * 1024 * 1024, Displays = 2, LastBackup = DateTimeOffset.Now.AddDays(-1), OnBattery = false,
        });
        Assert.True(HealthChecks.Overall(checks) <= HealthStatus.Note);
        Assert.Contains(checks, c => c.Area == "NDI" && c.Status == HealthStatus.Note);
    }

    [Fact]
    public void ProblemsSayWhatToDo()
    {
        var checks = HealthChecks.Run(new HealthFacts
        {
            FreeDiskBytes = 1L * 1024 * 1024 * 1024, MissingMediaFiles = 3, ScreensWithoutDisplay = ["Projector"], RenderTier = 0,
            SoundOutputs = 0, AutomaticBackups = false, HangsThisWeek = 2, OnBattery = true,
        });
        Assert.Equal(HealthStatus.Problem, HealthChecks.Overall(checks));
        var disk = checks.Single(c => c.Area == "Disk space");
        Assert.Equal(HealthStatus.Problem, disk.Status);
        Assert.NotNull(disk.Advice);
        Assert.Contains("3 media files", checks.Single(c => c.Area == "Media").Summary);
        Assert.Contains("Projector", checks.Single(c => c.Area == "Screens").Summary);
        Assert.All(checks.Where(c => c.Status >= HealthStatus.Warning), c => Assert.False(string.IsNullOrEmpty(c.Advice)));
    }

    [Fact]
    public void AnOldBackupIsAWarning()
    {
        var now = DateTimeOffset.Now;
        Assert.Equal(HealthStatus.Warning, HealthChecks.Run(new HealthFacts { LastBackup = now.AddDays(-10), Now = now }).Single(c => c.Area == "Backups").Status);
        Assert.Equal(HealthStatus.Warning, HealthChecks.Run(new HealthFacts { LastBackup = null }).Single(c => c.Area == "Backups").Status);
    }

    // MARK: Check this PC

    private const string Report = """
        MxU Slides self-test 2026-10-25 10:00:00
        PASS  the audience shows Verse 1
        PASS  the audience preview window is showing
        PASS  Push transition draws on the graphics card
        SKIP  NDI output appears on the network (NDI runtime not installed)
        SKIP  DeckLink output (no DeckLink card)
        FAIL  a 10-second recording plays back
        PASS  the music plays on the default sound output
        NOTE  something worth knowing
        RESULT: FAIL (1 problem)
        """;

    [Fact]
    public void TheReportIsReadAsAPassFailList()
    {
        var lines = PcCheck.Parse(Report);
        Assert.Equal(8, lines.Count);
        Assert.Equal(new PcCheck.Line(PcCheck.Result.Fail, "a 10-second recording plays back"), lines[5]);
        Assert.True(PcCheck.Finished(Report));
        Assert.False(PcCheck.Passed(Report));
        Assert.False(PcCheck.Finished("MxU Slides self-test\nPASS  x"));
    }

    [Fact]
    public void EachAreaGetsOneLineAVolunteerCanRead()
    {
        var areas = PcCheck.Summarize(PcCheck.Parse(Report)).ToDictionary(a => a.Name);
        Assert.Equal(PcCheck.AreaState.Ready, areas["Screens"].State);
        Assert.Equal(PcCheck.AreaState.Ready, areas["Graphics card and transitions"].State);
        Assert.Equal(PcCheck.AreaState.Problem, areas["Capture and streaming"].State);
        Assert.Contains("recording plays back", areas["Capture and streaming"].Summary);
        Assert.Equal(PcCheck.AreaState.Ready, areas["Sound"].State);
        Assert.Equal(PcCheck.AreaState.NotAvailable, areas["NDI and DeckLink"].State);
        Assert.Contains("NDI runtime not installed", areas["NDI and DeckLink"].Summary);
    }

    [Fact]
    public void AnAreaTheRunDidntCoverSaysSo()
    {
        var areas = PcCheck.Summarize(PcCheck.Parse("PASS  the audience shows Verse 1\nRESULT: PASS"));
        Assert.Equal(PcCheck.AreaState.NotChecked, areas.Single(a => a.Name == "Sound").State);
    }

    // MARK: The self-test's throwaway library

    private const string RealData = @"C:\Users\me\AppData\Roaming\MxU Slides";
    private const string RealLocal = @"C:\Users\me\AppData\Local\MxU Slides";
    private const string Temp = @"C:\Users\me\AppData\Local\Temp";

    [Fact]
    public void TheSelfTestRefusesTheRealLibrary()
    {
        Assert.Contains("aren't set", SelfTestSandbox.Problem(null, null, null, RealData, RealLocal, Temp));
        Assert.Contains("points at the real one", SelfTestSandbox.Problem(RealData, Temp + @"\x\local", "1", RealData, RealLocal, Temp));
        Assert.Contains("points at the real one", SelfTestSandbox.Problem(Temp + @"\x\data", RealLocal + @"\", "1", RealData, RealLocal, Temp));
        // Somewhere else on the disk, without the script's say-so: refused too.
        Assert.NotNull(SelfTestSandbox.Problem(@"D:\Church\data", @"D:\Church\local", null, RealData, RealLocal, Temp));
    }

    [Fact]
    public void TheScriptsAndCheckThisPcsFoldersAreAllowed()
    {
        Assert.Null(SelfTestSandbox.Problem(Temp + @"\MxU Slides self-test library 1\data", Temp + @"\MxU Slides self-test library 1\local", null, RealData, RealLocal, Temp));
        Assert.Null(SelfTestSandbox.Problem(@"D:\scratch\data", @"D:\scratch\local", "1", RealData, RealLocal, Temp));
    }
}
