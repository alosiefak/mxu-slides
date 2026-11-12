namespace MxU.Slides.Windows.Infrastructure;

/// <summary>What this PC remembers about making slides from notes (never shared: it's in settings.json).</summary>
public sealed partial class AppSettings
{
    /// <summary>
    /// How Make Slides last picked passages on this PC ("PickForMe", "BoldLines", "Structure" or
    /// "Nothing"), so the window opens the same way next time. The Mac keeps the same choice in its
    /// own defaults ("makeSlides.autoMark"). Null until a choice is made: Pick for me.
    /// </summary>
    public string? MakeSlidesAutoMark { get; set; }
}
