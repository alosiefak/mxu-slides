using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Stage;

/// <summary>
/// The data half of SlideScene/ConfidenceScene.swift's ConfidenceSceneBuilder:
/// lyric extraction, next-slide choice, the primary timer and the booth clock.
/// The scene(...) render functions live with the render model.
/// </summary>
public static partial class ConfidenceSceneBuilder
{
    private static readonly ConfidenceLayout BuiltIn = MakeBuiltIn();

    private static ConfidenceLayout MakeBuiltIn()
    {
        var layout = ConfidenceLayoutTemplates.DefaultTemplate("Built-in Layout");
        layout.Id = "built-in";
        return layout;
    }

    /// <summary>The layout used when a screen has none chosen. A fresh copy each read; object ids are stable.</summary>
    public static ConfidenceLayout BuiltInLayout => MxuJson.Clone(BuiltIn);

    /// <summary>The first live timer (running, or counting to a time), else the first timer.</summary>
    public static TimerSnapshot? PrimaryTimer(IReadOnlyList<TimerSnapshot> timers) =>
        timers.FirstOrDefault(t => t.IsLive) ?? timers.FirstOrDefault();

    public static string LyricText(Slide slide) => string.Join("\n", LyricObjects(slide).Select(o => o.Text));

    public static List<SlideObject> LyricObjects(Slide slide) =>
        slide.Objects.Where(o => o.ObjectKind == SlideObjectKind.Text && (o.Text.Length > 0 || o.Chords is { Count: > 0 })).ToList();

    public static bool HasLyricContent(Slide slide) => LyricObjects(slide).Count > 0;

    /// <summary>Every lyric object's chords, with lines offset so they index into <see cref="LyricText"/>.</summary>
    public static List<ChordPlacement> LyricChords(Slide slide)
    {
        var chords = new List<ChordPlacement>();
        var lineOffset = 0;
        foreach (var o in LyricObjects(slide))
        {
            foreach (var chord in o.Chords ?? [])
                chords.Add(new ChordPlacement { Line = chord.Line + lineOffset, Column = chord.Column, Symbol = chord.Symbol });
            lineOffset += o.Text.Split('\n').Length;
        }
        return chords;
    }

    public static SlideText SlideText(Slide slide, Presentation? presentation)
    {
        var objectTexts = slide.Objects
            .Where(o => o.Name.Length > 0 && o.Text.Length > 0)
            .Select(o => new ObjectText(o.Name, o.Text))
            .ToList();
        return new SlideText(LyricText(slide))
        {
            Chords = LyricChords(slide),
            MusicKey = presentation?.MusicKey,
            DisplayKey = presentation?.DisplayKey,
            ObjectTexts = objectTexts.Count == 0 ? null : objectTexts,
        };
    }

    /// <summary>The slide NEXT shows: the literal next, or with skipping on the first upcoming slide carrying text.</summary>
    public static Slide? NextSlide(IEnumerable<Slide> upcoming, bool skippingBlanks) =>
        skippingBlanks ? upcoming.FirstOrDefault(HasLyricContent) : upcoming.FirstOrDefault();

    /// <summary>The booth clock: 12-hour h:mm, no AM/PM, in the local time zone.</summary>
    public static string ClockString(DateTimeOffset at, TimeZoneInfo? zone = null)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone ?? TimeZoneInfo.Local);
        var twelve = local.Hour % 12 == 0 ? 12 : local.Hour % 12;
        return $"{twelve}:{local.Minute:00}";
    }
}

public static class ConfidenceLayoutExtensions
{
    /// <summary>A layout with its own stage-message box owns the alert; no banner is added over it.</summary>
    public static bool HasStageMessageObject(this ConfidenceLayout layout) =>
        layout.Objects.Any(o => o.TextLink?.Source == TextSourceKind.StageMessage);
}

/// <summary>The data half of AlertSceneBuilder; bannerItems/audienceItems live with the render model.</summary>
public static partial class AlertSceneBuilder
{
    /// <summary>The theme slide named "Alerts" (any case) that dresses an audience alert.</summary>
    public static Slide? AlertsThemeSlide(Theme? theme) =>
        theme?.Slides?.FirstOrDefault(s => string.Equals(s.Name, "Alerts", StringComparison.OrdinalIgnoreCase));
}
