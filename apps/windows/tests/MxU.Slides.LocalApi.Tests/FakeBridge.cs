using System.Globalization;

namespace MxU.Slides.LocalApi.Tests;

/// <summary>Records every call. Port of the Mac's FakeBridge, which implements every feature, the Mac-only ones included.</summary>
internal sealed class FakeBridge : IApiBridge
{
    public List<string> Calls { get; } = [];
    public Dictionary<string, byte[]> Documents { get; } = [];

    /// <summary>Presentations (or "advance") that must load first: the call yields before it acts.</summary>
    public HashSet<string> ColdDecks { get; } = [];

    public bool AdvanceError { get; set; }

    private static string Lower(bool? value) => value is { } b ? (b ? "true" : "false") : "-";

    public ApiLibrarySummary LibrarySummary() => new(new Dictionary<string, int> { ["presentations"] = 2 });

    public IReadOnlyList<ApiLibraryEntry> LibraryEntries(ApiDocumentKind kind) =>
        [new("p1", "Amazing Grace", kind.Raw(), "Lyrics", null)];

    public Task<byte[]> DocumentJsonAsync(ApiDocumentKind kind, string id) =>
        Documents.TryGetValue(id, out var data)
            ? Task.FromResult(data)
            : throw ApiError.NotFound($"No {kind.Raw()} with id {id}.");

    public ApiShowStatus ShowStatus() => new(null, null, new Dictionary<string, ApiLayerContent>(), [], null, null);

    public IReadOnlyList<ApiTimerStatus> Timers() => [];
    public ApiAudioStatus AudioStatus() => new([]);
    public IReadOnlyList<ApiTransportRow> TransportRows() => [];
    public ApiOutputsStatus OutputsStatus() => new([], []);

    public ApiSchedulerStatus SchedulerStatus() => new(false, null, []);
    public void FireScheduleTrigger(string id) => Calls.Add($"fireScheduleTrigger:{id}");

    public ApiTimecodeStatus TimecodeStatus() => new(
        HasInput: true, Source: "mtc", Follows: true, State: "locked", Label: "01:00:04:12", Rate: "25",
        ServiceItemId: "item-a",
        Anchor: new ApiTimecodeAnchor(3604.48, DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), Running: true));

    public IReadOnlyList<ApiMidiDeviceStatus> MidiDevices() =>
        [new("console-item", "Console", Enabled: true, Direction: "both", Connected: true, DestinationUid: 42, SourceUid: 43)];

    public void SetMidiDeviceEnabled(string id, bool enabled)
    {
        if (id != "console-item") throw ApiError.NotFound($"No MIDI device '{id}'.");
        Calls.Add($"setMIDIDeviceEnabled:{id}:{Lower(enabled)}");
    }

    public IReadOnlyList<ApiVideoInputStatus> VideoInputs() =>
        [new("vin-1", "Center Camera", Kind: "camera", SourceId: "cam-uid", AudioInputId: "ain-1")];

    public IReadOnlyList<ApiAudioInputStatus> AudioInputs() =>
        [new("ain-1", "Board Feed", Live: true, Enabled: true, Gain: 0.8, Muted: false, DeviceUid: "usb-uid", MixId: "main")];

    public IReadOnlyList<ApiAudioMixStatus> AudioMixes() =>
    [
        new("main", "Main", ChannelOffset: 0, Gain: 1, Muted: false),
        new("mix-b", "Broadcast", ChannelOffset: 2, Gain: 0.9, Muted: false, DeviceUid: "dante-uid"),
    ];

    public void SetMixerInput(string id, ApiMixerInputCommand command)
    {
        if (id != "ain-1") throw ApiError.NotFound($"No audio input '{id}'.");
        var gain = command.Gain is { } g ? g.ToString("0.0", CultureInfo.InvariantCulture) : "-";
        Calls.Add($"setMixerInput:{id}:{Lower(command.Enabled)}:{gain}:{Lower(command.Muted)}");
    }

    public void SetSchedulerEnabled(bool enabled) => Calls.Add($"setSchedulerEnabled:{Lower(enabled)}");

    public void SetScheduleTriggerEnabled(string id, bool enabled) => Calls.Add($"setScheduleTriggerEnabled:{id}:{Lower(enabled)}");

    public async Task FireSlideAsync(ApiFireSlideCommand command)
    {
        if (command.PresentationId is { } id && ColdDecks.Contains(id))
        {
            await Task.Yield();
            ColdDecks.Remove(id);
        }
        Calls.Add($"fireSlide:{command.PresentationId ?? command.ServiceItemId ?? "?"}");
    }

    public async Task AdvanceAsync(int steps, bool settled)
    {
        if (ColdDecks.Contains("advance"))
        {
            await Task.Yield();
            ColdDecks.Remove("advance");
        }
        if (AdvanceError) throw ApiError.BadRequest("Already at the end of the service.");
        Calls.Add(settled ? $"advance:{steps}:settled" : $"advance:{steps}");
    }

    public void Clear(ApiClearCommand command) => Calls.Add($"clear:{command.Function ?? command.Layer ?? "?"}");
    public void ClearAll() => Calls.Add("clearAll");
    public void FireMedia(string id) => Calls.Add($"fireMedia:{id}");
    public void FireOverlay(string id) => Calls.Add($"fireOverlay:{id}");
    public void DismissOverlay(string id) => Calls.Add($"dismissOverlay:{id}");
    public void FireActionCombo(string id) => Calls.Add($"fireActionCombo:{id}");
    public void FireAlert(ApiFireAlertCommand command) => Calls.Add($"fireAlert:{command.PresetId ?? command.Message ?? "?"}");
    public void DismissAlert() => Calls.Add("dismissAlert");
    public void TimerAction(string id, ApiTimerAction action) => Calls.Add($"timer:{id}:{action.ToString().ToLowerInvariant()}");
    public void FireAudioPlaylist(string id, string? startAtEntryId) => Calls.Add($"firePlaylist:{id}");
    public void FireAudioItem(string id) => Calls.Add($"fireAudioItem:{id}");
    public void StopAudio(ApiAudioStopCommand command) => Calls.Add("stopAudio");
    public void AudioTransport(ApiAudioTransportCommand command) => Calls.Add($"audioTransport:{command.Action}");
    public void MediaTransport(string id, ApiMediaTransportCommand command) => Calls.Add($"mediaTransport:{id}:{command.Action}");
    public void ActivateOutputPreset(string? id) => Calls.Add($"activatePreset:{id ?? "none"}");
    public void SelectService(string id) => Calls.Add($"selectService:{id}");

    public Task<string> CreateDocumentAsync(ApiDocumentKind kind, byte[] body)
    {
        const string id = "new-id";
        Documents[id] = body;
        Calls.Add($"create:{kind.Raw()}");
        return Task.FromResult(id);
    }

    public Task UpdateDocumentAsync(ApiDocumentKind kind, string id, byte[] body)
    {
        if (!Documents.ContainsKey(id)) throw ApiError.NotFound("No such document.");
        Documents[id] = body;
        Calls.Add($"update:{kind.Raw()}:{id}");
        return Task.CompletedTask;
    }

    public Task DeleteDocumentAsync(ApiDocumentKind kind, string id)
    {
        Documents.Remove(id);
        Calls.Add($"delete:{kind.Raw()}:{id}");
        return Task.CompletedTask;
    }

    public void AddServiceItem(string serviceId, string refId, int? index) => Calls.Add($"addItem:{serviceId}:{refId}");
}

/// <summary>Only what every bridge must implement: the Mac-only features keep IApiBridge's defaults.</summary>
internal class CoreOnlyBridge : IApiBridge
{
    public virtual ApiShowStatus ShowStatus() => new(null, null, new Dictionary<string, ApiLayerContent>(), [], null, null);

    public ApiLibrarySummary LibrarySummary() => new(new Dictionary<string, int>());
    public IReadOnlyList<ApiLibraryEntry> LibraryEntries(ApiDocumentKind kind) => [];
    public Task<byte[]> DocumentJsonAsync(ApiDocumentKind kind, string id) => throw ApiError.NotFound($"No document with id '{id}'.");
    public Task<string> CreateDocumentAsync(ApiDocumentKind kind, byte[] body) => Task.FromResult("id");
    public Task UpdateDocumentAsync(ApiDocumentKind kind, string id, byte[] body) => Task.CompletedTask;
    public Task DeleteDocumentAsync(ApiDocumentKind kind, string id) => Task.CompletedTask;
    public void AddServiceItem(string serviceId, string refId, int? index) { }
    public void SelectService(string id) { }
    public Task FireSlideAsync(ApiFireSlideCommand command) => Task.CompletedTask;
    public Task AdvanceAsync(int steps, bool settled) => Task.CompletedTask;
    public void Clear(ApiClearCommand command) { }
    public void ClearAll() { }
    public void FireMedia(string id) { }
    public void FireOverlay(string id) { }
    public void DismissOverlay(string id) { }
    public void FireAlert(ApiFireAlertCommand command) { }
    public void DismissAlert() { }
    public IReadOnlyList<ApiTimerStatus> Timers() => [];
    public void TimerAction(string id, ApiTimerAction action) { }
    public IReadOnlyList<ApiTransportRow> TransportRows() => [];
    public void MediaTransport(string id, ApiMediaTransportCommand command) { }
    public ApiOutputsStatus OutputsStatus() => new([], []);
}
