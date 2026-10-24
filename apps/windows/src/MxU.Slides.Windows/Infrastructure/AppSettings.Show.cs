namespace MxU.Slides.Windows.Infrastructure;

// The live controls' preferences on this computer (the Mac keeps these in UserDefaults).
public sealed partial class AppSettings
{
    /// <summary>
    /// What Clear All (F1) leaves alone, as the Mac stores it: comma-separated function names
    /// ("inputs,audio"). Null until the checklist is first changed: then it leaves Inputs up.
    /// </summary>
    public string? ClearAllSkips { get; set; }

    /// <summary>How far before the end the transport strip's "jump near the end" button goes, in seconds (0 = the very end).</summary>
    public int TransportSkipToEndSeconds { get; set; } = 30;

    /// <summary>The layer the transport strip looks at ("videos", "loopingVideos"…); null = Foreground Videos.</summary>
    public string? TransportLayer { get; set; }
}
