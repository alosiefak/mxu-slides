using Google.Protobuf;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using RV = Pro.SerializationInterop.RVProtoData;

namespace MxU.Slides.ProImport;

/// <summary>
/// Imports a whole ProPresenter show directory (the folder holding Libraries, Themes, Playlists and
/// Configuration). Library documents are saved to the <see cref="LibraryStore"/>; screens, looks,
/// timers, inputs, combos, scheduler triggers and MIDI devices come back as plans for the app.
/// </summary>
public sealed class ProWorkspaceImporter(LibraryStore library)
{
    private readonly ProPresenterImporter documents = new(library);

    public WorkspaceScan Scan(string showDirectory)
    {
        var scan = new WorkspaceScan();
        var config = Path.Combine(showDirectory, "Configuration");
        if (Decode(Path.Combine(config, "Timers"), RV.TimersDocument.Parser) is { } timers)
            scan.Timers = ProWorkspaceMapper.MapTimers(timers).Count;
        if (Decode(Path.Combine(config, "Workspace"), RV.ProPresenterWorkspace.Parser) is { } workspace)
        {
            scan.VideoInputs = workspace.VideoInputs.Count;
            scan.Screens = ProWorkspaceMapper.MapScreens(workspace).Plan.Screens.Count;
            scan.Looks = ProWorkspaceMapper.MapLooks(workspace).Looks.Count;
        }
        scan.Themes = Directories(Path.Combine(showDirectory, "Themes")).Count;
        scan.Presentations = ProPresenterImporter.CountDocuments(LibraryFolders(showDirectory));
        foreach (var file in new[] { "Media", "Audio" })
            if (Decode(Path.Combine(showDirectory, "Playlists", file), RV.PlaylistDocument.Parser) is { } bin)
                scan.Media += ProPlaylistMapper.MapMediaBinFolders(bin).Folders.Count;
        if (Decode(Path.Combine(config, "Props"), RV.PropDocument.Parser) is { } props)
            scan.Props = ProWorkspaceMapper.MapProps(props).Count;
        if (Decode(Path.Combine(config, "Messages"), RV.MessageDocument.Parser) is { } messages)
            scan.Messages = ProWorkspaceMapper.MapMessages(messages).Count;
        if (Decode(Path.Combine(config, "Stage"), RV.Stage.Types.Document.Parser) is { } stage)
            scan.StageLayouts = ProWorkspaceMapper.MapStageLayouts(stage, inputs: new ProInputPlanBook()).Count;
        if (Decode(Path.Combine(config, "Macros"), RV.MacrosDocument.Parser) is { } macros)
            scan.Macros = ProWorkspaceMapper.MapMacros(macros, inputs: new ProInputPlanBook()).Count;
        if (Decode(Path.Combine(config, "Calendar"), RV.Calendar.Parser) is { } calendar)
            scan.CalendarEvents = ProWorkspaceMapper.MapCalendar(calendar).Count;
        if (ReadBytes(Path.Combine(config, "CommunicationDevices")) is { } devices)
            scan.MidiDevices = ProWorkspaceMapper.MapCommunicationDevices(devices).Devices.Count;
        if (Decode(Path.Combine(config, "Groups"), RV.ProGroupsDocument.Parser) is { } groups)
            scan.GroupHotKeys = groups.Groups.Count(g => g.HotKey is { } key && ProDocumentMapper.HotKeyLetter(key.Code) is not null);
        var playlists = Path.Combine(showDirectory, "Playlists");
        scan.Playlists = Directory.Exists(playlists) ? Directory.EnumerateFiles(playlists).Count() : 0;
        return scan;
    }

    /// <param name="existingTimers">The app's timers, so ProPresenter timers with the same name reuse their ids.</param>
    /// <param name="existingInputs">The app's video inputs, so devices already set up are reused.</param>
    /// <param name="confirmOverwrite">Asked about each existing document when the policy is <see cref="ImportConflictPolicy.Ask"/>.</param>
    public Task<ProWorkspaceImportResult> ImportWorkspaceAsync(
        string showDirectory, WorkspaceImportOptions? options = null,
        IReadOnlyList<ProTimerSeed>? existingTimers = null, IReadOnlyList<ProInputSeed>? existingInputs = null,
        ImportConflictHandler? confirmOverwrite = null,
        IProgress<ProImportProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => ImportWorkspaceCoreAsync(
            showDirectory, options ?? new WorkspaceImportOptions(), existingTimers ?? [], existingInputs ?? [],
            confirmOverwrite, progress, cancellationToken), cancellationToken);

    private async Task<ProWorkspaceImportResult> ImportWorkspaceCoreAsync(
        string showDirectory, WorkspaceImportOptions options, IReadOnlyList<ProTimerSeed> existingTimers,
        IReadOnlyList<ProInputSeed> existingInputs, ImportConflictHandler? confirmOverwrite,
        IProgress<ProImportProgress>? progress, CancellationToken cancellationToken)
    {
        var result = new ProWorkspaceImportResult();
        var config = Path.Combine(showDirectory, "Configuration");
        var resolver = new ImportMediaResolver(library);
        var writer = new ImportWriter(library, options.Policy, confirmOverwrite);
        try
        {
            progress?.Report(new ProImportProgress("Timers"));
            var timerIdsByProUuid = new Dictionary<string, string>();
            var timerIdsByName = new Dictionary<string, string>();
            if (Decode(Path.Combine(config, "Timers"), RV.TimersDocument.Parser) is { } timersDoc)
            {
                result.TimerPlans = ProWorkspaceMapper.MapTimers(timersDoc, existingTimers);
                foreach (var plan in result.TimerPlans)
                {
                    timerIdsByProUuid[plan.ProUuid] = plan.NewId;
                    timerIdsByName[plan.Name.ToLowerInvariant()] = plan.NewId;
                }
            }

            var inputBook = new ProInputPlanBook();
            inputBook.Seed(existingInputs);
            var workspace = Decode(Path.Combine(config, "Workspace"), RV.ProPresenterWorkspace.Parser);
            foreach (var input in workspace?.VideoInputs ?? [])
                inputBook.RegisterWorkspaceInput(input.Uuid.Raw(), input.UserDescription, input.VideoInputDevice);

            var themeIdsByName = new Dictionary<string, string>();
            if (options.Themes)
            {
                var themeDirs = Directory.Exists(Path.Combine(showDirectory, "Themes"))
                    ? Directory.EnumerateFileSystemEntries(Path.Combine(showDirectory, "Themes")).Order(StringComparer.Ordinal).ToList()
                    : [];
                for (var index = 0; index < themeDirs.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var themeFile = Path.Combine(themeDirs[index], "Theme");
                    if (Decode(themeFile, RV.Template.Types.Document.Parser) is not { } doc) continue;
                    var name = Path.GetFileName(themeDirs[index]);
                    progress?.Report(new ProImportProgress("Themes", name, index, themeDirs.Count));
                    var mapped = ProWorkspaceMapper.MapTheme(doc, name);
                    themeIdsByName[name.ToLowerInvariant()] = mapped.Theme.Id;
                    var label = $"theme \"{name}\"";
                    if (await writer.CheckAsync<Theme>(mapped.Theme.Id, label, cancellationToken).ConfigureAwait(false) is not null) continue;
                    var resolution = resolver.Resolve(mapped.MediaWants, themeFile, showDirectory: showDirectory, cancellationToken: cancellationToken);
                    result.MediaImported += resolution.Imported;
                    mapped.Theme.Slides = mapped.Theme.Slides?.Select(s => ProPresenterImporter.ReplacingMediaIds(s, resolution.IdMap)).ToList();
                    if (writer.Write(mapped.Theme, label, result.Warnings)) result.ThemesImported.Add(name);
                    Prefix(mapped.Warnings.Concat(resolution.Warnings), label, result.Warnings);
                }
            }

            if (options.Presentations)
            {
                var importable = LibraryFolders(showDirectory);
                var totalDocuments = ProPresenterImporter.CountDocuments(importable);
                var completedDocuments = 0;
                foreach (var libraryDir in importable)
                {
                    var folder = importable.Count > 1
                        ? $"{ProPresenterImporter.DefaultFolder}/{Path.GetFileName(libraryDir)}"
                        : ProPresenterImporter.DefaultFolder;
                    var summaries = await documents.ImportItemsAsync(
                        [libraryDir], folder, resolver, writer, timerIdsByProUuid, timerIdsByName,
                        importMedia: options.PresentationMedia, cancellationToken: cancellationToken,
                        onDocument: path => progress?.Report(new ProImportProgress(
                            "Presentations", Path.GetFileNameWithoutExtension(path), completedDocuments++, totalDocuments)))
                        .ConfigureAwait(false);
                    result.Presentations.AddRange(summaries);
                    result.MediaImported += summaries.Sum(s => s.MediaImported);
                    result.MediaWithheld += summaries.Sum(s => s.MediaWithheld);
                }
            }

            progress?.Report(new ProImportProgress("Props"));
            if (options.Overlays && Decode(Path.Combine(config, "Props"), RV.PropDocument.Parser) is { } propsDoc)
            {
                foreach (var mapped in ProWorkspaceMapper.MapProps(propsDoc))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var label = $"prop \"{mapped.Overlay.Name}\"";
                    if (await writer.CheckAsync<Overlay>(mapped.Overlay.Id, label, cancellationToken).ConfigureAwait(false) is not null) continue;
                    var resolution = resolver.Resolve(mapped.MediaWants, Path.Combine(config, "Props"), showDirectory: showDirectory, cancellationToken: cancellationToken);
                    result.MediaImported += resolution.Imported;
                    mapped.Overlay.Objects = ProPresenterImporter.ReplacingMediaIds(mapped.Overlay.Objects, resolution.IdMap);
                    if (writer.Write(mapped.Overlay, label, result.Warnings)) result.OverlaysImported.Add(mapped.Overlay.Name);
                    Prefix(mapped.Warnings.Concat(resolution.Warnings), label, result.Warnings);
                }
            }

            progress?.Report(new ProImportProgress("Messages"));
            if (options.Alerts && Decode(Path.Combine(config, "Messages"), RV.MessageDocument.Parser) is { } messagesDoc)
            {
                foreach (var mapped in ProWorkspaceMapper.MapMessages(messagesDoc))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var preset = mapped.Preset;
                    if (mapped.TemplateThemeName is { } themeName)
                    {
                        if (themeIdsByName.TryGetValue(themeName.ToLowerInvariant(), out var themeId))
                        {
                            preset.ThemeId = themeId;
                            EnsureAlertsSlide(themeId, mapped.TemplateSlideName, result.Warnings);
                        }
                        else
                        {
                            result.Warnings.Add($"message \"{preset.Name}\": its Pro7 template theme \"{themeName}\" was not found — the alert uses the built-in banner");
                        }
                    }
                    var label = $"message \"{preset.Name}\"";
                    if (await writer.CheckAsync<AlertPreset>(preset.Id, label, cancellationToken).ConfigureAwait(false) is null
                        && writer.Write(preset, label, result.Warnings))
                        result.AlertsImported.Add(preset.Name);
                    Prefix(mapped.Warnings, label, result.Warnings);
                }
            }

            progress?.Report(new ProImportProgress("Stage layouts"));
            if (options.ConfidenceLayouts && Decode(Path.Combine(config, "Stage"), RV.Stage.Types.Document.Parser) is { } stageDoc)
            {
                foreach (var mapped in ProWorkspaceMapper.MapStageLayouts(stageDoc, timerIdsByProUuid, timerIdsByName, inputBook))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var label = $"stage layout \"{mapped.Layout.Name}\"";
                    if (await writer.CheckAsync<ConfidenceLayout>(mapped.Layout.Id, label, cancellationToken).ConfigureAwait(false) is not null) continue;
                    var resolution = resolver.Resolve(mapped.MediaWants, Path.Combine(config, "Stage"), showDirectory: showDirectory, cancellationToken: cancellationToken);
                    result.MediaImported += resolution.Imported;
                    mapped.Layout.Objects = ProPresenterImporter.ReplacingMediaIds(mapped.Layout.Objects, resolution.IdMap);
                    if (writer.Write(mapped.Layout, label, result.Warnings)) result.LayoutsImported.Add(mapped.Layout.Name);
                    Prefix(mapped.Warnings.Concat(resolution.Warnings), label, result.Warnings);
                }
            }

            progress?.Report(new ProImportProgress("Macros"));
            if (options.Combos && Decode(Path.Combine(config, "Macros"), RV.MacrosDocument.Parser) is { } macrosDoc)
            {
                foreach (var mapped in ProWorkspaceMapper.MapMacros(macrosDoc, timerIdsByProUuid, timerIdsByName, inputBook))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var resolution = resolver.Resolve(mapped.MediaWants, Path.Combine(config, "Macros"), showDirectory: showDirectory, cancellationToken: cancellationToken);
                    result.MediaImported += resolution.Imported;
                    var combo = ProWorkspaceMapper.ReplacingMediaIds(mapped.Combo, resolution.IdMap);
                    if (combo.Actions.Count < mapped.Combo.Actions.Count)
                        result.Warnings.Add($"macro \"{combo.Name}\": a fire-media step lost its file and was dropped");
                    result.Combos.Add(combo);
                    Prefix(mapped.Warnings.Concat(resolution.Warnings), $"macro \"{combo.Name}\"", result.Warnings);
                }
                result.ComboFolders.AddRange(MacroCollections(macrosDoc, result.Combos.Select(c => c.Id).ToHashSet()));
            }

            progress?.Report(new ProImportProgress("Calendar"));
            if (options.Schedules && Decode(Path.Combine(config, "Calendar"), RV.Calendar.Parser) is { } calendarDoc)
            {
                foreach (var mapped in ProWorkspaceMapper.MapCalendar(calendarDoc, timerIdsByProUuid, timerIdsByName))
                {
                    result.ScheduleTriggers.Add(mapped.Trigger);
                    Prefix(mapped.Warnings, $"calendar event \"{mapped.Trigger.Name}\"", result.Warnings);
                }
                if (!calendarDoc.Active && result.ScheduleTriggers.Count > 0)
                    result.Warnings.Add("the Pro calendar was turned off; its triggers imported enabled, pause them in the Scheduler if needed");
            }

            progress?.Report(new ProImportProgress("MIDI devices"));
            if (options.MidiDevices && ReadBytes(Path.Combine(config, "CommunicationDevices")) is { } deviceData)
            {
                var mapped = ProWorkspaceMapper.MapCommunicationDevices(deviceData);
                result.MidiDevices.AddRange(mapped.Devices);
                result.Warnings.AddRange(mapped.Warnings);
            }

            progress?.Report(new ProImportProgress("Screens & looks"));
            if (workspace is not null)
            {
                var screens = ProWorkspaceMapper.MapScreens(workspace);
                result.ScreenPlan = screens.Plan;
                result.Warnings.AddRange(screens.Warnings);
                var looks = ProWorkspaceMapper.MapLooks(workspace);
                result.LookPlans = looks.Looks;
                result.LiveLookUuid = looks.LiveLookUuid;
                result.Warnings.AddRange(looks.Warnings);
                foreach (var look in result.LookPlans)
                {
                    foreach (var screenLook in look.ScreenLooks)
                    {
                        if (screenLook.SlideThemeName is not { } name) continue;
                        if (themeIdsByName.TryGetValue(name.ToLowerInvariant(), out var themeId)) screenLook.SlideThemeId = themeId;
                        else result.Warnings.Add($"look \"{look.Name}\": its slide-layer theme \"{name}\" is not among the imported themes and was dropped");
                    }
                }
            }

            progress?.Report(new ProImportProgress("Playlists"));
            if (options.Playlists)
            {
                var playlists = await ImportPlaylistsAsync(showDirectory, result.Presentations, resolver, writer, cancellationToken).ConfigureAwait(false);
                result.ServicesImported.AddRange(playlists.Services);
                result.MediaImported += playlists.MediaImported;
                result.Warnings.AddRange(playlists.Warnings);
            }

            if (options.Media)
            {
                var outcome = ImportMediaBinFolders(showDirectory, resolver, progress, cancellationToken);
                result.MediaImported += outcome.MediaImported;
                result.MediaFoldersImported = outcome.Folders;
                result.Warnings.AddRange(outcome.Warnings);
            }

            if (options.GroupHotKeys && Decode(Path.Combine(config, "Groups"), RV.ProGroupsDocument.Parser) is { } groupsDoc)
            {
                foreach (var group in groupsDoc.Groups)
                {
                    if (group.HotKey is not { } hotKey || group.Name.Length == 0 || ProDocumentMapper.HotKeyLetter(hotKey.Code) is not { } letter) continue;
                    result.GroupHotKeys.TryAdd(ProDocumentMapper.NormalizedGroupName(group.Name), letter);
                }
            }

            result.InputPlans = inputBook.Plans.ToList();
            result.SkippedExisting = writer.SkippedExisting;
            result.SkippedEdited = writer.SkippedEdited.ToList();
            return result;
        }
        finally
        {
            writer.SaveLedger();
        }
    }

    private (List<string> Folders, int MediaImported, List<string> Warnings) ImportMediaBinFolders(
        string showDirectory, ImportMediaResolver resolver, IProgress<ProImportProgress>? progress, CancellationToken cancellationToken)
    {
        var folders = new List<string>();
        var imported = 0;
        var warnings = new List<string>();
        foreach (var file in new[] { "Media", "Audio" })
        {
            var playlistFile = Path.Combine(showDirectory, "Playlists", file);
            if (Decode(playlistFile, RV.PlaylistDocument.Parser) is not { } doc) continue;
            var mapped = ProPlaylistMapper.MapMediaBinFolders(doc);
            warnings.AddRange(mapped.Warnings);
            for (var index = 0; index < mapped.Folders.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var folder = mapped.Folders[index];
                progress?.Report(new ProImportProgress("Media", folder.Path, index, mapped.Folders.Count));
                var wants = mapped.MediaWants.Where(w => folder.WantIds.Contains(w.PlaceholderId));
                var resolution = resolver.Resolve(wants, playlistFile, showDirectory: showDirectory, cancellationToken: cancellationToken);
                imported += resolution.Imported;
                Prefix(resolution.Warnings, $"media playlist \"{folder.Path}\"", warnings);
                var libraryFolder = $"{ProPresenterImporter.DefaultFolder}/{folder.Path}";
                foreach (var id in folder.WantIds.Select(w => resolution.IdMap.GetValueOrDefault(w)).OfType<string>())
                {
                    if (library.Get<MediaItem>(id) is not { Folder: null } stored) continue;
                    var item = MxuJson.Clone(stored);
                    item.Folder = libraryFolder;
                    library.Save(item);
                }
                folders.Add(folder.Path);
            }
        }
        return (folders, imported, warnings);
    }

    private async Task<(List<string> Services, int MediaImported, List<string> Warnings)> ImportPlaylistsAsync(
        string showDirectory, List<ProDocumentSummary> presentations, ImportMediaResolver resolver,
        ImportWriter writer, CancellationToken cancellationToken)
    {
        var services = new List<string>();
        var imported = 0;
        var warnings = new List<string>();
        var playlistsDir = Path.Combine(showDirectory, "Playlists");

        var byAbsolutePath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byRelativePath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var showPath = SwiftCompat.FullNormalizedPath(showDirectory);
        foreach (var summary in presentations)
        {
            if (summary.PresentationId is not { } id) continue;
            var absolute = SwiftCompat.FullNormalizedPath(summary.SourcePath);
            byAbsolutePath[absolute] = id;
            if (absolute.StartsWith(showPath + "/", StringComparison.OrdinalIgnoreCase))
                byRelativePath[absolute[(showPath.Length + 1)..]] = id;
        }
        string? ResolvePresentation(ProPlaylistPresentationRef reference)
        {
            if (reference.AbsolutePath is { } absolute)
            {
                var standardized = SwiftCompat.NormalizedPath(absolute);
                if (byAbsolutePath.TryGetValue(standardized, out var id)) return id;
                var marker = standardized.IndexOf("/Libraries/", StringComparison.OrdinalIgnoreCase);
                if (marker >= 0 && byRelativePath.TryGetValue("Libraries/" + standardized[(marker + "/Libraries/".Length)..], out id)) return id;
            }
            if (reference.RelativePath is { } relative && byRelativePath.TryGetValue(SwiftCompat.NormalizedPath(relative), out var byRelative))
                return byRelative;
            return null;
        }

        if (!Directory.Exists(playlistsDir))
            warnings.Add("the workspace has no Playlists folder, so no services or playlists imported");

        var libraryFile = Path.Combine(playlistsDir, "Library");
        var libraryDoc = Decode(libraryFile, RV.PlaylistDocument.Parser);
        if (libraryDoc is null && Directory.Exists(playlistsDir))
        {
            var reason = File.Exists(libraryFile)
                ? "the Playlists/Library document could not be read"
                : "the workspace has no Playlists/Library document";
            warnings.Add($"{reason}, so no services imported");
        }
        if (libraryDoc is not null)
        {
            var mapped = ProPlaylistMapper.MapPresentationPlaylists(libraryDoc, ResolvePresentation);
            var resolution = resolver.Resolve(mapped.MediaWants, libraryFile, showDirectory: showDirectory, cancellationToken: cancellationToken);
            imported += resolution.Imported;
            foreach (var mappedService in mapped.Services)
            {
                var (service, dropped) = ProPlaylistMapper.ReplacingMediaIds(mappedService.Service, resolution.IdMap, resolution.AudioIds);
                var label = $"playlist \"{service.Name}\"";
                if (await writer.CheckAsync<Service>(service.Id, label, cancellationToken).ConfigureAwait(false) is null
                    && writer.Write(service, label, warnings))
                    services.Add(service.Name);
                Prefix(mappedService.Warnings.Concat(dropped), label, warnings);
            }
            warnings.AddRange(resolution.Warnings);
        }
        return (services, imported, warnings);
    }

    private static List<ProComboFolder> MacroCollections(RV.MacrosDocument doc, HashSet<string> comboIds)
    {
        var folders = new List<ProComboFolder>();
        foreach (var collection in doc.MacroCollections)
        {
            var members = collection.Items
                .Where(i => i.ItemTypeCase == RV.MacrosDocument.Types.MacroCollection.Types.Item.ItemTypeOneofCase.MacroId)
                .Select(i => i.MacroId.Lower())
                .Concat(collection.Macros.Select(m => m.Uuid.Lower()))
                .Where(comboIds.Contains)
                .Distinct()
                .ToList();
            if (members.Count == 0) continue;
            var name = collection.Name.Length == 0 ? "Macros" : collection.Name;
            var existing = folders.FindIndex(f => f.Name == name);
            if (existing >= 0) folders[existing] = new ProComboFolder(name, folders[existing].ComboIds.Concat(members).Distinct().ToList());
            else folders.Add(new ProComboFolder(name, members));
        }
        return folders;
    }

    private void EnsureAlertsSlide(string themeId, string? sourceSlideName, List<string> warnings)
    {
        if (library.Get<Theme>(themeId) is not { } stored) return;
        var theme = MxuJson.Clone(stored);
        var slides = theme.Slides ?? [];
        var source = (sourceSlideName is null ? null : slides.FirstOrDefault(s => string.Equals(s.Name, sourceSlideName, StringComparison.OrdinalIgnoreCase)))
            ?? slides.FirstOrDefault();
        if (source is null || slides.Any(s => string.Equals(s.Name, "Alerts", StringComparison.OrdinalIgnoreCase))) return;
        var alerts = MxuJson.Clone(source);
        alerts.Id = SwiftCompat.NewId();
        alerts.Name = "Alerts";
        theme.Slides = [.. slides, alerts];
        try
        {
            library.Save(theme);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"theme could not gain an Alerts slide: {error.Message}");
        }
    }

    private static void Prefix(IEnumerable<string> additions, string label, List<string> warnings)
    {
        var list = additions.ToList();
        foreach (var warning in list.Where(w => !w.StartsWith(label, StringComparison.Ordinal))) warnings.Add($"{label}: {warning}");
        foreach (var warning in list.Where(w => w.StartsWith(label, StringComparison.Ordinal))) warnings.Add(warning);
    }

    private static List<string> LibraryFolders(string showDirectory)
    {
        var libraries = Path.Combine(showDirectory, "Libraries");
        return Directories(libraries).Where(d => Path.GetFileName(d) != "LibraryData").Order(StringComparer.Ordinal).ToList();
    }

    private static List<string> Directories(string path) =>
        Directory.Exists(path) ? Directory.EnumerateDirectories(path).ToList() : [];

    private static byte[]? ReadBytes(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static T? Decode<T>(string path, MessageParser<T> parser) where T : class, IMessage<T>
    {
        if (ReadBytes(path) is not { } data) return null;
        try
        {
            return parser.ParseFrom(data);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return null;
        }
    }
}
