using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Stage;

/// <summary>The kinds of box the stage layout editor adds: what each one shows.</summary>
public enum StageBoxKind
{
    CurrentSlide,
    NextSlide,

    /// <summary>The live slide's words with their chords above them, for the band.</summary>
    Chords,

    /// <summary>The live slide's notes (Windows: see <see cref="StageLayoutEditing.NotesObjectName"/>).</summary>
    Notes,

    Clock,
    Timer,

    /// <summary>One of the service-tracking timers: Item Remaining, Service Elapsed…</summary>
    ServiceTimer,

    VideoCountdown,
    StageMessage,
    SlidePosition,
    GroupName,
    CurrentItem,
    NextItem,
    Timecode,

    /// <summary>A line of words you type (a heading, "PASTOR").</summary>
    Label,
}

/// <summary>One entry in the editor's Add menu.</summary>
public sealed record StageBoxChoice(StageBoxKind Kind, string Title, string Description);

/// <summary>
/// The stage (confidence) layout editor's rules: the layout as a one-slide composition the canvas
/// edits, its canvas size, the boxes it can add (current and next slide, clock, timers, the
/// stage message, notes, chords, video, the service-tracking timers…) as styled cards, safe
/// margins, and making, copying and renaming layouts.
/// <para>
/// The mirror and adopt are a port of the Mac's PresenterCore/Library/ConfidenceLayoutEditing.swift
/// (<c>editorMirror</c>, <c>adopt(editorMirror:)</c>, <c>isTallCanvas</c>); the boxes and margins are
/// Windows' (the Mac adds a text box and picks its link in the inspector). The Mac's multi-view tile
/// layouts (MultiViewTiles) aren't edited here: a layout with tiles opens read-only.
/// </para>
/// </summary>
public static class StageLayoutEditing
{
    /// <summary>
    /// A box showing the live slide's notes reads "the current slide's object called Notes". On Windows
    /// a slide's own notes answer to that name when the slide has no object called Notes
    /// (<see cref="ConfidenceSceneBuilder.SlideText"/>); a Mac shows such an object if the slide has one,
    /// else nothing. No new kind is written into the shared layout.
    /// </summary>
    public const string NotesObjectName = "Notes";

    public static readonly IReadOnlyList<StageBoxChoice> Boxes =
    [
        new(StageBoxKind.CurrentSlide, "Current slide", "The words on screen now"),
        new(StageBoxKind.NextSlide, "Next slide", "The words coming next (into the next service item at the end)"),
        new(StageBoxKind.Chords, "Chords", "The live slide's words with their chords, for the band"),
        new(StageBoxKind.Notes, "Slide notes", "The live slide's notes"),
        new(StageBoxKind.Clock, "Clock", "The time of day"),
        new(StageBoxKind.Timer, "Timer", "A countdown or count-up from the Timers panel"),
        new(StageBoxKind.ServiceTimer, "Service timer", "Item remaining, service elapsed…"),
        new(StageBoxKind.VideoCountdown, "Video countdown", "Time left on the playing video"),
        new(StageBoxKind.StageMessage, "Stage message", "Messages sent to the stage (no banner covers the layout then)"),
        new(StageBoxKind.SlidePosition, "Slide position", "\"3 of 12\""),
        new(StageBoxKind.GroupName, "Group name", "Verse 1, Chorus…"),
        new(StageBoxKind.CurrentItem, "Service item", "The service item now"),
        new(StageBoxKind.NextItem, "Next service item", "What comes after it"),
        new(StageBoxKind.Timecode, "Timecode", "The incoming timecode"),
        new(StageBoxKind.Label, "Label", "Words you type"),
    ];

    // MARK: Editing a layout on the canvas (ConfidenceLayoutEditing.swift)

    /// <summary>
    /// The layout as a one-slide presentation for the canvas: the slide's object list is the layout's
    /// own, so moving a box on the canvas moves the layout's box; the presentation carries its canvas size.
    /// </summary>
    public static (Presentation Mirror, Slide Slide) EditorMirror(ConfidenceLayout layout)
    {
        var slide = new Slide { Id = layout.Id, Name = layout.Name, Objects = layout.Objects, AnimationOrder = layout.AnimationOrder };
        var mirror = new Presentation
        {
            Id = layout.Id, Name = layout.Name, PresentationKind = PresentationKind.Deck, ThemeId = "", Slides = [slide],
            CanvasWidth = layout.CanvasWidth, CanvasHeight = layout.CanvasHeight,
        };
        return (mirror, slide);
    }

    /// <summary>Takes the edited mirror's objects, play order and canvas back into the layout.</summary>
    public static void Adopt(ConfidenceLayout layout, Presentation mirror)
    {
        layout.Objects = mirror.Slides.FirstOrDefault()?.Objects ?? [];
        layout.AnimationOrder = mirror.Slides.FirstOrDefault()?.AnimationOrder;
        layout.CanvasWidth = mirror.CanvasWidth;
        layout.CanvasHeight = mirror.CanvasHeight;
    }

    public static bool IsTallCanvas(ConfidenceLayout layout) => (layout.CanvasHeight ?? 1080) > (layout.CanvasWidth ?? 1920);

    public static SceneSize CanvasOf(ConfidenceLayout layout) => new(layout.CanvasWidth ?? 1920, layout.CanvasHeight ?? 1080);

    /// <summary>
    /// A new canvas size: the boxes keep their places relative to the canvas (scaled to the new
    /// width and height), and 1920 × 1080 is stored as absent, as on the Mac. False when nothing changed.
    /// </summary>
    public static bool SetCanvas(ConfidenceLayout layout, int width, int height)
    {
        if (width <= 0 || height <= 0) return false;
        var before = CanvasOf(layout);
        if ((int)before.Width == width && (int)before.Height == height) return false;
        var sx = width / before.Width;
        var sy = height / before.Height;
        foreach (var o in layout.Objects)
        {
            if (o.X is { } x) o.X = Math.Round(x * sx, 1);
            if (o.Y is { } y) o.Y = Math.Round(y * sy, 1);
            if (o.Width is { } w) o.Width = Math.Round(w * sx, 1);
            if (o.Height is { } h) o.Height = Math.Round(h * sy, 1);
        }
        var stored = width == 1920 && height == 1080;
        layout.CanvasWidth = stored ? null : width;
        layout.CanvasHeight = stored ? null : height;
        return true;
    }

    /// <summary>True for a layout made of multi-view tiles (a Mac feature this editor shows but doesn't change).</summary>
    public static bool IsMultiView(ConfidenceLayout layout) => layout.MultiView is not null;

    // MARK: Making, copying, renaming (the Mac's createConfidenceLayout and duplicate)

    /// <summary>A new layout from one of the starters (Blank, Classic, Current + Next…), named as the Mac names them.</summary>
    public static ConfidenceLayout Create(ConfidenceLayoutTemplate template) => template.Make(template.StarterName());

    /// <summary>A copy beside the original: a new id and " Copy" on the name, everything else the same (as the Mac copies).</summary>
    public static ConfidenceLayout Duplicate(ConfidenceLayout layout)
    {
        var copy = MxuJson.Clone(layout);
        copy.Id = StageIds.New();
        copy.Name = layout.Name + " Copy";
        return copy;
    }

    public static bool Rename(ConfidenceLayout layout, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0 || trimmed == layout.Name) return false;
        layout.Name = trimmed;
        return true;
    }

    // MARK: Boxes

    /// <summary>
    /// The objects a new box of <paramref name="kind"/> is made of: an outlined card, the linked text
    /// inside it and a caption on its edge (as the starter layouts draw them), or just the text for a
    /// label. It goes in the first free spot down the canvas. <paramref name="timerId"/> names the
    /// timer for a Timer or Service timer box.
    /// </summary>
    public static List<SlideObject> MakeBox(StageBoxKind kind, SceneSize canvas, IReadOnlyList<SlideObject> existing, string? timerId = null)
    {
        var (fullWidth, fullHeight) = DefaultSize(kind, canvas);
        // A card's caption hangs half over its bottom edge: the footprint is a little taller than the card.
        var hang = kind == StageBoxKind.Label ? 0 : CaptionHang;
        var (x, y, width, height) = FreeFrame(fullWidth, fullHeight, hang, canvas, existing);
        if (kind == StageBoxKind.Label)
        {
            return
            [
                new SlideObject
                {
                    Id = StageIds.New(), ObjectKind = SlideObjectKind.Text, Name = "Label", Text = "LABEL",
                    X = x, Y = y, Width = width, Height = height,
                    TextStyle = new TextStyle
                    {
                        FontSize = 48, ColorHex = "#FFFFFFFF", HorizontalAlignment = TextHorizontalAlignment.Center,
                        VerticalAlignment = TextVerticalAlignment.Middle, AutoShrink = true,
                    },
                },
            ];
        }
        var (title, source, color, size, tabular) = Look(kind);
        var inset = Math.Min(40, Math.Min(width, height) * 0.1);
        var text = ConfidenceLayoutTemplates.Linked(title, source, x + inset, y + inset, width - 2 * inset, height - 2 * inset,
            size: size, color: color, alignment: kind is StageBoxKind.Chords or StageBoxKind.Notes ? TextHorizontalAlignment.Left : TextHorizontalAlignment.Center,
            tabular: tabular, shrink: true, minSize: 20, clockFormat: kind == StageBoxKind.Clock ? "h:mm a" : null, chords: kind == StageBoxKind.Chords);
        if (text.TextLink is { } link)
        {
            if (kind is StageBoxKind.Timer or StageBoxKind.ServiceTimer) link.TimerId = timerId;
            if (kind == StageBoxKind.Notes) link.SourceObjectName = NotesObjectName;
        }
        var box = ConfidenceLayoutTemplates.Box($"{title} Box", x, y, width, height, CardColor(kind));
        var caption = ConfidenceLayoutTemplates.Caption(CaptionText(kind, title), x, width, y + height, CardColor(kind));
        return [box, text, caption];
    }

    private static string CardColor(StageBoxKind kind) => kind == StageBoxKind.NextSlide ? ConfidenceLayoutTemplates.Amber : "#FFFFFFFF";

    private static string CaptionText(StageBoxKind kind, string title) => kind switch
    {
        StageBoxKind.Timer => "TIME REMAINING",
        _ => title.ToUpperInvariant(),
    };

    /// <summary>The box's name, what it reads, its text colour and size, and whether its digits line up (clocks and timers).</summary>
    private static (string Title, TextSourceKind Source, string? Color, double Size, bool Tabular) Look(StageBoxKind kind) => kind switch
    {
        StageBoxKind.CurrentSlide => ("Current Slide", TextSourceKind.CurrentSlide, null, 96, false),
        StageBoxKind.NextSlide => ("Next Slide", TextSourceKind.NextSlide, ConfidenceLayoutTemplates.Amber, 72, false),
        StageBoxKind.Chords => ("Chords", TextSourceKind.CurrentSlide, null, 80, false),
        StageBoxKind.Notes => ("Notes", TextSourceKind.CurrentSlide, ConfidenceLayoutTemplates.Dimmed, 48, false),
        StageBoxKind.Clock => ("Clock", TextSourceKind.Clock, ConfidenceLayoutTemplates.Dimmed, 120, true),
        StageBoxKind.Timer => ("Timer", TextSourceKind.Timer, ConfidenceLayoutTemplates.Green, 120, true),
        StageBoxKind.ServiceTimer => ("Service Timer", TextSourceKind.Timer, ConfidenceLayoutTemplates.Green, 120, true),
        StageBoxKind.VideoCountdown => ("Video Countdown", TextSourceKind.VideoCountdown, ConfidenceLayoutTemplates.Green, 100, true),
        StageBoxKind.StageMessage => ("Stage Message", TextSourceKind.StageMessage, "#FF6B6BFF", 72, false),
        StageBoxKind.SlidePosition => ("Slide Position", TextSourceKind.SlidePosition, ConfidenceLayoutTemplates.Dimmed, 64, true),
        StageBoxKind.GroupName => ("Group", TextSourceKind.CurrentGroup, null, 64, false),
        StageBoxKind.CurrentItem => ("Service Item", TextSourceKind.CurrentServiceItem, null, 56, false),
        StageBoxKind.NextItem => ("Next Item", TextSourceKind.NextServiceItem, ConfidenceLayoutTemplates.Amber, 56, false),
        StageBoxKind.Timecode => ("Timecode", TextSourceKind.Timecode, ConfidenceLayoutTemplates.Dimmed, 80, true),
        _ => ("Label", TextSourceKind.CurrentSlide, null, 48, false),
    };

    /// <summary>A box's size on a 1920 × 1080 canvas, scaled to this one: big for words, smaller for clocks and names.</summary>
    private static (double Width, double Height) DefaultSize(StageBoxKind kind, SceneSize canvas)
    {
        var (w, h) = kind switch
        {
            // Sized so the usual set fits a 1080p screen side by side: current, next and notes down
            // the left, clocks and timers down the right.
            StageBoxKind.CurrentSlide or StageBoxKind.Chords => (1100.0, 440.0),
            StageBoxKind.NextSlide => (1100.0, 240.0),
            StageBoxKind.Notes => (1100.0, 220.0),
            StageBoxKind.StageMessage => (1100.0, 160.0),
            StageBoxKind.Label => (520.0, 90.0),
            StageBoxKind.SlidePosition or StageBoxKind.GroupName or StageBoxKind.CurrentItem or StageBoxKind.NextItem => (520.0, 130.0),
            _ => (520.0, 200.0),
        };
        var scale = Math.Min(canvas.Width / 1920, canvas.Height / 1080);
        if (scale <= 0) scale = 1;
        return (Math.Round(Math.Min(w * scale, canvas.Width - 80), 1), Math.Round(Math.Min(h * scale, canvas.Height - 80), 1));
    }

    /// <summary>How far a card's caption reaches below the card (it sits across the bottom edge).</summary>
    private const double CaptionHang = 18;

    /// <summary>
    /// Where a new box goes: the first spot (in rows from the top-left, inside the safe margin) that
    /// overlaps no box already there; if the canvas is full, cascaded down from the top-left.
    /// </summary>
    public static (double X, double Y) FreeSpot(double width, double height, SceneSize canvas, IReadOnlyList<SlideObject> existing) =>
        Spot(width, height, canvas, existing) ?? Cascade(width, height, canvas, existing);

    /// <summary>
    /// A free spot for a box of this size (its footprint <paramref name="hang"/> taller, for its
    /// caption), or for a smaller one when the screen is getting full; cascaded at full size when
    /// even the smallest doesn't fit.
    /// </summary>
    private static (double X, double Y, double Width, double Height) FreeFrame(double width, double height, double hang, SceneSize canvas, IReadOnlyList<SlideObject> existing)
    {
        foreach (var scale in (double[])[1, 0.8, 0.6, 0.45])
        {
            var (w, h) = (Math.Round(width * scale, 1), Math.Round(height * scale, 1));
            if (Spot(w, h + hang, canvas, existing) is { } spot) return (spot.X, spot.Y, w, h);
        }
        var (x, y) = Cascade(width, height, canvas, existing);
        return (x, y, width, height);
    }

    private static (double X, double Y)? Spot(double width, double height, SceneSize canvas, IReadOnlyList<SlideObject> existing)
    {
        var safe = SafeArea(canvas, 0.05);
        var taken = existing.Where(o => o.X is not null && o.Y is not null && o.Width is not null && o.Height is not null)
            .Select(o => new SceneRect(o.X!.Value, o.Y!.Value, o.Width!.Value, o.Height!.Value)).ToList();
        const double step = 20;
        for (var y = safe.Y; y + height <= safe.Bottom + 0.5; y += step)
            for (var x = safe.X; x + width <= safe.Right + 0.5; x += step)
            {
                var candidate = new SceneRect(x, y, width, height);
                if (!taken.Any(t => Overlaps(t, candidate))) return (Math.Round(x), Math.Round(y));
            }
        return null;
    }

    private static (double X, double Y) Cascade(double width, double height, SceneSize canvas, IReadOnlyList<SlideObject> existing)
    {
        var safe = SafeArea(canvas, 0.05);
        var cascade = Math.Min(existing.Count, 10) * 30;
        return (Math.Round(Math.Min(safe.X + cascade, Math.Max(0, canvas.Width - width))), Math.Round(Math.Min(safe.Y + cascade, Math.Max(0, canvas.Height - height))));
    }

    private static bool Overlaps(SceneRect a, SceneRect b) => a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;

    // MARK: Safe margins

    /// <summary>The margins the editor can draw and snap to: none, 5 % (action safe) or 10 % (title safe) in from each edge.</summary>
    public static readonly IReadOnlyList<(string Label, double Fraction)> SafeMargins = [("None", 0), ("5 % (action safe)", 0.05), ("10 % (title safe)", 0.10)];

    /// <summary>The canvas inset by a fraction of its size on every side.</summary>
    public static SceneRect SafeArea(SceneSize canvas, double fraction)
    {
        var dx = canvas.Width * Math.Clamp(fraction, 0, 0.4);
        var dy = canvas.Height * Math.Clamp(fraction, 0, 0.4);
        return new SceneRect(dx, dy, canvas.Width - 2 * dx, canvas.Height - 2 * dy);
    }

    /// <summary>The boxes that poke outside a safe area (the editor flags them: an old monitor may cut them off).</summary>
    public static List<SlideObject> OutsideSafeArea(IEnumerable<SlideObject> objects, SceneRect safe) =>
        [.. objects.Where(o => o.Hidden != true && o.X is { } x && o.Y is { } y && o.Width is { } w && o.Height is { } h
            && (x < safe.X - 0.5 || y < safe.Y - 0.5 || x + w > safe.Right + 0.5 || y + h > safe.Bottom + 0.5))];

    // MARK: Words for the list

    /// <summary>What a box shows, in a few words ("Clock", "Timer: Sermon", "Slide notes", "Static text").</summary>
    public static string Describe(SlideObject o, Func<string?, string?>? timerName = null)
    {
        if (o.ObjectKind != SlideObjectKind.Text) return o.ObjectKind == SlideObjectKind.Shape ? "Shape" : o.ObjectKind.ToString();
        if (o.TextLink is not { } link) return "Static text";
        if (link.SourceObjectName is { Length: > 0 } named)
            return string.Equals(named, NotesObjectName, StringComparison.OrdinalIgnoreCase) ? "Slide notes" : $"{SourceName(link.Source)}: \"{named}\"";
        return link.Source switch
        {
            TextSourceKind.Timer when link.TimerId is { Length: > 0 } id => $"Timer: {timerName?.Invoke(id) ?? id}",
            TextSourceKind.Timer => "Timer: first running",
            TextSourceKind.CurrentSlide when o.TextStyle?.ShowChords == true => "Current slide with chords",
            TextSourceKind.NextSlide when o.TextStyle?.ShowChords == true => "Next slide with chords",
            _ => SourceName(link.Source),
        };
    }

    /// <summary>A link source's name, as the Mac's inspector lists them.</summary>
    public static string SourceName(TextSourceKind source) => source switch
    {
        TextSourceKind.CurrentSlide => "Current Slide",
        TextSourceKind.NextSlide => "Next Slide",
        TextSourceKind.LastSlide => "Last Slide",
        TextSourceKind.Clock => "Clock",
        TextSourceKind.Timer => "Timer",
        TextSourceKind.VideoCountdown => "Video Countdown",
        TextSourceKind.SlidePosition => "Slide Position",
        TextSourceKind.CurrentServiceItem => "Current Service Item",
        TextSourceKind.NextServiceItem => "Next Service Item",
        TextSourceKind.StageMessage => "Stage Message",
        TextSourceKind.CurrentGroup => "Group Name",
        TextSourceKind.CurrentPresentation => "Presentation Name",
        TextSourceKind.NextServiceTitle => "Next Service Title",
        TextSourceKind.VideoMarkName => "Video Action Name",
        _ => "Timecode",
    };

    /// <summary>The clock formats the inspector offers (the Mac's clockFormatPresets): what each reads, and the pattern ("" = the booth default).</summary>
    public static readonly IReadOnlyList<(string Label, string Format)> ClockFormats =
    [
        ("10:25", ""), ("10:25:08", "h:mm:ss"), ("22:25", "HH:mm"), ("10:25 PM", "h:mm a"), ("10:25:08 PM", "h:mm:ss a"),
    ];

    /// <summary>The timer formats the inspector offers: what each reads.</summary>
    public static readonly IReadOnlyList<(string Label, TimerTextFormat Format)> TimerFormats =
    [
        ("83:18:23", TimerTextFormat.Digits), ("3d 11h 18m 23s", TimerTextFormat.Abbreviated), ("3 days 11 hours 18 minutes 23 seconds", TimerTextFormat.Words),
    ];

    /// <summary>The video layers a video countdown can follow (null = the foreground video).</summary>
    public static readonly IReadOnlyList<(string Label, string? Layer)> VideoLayers =
        [("Foreground video", null), ("Background loop", "loopingVideos"), ("Slide's video", "slide"), ("Overlay video", "overlays")];
}
