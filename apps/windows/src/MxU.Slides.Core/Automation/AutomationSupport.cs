using System.Globalization;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Automation;

// Show automation: action combos and the action router, the scheduler, timecode
// in (LTC/MTC) and presentation timelines. Ported from the Mac app's PresenterCore
// (Library/, Timeline/) and the logic halves of its ActionRouter, SchedulerController,
// TimecodeInput and PresentationTimelineController. Everything here is platform
// neutral: the WPF app supplies the hardware and the show through small interfaces
// (IShowActions, ISchedulerHost, ITimelineHost, ITimecodeInputs).
//
// One Swift habit matters throughout: Swift structs are values, so `var cue = other`
// makes a copy. The generated C# models are classes (references), so this port
// copies a document part before changing it (see Copy below) and never edits a
// caller's object in place.

/// <summary>A closed range of seconds or points, like Swift's <c>ClosedRange&lt;Double&gt;</c>.</summary>
public readonly record struct ClosedRange(double Lower, double Upper)
{
    public bool Contains(double value) => value >= Lower && value <= Upper;

    public double Length => Upper - Lower;
}

/// <summary>Copies of the generated document parts, so a port of Swift's value semantics can edit freely.</summary>
public static class AutomationCopies
{
    /// <summary>
    /// A copy of a timeline cue (its fades copied too; its actions list is a new list of the same actions).
    /// Written out by hand because cues are copied on every timeline edit; a test checks no field is missed.
    /// </summary>
    public static TimelineCue Copy(this TimelineCue cue) => new()
    {
        Id = cue.Id,
        Lane = cue.Lane,
        At = cue.At,
        Duration = cue.Duration,
        SectionId = cue.SectionId,
        SectionOccurrence = cue.SectionOccurrence,
        SlideId = cue.SlideId,
        MediaId = cue.MediaId,
        OverlayId = cue.OverlayId,
        AlertId = cue.AlertId,
        LiveInputId = cue.LiveInputId,
        Actions = cue.Actions is null ? null : [.. cue.Actions],
        InPoint = cue.InPoint,
        FadeIn = cue.FadeIn?.Copy(),
        FadeOut = cue.FadeOut?.Copy(),
        EdgeFades = cue.EdgeFades,
    };

    public static ClipFade Copy(this ClipFade fade) => new()
    {
        Seconds = fade.Seconds,
        Shape = fade.Shape,
        Midpoint = fade.Midpoint,
        Placement = fade.Placement,
    };

    /// <summary>A copy of the timeline that shares its cues (replace <see cref="PresentationTimeline.Cues"/> to edit them).</summary>
    public static PresentationTimeline Copy(this PresentationTimeline timeline) => new()
    {
        Id = timeline.Id,
        ArrangementId = timeline.ArrangementId,
        TimecodeEnabled = timeline.TimecodeEnabled,
        TimecodeStart = timeline.TimecodeStart,
        Length = timeline.Length,
        Cues = [.. timeline.Cues],
        MapLayout = timeline.MapLayout is null ? null : [.. timeline.MapLayout],
    };

    public static TrackChannel Copy(this TrackChannel channel) => new()
    {
        Id = channel.Id,
        Name = channel.Name,
        Role = channel.Role,
        ColorHex = channel.ColorHex,
        MatchNames = channel.MatchNames is null ? null : [.. channel.MatchNames],
        Mono = channel.Mono,
    };

    public static AudioSection Copy(this AudioSection section) => new()
    {
        Id = section.Id,
        Name = section.Name,
        Start = section.Start,
        SectionId = section.SectionId,
    };

    /// <summary>A copy of the map with its own section list (the recording is shared).</summary>
    public static SongMap Copy(this SongMap map) => new()
    {
        Recording = [.. map.Recording],
        Sections = [.. map.Sections],
        Source = map.Source,
    };
}

/// <summary>Swift's <c>Int(String)</c>: an optional sign then ASCII digits, nothing else (no spaces).</summary>
internal static class SwiftParse
{
    public static int? Int(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return null;
        var digits = text[0] is '+' or '-' ? text[1..] : text;
        if (digits.IsEmpty) return null;
        foreach (var c in digits)
            if (c is < '0' or > '9') return null;
        return int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    /// <summary>Swift's <c>Double(String)</c> for the plain decimal and exponent forms files use.</summary>
    public static double? Double(string? text) =>
        text is not null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>Swift's <c>split(whereSeparator:)</c>: pieces between separators, empty pieces left out.</summary>
    public static List<string> Split(string text, Func<char, bool> isSeparator)
    {
        var pieces = new List<string>();
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && !isSeparator(text[i])) continue;
            if (i > start) pieces.Add(text[start..i]);
            start = i + 1;
        }
        return pieces;
    }

    /// <summary>
    /// Foundation's <c>localizedStandardCompare</c>: how Finder sorts names, ignoring case and
    /// reading digit runs as numbers ("Keys 2" before "Keys 10").
    /// </summary>
    public static int StandardCompare(string a, string b) =>
        string.Compare(a, b, CultureInfo.InvariantCulture, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);
}

/// <summary>Swift's <c>(x).rounded()</c> and friends, which round halves away from zero.</summary>
internal static class SwiftMath
{
    public static double Rounded(double value) => Math.Round(value, MidpointRounding.AwayFromZero);
}
