namespace MxU.Slides.Windows.Infrastructure;

// Motion templates' choices on this computer (Phase 11): they name this PC's programs and folders.
public sealed partial class AppSettings
{
    /// <summary>An aerender.exe chosen by hand (Locate After Effects…), for an After Effects installed somewhere unusual; null to look in the usual places.</summary>
    public string? AfterEffectsAerender { get; set; }

    /// <summary>The output module template After Effects renders with (After Effects' own name for it).</summary>
    public string? AfterEffectsOutputModule { get; set; }

    /// <summary>The colour the last template video was rendered over, "#RRGGBB".</summary>
    public string? MotionExportColor { get; set; }
}
