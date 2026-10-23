using System.Globalization;
using MxU.Slides.Core.Model;

namespace MxU.Slides.PptxImport.Tests;

/// <summary>
/// Imports every .pptx in the folder named by PPTX_IMPORT_FIXTURE_DIR and prints
/// what came out. Skipped unless that variable is set.
/// </summary>
public class RealDeckSweep(ITestOutputHelper output)
{
    [Fact]
    public async Task Decks()
    {
        var directory = Environment.GetEnvironmentVariable("PPTX_IMPORT_FIXTURE_DIR");
        Assert.SkipWhen(string.IsNullOrEmpty(directory), "set PPTX_IMPORT_FIXTURE_DIR to a folder of .pptx files to sweep real decks");

        using var temp = new TempFolder("pptx-sweep");
        var library = TestPackages.Library(temp);
        var paths = Directory.EnumerateFiles(directory!).Where(f => Path.GetExtension(f).Equals(".pptx", StringComparison.OrdinalIgnoreCase));
        var summaries = await PptxImporter.ImportItemsAsync(paths, library, cancellationToken: TestContext.Current.CancellationToken);
        foreach (var s in summaries)
        {
            output.WriteLine($"== {s.Name}: id={s.PresentationId ?? "SKIPPED"} media={s.MediaImported}");
            if (s.MissingFonts.Count > 0) output.WriteLine($"   missingFonts=[{string.Join(", ", s.MissingFonts)}]");
            foreach (var warning in s.Warnings) output.WriteLine($"   △ {warning}");
            if (library.Get<Presentation>(s.PresentationId) is not { } p) continue;
            output.WriteLine($"   slides={p.Slides.Count} canvas={p.CanvasWidth ?? 1920}x{p.CanvasHeight ?? 1080} sections=[{string.Join(", ", p.Sections?.Select(x => x.Name) ?? [])}] bgFill={p.BackgroundFill?.FillKind}");
            for (var index = 0; index < p.Slides.Count; index++)
            {
                var slide = p.Slides[index];
                output.WriteLine($"   -- slide {index + 1} '{slide.Name}' objects={slide.Objects.Count} bgFill={slide.BackgroundFill?.ColorHex ?? "-"} bgMedia={slide.Background?.MediaId ?? "-"} actions={slide.Actions?.Count ?? 0}");
                foreach (var o in slide.Objects)
                {
                    var styleBits = o.TextStyle is { } style ? $"font={style.FontName ?? "-"} size={style.FontSize ?? 0} color={style.ColorHex ?? "-"}" : "";
                    var runBits = o.StyleRuns is { Count: > 0 } runs ? $" runs={runs.Count}" : "";
                    var shapeBits = o.ShapeKind is { } kind ? $" shape={kind} path={o.PathData?.Length ?? 0}ch" : "";
                    var mediaBits = o.MediaId is { } mediaId ? $" media={mediaId} crop={o.MediaSourceRect is not null}" : "";
                    if (o.Opacity is { } opacity) mediaBits += $" opacity={opacity.ToString(CultureInfo.InvariantCulture)}";
                    if (o.Fill?.FillKind == FillKind.Media) mediaBits += $" fillMedia={o.Fill.MediaId ?? "-"} fillCrop={o.Fill.MediaSourceRect is not null}";
                    var text = o.Text.Length > 36 ? o.Text[..36] : o.Text;
                    output.WriteLine($"      [{o.ObjectKind}] '{o.Name}' frame=({(int)(o.X ?? -1)},{(int)(o.Y ?? -1)},{(int)(o.Width ?? -1)},{(int)(o.Height ?? -1)}) {styleBits}{runBits}{shapeBits}{mediaBits} text={text}");
                }
            }
        }
        Assert.NotEmpty(summaries);
    }
}
