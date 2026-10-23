using MxU.Slides.Core.Model;
using RV = Pro.SerializationInterop.RVProtoData;

namespace MxU.Slides.ProImport;

public sealed partial class ProPresenterImporter
{
    /// <summary>Imports a .protheme package, a theme folder, or a bare Theme document as an MxU theme.</summary>
    public Task<ProThemeImportSummary> ImportThemeAsync(
        string path, ProImportOptions? options = null,
        IProgress<ProImportProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => ImportThemeCoreAsync(path, options, progress, cancellationToken), cancellationToken);

    private async Task<ProThemeImportSummary> ImportThemeCoreAsync(
        string path, ProImportOptions? options, IProgress<ProImportProgress>? progress, CancellationToken cancellationToken)
    {
        options ??= DefaultOptions;
        var fallbackName = Path.GetFileNameWithoutExtension(path);
        ProThemeImportSummary Failed(string reason) => new() { SourcePath = path, Name = fallbackName, Warnings = [reason] };

        var resolver = new ImportMediaResolver(Library);
        var writer = new ImportWriter(Library, options.Policy, options.ConfirmOverwrite);
        string? extracted = null;
        try
        {
            var root = path;
            if (Path.GetExtension(path).Equals(".protheme", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    extracted = ImportArchive.Extract(path);
                    root = extracted;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    return Failed($"could not extract the theme: {error.Message}");
                }
            }
            if (ThemeFile(root) is not { } themeFile) return Failed("no Theme document inside");
            var name = Path.GetFileName(themeFile) == "Theme"
                ? Path.GetFileName(Path.GetDirectoryName(themeFile)) ?? fallbackName
                : fallbackName;
            progress?.Report(new ProImportProgress("Themes", name));

            ProMappedTheme mapped;
            try
            {
                var document = RV.Template.Types.Document.Parser.ParseFrom(await File.ReadAllBytesAsync(themeFile, cancellationToken).ConfigureAwait(false));
                mapped = ProWorkspaceMapper.MapTheme(document, name);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                return Failed($"not a readable ProPresenter theme: {error.Message}");
            }
            var warnings = mapped.Warnings.ToList();
            var label = $"theme \"{name}\"";
            if (await writer.CheckAsync<Theme>(mapped.Theme.Id, label, cancellationToken).ConfigureAwait(false) is { } reason)
                return new ProThemeImportSummary { SourcePath = path, ThemeId = mapped.Theme.Id, Name = name, Skipped = reason };

            var resolution = resolver.Resolve(mapped.MediaWants, themeFile, extracted, cancellationToken: cancellationToken);
            warnings.AddRange(resolution.Warnings);
            var theme = mapped.Theme;
            theme.Slides = theme.Slides?.Select(slide => ReplacingMediaIds(slide, resolution.IdMap)).ToList();
            if (!writer.Write(theme, label, warnings)) return Failed($"could not save: {warnings.LastOrDefault() ?? "unknown error"}");
            return new ProThemeImportSummary
            {
                SourcePath = path, ThemeId = theme.Id, Name = name, Warnings = warnings, MediaImported = resolution.Imported,
            };
        }
        finally
        {
            writer.SaveLedger();
            ImportArchive.TryDelete(extracted);
        }
    }

    internal static Slide ReplacingMediaIds(Slide slide, IReadOnlyDictionary<string, string> map)
    {
        slide.Objects = ReplacingMediaIds(slide.Objects, map);
        return slide;
    }

    internal static List<SlideObject> ReplacingMediaIds(List<SlideObject> objects, IReadOnlyDictionary<string, string> map)
    {
        var carrier = new Presentation
        {
            Id = "carrier", Name = "", PresentationKind = PresentationKind.Deck, ThemeId = "",
            Slides = [new Slide { Id = "carrier", Name = "", Objects = objects }],
        };
        return ProDocumentMapper.ReplacingMediaIds(carrier, map).Slides[0].Objects;
    }

    /// <summary>The Theme document at <paramref name="root"/>, directly inside it, or one folder down.</summary>
    internal static string? ThemeFile(string root)
    {
        if (File.Exists(root)) return Path.GetFileName(root) == "Theme" ? root : null;
        if (!Directory.Exists(root)) return null;
        var direct = Path.Combine(root, "Theme");
        if (File.Exists(direct)) return direct;
        foreach (var child in Directory.EnumerateFileSystemEntries(root).Order(StringComparer.Ordinal))
        {
            var nested = Path.Combine(child, "Theme");
            if (File.Exists(nested)) return nested;
        }
        return null;
    }
}
