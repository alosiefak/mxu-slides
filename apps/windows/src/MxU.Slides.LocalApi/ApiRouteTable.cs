using System.Text.Json.Nodes;

namespace MxU.Slides.LocalApi;

/// <summary>Every operation, with the Mac's paths, methods, scopes and shapes. Port of APIRouteTable.</summary>
public static partial class ApiRouteTable
{
    public static List<ApiRoute> Build(IApiBridge bridge)
    {
        var routes = new List<ApiRoute>();

        void Add(string method, string path, ApiScope scope, string operationId, string summary, string tag,
            JsonNode response, Func<ApiRequestContext, Task<ApiHandlerResponse>> handler, JsonNode? request = null) =>
            routes.Add(new ApiRoute(method, path, scope, operationId, summary, tag, request, response, handler));

        static Task<ApiHandlerResponse> Json<T>(T value) => Task.FromResult(ApiHandlerResponse.Json(value));
        static JsonNode Ref(string name) => ApiJson.Ref(name);
        static JsonNode ListOf(string name) => new JsonArray(ApiJson.Ref(name));
        var ok = Task.FromResult(ApiHandlerResponse.Ok);

        Add("GET", "/v1/library", ApiScope.View,
            "getLibrary", "Library section counts.", "Library",
            Ref("LibrarySummary"), _ => Json(bridge.LibrarySummary()));

        Add("GET", "/v1/library/{kind}", ApiScope.View,
            "listLibraryEntries", "List a section's entries (id, name, folder).", "Library",
            ListOf("LibraryEntry"), context => Json(bridge.LibraryEntries(context.DocumentKind())));

        Add("GET", "/v1/documents/{kind}/{id}", ApiScope.View,
            "getDocument",
            "Fetch a full document as schema JSON. Stream presets require edit access because they can contain ingest credentials.",
            "Documents",
            Ref("Document"), async context =>
            {
                var kind = context.DocumentKind();
                if (kind == ApiDocumentKind.StreamPresets && !context.Scope.Allows(ApiScope.Edit))
                    throw ApiError.Forbidden(ApiScope.Edit);
                return ApiHandlerResponse.Raw(await bridge.DocumentJsonAsync(kind, context.PathParameter("id")));
            });

        Add("GET", "/v1/status", ApiScope.View,
            "getShowStatus", "Live show state: slide, layers, overlays, alert, next.", "Show",
            Ref("ShowStatus"), _ => Json(bridge.ShowStatus()));

        Add("GET", "/v1/timers", ApiScope.View,
            "listTimers", "All timers with run state.", "Timers",
            ListOf("TimerStatus"), _ => Json(bridge.Timers()));

        Add("GET", "/v1/audio", ApiScope.View,
            "getAudioStatus", "Playing audio buses.", "Audio",
            Ref("AudioStatus"), _ => Json(bridge.AudioStatus()));

        Add("GET", "/v1/transport", ApiScope.View,
            "getTransport", "Playing videos with positions.", "Transport",
            ListOf("TransportRow"), _ => Json(bridge.TransportRows()));

        Add("GET", "/v1/outputs", ApiScope.View,
            "getOutputs", "Screens and output presets.", "Outputs",
            Ref("OutputsStatus"), _ => Json(bridge.OutputsStatus()));

        Add("GET", "/v1/scheduler", ApiScope.View,
            "getSchedulerStatus", "Scheduler state: on/off (this machine), next fire, per-trigger status.", "Scheduler",
            Ref("SchedulerStatus"), _ => Json(bridge.SchedulerStatus()));

        Add("GET", "/v1/timecode", ApiScope.View,
            "getTimecodeStatus",
            "Timecode in on this computer: input, lock, frame, rate, the song it drives, anchors to count from.",
            "Timecode",
            Ref("TimecodeStatus"), _ => Json(bridge.TimecodeStatus()));

        Add("POST", "/v1/scheduler/enabled", ApiScope.Control,
            "setSchedulerEnabled", "Turn the Scheduler on or off on THIS machine.", "Scheduler",
            Ref("OK"), context =>
            {
                bridge.SetSchedulerEnabled(context.Decode<ApiSchedulerEnableCommand>().Enabled);
                return ok;
            }, Ref("SchedulerEnableCommand"));

        Add("POST", "/v1/scheduler/triggers/{id}/enabled", ApiScope.Control,
            "setScheduleTriggerEnabled", "Pause or resume one trigger.", "Scheduler",
            Ref("OK"), context =>
            {
                var command = context.Decode<ApiSchedulerEnableCommand>();
                bridge.SetScheduleTriggerEnabled(context.PathParameter("id"), command.Enabled);
                return ok;
            }, Ref("SchedulerEnableCommand"));

        Add("POST", "/v1/scheduler/triggers/{id}/fire", ApiScope.Control,
            "fireScheduleTrigger", "Run a trigger's actions now — gates ignored (Run Now).", "Scheduler",
            Ref("OK"), context =>
            {
                bridge.FireScheduleTrigger(context.PathParameter("id"));
                return ok;
            });

        Add("GET", "/v1/inputs/video", ApiScope.View,
            "listVideoInputs",
            "Named video inputs: created items with their assigned capture device and attached audio input.",
            "Inputs",
            ListOf("VideoInputStatus"), _ => Json(bridge.VideoInputs()));

        Add("GET", "/v1/inputs/audio", ApiScope.View,
            "listAudioInputs", "Named audio inputs with their mixer strip state (on air, gain, mute).", "Inputs",
            ListOf("AudioInputStatus"), _ => Json(bridge.AudioInputs()));

        Add("GET", "/v1/audio/mixes", ApiScope.View,
            "listAudioMixes", "The room's mixes: in-app summing points with destination, fader, and mute.", "Inputs",
            ListOf("AudioMixStatus"), _ => Json(bridge.AudioMixes()));

        Add("POST", "/v1/inputs/audio/{id}/mixer", ApiScope.Control,
            "setMixerInput",
            "Change one audio input's mixer strip: on-air switch, gain, mute, lip-sync delay (ms). Absent fields don't change.",
            "Inputs",
            Ref("OK"), context =>
            {
                var command = context.Decode<ApiMixerInputCommand>();
                bridge.SetMixerInput(context.PathParameter("id"), command);
                return ok;
            }, Ref("MixerInputCommand"));

        Add("GET", "/v1/midi/devices", ApiScope.View,
            "listMIDIDevices",
            "MIDI device items with enabled, direction, connected state, and this machine's resolved endpoints.",
            "MIDI",
            ListOf("MIDIDeviceStatus"), _ => Json(bridge.MidiDevices()));

        Add("POST", "/v1/midi/devices/{id}/enabled", ApiScope.Control,
            "setMIDIDeviceEnabled", "Turn one MIDI device on or off (both directions).", "MIDI",
            Ref("OK"), context =>
            {
                var command = context.Decode<ApiMidiDeviceEnableCommand>();
                bridge.SetMidiDeviceEnabled(context.PathParameter("id"), command.Enabled);
                return ok;
            }, Ref("MIDIDeviceEnableCommand"));

        Add("POST", "/v1/show/slide", ApiScope.Control,
            "fireSlide", "Fire a slide (library context, or a service item's flattened position).", "Show",
            Ref("OK"), async context =>
            {
                await bridge.FireSlideAsync(context.Decode<ApiFireSlideCommand>());
                return ApiHandlerResponse.Ok;
            }, Ref("FireSlideCommand"));

        Add("POST", "/v1/show/advance", ApiScope.Control,
            "advance",
            "Advance (the grid's arrows; negative steps go back). Consumes the live slide's pending animation steps first — one call reveals one click group; past the last click one more call plays the EXIT group while the cue stays live, and the call after that moves to the next slide. Going back un-plays the exit / un-reveals a step before leaving the slide. Pass settled=true for the cut advance: the next slide fires with its With Slide steps already settled.",
            "Show",
            Ref("OK"), async context =>
            {
                var command = context.Decode<ApiAdvanceCommand>();
                await bridge.AdvanceAsync(command.Steps ?? 1, command.Settled ?? false);
                return ApiHandlerResponse.Ok;
            }, Ref("AdvanceCommand"));

        Add("POST", "/v1/show/clear", ApiScope.Control,
            "clear", "Clear one function or one layer.", "Show",
            Ref("OK"), context =>
            {
                bridge.Clear(context.Decode<ApiClearCommand>());
                return ok;
            }, Ref("ClearCommand"));

        Add("POST", "/v1/show/clear-all", ApiScope.Control,
            "clearAll", "The panic clear.", "Show",
            Ref("OK"), _ =>
            {
                bridge.ClearAll();
                return ok;
            });

        Add("POST", "/v1/media/{id}/fire", ApiScope.Control,
            "fireMedia", "Fire a media item onto its classification's default layer.", "Media",
            Ref("OK"), context =>
            {
                bridge.FireMedia(context.PathParameter("id"));
                return ok;
            });

        Add("POST", "/v1/action-combos/{id}/fire", ApiScope.Control,
            "fireActionCombo", "Run an Action Combo.", "Action Combos",
            Ref("OK"), context =>
            {
                bridge.FireActionCombo(context.PathParameter("id"));
                return ok;
            });

        Add("POST", "/v1/overlays/{id}/fire", ApiScope.Control,
            "fireOverlay", "Go live with an overlay.", "Overlays",
            Ref("OK"), context =>
            {
                bridge.FireOverlay(context.PathParameter("id"));
                return ok;
            });

        Add("POST", "/v1/overlays/{id}/dismiss", ApiScope.Control,
            "dismissOverlay", "Take an overlay down.", "Overlays",
            Ref("OK"), context =>
            {
                bridge.DismissOverlay(context.PathParameter("id"));
                return ok;
            });

        Add("POST", "/v1/alerts/fire", ApiScope.Control,
            "fireAlert", "Fire an alert preset (with token values) or an ad-hoc alert.", "Alerts",
            Ref("OK"), context =>
            {
                bridge.FireAlert(context.Decode<ApiFireAlertCommand>());
                return ok;
            }, Ref("FireAlertCommand"));

        Add("POST", "/v1/alerts/dismiss", ApiScope.Control,
            "dismissAlert", "Take the live alert down.", "Alerts",
            Ref("OK"), _ =>
            {
                bridge.DismissAlert();
                return ok;
            });

        foreach (var action in Enum.GetValues<ApiTimerAction>())
        {
            var name = action.ToString();
            Add("POST", $"/v1/timers/{{id}}/{name.ToLowerInvariant()}", ApiScope.Control,
                $"timer{name}", $"{name} a timer.", "Timers",
                Ref("OK"), context =>
                {
                    bridge.TimerAction(context.PathParameter("id"), action);
                    return ok;
                });
        }

        Add("POST", "/v1/audio/playlists/{id}/fire", ApiScope.Control,
            "fireAudioPlaylist", "Fire a playlist onto its output target.", "Audio",
            Ref("OK"), context =>
            {
                var command = context.Decode<ApiAudioFireCommand>();
                bridge.FireAudioPlaylist(context.PathParameter("id"), command.StartAtEntryId);
                return ok;
            }, Ref("AudioFireCommand"));

        Add("POST", "/v1/audio/items/{id}/fire", ApiScope.Control,
            "fireAudioItem", "Play one audio library item (no playlist semantics).", "Audio",
            Ref("OK"), context =>
            {
                bridge.FireAudioItem(context.PathParameter("id"));
                return ok;
            });

        Add("POST", "/v1/audio/stop", ApiScope.Control,
            "stopAudio", "Stop one bus by playlist or item id (body empty = all).", "Audio",
            Ref("OK"), context =>
            {
                bridge.StopAudio(context.Decode<ApiAudioStopCommand>());
                return ok;
            }, Ref("AudioStopCommand"));

        Add("POST", "/v1/audio/transport", ApiScope.Control,
            "audioTransport", "Audio transport: playPause, next, previous, seek.", "Audio",
            Ref("OK"), context =>
            {
                bridge.AudioTransport(context.Decode<ApiAudioTransportCommand>());
                return ok;
            }, Ref("AudioTransportCommand"));

        Add("POST", "/v1/transport/{id}", ApiScope.Control,
            "mediaTransport", "Video transport: pause, resume, toggle, reset, seek.", "Transport",
            Ref("OK"), context =>
            {
                bridge.MediaTransport(context.PathParameter("id"), context.Decode<ApiMediaTransportCommand>());
                return ok;
            }, Ref("MediaTransportCommand"));

        Add("POST", "/v1/output-presets/{id}/activate", ApiScope.Control,
            "activateOutputPreset", "Activate an output preset ('none' deactivates).", "Outputs",
            Ref("OK"), context =>
            {
                var id = context.PathParameter("id");
                bridge.ActivateOutputPreset(id == "none" ? null : id);
                return ok;
            });

        Add("POST", "/v1/services/{id}/select", ApiScope.Control,
            "selectService", "Make a service the run-of-show target (what advance walks).", "Services",
            Ref("OK"), context =>
            {
                bridge.SelectService(context.PathParameter("id"));
                return ok;
            });

        Add("POST", "/v1/documents/{kind}", ApiScope.Edit,
            "createDocument", "Create a document from schema JSON (id optional — generated when absent).", "Documents",
            Ref("Created"), async context =>
            {
                var id = await bridge.CreateDocumentAsync(context.DocumentKind(), context.Body);
                return ApiHandlerResponse.Json(new ApiCreatedResponse(id), status: 201);
            }, Ref("Document"));

        Add("PUT", "/v1/documents/{kind}/{id}", ApiScope.Edit,
            "updateDocument", "Replace a document's contents (writes through the CRDT document layer).", "Documents",
            Ref("OK"), async context =>
            {
                await bridge.UpdateDocumentAsync(context.DocumentKind(), context.PathParameter("id"), context.Body);
                return ApiHandlerResponse.Ok;
            }, Ref("Document"));

        Add("DELETE", "/v1/documents/{kind}/{id}", ApiScope.Edit,
            "deleteDocument", "Delete a document.", "Documents",
            Ref("OK"), async context =>
            {
                await bridge.DeleteDocumentAsync(context.DocumentKind(), context.PathParameter("id"));
                return ApiHandlerResponse.Ok;
            });

        Add("POST", "/v1/services/{id}/items", ApiScope.Edit,
            "addServiceItem", "Add a library item to a service's run order.", "Services",
            Ref("OK"), context =>
            {
                var command = context.Decode<ApiAddServiceItemCommand>();
                bridge.AddServiceItem(context.PathParameter("id"), command.RefId, command.Index);
                return ok;
            }, Ref("AddServiceItemCommand"));

        // Windows only (ApiRouteTable.Windows.cs): production control.
        AddWindowsRoutes(routes, bridge);
        return routes;
    }
}
