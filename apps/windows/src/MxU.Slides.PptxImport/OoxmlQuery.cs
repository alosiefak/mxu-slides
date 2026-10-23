using System.Globalization;
using System.Xml.Linq;

namespace MxU.Slides.PptxImport;

/// <summary>Namespace-agnostic lookups over OOXML parts, matching elements and attributes by local name.</summary>
internal static class OoxmlQuery
{
    public static IEnumerable<XElement> Children(this XElement element, string local) =>
        element.Elements().Where(e => e.Name.LocalName == local);

    public static XElement? First(this XElement element, string local) =>
        element.Elements().FirstOrDefault(e => e.Name.LocalName == local);

    public static XElement? Descendant(this XElement element, params string[] path)
    {
        XElement? node = element;
        foreach (var step in path) node = node?.First(step);
        return node;
    }

    public static string? Attr(this XElement element, string localName) =>
        element.Attributes().FirstOrDefault(a => !a.IsNamespaceDeclaration && a.Name.LocalName == localName)?.Value;

    public static string? RelationshipAttr(this XElement element, string localName) =>
        element.Attributes().FirstOrDefault(a =>
            !a.IsNamespaceDeclaration && a.Name.LocalName == localName
            && a.Name.NamespaceName.Contains("/relationships", StringComparison.Ordinal))?.Value;

    public static double? EmuAttr(this XElement element, string localName) => ParseDouble(element.Attr(localName));

    public static double? DoubleAttr(this XElement element, string localName) => ParseDouble(element.Attr(localName));

    public static int? IntAttr(this XElement element, string localName) =>
        int.TryParse(element.Attr(localName), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : null;

    public static bool? BoolAttr(this XElement element, string localName) => element.Attr(localName) switch
    {
        "1" or "true" => true,
        "0" or "false" => false,
        _ => null,
    };

    public static double? ParseDouble(string? text) =>
        double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
            CultureInfo.InvariantCulture, out var value) ? value : null;

    public static void AppendUnique(this List<string> warnings, string message)
    {
        if (!warnings.Contains(message)) warnings.Add(message);
    }
}
