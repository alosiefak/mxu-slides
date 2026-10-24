namespace MxU.Slides.Core.Model;

/// <summary>A top-level library document: stored as one JSON file named by its id.</summary>
public interface ILibraryDocument
{
    string Id { get; set; }
    string Name { get; set; }
}

public sealed partial class Presentation : ILibraryDocument;
public sealed partial class Theme : ILibraryDocument;
public sealed partial class Service : ILibraryDocument;
public sealed partial class MediaItem : ILibraryDocument;
public sealed partial class Overlay : ILibraryDocument;
public sealed partial class ConfidenceLayout : ILibraryDocument;
public sealed partial class AlertPreset : ILibraryDocument;
public sealed partial class AudioItem : ILibraryDocument;
public sealed partial class Playlist : ILibraryDocument;
public sealed partial class ActionCombo : ILibraryDocument;
public sealed partial class ScheduleTrigger : ILibraryDocument;
public sealed partial class OutputPreset : ILibraryDocument;
public sealed partial class StreamRecordPreset : ILibraryDocument;

/// <summary>How action combos are arranged in folders (one document, id "combo-board"). Boards have no name of their own.</summary>
public sealed partial class ControlBoard : ILibraryDocument
{
    string ILibraryDocument.Name { get => "Action combos"; set { } }
}

/// <summary>How schedule triggers are arranged in folders (one document, id "scheduler-board").</summary>
public sealed partial class SchedulerBoard : ILibraryDocument
{
    string ILibraryDocument.Name { get => "Schedule"; set { } }
}

/// <summary>A streaming destination. Its stream key is a secret, kept out of the JSON (see the Broadcast project).</summary>
public sealed partial class StreamDestination : ILibraryDocument;

/// <summary>A song's stems from one download (MultiTracks.com and friends), so they can be added to a timeline together.</summary>
public sealed partial class TrackSet : ILibraryDocument;

/// <summary>The church's list of tracks (click, guide, keys…): one document, id "track-channels", as on the Mac.</summary>
public sealed partial class TrackChannelList : ILibraryDocument
{
    /// <summary>The one list's id (TrackChannelList.wellKnownID on the Mac).</summary>
    public const string WellKnownId = "track-channels";

    string ILibraryDocument.Name { get => "Track Channels"; set { } }
}
