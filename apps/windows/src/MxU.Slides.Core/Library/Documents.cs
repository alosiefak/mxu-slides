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
