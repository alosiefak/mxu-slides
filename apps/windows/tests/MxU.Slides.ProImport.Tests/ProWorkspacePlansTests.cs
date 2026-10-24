using MxU.Slides.Core.Model;
using MxU.Slides.Core.Outputs;
using MxU.Slides.Core.Stage;

namespace MxU.Slides.ProImport.Tests;

public class ProWorkspacePlansTests
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private static ProScreenPlan.Screen Screen(string uuid, string name, bool confidence = false, ProScreenPlan.OutputCorrection? correction = null,
        params ProScreenPlan.Slice[] slices) =>
        new() { ProUuid = uuid, Name = name, IsConfidence = confidence, Width = 1920, Height = 1080, Correction = correction, Slices = slices };

    private static (ProWorkspacePlans Plans, TestLibrary Library, OutputConfiguration Outputs, TimerController Timers) Make(
        Func<ProInputPlan, bool>? inputs = null, Action<Guid, string>? stage = null,
        Func<ProScreenPlan.MaskPlan, IReadOnlyList<OutputMask>>? masks = null)
    {
        var library = new TestLibrary();
        var outputs = new OutputConfiguration();
        var timers = new TimerController();
        var plans = new ProWorkspacePlans
        {
            Library = library.Store, Outputs = outputs, Timers = timers, ImportInput = inputs, SetStageLayout = stage, MasksFromPlan = masks,
            Clock = new FixedClock(new DateTimeOffset(2026, 10, 25, 8, 0, 0, TimeSpan.Zero)),
        };
        return (plans, library, outputs, timers);
    }

    private static WorkspaceImportOptions Options(ImportConflictPolicy policy = ImportConflictPolicy.UpdateUnedited) => new() { Policy = policy };

    [Fact]
    public void TimersArrivePerKindAndExistingOnesAreKept()
    {
        var (plans, library, _, timers) = Make();
        using (library)
        {
            var result = new ProWorkspaceImportResult
            {
                TimerPlans =
                [
                    new("u1", "t-sermon", "Sermon", new ProTimerMode.Countdown(2100)),
                    new("u2", "t-start", "Service starts", new ProTimerMode.CountdownToTime(10, 30)),
                    new("u3", "t-up", "Elapsed", new ProTimerMode.CountUp(0)),
                ],
            };
            Assert.Equal(3, plans.Apply(result, Options()).TimersImported);
            Assert.Equal(TimerMode.Countdown, timers.Timers.Single(t => t.Id == "t-sermon").Mode);
            Assert.Equal(2100, timers.Timers.Single(t => t.Id == "t-sermon").DurationSeconds);
            Assert.Equal(TimerMode.CountdownToTime, timers.Timers.Single(t => t.Id == "t-start").Mode);
            Assert.NotNull(timers.Timers.Single(t => t.Id == "t-start").TargetTime);
            Assert.Equal(TimerMode.CountUp, timers.Timers.Single(t => t.Id == "t-up").Mode);

            timers.ImportTimer("t-sermon", "Mine", TimerMode.Countdown, 60);
            Assert.Equal(0, plans.Apply(result, Options()).TimersImported);
            Assert.Equal("Mine", timers.Timers.Single(t => t.Id == "t-sermon").Name);
            Assert.Equal(3, plans.Apply(result, Options(ImportConflictPolicy.Replace)).TimersImported);
            Assert.Equal("Sermon", timers.Timers.Single(t => t.Id == "t-sermon").Name);
        }
    }

    [Fact]
    public void InputsGoToTheInventory()
    {
        var seen = new List<string>();
        var (plans, library, _, _) = Make(inputs: plan =>
        {
            seen.Add(plan.Name);
            return plan.Name != "Already here";
        });
        using (library)
        {
            var result = new ProWorkspaceImportResult
            {
                InputPlans = [new("i1", "Pulpit camera", CaptureSourceKind.Camera, "cam"), new("i2", "Already here", CaptureSourceKind.Ndi, "x")],
            };
            Assert.Equal(1, plans.Apply(result, Options()).InputsImported);
            Assert.Equal(["Pulpit camera", "Already here"], seen);
            Assert.Equal(0, plans.Apply(result, Options() with { VideoInputs = false }).InputsImported);
        }
    }

    [Fact]
    public void NewScreensArriveWithTheirRoleAndCorrections()
    {
        var (plans, library, outputs, _) = Make();
        using (library)
        {
            var correction = new ProScreenPlan.OutputCorrection { Brightness = 0.1, TopLeft = new(5, 3), RotationDegrees = 0 };
            var result = new ProWorkspaceImportResult
            {
                ScreenPlan = new ProScreenPlan { Screens = [Screen("s1", "Front", correction: correction), Screen("s2", "Stage", confidence: true)] },
            };
            Assert.Equal(2, plans.Apply(result, Options()).ScreensAdded);
            var front = outputs.Screens.Single(s => s.Name == "Front");
            var stage = outputs.Screens.Single(s => s.Name == "Stage");
            Assert.Equal((1920, 1080), (front.Width, front.Height));
            Assert.Equal(ScreenRole.Audience, outputs.Role(front.Id));
            Assert.Equal(ScreenRole.Confidence, outputs.Role(stage.Id));
            var adjustments = outputs.Adjustments(front.Id);
            Assert.Equal(0.1, adjustments.Brightness);
            Assert.Equal(new OutputPoint(5, 3), adjustments.TopLeft);
            Assert.Null(adjustments.RotationDegrees);
            Assert.Null(adjustments.Contrast);
        }
    }

    [Fact]
    public void AScreenAlreadyHereIsReusedAndOnlyReplaceReconfiguresIt()
    {
        var (plans, library, outputs, _) = Make();
        using (library)
        {
            var mine = outputs.AddScreen("Front", 1280, 720)!;
            var result = new ProWorkspaceImportResult
            {
                ScreenPlan = new ProScreenPlan { Screens = [Screen("s1", "Front", correction: new ProScreenPlan.OutputCorrection { Gamma = 1.2 })] },
            };
            Assert.Equal(0, plans.Apply(result, Options()).ScreensAdded);
            Assert.Single(outputs.Screens);
            Assert.Null(outputs.Adjustments(mine.Id).Gamma);
            plans.Apply(result, Options(ImportConflictPolicy.Replace));
            Assert.Equal(1.2, outputs.Adjustments(mine.Id).Gamma);
            Assert.Equal(1280, outputs.Screen(mine.Id)!.Width);
        }
    }

    [Fact]
    public void SlicesReplaceTheScreensOwnWithTheirBlends()
    {
        var (plans, library, outputs, _) = Make();
        using (library)
        {
            var left = new ProScreenPlan.Slice { ProUuid = "a", Name = "Left", X = 0, Y = 0, Width = 0.55, Height = 1, BlendRight = new(0.1, 2.2, null, null) };
            var right = new ProScreenPlan.Slice { ProUuid = "b", Name = "Right", X = 0.45, Y = 0, Width = 0.55, Height = 1, BlendLeft = new(0.1, 2.2, 0.9, null) };
            var plain = new ProScreenPlan.Slice { ProUuid = "c", Name = "Plain", X = 0, Y = 0, Width = 1, Height = 1 };
            var result = new ProWorkspaceImportResult { ScreenPlan = new ProScreenPlan { Screens = [Screen("s1", "Wide", slices: [left, right, plain])] } };
            plans.Apply(result, Options());
            var wide = outputs.Screens.Single();
            var slices = outputs.ExtraSlices(wide.Id);
            Assert.Equal(["Left", "Right", "Plain"], slices.Select(s => s.Name));
            Assert.Equal(new SceneRectish(0.45, 0.55), new SceneRectish(slices[1].SourceRect.X, slices[1].SourceRect.Width));
            Assert.Equal(0.1, outputs.Adjustments(slices[0].Id).BlendRight!.Width);
            Assert.Equal(0.9, outputs.Adjustments(slices[1].Id).BlendLeft!.Intensity);
            Assert.True(outputs.Adjustments(slices[2].Id).IsNeutral);

            // Importing again with Replace swaps them rather than adding more.
            plans.Apply(result, Options(ImportConflictPolicy.Replace));
            Assert.Equal(3, outputs.ExtraSlices(wide.Id).Count);
        }
    }

    private readonly record struct SceneRectish(double X, double Width);

    [Fact]
    public void LooksBecomeOutputPresetsWithTheirMasksAndThemes()
    {
        var made = new List<string>();
        var (plans, library, outputs, _) = Make(masks: plan =>
        {
            made.Add(plan.Name);
            return [new OutputMask { Name = plan.Name, PathData = "M0 0 L10 0 L10 10 Z" }];
        });
        using (library)
        {
            var result = new ProWorkspaceImportResult
            {
                ScreenPlan = new ProScreenPlan
                {
                    Screens = [Screen("s1", "Front"), Screen("s2", "Lobby")],
                    Masks = [new ProScreenPlan.MaskPlan("m1", "Pillars", [new ProScreenPlan.MaskShape(0, 0, 0.1, 1, "rectangle", 0, null)])],
                },
                LookPlans =
                [
                    new("look-1", "Lyrics only", [
                        new ProLookPlan.ScreenLook { ProScreenUuid = "s1", EnabledLayers = ["slide"], SlideThemeId = "theme-a", MaskProUuid = "m1" },
                        new ProLookPlan.ScreenLook { ProScreenUuid = "s2", EnabledLayers = ["slide", "media"] },
                        new ProLookPlan.ScreenLook { ProScreenUuid = "gone", EnabledLayers = ["slide"] },
                    ]),
                ],
            };
            var summary = plans.Apply(result, Options());
            Assert.Equal((1, 1), (summary.LooksImported, summary.MasksImported));
            Assert.Equal(["Pillars"], made);
            var front = outputs.Screens.Single(s => s.Name == "Front");
            var mask = Assert.Single(outputs.Masks(front.Id));
            var preset = library.Store.Get<OutputPreset>("look-1")!;
            Assert.Equal("Lyrics only", preset.Name);
            Assert.Equal(2, preset.Assignments.Count);
            var first = preset.Assignments[0];
            Assert.Equal(OutputTargetKind.PlaceholderScreen, first.TargetKind);
            Assert.Equal(front.Id.ToString().ToUpperInvariant(), first.TargetId);
            Assert.Equal("theme-a", first.SlideThemeId);
            Assert.Equal([mask.Id.ToString().ToUpperInvariant()], first.MaskIds);
            Assert.Equal(["slide", "media"], preset.Assignments[1].EnabledLayers);
            Assert.Null(preset.Assignments[1].MaskIds);

            // A preset already in the library is kept unless the import replaces.
            preset.Name = "Edited here";
            library.Store.Save(preset);
            Assert.Equal((0, 1), (plans.Apply(result, Options()).LooksImported, plans.Apply(result, Options()).LooksKept));
            Assert.Equal("Edited here", library.Store.Get<OutputPreset>("look-1")!.Name);
            plans.Apply(result, Options(ImportConflictPolicy.Replace));
            Assert.Equal("Lyrics only", library.Store.Get<OutputPreset>("look-1")!.Name);
            Assert.Single(outputs.Masks(front.Id));
        }
    }

    [Fact]
    public void StageLayoutsGoToTheirScreens()
    {
        var set = new List<(Guid, string)>();
        var (plans, library, outputs, _) = Make(stage: (screen, layout) => set.Add((screen, layout)));
        using (library)
        {
            var result = new ProWorkspaceImportResult
            {
                ScreenPlan = new ProScreenPlan
                {
                    Screens = [Screen("s2", "Stage", confidence: true)],
                    StageAssignments = [new("s2", "layout-1"), new("unknown", "layout-2")],
                },
            };
            plans.Apply(result, Options());
            Assert.Equal([(outputs.Screens.Single().Id, "layout-1")], set);
        }
    }

    [Fact]
    public void SwitchedOffPartsAreLeftAlone()
    {
        var (plans, library, outputs, timers) = Make();
        using (library)
        {
            var result = new ProWorkspaceImportResult
            {
                TimerPlans = [new("u1", "t", "T", new ProTimerMode.Countdown(10))],
                ScreenPlan = new ProScreenPlan { Screens = [Screen("s1", "Front")] },
                LookPlans = [new("look", "Look", [new ProLookPlan.ScreenLook { ProScreenUuid = "s1", EnabledLayers = ["slide"] }])],
            };
            var off = Options() with { Timers = false, Screens = false, Looks = false };
            Assert.Equal(new ProWorkspacePlans.Summary(0, 0, 0, 0, 0, 0), plans.Apply(result, off));
            Assert.Empty(timers.Timers);
            Assert.Empty(outputs.Screens);
            Assert.Null(library.Store.Get<OutputPreset>("look"));
        }
    }
}
