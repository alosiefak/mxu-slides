using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Stage;

/// <summary>
/// Fills text boxes linked to a live source (current slide, timers, clock…).
/// Port of SlideScene/LinkedText.swift.
/// </summary>
public static class LinkedText
{
    /// <summary>Chord display (transposition and notation) for linked slide text.</summary>
    internal static Func<List<ChordPlacement>, string?, string?, ChordNotation, List<ChordPlacement>> DisplayPlacements { get; set; } =
        Lyrics.ChordMath.DisplayPlacements;

    public static string? Resolve(TextLink link, ConfidenceInfo info, DateTimeOffset at)
    {
        if (link.SourceObjectName is { Length: > 0 } name
            && link.Source is TextSourceKind.CurrentSlide or TextSourceKind.NextSlide or TextSourceKind.LastSlide)
        {
            var slide = link.Source switch
            {
                TextSourceKind.CurrentSlide => info.Current,
                TextSourceKind.NextSlide => info.Next,
                _ => info.Last,
            };
            var match = slide?.ObjectTexts?.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));
            return NonEmpty(match?.Text);
        }
        switch (link.Source)
        {
            case TextSourceKind.CurrentSlide: return NonEmpty(info.Current?.Body);
            case TextSourceKind.NextSlide: return NonEmpty((SlideText(link, info) ?? info.Next)?.Body);
            case TextSourceKind.LastSlide: return NonEmpty(info.Last?.Body);
            case TextSourceKind.Clock: return ClockString(at, link.ClockFormat);
            case TextSourceKind.Timer:
                return Timer(link, info)?.DisplayString(at, link.TimerFormat, link.TimerPattern);
            case TextSourceKind.VideoCountdown:
            {
                if (info.VideoCountdownFor(link.VideoLayer) is not { Duration: > 0 } video) return null;
                var timecode = TimerSnapshot.Text(video.Remaining(at), link.TimerFormat, link.TimerPattern);
                return link.ShowsVideoName == false ? timecode : $"{video.Name}  {timecode}";
            }
            case TextSourceKind.SlidePosition:
                return info.SlidePosition is { } position ? $"{position.Index} of {position.Total}" : null;
            case TextSourceKind.CurrentServiceItem: return NonEmpty(info.CurrentItemName);
            case TextSourceKind.NextServiceItem: return NonEmpty(info.NextItemName);
            case TextSourceKind.CurrentGroup: return NonEmpty(info.CurrentGroupName);
            case TextSourceKind.CurrentPresentation: return NonEmpty(info.CurrentPresentationName);
            case TextSourceKind.NextServiceTitle: return NonEmpty(info.NextServiceTitle);
            case TextSourceKind.Timecode: return info.Timecode?.Text(at);
            case TextSourceKind.StageMessage:
                if (info.Alert is not { ShowsOnConfidence: true } alert || !info.AlertVisible) return null;
                return NonEmpty(ResolvedAlertMessage(alert.Message, info, at));
            default: return null;
        }
    }

    /// <summary>
    /// The objects with every linked box's text (and chords, and timer warning ink)
    /// filled in. Unlinked objects come back as the same instances; linked ones are copies.
    /// </summary>
    public static List<SlideObject> ResolvedObjects(
        IEnumerable<SlideObject> objects, ConfidenceInfo info, DateTimeOffset at, Func<TextLink, TextLink>? reading = null) =>
        objects.Select(o => ResolvedObject(o, info, at, reading)).ToList();

    private static SlideObject ResolvedObject(SlideObject o, ConfidenceInfo info, DateTimeOffset at, Func<TextLink, TextLink>? reading)
    {
        if (o.TextLink is not { } stored) return o;
        var link = reading?.Invoke(stored) ?? stored;
        var resolved = MxuJson.Clone(o);
        resolved.Text = Resolve(link, info, at) ?? "";

        var named = !string.IsNullOrEmpty(link.SourceObjectName);
        if (SlideText(link, info) is { } slide)
        {
            var chords = named ? [] : slide.Chords.Select(Copy).ToList();
            if (link.MaxLines is { } maxLines && maxLines > 0)
            {
                var lines = resolved.Text.Split('\n');
                if (lines.Length > maxLines) resolved.Text = string.Join("\n", lines.Take(maxLines));
                chords = chords.Where(c => c.Line < maxLines).ToList();
            }
            chords = DisplayPlacements(chords, slide.MusicKey, slide.DisplayKey, o.TextStyle?.ChordNotation ?? ChordNotation.Chords);
            resolved.Chords = chords.Count == 0 ? null : chords;
        }
        if (WarningColorHex(link, info, at) is { } ink)
        {
            resolved.TextStyle ??= new TextStyle();
            resolved.TextStyle.ColorHex = ink;
        }
        return resolved;
    }

    private static ChordPlacement Copy(ChordPlacement c) => new() { Line = c.Line, Column = c.Column, Symbol = c.Symbol };

    /// <summary>The slide a slide-sourced link reads; next-slide links that include steps read the next step first.</summary>
    public static SlideText? SlideText(TextLink link, ConfidenceInfo info) => link.Source switch
    {
        TextSourceKind.CurrentSlide => info.Current,
        TextSourceKind.NextSlide => link.IncludeSteps == true ? info.NextStep ?? info.Next : info.Next,
        TextSourceKind.LastSlide => info.Last,
        _ => null,
    };

    public static string? WarningColorHex(TextLink link, ConfidenceInfo info, DateTimeOffset at)
    {
        if (link.UsesWarningColor == false) return null;
        switch (link.Source)
        {
            case TextSourceKind.Timer:
                return Timer(link, info)?.ActiveWarning(at)?.ColorHex;
            case TextSourceKind.VideoCountdown:
                if (info.VideoCountdownFor(link.VideoLayer) is not { Duration: > 0 } video) return null;
                return video.Remaining(at) <= TimerSnapshot.WarningThreshold ? TimerWarning.AmberHex : null;
            default:
                return null;
        }
    }

    /// <summary>The sources offered when authoring a link; next service title only stays for boxes already using it.</summary>
    public static List<TextSourceKind> AuthoringSources(TextSourceKind? current) =>
        Enum.GetValues<TextSourceKind>().Where(s => s != TextSourceKind.NextServiceTitle || s == current).ToList();

    public static bool IsTimeVarying(TextLink link) =>
        link.Source is TextSourceKind.Clock or TextSourceKind.Timer or TextSourceKind.VideoCountdown or TextSourceKind.Timecode;

    public static bool TicksSubSecond(TextLink link) => link.Source switch
    {
        TextSourceKind.Timer or TextSourceKind.VideoCountdown => link.TimerPattern is { } pattern && TimerPattern.HasFraction(pattern),
        TextSourceKind.Timecode => true,
        _ => false,
    };

    public const double SubSecondTickInterval = 1.0 / 30;

    public static readonly DateTimeOffset PreviewDate = DateTimeOffset.FromUnixTimeSeconds(1_784_800_000);

    /// <summary>Sample data so every source reads something in the layout editor.</summary>
    public static readonly ConfidenceInfo PreviewInfo = new()
    {
        Current = new SlideText("Amazing grace, how sweet the sound\nThat saved a wretch like me")
        {
            Chords = [Chord(0, 0, "G"), Chord(0, 24, "C"), Chord(0, 30, "G"), Chord(1, 5, "Em"), Chord(1, 13, "D")],
            MusicKey = "G",
        },
        Next = new SlideText("I once was lost, but now am found\nWas blind, but now I see")
        {
            Chords = [Chord(0, 0, "G"), Chord(0, 17, "G/B"), Chord(1, 0, "C"), Chord(1, 19, "G")],
            MusicKey = "G",
        },
        Last = new SlideText("'Twas grace that taught my heart to fear\nAnd grace my fears relieved"),
        Alert = new CueAlert("Mics hot after this song", AlertBehavior.Persist, AlertTarget.Confidence),
        AlertVisible = true,
        Timers = [new TimerSnapshot("Sermon", TimerMode.Countdown, banked: 753.58, durationSeconds: 1800)],
        VideoCountdown = new VideoCountdown("Announcements", 300, 180, PreviewDate, IsPlaying: false),
        VideoCountdowns = new Dictionary<string, VideoCountdown>
        {
            ["videos"] = new("Announcements", 300, 180, PreviewDate, IsPlaying: false),
            ["loopingVideos"] = new("Background Loop", 240, 60, PreviewDate, IsPlaying: false),
            ["slide"] = new("Slide Video", 90, 35, PreviewDate, IsPlaying: false),
            ["overlays"] = new("Overlay Video", 60, 20, PreviewDate, IsPlaying: false),
        },
        SlidePosition = new SlidePosition(3, 12),
        CurrentItemName = "Amazing Grace",
        CurrentGroupName = "Verse 1",
        NextItemName = "Welcome & Announcements",
        CurrentPresentationName = "Amazing Grace",
        NextServiceTitle = "Sunday Gathering",
        Timecode = new TimecodeDisplay(3662.5, TimecodeRate.Fps30, PreviewDate, IsRunning: false),
    };

    private static ChordPlacement Chord(int line, int column, string symbol) => new() { Line = line, Column = column, Symbol = symbol };

    public static List<SlideObject> PreviewObjects(IEnumerable<SlideObject> objects) =>
        ResolvedObjects(objects, PreviewInfo, PreviewDate, PreviewLink);

    public static string SampleText(TextLink link) => Resolve(PreviewLink(link), PreviewInfo, PreviewDate) ?? "";

    /// <summary>Previews read the sample timer whatever timer the box names.</summary>
    private static TextLink PreviewLink(TextLink link)
    {
        var sampled = MxuJson.Clone(link);
        sampled.TimerId = null;
        return sampled;
    }

    /// <summary>Fills "{Timer Name}" slots with the named timer's live digits; other slots pass through untouched.</summary>
    public static string ResolvedAlertMessage(string message, ConfidenceInfo info, DateTimeOffset at)
    {
        if (!message.Contains('{')) return message;
        var values = new Dictionary<string, string>();
        foreach (var name in AlertTokens.TokenNames(message))
        {
            var timer = info.Timers.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            values[name] = timer is null ? $"{{{name}}}" : timer.DisplayString(at);
        }
        return AlertTokens.Compose(message, values);
    }

    public static bool AlertMessageHasTimerToken(string message, IEnumerable<TimerSnapshot> timers)
    {
        if (!message.Contains('{')) return false;
        var list = timers as IReadOnlyCollection<TimerSnapshot> ?? timers.ToList();
        return AlertTokens.TokenNames(message).Any(name =>
            list.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The timer a link names, or the primary timer when it names none. A deleted timer reads as null.</summary>
    public static TimerSnapshot? Timer(TextLink link, ConfidenceInfo info) =>
        !string.IsNullOrEmpty(link.TimerId)
            ? info.Timers.FirstOrDefault(t => t.Id == link.TimerId)
            : ConfidenceSceneBuilder.PrimaryTimer(info.Timers);

    /// <summary>The clock read: the booth default (h:mm) without a format, else the Unicode date pattern.</summary>
    public static string ClockString(DateTimeOffset at, string? format) =>
        string.IsNullOrEmpty(format) ? ConfidenceSceneBuilder.ClockString(at) : DateFormatPattern.Format(at, format);

    private static string? NonEmpty(string? text) => string.IsNullOrEmpty(text) ? null : text;
}
