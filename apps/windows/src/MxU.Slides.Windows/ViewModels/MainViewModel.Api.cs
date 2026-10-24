using System.Text.Json;
using MxU.Slides.Audio.Control;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Outputs;
using MxU.Slides.Core.Scene;
using MxU.Slides.Core.Stage;
using MxU.Slides.LocalApi;
using MxU.Slides.Windows.Outputs;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// The Local API's view of the app (the Mac's LocalAPIBridgeAdapter): the
/// server calls these on the UI thread. Kinds the Windows library doesn't
/// store yet answer with an empty list and "not found".
/// </summary>
public sealed partial class MainViewModel : IApiBridge
{
    private static Type? DocumentType(ApiDocumentKind kind) => kind switch
    {
        ApiDocumentKind.Presentations => typeof(Presentation),
        ApiDocumentKind.Services => typeof(Service),
        ApiDocumentKind.Themes => typeof(Theme),
        ApiDocumentKind.Media => typeof(MediaItem),
        ApiDocumentKind.Overlays => typeof(Overlay),
        ApiDocumentKind.AlertPresets => typeof(AlertPreset),
        ApiDocumentKind.ConfidenceLayouts => typeof(ConfidenceLayout),
        _ => null,
    };

    private IEnumerable<ILibraryDocument> Documents(Type type) => type.Name switch
    {
        nameof(Presentation) => Library.All<Presentation>(),
        nameof(Service) => Library.All<Service>(),
        nameof(Theme) => Library.All<Theme>(),
        nameof(MediaItem) => Library.All<MediaItem>(),
        nameof(Overlay) => Library.All<Overlay>(),
        nameof(AlertPreset) => Library.All<AlertPreset>(),
        _ => Library.All<ConfidenceLayout>(),
    };

    private ILibraryDocument? Document(Type type, string id) => Documents(type).FirstOrDefault(d => d.Id == id);

    ApiLibrarySummary IApiBridge.LibrarySummary() => new(ApiDocumentKinds.All.ToDictionary(
        k => k.Raw(), k => DocumentType(k) is { } t ? Documents(t).Count() : 0));

    IReadOnlyList<ApiLibraryEntry> IApiBridge.LibraryEntries(ApiDocumentKind kind)
    {
        if (DocumentType(kind) is not { } type) return [];
        return Documents(type).Select(d => new ApiLibraryEntry(d.Id, d.Name, kind.Raw(), FolderOf(d), UpdatedAt(type, d.Id))).ToList();
    }

    private static string? FolderOf(ILibraryDocument d) => d switch
    {
        Presentation p => p.Folder,
        MediaItem m => m.Folder,
        Overlay o => o.Folder,
        ConfidenceLayout c => c.Folder,
        _ => null,
    };

    private DateTimeOffset? UpdatedAt(Type type, string id)
    {
        var path = Library.DocumentPath(type, id);
        return File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) : null;
    }

    Task<byte[]> IApiBridge.DocumentJsonAsync(ApiDocumentKind kind, string id)
    {
        var safe = ApiIds.Validated(id);
        if (DocumentType(kind) is not { } type || Document(type, safe) is not { } doc)
            throw ApiError.NotFound($"No document with id '{id}'.");
        return Task.FromResult(MxuJson.SerializeToUtf8((object)doc, indented: false));
    }

    Task<string> IApiBridge.CreateDocumentAsync(ApiDocumentKind kind, byte[] body)
    {
        if (DocumentType(kind) is not { } type) throw ApiError.NotFound("Unknown document kind.");
        System.Text.Json.Nodes.JsonObject json;
        try
        {
            json = System.Text.Json.Nodes.JsonNode.Parse(body) as System.Text.Json.Nodes.JsonObject
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw ApiError.BadRequest("Body must be a JSON object (the document).");
        }
        json["id"] = json["id"]?.GetValue<string>() is { Length: > 0 } supplied ? ApiIds.Validated(supplied) : LibraryStore.NewId();
        var doc = Decode(type, System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()));
        if (Document(type, doc.Id) is not null) throw new ApiError(409, "conflict", $"A document with id '{doc.Id}' already exists.");
        Save(type, doc);
        return Task.FromResult(doc.Id);
    }

    Task IApiBridge.UpdateDocumentAsync(ApiDocumentKind kind, string id, byte[] body)
    {
        var safe = ApiIds.Validated(id);
        if (DocumentType(kind) is not { } type || Document(type, safe) is null) throw ApiError.NotFound($"No document with id '{id}'.");
        var doc = Decode(type, body);
        if (doc.Id != safe) throw ApiError.BadRequest($"Body id '{doc.Id}' does not match the path id '{safe}'.");
        Save(type, doc);
        return Task.CompletedTask;
    }

    Task IApiBridge.DeleteDocumentAsync(ApiDocumentKind kind, string id)
    {
        var safe = ApiIds.Validated(id);
        if (DocumentType(kind) is not { } type || Document(type, safe) is null) throw ApiError.NotFound($"No document with id '{id}'.");
        switch (type.Name)
        {
            case nameof(Presentation): Library.Delete<Presentation>(safe); break;
            case nameof(Service): Library.Delete<Service>(safe); break;
            case nameof(Theme): Library.Delete<Theme>(safe); break;
            case nameof(MediaItem): Library.Delete<MediaItem>(safe); break;
            case nameof(Overlay): Library.Delete<Overlay>(safe); break;
            case nameof(AlertPreset): Library.Delete<AlertPreset>(safe); break;
            default: Library.Delete<ConfidenceLayout>(safe); break;
        }
        return Task.CompletedTask;
    }

    private static ILibraryDocument Decode(Type type, byte[] body)
    {
        try
        {
            return JsonSerializer.Deserialize(body, type, MxuJson.Options) as ILibraryDocument ?? throw new JsonException("Empty document");
        }
        catch (JsonException ex)
        {
            throw ApiError.BadRequest($"Not a valid {type.Name}: {ex.Message}");
        }
    }

    private void Save(Type type, ILibraryDocument doc)
    {
        switch (doc)
        {
            case Presentation p: SavePresentation(p); break;
            case Service s: Library.Save(s); break;
            case Theme t: Library.Save(t); Show.ThemeEdited(t); break;
            case MediaItem m: Library.Save(m); break;
            case Overlay o: Library.Save(o); break;
            case AlertPreset a: Library.Save(a); break;
            case ConfidenceLayout c: Library.Save(c); break;
            default: throw ApiError.NotFound($"Unknown document kind {type.Name}.");
        }
    }

    void IApiBridge.AddServiceItem(string serviceId, string refId, int? index)
    {
        if (Library.Get<Service>(serviceId) is null) throw ApiError.NotFound($"No service '{serviceId}'.");
        ServiceItem item = Library.Get<Presentation>(refId) is { } p
            ? new ServiceItem { Id = LibraryStore.NewId(), ItemKind = ServiceItemKind.Presentation, Name = p.Name, RefId = p.Id }
            : Library.Get<MediaItem>(refId) is { } m
                ? new ServiceItem { Id = LibraryStore.NewId(), ItemKind = ServiceItemKind.Media, Name = m.Name, RefId = m.Id }
                : throw ApiError.NotFound($"No library item '{refId}'.");
        // Into the version this computer runs, if it runs one (MainViewModel.ServiceVersions.cs).
        UpdateService(serviceId, service =>
        {
            service.Items.Add(item);
            if (index is { } i && i < service.Items.Count - 1)
            {
                service.Items.RemoveAt(service.Items.Count - 1);
                service.Items.Insert(Math.Max(0, i), item);
            }
        });
    }

    void IApiBridge.SelectService(string id)
    {
        if (Services.FirstOrDefault(s => s.Id == id) is not { } service) throw ApiError.NotFound($"No service '{id}'.");
        SelectedService = service;
    }

    // MARK: Show

    ApiShowStatus IApiBridge.ShowStatus()
    {
        var info = BuildConfidenceInfo();
        ApiLiveSlide? liveSlide = null;
        if (Show.Live is { } live)
        {
            var text = ConfidenceSceneBuilder.SlideText(live.Slide, live.Presentation).Body;
            var count = live.Presentation is { } lp ? SlideSceneBuilder.ArrangedSlides(lp, live.ArrangementId).Count : (int?)null;
            liveSlide = new ApiLiveSlide(
                live.Presentation?.Id ?? "", live.Presentation?.Name, live.Slide.Id, Live?.Occurrence,
                string.IsNullOrEmpty(live.Slide.Name) ? null : live.Slide.Name,
                Live?.ItemId is { } itemId && SelectedService?.Items.Any(i => i.Id == itemId) == true ? itemId : null,
                Live?.Occurrence, Show.SlideAnimationStep?.Consumed, Show.SlideAnimationStep?.Total,
                Text: text.Length == 0 ? null : text, SlideCount: count);
        }
        var layers = Show.LiveMedia.ToDictionary(p => p.Key.Raw(),
            p => new ApiLayerContent(p.Value.MediaId, Library.Get<MediaItem>(p.Value.MediaId)?.Name, p.Value.Loops));
        var next = info.Next?.Body is { Length: > 0 } body ? body : null;
        return new ApiShowStatus(
            liveSlide, next, layers,
            Show.LiveOverlays.Select(o => new ApiLiveOverlay(o.Id, o.Name, o.Layer)).ToList(),
            info.Alert is { } alert
                ? new ApiLiveAlert(alert.Id, alert.Message, AlertBehaviorJsonConverter.ToRaw(alert.Behavior),
                    Core.Model.AlertTargetJsonConverter.ToRaw(alert.Target), alert.Layer)
                : null,
            SelectedService?.Id,
            SelectedService is { } s ? new ApiServiceRef(s.Id, s.Name) : null,
            new ApiNextSlide(next),
            new ApiServicePosition(info.CurrentItemName, info.NextItemName));
    }

    Task IApiBridge.FireSlideAsync(ApiFireSlideCommand command)
    {
        if (command.ServiceItemId is { } itemId)
        {
            var service = Library.All<Service>().OrderByDescending(s => s.Id == SelectedService?.Id).FirstOrDefault(s => s.Items.Any(i => i.Id == itemId))
                ?? throw ApiError.NotFound($"No service contains an item '{itemId}'.");
            var positions = Positions(service).Where(p => p.Item.Id == itemId && p.Presentation is not null).ToList();
            if (positions.Count == 0) throw ApiError.NotFound($"Service item '{itemId}' has no firable slides.");
            var index = command.Occurrence ?? command.SlideIndex ?? 0;
            if (index < 0 || index >= positions.Count)
                throw ApiError.BadRequest($"Occurrence {index} is out of range (item has {positions.Count} positions).");
            if (SelectedService?.Id != service.Id) SelectedService = Services.FirstOrDefault(s => s.Id == service.Id);
            FirePosition(positions[index]);
            return Task.CompletedTask;
        }
        if (command.PresentationId is not { } presentationId || Library.Get<Presentation>(presentationId) is not { } presentation)
            throw ApiError.NotFound("Provide serviceItemId, or a presentationId that exists.");
        var slides = SlideSceneBuilder.ArrangedSlides(presentation, command.ArrangementId);
        int target;
        if (command.SlideId is { } slideId)
        {
            target = slides.FindIndex(s => s.Id == slideId);
            if (target < 0) throw ApiError.NotFound($"No slide '{slideId}' in that presentation's arranged order.");
        }
        else
        {
            target = command.SlideIndex ?? 0;
        }
        if (target < 0 || target >= slides.Count) throw ApiError.BadRequest($"Slide index {target} is out of range ({slides.Count} slides).");
        Fire(presentation, command.ArrangementId, target, null);
        return Task.CompletedTask;
    }

    Task IApiBridge.AdvanceAsync(int steps, bool settled)
    {
        var moved = false;
        for (var i = 0; i < Math.Abs(steps); i++)
        {
            if (!Step(Math.Sign(steps), settled)) break;
            moved = true;
        }
        if (steps != 0 && !moved) throw ApiError.BadRequest($"Already at the {(steps > 0 ? "end" : "start")} of the service.");
        return Task.CompletedTask;
    }

    void IApiBridge.Clear(ApiClearCommand command)
    {
        if (command.Function is { } raw)
        {
            ShowFunction function = raw switch
            {
                "slides" => ShowFunction.Slides,
                "media" => ShowFunction.Media,
                "overlays" => ShowFunction.Overlays,
                "audio" => ShowFunction.Audio,
                "alerts" => ShowFunction.Alerts,
                "inputs" => ShowFunction.Inputs,
                _ => throw ApiError.BadRequest($"Unknown function '{raw}'. Functions: slides, media, overlays, audio, alerts, inputs."),
            };
            // As its key does: Music stops the music too (MainViewModel.Clear.cs).
            ClearFunction(function);
        }
        else if (command.Layer is { } layerRaw)
        {
            var layer = LayerKinds.Parse(layerRaw)
                ?? throw ApiError.BadRequest($"Unknown layer '{layerRaw}'. Layers: {string.Join(", ", LayerKinds.All.Select(l => l.Raw()))}.");
            ClearLayer(layer);
        }
        else
        {
            throw ApiError.BadRequest("Provide either 'function' or 'layer'.");
        }
    }

    void IApiBridge.ClearAll() => ClearAllCommand.Execute(null);

    void IApiBridge.FireMedia(string id) =>
        FireMedia(Library.Get<MediaItem>(id) ?? throw ApiError.NotFound($"No media item '{id}'."));

    void IApiBridge.FireOverlay(string id)
    {
        SetOverlay(Library.Get<Overlay>(id) ?? throw ApiError.NotFound($"No overlay '{id}'."), up: true);
    }

    void IApiBridge.DismissOverlay(string id)
    {
        if (Show.LiveOverlays.FirstOrDefault(o => o.Id == id) is { } overlay) SetOverlay(overlay, up: false);
    }

    /// <summary>
    /// As the Mac's bridge: a preset fires as itself (its behaviour, target, layer and theme) with
    /// the blanks sent filled in and timer tokens kept live; a bare message fires with the
    /// behaviour, target and theme sent (steady, on the confidence monitors, by default).
    /// </summary>
    void IApiBridge.FireAlert(ApiFireAlertCommand command)
    {
        if (command.PresetId is { } presetId)
        {
            var preset = Library.Get<AlertPreset>(presetId) ?? throw ApiError.NotFound($"No alert preset '{presetId}'.");
            ShowAlertPreset(preset, command.Tokens, command.Message);
            return;
        }
        if (command.Message is not { Length: > 0 } message) throw ApiError.BadRequest("Provide 'presetId' or 'message'.");
        var behavior = AlertBehaviorJsonConverter.TryParse(command.Behavior, out var b) ? b : AlertBehavior.Persist;
        var target = Core.Model.AlertTargetJsonConverter.TryParse(command.Target, out var t) ? t : Core.Model.AlertTarget.Confidence;
        FireAlert(LibraryStore.NewId(), message, behavior, target, command.ThemeId);
    }

    void IApiBridge.DismissAlert() => ClearAlertsCommand.Execute(null);

    // MARK: Timers, transport and outputs

    IReadOnlyList<ApiTimerStatus> IApiBridge.Timers()
    {
        var now = DateTimeOffset.Now;
        return Timers.Timers.Select(t => new ApiTimerStatus(
            t.Id, t.Name, TimerController.ModeKey(t.Mode), t.IsRunning,
            (int)Math.Round(t.Value(now)), t.Urgency(now) == TimerUrgency.Overrun, null, null,
            t.RunningSince, t.Banked, t.DurationSeconds, t.TargetTime, t.ArmedAt,
            t.Warnings.Select(w => new ApiTimerWarning(w.RemainingSeconds, w.ColorHex)).ToList())).ToList();
    }

    void IApiBridge.TimerAction(string id, ApiTimerAction action)
    {
        if (Timers.Snapshot(id) is null) throw ApiError.NotFound($"No timer '{id}'.");
        switch (action)
        {
            case ApiTimerAction.Play: Timers.Start(id); break;
            case ApiTimerAction.Pause: Timers.Pause(id); break;
            default: Timers.Reset(id); break;
        }
    }

    IReadOnlyList<ApiTransportRow> IApiBridge.TransportRows()
    {
        var rows = new List<ApiTransportRow>();
        foreach (var (layer, cue) in Show.LiveMedia)
        {
            var key = $"live:live-media-{layer.Raw()}:{cue.MediaId}";
            if (Media.Transport(key) is not { } t) continue;
            rows.Add(new ApiTransportRow(cue.MediaId, Media.NameOf(cue.MediaId) ?? "Video", layer.Raw(), t.Playing, cue.Loops ?? true,
                t.Position.TotalSeconds, t.Duration?.TotalSeconds ?? 0));
        }
        return rows;
    }

    void IApiBridge.MediaTransport(string id, ApiMediaTransportCommand command)
    {
        var entry = Show.LiveMedia.FirstOrDefault(p => p.Value.MediaId == id);
        var key = entry.Value is null ? null : $"live:live-media-{entry.Key.Raw()}:{id}";
        if (key is null || Media.Transport(key) is not { } transport)
            throw ApiError.NotFound($"No playing video '{id}'. GET /v1/transport lists them.");
        switch (command.Action)
        {
            case "toggle": if (transport.Playing) Media.Pause(key); else Media.Play(key); break;
            case "pause": Media.Pause(key); break;
            case "resume": Media.Play(key); break;
            case "reset": Media.Restart(key); break;
            case "seek":
                if (command.Position is not { } position) throw ApiError.BadRequest("'seek' needs 'position' (seconds).");
                Media.Seek(key, TimeSpan.FromSeconds(Math.Max(0, position)));
                break;
            default:
                throw ApiError.BadRequest($"Unknown action '{command.Action}'. Actions: toggle, pause, resume, reset, seek.");
        }
    }

    ApiAudioStatus IApiBridge.AudioStatus() =>
        new([.. Audio.Status().Select(b => new ApiAudioBus(b.PlaylistId, b.AudioItemId, b.TrackName, b.Playing, b.Position, b.Duration))]);

    void IApiBridge.FireAudioPlaylist(string id, string? startAtEntryId) =>
        PlayAudioPlaylist(Library.Get<Playlist>(ApiIds.Validated(id)) ?? throw ApiError.NotFound($"No playlist '{id}'."), startAtEntryId, null);

    void IApiBridge.FireAudioItem(string id) =>
        PlayAudio(Library.Get<AudioItem>(ApiIds.Validated(id)) ?? throw ApiError.NotFound($"No audio item '{id}'."), null);

    void IApiBridge.StopAudio(ApiAudioStopCommand command)
    {
        if (command.PlaylistId is { } playlist) Audio.StopPlaylist(playlist);
        else if (command.AudioItemId is { } item) Audio.StopAudioItem(item);
        else Audio.Stop();
        UpdateNowPlaying();
    }

    void IApiBridge.AudioTransport(ApiAudioTransportCommand command)
    {
        var action = AudioController.ParseTransportAction(command.Action)
            ?? throw ApiError.BadRequest($"Unknown action '{command.Action}'. Actions: playPause, next, previous, seek.");
        try { Audio.Transport(action, command.Position); }
        catch (InvalidOperationException ex) { throw ApiError.BadRequest(ex.Message); }
    }

    void IApiBridge.FireActionCombo(string id)
    {
        if (Library.Get<ActionCombo>(ApiIds.Validated(id)) is null) throw ApiError.NotFound($"No action combo '{id}'.");
        RunActionCombo(id);
    }

    void IApiBridge.ActivateOutputPreset(string? id)
    {
        if (id is null)
        {
            // No preset: every layer and the screens' own looks everywhere again.
            ActivateOutputPreset(null);
            return;
        }
        if (Library.Get<OutputPreset>(ApiIds.Validated(id)) is null) throw ApiError.NotFound($"No output preset '{id}'.");
        ((Core.Automation.IShowActions)this).SwitchOutputPreset(id);
    }

    ApiOutputsStatus IApiBridge.OutputsStatus()
    {
        if (Outputs?.Config is not { } config) return new ApiOutputsStatus([], []);
        var screens = config.Screens.Select(screen =>
        {
            var slices = config.Slices(screen.Id).Select(slice =>
            {
                var device = config.Device(slice.Id);
                var rect = slice.SourceRect;
                return new ApiOutput(
                    SwiftUuid.Format(slice.Id), config.DeviceName(slice.Id) ?? slice.Name,
                    rect.X * screen.Width, rect.Y * screen.Height, rect.Width * screen.Width, rect.Height * screen.Height,
                    device is null ? "none" : OutputDevice.Kind(device) switch
                    {
                        OutputDeviceKind.Ndi => "ndi",
                        OutputDeviceKind.DeckLink => "decklink",
                        _ => "display",
                    },
                    config.EffectiveAdjustments(slice.Id) is not null);
            }).ToList();
            return new ApiOutputScreen(
                SwiftUuid.Format(screen.Id), screen.Name, config.Role(screen.Id).Raw(), config.IsClaimed(screen.Id),
                screen.Width, screen.Height, slices, [.. config.ActiveMasks(screen.Id).Select(m => m.Name)]);
        }).ToList();
        var presets = Library.All<OutputPreset>().Select(p => new ApiOutputPreset(p.Id, p.Name, p.Id == Settings.ActiveOutputPresetId)).ToList();
        return new ApiOutputsStatus(screens, presets);
    }
}
