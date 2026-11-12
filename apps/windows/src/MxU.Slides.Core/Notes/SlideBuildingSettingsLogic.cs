using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Notes;

/// <summary>
/// The library's one SlideBuildingSettings document: the design map Make Slides uses, and the
/// themes for notes and imported lyrics. It's shared with the Mac (a team document), so every
/// computer makes slides the same way. Port of SlideBuildingSettingsLogic.swift; the Mac's
/// UserDefaults copy (a Mac's settings from before the document existed) has no Windows twin.
/// </summary>
public static class SlideBuildingSettingsLogic
{
    /// <summary>The document once the library has one; this computer's own settings until then.</summary>
    public static SlideBuildingSettings Effective(SlideBuildingSettings? document, SlideBuildingSettings defaults) => document ?? defaults;

    public static SlideBuildingSettings New() => new() { Id = SlideBuildingSettings.WellKnownId };

    public static bool IsUnset(this SlideBuildingSettings s) =>
        s.DesignMap is null && s.MessageNotesThemeId is null && s.LyricsImportThemeId is null && s.LyricsImportDesign is null;

    public const string DefaultLyricsDesign = "Lyrics";

    /// <summary>The design imported lyrics take.</summary>
    public static string LyricsDesign(this SlideBuildingSettings s) =>
        string.IsNullOrEmpty(s.LyricsImportDesign) ? DefaultLyricsDesign : s.LyricsImportDesign;

    /// <summary>The theme (and design) lyrics and notes are made in. A new theme forgets the old theme's design.</summary>
    public static void SetLyricsLook(this SlideBuildingSettings s, string themeId, string? design)
    {
        var themeChanged = themeId != (s.LyricsImportThemeId ?? "");
        s.LyricsImportThemeId = themeId;
        if (design is not null) s.LyricsImportDesign = design.Length == 0 || themeId.Length == 0 ? null : design;
        else if (themeChanged) s.LyricsImportDesign = null;
    }

    /// <summary>A fresh library starts on its starter theme, while nobody has chosen one (an empty choice counts).</summary>
    public static void StartOnTheme(this SlideBuildingSettings s, string themeId)
    {
        if (s.LyricsImportThemeId is not null) return;
        s.SetLyricsLook(themeId, null);
    }

    public static SlideDesignMap SlideDesignMap(this SlideBuildingSettings s) => Notes.SlideDesignMap.Decode(s.DesignMap);

    public static void SetSlideDesignMap(this SlideBuildingSettings s, SlideDesignMap map) => s.DesignMap = map.Encoded();

    /// <summary>The theme a note's slides are made in: the lyrics theme, else the library's starter theme.</summary>
    public static string NotesThemeId(this SlideBuildingSettings s, string? starter) => s.LyricsImportThemeId ?? starter ?? "";

    /// <summary>Fields the document leaves unset taken from this computer's own.</summary>
    public static void FillUnset(this SlideBuildingSettings s, SlideBuildingSettings own)
    {
        s.DesignMap ??= own.DesignMap;
        s.MessageNotesThemeId ??= own.MessageNotesThemeId;
        s.LyricsImportThemeId ??= own.LyricsImportThemeId;
        s.LyricsImportDesign ??= own.LyricsImportDesign;
    }
}

/// <summary>When a library gets its settings document: never while held back, once it's read, from the shared one if any.</summary>
public static class SlideBuildingSeed
{
    public enum Step
    {
        /// <summary>Nothing to do yet.</summary>
        None,

        /// <summary>The shared library has one: use it.</summary>
        Adopt,

        /// <summary>Write this computer's own settings as the document.</summary>
        Seed,
    }

    public static Step StepFor(bool held, bool indexLoaded, bool inCloud, SlideBuildingSettings own)
    {
        if (held || !indexLoaded) return Step.None;
        if (inCloud) return Step.Adopt;
        return own.IsUnset() ? Step.None : Step.Seed;
    }
}
