using MxU.Slides.Broadcast.Media;
using MxU.Slides.Broadcast.Media.Interop;

namespace MxU.Slides.Broadcast.Tests;

/// <summary>
/// Blobs (an H.264 sequence header, a picture's visible area) go to and from Media Foundation as
/// plain byte pointers. Before Phase 4 they were sent as SAFEARRAYs (the default for a byte[] in a
/// COM interface): the MP4 writer gave Windows a garbage sequence header, and reading one back
/// overwrote memory.
/// </summary>
public sealed class MediaFoundationBlobTests
{
    [Fact]
    public void BlobsRoundTripByteForByte()
    {
        var type = MediaFoundation.CreateMediaType();
        try
        {
            byte[] data = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17];
            MF.Check(type.SetBlob(MF.MF_MT_USER_DATA, data, (uint)data.Length), "SetBlob");
            Assert.Equal(data, MediaFoundation.GetBlob(type, MF.MF_MT_USER_DATA));
        }
        finally
        {
            MediaFoundation.Release(type);
        }
    }
}
