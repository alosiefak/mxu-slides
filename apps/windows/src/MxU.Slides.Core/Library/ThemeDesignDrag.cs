using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Library;

/// <summary>
/// What a theme design carries when it's dragged (from the theme designs browser onto a slide):
/// "mxuthemedesign::&lt;theme id&gt;::&lt;design id&gt;". A slide it's dropped on takes that theme
/// and design, as Apply Theme would.
/// <para>Port of the Mac's PresenterCore/Library/NewSlide.swift (<c>ThemeDesignDrag</c>) and AppModel's <c>themeDesign(_:)</c>.</para>
/// </summary>
public static class ThemeDesignDrag
{
    public const string Prefix = "mxuthemedesign::";

    public static string Payload(string themeId, string designId) => Prefix + themeId + "::" + designId;

    /// <summary>The theme and design a payload names, or null when it isn't a theme design's.</summary>
    public static (string ThemeId, string DesignId)? Parse(string? payload)
    {
        if (payload is null || !payload.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var parts = payload[Prefix.Length..].Split("::");
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0) return null;
        return (parts[0], parts[1]);
    }

    /// <summary>The theme and the design itself, when the payload names a design that's still there.</summary>
    public static (string ThemeId, Slide Design)? Resolve(string? payload, Func<string, Theme?> theme)
    {
        if (Parse(payload) is not { } drag || theme(drag.ThemeId)?.Slides?.FirstOrDefault(d => d.Id == drag.DesignId) is not { } design) return null;
        return (drag.ThemeId, design);
    }
}
