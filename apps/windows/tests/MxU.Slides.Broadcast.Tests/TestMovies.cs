using MxU.Slides.Broadcast.Media;
using MxU.Slides.Broadcast.Recording;

namespace MxU.Slides.Broadcast.Tests;

/// <summary>Short MP4s made with the app's own recorder (software encoder), for tests that need a real video file.</summary>
internal static class TestMovies
{
    /// <summary>
    /// <paramref name="frames"/> pictures at 30 fps (a square that moves), with a 440 Hz tone when
    /// <paramref name="withAudio"/>. Returns the finished file's path.
    /// </summary>
    public static async Task<string> WriteAsync(string path, int frames, bool withAudio = false, int width = 160, int height = 96)
    {
        var recorder = new Recorder(path, new RecordingConfiguration(RecordingCodec.H264, width, height, 30, IncludesAudio: withAudio)
        {
            Encoder = EncoderPreference.Software,
        });
        const double start = 5000;
        long sample = 0;
        for (var frame = 0; frame < frames; frame++)
        {
            var at = start + frame / 30.0;
            // The recorder keeps a short queue; give it time so no picture is dropped.
            for (var tries = 0; tries < 200 && !recorder.Append(Synthetic.VideoFrame(width, height, frame, at)); tries++)
                await Task.Delay(5, TestContext.Current.CancellationToken);
            if (withAudio)
            {
                recorder.AppendAudio(Synthetic.AudioBuffer(48_000, 2, 1600, sample, at));
                sample += 1600;
            }
            await Task.Delay(4, TestContext.Current.CancellationToken);
        }
        await recorder.FinishAsync();
        Assert.Equal(RecorderStateKind.Finished, recorder.Status.State.Kind);
        return recorder.FilePath;
    }
}
