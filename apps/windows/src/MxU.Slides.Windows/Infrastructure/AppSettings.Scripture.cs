namespace MxU.Slides.Windows.Infrastructure;

// The Bible window's choices on this computer, so it opens the way the operator left it.
public sealed partial class AppSettings
{
    /// <summary>The translation passages are looked up in ("kjv", "web", "asv").</summary>
    public string ScriptureTranslation { get; set; } = "kjv";

    /// <summary>A second translation shown beside the first in the Bible window; null for none.</summary>
    public string? ScriptureCompare { get; set; }

    /// <summary>True to put the second translation on the slides too, side by side with the first.</summary>
    public bool ScriptureCompareOnSlides { get; set; }

    /// <summary>"FullScreen", "LowerThird" or "SideThird" (<see cref="Core.Scripture.ScriptureLayout"/>).</summary>
    public string ScriptureLayout { get; set; } = nameof(Core.Scripture.ScriptureLayout.FullScreen);

    /// <summary>At most this many verses on a slide; 0 = as many as fit.</summary>
    public int ScriptureVersesPerSlide { get; set; }

    public bool ScriptureVerseNumbers { get; set; } = true;

    public bool ScriptureReferenceLine { get; set; } = true;

    /// <summary>The theme scripture slides are made in; null = the default theme.</summary>
    public string? ScriptureThemeId { get; set; }
}
