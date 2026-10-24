namespace MxU.Slides.Windows.Rendering;

public sealed partial class MediaHub
{
    /// <summary>
    /// Forgets everything decoded from a media item's file: its pictures (and that one failed
    /// to load while the file was missing) and its video poster. Called after the item gets a
    /// file back (Locate) or a new one (Replace everywhere, Transcode now), so every tile and
    /// output draws the new file. The Mac's ThumbnailStore.mediaFileChanged.
    /// </summary>
    public void ForgetFile(string mediaId)
    {
        Invalidate(mediaId);
        posters.Remove(mediaId);
        posterFailed.Remove(mediaId);
        naturalSizes.Remove(mediaId);
    }
}
