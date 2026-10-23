using System.Text.Json.Serialization;

namespace MxU.Slides.LocalApi;

// The JSON shapes the API sends and receives. Port of LocalAPI/WireModels.swift:
// property names are camelCased by ApiJson, null optionals are left out and
// dates are ISO 8601, so every payload reads exactly like the Mac's.

/// <summary>An answer that isn't a success: <c>{"status","code","message"}</c>. Port of APIError.</summary>
public sealed class ApiError(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;

    public ApiErrorBody Body => new(Status, Code, Message);

    public static ApiError BadRequest(string message) => new(400, "bad_request", message);

    public static ApiError NotFound(string message) => new(404, "not_found", message);

    public static ApiError Unauthorized(string message = "Missing or invalid token.") => new(401, "unauthorized", message);

    public static ApiError Forbidden(ApiScope required) =>
        new(403, "insufficient_scope", $"This endpoint requires the {required.Raw()} scope.");

    /// <summary>
    /// A feature this app doesn't have, reported the way the Mac reports one it lacks
    /// (<c>404 not_found "Actions unavailable."</c>), so clients see the same answer.
    /// </summary>
    public static ApiError Unavailable(string feature) => NotFound($"{feature} unavailable.");
}

public sealed record ApiErrorBody(int Status, string Code, string Message);

public enum ApiDocumentKind
{
    Presentations, Services, Themes, Media, Audio, Playlists, Overlays,
    AlertPresets, OutputPresets, StreamPresets, ActionCombos, ScheduleTriggers, ConfidenceLayouts,
}

public static class ApiDocumentKinds
{
    public static IReadOnlyList<ApiDocumentKind> All { get; } = Enum.GetValues<ApiDocumentKind>();

    public static string Raw(this ApiDocumentKind kind) => kind switch
    {
        ApiDocumentKind.AlertPresets => "alert-presets",
        ApiDocumentKind.OutputPresets => "output-presets",
        ApiDocumentKind.StreamPresets => "stream-presets",
        ApiDocumentKind.ActionCombos => "action-combos",
        ApiDocumentKind.ScheduleTriggers => "schedule-triggers",
        ApiDocumentKind.ConfidenceLayouts => "confidence-layouts",
        _ => kind.ToString().ToLowerInvariant(),
    };

    public static ApiDocumentKind? Parse(string raw) =>
        All.Where(k => k.Raw() == raw).Select(k => (ApiDocumentKind?)k).FirstOrDefault();
}

public sealed record ApiLibraryEntry(string Id, string Name, string Kind, string? Folder, DateTimeOffset? UpdatedAt);

public sealed record ApiLibrarySummary(IReadOnlyDictionary<string, int> Counts);

public sealed record ApiCreatedResponse(string Id);

public sealed record ApiOkResponse(bool Ok = true);

// MARK: Show status (APIShowStatus and its nested types)

public sealed record ApiShowStatus(
    ApiLiveSlide? LiveSlide,
    string? NextSlideText,
    IReadOnlyDictionary<string, ApiLayerContent> MediaLayers,
    IReadOnlyList<ApiLiveOverlay> Overlays,
    ApiLiveAlert? Alert,
    string? CurrentServiceId,
    ApiServiceRef? Service = null,
    ApiNextSlide? NextSlide = null,
    ApiServicePosition? Position = null);

/// <summary>
/// The live slide. <see cref="StepIndex"/> is the click groups consumed so far and
/// <see cref="StepCount"/> the click groups on the slide; both are absent without animation steps.
/// </summary>
public sealed record ApiLiveSlide(
    string PresentationId,
    string? PresentationName,
    string SlideId,
    int? SlideIndex,
    string? SlideName,
    string? ServiceItemId,
    int? Occurrence,
    int? StepIndex = null,
    int? StepCount = null,
    string? Text = null,
    int? SlideCount = null);

public sealed record ApiServiceRef(string Id, string Name);

public sealed record ApiNextSlide(string? Text, string? PresentationId = null, string? SlideId = null, int? SlideIndex = null);

public sealed record ApiServicePosition(string? CurrentItemName, string? NextItemName);

public sealed record ApiLiveOverlay(string Id, string Name, string? Layer);

public sealed record ApiLiveAlert(string Id, string Message, string Behavior, string Target, string? Layer);

public sealed record ApiLayerContent(string MediaId, string? MediaName, bool? Loops);

// MARK: Timecode

/// <summary>Timecode in: <see cref="Source"/> is "ltc" or "mtc"; <see cref="State"/> is noInput, locking, locked, freewheel or stopped.</summary>
public sealed record ApiTimecodeStatus(
    bool HasInput,
    string Source,
    bool Follows,
    string State,
    string? Label = null,
    string? Rate = null,
    bool Reverse = false,
    int OffsetFrames = 0,
    string? ServiceItemId = null,
    ApiTimecodeAnchor? Anchor = null,
    ApiTimecodeTimeline? Timeline = null);

public sealed record ApiTimecodeAnchor(double Seconds, DateTimeOffset AnchoredAt, bool Running, bool Reverse = false);

public sealed record ApiTimecodeTimeline(string ServiceItemId, double Position, DateTimeOffset AnchoredAt, bool Running, double Length);

// MARK: Scheduler

public sealed record ApiSchedulerStatus(bool Enabled, ApiSchedulerUpcoming? NextFire, IReadOnlyList<ApiSchedulerTriggerStatus> Triggers);

public sealed record ApiSchedulerUpcoming(string TriggerId, string Name, DateTimeOffset At);

public sealed record ApiSchedulerTriggerStatus(
    string Id,
    string Name,
    bool Enabled,
    bool FolderEnabled,
    string? FolderId,
    string? FolderName,
    DateTimeOffset? NextFire,
    DateTimeOffset? LastFired,
    bool? Archived = null);

public sealed record ApiSchedulerEnableCommand([property: JsonRequired] bool Enabled);

// MARK: Timers

/// <summary>A timer; <see cref="Mode"/> is countdown, countdownToTime or countUp.</summary>
public sealed record ApiTimerStatus(
    string Id,
    string Name,
    string Mode,
    bool Running,
    int DisplaySeconds,
    bool Overrun,
    string? FolderId,
    string? FolderName,
    DateTimeOffset? RunningSince = null,
    double? BankedSeconds = null,
    double? DurationSeconds = null,
    DateTimeOffset? TargetTime = null,
    DateTimeOffset? ArmedAt = null,
    IReadOnlyList<ApiTimerWarning>? Warnings = null);

public sealed record ApiTimerWarning(double RemainingSeconds, string ColorHex);

public sealed record ApiVideoCountdown(
    string Layer, string Name, double Duration, double Position, DateTimeOffset AnchoredAt, bool IsPlaying,
    string? ServiceItemId = null);

public sealed record ApiTimersDocument(IReadOnlyList<ApiTimerStatus> Timers, IReadOnlyList<ApiVideoCountdown> VideoCountdowns);

// MARK: Service snapshot

public sealed record ApiServiceSnapshot(ApiServiceRef Service, IReadOnlyList<ApiServiceSnapshot.Item> Items)
{
    public sealed record Size(int Width, int Height);

    public sealed record SlideRef(
        string Id, int Index, string? Label, string? Group, string? GroupColorHex, string? Text,
        string? ThumbnailChecksum, Size Size);

    public sealed record PresentationRef(
        string Id, string Name, string? ArrangementId, string? ArrangementName, IReadOnlyList<SlideRef> Slides);

    public sealed record MediaRef(string Id, string Name, int? DurationSeconds, string? ThumbnailChecksum);

    public sealed record Item(
        string Id, string Name, string Kind, string? ColorHex,
        PresentationRef? Presentation = null, MediaRef? Media = null);
}

// MARK: Inputs and mixes

/// <summary>A named video input; <see cref="Kind"/> is "camera" or "ndi".</summary>
public sealed record ApiVideoInputStatus(
    string Id, string Name, string? Kind = null, string? SourceId = null, string? AudioInputId = null, int? DelayFrames = null);

public sealed record ApiAudioInputStatus(
    string Id, string Name, bool Live, bool Enabled, double Gain, bool Muted,
    string? DeviceUid = null, string? MixId = null, int? DelayMs = null);

public sealed record ApiAudioMixStatus(
    string Id, string Name, int ChannelOffset, double Gain, bool Muted,
    string? DeviceUid = null, IReadOnlyList<string>? OutputIds = null);

/// <summary>Absent fields don't change.</summary>
public sealed record ApiMixerInputCommand(bool? Enabled = null, double? Gain = null, bool? Muted = null, int? DelayMs = null);

// MARK: Audio, transport and outputs

public sealed record ApiAudioStatus(IReadOnlyList<ApiAudioBus> Buses);

public sealed record ApiAudioBus(
    string? PlaylistId, string? AudioItemId, string? TrackName, bool Playing, double? Position, double? Duration);

public sealed record ApiTransportRow(
    string MediaId, string Name, string Layer, bool Playing, bool Looping, double Position, double Duration);

public sealed record ApiOutputsStatus(IReadOnlyList<ApiOutputScreen> Screens, IReadOnlyList<ApiOutputPreset> Presets);

public sealed record ApiOutputScreen(
    string Id, string Name, string Role, bool Backed, int Width, int Height,
    IReadOnlyList<ApiOutput> Outputs, IReadOnlyList<string> ActiveMasks);

/// <summary>One slice of a screen; <see cref="Backing"/> is display, ndi, decklink or none.</summary>
public sealed record ApiOutput(
    string Id, string? Name, double X, double Y, double Width, double Height, string Backing, bool Adjusted);

public sealed record ApiOutputPreset(string Id, string Name, bool Active);

// MARK: Commands

/// <summary>A library slide (presentation + slide id or index), or a service item's flattened position.</summary>
public sealed record ApiFireSlideCommand(
    string? PresentationId = null, string? SlideId = null, int? SlideIndex = null,
    string? ArrangementId = null, string? ServiceItemId = null, int? Occurrence = null);

/// <summary>Negative steps go back; <see cref="Settled"/> is the cut advance.</summary>
public sealed record ApiAdvanceCommand(int? Steps = null, bool? Settled = null);

/// <summary>One function (slides, media, …) or one layer.</summary>
public sealed record ApiClearCommand(string? Function = null, string? Layer = null);

public sealed record ApiFireAlertCommand(
    string? PresetId = null, string? Message = null, IReadOnlyDictionary<string, string>? Tokens = null,
    string? Behavior = null, string? Target = null, string? ThemeId = null);

public enum ApiTimerAction { Play, Pause, Reset }

public sealed record ApiAudioFireCommand(string? StartAtEntryId = null);

public sealed record ApiAudioStopCommand(string? PlaylistId = null, string? AudioItemId = null);

/// <summary><see cref="Action"/> is playPause, next, previous or seek.</summary>
public sealed record ApiAudioTransportCommand([property: JsonRequired] string Action, double? Position = null);

/// <summary><see cref="Action"/> is pause, resume, toggle, reset or seek.</summary>
public sealed record ApiMediaTransportCommand([property: JsonRequired] string Action, double? Position = null);

/// <summary>A MIDI device item; <see cref="Direction"/> is output, input or both.</summary>
public sealed record ApiMidiDeviceStatus(
    string Id, string Name, bool Enabled, string Direction, bool Connected,
    int? Channel = null, int? DestinationUid = null, int? SourceUid = null);

public sealed record ApiMidiDeviceEnableCommand([property: JsonRequired] bool Enabled);

public sealed record ApiAddServiceItemCommand([property: JsonRequired] string RefId, int? Index = null);

public sealed record ApiServerInfo(string Name, string Product, string ApiVersion, int SchemaVersion);
