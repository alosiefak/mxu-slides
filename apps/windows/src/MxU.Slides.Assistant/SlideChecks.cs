using System.Globalization;
using System.Text.Json;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Assistant;

/// <summary>
/// Slide checks: is each slide easy to read from the back of the room? Everything here is
/// arithmetic on what the app already knows about a slide (its colours, its text, its boxes), so
/// it runs with no AI and no internet. The app measures; this decides.
/// <list type="bullet">
/// <item><b>Contrast:</b> the text's colour against the picture behind it, by the web's standard
/// measure (WCAG contrast ratio: 21 is black on white, 1 is the same colour).</item>
/// <item><b>A busy background:</b> the picture behind the words varies a lot in brightness, and
/// the words have no shadow, outline or band to lift them off it.</item>
/// <item><b>A lot to read:</b> more words or lines than a congregation takes in at a glance.</item>
/// <item><b>Cut off:</b> the words don't fit their box even after shrinking, so the end is lost.</item>
/// <item><b>Off the slide:</b> a text box hangs over the slide's edge (often a lower third).</item>
/// <item><b>Small text.</b></item>
/// </list>
/// A finding offers a <see cref="Fix"/> only where one is safe: moving a box back onto the slide,
/// or turning on shrink-to-fit for a box that already has its own text style. Colour and wording
/// are the designer's and the pastor's, so those are reported and left alone.
/// <para>
/// The assistant's optional second look (<see cref="VisionRequest"/>) sends pictures of the slides
/// and returns notes; they are shown as its opinion, and never change a slide.
/// </para>
/// </summary>
public static class SlideChecks
{
    public enum Kind { LowContrast, BusyBackground, LotsToRead, CutOff, OffTheSlide, SmallText }

    public enum Fix { MoveOntoSlide, ShrinkToFit }

    /// <summary>What the app measured about one text box of a slide, in the slide's own units (1920 × 1080 for HD).</summary>
    /// <param name="Behind">The average colour of the picture behind the box (the slide drawn without its words); null when it couldn't be drawn.</param>
    /// <param name="BehindSpread">How much the brightness behind the box varies, 0 (flat) to about 0.5 (black and white).</param>
    /// <param name="Lifted">The words have a shadow, an outline or a band behind each line.</param>
    /// <param name="Overflows">The words don't fit the box after shrinking as far as the style allows.</param>
    /// <param name="OwnFrame">The box's place is the slide's own (not the theme's), so it can be moved.</param>
    /// <param name="OwnStyle">The box has its own text style, so shrink-to-fit can be set on it.</param>
    public sealed record TextBox(
        string ObjectId, string Text, SceneRect Frame, SceneColor Color, double FontSize, SceneColor? Behind, double BehindSpread,
        bool Lifted, bool Overflows, bool AutoShrink, bool OwnFrame, bool OwnStyle);

    public sealed record Finding(Kind Kind, string SlideId, int SlideNumber, string ObjectId, string Message, Fix? Fix = null);

    // The thresholds, in one place. Contrast 3 is the web's minimum for large text; a shadow or
    // outline makes up for some of it, so lifted text is only flagged when it is very close.
    public const double ContrastNeeded = 3.0, ContrastNeededLifted = 1.6, BusySpread = 0.22;
    public const int MostWords = 45, MostLines = 8;
    public const double SmallestFont = 0.033; // of the slide's height: 36 on a 1080-high slide

    /// <summary>The checks for one slide.</summary>
    public static List<Finding> Check(string slideId, int number, SceneSize canvas, IReadOnlyList<TextBox> boxes)
    {
        var findings = new List<Finding>();
        foreach (var box in boxes.Where(b => b.Text.Trim().Length > 0))
        {
            var lowerThird = box.Frame.Y + box.Frame.Height / 2 > canvas.Height * 0.66;
            var what = lowerThird ? "The lower-third text" : "The text";
            void Add(Kind kind, string message, Fix? fix = null) => findings.Add(new Finding(kind, slideId, number, box.ObjectId, message, fix));

            if (box.Behind is { } behind)
            {
                var ratio = ContrastRatio(box.Color, behind);
                if (ratio < (box.Lifted ? ContrastNeededLifted : ContrastNeeded))
                    Add(Kind.LowContrast, string.Create(CultureInfo.InvariantCulture, $"{what} is hard to read against what's behind it (contrast {ratio:0.0} to 1; {ContrastNeeded:0} or more reads well)."));
                else if (box.BehindSpread > BusySpread && !box.Lifted)
                    Add(Kind.BusyBackground, $"{what} sits on a busy background with no shadow or outline to lift it off.");
            }

            var words = box.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            var lines = box.Text.Split('\n').Count(l => l.Trim().Length > 0);
            if (words > MostWords || lines > MostLines)
                Add(Kind.LotsToRead, $"A lot to read at once: {words} words on {lines} line{(lines == 1 ? "" : "s")}. Two slides would be easier.");

            if (box.Overflows)
                Add(Kind.CutOff, $"{what} doesn't fit its box, so the end is cut off.", box is { AutoShrink: false, OwnStyle: true } ? Fix.ShrinkToFit : null);

            var hangs = box.Frame.X < -1 || box.Frame.Y < -1 || box.Frame.Right > canvas.Width + 1 || box.Frame.Bottom > canvas.Height + 1;
            if (hangs)
                Add(Kind.OffTheSlide, $"{what} box runs off the edge of the slide.", box.OwnFrame ? Fix.MoveOntoSlide : null);

            if (box.FontSize > 0 && box.FontSize < canvas.Height * SmallestFont && !box.Overflows)
                Add(Kind.SmallText, string.Create(CultureInfo.InvariantCulture, $"{what} is small ({box.FontSize:0} on a slide {canvas.Height:0} high): hard to read from the back."));
        }
        return findings;
    }

    /// <summary>Where a box that hangs off the slide should go: the same size if it fits, moved just inside; made smaller only if it is bigger than the slide.</summary>
    public static SceneRect OntoSlide(SceneRect frame, SceneSize canvas)
    {
        var width = Math.Min(frame.Width, canvas.Width);
        var height = Math.Min(frame.Height, canvas.Height);
        return new SceneRect(Math.Clamp(frame.X, 0, canvas.Width - width), Math.Clamp(frame.Y, 0, canvas.Height - height), width, height);
    }

    /// <summary>
    /// The WCAG contrast ratio between two colours: (lighter + 0.05) / (darker + 0.05), where each
    /// is the colour's relative luminance. 1 (the same) to 21 (black on white).
    /// </summary>
    public static double ContrastRatio(SceneColor a, SceneColor b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>How bright a colour looks, 0 to 1 (green counts most, blue least), as WCAG defines it.</summary>
    public static double Luminance(SceneColor color)
    {
        static double Linear(double c) => c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        return 0.2126 * Linear(color.Red) + 0.7152 * Linear(color.Green) + 0.0722 * Linear(color.Blue);
    }

    /// <summary>
    /// The average colour and the spread of brightness of a block of pixels (BGRA, four bytes
    /// each, as WPF renders them). Every <paramref name="step"/>-th pixel is enough.
    /// </summary>
    public static (SceneColor Average, double Spread) Sample(ReadOnlySpan<byte> bgra, int step = 3)
    {
        double r = 0, g = 0, b = 0, sum = 0, squares = 0;
        var count = 0;
        for (var i = 0; i + 3 < bgra.Length; i += 4 * Math.Max(1, step))
        {
            var color = new SceneColor(bgra[i + 2] / 255.0, bgra[i + 1] / 255.0, bgra[i] / 255.0);
            // Perceived lightness (the square root of luminance is near enough), so the spread
            // means the same in a dark picture as in a light one.
            var light = Math.Sqrt(Luminance(color));
            (r, g, b) = (r + color.Red, g + color.Green, b + color.Blue);
            sum += light;
            squares += light * light;
            count++;
        }
        if (count == 0) return (SceneColor.Black, 0);
        var mean = sum / count;
        return (new SceneColor(r / count, g / count, b / count), Math.Sqrt(Math.Max(0, squares / count - mean * mean)));
    }

    // MARK: The assistant's second look (optional)

    public sealed record VisionNote(int SlideNumber, string Note);

    public const int MostPictures = 8;

    public static string VisionSchema => JsonSerializer.Serialize(new
    {
        type = "object",
        properties = new
        {
            notes = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new { slide = new { type = "integer" }, note = new { type = "string" } },
                    required = new[] { "slide", "note" },
                    additionalProperties = false,
                },
            },
        },
        required = new[] { "notes" },
        additionalProperties = false,
    });

    /// <summary>Asks the assistant to look at pictures of slides (PNG bytes, in slide order) for readability.</summary>
    /// <param name="numbers">Each picture's slide number, in the same order.</param>
    public static AssistantRequest VisionRequest(IReadOnlyList<byte[]> pictures, IReadOnlyList<int> numbers) => new()
    {
        Feature = "Slide checks (pictures)",
        Instructions =
            """
            You check church slides for how easily a congregation can read them from the back of a room, on a projector that washes colours out. You are shown pictures of slides in order. For each slide with a real readability problem, give one short note: text that is hard to read against its background, a busy picture behind words, too much text, text too small, words too near or over the edge, a lower third that covers something. Say what to change in a few words. Give no note for a slide that reads well. Do not comment on wording, theology or taste.
            """,
        Ask = $"The pictures are slides number {string.Join(", ", numbers)}, in that order. Use those numbers.",
        Schema = VisionSchema, Pictures = pictures, Effort = AssistantEffort.Low,
    };

    public static List<VisionNote> ReadVision(JsonElement reply, IReadOnlyList<int> numbers)
    {
        var notes = new List<VisionNote>();
        if (reply.ValueKind != JsonValueKind.Object || !reply.TryGetProperty("notes", out var list) || list.ValueKind != JsonValueKind.Array) return notes;
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("slide", out var slide) && slide.ValueKind == JsonValueKind.Number && slide.TryGetInt32(out var number) && numbers.Contains(number)
                && item.TryGetProperty("note", out var note) && note.ValueKind == JsonValueKind.String && note.GetString()!.Trim() is { Length: > 0 } words)
                notes.Add(new VisionNote(number, words.Length > 240 ? words[..240] : words));
        }
        return notes;
    }
}
