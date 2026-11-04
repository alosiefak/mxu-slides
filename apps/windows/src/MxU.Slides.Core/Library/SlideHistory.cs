using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Library;

/// <summary>
/// A slide's earlier versions, newest first, kept on the slide itself (its shared "history"
/// field) so a change made for someone (an update from the notes) can be undone on either
/// computer. Port of SlideHistory.swift.
/// </summary>
public static class SlideHistory
{
    /// <summary>How many earlier versions a slide keeps.</summary>
    public const int Limit = 10;

    /// <summary><paramref name="replacement"/> under the current slide's id, with the current slide at the top of its history.</summary>
    public static Slide Pushing(Slide current, Slide replacement, string reason, string now)
    {
        var archived = MxuJson.Clone(current);
        archived.History = null;
        var next = MxuJson.Clone(replacement);
        next.Id = current.Id;
        next.History = new[] { new SlideVersion { At = now, Reason = reason, Slide = archived } }
            .Concat((current.History ?? []).Select(MxuJson.Clone)).Take(Limit).ToList();
        return next;
    }

    /// <summary>Puts an earlier version back, keeping the slide's "keep my words" choice, and remembers what it replaced.</summary>
    public static Slide Restoring(SlideVersion version, Slide current, string now)
    {
        var restored = MxuJson.Clone(version.Slide);
        restored.KeepWords = current.KeepWords;
        return Pushing(current, restored, "Before restore", now);
    }
}
