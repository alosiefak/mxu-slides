namespace MxU.Slides.Windows.Infrastructure;

public sealed partial class AppSettings
{
    /// <summary>
    /// The Screen Map: where this PC's screens sit in the room (Core/Outputs/ScreenMap.cs). It is
    /// this PC's, like the screens themselves, so it lives here rather than in the library (the
    /// Mac keeps it in its own defaults, "outputs.screenMap"). Null until a map is made.
    /// </summary>
    public MxU.Slides.Core.Outputs.ScreenMap? ScreenMap { get; set; }
}
