namespace MxU.Slides.LocalApi;

/// <summary>
/// What the Local API asks of the app: the C# counterpart of the Mac's LocalAPIBridge.
/// The server calls it on the synchronization context that started it (the WPF dispatcher
/// when <see cref="LocalApiServer.Start(int, IApiBridge, ApiTokenStore)"/> runs on the UI
/// thread), as the Mac's bridge runs on the main actor. Throw <see cref="ApiError"/> to
/// answer with an error (the Mac's adapter uses NotFound for unknown ids and BadRequest for
/// bad input); any other exception becomes a 500.
/// </summary>
public interface IApiBridge
{
    // MARK: Library and documents

    /// <summary>Counts per kind, keyed by the kind's raw name ("presentations", "alert-presets", …).</summary>
    ApiLibrarySummary LibrarySummary();

    /// <summary>A kind's entries; an empty list for kinds this app doesn't store.</summary>
    IReadOnlyList<ApiLibraryEntry> LibraryEntries(ApiDocumentKind kind);

    /// <summary>The document as schema JSON; <see cref="ApiError.NotFound"/> when there is none.</summary>
    Task<byte[]> DocumentJsonAsync(ApiDocumentKind kind, string id);

    /// <summary>Creates from schema JSON (the id is optional: generate one when absent) and returns the id.</summary>
    Task<string> CreateDocumentAsync(ApiDocumentKind kind, byte[] body);

    /// <summary>Replaces a document; the body's id must match <paramref name="id"/>.</summary>
    Task UpdateDocumentAsync(ApiDocumentKind kind, string id, byte[] body);

    Task DeleteDocumentAsync(ApiDocumentKind kind, string id);

    /// <summary>Appends a library item to a service's run order, then moves it to <paramref name="index"/> when given.</summary>
    void AddServiceItem(string serviceId, string refId, int? index);

    /// <summary>Makes a service the run-of-show target (what advance walks).</summary>
    void SelectService(string id);

    // MARK: Show

    ApiShowStatus ShowStatus();

    /// <summary>A service item's flattened position (serviceItemId + occurrence), else a library slide.</summary>
    Task FireSlideAsync(ApiFireSlideCommand command);

    /// <summary>The grid's arrows through the current service; negative steps go back.</summary>
    Task AdvanceAsync(int steps, bool settled);

    /// <summary>One function (slides, media, overlays, audio, alerts, inputs) or one layer.</summary>
    void Clear(ApiClearCommand command);

    /// <summary>The panic clear.</summary>
    void ClearAll();

    /// <summary>Fires a media item onto its classification's default layer.</summary>
    void FireMedia(string id);

    void FireOverlay(string id);

    void DismissOverlay(string id);

    /// <summary>An alert preset (with token values) or an ad-hoc message, behavior and target.</summary>
    void FireAlert(ApiFireAlertCommand command);

    void DismissAlert();

    // MARK: Timers, video transport and outputs

    IReadOnlyList<ApiTimerStatus> Timers();

    void TimerAction(string id, ApiTimerAction action);

    /// <summary>Playing videos with their positions.</summary>
    IReadOnlyList<ApiTransportRow> TransportRows();

    /// <summary>Video transport on a row of <see cref="TransportRows"/>: pause, resume, toggle, reset, seek.</summary>
    void MediaTransport(string id, ApiMediaTransportCommand command);

    ApiOutputsStatus OutputsStatus();

    // MARK: Not on Windows yet
    // Each default answers the way the Mac reports a feature it doesn't have
    // (ApiError.Unavailable: 404 not_found "Scheduler unavailable."), so the route
    // exists, checks its scope and tells the client why. Implement one to switch it on.

    /// <summary>Nothing can be playing without an audio engine, so this reads as an empty Audio function.</summary>
    ApiAudioStatus AudioStatus() => new([]);

    void FireAudioPlaylist(string id, string? startAtEntryId) => throw ApiError.Unavailable("Audio");

    void FireAudioItem(string id) => throw ApiError.Unavailable("Audio");

    void StopAudio(ApiAudioStopCommand command) => throw ApiError.Unavailable("Audio");

    void AudioTransport(ApiAudioTransportCommand command) => throw ApiError.Unavailable("Audio");

    void FireActionCombo(string id) => throw ApiError.Unavailable("Actions");

    /// <summary><paramref name="id"/> is null to deactivate.</summary>
    void ActivateOutputPreset(string? id) => throw ApiError.Unavailable("Output presets");

    ApiSchedulerStatus SchedulerStatus() => throw ApiError.Unavailable("Scheduler");

    void SetSchedulerEnabled(bool enabled) => throw ApiError.Unavailable("Scheduler");

    void SetScheduleTriggerEnabled(string id, bool enabled) => throw ApiError.Unavailable("Scheduler");

    void FireScheduleTrigger(string id) => throw ApiError.Unavailable("Scheduler");

    ApiTimecodeStatus TimecodeStatus() => throw ApiError.Unavailable("Timecode");

    IReadOnlyList<ApiMidiDeviceStatus> MidiDevices() => throw ApiError.Unavailable("MIDI");

    void SetMidiDeviceEnabled(string id, bool enabled) => throw ApiError.Unavailable("MIDI");

    IReadOnlyList<ApiVideoInputStatus> VideoInputs() => throw ApiError.Unavailable("Video inputs");

    IReadOnlyList<ApiAudioInputStatus> AudioInputs() => throw ApiError.Unavailable("Audio inputs");

    IReadOnlyList<ApiAudioMixStatus> AudioMixes() => throw ApiError.Unavailable("Audio mixes");

    void SetMixerInput(string id, ApiMixerInputCommand command) => throw ApiError.Unavailable("Audio inputs");
}

public static class ApiIds
{
    /// <summary>
    /// A document id safe to use as a key or file name: 1–128 of A–Z a–z 0–9 - _ . and no "..".
    /// Port of the Mac adapter's validatedID; throws <see cref="ApiError.BadRequest"/> otherwise.
    /// </summary>
    public static string Validated(string id) =>
        id.Length is > 0 and <= 128 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') && !id.Contains("..")
            ? id
            : throw ApiError.BadRequest("Invalid document id.");
}
