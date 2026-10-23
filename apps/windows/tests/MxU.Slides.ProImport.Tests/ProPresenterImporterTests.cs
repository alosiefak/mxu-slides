using System.IO.Compression;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using static MxU.Slides.ProImport.Tests.Proto;
using G = Pro.SerializationInterop.RVProtoData.Graphics.Types;
using RV = Pro.SerializationInterop.RVProtoData;

namespace MxU.Slides.ProImport.Tests;

public class ProPresenterImporterTests
{
    private static readonly ProImportOptions UpdateUnedited = new() { Policy = ImportConflictPolicy.UpdateUnedited };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static RV.Presentation MakeDocument(string? mediaPath)
    {
        var graphics = new G.Element
        {
            Uuid = Uuid(Guid.NewGuid().ToString()),
            Bounds = new() { Size = new() { Width = 1920, Height = 1080 } },
            Opacity = 1,
            Text = new()
            {
                RtfData = RtfBytes("{\\rtf1\\ansi{\\fonttbl\\f0\\fnil Helvetica;}\\f0 Hello glass}"),
                Attributes = new() { Font = new() { Name = "Helvetica", Size = 80 } },
            },
        };
        var cue = new RV.Cue
        {
            Uuid = Uuid(Guid.NewGuid().ToString()),
            Actions =
            {
                new RV.Action
                {
                    Uuid = Uuid(Guid.NewGuid().ToString()),
                    Slide = new() { Presentation = new() { BaseSlide = new() { Elements = { Wrap(graphics) }, Size = new() { Width = 1920, Height = 1080 } } } },
                },
            },
        };
        if (mediaPath is not null)
        {
            cue.Actions.Add(new RV.Action
            {
                Uuid = Uuid(Guid.NewGuid().ToString()),
                Media = new()
                {
                    Element = new() { Uuid = Uuid(Guid.NewGuid().ToString()), Url = new() { AbsoluteString = FileUrl(mediaPath) }, Image = new() },
                    LayerType = RV.Action.Types.LayerType.Foreground,
                },
            });
        }
        return new RV.Presentation { Uuid = Uuid(Guid.NewGuid().ToString()), Name = "Bundle Song", Cues = { cue } };
    }

    [Fact]
    public async Task ImportsABundleWithMediaAndDedupes()
    {
        using var library = new TestLibrary();
        var importer = new ProPresenterImporter(library.Store);
        using var stage = new TempFolder();
        const string fakeOriginal = "Users/test/Documents/ProPresenter - Test/Media/Assets/bg.png";
        WriteImage(stage.Combine(fakeOriginal.Split('/')));
        Write(MakeDocument("/" + fakeOriginal), stage.Combine("song.pro"));
        using var archives = new TempFolder();
        var bundle = archives.Combine($"{Guid.NewGuid()}.probundle");
        ZipFile.CreateFromDirectory(stage.Path, bundle);

        var summaries = await importer.ImportDocumentsAsync([bundle], UpdateUnedited, cancellationToken: Token);
        var summary = Assert.Single(summaries);
        Assert.NotNull(summary.PresentationId);
        Assert.Equal(1, summary.MediaImported);
        Assert.Empty(summary.Warnings);

        var imported = library.Store.Get<Presentation>(summary.PresentationId)!;
        Assert.Equal("Bundle Song", imported.Name);
        Assert.Equal("ProPresenter Import", imported.Folder);
        Assert.Single(imported.Slides);
        Assert.Equal("Hello glass", imported.Slides[0].Objects[0].Text);
        Assert.Null(imported.Slides[0].Background);
        var mediaId = imported.Slides[0].Actions!.First(a => a.Kind == SlideActionKind.FireMedia).MediaId!;
        Assert.False(mediaId.StartsWith(ProDocumentMapper.PlaceholderPrefix));
        Assert.Equal(MediaClassification.Foreground, library.Store.Get<MediaItem>(mediaId)!.Classification);

        var again = await importer.ImportDocumentsAsync([bundle], UpdateUnedited, cancellationToken: Token);
        Assert.Equal(0, again[0].MediaImported);
        Assert.Equal(1, library.Store.Count<MediaItem>());
    }

    [Fact]
    public async Task ReimportHonorsTheConflictPolicy()
    {
        using var library = new TestLibrary();
        var importer = new ProPresenterImporter(library.Store);
        var doc = MakeDocument(null);
        var docId = doc.Uuid.String.ToLowerInvariant();
        library.Store.Save(new Presentation
        {
            Id = docId, Name = "Old Import", PresentationKind = PresentationKind.Deck, ThemeId = "", Folder = "Old Folder",
            Slides = [new Slide { Id = "stale-slide", Name = "Stale", Objects = [], SectionId = "stale-section" }],
            Sections = [new PresentationSection { Id = "stale-section", Name = "" }],
        });
        using var stage = new TempFolder();
        Write(doc, stage.Combine("song.pro"));

        var summaries = await importer.ImportDocumentsAsync([stage.Path], UpdateUnedited, cancellationToken: Token);
        Assert.Equal(docId, summaries[0].PresentationId);
        Assert.Equal(ImportSkipReason.Edited, summaries[0].Skipped);
        Assert.Equal("Old Import", library.Store.Get<Presentation>(docId)!.Name);

        summaries = await importer.ImportDocumentsAsync([stage.Path], new ProImportOptions { Policy = ImportConflictPolicy.KeepMine }, cancellationToken: Token);
        Assert.Equal(ImportSkipReason.Existing, summaries[0].Skipped);

        summaries = await importer.ImportDocumentsAsync([stage.Path], new ProImportOptions { Policy = ImportConflictPolicy.Replace }, cancellationToken: Token);
        Assert.Null(summaries[0].Skipped);
        var imported = library.Store.Get<Presentation>(docId)!;
        Assert.Equal("Bundle Song", imported.Name);
        Assert.Null(imported.Sections);
        Assert.Single(imported.Slides);
        Assert.All(imported.Slides, s => Assert.NotEqual("stale-slide", s.Id));

        summaries = await importer.ImportDocumentsAsync([stage.Path], UpdateUnedited, cancellationToken: Token);
        Assert.Null(summaries[0].Skipped);

        var edited = MxuJson.Clone(library.Store.Get<Presentation>(docId)!);
        edited.Name = "Re-themed here";
        library.Store.Save(edited);
        summaries = await importer.ImportDocumentsAsync([stage.Path], UpdateUnedited, cancellationToken: Token);
        Assert.Equal(ImportSkipReason.Edited, summaries[0].Skipped);
        Assert.Equal("Re-themed here", library.Store.Get<Presentation>(docId)!.Name);
    }

    [Fact]
    public async Task LedgerSurvivesALibraryReload()
    {
        using var library = new TestLibrary();
        using var stage = new TempFolder();
        Write(MakeDocument(null), stage.Combine("song.pro"));
        await new ProPresenterImporter(library.Store).ImportDocumentsAsync([stage.Path], UpdateUnedited, cancellationToken: Token);

        var reopened = new LibraryStore(library.Store.Root);
        reopened.Load();
        var again = await new ProPresenterImporter(reopened).ImportDocumentsAsync([stage.Path], UpdateUnedited, cancellationToken: Token);
        Assert.Null(again[0].Skipped);
    }

    [Fact]
    public async Task AskPolicyOverwritesOnlyWhenTheCallerSaysSo()
    {
        using var library = new TestLibrary();
        var importer = new ProPresenterImporter(library.Store);
        using var stage = new TempFolder();
        var doc = MakeDocument(null);
        Write(doc, stage.Combine("song.pro"));
        var docId = doc.Uuid.String.ToLowerInvariant();
        Assert.Null((await importer.ImportDocumentsAsync([stage.Path], cancellationToken: Token))[0].Skipped);

        var edited = MxuJson.Clone(library.Store.Get<Presentation>(docId)!);
        edited.Name = "Mine";
        library.Store.Save(edited);

        var silent = await importer.ImportDocumentsAsync([stage.Path], cancellationToken: Token);
        Assert.Equal(ImportSkipReason.Edited, silent[0].Skipped);
        Assert.Equal("Mine", library.Store.Get<Presentation>(docId)!.Name);

        var asked = new List<ImportConflict>();
        var declined = await importer.ImportDocumentsAsync([stage.Path], new ProImportOptions
        {
            ConfirmOverwrite = (conflict, _) =>
            {
                asked.Add(conflict);
                return ValueTask.FromResult(false);
            },
        }, cancellationToken: Token);
        Assert.Equal(ImportSkipReason.Edited, declined[0].Skipped);
        var conflict = Assert.Single(asked);
        Assert.Equal(typeof(Presentation), conflict.DocumentType);
        Assert.Equal(docId, conflict.Id);
        Assert.True(conflict.EditedSinceImport);
        Assert.Equal("Mine", library.Store.Get<Presentation>(docId)!.Name);

        var accepted = await importer.ImportDocumentsAsync([stage.Path], new ProImportOptions
        {
            ConfirmOverwrite = (_, _) => ValueTask.FromResult(true),
        }, cancellationToken: Token);
        Assert.Null(accepted[0].Skipped);
        Assert.Equal("Bundle Song", library.Store.Get<Presentation>(docId)!.Name);
    }

    [Fact]
    public async Task PresentationsCanImportWithoutTheirMedia()
    {
        using var library = new TestLibrary();
        using var show = new TempFolder();
        var background = show.Combine("Media", "Assets", "bg.png");
        WriteImage(background);
        var doc = MakeDocument(background);
        Write(doc, show.Combine("Libraries", "Default", "song.pro"));

        var options = new WorkspaceImportOptions { Policy = ImportConflictPolicy.UpdateUnedited, PresentationMedia = false, Media = false };
        var importer = new ProWorkspaceImporter(library.Store);
        var progress = new RecordingProgress();
        var withoutMedia = await importer.ImportWorkspaceAsync(show.Path, options, progress: progress, cancellationToken: Token);
        Assert.NotNull(withoutMedia.Presentations[0].PresentationId);
        Assert.Equal(0, withoutMedia.MediaImported);
        Assert.Equal(1, withoutMedia.MediaWithheld);
        Assert.DoesNotContain("Media", progress.Phases);
        Assert.DoesNotContain(withoutMedia.Warnings, w => w.Contains("media not found"));
        Assert.Equal(0, library.Store.Count<MediaItem>());

        options.PresentationMedia = true;
        options.Policy = ImportConflictPolicy.Replace;
        var withMedia = await importer.ImportWorkspaceAsync(show.Path, options, cancellationToken: Token);
        Assert.Equal(1, withMedia.MediaImported);
        Assert.Equal(0, withMedia.MediaWithheld);

        options.PresentationMedia = false;
        var linked = await importer.ImportWorkspaceAsync(show.Path, options, cancellationToken: Token);
        Assert.Equal(0, linked.MediaImported);
        Assert.Equal(0, linked.MediaWithheld);
        var presentation = library.Store.Get<Presentation>(doc.Uuid.String.ToLowerInvariant())!;
        var fire = presentation.Slides[0].Actions!.First(a => a.Kind == SlideActionKind.FireMedia);
        Assert.False(fire.MediaId?.StartsWith(ProDocumentMapper.PlaceholderPrefix));
    }

    [Fact]
    public async Task MediaBinPlaylistsBecomeFolders()
    {
        using var library = new TestLibrary();
        using var show = new TempFolder();
        var shared = show.Combine("Media", "Assets", "shared.png");
        var loop = show.Combine("Media", "Assets", "loop.png");
        WriteImage(shared, 1);
        WriteImage(loop, 2);
        Write(MakeDocument(shared), show.Combine("Libraries", "Default", "song.pro"));

        static RV.PlaylistItem CueItem(string id, string file) => new()
        {
            Uuid = Uuid(id),
            Name = Path.GetFileName(file),
            Cue = new()
            {
                Uuid = Uuid($"cue-{id}"),
                Actions = { new RV.Action { Uuid = Uuid($"action-{id}"), Media = new() { Element = new() { Uuid = Uuid($"media-{id}"), Url = new() { AbsoluteString = FileUrl(file) }, Image = new() } } } },
            },
        };
        static RV.Playlist Playlist(string id, string name, params RV.PlaylistItem[] items)
        {
            var node = new RV.Playlist { Uuid = Uuid(id), Name = name, Items = new() };
            node.Items.Items.Add(items);
            return node;
        }
        static RV.Playlist Folder(string id, string name, params RV.Playlist[] children)
        {
            var node = new RV.Playlist { Uuid = Uuid(id), Name = name, Playlists = new() };
            node.Playlists.Playlists.Add(children);
            return node;
        }
        var binDoc = new RV.PlaylistDocument
        {
            RootNode = Folder("ROOT", "PLAYLIST",
                Folder("F-1", "Backgrounds", Playlist("PL-1", "Christmas", CueItem("A", shared), CueItem("B", loop))),
                Playlist("PL-2", "Loose", CueItem("C", loop))),
        };
        Write(binDoc, show.Combine("Playlists", "Media"));

        var mapped = ProPlaylistMapper.MapMediaBinFolders(binDoc);
        Assert.Equal(["Backgrounds/Christmas", "Loose"], mapped.Folders.Select(f => f.Path));
        Assert.Equal([2, 1], mapped.Folders.Select(f => f.WantIds.Count));

        var importer = new ProWorkspaceImporter(library.Store);
        Assert.Equal(2, importer.Scan(show.Path).Media);

        var options = new WorkspaceImportOptions { Policy = ImportConflictPolicy.UpdateUnedited };
        var progress = new RecordingProgress();
        var first = await importer.ImportWorkspaceAsync(show.Path, options, progress: progress, cancellationToken: Token);
        Assert.Contains("Media", progress.Phases);
        Assert.Equal(["Backgrounds/Christmas", "Loose"], first.MediaFoldersImported);
        Assert.Equal(2, first.MediaImported);
        var items = library.Store.All<MediaItem>();
        Assert.Equal(2, items.Count);
        var sharedItem = items.First(i => i.FileName == "shared.png");
        var loopItem = items.First(i => i.FileName == "loop.png");
        Assert.Equal("ProPresenter Import/Backgrounds/Christmas", sharedItem.Folder);
        Assert.Equal(MediaClassification.Foreground, sharedItem.Classification);
        Assert.Equal("ProPresenter Import/Backgrounds/Christmas", loopItem.Folder);

        var filed = MxuJson.Clone(loopItem);
        filed.Folder = "Mine";
        library.Store.Save(filed);
        var again = await importer.ImportWorkspaceAsync(show.Path, options, cancellationToken: Token);
        Assert.Equal(0, again.MediaImported);
        Assert.Equal(2, library.Store.Count<MediaItem>());
        Assert.Equal("Mine", library.Store.Get<MediaItem>(loopItem.Id)!.Folder);

        using var fresh = new TestLibrary();
        var offResult = await new ProWorkspaceImporter(fresh.Store).ImportWorkspaceAsync(show.Path, new WorkspaceImportOptions { Media = false }, cancellationToken: Token);
        Assert.Equal(1, offResult.MediaImported);
        Assert.Empty(offResult.MediaFoldersImported);
        Assert.Equal([null], fresh.Store.All<MediaItem>().Select(i => i.Folder));
    }

    [Fact]
    public void SavedOptionsGainNewRowsAsDefaults()
    {
        const string legacy = """{"policy":"keepMine","presentations":false,"themes":true,"overlays":true,"alerts":true,"confidenceLayouts":true,"combos":true,"playlists":true,"schedules":true,"groupHotKeys":true,"screens":false,"screenCorrections":true,"screenSlices":true,"screenMasks":true,"looks":true,"stageAssignments":true,"timers":true,"videoInputs":true,"midiDevices":true}""";
        var decoded = MxuJson.Deserialize<WorkspaceImportOptions>(legacy);
        Assert.Equal(ImportConflictPolicy.KeepMine, decoded.Policy);
        Assert.False(decoded.Presentations);
        Assert.False(decoded.Screens);
        Assert.True(decoded.PresentationMedia);
        Assert.True(decoded.Media);

        var options = new WorkspaceImportOptions { PresentationMedia = false, Media = false };
        Assert.Equal(options, MxuJson.Deserialize<WorkspaceImportOptions>(MxuJson.Serialize(options)));
        Assert.Contains("\"presentationMedia\": false", MxuJson.Serialize(options));
    }

    [Fact]
    public async Task MissingMediaBecomesAWarningNotAFailure()
    {
        using var library = new TestLibrary();
        using var folder = new TempFolder();
        var pro = folder.Combine($"{Guid.NewGuid()}.pro");
        Write(MakeDocument("/nonexistent/path/gone.mov"), pro);

        var summary = Assert.Single(await new ProPresenterImporter(library.Store).ImportDocumentsAsync([pro], cancellationToken: Token));
        Assert.NotNull(summary.PresentationId);
        Assert.Contains(summary.Warnings, w => w.Contains("media not found"));
        Assert.Null(library.Store.Get<Presentation>(summary.PresentationId)!.Slides[0].Background);
    }

    [Fact]
    public async Task GarbageFileIsSkippedWithReason()
    {
        using var library = new TestLibrary();
        using var folder = new TempFolder();
        var garbage = folder.Combine($"{Guid.NewGuid()}.pro");
        File.WriteAllText(garbage, "not a protobuf at all, sorry");

        var summary = Assert.Single(await new ProPresenterImporter(library.Store).ImportDocumentsAsync([garbage], cancellationToken: Token));
        Assert.Null(summary.PresentationId);
        Assert.Contains("not a readable ProPresenter 7 document", summary.Warnings[0]);
    }

    [Fact]
    public async Task ImportFilesRoutesEachKindAndReportsProgress()
    {
        using var library = new TestLibrary();
        using var folder = new TempFolder();
        var pro = folder.Combine("Song.pro");
        Write(MakeDocument(null), pro);
        var themeDir = folder.Combine("Lower Third");
        Write(new RV.Template.Types.Document { Slides = { new RV.Template.Types.Slide { Name = "Lyrics" } } }, Path.Combine(themeDir, "Theme"));
        var protheme = folder.Combine("Lower Third.protheme");
        ZipFile.CreateFromDirectory(themeDir, protheme, CompressionLevel.Fastest, includeBaseDirectory: true);

        var progress = new RecordingProgress();
        var result = await new ProPresenterImporter(library.Store).ImportFilesAsync([pro, protheme], progress: progress, cancellationToken: Token);
        Assert.True(Assert.Single(result.Documents).Imported);
        Assert.Equal("pro7-theme-lower-third", Assert.Single(result.Themes).ThemeId);
        Assert.Empty(result.Playlists);
        Assert.Contains("Presentations", progress.Phases);
        Assert.Contains("Themes", progress.Phases);
        Assert.NotNull(library.Store.Get<Theme>("pro7-theme-lower-third"));
    }

    [Fact]
    public async Task CancellationStopsBetweenDocuments()
    {
        using var library = new TestLibrary();
        using var folder = new TempFolder();
        Write(MakeDocument(null), folder.Combine("One.pro"));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ProPresenterImporter(library.Store).ImportDocumentsAsync([folder.Path], cancellationToken: cancelled.Token));
        Assert.Equal(0, library.Store.Count<Presentation>());
    }

    [Fact]
    public async Task RealCorpusSweep()
    {
        var dir = Environment.GetEnvironmentVariable("PRO_IMPORT_FIXTURE_DIR");
        Assert.SkipWhen(string.IsNullOrEmpty(dir), "set PRO_IMPORT_FIXTURE_DIR to a folder of .pro files");
        using var library = new TestLibrary();
        var summaries = await new ProPresenterImporter(library.Store).ImportDocumentsAsync(
            [Environment.ExpandEnvironmentVariables(dir!)], new ProImportOptions { Policy = ImportConflictPolicy.Replace }, cancellationToken: Token);
        Assert.NotEmpty(summaries);
        var failed = summaries.Where(s => s.PresentationId is null).ToList();
        var warned = summaries.Where(s => s.Warnings.Count > 0).ToList();
        var output = TestContext.Current.TestOutputHelper;
        output?.WriteLine($"PRO_IMPORT sweep: {summaries.Count} docs, {failed.Count} failed, {warned.Count} with warnings");
        foreach (var summary in warned) output?.WriteLine($"  {summary.Name}: {string.Join(" | ", summary.Warnings)}");
        Assert.True(failed.Count == 0, string.Join("\n", failed.Select(f => $"{Path.GetFileName(f.SourcePath)}: {string.Join("; ", f.Warnings)}")));
    }
}
