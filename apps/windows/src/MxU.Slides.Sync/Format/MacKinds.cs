using MxU.Slides.Core.Model;

namespace MxU.Slides.Sync.Format;

/// <summary>Who shares a kind of document on the Mac (its <c>SyncScope</c>).</summary>
public enum MacScope
{
    /// <summary>The whole church: songs, services, themes, media… These are what library sync carries.</summary>
    Team,

    /// <summary>One computer's own set-up: output presets, MIDI devices, stream destinations…</summary>
    Station,

    /// <summary>Never leaves the computer (the import ledger).</summary>
    Local,
}

/// <summary>
/// One kind of library document, as the Mac names it: <c>DocumentKind</c> in
/// PresenterCore/Documents/DocumentEntity.swift (its raw value and <c>directoryName</c>), the
/// schema record that holds it (<c>SyncScope.entityType</c>), and who shares it
/// (<c>SyncScope.scope</c>). <see cref="WindowsType"/> is the C# model Windows keeps it as, or null
/// for the kinds Windows doesn't keep yet (they're left exactly as they are).
/// </summary>
public sealed record MacKind(string RawValue, string DirectoryName, string RecordName, MacScope Scope, Type? WindowsType)
{
    /// <summary>
    /// Synced between Windows and the Mac: a church-wide kind that Windows keeps. A computer's own
    /// set-up (output presets, MIDI devices, the combo and scheduler boards…) stays on each computer,
    /// as it does between Macs.
    /// </summary>
    public bool Synced => Scope == MacScope.Team && WindowsType is not null;

    /// <summary>Every kind, in the Mac's <c>DocumentKind.allCases</c> order.</summary>
    public static readonly IReadOnlyList<MacKind> All =
    [
        new("presentation", "presentations", "Presentation", MacScope.Team, typeof(Presentation)),
        new("service", "services", "Service", MacScope.Team, typeof(Service)),
        new("theme", "themes", "Theme", MacScope.Team, typeof(Theme)),
        new("media", "media", "MediaItem", MacScope.Team, typeof(MediaItem)),
        new("audio", "audio", "AudioItem", MacScope.Team, typeof(AudioItem)),
        new("playlist", "playlists", "Playlist", MacScope.Team, typeof(Playlist)),
        new("overlay", "overlays", "Overlay", MacScope.Team, typeof(Overlay)),
        new("outputPreset", "output-presets", "OutputPreset", MacScope.Station, typeof(OutputPreset)),
        new("alertPreset", "alert-presets", "AlertPreset", MacScope.Team, typeof(AlertPreset)),
        new("streamRecordPreset", "stream-presets", "StreamRecordPreset", MacScope.Station, typeof(StreamRecordPreset)),
        new("streamDestination", "stream-destinations", "StreamDestination", MacScope.Station, typeof(StreamDestination)),
        new("actionCombo", "action-combos", "ActionCombo", MacScope.Team, typeof(ActionCombo)),
        new("scheduleTrigger", "schedule-triggers", "ScheduleTrigger", MacScope.Team, typeof(ScheduleTrigger)),
        new("schedulerBoard", "scheduler", "SchedulerBoard", MacScope.Station, typeof(SchedulerBoard)),
        new("controlBoard", "control-boards", "ControlBoard", MacScope.Station, typeof(ControlBoard)),
        new("confidenceLayout", "confidence-layouts", "ConfidenceLayout", MacScope.Team, typeof(ConfidenceLayout)),
        new("groupPalette", "groups", "GroupPalette", MacScope.Team, typeof(GroupPalette)),
        new("effectPresetBoard", "effect-presets", "EffectPresetBoard", MacScope.Team, typeof(EffectPresetBoard)),
        new("animationPresetBoard", "animation-presets", "AnimationPresetBoard", MacScope.Team, typeof(AnimationPresetBoard)),
        new("midiDevice", "midi-devices", "MIDIDevice", MacScope.Station, typeof(MIDIDevice)),
        new("importLedger", "import-ledger", "ImportLedger", MacScope.Local, null),
        new("serviceLinkRules", "service-link-rules", "ServiceLinkRules", MacScope.Team, null),
        new("note", "notes", "NoteDocument", MacScope.Team, null),
        new("stationSettings", "station-settings", "StationSettings", MacScope.Station, null),
        new("slideBuildingSettings", "slide-building-settings", "SlideBuildingSettings", MacScope.Team, null),
        new("font", "font-files", "FontFile", MacScope.Team, null),
        new("workspaceSettings", "workspace-settings", "WorkspaceSettings", MacScope.Station, null),
        new("trackChannelList", "track-channels", "TrackChannelList", MacScope.Team, typeof(TrackChannelList)),
        new("trackSet", "track-sets", "TrackSet", MacScope.Team, typeof(TrackSet)),
    ];

    /// <summary>The kinds library sync carries.</summary>
    public static readonly IReadOnlyList<MacKind> SyncedKinds = All.Where(k => k.Synced).ToList();

    public static MacKind? ForWindowsType(Type type) => All.FirstOrDefault(k => k.WindowsType == type);

    public static MacKind? ForDirectory(string directory) => All.FirstOrDefault(k => k.DirectoryName == directory);

    public override string ToString() => RawValue;
}
