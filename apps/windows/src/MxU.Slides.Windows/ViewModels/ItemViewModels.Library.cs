using CommunityToolkit.Mvvm.ComponentModel;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>A media tile's warning: its file is missing, or it was flagged when imported (see MainViewModel.MissingMedia.cs).</summary>
public sealed partial class MediaTileViewModel
{
    /// <summary>The word on the tile's badge ("FILE MISSING", "NEEDS TRANSCODE"…), or null when all is well.</summary>
    [ObservableProperty]
    public partial string? Badge { get; set; }

    /// <summary>Why, for the badge's tooltip.</summary>
    [ObservableProperty]
    public partial string? BadgeDetail { get; set; }

    [ObservableProperty]
    public partial bool IsFileMissing { get; set; }

    /// <summary>True when "Transcode now" can be offered (flagged, or a transcode that failed, and its file is here).</summary>
    public bool CanTranscode => !IsFileMissing && Item.FileStatus is MediaFileStatus.NeedsTranscode or MediaFileStatus.TranscodeFailed;

    /// <summary>Sets the badge from the item's file status, as the Mac's library row does (missing beats the status).</summary>
    internal void ShowFileState(bool missing)
    {
        IsFileMissing = missing;
        (string? badge, string? detail) state = missing
            ? ("FILE MISSING", "The file isn't on this PC. Right-click › Locate missing file… to point at it.")
            : Item.FileStatus switch
            {
                MediaFileStatus.NeedsTranscode => ("NEEDS TRANSCODE", Item.StatusDetail),
                MediaFileStatus.Transcoding => ("TRANSCODING…", "Making an H.264 copy that plays on Windows."),
                MediaFileStatus.TranscodeFailed => ("TRANSCODE FAILED", Item.StatusDetail),
                // A ready file can still carry a note ("its sound can't be decoded…"): tooltip only.
                _ => (null, Item.StatusDetail.Length > 0 ? Item.StatusDetail : null),
            };
        (Badge, BadgeDetail) = state;
        OnPropertyChanged(nameof(CanTranscode));
        OnPropertyChanged(nameof(Detail));
    }

    /// <summary>"Video · 1:32 · 1280 × 720", or that the file is missing (the Mac's media card line).</summary>
    public string Detail => MediaUsage.Detail(Item.MediaKind, Item.PixelWidth, Item.PixelHeight, Item.DurationSeconds, IsFileMissing);
}

/// <summary>A slide tile's warning when media it shows (its background, a picture on it) has no file on this PC.</summary>
public sealed partial class SlideTileViewModel
{
    /// <summary>The names of the slide's media whose files are missing, as the badge's tooltip; null when none.</summary>
    [ObservableProperty]
    public partial string? MissingMedia { get; set; }
}
