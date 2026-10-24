using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Rendering;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>One slide in the slide grid: its thumbnail scene, number, group label and live state.</summary>
public sealed partial class SlideTileViewModel : ObservableObject
{
    public SlideTileViewModel(int index, Slide slide, RenderScene scene, string? groupName, string groupColorHex, bool startsGroup)
    {
        Index = index;
        Slide = slide;
        Scene = scene;
        GroupName = groupName;
        StartsGroup = startsGroup;
        GroupBrush = Painting.Brush(groupColorHex, new SceneColor(0.36, 0.36, 0.36));
    }

    public int Index { get; }
    public int Number => Index + 1;
    public Slide Slide { get; }

    /// <summary>The thumbnail; replaced as the slide is edited so the grid updates while typing.</summary>
    [ObservableProperty]
    public partial RenderScene Scene { get; set; }

    /// <summary>
    /// Some text on the slide doesn't fit its box, so the output cuts it off: the tile shows the
    /// Mac's orange warning. Measured just after the grid draws, so opening a long song stays quick.
    /// </summary>
    [ObservableProperty]
    public partial bool HasClippedText { get; set; }

    public static string ClippedTextHelp =>
        "Text on this slide doesn't fit its box — the overflow is clipped on the output. Open the slide in Edit to see the outlined box.";

    partial void OnSceneChanged(RenderScene value)
    {
        var scene = value;
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            if (!ReferenceEquals(Scene, scene)) return; // edited again since: that scene is measured instead
            try
            {
                HasClippedText = SlideSceneBuilder.ClippedTextObjectIds(Slide, scene, TextLayout.OverflowsFrame).Count > 0;
            }
            catch (Exception ex)
            {
                Infrastructure.Log.Warn($"Couldn't measure slide {Number}'s text: {ex.Message}");
            }
        });
    }

    public string? GroupName { get; }
    public bool StartsGroup { get; }
    public Brush GroupBrush { get; }

    public string Label => string.IsNullOrEmpty(GroupName) ? (string.IsNullOrWhiteSpace(Slide.Name) ? "" : Slide.Name) : GroupName;

    public string Notes => Slide.Notes ?? "";

    /// <summary>The slide's notes as a tooltip, or none when it has no notes.</summary>
    public string? NotesTooltip => string.IsNullOrWhiteSpace(Slide.Notes) ? null : Slide.Notes;

    [ObservableProperty]
    public partial bool IsLive { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>A presentation row in the library list.</summary>
public sealed class LibraryEntryViewModel(Presentation presentation)
{
    public Presentation Presentation { get; } = presentation;
    public string Name => Presentation.Name;
    public bool IsSong => Presentation.PresentationKind == PresentationKind.Song || Presentation.Sections is { Count: > 0 };
    public string Kind => IsSong ? "Song" : "Presentation";
    public string Glyph => IsSong ? "" : "";
    public string Detail => $"{Presentation.Slides.Count} slide{(Presentation.Slides.Count == 1 ? "" : "s")}"
        + (Presentation.Ccli?.Author is { Length: > 0 } author ? $" · {author}" : "");
}

/// <summary>A row in the service (run order).</summary>
public sealed partial class ServiceItemViewModel : ObservableObject
{
    public ServiceItemViewModel(ServiceItem item, int number, string? detail, bool missing)
    {
        Item = item;
        Number = number;
        Detail = detail;
        IsMissing = missing;
        Accent = Painting.Brush(item.ColorHex, new SceneColor(0.23, 0.51, 0.96, 0));
    }

    public ServiceItem Item { get; }
    public int Number { get; }
    public string Name => Item.Name;
    public string? Detail { get; }
    public bool IsMissing { get; }
    public bool IsHeader => Item.ItemKind == ServiceItemKind.Header;
    public Brush Accent { get; }

    public string Glyph => Item.ItemKind switch
    {
        ServiceItemKind.Media => "",
        ServiceItemKind.Audio => "",
        ServiceItemKind.Header => "",
        _ => "",
    };

    [ObservableProperty]
    public partial bool IsLive { get; set; }
}

/// <summary>A media item in the media bin.</summary>
public sealed partial class MediaTileViewModel : ObservableObject
{
    public MediaTileViewModel(MediaItem item, RenderScene preview)
    {
        Item = item;
        Preview = preview;
    }

    public MediaItem Item { get; }
    public RenderScene Preview { get; }
    public string Name => Item.Name;
    public bool IsVideo => Item.MediaKind == MediaKind.Video;
    public string Kind => IsVideo ? (Item.Classification == MediaClassification.Background ? "Loop" : "Video") : "Image";

    [ObservableProperty]
    public partial bool IsLive { get; set; }

    public static RenderScene PreviewScene(MediaItem item)
    {
        var scene = new RenderScene(SceneSize.Hd);
        scene.AddItem(new RenderItem
        {
            Id = $"bin-{item.Id}",
            Frame = new SceneRect(SceneSize.Hd),
            Content = new ItemContent.Media(item.Id, SceneMediaScaleMode.Fill),
        }, LayerKind.LoopingVideos);
        return scene;
    }
}

public sealed record ThemeChoice(string Id, string Name)
{
    public override string ToString() => Name;
}

public sealed record DesignChoice(string Name, string? Folder)
{
    public string Label => Folder is null ? Name : $"{Name}  ·  {Folder}";
    public override string ToString() => Label;
}

public sealed record ArrangementChoice(string? Id, string Name)
{
    public override string ToString() => Name;
}

internal static class GroupNames
{
    public static string? For(Slide slide, Presentation presentation) =>
        presentation.Sections?.FirstOrDefault(s => s.Id == slide.SectionId)?.Name;

    public static string ColorFor(Slide slide, Presentation presentation)
    {
        var section = presentation.Sections?.FirstOrDefault(s => s.Id == slide.SectionId);
        return StarterContent.GroupColor(section?.Name, section?.ColorHex);
    }
}
