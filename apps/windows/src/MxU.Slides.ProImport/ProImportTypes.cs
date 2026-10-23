using MxU.Slides.Core.Model;

namespace MxU.Slides.ProImport;

public sealed record ProThemeImportSummary
{
    public required string SourcePath { get; init; }
    public string? ThemeId { get; init; }
    public required string Name { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public int MediaImported { get; init; }
    public ImportSkipReason? Skipped { get; init; }
}

public abstract record ProTimerMode
{
    public sealed record Countdown(double Seconds) : ProTimerMode;
    public sealed record CountdownToTime(int Hour, int Minute) : ProTimerMode;
    public sealed record CountUp(double LimitSeconds) : ProTimerMode;
}

/// <summary>A ProPresenter timer for the app's timer list (timers live outside the library).</summary>
public sealed record ProTimerPlan(string ProUuid, string NewId, string Name, ProTimerMode Mode);

public sealed record ProTimerSeed(string Id, string Name);

/// <summary>A video input for the app's input inventory.</summary>
public sealed record ProInputPlan(string NewId, string Name, CaptureSourceKind Kind, string? SourceId);

public sealed record ProInputSeed(string Id, string Name, string? SourceId);

/// <summary>A ProPresenter look, for the app to turn into an output preset.</summary>
public sealed record ProLookPlan(string ProUuid, string Name, IReadOnlyList<ProLookPlan.ScreenLook> ScreenLooks)
{
    public sealed record ScreenLook
    {
        public required string ProScreenUuid { get; init; }
        public required IReadOnlyList<string> EnabledLayers { get; init; }
        public string? SlideThemeName { get; init; }
        public string? SlideThemeId { get; set; }
        public string? MaskProUuid { get; init; }
    }
}

/// <summary>ProPresenter screens, slices, masks and stage assignments, for the app's output setup.</summary>
public sealed record ProScreenPlan
{
    public sealed record OutputCorrection
    {
        public readonly record struct Offset(double X, double Y);

        public Offset? TopLeft { get; set; }
        public Offset? TopRight { get; set; }
        public Offset? BottomLeft { get; set; }
        public Offset? BottomRight { get; set; }
        public double Brightness { get; set; }
        public double Contrast { get; set; }
        public double Gamma { get; set; }
        public double BlackLevel { get; set; }
        public double RedLevel { get; set; }
        public double GreenLevel { get; set; }
        public double BlueLevel { get; set; }
        public double RotationDegrees { get; set; }

        public bool IsNeutral
        {
            get
            {
                static bool Zero(Offset? offset) => offset is null || offset.Value is { X: 0, Y: 0 };
                return Zero(TopLeft) && Zero(TopRight) && Zero(BottomLeft) && Zero(BottomRight)
                    && Brightness == 0 && Contrast == 0 && Gamma == 0 && BlackLevel == 0
                    && RedLevel == 0 && GreenLevel == 0 && BlueLevel == 0 && RotationDegrees == 0;
            }
        }
    }

    public sealed record EdgeBlendPlan(double Width, double Curve, double? Intensity, double? BlackLift);

    public sealed record Slice
    {
        public required string ProUuid { get; init; }
        public required string Name { get; init; }
        public double X { get; init; }
        public double Y { get; init; }
        public double Width { get; init; }
        public double Height { get; init; }
        public OutputCorrection? Correction { get; init; }
        public EdgeBlendPlan? BlendLeft { get; set; }
        public EdgeBlendPlan? BlendRight { get; set; }
        public EdgeBlendPlan? BlendTop { get; set; }
        public EdgeBlendPlan? BlendBottom { get; set; }
    }

    /// <summary>A mask shape in unit coordinates; <see cref="ShapeKind"/> is the schema's raw value ("rectangle", "path", …).</summary>
    public sealed record MaskShape(double X, double Y, double Width, double Height, string ShapeKind, double CornerRadius, string? PathData);

    public sealed record MaskPlan(string ProUuid, string Name, IReadOnlyList<MaskShape> Shapes);

    public sealed record Screen
    {
        public required string ProUuid { get; init; }
        public required string Name { get; init; }
        public bool IsConfidence { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public OutputCorrection? Correction { get; init; }
        public IReadOnlyList<Slice> Slices { get; init; } = [];
    }

    public sealed record StageAssignment(string ScreenProUuid, string LayoutId);

    public IReadOnlyList<Screen> Screens { get; init; } = [];
    public IReadOnlyList<StageAssignment> StageAssignments { get; init; } = [];
    public IReadOnlyList<MaskPlan> Masks { get; init; } = [];
}

/// <summary>What happened to one .pro / .probundle document.</summary>
public sealed record ProDocumentSummary
{
    public required string SourcePath { get; init; }
    /// <summary>The presentation's id; set when it was imported, and also when it was skipped because it already exists.</summary>
    public string? PresentationId { get; init; }
    public required string Name { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public int MediaImported { get; init; }
    /// <summary>ProPresenter group hot keys, by normalised group name ("verse1" → "a"), for the app's group palette.</summary>
    public IReadOnlyDictionary<string, string> GroupHotKeys { get; init; } = new Dictionary<string, string>();
    public ImportSkipReason? Skipped { get; init; }
    /// <summary>Referenced media files left out because media import was off.</summary>
    public int MediaWithheld { get; init; }

    public bool Imported => PresentationId is not null && Skipped is null;
}

public sealed class ProPlaylistBundleSummary
{
    public required string SourcePath { get; init; }
    public List<string> Services { get; } = [];
    public List<ProDocumentSummary> Documents { get; set; } = [];
    public int MediaImported { get; set; }
    public List<string> Warnings { get; } = [];
    public string? FailureReason { get; set; }
}

/// <summary>A ProPresenter macro collection, as a combo folder.</summary>
public sealed record ProComboFolder(string Name, IReadOnlyList<string> ComboIds);

/// <summary>Everything <see cref="ProPresenterImporter.ImportFilesAsync"/> did, grouped by kind of file.</summary>
public sealed record ProImportResult(
    IReadOnlyList<ProDocumentSummary> Documents,
    IReadOnlyList<ProPlaylistBundleSummary> Playlists,
    IReadOnlyList<ProThemeImportSummary> Themes);

/// <summary>
/// The outcome of a workspace import. Documents the Windows library can hold (presentations, themes,
/// overlays, alerts, stage layouts, services, media) are already saved; the rest are plans or mapped
/// documents for the app to apply (timers, inputs, screens, looks, and — until the library stores
/// them — combos, scheduler triggers and MIDI devices).
/// </summary>
public sealed class ProWorkspaceImportResult
{
    public List<ProDocumentSummary> Presentations { get; } = [];
    public List<string> ThemesImported { get; } = [];
    public List<string> OverlaysImported { get; } = [];
    public List<string> AlertsImported { get; } = [];
    public List<string> LayoutsImported { get; } = [];
    public List<string> ServicesImported { get; } = [];
    public int MediaImported { get; set; }
    public int MediaWithheld { get; set; }
    public List<string> MediaFoldersImported { get; set; } = [];
    public List<string> Warnings { get; } = [];

    /// <summary>Macros mapped to action combos (not saved: the Windows library has no combo store yet).</summary>
    public List<ActionCombo> Combos { get; } = [];
    /// <summary>Macro collections, as combo folders: folder name and member combo ids.</summary>
    public List<ProComboFolder> ComboFolders { get; } = [];
    /// <summary>Calendar events mapped to scheduler triggers (not saved), for a "ProPresenter Calendar" folder.</summary>
    public List<ScheduleTrigger> ScheduleTriggers { get; } = [];
    /// <summary>MIDI communication devices (not saved).</summary>
    public List<MIDIDevice> MidiDevices { get; } = [];

    public ProScreenPlan ScreenPlan { get; set; } = new();
    public List<ProTimerPlan> TimerPlans { get; set; } = [];
    public List<ProInputPlan> InputPlans { get; set; } = [];
    public List<ProLookPlan> LookPlans { get; set; } = [];
    public string? LiveLookUuid { get; set; }
    public Dictionary<string, string> GroupHotKeys { get; } = [];

    public int SkippedExisting { get; set; }
    public List<string> SkippedEdited { get; set; } = [];
}
