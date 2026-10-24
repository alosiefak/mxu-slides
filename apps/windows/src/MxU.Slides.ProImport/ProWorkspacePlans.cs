using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Outputs;
using MxU.Slides.Core.Scene;
using MxU.Slides.Core.Stage;

namespace MxU.Slides.ProImport;

/// <summary>
/// The second half of a ProPresenter workspace import: the parts that aren't library
/// documents. Timers go to the timer list, video inputs to the input inventory, screens (with
/// their corrections, slices, masks and stage layouts) to the output setup, and looks become
/// output presets. Port of the finishing half of WorkspaceImport.run in
/// apps/mac/Sources/WorkspaceImport.swift; the app calls it on the UI thread after the import.
/// </summary>
public sealed class ProWorkspacePlans
{
    /// <summary>What applying did, for the import's summary.</summary>
    public sealed record Summary(int TimersImported, int InputsImported, int ScreensAdded, int MasksImported, int LooksImported, int LooksKept);

    public required LibraryStore Library { get; init; }

    public required OutputConfiguration Outputs { get; init; }

    public required TimerController Timers { get; init; }

    /// <summary>Adds an entry to the video input inventory (id, name, kind, source); false when it was already there.</summary>
    public Func<ProInputPlan, bool>? ImportInput { get; init; }

    /// <summary>Gives a screen a stage layout (screen id, layout id).</summary>
    public Action<Guid, string>? SetStageLayout { get; init; }

    /// <summary>Turns a ProPresenter mask into output masks (shape outlines as path data); masks are skipped without it.</summary>
    public Func<ProScreenPlan.MaskPlan, IReadOnlyList<OutputMask>>? MasksFromPlan { get; init; }

    /// <summary>Where "countdown to a time" timers count to (tests pass a fixed clock).</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    public Summary Apply(ProWorkspaceImportResult result, WorkspaceImportOptions options)
    {
        // Keep mine and Update unedited leave screens the library already had alone; Replace refreshes them.
        var refreshExisting = options.Policy == ImportConflictPolicy.Replace;
        var timers = options.Timers ? ApplyTimers(result.TimerPlans, refreshExisting) : 0;
        var inputs = options.VideoInputs && ImportInput is { } import ? result.InputPlans.Count(import) : 0;

        var screenIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var added = new HashSet<Guid>();
        if (options.Screens)
        {
            foreach (var planned in result.ScreenPlan.Screens)
            {
                if (Outputs.Screens.FirstOrDefault(s => s.Name == planned.Name) is { } existing)
                {
                    screenIds[planned.ProUuid] = existing.Id;
                    continue;
                }
                if (Outputs.AddScreen(planned.Name, planned.Width, planned.Height) is not { } screen) continue;
                if (planned.IsConfidence) Outputs.SetRole(screen.Id, ScreenRole.Confidence);
                screenIds[planned.ProUuid] = screen.Id;
                added.Add(screen.Id);
            }
        }
        else
        {
            // Looks and stage layouts still find screens the library already has, by name.
            foreach (var planned in result.ScreenPlan.Screens)
                if (Outputs.Screens.FirstOrDefault(s => s.Name == planned.Name) is { } existing) screenIds[planned.ProUuid] = existing.Id;
        }
        bool MayConfigure(Guid screenId) => refreshExisting || added.Contains(screenId);

        if (options.ScreenCorrections)
        {
            foreach (var planned in result.ScreenPlan.Screens)
            {
                if (planned.Correction is not { } correction || !screenIds.TryGetValue(planned.ProUuid, out var screenId) || !MayConfigure(screenId)) continue;
                Outputs.SetAdjustments(screenId, Adjustments(correction));
            }
        }

        if (options.ScreenSlices)
        {
            foreach (var planned in result.ScreenPlan.Screens.Where(s => s.Slices.Count > 0))
            {
                if (!screenIds.TryGetValue(planned.ProUuid, out var screenId) || !MayConfigure(screenId)) continue;
                // The screen's own slices give way to ProPresenter's (the Mac also takes their NDI and DeckLink away).
                foreach (var slice in Outputs.ExtraSlices(screenId).ToList())
                {
                    Outputs.AssignDevice(slice.Id, null);
                    Outputs.RemoveSlice(slice.Id);
                }
                foreach (var planSlice in planned.Slices)
                {
                    if (Outputs.AddSlice(screenId, planSlice.Name, new SceneRect(planSlice.X, planSlice.Y, planSlice.Width, planSlice.Height)) is not { } created) continue;
                    var adjustments = (planSlice.Correction is { } correction ? Adjustments(correction) : new OutputAdjustments()) with
                    {
                        BlendLeft = Blend(planSlice.BlendLeft),
                        BlendRight = Blend(planSlice.BlendRight),
                        BlendTop = Blend(planSlice.BlendTop),
                        BlendBottom = Blend(planSlice.BlendBottom),
                    };
                    if (!adjustments.IsNeutral) Outputs.SetAdjustments(created.Id, adjustments);
                }
            }
        }

        // Masks belong to a screen; a look names the mask it turns on.
        var maskIds = new Dictionary<(Guid Screen, string Mask), IReadOnlyList<Guid>>();
        var masks = 0;
        if (options.ScreenMasks && MasksFromPlan is { } toMasks)
        {
            var plans = result.ScreenPlan.Masks.GroupBy(m => m.ProUuid, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            foreach (var screenLook in result.LookPlans.SelectMany(look => look.ScreenLooks))
            {
                if (screenLook.MaskProUuid is not { } maskUuid || !plans.TryGetValue(maskUuid, out var maskPlan)
                    || !screenIds.TryGetValue(screenLook.ProScreenUuid, out var screenId) || !MayConfigure(screenId)
                    || maskIds.ContainsKey((screenId, maskUuid)))
                    continue;
                var imported = toMasks(maskPlan);
                if (imported.Count == 0) continue;
                var library = Outputs.Masks(screenId).Where(existing => imported.All(m => m.Name != existing.Name)).Concat(imported).ToList();
                Outputs.SetMasks(screenId, library);
                maskIds[(screenId, maskUuid)] = [.. imported.Select(m => m.Id)];
                masks += imported.Count;
            }
        }

        if (options.StageAssignments && SetStageLayout is { } setLayout)
        {
            foreach (var assignment in result.ScreenPlan.StageAssignments)
                if (screenIds.TryGetValue(assignment.ScreenProUuid, out var screenId) && MayConfigure(screenId)) setLayout(screenId, assignment.LayoutId);
        }

        var looks = 0;
        var kept = 0;
        if (options.Looks)
        {
            foreach (var plan in result.LookPlans)
            {
                if (!refreshExisting && Library.Get<OutputPreset>(plan.ProUuid) is not null)
                {
                    kept++;
                    continue;
                }
                var assignments = new List<OutputAssignment>();
                foreach (var screenLook in plan.ScreenLooks)
                {
                    if (!screenIds.TryGetValue(screenLook.ProScreenUuid, out var screenId)) continue;
                    List<string>? lookMasks = screenLook.MaskProUuid is { } maskUuid && maskIds.TryGetValue((screenId, maskUuid), out var ids)
                        ? [.. ids.Select(id => id.ToString().ToUpperInvariant()).Order(StringComparer.Ordinal)]
                        : null;
                    assignments.Add(new OutputAssignment
                    {
                        TargetKind = OutputTargetKind.PlaceholderScreen,
                        TargetId = screenId.ToString().ToUpperInvariant(),
                        EnabledLayers = [.. screenLook.EnabledLayers],
                        SlideThemeId = screenLook.SlideThemeId,
                        MaskIds = lookMasks,
                    });
                }
                Library.Save(new OutputPreset { Id = plan.ProUuid, Name = plan.Name, Assignments = assignments });
                looks++;
            }
        }

        return new Summary(timers, inputs, added.Count, masks, looks, kept);
    }

    private int ApplyTimers(IReadOnlyList<ProTimerPlan> plans, bool refreshExisting)
    {
        var count = 0;
        foreach (var plan in plans)
        {
            if (!refreshExisting && Timers.Timers.Any(t => t.Id == plan.NewId)) continue;
            switch (plan.Mode)
            {
                case ProTimerMode.Countdown countdown:
                    Timers.ImportTimer(plan.NewId, plan.Name, TimerMode.Countdown, countdown.Seconds);
                    break;
                case ProTimerMode.CountdownToTime toTime:
                    var target = TimerController.NextOccurrence(toTime.Hour, toTime.Minute, Clock.GetUtcNow(), Clock.LocalTimeZone);
                    Timers.ImportTimer(plan.NewId, plan.Name, TimerMode.CountdownToTime, targetTime: target);
                    break;
                case ProTimerMode.CountUp countUp:
                    Timers.ImportTimer(plan.NewId, plan.Name, TimerMode.CountUp, countUp.LimitSeconds);
                    break;
                default:
                    continue;
            }
            count++;
        }
        return count;
    }

    /// <summary>ProPresenter's corrections as the output pass's adjustments (zero means "not set").</summary>
    public static OutputAdjustments Adjustments(ProScreenPlan.OutputCorrection correction)
    {
        static OutputPoint? Point(ProScreenPlan.OutputCorrection.Offset? offset) => offset is { } o ? new OutputPoint(o.X, o.Y) : null;
        static double? Value(double raw) => raw == 0 ? null : raw;
        return new OutputAdjustments
        {
            TopLeft = Point(correction.TopLeft),
            TopRight = Point(correction.TopRight),
            BottomLeft = Point(correction.BottomLeft),
            BottomRight = Point(correction.BottomRight),
            Brightness = Value(correction.Brightness),
            Contrast = Value(correction.Contrast),
            Gamma = Value(correction.Gamma),
            BlackLevel = Value(correction.BlackLevel),
            RedLevel = Value(correction.RedLevel),
            GreenLevel = Value(correction.GreenLevel),
            BlueLevel = Value(correction.BlueLevel),
            RotationDegrees = Value(correction.RotationDegrees),
        };
    }

    private static OutputEdgeBlend? Blend(ProScreenPlan.EdgeBlendPlan? plan) =>
        plan is { } p ? new OutputEdgeBlend(p.Width, p.Curve, p.Intensity, p.BlackLift) : null;
}
