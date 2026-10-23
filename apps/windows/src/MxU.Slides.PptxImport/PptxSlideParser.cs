using System.Globalization;
using System.Xml.Linq;
using MxU.Slides.Core.Model;
using Rels = System.Collections.Generic.Dictionary<string, MxU.Slides.PptxImport.PptxPackage.Relationship>;

namespace MxU.Slides.PptxImport;

/// <summary>Reads one slide part (plus the master and layout decorations it shows) into a <see cref="PptxSlide"/>.</summary>
internal static class PptxSlideParser
{
    private sealed record MediaContext(PptxPackage Package, string PartPath, Rels Rels);

    public static PptxSlide Parse(string partPath, PptxPackage package, List<string> warnings, string? presentationPart = null)
    {
        var root = package.Document(partPath).Root
            ?? throw PptxImportException.InvalidPackage($"{partPath} has no root element");
        var inheritance = new PptxInheritance(partPath, presentationPart, package);
        var rels = package.Relationships(partPath);
        var slide = new PptxSlide();
        var cSld = root.First("cSld");
        slide.Name = cSld?.Attr("name");

        if (cSld?.First("bg") is { } bg)
            slide.BackgroundFill = ParseBackground(bg, partPath, package, inheritance, warnings);
        else if (inheritance.InheritedBackgroundElement() is { } inherited)
            slide.BackgroundFill = ParseBackground(inherited.Element, inherited.Part, package, inheritance, warnings);

        if (root.BoolAttr("showMasterSp") ?? true)
        {
            if (inheritance.LayoutShowsMasterShapes && inheritance.MasterPart is { } masterPart)
                slide.Shapes.AddRange(ParseDecorations(masterPart, package, inheritance, warnings));
            if (inheritance.LayoutPart is { } layoutPart)
                slide.Shapes.AddRange(ParseDecorations(layoutPart, package, inheritance, warnings));
        }

        if (cSld?.First("spTree") is { } spTree)
            slide.Shapes.AddRange(ParseShapeTreeChildren(spTree, package, partPath, rels, inheritance, includePlaceholders: true, warnings));

        slide.Transition = ParseTransition(root);
        slide.AnimationSteps = ParseTiming(root);
        slide.Notes = ParseNotes(partPath, package, rels, inheritance, warnings);

        foreach (var shape in slide.Shapes.Where(s => s.UsesBackgroundFill && s.Fill is null))
            shape.Fill = slide.BackgroundFill;
        return slide;
    }

    private static List<PptxShape> ParseDecorations(string part, PptxPackage package, PptxInheritance inheritance, List<string> warnings)
    {
        if (inheritance.DecorationSpTree(part) is not { } spTree) return [];
        return ParseShapeTreeChildren(spTree, package, part, package.Relationships(part), inheritance, includePlaceholders: false, warnings);
    }

    private static List<PptxShape> ParseShapeTreeChildren(
        XElement spTree, PptxPackage package, string partPath, Rels rels,
        PptxInheritance inheritance, bool includePlaceholders, List<string> warnings)
    {
        var shapes = new List<PptxShape>();
        foreach (var child in spTree.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "sp" or "cxnSp":
                    if (!includePlaceholders && child.Descendant("nvSpPr", "nvPr", "ph") is not null) continue;
                    if (ParseSp(child, package, partPath, rels, inheritance, warnings) is { } shape) shapes.Add(shape);
                    break;
                case "pic":
                    if (!includePlaceholders && child.Descendant("nvPicPr", "nvPr", "ph") is not null) continue;
                    if (ParsePic(child, package, partPath, rels, inheritance, warnings) is { } picture) shapes.Add(picture);
                    break;
                case "grpSp":
                    shapes.AddRange(ParseGroup(child, package, partPath, rels, inheritance, includePlaceholders, warnings));
                    break;
                case "graphicFrame":
                    shapes.AddRange(ParseGraphicFrame(child, package, partPath, rels, inheritance, warnings));
                    break;
            }
        }
        return shapes;
    }

    private static List<PptxShape> ParseGroup(
        XElement grpSp, PptxPackage package, string partPath, Rels rels,
        PptxInheritance inheritance, bool includePlaceholders, List<string> warnings)
    {
        var xfrm = grpSp.First("grpSpPr")?.First("xfrm");
        if (ParseTransform(xfrm) is not { } group)
        {
            warnings.AppendUnique("a shape group has no position — its shapes were dropped");
            return [];
        }
        var chOff = xfrm?.First("chOff");
        var chExt = xfrm?.First("chExt");
        var childSpace = (
            X: chOff?.EmuAttr("x") ?? 0, Y: chOff?.EmuAttr("y") ?? 0,
            Width: chExt?.EmuAttr("cx") ?? group.ExtXEmu, Height: chExt?.EmuAttr("cy") ?? group.ExtYEmu);
        var children = ParseShapeTreeChildren(grpSp, package, partPath, rels, inheritance, includePlaceholders, warnings);

        var groupFill = grpSp.First("grpSpPr") is { } grpSpPr
            ? ParseFill(grpSpPr, "a shape group", package, partPath, rels, inheritance, warnings)
            : null;
        foreach (var shape in children)
        {
            shape.Transform = Composed(shape.Transform, group, childSpace, warnings);
            if (shape.UsesGroupFill && shape.Fill is null && groupFill is not null)
            {
                shape.Fill = groupFill;
                shape.UsesGroupFill = false;
            }
        }
        return children;
    }

    private static PptxTransform Composed(
        PptxTransform child, PptxTransform group,
        (double X, double Y, double Width, double Height) childSpace, List<string> warnings)
    {
        var scaleX = childSpace.Width > 0 ? group.ExtXEmu / childSpace.Width : 1;
        var scaleY = childSpace.Height > 0 ? group.ExtYEmu / childSpace.Height : 1;
        if (Math.Abs(scaleX - scaleY) > 0.0001 && child.Rotation60k != 0)
            warnings.AppendUnique("a rotated shape inside a stretched group may sit slightly off");
        var result = child;
        result.OffXEmu = group.OffXEmu + (child.OffXEmu - childSpace.X) * scaleX;
        result.OffYEmu = group.OffYEmu + (child.OffYEmu - childSpace.Y) * scaleY;
        result.ExtXEmu = child.ExtXEmu * scaleX;
        result.ExtYEmu = child.ExtYEmu * scaleY;

        var groupCenterX = group.OffXEmu + group.ExtXEmu / 2;
        var groupCenterY = group.OffYEmu + group.ExtYEmu / 2;
        var centerX = result.OffXEmu + result.ExtXEmu / 2;
        var centerY = result.OffYEmu + result.ExtYEmu / 2;
        if (group.FlipH)
        {
            centerX = 2 * groupCenterX - centerX;
            result.FlipH = !result.FlipH;
            result.Rotation60k = -result.Rotation60k;
        }
        if (group.FlipV)
        {
            centerY = 2 * groupCenterY - centerY;
            result.FlipV = !result.FlipV;
            result.Rotation60k = -result.Rotation60k;
        }
        if (group.Rotation60k != 0)
        {
            var angle = group.Rotation60k / 60000 * Math.PI / 180;
            var dx = centerX - groupCenterX;
            var dy = centerY - groupCenterY;
            centerX = groupCenterX + dx * Math.Cos(angle) - dy * Math.Sin(angle);
            centerY = groupCenterY + dx * Math.Sin(angle) + dy * Math.Cos(angle);
            result.Rotation60k += group.Rotation60k;
        }
        result.OffXEmu = centerX - result.ExtXEmu / 2;
        result.OffYEmu = centerY - result.ExtYEmu / 2;
        return result;
    }

    private static List<PptxShape> ParseGraphicFrame(
        XElement frame, PptxPackage package, string partPath, Rels rels,
        PptxInheritance inheritance, List<string> warnings)
    {
        var name = frame.Descendant("nvGraphicFramePr", "cNvPr")?.Attr("name") ?? "content";
        var transform = ParseTransform(frame.First("xfrm"));
        if (frame.Descendant("graphic", "graphicData") is not { } data) return [];
        if (data.First("tbl") is { } table)
        {
            if (transform is not { } position)
            {
                warnings.AppendUnique($"table \"{name}\" has no position and was skipped");
                return [];
            }
            return [TableShape(table, name, position, inheritance, warnings)];
        }
        if (data.First("chart") is not null)
        {
            warnings.AppendUnique($"chart \"{name}\" was skipped — charts are not imported yet");
            return [];
        }
        if (data.First("relIds") is not null)
            return SmartArtShapes(data, name, transform, package, partPath, rels, inheritance, warnings);
        warnings.AppendUnique($"\"{name}\" embeds an unsupported content type and was skipped");
        return [];
    }

    private static PptxShape TableShape(XElement table, string? name, PptxTransform transform, PptxInheritance inheritance, List<string> warnings)
    {
        warnings.AppendUnique("tables are imported as plain text — cells joined by tabs");
        PptxRun? style = null;
        var rows = new List<string>();
        foreach (var row in table.Children("tr"))
        {
            var cells = new List<string>();
            foreach (var cell in row.Children("tc"))
            {
                if (cell.First("txBody") is not { } txBody)
                {
                    cells.Add("");
                    continue;
                }
                var body = ParseTextBody(txBody, null, inheritance, warnings);
                style ??= body.Lines.SelectMany(l => l.Runs).FirstOrDefault(r => r.Text.Length > 0);
                cells.Add(body.PlainText.Replace("\n", " "));
            }
            rows.Add(string.Join("\t", cells));
        }
        var textBody = new PptxTextBody
        {
            Paragraphs = rows.Select(row => new PptxParagraph { Runs = [(style ?? new PptxRun()) with { Text = row }] }).ToList(),
        };
        return new PptxShape { Kind = PptxShapeKind.Shape, Name = name, Transform = transform, TextBody = textBody };
    }

    private static List<PptxShape> SmartArtShapes(
        XElement data, string frameName, PptxTransform? transform,
        PptxPackage package, string partPath, Rels rels,
        PptxInheritance inheritance, List<string> warnings)
    {
        if (transform is not { } frame)
        {
            warnings.AppendUnique($"SmartArt \"{frameName}\" has no position and was skipped");
            return [];
        }
        string? drawingPart = null;

        if (data.First("relIds")?.RelationshipAttr("dm") is { } dmRel && rels.TryGetValue(dmRel, out var rel) && !rel.External)
        {
            var dataPart = package.ResolveTarget(rel.Target, partPath);
            if (package.TryRoot(dataPart)?.Descendants().FirstOrDefault(e => e.Name.LocalName == "dataModelExt") is { } ext
                && ext.Attr("relId") is { } relId
                && package.Relationships(dataPart).TryGetValue(relId, out var drawingRel) && !drawingRel.External)
                drawingPart = package.ResolveTarget(drawingRel.Target, dataPart);
        }

        if (drawingPart is null
            && rels.Values.FirstOrDefault(r => r.Type.Contains("diagramDrawing", StringComparison.Ordinal) && !r.External) is { } fallback)
            drawingPart = package.ResolveTarget(fallback.Target, partPath);
        if (drawingPart is null || package.TryRoot(drawingPart)?.First("spTree") is not { } spTree)
        {
            warnings.AppendUnique($"SmartArt \"{frameName}\" carries no pre-laid-out drawing and was skipped");
            return [];
        }
        var drawingRels = package.Relationships(drawingPart);
        var shapes = new List<PptxShape>();
        foreach (var sp in spTree.Children("sp"))
        {
            if (ParseSp(sp, package, drawingPart, drawingRels, inheritance, warnings) is not { } shape) continue;
            shape.Transform = shape.Transform with
            {
                OffXEmu = shape.Transform.OffXEmu + frame.OffXEmu,
                OffYEmu = shape.Transform.OffYEmu + frame.OffYEmu,
            };
            shapes.Add(shape);
        }
        return shapes;
    }

    private static PptxShape? ParseSp(
        XElement sp, PptxPackage package, string partPath, Rels rels,
        PptxInheritance inheritance, List<string> warnings)
    {
        var cNvPr = sp.Descendant("nvSpPr", "cNvPr") ?? sp.Descendant("nvCxnSpPr", "cNvPr");
        var name = sp.Descendant("nvSpPr", "cNvPr")?.Attr("name") ?? sp.Descendant("nvCxnSpPr", "cNvPr")?.Attr("name");
        var spPr = sp.First("spPr");
        (string? Type, int? Idx)? placeholder = sp.Descendant("nvSpPr", "nvPr", "ph") is { } ph ? (ph.Attr("type"), ph.IntAttr("idx")) : null;

        var transform = ParseTransform(spPr?.First("xfrm"));
        if (transform is null && placeholder is { } inherited)
            transform = inheritance.InheritedTransform(inherited.Type, inherited.Idx);
        if (transform is not { } position)
        {
            warnings.AppendUnique($"\"{name ?? "a shape"}\" has no position on the slide or its layout — dropped");
            return null;
        }
        var shape = new PptxShape
        {
            Kind = PptxShapeKind.Shape,
            Name = name,
            Transform = position,
            ShapeId = cNvPr?.IntAttr("id"),
            IsPlaceholder = placeholder is not null,
            PlaceholderType = placeholder?.Type,
            PlaceholderIndex = placeholder?.Idx,
            UsesBackgroundFill = sp.BoolAttr("useBgFill") ?? false,
        };

        ParseGeometry(shape, spPr, position, warnings);
        if (spPr is not null)
        {
            shape.Fill = ParseFill(spPr, $"\"{name ?? "a shape"}\"", package, partPath, rels, inheritance, warnings);
            shape.Stroke = ParseLine(spPr.First("ln"), inheritance, warnings);
            if (spPr.First("grpFill") is not null) shape.UsesGroupFill = true;
        }

        if (shape.Fill is null && !shape.UsesGroupFill && !shape.UsesBackgroundFill
            && sp.First("style")?.First("fillRef") is { } fillRef
            && fillRef.IntAttr("idx") is { } index && index > 0)
        {
            var phClr = Prefix6(inheritance.Colors.Color(fillRef, warnings));
            if (inheritance.ThemeFillStyle(index) is { } themeStyle)
                shape.Fill = Fill(themeStyle, $"\"{name ?? "a shape"}\"", phClr, null, inheritance, warnings);
        }
        if (sp.First("txBody") is { } txBody)
            shape.TextBody = ParseTextBody(txBody, placeholder, inheritance, warnings);
        return shape;
    }

    private static string? Prefix6(string? hex) => hex is null ? null : hex[..Math.Min(6, hex.Length)];

    private static PptxShape? ParsePic(
        XElement pic, PptxPackage package, string partPath, Rels rels,
        PptxInheritance inheritance, List<string> warnings)
    {
        var name = pic.Descendant("nvPicPr", "cNvPr")?.Attr("name");
        var spPr = pic.First("spPr");
        if (ParseTransform(spPr?.First("xfrm")) is not { } transform)
        {
            warnings.AppendUnique($"\"{name ?? "a picture"}\" has no position on the slide or its layout — dropped");
            return null;
        }
        var shape = new PptxShape
        {
            Kind = PptxShapeKind.Picture,
            Name = name,
            Transform = transform,
            ShapeId = pic.Descendant("nvPicPr", "cNvPr")?.IntAttr("id"),
        };

        ParseGeometry(shape, spPr, transform, warnings);
        if (spPr is not null) shape.Stroke = ParseLine(spPr.First("ln"), inheritance, warnings);

        var nvPr = pic.Descendant("nvPicPr", "nvPr");
        if (nvPr?.First("videoFile") is { } video)
        {
            shape.MediaKind = PptxMediaKind.Video;
            shape.MediaRelationshipId = video.RelationshipAttr("link") ?? video.Attr("link");
        }
        else if (nvPr?.First("audioFile") is { } audio)
        {
            shape.MediaKind = PptxMediaKind.Audio;
            shape.MediaRelationshipId = audio.RelationshipAttr("link") ?? audio.Attr("link");
        }
        if (pic.First("blipFill") is { } blipFill)
        {
            var blip = blipFill.First("blip");
            if (shape.MediaKind == PptxMediaKind.Image)
                shape.MediaRelationshipId = blip is null ? null : blip.RelationshipAttr("embed") ?? blip.Attr("embed");

            if (blip?.First("alphaModFix")?.DoubleAttr("amt") is { } amount && amount < 100_000)
                shape.BlipAlpha = amount / 100_000;
            if (blipFill.First("srcRect") is { } rect)
            {
                var sourceRect = new PptxSourceRect(
                    rect.DoubleAttr("l") ?? 0, rect.DoubleAttr("t") ?? 0,
                    rect.DoubleAttr("r") ?? 0, rect.DoubleAttr("b") ?? 0);
                if (!sourceRect.IsFullFrame) shape.SourceRect = sourceRect;
            }
        }
        shape.MediaPath = MediaPath(
            shape.MediaRelationshipId, $"\"{name ?? "a picture"}\"", package, partPath, rels,
            externalAsPath: shape.MediaKind != PptxMediaKind.Image, warnings);
        return shape;
    }

    private static string? CustomPath(XElement custGeom, double extXEmu, double extYEmu, List<string> warnings)
    {
        if (custGeom.First("pathLst") is not { } pathList) return null;
        var segments = new List<string>();
        foreach (var path in pathList.Children("path"))
        {
            var width = path.DoubleAttr("w") ?? extXEmu;
            var height = path.DoubleAttr("h") ?? extYEmu;
            if (width <= 0 || height <= 0) continue;
            if (PathCommands(path, width, height, warnings) is { } segment)
                segments.Add(segment);
            else
                warnings.AppendUnique("a custom shape uses formula-driven geometry — that outline was skipped");
        }
        return segments.Count == 0 ? null : string.Join(" ", segments);
    }

    private static string? PathCommands(XElement path, double width, double height, List<string> warnings)
    {
        (double X, double Y)? Point(XElement? pt) =>
            pt?.DoubleAttr("x") is { } x && pt.DoubleAttr("y") is { } y ? (x / width, y / height) : null;
        static string C((double X, double Y) p) => PptxPresetGeometry.Coordinate(p);

        var commands = new List<string>();
        (double X, double Y)? current = null;
        (double X, double Y)? subpathStart = null;
        foreach (var command in path.Elements())
        {
            switch (command.Name.LocalName)
            {
                case "moveTo":
                {
                    if (Point(command.First("pt")) is not { } p) return null;
                    commands.Add($"M {C(p)}");
                    current = p;
                    subpathStart = p;
                    break;
                }
                case "lnTo":
                {
                    if (current is not { } from || Point(command.First("pt")) is not { } p) return null;
                    commands.Add($"C {C(from)} {C(p)} {C(p)}");
                    current = p;
                    break;
                }
                case "cubicBezTo":
                {
                    var pts = command.Children("pt").ToList();
                    if (pts.Count != 3 || Point(pts[0]) is not { } c1 || Point(pts[1]) is not { } c2 || Point(pts[2]) is not { } end) return null;
                    commands.Add($"C {C(c1)} {C(c2)} {C(end)}");
                    current = end;
                    break;
                }
                case "quadBezTo":
                {
                    var pts = command.Children("pt").ToList();
                    if (current is not { } from || pts.Count != 2 || Point(pts[0]) is not { } q || Point(pts[1]) is not { } end) return null;
                    var c1 = (from.X + 2.0 / 3 * (q.X - from.X), from.Y + 2.0 / 3 * (q.Y - from.Y));
                    var c2 = (end.X + 2.0 / 3 * (q.X - end.X), end.Y + 2.0 / 3 * (q.Y - end.Y));
                    commands.Add($"C {C(c1)} {C(c2)} {C(end)}");
                    current = end;
                    break;
                }
                case "arcTo":
                {
                    if (current is not { } from
                        || command.DoubleAttr("wR") is not { } radiusW || command.DoubleAttr("hR") is not { } radiusH
                        || command.DoubleAttr("stAng") is not { } startAngle || command.DoubleAttr("swAng") is not { } sweep)
                        return null;
                    warnings.AppendUnique("curved arc segments in custom shapes are approximated as straight edges");
                    var rx = radiusW / width;
                    var ry = radiusH / height;
                    var start = startAngle / 60000 * Math.PI / 180;
                    var stop = (startAngle + sweep) / 60000 * Math.PI / 180;
                    var center = (X: from.X - rx * Math.Cos(start), Y: from.Y - ry * Math.Sin(start));
                    var p = (center.X + rx * Math.Cos(stop), center.Y + ry * Math.Sin(stop));
                    commands.Add($"C {C(from)} {C(p)} {C(p)}");
                    current = p;
                    break;
                }
                case "close":
                    if (current is { } last && subpathStart is { } first)
                    {
                        commands.Add($"C {C(last)} {C(first)} {C(first)}");
                        commands.Add("Z");
                        current = first;
                    }
                    break;
            }
        }
        return commands.Count >= 2 ? string.Join(" ", commands) : null;
    }

    private static void ParseGeometry(PptxShape shape, XElement? spPr, PptxTransform transform, List<string> warnings)
    {
        if (spPr?.First("prstGeom") is { } geometry)
        {
            shape.PresetGeometry = geometry.Attr("prst");
            if (geometry.Descendant("avLst", "gd")?.Attr("fmla") is { } fmla && fmla.StartsWith("val ", StringComparison.Ordinal))
                shape.RoundRectAdjustment = OoxmlQuery.ParseDouble(fmla[4..]);
        }
        if (spPr?.First("custGeom") is { } custom)
            shape.CustomPathData = CustomPath(custom, transform.ExtXEmu, transform.ExtYEmu, warnings);
    }

    public static PptxTransform? ParseTransform(XElement? xfrm)
    {
        if (xfrm?.First("off") is not { } off || xfrm.First("ext") is not { } ext
            || off.EmuAttr("x") is not { } x || off.EmuAttr("y") is not { } y
            || ext.EmuAttr("cx") is not { } cx || ext.EmuAttr("cy") is not { } cy)
            return null;
        return new PptxTransform(x, y, cx, cy,
            Rotation60k: xfrm.DoubleAttr("rot") ?? 0,
            FlipH: xfrm.BoolAttr("flipH") ?? false,
            FlipV: xfrm.BoolAttr("flipV") ?? false);
    }

    private static readonly string[] FillKinds = ["noFill", "solidFill", "gradFill", "blipFill", "pattFill", "grpFill"];

    private static PptxFill? ParseFill(
        XElement parent, string context, PptxPackage package, string partPath, Rels rels,
        PptxInheritance inheritance, List<string> warnings, string? phClr = null)
    {
        if (parent.Elements().FirstOrDefault(e => FillKinds.Contains(e.Name.LocalName)) is not { } element) return null;
        return Fill(element, context, phClr, new MediaContext(package, partPath, rels), inheritance, warnings);
    }

    private static PptxFill? Fill(
        XElement element, string context, string? phClr, MediaContext? mediaContext,
        PptxInheritance inheritance, List<string> warnings)
    {
        switch (element.Name.LocalName)
        {
            case "noFill":
                return new PptxFill.NoFill();
            case "solidFill":
                return inheritance.Colors.Color(element, warnings, phClr) is { } hex ? new PptxFill.Solid(hex) : null;
            case "gradFill":
                return Gradient(element, phClr, inheritance, warnings) is { } gradient ? new PptxFill.Gradient(gradient) : null;
            case "blipFill":
            {
                if (mediaContext is null)
                {
                    warnings.AppendUnique("theme picture backgrounds are not imported yet");
                    return null;
                }
                var blip = element.First("blip");
                var embed = blip is null ? null : blip.RelationshipAttr("embed") ?? blip.Attr("embed");
                if (MediaPath(embed, context, mediaContext.Package, mediaContext.PartPath, mediaContext.Rels, externalAsPath: false, warnings) is not { } path)
                    return null;

                var tile = element.First("tile");
                var alpha = blip?.First("alphaModFix")?.DoubleAttr("amt") / 100_000 ?? 1;
                if (tile is not null || alpha < 1)
                {
                    return new PptxFill.BlipTiled(
                        path, alpha,
                        ScaleX: (tile?.DoubleAttr("sx") ?? 100_000) / 100_000,
                        ScaleY: (tile?.DoubleAttr("sy") ?? 100_000) / 100_000,
                        Tiled: tile is not null);
                }
                return new PptxFill.Blip(path);
            }
            case "pattFill":
            {
                var foreground = inheritance.Colors.Color(element.First("fgClr"), warnings, phClr);
                var background = inheritance.Colors.Color(element.First("bgClr"), warnings, phClr);
                if (foreground is null) return background is null ? null : new PptxFill.Solid(background);
                if (background is null) return new PptxFill.Solid(foreground);
                return new PptxFill.Pattern(element.Attr("prst") ?? "", foreground, background);
            }
            default:
                return null;
        }
    }

    public static string Blend(string foreground, string background, double coverage)
    {
        static double[] Bytes(string hex)
        {
            var padded = hex.Length == 6 ? hex + "FF" : hex;
            if (padded.Length < 8) return [];
            var values = new List<double>();
            for (var offset = 0; offset < 8; offset += 2)
            {
                if (byte.TryParse(padded.AsSpan(offset, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
                    values.Add(value);
            }
            return [.. values];
        }
        var fg = Bytes(foreground);
        var bg = Bytes(background);
        if (fg.Length != 4 || bg.Length != 4) return foreground;
        return string.Concat(fg.Zip(bg, (f, b) => f * coverage + b * (1 - coverage))
            .Select(v => ((byte)Math.Clamp(Math.Round(v, MidpointRounding.AwayFromZero), 0, 255)).ToString("X2", CultureInfo.InvariantCulture)));
    }

    private static PptxGradient? Gradient(XElement element, string? phClr, PptxInheritance inheritance, List<string> warnings)
    {
        var stops = new List<PptxGradientStop>();
        if (element.First("gsLst") is { } gsLst)
        {
            foreach (var gs in gsLst.Children("gs"))
            {
                if (inheritance.Colors.Color(gs, warnings, phClr) is not { } color) continue;
                stops.Add(new PptxGradientStop(gs.DoubleAttr("pos") ?? 0, color));
            }
        }
        if (stops.Count < 2)
        {
            warnings.AppendUnique("a gradient fill had fewer than two resolvable stops and was skipped");
            return null;
        }
        stops = [.. stops.OrderBy(s => s.Position)];
        double? angle = null;
        if (element.First("lin") is { } lin)
            angle = lin.DoubleAttr("ang") ?? 0;
        else
            warnings.AppendUnique("radial gradient approximated as linear");
        return new PptxGradient(angle, stops);
    }

    private static PptxStroke? ParseLine(XElement? ln, PptxInheritance inheritance, List<string> warnings)
    {
        if (ln is null) return null;
        var width = ln.EmuAttr("w") ?? 9525;
        StrokeDashKind? dash = ln.First("prstDash")?.Attr("val") switch
        {
            null or "solid" => null,
            "dot" or "sysDot" => StrokeDashKind.Dotted,
            _ => StrokeDashKind.Dashed,
        };
        if (ln.First("solidFill") is { } solid && inheritance.Colors.Color(solid, warnings) is { } hex)
            return new PptxStroke(hex, width, dash);

        if (ln.First("gradFill")?.First("gsLst")?.Children("gs").FirstOrDefault() is { } firstStop
            && inheritance.Colors.Color(firstStop, warnings) is { } stopHex)
        {
            warnings.AppendUnique("gradient outlines are approximated as their first color");
            return new PptxStroke(stopHex, width, dash);
        }
        return null;
    }

    private static string? MediaPath(
        string? relationshipId, string context, PptxPackage package, string partPath, Rels rels,
        bool externalAsPath, List<string> warnings)
    {
        if (relationshipId is null) return null;
        if (!rels.TryGetValue(relationshipId, out var rel))
        {
            warnings.AppendUnique($"{context} references a missing relationship and was skipped");
            return null;
        }
        if (rel.External)
        {
            if (externalAsPath)
            {
                if (Uri.TryCreate(rel.Target, UriKind.Absolute, out var url) && url.IsFile) return url.LocalPath;
                if (rel.Target.StartsWith('/')) return Uri.UnescapeDataString(rel.Target);
                if (Path.IsPathFullyQualified(rel.Target)) return rel.Target;
                warnings.AppendUnique($"{context} links media at an unresolvable location and was skipped");
                return null;
            }
            warnings.AppendUnique($"{context} links media outside the file — linked media is not imported yet");
            return null;
        }
        if (package.TryFilePath(package.ResolveTarget(rel.Target, partPath)) is not { } file)
        {
            warnings.AppendUnique($"{context} points outside the presentation and was skipped");
            return null;
        }
        return file;
    }

    private static PptxFill? ParseBackground(XElement bg, string owningPart, PptxPackage package, PptxInheritance inheritance, List<string> warnings)
    {
        if (bg.First("bgPr") is { } bgPr)
            return ParseFill(bgPr, "the slide background", package, owningPart, package.Relationships(owningPart), inheritance, warnings);
        if (bg.First("bgRef") is { } bgRef)
        {
            var index = bgRef.IntAttr("idx") ?? 0;
            if (index <= 0) return null;

            var phClr = Prefix6(inheritance.Colors.Color(bgRef, warnings));
            if (inheritance.ThemeFillStyle(index) is not { } style)
            {
                warnings.AppendUnique("the theme has no background style for this slide — background skipped");
                return null;
            }
            return Fill(style, "the slide background", phClr, null, inheritance, warnings);
        }
        return null;
    }

    private static PptxTextBody ParseTextBody(
        XElement txBody, (string? Type, int? Idx)? placeholder, PptxInheritance inheritance, List<string> warnings)
    {
        var body = new PptxTextBody();
        if (txBody.First("bodyPr") is { } bodyPr)
        {
            body.Anchor = bodyPr.Attr("anchor");
            body.Wrap = bodyPr.Attr("wrap");
            body.TopInsetEmu = bodyPr.EmuAttr("tIns");
            body.LeftInsetEmu = bodyPr.EmuAttr("lIns");
            body.BottomInsetEmu = bodyPr.EmuAttr("bIns");
            body.RightInsetEmu = bodyPr.EmuAttr("rIns");
            if (bodyPr.First("normAutofit") is { } autofit)
            {
                body.HasNormAutofit = true;
                body.AutofitFontScale = autofit.DoubleAttr("fontScale");
            }

            if (bodyPr.First("spAutoFit") is not null) body.HasSpAutofit = true;
            if (bodyPr.Attr("vert") is { } vertical && vertical != "horz")
                warnings.AppendUnique("vertical text is not supported — imported horizontal");
        }
        foreach (var paragraphElement in txBody.Children("p"))
        {
            var pPr = paragraphElement.First("pPr");
            var level = pPr?.IntAttr("lvl") ?? 0;

            var defaults = inheritance.TextDefaults(placeholder, level, warnings)
                .Merging(inheritance.DefaultsFromParagraphProperties(pPr, warnings));
            var paragraph = new PptxParagraph { Alignment = defaults.Alignment };
            foreach (var child in paragraphElement.Elements())
            {
                switch (child.Name.LocalName)
                {
                    case "r":
                        paragraph.Runs.Add(ParseRun(child, defaults, inheritance, warnings));
                        break;
                    case "br":
                        paragraph.Runs.Add(new PptxRun { IsBreak = true });
                        break;
                    case "fld":
                        warnings.AppendUnique("dynamic fields (slide numbers, dates) imported as static text");
                        paragraph.Runs.Add(ParseRun(child, defaults, inheritance, warnings));
                        break;
                }
            }
            body.Paragraphs.Add(paragraph);
        }
        return body;
    }

    private static PptxRun ParseRun(XElement r, PptxTextDefaults defaults, PptxInheritance inheritance, List<string> warnings)
    {
        var effective = r.First("rPr") is { } rPr ? defaults.Merging(inheritance.RunProperties(rPr, warnings)) : defaults;
        return new PptxRun
        {
            Text = r.First("t")?.Value ?? "",
            FontFamily = effective.FontFamily,
            SizeHundredthsPt = effective.SizeHundredthsPt,
            Bold = effective.Bold ?? false,
            Italic = effective.Italic ?? false,
            Underline = effective.Underline ?? false,
            Strike = effective.Strike ?? false,
            ColorHex = effective.ColorHex,
            TrackingHundredthsPt = effective.TrackingHundredthsPt,
        };
    }

    private static PptxTransition? ParseTransition(XElement root)
    {
        static XElement? Find(XElement element)
        {
            foreach (var child in element.Elements())
            {
                if (child.Name.LocalName == "cSld") continue;
                if (child.Name.LocalName == "transition") return child;
                if (Find(child) is { } found) return found;
            }
            return null;
        }
        if (Find(root) is not { } transition) return null;
        var result = new PptxTransition();

        if (transition.Elements().FirstOrDefault(e => e.Name.LocalName != "extLst") is { } effect)
        {
            result.Kind = effect.Name.LocalName;
            result.ThroughBlack = effect.BoolAttr("thruBlk") ?? false;
            result.DurationSeconds = transition.DoubleAttr("dur") is { } ms
                ? ms / 1000
                : transition.Attr("spd") switch
                {
                    "slow" => 1.0,
                    "med" => 0.75,
                    _ => 0.5,
                };
        }
        result.AdvanceAfterMs = transition.DoubleAttr("advTm");
        return result;
    }

    private static List<PptxAnimation> ParseTiming(XElement root)
    {
        if (root.First("timing") is not { } timing) return [];
        var animationSteps = new List<PptxAnimation>();
        void Walk(XElement element)
        {
            foreach (var child in element.Elements())
            {
                if (child.Name.LocalName == "cTn" && child.Attr("presetClass") is { } presetClass)
                {
                    if (Effect(child, presetClass) is { } build) animationSteps.Add(build);
                    continue;
                }
                Walk(child);
            }
        }
        static PptxAnimation? Effect(XElement cTn, string presetClass)
        {
            XElement? target = null;
            double? duration = null;
            foreach (var node in cTn.Descendants())
            {
                if (node.Name.LocalName == "spTgt" && target is null) target = node;
                if (node.Name.LocalName == "cTn" && node.DoubleAttr("dur") is { } dur) duration = Math.Max(duration ?? 0, dur);
            }
            if (target?.IntAttr("spid") is not { } spid) return null;
            var range = target.Descendant("txEl", "pRg");
            return new PptxAnimation(
                spid,
                PresetClass: presetClass,
                PresetId: cTn.IntAttr("presetID"),
                PresetSubtype: cTn.IntAttr("presetSubtype"),
                NodeType: cTn.Attr("nodeType"),
                DelayMs: cTn.First("stCondLst")?.First("cond")?.DoubleAttr("delay") ?? 0,
                DurationMs: duration,
                ParagraphStart: range?.IntAttr("st"),
                ParagraphEnd: range?.IntAttr("end"));
        }
        Walk(timing);
        return animationSteps;
    }

    private static string? ParseNotes(string slidePart, PptxPackage package, Rels rels, PptxInheritance inheritance, List<string> warnings)
    {
        if (rels.Values.FirstOrDefault(r => r.Type.EndsWith("/notesSlide", StringComparison.Ordinal) && !r.External) is not { } rel) return null;
        var part = package.ResolveTarget(rel.Target, slidePart);
        if (package.TryRoot(part)?.Descendant("cSld", "spTree") is not { } spTree) return null;
        foreach (var sp in spTree.Children("sp"))
        {
            if (sp.Descendant("nvSpPr", "nvPr", "ph")?.Attr("type") != "body" || sp.First("txBody") is not { } txBody) continue;
            var text = ParseTextBody(txBody, null, inheritance, warnings).PlainText;
            return text.Length == 0 ? null : text;
        }
        return null;
    }
}
