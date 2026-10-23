using MxU.Slides.Core.Model;

namespace MxU.Slides.ProImport.Tests;

/// <summary>Imports real ProPresenter files from the folder named by PRO_IMPORT_BUNDLES and prints what came in; skipped otherwise.</summary>
public class RealBundleSweep
{
    [Fact]
    public async Task Bundles()
    {
        var dir = Environment.GetEnvironmentVariable("PRO_IMPORT_BUNDLES");
        Assert.SkipWhen(string.IsNullOrEmpty(dir), "set PRO_IMPORT_BUNDLES to a folder of .pro / .probundle files");
        using var library = new TestLibrary();
        var files = Directory.EnumerateFiles(Environment.ExpandEnvironmentVariables(dir!))
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".pro" or ".probundle")
            .ToList();
        var summaries = await new ProPresenterImporter(library.Store).ImportDocumentsAsync(
            files, new ProImportOptions { Policy = ImportConflictPolicy.Replace }, cancellationToken: TestContext.Current.CancellationToken);
        var output = TestContext.Current.TestOutputHelper;
        foreach (var s in summaries)
        {
            output?.WriteLine($"== {s.Name}: id={s.PresentationId ?? "SKIPPED"} media={s.MediaImported} warnings=[{string.Join("; ", s.Warnings)}]");
            if (library.Store.Get<Presentation>(s.PresentationId) is not { } p) continue;
            output?.WriteLine($"   slides={p.Slides.Count} sections=[{string.Join(", ", p.Sections?.Select(x => x.Name) ?? [])}] arrangements=[{string.Join(", ", p.Arrangements?.Select(a => a.Name) ?? [])}] ccli={p.Ccli?.SongNumber}");
            foreach (var slide in p.Slides.Take(3))
            {
                foreach (var o in slide.Objects)
                {
                    var style = o.TextStyle is { } t
                        ? $"font={t.FontName ?? "-"} size={t.FontSize ?? 0} color={t.ColorHex ?? "-"} track={t.Tracking ?? 0} valign={t.VerticalAlignment}"
                        : "";
                    var text = o.Text.Length > 40 ? o.Text[..40] : o.Text;
                    output?.WriteLine($"   [{o.ObjectKind}] '{o.Name}' frame=({o.X ?? -1},{o.Y ?? -1},{o.Width ?? -1},{o.Height ?? -1}) {style} text={text}");
                }
                if (slide.Background is { } bg) output?.WriteLine($"   bg media={bg.MediaId} layer={bg.Layer}");
                if (slide.Notes is { } notes) output?.WriteLine($"   notes={(notes.Length > 80 ? notes[..80] : notes)}");
            }
        }
        Assert.NotEmpty(summaries);
    }
}
