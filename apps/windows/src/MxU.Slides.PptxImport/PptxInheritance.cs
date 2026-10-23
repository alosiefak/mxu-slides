using System.Xml.Linq;

namespace MxU.Slides.PptxImport;

internal record struct PptxTextDefaults
{
    public string? Alignment { get; set; }
    public double? SizeHundredthsPt { get; set; }
    public bool? Bold { get; set; }
    public bool? Italic { get; set; }
    public bool? Underline { get; set; }
    public bool? Strike { get; set; }
    public string? ColorHex { get; set; }
    public string? FontFamily { get; set; }
    public double? TrackingHundredthsPt { get; set; }

    public readonly PptxTextDefaults Merging(PptxTextDefaults inner) => new()
    {
        Alignment = inner.Alignment ?? Alignment,
        SizeHundredthsPt = inner.SizeHundredthsPt ?? SizeHundredthsPt,
        Bold = inner.Bold ?? Bold,
        Italic = inner.Italic ?? Italic,
        Underline = inner.Underline ?? Underline,
        Strike = inner.Strike ?? Strike,
        ColorHex = inner.ColorHex ?? ColorHex,
        FontFamily = inner.FontFamily ?? FontFamily,
        TrackingHundredthsPt = inner.TrackingHundredthsPt ?? TrackingHundredthsPt,
    };
}

/// <summary>
/// A slide's layout → master → theme chain: placeholder geometry, the text style
/// cascade, the colour map and theme fill styles a slide inherits.
/// </summary>
internal sealed class PptxInheritance
{
    private readonly PptxPackage package;
    private readonly XElement? layoutRoot;
    private readonly XElement? masterRoot;
    private readonly XElement? themeElements;
    private readonly XElement? defaultTextStyle;
    private readonly string? majorLatinFamily;
    private readonly string? minorLatinFamily;

    public PptxInheritance(string slidePart, string? presentationPart, PptxPackage package)
    {
        this.package = package;

        string? Related(string? part, string suffix)
        {
            if (part is null) return null;
            var rel = package.Relationships(part).Values
                .FirstOrDefault(r => r.Type.EndsWith(suffix, StringComparison.Ordinal) && !r.External);
            return rel is null ? null : package.ResolveTarget(rel.Target, part);
        }
        LayoutPart = Related(slidePart, "/slideLayout");
        MasterPart = Related(LayoutPart, "/slideMaster");
        var themePart = Related(MasterPart, "/theme");
        layoutRoot = package.TryRoot(LayoutPart);
        masterRoot = package.TryRoot(MasterPart);
        themeElements = package.TryRoot(themePart)?.First("themeElements");
        defaultTextStyle = package.TryRoot(presentationPart)?.First("defaultTextStyle");
        LayoutShowsMasterShapes = layoutRoot?.BoolAttr("showMasterSp") ?? true;

        var scheme = new Dictionary<string, string>(StringComparer.Ordinal);
        if (themeElements?.First("clrScheme") is { } clrScheme)
        {
            foreach (var slot in clrScheme.Elements())
            {
                if (slot.First("srgbClr")?.Attr("val") is { } srgb) scheme[slot.Name.LocalName] = srgb.ToUpperInvariant();
                else if (slot.First("sysClr")?.Attr("lastClr") is { } sys) scheme[slot.Name.LocalName] = sys.ToUpperInvariant();
            }
        }
        var fontScheme = themeElements?.First("fontScheme");
        majorLatinFamily = fontScheme?.Descendant("majorFont", "latin")?.Attr("typeface");
        minorLatinFamily = fontScheme?.Descendant("minorFont", "latin")?.Attr("typeface");

        static Dictionary<string, string>? Mapping(XElement? element)
        {
            if (element is null) return null;
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration))
                result[attribute.Name.LocalName] = attribute.Value;
            return result.Count == 0 ? null : result;
        }
        var slotMap = Mapping(masterRoot?.First("clrMap")) ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var slideRoot = package.TryRoot(slidePart);
        foreach (var source in new[] { layoutRoot, slideRoot })
        {
            if (Mapping(source?.First("clrMapOvr")?.First("overrideClrMapping")) is { } overrideMap) slotMap = overrideMap;
        }
        Colors = new PptxColorResolver(scheme, slotMap);
    }

    public PptxColorResolver Colors { get; }
    public string? LayoutPart { get; }
    public string? MasterPart { get; }
    public bool LayoutShowsMasterShapes { get; }

    public string? ResolveFontFamily(string? typeface)
    {
        if (typeface is null) return null;
        if (!typeface.StartsWith('+')) return typeface;
        return typeface.StartsWith("+mj", StringComparison.Ordinal) ? majorLatinFamily : minorLatinFamily;
    }

    public PptxTransform? InheritedTransform(string? placeholderType, int? placeholderIndex)
    {
        if (MatchingPlaceholder(layoutRoot, placeholderType, placeholderIndex, useIdx: true) is { } layout
            && PptxSlideParser.ParseTransform(layout.First("spPr")?.First("xfrm")) is { } fromLayout)
            return fromLayout;
        if (MatchingPlaceholder(masterRoot, placeholderType, null, useIdx: false) is { } master
            && PptxSlideParser.ParseTransform(master.First("spPr")?.First("xfrm")) is { } fromMaster)
            return fromMaster;
        return null;
    }

    private static XElement? MatchingPlaceholder(XElement? root, string? type, int? idx, bool useIdx)
    {
        if (root?.Descendant("cSld", "spTree") is not { } spTree) return null;
        var candidates = new List<(XElement Sp, string Type, int? Idx)>();
        foreach (var sp in spTree.Children("sp"))
        {
            if (sp.Descendant("nvSpPr", "nvPr", "ph") is not { } ph) continue;
            candidates.Add((sp, NormalizedType(ph.Attr("type")), ph.IntAttr("idx")));
        }
        if (useIdx && idx is { } wantedIdx && candidates.FirstOrDefault(c => c.Idx == wantedIdx).Sp is { } byIdx) return byIdx;
        var wanted = NormalizedType(type);
        if (candidates.FirstOrDefault(c => c.Type == wanted).Sp is { } byType) return byType;
        if (!useIdx && wanted is "body" or "subTitle" && candidates.FirstOrDefault(c => c.Type == "body").Sp is { } body) return body;
        return null;
    }

    private static string NormalizedType(string? type) => type switch
    {
        "ctrTitle" => "title",
        null => "body",
        _ => type,
    };

    public PptxTextDefaults TextDefaults((string? Type, int? Idx)? placeholder, int level, List<string> warnings)
    {
        var clamped = Math.Clamp(level, 0, 4);
        var defaults = new PptxTextDefaults();
        if (placeholder is not { } ph)
        {
            defaults.FontFamily = minorLatinFamily;
            if (defaultTextStyle is not null) defaults = defaults.Merging(LevelDefaults(defaultTextStyle, clamped, warnings));
            return defaults;
        }

        var family = NormalizedType(ph.Type);
        defaults.FontFamily = family == "title" ? majorLatinFamily ?? minorLatinFamily : minorLatinFamily;
        var styleName = family switch
        {
            "title" => "titleStyle",
            "body" or "subTitle" => "bodyStyle",
            _ => "otherStyle",
        };
        if (masterRoot?.First("txStyles")?.First(styleName) is { } style)
            defaults = defaults.Merging(LevelDefaults(style, clamped, warnings));
        if (MatchingPlaceholder(layoutRoot, ph.Type, ph.Idx, useIdx: true)?.First("txBody")?.First("lstStyle") is { } lstStyle)
            defaults = defaults.Merging(LevelDefaults(lstStyle, clamped, warnings));
        return defaults;
    }

    private PptxTextDefaults LevelDefaults(XElement container, int level, List<string> warnings) =>
        DefaultsFromParagraphProperties(container.First($"lvl{level + 1}pPr"), warnings);

    public PptxTextDefaults DefaultsFromParagraphProperties(XElement? element, List<string> warnings)
    {
        if (element is null) return new PptxTextDefaults();
        var defaults = new PptxTextDefaults { Alignment = element.Attr("algn") };
        if (element.First("defRPr") is { } defRPr) defaults = defaults.Merging(RunProperties(defRPr, warnings));
        return defaults;
    }

    public PptxTextDefaults RunProperties(XElement element, List<string> warnings)
    {
        var defaults = new PptxTextDefaults
        {
            SizeHundredthsPt = element.DoubleAttr("sz"),
            Bold = element.BoolAttr("b"),
            Italic = element.BoolAttr("i"),
            TrackingHundredthsPt = element.DoubleAttr("spc"),
        };
        if (element.Attr("u") is { } underline) defaults.Underline = underline != "none";
        if (element.Attr("strike") is { } strike) defaults.Strike = strike != "noStrike";
        if (element.First("solidFill") is { } solidFill) defaults.ColorHex = Colors.Color(solidFill, warnings);
        defaults.FontFamily = ResolveFontFamily(element.First("latin")?.Attr("typeface"));
        return defaults;
    }

    public (XElement Element, string Part)? InheritedBackgroundElement()
    {
        if (layoutRoot is not null && LayoutPart is not null && layoutRoot.Descendant("cSld", "bg") is { } layoutBg)
            return (layoutBg, LayoutPart);
        if (masterRoot is not null && MasterPart is not null && masterRoot.Descendant("cSld", "bg") is { } masterBg)
            return (masterBg, MasterPart);
        return null;
    }

    public XElement? ThemeFillStyle(int index)
    {
        var fmtScheme = themeElements?.First("fmtScheme");
        var list = index >= 1001 ? fmtScheme?.First("bgFillStyleLst") : fmtScheme?.First("fillStyleLst");
        var fills = list?.Elements().ToList() ?? [];
        var position = index >= 1001 ? index - 1001 : index - 1;
        return position >= 0 && position < fills.Count ? fills[position] : null;
    }

    public XElement? DecorationSpTree(string? part) => package.TryRoot(part)?.Descendant("cSld", "spTree");
}
