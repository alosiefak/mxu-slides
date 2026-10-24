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

/// <summary>Section names with their colours and hot keys (one document, id "group-palette"; see <see cref="GroupPalettes"/>).</summary>
public sealed partial class GroupPalette : ILibraryDocument
{
    string ILibraryDocument.Name { get => "Groups"; set { } }
}

/// <summary>A MIDI device the show talks to ("Lighting desk"); which ports it uses on this PC is a setting (see Core/Control/MidiDevices.cs).</summary>
public sealed partial class MIDIDevice : ILibraryDocument;

/// <summary>A streaming destination. Its stream key is a secret, kept out of the JSON (see the Broadcast project).</summary>
public sealed partial class StreamDestination : ILibraryDocument;
