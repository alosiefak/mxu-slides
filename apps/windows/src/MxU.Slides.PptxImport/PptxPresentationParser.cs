using System.Xml.Linq;
using MxU.Slides.Core.Library;

namespace MxU.Slides.PptxImport;

internal static class PptxPresentationParser
{
    public sealed record Result(string PartPath, double SlideWidthEmu, double SlideHeightEmu, List<string> SlidePartPaths)
    {
        public List<PptxSection> Sections { get; init; } = [];
    }

    public static Result Parse(PptxPackage package, List<string> warnings)
    {
        var part = package.PresentationPartPath();
        var root = package.Document(part).Root
            ?? throw PptxImportException.InvalidPackage($"{part} has no root element");

        var width = 12_192_000.0;
        var height = 6_858_000.0;
        if (root.First("sldSz") is { } size && size.EmuAttr("cx") is { } cx && size.EmuAttr("cy") is { } cy && cx > 0 && cy > 0)
        {
            width = cx;
            height = cy;
        }
        else
        {
            warnings.Add("the deck declared no slide size — assuming 16:9");
        }

        var rels = package.Relationships(part);
        var slideParts = new List<string>();
        var indexBySlideNumber = new Dictionary<string, int>(StringComparer.Ordinal);
        if (root.First("sldIdLst") is { } list)
        {
            foreach (var slideId in list.Children("sldId"))
            {
                if (slideId.RelationshipAttr("id") is not { } relId || !rels.TryGetValue(relId, out var rel) || rel.External) continue;
                if (slideId.Attribute("id")?.Value is { } number) indexBySlideNumber[number] = slideParts.Count;
                slideParts.Add(package.ResolveTarget(rel.Target, part));
            }
        }

        return new Result(part, width, height, slideParts) { Sections = Sections(root, indexBySlideNumber) };
    }

    private static List<PptxSection> Sections(XElement root, Dictionary<string, int> indexBySlideNumber)
    {
        if (root.First("extLst") is not { } extLst) return [];
        var sectionList = extLst.Children("ext").Select(ext => ext.First("sectionLst")).FirstOrDefault(found => found is not null);
        if (sectionList is null) return [];
        var sections = new List<PptxSection>();
        foreach (var section in sectionList.Children("section"))
        {
            var guid = (section.Attr("id") ?? LibraryStore.NewId()).Trim('{', '}').ToLowerInvariant();
            var result = new PptxSection(guid, section.Attr("name") ?? "");
            if (section.First("sldIdLst") is { } idList)
            {
                foreach (var slideId in idList.Children("sldId"))
                {
                    if (slideId.Attr("id") is { } number && indexBySlideNumber.TryGetValue(number, out var index))
                        result.SlideIndexes.Add(index);
                }
            }
            sections.Add(result);
        }
        return sections;
    }

    public static List<PptxEmbeddedFont> EmbeddedFonts(PptxPackage package)
    {
        string part;
        try
        {
            part = package.PresentationPartPath();
        }
        catch (Exception e) when (PptxPackage.IsReadFailure(e))
        {
            return [];
        }
        if (package.TryRoot(part)?.First("embeddedFontLst") is not { } fontList) return [];
        var rels = package.Relationships(part);
        var fonts = new List<PptxEmbeddedFont>();
        foreach (var embedded in fontList.Children("embeddedFont"))
        {
            if (embedded.First("font")?.Attr("typeface") is not { } typeface) continue;
            foreach (var variant in new[] { "regular", "bold", "italic", "boldItalic" })
            {
                if (embedded.First(variant)?.RelationshipAttr("id") is not { } relId
                    || !rels.TryGetValue(relId, out var rel) || rel.External)
                    continue;
                if (package.TryFilePath(package.ResolveTarget(rel.Target, part)) is not { } file) continue;
                fonts.Add(new PptxEmbeddedFont(typeface, variant, file));
            }
        }
        return fonts;
    }
}
