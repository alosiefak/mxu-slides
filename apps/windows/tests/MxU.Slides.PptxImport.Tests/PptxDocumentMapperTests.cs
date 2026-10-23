using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;

namespace MxU.Slides.PptxImport.Tests;

/// <summary>
/// The Mac tests use Helvetica as the installed font; Windows ships Arial instead,
/// so the font names here are Arial ones (named the way the Windows editor names faces).
/// </summary>
public class PptxDocumentMapperTests
{
    private static PptxDeck Deck(double cx, double cy, params PptxSlide[] slides) =>
        new(cx, cy) { Slides = [.. slides] };

    private static PptxDeck Deck(params PptxSlide[] slides) => Deck(12_192_000, 6_858_000, slides);

    private static PptxTransform Transform(
        double x = 914_400, double y = 457_200, double cx = 1_828_800, double cy = 914_400,
        double rot = 0, bool flipH = false, bool flipV = false) =>
        new(x, y, cx, cy, rot, flipH, flipV);

    private static PptxRun Run(
        string text, string? font = "Arial", double? size = 4000,
        bool bold = false, bool underline = false, bool strike = false,
        string? color = null, double? tracking = null) =>
        new()
        {
            Text = text, FontFamily = font, SizeHundredthsPt = size, Bold = bold,
            Underline = underline, Strike = strike, ColorHex = color, TrackingHundredthsPt = tracking,
        };

    private static PptxShape TextShape(
        IEnumerable<PptxRun> runs, string? anchor = null, string? alignment = null,
        bool hasNormAutofit = false, double? fontScale = null, PptxTransform? transform = null) =>
        new()
        {
            Kind = PptxShapeKind.Shape,
            Name = "Text Box",
            Transform = transform ?? Transform(),
            TextBody = new PptxTextBody
            {
                Anchor = anchor,
                HasNormAutofit = hasNormAutofit,
                AutofitFontScale = fontScale,
                Paragraphs = [new PptxParagraph { Alignment = alignment, Runs = [.. runs] }],
            },
        };

    private static PptxShape Shape(PptxShapeKind kind, string name, PptxTransform? transform = null) =>
        new() { Kind = kind, Name = name, Transform = transform ?? Transform() };

    private static PptxSlide SlideWith(params PptxShape[] shapes) => new() { Shapes = [.. shapes] };

    private static PptxMappedDocument Map(PptxDeck deck) => PptxDocumentMapper.Map(deck, "Deck", "test-id");

    private static void AssertFill(ObjectFill expected, ObjectFill? actual) =>
        Assert.Equal(MxuJson.Serialize(expected, indented: false), MxuJson.Serialize(actual, indented: false));

    [Fact]
    public void SixteenNineDeckScalesEmuToSceneAndLeavesCanvasNil()
    {
        var mapped = Map(Deck(SlideWith(TextShape([Run("Hello")]))));
        Assert.Null(mapped.Presentation.CanvasWidth);
        Assert.Null(mapped.Presentation.CanvasHeight);
        var item = mapped.Presentation.Slides[0].Objects[0];
        Assert.Equal(SlideObjectKind.Text, item.ObjectKind);
        Assert.Equal(144, item.X);
        Assert.Equal(72, item.Y);
        Assert.Equal(288, item.Width);
        Assert.Equal(144, item.Height);

        Assert.Equal(80, item.TextStyle?.FontSize);
    }

    [Fact]
    public void FourThreeDeckGetsExplicitCanvasAndKScaledFonts()
    {
        var mapped = Map(Deck(9_144_000, 6_858_000, SlideWith(TextShape([Run("Hello")]))));
        Assert.Equal(1440, mapped.Presentation.CanvasWidth);
        Assert.Equal(1080, mapped.Presentation.CanvasHeight);
        Assert.Equal(80, mapped.Presentation.Slides[0].Objects[0].TextStyle?.FontSize);
    }

    [Fact]
    public void RotationConvertsWithoutNegation()
    {
        var shape = TextShape([Run("Hello")], transform: Transform(rot: 5_400_000));
        var mapped = Map(Deck(SlideWith(shape)));
        Assert.Equal(90, mapped.Presentation.Slides[0].Objects[0].RotationDegrees);
    }

    [Fact]
    public void FlipsCarryAndAbsentFlipsStayNil()
    {
        var flipped = TextShape([Run("Hello")], transform: Transform(flipH: true, flipV: true));
        var plain = TextShape([Run("There")]);
        var objects = Map(Deck(SlideWith(flipped, plain))).Presentation.Slides[0].Objects;
        Assert.True(objects[0].FlipHorizontal);
        Assert.True(objects[0].FlipVertical);
        Assert.Null(objects[1].FlipHorizontal);
        Assert.Null(objects[1].FlipVertical);
    }

    [Fact]
    public void PictureCropBecomesSourceRectWithStretch()
    {
        var picture = Shape(PptxShapeKind.Picture, "Photo");
        picture.MediaPath = "/tmp/extracted/ppt/media/image1.png";
        picture.SourceRect = new PptxSourceRect(L: 25_000, T: 10_000, R: 25_000, B: 10_000);
        var mapped = Map(Deck(SlideWith(picture)));
        var item = mapped.Presentation.Slides[0].Objects[0];

        Assert.Equal(SlideObjectKind.Shape, item.ObjectKind);
        Assert.StartsWith(PptxDocumentMapper.PlaceholderPrefix, item.Fill?.MediaId);
        Assert.Equal(MediaScaleMode.Stretch, item.Fill?.MediaScaleMode);
        Assert.True(MxuJson.StructurallyEqual(new MediaSourceRect { X = 0.25, Y = 0.1, Width = 0.5, Height = 0.8 }, item.Fill?.MediaSourceRect));
        Assert.Single(mapped.MediaWants);
        Assert.Equal("/tmp/extracted/ppt/media/image1.png", mapped.MediaWants[0].AbsolutePath);
    }

    [Fact]
    public void UncroppedPictureHasNoSourceRect()
    {
        var picture = Shape(PptxShapeKind.Picture, "Photo");
        picture.MediaPath = "/tmp/extracted/ppt/media/image1.png";
        var item = Map(Deck(SlideWith(picture))).Presentation.Slides[0].Objects[0];
        Assert.Null(item.Fill?.MediaSourceRect);
        Assert.Equal(MediaScaleMode.Stretch, item.Fill?.MediaScaleMode);
    }

    [Fact]
    public void RoundRectCornerRadiusScalesFromAdjustment()
    {
        var shape = Shape(PptxShapeKind.Shape, "Card");
        shape.PresetGeometry = "roundRect";
        shape.Fill = new PptxFill.Solid("FF0000");
        var item = Map(Deck(SlideWith(shape))).Presentation.Slides[0].Objects[0];
        Assert.Equal(ShapeKind.RoundedRectangle, item.ShapeKind);

        Assert.True(Math.Abs((item.CornerRadius ?? 0) - 0.16667 * 144) < 0.01);

        shape.RoundRectAdjustment = 50_000;
        Assert.Equal(72, Map(Deck(SlideWith(shape))).Presentation.Slides[0].Objects[0].CornerRadius);
    }

    [Fact]
    public void UnknownPresetApproximatesAsRectangleWithWarning()
    {
        var shape = Shape(PptxShapeKind.Shape, "Heart");
        shape.PresetGeometry = "heart";
        shape.Fill = new PptxFill.Solid("FF0000");
        var mapped = Map(Deck(SlideWith(shape)));
        Assert.Equal(ShapeKind.Rectangle, mapped.Presentation.Slides[0].Objects[0].ShapeKind);
        Assert.Contains(mapped.Warnings, w => w.Contains("heart") && w.Contains("approximated"));
    }

    [Fact]
    public void SolidFillCarriesHexWithAlpha()
    {
        var shape = Shape(PptxShapeKind.Shape, "Band");
        shape.Fill = new PptxFill.Solid("1a2b3c");
        shape.Stroke = new PptxStroke("00FF00", 25_400);
        var item = Map(Deck(SlideWith(shape))).Presentation.Slides[0].Objects[0];
        AssertFill(new ObjectFill { FillKind = FillKind.Solid, ColorHex = "#1A2B3CFF" }, item.Fill);

        Assert.True(MxuJson.StructurallyEqual(new ObjectStroke { ColorHex = "#00FF00FF", Width = 4 }, item.Stroke));
    }

    [Fact]
    public void InklessEmptyShapeIsDropped()
    {
        var mapped = Map(Deck(SlideWith(Shape(PptxShapeKind.Shape, "Ghost"))));
        Assert.Empty(mapped.Presentation.Slides[0].Objects);
    }

    [Fact]
    public void GradientFillMapsAngleAndStopUnits()
    {
        var shape = Shape(PptxShapeKind.Shape, "Wash");
        shape.Fill = new PptxFill.Gradient(new PptxGradient(2_700_000, [new(0, "FF0000FF"), new(100_000, "0000FF80")]));
        var fill = Map(Deck(SlideWith(shape))).Presentation.Slides[0].Objects[0].Fill;
        AssertFill(new ObjectFill
        {
            FillKind = FillKind.LinearGradient,
            GradientAngleDegrees = 45,
            GradientStops =
            [
                new GradientStop { ColorHex = "#FF0000FF", Position = 0 },
                new GradientStop { ColorHex = "#0000FF80", Position = 1 },
            ],
        }, fill);
    }

    [Fact]
    public void GradientBackgroundRidesTheBackdropLayer()
    {
        var slide = new PptxSlide
        {
            BackgroundFill = new PptxFill.Gradient(new PptxGradient(5_400_000, [new(0, "000000FF"), new(100_000, "FFFFFFFF")])),
        };
        var mapped = Map(Deck(slide));

        Assert.Empty(mapped.Presentation.Slides[0].Objects);
        var fill = mapped.Presentation.BackgroundFill;
        Assert.Equal(FillKind.LinearGradient, fill?.FillKind);
        Assert.Equal(90, fill?.GradientAngleDegrees);
        Assert.Null(mapped.Presentation.Slides[0].Background);
    }

    [Fact]
    public void MidLineBoldRunBecomesFontNameRun()
    {
        var shape = TextShape([Run("Hello "), Run("bold", bold: true), Run(" world")]);
        var item = Map(Deck(SlideWith(shape))).Presentation.Slides[0].Objects[0];
        Assert.Equal("Arial", item.TextStyle?.FontName);
        var styleRun = Assert.IsType<TextStyleRun>(item.StyleRuns?.FirstOrDefault());
        Assert.Equal(0, styleRun.Line);
        Assert.Equal(6, styleRun.Column);
        Assert.Equal(4, styleRun.Length);
        Assert.Equal("Arial-Bold", styleRun.FontName);
    }

    [Fact]
    public void PartialUnderlineBecomesExactRuns()
    {
        var shape = TextShape([Run("ab"), Run("cd", underline: true), Run("ef")]);
        var item = Map(Deck(SlideWith(shape))).Presentation.Slides[0].Objects[0];
        Assert.Null(item.TextStyle?.Underline);
        Assert.True(MxuJson.StructurallyEqual(
            new List<TextStyleRun> { new() { Line = 0, Column = 2, Length = 2, Underline = true } }, item.StyleRuns));
    }

    [Fact]
    public void BodyUniformUnderlineRidesTextStyle()
    {
        var shape = TextShape([Run("all", underline: true), Run(" underlined", underline: true)]);
        var item = Map(Deck(SlideWith(shape))).Presentation.Slides[0].Objects[0];
        Assert.True(item.TextStyle?.Underline);
        Assert.Null(item.StyleRuns);
    }

    [Fact]
    public void AnchorIsAlwaysWritten()
    {
        var objects = Map(Deck(SlideWith(TextShape([Run("top")]), TextShape([Run("middle")], anchor: "ctr")))).Presentation.Slides[0].Objects;
        Assert.Equal(TextVerticalAlignment.Top, objects[0].TextStyle?.VerticalAlignment);
        Assert.Equal(TextVerticalAlignment.Middle, objects[1].TextStyle?.VerticalAlignment);
    }

    [Fact]
    public void AlignmentMapsAndJustifiedWarns()
    {
        var mapped = Map(Deck(SlideWith(TextShape([Run("mid")], alignment: "ctr"), TextShape([Run("both edges")], alignment: "just"))));
        Assert.Equal(TextHorizontalAlignment.Center, mapped.Presentation.Slides[0].Objects[0].TextStyle?.HorizontalAlignment);
        Assert.Equal(TextHorizontalAlignment.Left, mapped.Presentation.Slides[0].Objects[1].TextStyle?.HorizontalAlignment);
        Assert.Contains(mapped.Warnings, w => w.Contains("justified"));
    }

    [Fact]
    public void NormAutofitShrinksAndBakesFontScale()
    {
        var shape = TextShape([Run("shrunk")], hasNormAutofit: true, fontScale: 62_500);
        var style = Map(Deck(SlideWith(shape))).Presentation.Slides[0].Objects[0].TextStyle;
        Assert.True(style?.AutoShrink);

        Assert.Equal(50, style?.FontSize);
    }

    [Fact]
    public void DefaultBodyInsetsAreWrittenScaled()
    {
        var style = Map(Deck(SlideWith(TextShape([Run("Hi")])))).Presentation.Slides[0].Objects[0].TextStyle;

        Assert.Equal(7.2, style?.InsetTop);
        Assert.Equal(7.2, style?.InsetBottom);
        Assert.Equal(14.4, style?.InsetLeft);
        Assert.Equal(14.4, style?.InsetRight);
    }

    [Fact]
    public void LineDominantDeltaBecomesLineStyleOverride()
    {
        var shape = Shape(PptxShapeKind.Shape, "Text");
        shape.TextBody = new PptxTextBody
        {
            Paragraphs =
            [
                new PptxParagraph { Runs = [Run("The long base line")] },
                new PptxParagraph { Runs = [Run("Small line", size: 2000)] },
            ],
        };
        var item = Map(Deck(SlideWith(shape))).Presentation.Slides[0].Objects[0];
        Assert.Equal("The long base line\nSmall line", item.Text);
        Assert.True(MxuJson.StructurallyEqual(
            new List<LineStyleOverride> { new() { LineIndex = 1, FontSize = 40 } }, item.TextStyle?.LineStyles));
        Assert.Null(item.StyleRuns);
    }

    [Fact]
    public void DistinctBackgroundsStayPerSlideWithNoSyntheticRect()
    {
        var first = SlideWith(TextShape([Run("Over")]));
        first.BackgroundFill = new PptxFill.Solid("112233");
        var second = new PptxSlide { BackgroundFill = new PptxFill.Solid("445566") };
        var mapped = Map(Deck(first, second));

        Assert.Null(mapped.Presentation.BackgroundFill);
        AssertFill(new ObjectFill { FillKind = FillKind.Solid, ColorHex = "#112233FF" }, mapped.Presentation.Slides[0].BackgroundFill);
        AssertFill(new ObjectFill { FillKind = FillKind.Solid, ColorHex = "#445566FF" }, mapped.Presentation.Slides[1].BackgroundFill);
        Assert.Single(mapped.Presentation.Slides[0].Objects);
        Assert.Equal(SlideObjectKind.Text, mapped.Presentation.Slides[0].Objects[0].ObjectKind);
    }

    [Fact]
    public void UniformBackgroundHoistsToPresentation()
    {
        var first = new PptxSlide { BackgroundFill = new PptxFill.Solid("112233") };
        var second = new PptxSlide { BackgroundFill = new PptxFill.Solid("112233") };
        var mapped = Map(Deck(first, second));
        AssertFill(new ObjectFill { FillKind = FillKind.Solid, ColorHex = "#112233FF" }, mapped.Presentation.BackgroundFill);
        Assert.All(mapped.Presentation.Slides, s => Assert.Null(s.BackgroundFill));

        var mixed = Map(Deck(first, new PptxSlide()));
        Assert.Null(mixed.Presentation.BackgroundFill);
        Assert.NotNull(mixed.Presentation.Slides[0].BackgroundFill);
    }

    [Fact]
    public void PictureBackgroundBecomesBottomSlideObject()
    {
        var slide = new PptxSlide { BackgroundFill = new PptxFill.Blip("/tmp/extracted/ppt/media/bg.png") };
        var mapped = Map(Deck(slide));
        Assert.Null(mapped.Presentation.Slides[0].Background);
        var backdrop = mapped.Presentation.Slides[0].Objects[0];
        Assert.Equal(SlideObjectKind.Shape, backdrop.ObjectKind);
        Assert.Equal(FillKind.Media, backdrop.Fill?.FillKind);
        Assert.StartsWith(PptxDocumentMapper.PlaceholderPrefix, backdrop.Fill?.MediaId);
        Assert.Equal(0, backdrop.X);
        Assert.Equal(1920, backdrop.Width);
        Assert.Equal(1080, backdrop.Height);
        Assert.Single(mapped.MediaWants);
    }

    [Fact]
    public void FadeMapsAndPushDegradesToDissolveWithWarning()
    {
        var fade = new PptxSlide { Transition = new PptxTransition { Kind = "fade", DurationSeconds = 0.7 } };
        var fadeBlack = new PptxSlide { Transition = new PptxTransition { Kind = "fade", ThroughBlack = true, DurationSeconds = 0.5 } };
        var push = new PptxSlide { Transition = new PptxTransition { Kind = "push", DurationSeconds = 0.75, AdvanceAfterMs = 5000 } };
        var mapped = Map(Deck(fade, fadeBlack, push));
        var slides = mapped.Presentation.Slides;
        Assert.True(MxuJson.StructurallyEqual(new Transition { TransitionKind = TransitionKind.Dissolve, DurationSeconds = 0.7 }, slides[0].Transition));
        Assert.True(MxuJson.StructurallyEqual(new Transition { TransitionKind = TransitionKind.FadeBlack, DurationSeconds = 0.5 }, slides[1].Transition));
        Assert.True(MxuJson.StructurallyEqual(new Transition { TransitionKind = TransitionKind.Dissolve, DurationSeconds = 0.75 }, slides[2].Transition));
        Assert.True(MxuJson.StructurallyEqual(new AutoAdvance { DelaySeconds = 5 }, slides[2].AutoAdvance));
        Assert.Contains(mapped.Warnings, w => w.Contains("push") && w.Contains("dissolve"));
    }

    [Fact]
    public void AdvanceTimeWithoutEffectStillAutoAdvances()
    {
        var slide = new PptxSlide { Transition = new PptxTransition { AdvanceAfterMs = 3000 } };
        var mapped = Map(Deck(slide));
        Assert.Null(mapped.Presentation.Slides[0].Transition);
        Assert.True(MxuJson.StructurallyEqual(new AutoAdvance { DelaySeconds = 3 }, mapped.Presentation.Slides[0].AutoAdvance));
    }

    [Fact]
    public void NotesCarry()
    {
        var mapped = Map(Deck(new PptxSlide { Notes = "Greet the guest speaker" }));
        Assert.Equal("Greet the guest speaker", mapped.Presentation.Slides[0].Notes);
    }

    [Fact]
    public void CuratedPresetsBecomeExactPaths()
    {
        var triangle = Shape(PptxShapeKind.Shape, "Tri");
        triangle.PresetGeometry = "triangle";
        triangle.Fill = new PptxFill.Solid("FF0000");
        var star = triangle with { PresetGeometry = "star5" };
        var mapped = Map(Deck(SlideWith(triangle, star)));
        var triangleObject = mapped.Presentation.Slides[0].Objects[0];
        Assert.Equal(ShapeKind.Path, triangleObject.ShapeKind);
        Assert.Equal("M 0.5000 0.0000 C 0.5000 0.0000 1.0000 1.0000 1.0000 1.0000 C 1.0000 1.0000 0.0000 1.0000 0.0000 1.0000 C 0.0000 1.0000 0.5000 0.0000 0.5000 0.0000 Z", triangleObject.PathData);
        var starObject = mapped.Presentation.Slides[0].Objects[1];
        Assert.Equal(ShapeKind.Path, starObject.ShapeKind);
        Assert.Equal("M 0.5000 0.0000 C 0.5000 0.0000 0.3820 0.3820 0.3820 0.3820 C 0.3820 0.3820 0.0000 0.3820 0.0000 0.3820 C 0.0000 0.3820 0.3090 0.6180 0.3090 0.6180 C 0.3090 0.6180 0.1910 1.0000 0.1910 1.0000 C 0.1910 1.0000 0.5000 0.7639 0.5000 0.7639 C 0.5000 0.7639 0.8090 1.0000 0.8090 1.0000 C 0.8090 1.0000 0.6910 0.6180 0.6910 0.6180 C 0.6910 0.6180 1.0000 0.3820 1.0000 0.3820 C 1.0000 0.3820 0.6180 0.3820 0.6180 0.3820 C 0.6180 0.3820 0.5000 0.0000 0.5000 0.0000 Z", starObject.PathData);
        Assert.DoesNotContain(mapped.Warnings, w => w.Contains("approximated as a rectangle"));
    }

    [Fact]
    public void FlowChartConnectorIsACircleNotAnApproximation()
    {
        var shape = Shape(PptxShapeKind.Shape, "Node");
        shape.PresetGeometry = "flowChartConnector";
        shape.Fill = new PptxFill.Solid("FF0000");
        var mapped = Map(Deck(SlideWith(shape)));
        Assert.Equal(ShapeKind.Ellipse, mapped.Presentation.Slides[0].Objects[0].ShapeKind);
        Assert.Empty(mapped.Warnings);
    }

    [Fact]
    public void VerticalArrowsJoinTheCuratedTable()
    {
        var up = Shape(PptxShapeKind.Shape, "Up");
        up.PresetGeometry = "upArrow";
        up.Fill = new PptxFill.Solid("FF0000");
        var down = up with { PresetGeometry = "downArrow" };
        var mapped = Map(Deck(SlideWith(up, down)));
        Assert.Equal("M 0.2500 1.0000 C 0.2500 1.0000 0.2500 0.4000 0.2500 0.4000 C 0.2500 0.4000 0.0000 0.4000 0.0000 0.4000 C 0.0000 0.4000 0.5000 0.0000 0.5000 0.0000 C 0.5000 0.0000 1.0000 0.4000 1.0000 0.4000 C 1.0000 0.4000 0.7500 0.4000 0.7500 0.4000 C 0.7500 0.4000 0.7500 1.0000 0.7500 1.0000 C 0.7500 1.0000 0.2500 1.0000 0.2500 1.0000 Z", mapped.Presentation.Slides[0].Objects[0].PathData);
        Assert.Equal("M 0.2500 0.0000 C 0.2500 0.0000 0.2500 0.6000 0.2500 0.6000 C 0.2500 0.6000 0.0000 0.6000 0.0000 0.6000 C 0.0000 0.6000 0.5000 1.0000 0.5000 1.0000 C 0.5000 1.0000 1.0000 0.6000 1.0000 0.6000 C 1.0000 0.6000 0.7500 0.6000 0.7500 0.6000 C 0.7500 0.6000 0.7500 0.0000 0.7500 0.0000 C 0.7500 0.0000 0.2500 0.0000 0.2500 0.0000 Z", mapped.Presentation.Slides[0].Objects[1].PathData);
        Assert.Empty(mapped.Warnings);
    }

    [Fact]
    public void MissingFontsNeverArmAutoShrink()
    {
        var missing = Map(Deck(SlideWith(TextShape([Run("Hello", font: "DefinitelyNotInstalledSans")]))));
        Assert.Null(missing.Presentation.Slides[0].Objects[0].TextStyle?.AutoShrink);

        var shape = Shape(PptxShapeKind.Shape, "Text Box");
        shape.TextBody = new PptxTextBody { HasSpAutofit = true, Paragraphs = [new PptxParagraph { Runs = [Run("Hi")] }] };
        var resized = Map(Deck(SlideWith(shape)));
        Assert.Null(resized.Presentation.Slides[0].Objects[0].TextStyle?.AutoShrink);
    }

    [Fact]
    public void BorderedPictureBecomesStrokedMediaShape()
    {
        var picture = Shape(PptxShapeKind.Picture, "P");
        picture.MediaPath = "/tmp/x.png";
        picture.Stroke = new PptxStroke("000000FF", 38100);
        var item = Map(Deck(SlideWith(picture))).Presentation.Slides[0].Objects[0];
        Assert.Equal(SlideObjectKind.Shape, item.ObjectKind);
        Assert.Null(item.ShapeKind);
        Assert.Equal(FillKind.Media, item.Fill?.FillKind);
        Assert.True(MxuJson.StructurallyEqual(new ObjectStroke { ColorHex = "#000000FF", Width = 6 }, item.Stroke));
    }

    [Fact]
    public void BlipAlphaImportsAsObjectOpacity()
    {
        var picture = Shape(PptxShapeKind.Picture, "P");
        picture.MediaPath = "/tmp/x.png";
        picture.BlipAlpha = 0.4;
        Assert.Equal(0.4, Map(Deck(SlideWith(picture))).Presentation.Slides[0].Objects[0].Opacity);

        var opaque = Shape(PptxShapeKind.Picture, "P");
        opaque.MediaPath = "/tmp/x.png";
        Assert.Null(Map(Deck(SlideWith(opaque))).Presentation.Slides[0].Objects[0].Opacity);
    }

    [Fact]
    public void MissingFontFamiliesCollectDedupedInFirstSeenOrder()
    {
        var shape = TextShape(
        [
            Run("One ", font: "DefinitelyNotInstalledSans"),
            Run("two ", font: "AlsoAbsentSerif", bold: true),
            Run("three", font: "DefinitelyNotInstalledSans", bold: true),
        ]);
        var mapped = Map(Deck(SlideWith(shape)));
        Assert.Equal(new[] { "DefinitelyNotInstalledSans", "AlsoAbsentSerif" }, mapped.MissingFonts);
        Assert.Contains(mapped.Warnings, w => w.Contains("DefinitelyNotInstalledSans") && w.Contains("not installed"));

        var installed = Map(Deck(SlideWith(TextShape([Run("Hi")]))));
        Assert.Empty(installed.MissingFonts);
    }

    [Fact]
    public void CustomPathWinsOverPreset()
    {
        var shape = Shape(PptxShapeKind.Shape, "Torn");
        shape.PresetGeometry = "star5";
        shape.CustomPathData = "M 0.0000 0.0000 C 0.0000 0.0000 1.0000 1.0000 1.0000 1.0000";
        shape.Fill = new PptxFill.Solid("FF0000");
        var item = Map(Deck(SlideWith(shape))).Presentation.Slides[0].Objects[0];
        Assert.Equal(ShapeKind.Path, item.ShapeKind);
        Assert.Equal(shape.CustomPathData, item.PathData);
    }

    [Fact]
    public void ConnectorsBecomeStrokedLines()
    {
        var straight = Shape(PptxShapeKind.Shape, "Line");
        straight.PresetGeometry = "line";
        straight.Stroke = new PptxStroke("FF0000FF", 12_700);
        var bent = straight with { Name = "Elbow", PresetGeometry = "bentConnector3" };
        var mapped = Map(Deck(SlideWith(straight, bent)));
        var objects = mapped.Presentation.Slides[0].Objects;
        Assert.Equal(2, objects.Count);
        Assert.Equal(ShapeKind.Path, objects[0].ShapeKind);
        Assert.Equal(PptxPresetGeometry.LinePath, objects[0].PathData);
        Assert.Equal("#FF0000FF", objects[0].Stroke?.ColorHex);
        Assert.Equal(PptxPresetGeometry.LinePath, objects[1].PathData);
        Assert.Contains(mapped.Warnings, w => w.Contains("bentConnector3") && w.Contains("straight line"));
    }

    [Fact]
    public void PictureWithCustomGeometryBecomesShapeWithMediaFill()
    {
        var picture = Shape(PptxShapeKind.Picture, "Torn Photo");
        picture.MediaPath = "/tmp/extracted/ppt/media/image1.png";
        picture.CustomPathData = "M 0.0000 0.0000 C 0.0000 0.0000 1.0000 1.0000 1.0000 1.0000";
        picture.SourceRect = new PptxSourceRect(L: 25_000, T: 0, R: 25_000, B: 0);
        var mapped = Map(Deck(SlideWith(picture)));
        var item = mapped.Presentation.Slides[0].Objects[0];

        Assert.Equal(SlideObjectKind.Shape, item.ObjectKind);
        Assert.Equal(ShapeKind.Path, item.ShapeKind);
        Assert.Equal(picture.CustomPathData, item.PathData);
        Assert.Null(item.MediaId);
        Assert.Equal(FillKind.Media, item.Fill?.FillKind);
        Assert.StartsWith(PptxDocumentMapper.PlaceholderPrefix, item.Fill?.MediaId);
        Assert.Equal(MediaScaleMode.Stretch, item.Fill?.MediaScaleMode);
        Assert.True(MxuJson.StructurallyEqual(new MediaSourceRect { X = 0.25, Y = 0, Width = 0.5, Height = 1 }, item.Fill?.MediaSourceRect));
        Assert.Single(mapped.MediaWants);
    }

    [Fact]
    public void PictureWithEllipsePresetClipsAndUncuratedStaysRectangle()
    {
        var round = Shape(PptxShapeKind.Picture, "Portrait");
        round.MediaPath = "/tmp/a.png";
        round.PresetGeometry = "ellipse";
        var heart = Shape(PptxShapeKind.Picture, "Valentine");
        heart.MediaPath = "/tmp/b.png";
        heart.PresetGeometry = "heart";
        var mapped = Map(Deck(SlideWith(round, heart)));
        var ellipse = mapped.Presentation.Slides[0].Objects[0];
        Assert.Equal(SlideObjectKind.Shape, ellipse.ObjectKind);
        Assert.Equal(ShapeKind.Ellipse, ellipse.ShapeKind);
        Assert.Equal(FillKind.Media, ellipse.Fill?.FillKind);

        var fallback = mapped.Presentation.Slides[0].Objects[1];
        Assert.Equal(SlideObjectKind.Shape, fallback.ObjectKind);
        Assert.Null(fallback.ShapeKind);
        Assert.Null(fallback.PathData);
        Assert.NotNull(fallback.Fill?.MediaId);
        Assert.Contains(mapped.Warnings, w => w.Contains("heart") && w.Contains("approximated"));
    }

    [Fact]
    public void AudioPictureBecomesFireAudioActionNotAnObject()
    {
        var audio = Shape(PptxShapeKind.Picture, "Bell");
        audio.MediaKind = PptxMediaKind.Audio;
        audio.MediaPath = "/tmp/extracted/ppt/media/media1.mp3";
        var mapped = Map(Deck(SlideWith(audio)));
        var slide = mapped.Presentation.Slides[0];
        Assert.Empty(slide.Objects);
        var action = Assert.IsType<SlideAction>(slide.Actions?.FirstOrDefault());
        Assert.Equal(SlideActionKind.FireAudio, action.Kind);
        var placeholder = Assert.IsType<string>(action.AudioItemId);
        Assert.StartsWith(PptxDocumentMapper.PlaceholderPrefix, placeholder);
        Assert.Single(mapped.MediaWants);
        Assert.Contains(mapped.Warnings, w => w.Contains("play action"));

        var resolved = PptxDocumentMapper.ReplacingMediaIds(mapped.Presentation, new Dictionary<string, string> { [placeholder] = "audio-1" });
        Assert.Equal("audio-1", resolved.Slides[0].Actions?.FirstOrDefault()?.AudioItemId);
        var stripped = PptxDocumentMapper.ReplacingMediaIds(mapped.Presentation, new Dictionary<string, string>());
        Assert.Null(stripped.Slides[0].Actions);
    }

    [Fact]
    public void VideoPictureBecomesMediaFilledShapeWithWant()
    {
        var video = Shape(PptxShapeKind.Picture, "Clip");
        video.MediaKind = PptxMediaKind.Video;
        video.MediaPath = "/tmp/extracted/ppt/media/media1.mp4";
        var mapped = Map(Deck(SlideWith(video)));
        var item = mapped.Presentation.Slides[0].Objects[0];
        Assert.Equal(SlideObjectKind.Shape, item.ObjectKind);
        Assert.Equal(FillKind.Media, item.Fill?.FillKind);
        Assert.Equal("/tmp/extracted/ppt/media/media1.mp4", mapped.MediaWants.FirstOrDefault()?.AbsolutePath);
    }

    [Fact]
    public void SectionsMapWithSlideMembership()
    {
        var built = Deck(new PptxSlide(), new PptxSlide(), new PptxSlide());
        built.Sections =
        [
            new PptxSection("aaaa-1", "Intro") { SlideIndexes = [0, 1] },
            new PptxSection("bbbb-2", "Empty") { SlideIndexes = [9] },
            new PptxSection("cccc-3", "Close") { SlideIndexes = [2] },
        ];
        var mapped = Map(built);
        Assert.True(MxuJson.StructurallyEqual(
            new List<PresentationSection> { new() { Id = "aaaa-1", Name = "Intro" }, new() { Id = "cccc-3", Name = "Close" } },
            mapped.Presentation.Sections));
        Assert.Equal(new[] { "aaaa-1", "aaaa-1", "cccc-3" }, mapped.Presentation.Slides.Select(s => s.SectionId));
    }

    [Fact]
    public void ReplacingMediaIdsRemapsAndStripsUnresolved()
    {
        var picture = Shape(PptxShapeKind.Picture, "Photo");
        picture.MediaPath = "/tmp/a.png";
        var filled = Shape(PptxShapeKind.Shape, "Band");
        filled.Fill = new PptxFill.Blip("/tmp/b.png");
        var slide = SlideWith(picture, filled);
        slide.BackgroundFill = new PptxFill.Blip("/tmp/c.png");
        var mapped = Map(Deck(slide));

        var backdropId = mapped.Presentation.Slides[0].Objects[0].Fill!.MediaId!;
        var replaced = PptxDocumentMapper.ReplacingMediaIds(mapped.Presentation, new Dictionary<string, string> { [backdropId] = "real-media-id" });
        var objects = replaced.Slides[0].Objects;
        Assert.Single(objects);
        Assert.Equal("real-media-id", objects[0].Fill?.MediaId);
        Assert.Null(replaced.Slides[0].Background);

        var stripped = PptxDocumentMapper.ReplacingMediaIds(mapped.Presentation, new Dictionary<string, string>());
        Assert.Empty(stripped.Slides[0].Objects);
    }

    [Fact]
    public void TimingEffectsBecomeAnimationSteps()
    {
        var title = TextShape([Run("Hello")]);
        title.ShapeId = 2;
        var points = Shape(PptxShapeKind.Shape, "Points", Transform(y: 2_000_000));
        points.TextBody = new PptxTextBody
        {
            Paragraphs = [new PptxParagraph { Runs = [Run("One")] }, new PptxParagraph { Runs = [Run("Two")] }],
        };
        points.ShapeId = 3;
        var slide = new PptxSlide
        {
            Name = "S",
            Shapes = [title, points],
            AnimationSteps =
            [
                new(2, PresetClass: "entr", PresetId: 2, PresetSubtype: 8, NodeType: "clickEffect", DelayMs: 0, DurationMs: 750),
                new(3, PresetClass: "entr", PresetId: 10, NodeType: "clickEffect", DelayMs: 100, DurationMs: 500, ParagraphStart: 0, ParagraphEnd: 0),
                new(3, PresetClass: "entr", PresetId: 10, NodeType: "afterEffect", DelayMs: 500, DurationMs: 500, ParagraphStart: 1, ParagraphEnd: 1),
                new(2, PresetClass: "emph", PresetId: 6, NodeType: "withEffect", DurationMs: 1000),
                new(2, PresetClass: "exit", PresetId: 58, NodeType: "clickEffect", DurationMs: 400),
                new(99, PresetClass: "entr", PresetId: 10, NodeType: "clickEffect"),
            ],
        };
        var mapped = Map(Deck(slide));
        var output = mapped.Presentation.Slides[0];
        var titleObject = output.Objects.First(o => o.Text == "Hello");
        var pointsObject = output.Objects.First(o => o.Text == "One\nTwo");

        var t = titleObject.AnimationSteps ?? [];
        Assert.Equal(3, t.Count);
        Assert.True(t[0].Kind == AnimationKind.In && t[0].Animation == StepAnimation.Move && t[0].Edge == AnimationEdge.Left
            && t[0].Trigger == AnimationTrigger.OnClick && t[0].DurationSeconds == 0.75);
        Assert.True(t[1].Kind == AnimationKind.Emphasis && t[1].Animation == StepAnimation.Scale && t[1].Trigger == AnimationTrigger.WithPrevious);
        Assert.True(t[2].Kind == AnimationKind.Out && t[2].Animation == StepAnimation.Scale && t[2].Trigger == AnimationTrigger.OnClick);

        var p = pointsObject.AnimationSteps ?? [];
        Assert.Equal(2, p.Count);
        Assert.True(MxuJson.StructurallyEqual(new List<AnimationRange> { new() { Line = 0, Column = 0, Length = 3 } }, p[0].Ranges));
        Assert.Equal(0.1, p[0].DelaySeconds);
        Assert.True(MxuJson.StructurallyEqual(new List<AnimationRange> { new() { Line = 1, Column = 0, Length = 3 } }, p[1].Ranges));
        Assert.Equal(AnimationTrigger.AfterPrevious, p[1].Trigger);
        Assert.Null(p[1].DelaySeconds);

        Assert.Equal(new[] { t[0].Id, p[0].Id, p[1].Id, t[1].Id, t[2].Id }, output.AnimationOrder);

        Assert.Contains(mapped.Warnings, w => w.Contains("entrance/exit 58"));
        Assert.Contains("PowerPoint animation", output.Notes);

        Assert.All(output.Objects, o => Assert.All(o.AnimationSteps ?? [], s => Assert.NotEqual(AnimationTrigger.OnDismiss, s.Trigger)));
    }

    [Fact]
    public void EdgeMovesThatFlyInTogetherShareOneOffset()
    {
        var left = TextShape([Run("Left")], transform: Transform(x: 0));
        left.ShapeId = 2;
        var right = TextShape([Run("Right")], transform: Transform(x: 4_572_000));
        right.ShapeId = 3;
        var slide = new PptxSlide
        {
            Shapes = [left, right],
            AnimationSteps =
            [
                new(2, PresetClass: "entr", PresetId: 2, PresetSubtype: 8, NodeType: "clickEffect", DurationMs: 500),
                new(3, PresetClass: "entr", PresetId: 2, PresetSubtype: 8, NodeType: "withEffect", DurationMs: 500),
            ],
        };
        var objects = Map(Deck(slide)).Presentation.Slides[0].Objects;
        var steps = objects.Select(o => Assert.Single(o.AnimationSteps ?? [])).ToList();
        Assert.All(steps, s => Assert.Null(s.Edge));
        Assert.All(steps, s => Assert.Equal(-(720 + 288.0), s.OffsetX));
        Assert.All(steps, s => Assert.Null(s.OffsetY));
    }
}
