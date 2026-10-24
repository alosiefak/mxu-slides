using System.Runtime.InteropServices.WindowsRuntime;
using MxU.Slides.VideoIO.Capture;
using MxU.Slides.VideoIO.Common;
using Windows.Graphics.Imaging;

namespace MxU.Slides.VideoIO.Tests.Capture;

/// <summary>
/// Windows: the camera path. CI has no camera, so the pixel copy is fed bitmaps made in
/// memory; the end-to-end test needs a camera and opts in with MXU_VIDEOIO_CAMERA_TEST=1
/// (it turns the camera on).
/// </summary>
public sealed class CameraInputTests
{
    [Fact]
    public void ABgraBitmapIsCopiedWithAlphaForcedOpaque()
    {
        byte[] pixels = [1, 2, 3, 0, 4, 5, 6, 0, 7, 8, 9, 0, 10, 11, 12, 0, 13, 14, 15, 0, 16, 17, 18, 0];
        using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, 3, 2, BitmapAlphaMode.Ignore);
        bitmap.CopyFromBuffer(pixels.AsBuffer());
        var store = new LatestFrameStore();
        var info = CameraInput.CopyBitmap(bitmap, store, 12.5);
        Assert.Equal(new CameraInput.VideoFrameInfo(3, 2), info);
        var frame = store.Latest;
        Assert.NotNull(frame);
        Assert.Equal(12, frame.Stride);
        Assert.Equal(12.5, frame.HostTime);
        Assert.Equal(new byte[] { 1, 2, 3, 255, 4, 5, 6, 255, 7, 8, 9, 255, 10, 11, 12, 255, 13, 14, 15, 255, 16, 17, 18, 255 }, frame.Pixels.ToArray());
    }

    [Fact]
    public void AnotherPixelFormatIsConvertedToBgra()
    {
        using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Gray8, 4, 2, BitmapAlphaMode.Ignore);
        bitmap.CopyFromBuffer(new byte[] { 0, 0, 255, 255, 0, 0, 255, 255 }.AsBuffer());
        var store = new LatestFrameStore();
        Assert.NotNull(CameraInput.CopyBitmap(bitmap, store, 0));
        var frame = store.Latest;
        Assert.NotNull(frame);
        Assert.Equal((4, 2), (frame.Width, frame.Height));
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, frame.Pixels[..4].ToArray());
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, frame.Pixels.Slice(8, 4).ToArray());
    }

    [Fact]
    public async Task ListingCamerasNeverThrows()
    {
        var cameras = await CameraDevices.ListAsync(TestContext.Current.CancellationToken);
        Assert.All(cameras, camera => Assert.NotEmpty(camera.Id));
    }

    [Fact]
    public async Task RatesForAMissingCameraAreEmpty() =>
        Assert.Empty(await CameraDevices.FrameRatesAsync(@"\\?\USB#VID_0000&PID_0000#nothing", TestContext.Current.CancellationToken));

    [Fact]
    public async Task ACameraListsItsRatesWithoutTurningOn()
    {
        var cameras = await CameraDevices.ListAsync(TestContext.Current.CancellationToken);
        if (cameras.Count == 0) Assert.Skip("no camera on this PC");
        var rates = await CameraDevices.FrameRatesAsync(cameras[0].Id, TestContext.Current.CancellationToken);
        Assert.All(rates, rate => Assert.InRange(rate, 0.01, 60.5));
        Assert.Equal(rates.Order(), rates);
    }

    [Fact]
    public void AMissingCameraFailsQuietly()
    {
        using var camera = new CameraInput(@"\\?\USB#VID_0000&PID_0000#nothing", "Nowhere");
        camera.Start();
        Assert.True(TestSupport.Eventually(() => camera.State.Status == VideoInputStatus.Failed, 15));
        Assert.Null(camera.LatestFrame());
    }

    [Fact]
    public async Task ARealCameraDeliversOpaqueFrames()
    {
        var cameras = await CameraDevices.ListAsync(TestContext.Current.CancellationToken);
        if (cameras.Count == 0) Assert.Skip("no camera on this PC");
        if (Environment.GetEnvironmentVariable("MXU_VIDEOIO_CAMERA_TEST") != "1") Assert.Skip("set MXU_VIDEOIO_CAMERA_TEST=1 to let this test turn the camera on");
        using var camera = new CameraInput(cameras[0].Id, cameras[0].Name);
        await camera.StartAsync();
        Assert.True(TestSupport.Eventually(() => camera.LatestFrame() is not null, 10), $"no frame; state {camera.State}");
        var frame = camera.LatestFrame()!;
        Assert.True(frame.Width > 0 && frame.Height > 0);
        Assert.Equal(255, frame.Pixels.Span[3]);
        Assert.Equal(VideoInputStatus.Receiving, camera.State.Status);
    }
}
