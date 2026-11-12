using MxU.Slides.Core.Model;
using MxU.Slides.Core.Notes;

namespace MxU.Slides.PptxImport;

/// <summary>
/// A PowerPoint file as a note: each slide's text boxes top to bottom, left to right, a line per
/// paragraph line, the title placeholders as headings. Port of the Mac's PPTXNoteText.swift.
/// </summary>
public static class PptxNoteText
{
    public static NoteBody Body(string path, CancellationToken cancellationToken = default)
    {
        var root = PptxArchive.Extract(path, cancellationToken);
        try
        {
            var package = new PptxPackage(root);
            var warnings = new List<string>();
            var parsed = PptxPresentationParser.Parse(package, warnings);
            var slides = parsed.SlidePartPaths.Select(part => PptxSlideParser.Parse(part, package, warnings, parsed.PartPath)).ToList();
            return NoteFileReader.Body(Lines(slides));
        }
        finally
        {
            PptxArchive.TryDelete(root);
        }
    }

    internal static List<NoteFileReader.Line> Lines(IEnumerable<PptxSlide> slides) =>
        slides.SelectMany(slide => slide.Shapes
            .Where(s => s.TextBody is not null)
            .OrderBy(s => s.Transform.OffYEmu).ThenBy(s => s.Transform.OffXEmu)
            .SelectMany(shape =>
            {
                int? heading = shape.PlaceholderType switch { "ctrTitle" => 1, "title" => 2, _ => null };
                return shape.TextBody!.Lines.Select(line => new NoteFileReader.Line(
                    line.Runs.Select(run => new NoteRun
                    {
                        Text = run.Text, Bold = run.Bold ? true : null, Italic = run.Italic ? true : null,
                        Underline = run.Underline ? true : null, Strike = run.Strike ? true : null,
                    }).ToList(), Heading: heading));
            })).ToList();
}
