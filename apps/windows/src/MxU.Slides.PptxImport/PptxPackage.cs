using System.Xml;
using System.Xml.Linq;

namespace MxU.Slides.PptxImport;

/// <summary>An extracted .pptx folder: part lookup, relationships and cached XML parts.</summary>
internal sealed class PptxPackage(string root)
{
    public const long MaxXmlPartBytes = 64L << 20;

    public sealed record Relationship(string Type, string Target, bool External);

    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Parse,
        XmlResolver = null,
        MaxCharactersFromEntities = 1 << 16,
    };

    private readonly Dictionary<string, XDocument> documents = new(StringComparer.Ordinal);
    private string? resolvedRoot;

    public string Root { get; } = root;

    public string PresentationPartPath()
    {
        var rels = RelationshipsInRelsPart("_rels/.rels");
        var rel = rels.Values.FirstOrDefault(r => r.Type.EndsWith("/officeDocument", StringComparison.Ordinal));
        if (rel is null || rel.External)
            throw PptxImportException.InvalidPackage("no presentation part in _rels/.rels — not a PowerPoint file");
        return ResolveTarget(rel.Target, "");
    }

    public Dictionary<string, Relationship> Relationships(string part)
    {
        var directory = DirectoryOf(part);
        var name = part[(part.LastIndexOf('/') + 1)..];
        var relsPart = directory.Length == 0 ? $"_rels/{name}.rels" : $"{directory}/_rels/{name}.rels";
        try
        {
            return RelationshipsInRelsPart(relsPart);
        }
        catch (Exception e) when (IsReadFailure(e))
        {
            return new(StringComparer.Ordinal);
        }
    }

    private Dictionary<string, Relationship> RelationshipsInRelsPart(string part)
    {
        var result = new Dictionary<string, Relationship>(StringComparer.Ordinal);
        if (Document(part).Root is not { } rootElement) return result;
        foreach (var rel in rootElement.Children("Relationship"))
        {
            if (rel.Attr("Id") is not { } id || rel.Attr("Type") is not { } type || rel.Attr("Target") is not { } target) continue;
            result[id] = new Relationship(type, target, rel.Attr("TargetMode") == "External");
        }
        return result;
    }

    public string ResolveTarget(string target, string relativeTo)
    {
        if (target.StartsWith('/')) return target.TrimStart('/');
        var components = DirectoryOf(relativeTo).Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var piece in target.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (piece)
            {
                case "..":
                    if (components.Count > 0 && components[^1] != "..")
                        components.RemoveAt(components.Count - 1);
                    else
                        // Preserve an escape attempt so FilePath rejects it.
                        components.Add("..");
                    break;
                case ".":
                    break;
                default:
                    components.Add(piece);
                    break;
            }
        }
        return string.Join('/', components);
    }

    private static string DirectoryOf(string part)
    {
        var trimmed = part.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? "" : trimmed[..slash];
    }

    /// <summary>The file for a part, refusing anything outside the package and any link, even one that stays inside.</summary>
    public string FilePath(string part)
    {
        if (resolvedRoot is null)
        {
            var info = new DirectoryInfo(Root);
            resolvedRoot = Path.TrimEndingDirectorySeparator(info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? info.FullName);
        }
        var directory = resolvedRoot;
        if (!PptxArchive.IsSafeRelativePath(part))
            throw PptxImportException.InvalidPackage("a package part points outside the presentation");
        var file = Path.GetFullPath(directory + Path.DirectorySeparatorChar + part.Replace('/', Path.DirectorySeparatorChar));
        if (!file.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw PptxImportException.InvalidPackage("a package part points outside the presentation");
        for (var component = file; component.Length > directory.Length; component = Path.GetDirectoryName(component)!)
        {
            if (new FileInfo(component).LinkTarget is not null)
                throw PptxImportException.InvalidPackage("symbolic links are not allowed in presentation parts");
        }
        return file;
    }

    public XDocument Document(string part)
    {
        if (documents.TryGetValue(part, out var cached)) return cached;
        var file = new FileInfo(FilePath(part));
        if (!file.Exists) throw PptxImportException.InvalidPackage($"{part} is missing from the presentation");
        if (file.Length > MaxXmlPartBytes) throw PptxImportException.InvalidPackage($"{part} is too large to read");
        using var stream = file.OpenRead();
        using var reader = XmlReader.Create(stream, ReaderSettings);
        while (reader.Read() && reader.NodeType != XmlNodeType.Element)
        {
            if (reader.NodeType == XmlNodeType.DocumentType)
                throw PptxImportException.InvalidPackage("DTD declarations are not allowed in presentation XML");
        }
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        documents[part] = document;
        return document;
    }

    /// <summary>The part's root element, or null when it is missing or unreadable.</summary>
    public XElement? TryRoot(string? part)
    {
        if (part is null) return null;
        try
        {
            return Document(part).Root;
        }
        catch (Exception e) when (IsReadFailure(e))
        {
            return null;
        }
    }

    public string? TryFilePath(string part)
    {
        try
        {
            return FilePath(part);
        }
        catch (Exception e) when (IsReadFailure(e))
        {
            return null;
        }
    }

    public static bool IsReadFailure(Exception e) =>
        e is PptxImportException or IOException or UnauthorizedAccessException or XmlException
            or InvalidOperationException or ArgumentException or NotSupportedException;
}
