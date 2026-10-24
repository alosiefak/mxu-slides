using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Library;

/// <summary>
/// The library's group palette: the section names (Verse 1, Chorus…) with their colours and
/// hot keys. It lives in the library as one well-known document, so hot keys set here, or
/// brought in from ProPresenter, apply to every song. Port of GroupPaletteLogic.swift and the
/// GroupPalette hot-key half of KeyboardControl.swift (PresenterCore).
/// </summary>
public static class GroupPalettes
{
    /// <summary>The palette's document id, as on the Mac.</summary>
    public const string WellKnownId = "group-palette";

    /// <summary>A new palette: the starter groups, every hot key at its default (unset).</summary>
    public static GroupPalette Defaults() => new()
    {
        Id = WellKnownId,
        Groups = [.. StarterContent.GroupPalette.Select(g => new GroupDefinition { Id = g.Id, Name = g.Name, ColorHex = g.ColorHex })],
    };

    /// <summary>The library's palette, or the defaults when it has none yet.</summary>
    public static GroupPalette Current(LibraryStore library) => library.Get<GroupPalette>(WellKnownId) ?? Defaults();

    /// <summary>Each group's colour by normalized name (the first group with a name wins).</summary>
    public static Dictionary<string, string> ColorsByNormalizedName(GroupPalette palette)
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in palette.Groups) table.TryAdd(HotKeyJump.NormalizedName(group.Name), group.ColorHex);
        return table;
    }

    /// <summary>
    /// Gives groups whose hot key isn't set the imported letter ("first import wins"). An
    /// explicit "none" (empty) is kept, and a letter equal to the default is left unset.
    /// </summary>
    public static void FillUnsetHotKeys(GroupPalette palette, IReadOnlyDictionary<string, string> imported)
    {
        foreach (var group in palette.Groups)
        {
            if (group.HotKey is not null) continue;
            var name = HotKeyJump.NormalizedName(group.Name);
            if (ImportedLetter(imported, name) is not { } letter) continue;
            if (HotKeyJump.DefaultHotKeys.GetValueOrDefault(name) == letter) continue;
            group.HotKey = letter;
        }
    }

    /// <summary>The imported letter for a group, if it is a single letter.</summary>
    public static string? ImportedLetter(IReadOnlyDictionary<string, string> imported, string normalizedName) =>
        imported.TryGetValue(normalizedName, out var raw) && raw.ToLowerInvariant() is { Length: 1 } letter && char.IsLetter(letter[0]) ? letter : null;
}
