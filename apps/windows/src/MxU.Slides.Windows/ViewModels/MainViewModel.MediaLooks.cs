using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Media;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// A media item's picture and sound wherever it's shown full screen: its effects (shared with the
/// Mac, which applies them the same way: <see cref="MediaDressing"/>) and its Windows look (fit,
/// zoom, crop and volume: <see cref="MediaLookExtras"/>), and saving the media editor's changes.
/// </summary>
public sealed partial class MainViewModel
{
    private MediaLookExtras? mediaLooks;

    /// <summary>The media items' Windows looks, kept beside the library.</summary>
    public MediaLookExtras MediaLooks => mediaLooks ??= new MediaLookExtras(Library.Extras);

    /// <summary>The look of a media item by id (the default when there's no such item).</summary>
    public MediaLook MediaLookFor(string mediaId) => Library.Get<MediaItem>(mediaId) is { } item ? MediaLooks.For(item) : MediaLook.Default;

    /// <summary>A video's own volume, for the media hub (<see cref="Rendering.MediaHub.GainOf"/>).</summary>
    private double GainFor(string mediaId) => MediaLookFor(mediaId).Volume;

    /// <summary>The scene with every full-screen media item dressed in its effects and look.</summary>
    private AnimatedScene DressMedia(AnimatedScene scene) =>
        MediaDressing.Dress(scene, id => Library.Get<MediaItem>(id) is { } item ? new MediaDress(item.Effects, MediaLooks.For(item)) : null);

    /// <summary>
    /// Saves a media item the media editor changed, and shows the change on screen straight away if
    /// it's live (its effects and look; a new trim or speed applies the next time it's fired).
    /// </summary>
    public void SaveMediaItem(MediaItem item)
    {
        try
        {
            Library.Save(item);
        }
        catch (Exception ex)
        {
            Infrastructure.Log.Error($"Saving the media item \"{item.Name}\" failed", ex);
            Problem = $"Couldn't save \"{item.Name}\": {ex.Message}";
            return;
        }
        MediaShown(item);
    }

    /// <summary>Saves a media item's Windows look and shows it at once if the item is live.</summary>
    public void SaveMediaLook(MediaItem item, MediaLook look)
    {
        try
        {
            MediaLooks.Save(item, look);
        }
        catch (Exception ex)
        {
            Infrastructure.Log.Error($"Saving the look of \"{item.Name}\" failed", ex);
            Problem = $"Couldn't save the look of \"{item.Name}\": {ex.Message}";
            return;
        }
        Media.RefreshGains();
        MediaShown(item);
    }

    /// <summary>True when the item is on screen now (its own layer, or as the live slide's background).</summary>
    public bool IsMediaLive(string mediaId) => Show.LiveMedia.Values.Any(m => m.MediaId == mediaId)
        || Show.Scene().AllItems.Any(i => i.Content is Core.Scene.ItemContent.Media media && media.Id == mediaId);

    private void MediaShown(MediaItem item)
    {
        if (IsMediaLive(item.Id)) RenderOutputs(Rendering.SceneTransition.Cut, refreshOnly: true);
    }
}
