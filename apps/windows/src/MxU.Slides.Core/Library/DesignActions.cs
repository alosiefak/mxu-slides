using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Library;

/// <summary>
/// A theme design's own actions (a "Lower third" design that switches the output preset, say):
/// a slide drawn with the design carries them too. Port of the parts of the Mac's
/// DesignActions.swift that Make Slides needs: finding a design by name, and a slide taking on
/// its design's actions.
/// </summary>
public static class DesignActions
{
    /// <summary>The theme's design with that name (any case), else its first design; null when it has none.</summary>
    public static Slide? Design(this Theme theme, string? name)
    {
        var designs = theme.Slides ?? [];
        if (!string.IsNullOrEmpty(name)
            && designs.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)) is { } match)
            return match;
        return designs.FirstOrDefault();
    }

    /// <summary>
    /// The slide takes on <paramref name="design"/>'s actions, each with a new id. Actions the
    /// <paramref name="previous"/> design gave it, and the new one doesn't, are dropped; the slide's
    /// own actions stay.
    /// </summary>
    public static void AdoptActions(this Slide slide, Slide? design, Slide? previous = null, Func<string>? newId = null)
    {
        newId ??= Lyrics.SwiftString.Uuid;
        var given = design?.Actions ?? [];
        var old = previous?.Actions ?? [];
        var own = (slide.Actions ?? []).ToList();
        own.RemoveAll(action => old.Any(o => SameWork(o, action)) && !given.Any(g => SameWork(g, action)));
        foreach (var action in given)
        {
            if (own.Any(o => SameWork(action, o))) continue;
            var copy = MxuJson.Clone(action);
            copy.Id = newId();
            own.Add(copy);
        }
        slide.Actions = own.Count == 0 ? null : own;
    }

    /// <summary>The same action but for its id.</summary>
    public static bool SameWork(SlideAction a, SlideAction b)
    {
        var aligned = MxuJson.Clone(b);
        aligned.Id = a.Id;
        return MxuJson.StructurallyEqual(a, aligned);
    }
}
