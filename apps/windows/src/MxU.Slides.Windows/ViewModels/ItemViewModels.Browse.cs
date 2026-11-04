using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>What a library row says about itself beyond its name: a star, tags, where it's filed, and (in search results) the words that matched.</summary>
public sealed partial class LibraryEntryViewModel
{
    /// <summary>The browser's view of it (folder, dates, favourite, tags); null for rows made elsewhere.</summary>
    public LibraryEntry? Entry { get; init; }

    /// <summary>The lyric line a search matched ("…I once was lost but now am found…").</summary>
    public string? Snippet { get; init; }

    /// <summary>Why a search found it when it isn't the title ("CCLI 22025", "by John Newton", "tag: Easter").</summary>
    public string? MatchNote { get; init; }

    /// <summary>Show the folder it's in (in search results and the flat list, which mix folders).</summary>
    public bool ShowsFolder { get; init; }

    public bool IsFavorite => Entry?.Favorite == true;

    public string? TagsLine => Entry is { Tags.Count: > 0 } entry ? string.Join(" · ", entry.Tags) : null;

    /// <summary>The second line: slides and author, then the match or the folder.</summary>
    public string Line
    {
        get
        {
            var parts = new List<string> { Detail };
            if (MatchNote is { } note) parts.Add(note);
            if (ShowsFolder && Entry is { Folder.Length: > 0 } entry) parts.Add("in " + entry.Folder.Replace("/", " › "));
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>A folder in the library list: click to go in. Its count is everything at or below it.</summary>
public sealed class LibraryFolderRowViewModel(string name, int count, IReadOnlyList<string> path)
{
    public string Name { get; } = name;
    public int Count { get; } = count;
    public IReadOnlyList<string> Path { get; } = path;
    public string Glyph => "";
    public string CountText => Count.ToString(System.Globalization.CultureInfo.CurrentCulture);
    public string Line => Count == 0 ? "Empty folder" : Count == 1 ? "1 item" : $"{Count} items";
}

/// <summary>"All songs and presentations": the whole library in one list, folders or not (the Mac's "All Presentations" row).</summary>
public sealed class LibraryAllRowViewModel(int count)
{
    public string Name => "All songs and presentations";
    public int Count { get; } = count;
    public string Glyph => "";
    public string Line => Count == 1 ? "1 item" : $"{Count} items";
}

/// <summary>A picture, video or piece of music in the library's search results.</summary>
public sealed class LibraryMediaHitViewModel(LibraryEntry entry, string detail, SearchField matched)
{
    public LibraryEntry Entry { get; } = entry;
    public string Name => Entry.Name;
    public bool IsAudio => Entry.Kind == LibraryItemKind.Audio;
    public bool IsFavorite => Entry.Favorite;
    public string Glyph => IsAudio ? "" : "";
    public string Line => detail + (matched switch
    {
        SearchField.Tag => " · tag: " + string.Join(", ", Entry.Tags),
        SearchField.FileName => " · by file name",
        _ => "",
    }) + (Entry.Folder.Length > 0 ? " · in " + Entry.Folder.Replace("/", " › ") : "");
}

/// <summary>The media bin's tile: picked for a batch, a favourite, its tags.</summary>
public sealed partial class MediaTileViewModel
{
    /// <summary>Picked with Ctrl+click or Shift+click, for a batch action (outlined in the bin).</summary>
    [ObservableProperty]
    public partial bool IsPicked { get; set; }

    public bool IsFavorite => Item.Favorite;

    public string? TagsLine => Item.Tags.Count > 0 ? string.Join(" · ", Item.Tags) : null;

    /// <summary>The tile's tooltip: name, then the details, tags and folder.</summary>
    public string Tooltip => string.Join("\n", new[] { Item.Name, Detail, TagsLine is { } tags ? "Tags: " + tags : null, Item.Folder is { Length: > 0 } folder ? "Folder: " + folder : null }.OfType<string>());
}

/// <summary>One step of the folder crumbs above the library list ("Library › Hymns › Easter").</summary>
public sealed record LibraryCrumb(string Name, IReadOnlyList<string> Path);

internal static class LibraryBrushes
{
    public static readonly Brush Star = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0xC5, 0x42)));

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
