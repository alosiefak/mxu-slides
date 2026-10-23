using MxU.Slides.Core.Model;

namespace MxU.Slides.PptxImport;

internal sealed record PptxDeck(double SlideWidthEmu, double SlideHeightEmu)
{
    public List<PptxSlide> Slides { get; set; } = [];
    public List<PptxSection> Sections { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

internal sealed record PptxSection(string Id, string Name)
{
    public List<int> SlideIndexes { get; set; } = [];

    public bool Equals(PptxSection? other) =>
        other is not null && Id == other.Id && Name == other.Name && SlideIndexes.SequenceEqual(other.SlideIndexes);

    public override int GetHashCode() => HashCode.Combine(Id, Name);
}

internal sealed record PptxSlide
{
    public string? Name { get; set; }
    public List<PptxShape> Shapes { get; set; } = [];
    public PptxFill? BackgroundFill { get; set; }
    public PptxTransition? Transition { get; set; }
    public string? Notes { get; set; }
    public List<PptxAnimation> AnimationSteps { get; set; } = [];
}

internal sealed record PptxAnimation(
    int ShapeId,
    string? PresetClass = null,
    int? PresetId = null,
    int? PresetSubtype = null,
    string? NodeType = null,
    double DelayMs = 0,
    double? DurationMs = null,
    int? ParagraphStart = null,
    int? ParagraphEnd = null);

internal abstract record PptxFill
{
    public sealed record NoFill : PptxFill;

    public sealed record Solid(string ColorHex) : PptxFill;

    public sealed record Blip(string Path) : PptxFill;

    public sealed record BlipTiled(string Path, double Alpha, double ScaleX, double ScaleY, bool Tiled) : PptxFill;

    public sealed record Pattern(string Preset, string ForegroundHex, string BackgroundHex) : PptxFill;

    public sealed record Gradient(PptxGradient Value) : PptxFill;
}

internal sealed record PptxGradientStop(double Position, string ColorHex);

internal sealed record PptxGradient(double? Angle60k, List<PptxGradientStop> Stops)
{
    public bool Equals(PptxGradient? other) =>
        other is not null && Angle60k == other.Angle60k && Stops.SequenceEqual(other.Stops);

    public override int GetHashCode() => HashCode.Combine(Angle60k, Stops.Count);
}

internal sealed record PptxStroke(string ColorHex, double WidthEmu, StrokeDashKind? Dash = null);

internal record struct PptxTransform(
    double OffXEmu, double OffYEmu, double ExtXEmu, double ExtYEmu,
    double Rotation60k = 0, bool FlipH = false, bool FlipV = false);

internal sealed record PptxSourceRect(double L = 0, double T = 0, double R = 0, double B = 0)
{
    public bool IsFullFrame => L == 0 && T == 0 && R == 0 && B == 0;
}

internal enum PptxMediaKind
{
    Image,
    Video,
    Audio,
}

internal enum PptxShapeKind
{
    Shape,
    Picture,
}

internal sealed record PptxShape
{
    public required PptxShapeKind Kind { get; set; }
    public string? Name { get; set; }
    public int? ShapeId { get; set; }
    public required PptxTransform Transform { get; set; }
    public PptxTextBody? TextBody { get; set; }
    public PptxFill? Fill { get; set; }
    public PptxStroke? Stroke { get; set; }
    public string? PresetGeometry { get; set; }
    public double? RoundRectAdjustment { get; set; }
    public string? CustomPathData { get; set; }
    public PptxSourceRect? SourceRect { get; set; }
    public string? MediaRelationshipId { get; set; }
    public string? MediaPath { get; set; }
    public PptxMediaKind MediaKind { get; set; } = PptxMediaKind.Image;
    public double? BlipAlpha { get; set; }
    public bool UsesBackgroundFill { get; set; }
    public bool UsesGroupFill { get; set; }
    public bool IsPlaceholder { get; set; }
    public string? PlaceholderType { get; set; }
    public int? PlaceholderIndex { get; set; }
}

internal sealed record PptxTextBody
{
    public string? Anchor { get; set; }
    public string? Wrap { get; set; }
    public double? TopInsetEmu { get; set; }
    public double? LeftInsetEmu { get; set; }
    public double? BottomInsetEmu { get; set; }
    public double? RightInsetEmu { get; set; }
    public bool HasNormAutofit { get; set; }
    public double? AutofitFontScale { get; set; }
    public bool HasSpAutofit { get; set; }
    public List<PptxParagraph> Paragraphs { get; set; } = [];

    /// <summary>Paragraphs split at line breaks: the lines the slide object's text is made of.</summary>
    public List<PptxTextLine> Lines
    {
        get
        {
            var result = new List<PptxTextLine>();
            foreach (var paragraph in Paragraphs)
            {
                var current = new PptxTextLine(paragraph.Alignment);
                foreach (var run in paragraph.Runs)
                {
                    if (run.IsBreak)
                    {
                        result.Add(current);
                        current = new PptxTextLine(paragraph.Alignment);
                    }
                    else
                    {
                        current.Runs.Add(run);
                    }
                }
                result.Add(current);
            }
            return result;
        }
    }

    public string PlainText => string.Join("\n", Lines.Select(l => l.Text));
}

internal sealed record PptxParagraph
{
    public string? Alignment { get; set; }
    public List<PptxRun> Runs { get; set; } = [];
}

internal sealed record PptxRun
{
    public string Text { get; init; } = "";
    public bool IsBreak { get; init; }
    public string? FontFamily { get; init; }
    public double? SizeHundredthsPt { get; init; }
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public bool Underline { get; init; }
    public bool Strike { get; init; }
    public string? ColorHex { get; init; }
    public double? TrackingHundredthsPt { get; init; }
}

internal sealed record PptxTransition
{
    public string? Kind { get; set; }
    public bool ThroughBlack { get; set; }
    public double? DurationSeconds { get; set; }
    public double? AdvanceAfterMs { get; set; }
}

internal sealed record PptxTextLine(string? Alignment)
{
    public List<PptxRun> Runs { get; } = [];

    public string Text => string.Concat(Runs.Select(r => r.Text));
}

internal sealed record PptxEmbeddedFont(string Typeface, string Variant, string FilePath);
