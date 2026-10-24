namespace MxU.Slides.Core.Outputs;

/// <summary>
/// What an operator preview shows: one screen (exactly what that screen's outputs show, its look
/// and layers included), or a stage layout that no screen may be showing yet ("layout::" + its id).
/// Port of PresenterCore/Library/PreviewTargetLogic.swift, plus the screen list and aspect that
/// the Mac's LivePanel.swift (PreviewTargets) works out from its outputs.
/// </summary>
public static class PreviewTargets
{
    /// <summary>One entry of the preview picker.</summary>
    public sealed record Choice(string Id, string Name);

    public const string LayoutPrefix = "layout::";

    public static string LayoutTarget(string layoutId) => LayoutPrefix + layoutId;

    /// <summary>The layout a target names, or null when it names a screen.</summary>
    public static string? LayoutId(string target) =>
        target.StartsWith(LayoutPrefix, StringComparison.Ordinal) && target.Length > LayoutPrefix.Length ? target[LayoutPrefix.Length..] : null;

    /// <summary>The stored pick, else (it went away, or nothing was picked) the first screen, else the first layout.</summary>
    public static Choice? Resolve(string? stored, IReadOnlyList<Choice> screens, IReadOnlyList<Choice> layouts) =>
        screens.Concat(layouts).FirstOrDefault(c => c.Id == stored) ?? screens.FirstOrDefault() ?? layouts.FirstOrDefault();

    /// <summary>A layout previews at its own canvas's shape (a tall rail, say); unknown sizes read 16:9.</summary>
    public static double Aspect(int? canvasWidth, int? canvasHeight) =>
        canvasWidth is > 0 && canvasHeight is > 0 ? (double)canvasWidth.Value / canvasHeight.Value : 16.0 / 9.0;

    /// <summary>Every screen in the setup, by its id as text (the Mac lists its placeholder screens, then direct displays, which Windows doesn't have).</summary>
    public static List<Choice> Screens(OutputConfiguration config) =>
        [.. config.Screens.Select(screen => new Choice(SwiftUuid.Format(screen.Id), screen.Name))];

    /// <summary>The shape a target previews at: a layout's canvas, a screen's size, else 16:9.</summary>
    public static double Aspect(string? target, OutputConfiguration config, IReadOnlyDictionary<string, double>? layoutAspects = null)
    {
        if (target is null) return 16.0 / 9.0;
        if (LayoutId(target) is { } layoutId) return layoutAspects?.GetValueOrDefault(layoutId, 16.0 / 9.0) ?? 16.0 / 9.0;
        return SwiftUuid.Parse(target) is { } id && config.Screen(id) is { Height: > 0 } screen ? (double)screen.Width / screen.Height : 16.0 / 9.0;
    }
}
