using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Stage;

/// <summary>Starter stage display layouts. Port of PresenterCore/Library/ConfidenceLayoutTemplate.swift.</summary>
public enum ConfidenceLayoutTemplate
{
    Blank,
    Classic,
    CurrentAndNext,
    Timers,
    CurrentOverNext,
    CurrentOverNextChords,
}

public static class ConfidenceLayoutTemplates
{
    public static readonly IReadOnlyList<ConfidenceLayoutTemplate> All = Enum.GetValues<ConfidenceLayoutTemplate>();

    /// <summary>The starters a new workspace is seeded with.</summary>
    public static readonly IReadOnlyList<ConfidenceLayoutTemplate> WorkspaceStarters =
    [
        ConfidenceLayoutTemplate.CurrentAndNext, ConfidenceLayoutTemplate.Timers,
        ConfidenceLayoutTemplate.CurrentOverNext, ConfidenceLayoutTemplate.CurrentOverNextChords,
    ];

    public static string Raw(this ConfidenceLayoutTemplate template) => template switch
    {
        ConfidenceLayoutTemplate.Blank => "blank",
        ConfidenceLayoutTemplate.Classic => "classic",
        ConfidenceLayoutTemplate.CurrentAndNext => "currentAndNext",
        ConfidenceLayoutTemplate.Timers => "timers",
        ConfidenceLayoutTemplate.CurrentOverNext => "currentOverNext",
        ConfidenceLayoutTemplate.CurrentOverNextChords => "currentOverNextChords",
        _ => template.ToString(),
    };

    public static ConfidenceLayoutTemplate? Parse(string? raw) =>
        All.Cast<ConfidenceLayoutTemplate?>().FirstOrDefault(t => t!.Value.Raw() == raw);

    public static string Title(this ConfidenceLayoutTemplate template) => template switch
    {
        ConfidenceLayoutTemplate.Blank => "Blank",
        ConfidenceLayoutTemplate.Classic => "Classic",
        ConfidenceLayoutTemplate.CurrentAndNext => "Current + Next",
        ConfidenceLayoutTemplate.Timers => "Timers",
        ConfidenceLayoutTemplate.CurrentOverNext => "Current Over Next",
        ConfidenceLayoutTemplate.CurrentOverNextChords => "Current Over Next + Chords",
        _ => template.ToString(),
    };

    public static string StarterName(this ConfidenceLayoutTemplate template) =>
        template == ConfidenceLayoutTemplate.Blank ? "Untitled Layout" : template.Title();

    /// <summary>A new layout (fresh ids throughout) laid out on the 1920×1080 canvas.</summary>
    public static ConfidenceLayout Make(this ConfidenceLayoutTemplate template, string name) => template switch
    {
        ConfidenceLayoutTemplate.Classic => Classic(name),
        ConfidenceLayoutTemplate.CurrentAndNext => CurrentAndNext(name),
        ConfidenceLayoutTemplate.Timers => Timers(name),
        ConfidenceLayoutTemplate.CurrentOverNext => CurrentOverNext(name),
        ConfidenceLayoutTemplate.CurrentOverNextChords => CurrentOverNextChords(name),
        _ => Layout(name, []),
    };

    /// <summary>The layout a new stage display starts from (Swift's ConfidenceLayout.defaultTemplate).</summary>
    public static ConfidenceLayout DefaultTemplate(string name) => ConfidenceLayoutTemplate.Classic.Make(name);

    internal const string Amber = "#FFB826FF";
    internal const string Dimmed = "#B8B8B8FF";
    internal const string Green = "#7ED957FF";
    private const string White = "#FFFFFFFF";

    private static ConfidenceLayout Layout(string name, List<SlideObject> objects) =>
        new() { Id = StageIds.New(), Name = name, Objects = objects };

    private static ConfidenceLayout Classic(string name) => Layout(name,
    [
        Box("Timer Box", x: 60, y: 40, width: 480, height: 140),
        Linked("Timer", TextSourceKind.Timer, x: 80, y: 56, width: 440, height: 108,
               size: 84, color: Green, tabular: true, shrink: true, minSize: 80),
        Caption("TIME REMAINING", x: 60, width: 480, boxBottom: 180),
        Box("Video Box", x: 720, y: 40, width: 480, height: 140),
        Linked("Video Remaining", TextSourceKind.VideoCountdown, x: 740, y: 56, width: 440, height: 108,
               size: 84, color: Green, tabular: true, shrink: true),
        Caption("VIDEO COUNTDOWN", x: 720, width: 480, boxBottom: 180),
        Box("Clock Box", x: 1380, y: 40, width: 480, height: 140),
        Linked("Clock", TextSourceKind.Clock, x: 1400, y: 56, width: 440, height: 108,
               size: 84, color: Dimmed, tabular: true, shrink: true, minSize: 80, clockFormat: "h:mm a"),
        Caption("CLOCK", x: 1380, width: 480, boxBottom: 180),
        Box("Current Box", x: 60, y: 230, width: 1800, height: 540),
        Linked("Current Slide", TextSourceKind.CurrentSlide, x: 100, y: 266, width: 1720, height: 468,
               size: 110, shrink: true),
        Caption("CURRENT SLIDE", x: 60, width: 1800, boxBottom: 770),
        Box("Next Box", x: 60, y: 826, width: 1800, height: 194, color: Amber),
        Linked("Next Slide", TextSourceKind.NextSlide, x: 100, y: 848, width: 1720, height: 150,
               size: 64, color: Amber, shrink: true),
        Caption("NEXT SLIDE", x: 60, width: 1800, boxBottom: 1020, color: Amber),
    ]);

    private static ConfidenceLayout CurrentAndNext(string name) => Layout(name,
    [
        Box("Current Box", x: 60, y: 60, width: 880, height: 680),
        Linked("Current Slide", TextSourceKind.CurrentSlide, x: 100, y: 100, width: 800, height: 600,
               size: 88, alignment: TextHorizontalAlignment.Left, shrink: true),
        Caption("CURRENT SLIDE", x: 60, width: 880, boxBottom: 740),
        Box("Next Box", x: 980, y: 60, width: 880, height: 680, color: Amber),
        Linked("Next Slide", TextSourceKind.NextSlide, x: 1020, y: 100, width: 800, height: 600,
               size: 88, color: Amber, alignment: TextHorizontalAlignment.Left, shrink: true),
        Caption("NEXT SLIDE", x: 980, width: 880, boxBottom: 740, color: Amber),
        Box("Timer Box", x: 60, y: 800, width: 880, height: 220),
        Linked("Timer", TextSourceKind.Timer, x: 100, y: 820, width: 800, height: 160,
               size: 120, color: Green, tabular: true, shrink: true),
        Caption("TIME REMAINING", x: 60, width: 880, boxBottom: 1020),
        Box("Clock Box", x: 980, y: 800, width: 880, height: 220),
        Linked("Clock", TextSourceKind.Clock, x: 1020, y: 820, width: 800, height: 160,
               size: 120, color: Dimmed, tabular: true, shrink: true, clockFormat: "h:mm a"),
        Caption("CLOCK", x: 980, width: 880, boxBottom: 1020),
    ]);

    private static ConfidenceLayout Timers(string name) => Layout(name,
    [
        Box("Timer Box", x: 460, y: 70, width: 1000, height: 280),
        Linked("Timer", TextSourceKind.Timer, x: 500, y: 110, width: 920, height: 200,
               size: 160, color: Green, tabular: true, shrink: true),
        Caption("TIME REMAINING", x: 460, width: 1000, boxBottom: 350),
        Box("Video Box", x: 460, y: 400, width: 1000, height: 280),
        Linked("Video Countdown", TextSourceKind.VideoCountdown, x: 500, y: 440, width: 920, height: 200,
               size: 160, color: Green, tabular: true, shrink: true),
        Caption("VIDEO COUNTDOWN", x: 460, width: 1000, boxBottom: 680),
        Box("Clock Box", x: 460, y: 730, width: 1000, height: 280),
        Linked("Clock", TextSourceKind.Clock, x: 500, y: 770, width: 920, height: 200,
               size: 160, color: Green, tabular: true, shrink: true, clockFormat: "h:mm a"),
        Caption("CLOCK", x: 460, width: 1000, boxBottom: 1010),
    ]);

    private static ConfidenceLayout CurrentOverNext(string name) => Layout(name,
    [
        Linked("Clock", TextSourceKind.Clock, x: 660, y: 28, width: 600, height: 64,
               size: 48, color: Dimmed, tabular: true, clockFormat: "h:mm a"),
        Box("Current Box", x: 60, y: 116, width: 1800, height: 550),
        Linked("Current Slide", TextSourceKind.CurrentSlide, x: 100, y: 156, width: 1720, height: 470,
               size: 110, shrink: true),
        Caption("CURRENT SLIDE", x: 60, width: 1800, boxBottom: 666),
        Box("Next Box", x: 60, y: 726, width: 1800, height: 294, color: Amber),
        Linked("Next Slide", TextSourceKind.NextSlide, x: 100, y: 756, width: 1720, height: 234,
               size: 76, color: Amber, shrink: true),
        Caption("NEXT SLIDE", x: 60, width: 1800, boxBottom: 1020, color: Amber),
    ]);

    private static ConfidenceLayout CurrentOverNextChords(string name) => Layout(name,
    [
        Linked("Clock", TextSourceKind.Clock, x: 660, y: 28, width: 600, height: 64,
               size: 48, color: Dimmed, tabular: true, clockFormat: "h:mm a"),
        Box("Current Box", x: 60, y: 116, width: 1800, height: 590),
        Linked("Current Slide", TextSourceKind.CurrentSlide, x: 100, y: 156, width: 1720, height: 510,
               size: 96, alignment: TextHorizontalAlignment.Left, shrink: true, chords: true),
        Caption("CURRENT SLIDE", x: 60, width: 1800, boxBottom: 706),
        Box("Next Box", x: 60, y: 766, width: 1800, height: 254, color: Amber),
        Linked("Next Slide", TextSourceKind.NextSlide, x: 100, y: 796, width: 1720, height: 194,
               size: 72, alignment: TextHorizontalAlignment.Left, chords: true, maxLines: 1),
        Caption("NEXT SLIDE", x: 60, width: 1800, boxBottom: 1020, color: Amber),
    ]);

    internal static SlideObject Linked(
        string objectName, TextSourceKind source,
        double x, double y, double width, double height,
        double size, string? color = null,
        TextHorizontalAlignment alignment = TextHorizontalAlignment.Center,
        bool tabular = false, bool shrink = false, double minSize = 20,
        string? clockFormat = null,
        bool chords = false, int? maxLines = null) => new()
    {
        Id = StageIds.New(),
        ObjectKind = SlideObjectKind.Text,
        Name = objectName,
        Text = "",
        X = x,
        Y = y,
        Width = width,
        Height = height,
        TextLink = new TextLink { Source = source, ClockFormat = clockFormat, MaxLines = maxLines },
        TextStyle = new TextStyle
        {
            FontSize = size,
            ColorHex = color,
            HorizontalAlignment = alignment,
            VerticalAlignment = TextVerticalAlignment.Middle,
            TabularFigures = tabular ? true : null,
            AutoShrink = shrink ? true : null,
            MinFontSize = shrink ? minSize : null,
            ShowChords = chords ? true : null,
        },
    };

    private static SlideObject Box(string objectName, double x, double y, double width, double height, string color = White) => new()
    {
        Id = StageIds.New(),
        ObjectKind = SlideObjectKind.Shape,
        Name = objectName,
        Text = "",
        ShapeKind = ShapeKind.RoundedRectangle,
        CornerRadius = 24,
        X = x,
        Y = y,
        Width = width,
        Height = height,
        Fill = new ObjectFill { FillKind = FillKind.None },
        Stroke = new ObjectStroke { ColorHex = color, Width = 3 },
    };

    private static SlideObject Caption(string text, double x, double width, double boxBottom, string color = White) => new()
    {
        Id = StageIds.New(),
        ObjectKind = SlideObjectKind.Text,
        Name = "Caption",
        Text = text,
        X = x,
        Y = boxBottom - 18,
        Width = width,
        Height = 36,
        TextStyle = new TextStyle
        {
            FontName = "HelveticaNeue-Bold",
            FontSize = 26,
            ColorHex = color,
            Tracking = 3,
            HorizontalAlignment = TextHorizontalAlignment.Center,
            VerticalAlignment = TextVerticalAlignment.Middle,
            LineFill = new TextLineFill
            {
                Fill = new ObjectFill { FillKind = FillKind.Solid, ColorHex = "#000000FF" },
                WidthMode = TextLineFillWidthMode.LineWidth,
                HorizontalPadding = 22,
            },
        },
    };
}
