using MxU.Slides.Core.Control;

namespace MxU.Slides.Windows.Infrastructure;

// Phase 8's operator choices on this computer. None of them is shared with the Mac or another PC:
// each desk can have its own shortcuts, its own volunteer PIN and its own plan time.
public sealed partial class AppSettings
{
    /// <summary>This PC's shortcuts: only what was changed from the defaults (Core/Control/KeyboardControl.cs).</summary>
    public KeyCommandMap KeyMap { get; set; } = new();

    /// <summary>Settings › Present › Volunteer Mode: adds Volunteer beside Show and Edit.</summary>
    public bool VolunteerModeEnabled { get; set; }

    /// <summary>
    /// The app is in volunteer mode now. Kept here, so a volunteer can't get out of it by closing
    /// the app and opening it again (the Mac keeps its run-only flag the same way).
    /// </summary>
    public bool VolunteerModeOn { get; set; }

    /// <summary>The PIN that leaves volunteer mode, as a salted hash (never the digits); null for none.</summary>
    public string? VolunteerPinHash { get; set; }

    /// <summary>The panels a volunteer keeps ("timers,alerts…"); null: all of them.</summary>
    public string? VolunteerPanels { get; set; }

    /// <summary>The slide area shows the whole service as one scroll instead of one presentation's grid.</summary>
    public bool ContinuousView { get; set; }

    /// <summary>The service headers collapsed in the list on this PC, by service id (the Mac keeps "runOrder.collapsed.&lt;id&gt;").</summary>
    public Dictionary<string, List<string>> CollapsedHeaders { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The plan time this PC runs, by service id (a Planning Center plan with 9:00 and 11:00).</summary>
    public Dictionary<string, string> PlanTimes { get; set; } = new(StringComparer.Ordinal);
}
