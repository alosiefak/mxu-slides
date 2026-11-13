using MxU.Slides.Broadcast.Media;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Motion;

namespace MxU.Slides.Broadcast.Tests;

/// <summary>
/// The two things Phase 13's montages added to the drawn-video pipeline: a sound track in
/// RenderedVideoWriter's MP4, and VideoClipReader, which reads a clip's pictures in order.
/// </summary>
[Collection("Transcoding")]
public sealed class RenderedSoundTests : IDisposable
{
    private const int W = 192, H = 108, Fps = 30;
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "RenderedSound-" + Guid.NewGuid().ToString("N"));

    public RenderedSoundTests() => Directory.CreateDirectory(scratch);

    public void Dispose()
    {
        try { Directory.Delete(scratch, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A video whose every frame is one flat grey, darker to lighter (frame i has brightness 40 + 3i), with a tone if asked.</summary>
    private string Greys(string name, int frames, int? soundRate)
    {
        var path = Path.Combine(scratch, name);
        using var writer = new RenderedVideoWriter(path, W, H, Fps, LoopTiming.Bitrate(W, H, Fps), soundRate);
        var picture = new byte[W * H * 3 / 2];
        long sounded = 0;
        for (var i = 0; i < frames; i++)
        {
            picture.AsSpan(0, W * H).Fill((byte)(40 + 3 * i));
            picture.AsSpan(W * H).Fill(128);
            writer.Write(picture, i);
            if (soundRate is { } rate)
            {
                // The stretch of sound that plays during this frame.
                var upTo = (i + 1L) * rate / Fps;
                var sound = new float[(upTo - sounded) * 2];
                for (var s = 0; s < sound.Length / 2; s++)
                    sound[s * 2] = sound[s * 2 + 1] = 0.4f * MathF.Sin(2 * MathF.PI * 440 * (sounded + s) / rate);
                sounded = upTo;
                writer.WriteAudio(sound);
            }
        }
        writer.Finish();
        return path;
    }

    [Theory]
    [InlineData(48_000)]
    [InlineData(44_100)]
    [InlineData(22_050)] // not a rate AAC takes here: converted on the way in
    public void ADrawnVideoCanCarrySound(int rate)
    {
        var path = Greys($"sound-{rate}.mp4", 60, rate);
        var probe = MediaImportProbe.ProbeVideo(path);
        Assert.Equal((MediaFileStatus.Ready, true, true), (probe.FileStatus, probe.HasVideo, probe.HasAudio));
        // Two seconds of each: the sound is neither cut short nor left running on.
        Assert.Equal(2, probe.DurationSeconds ?? 0, 1);
    }

    [Fact]
    public void WithoutSoundTheFileIsAsItAlwaysWas()
    {
        var path = Greys("silent.mp4", 30, null);
        var probe = MediaImportProbe.ProbeVideo(path);
        Assert.Equal((MediaFileStatus.Ready, false), (probe.FileStatus, probe.HasAudio));
        Assert.Equal(1, probe.DurationSeconds ?? 0, 2);
    }

    [Fact]
    public void AClipsPicturesAreReadInOrderByTheirTime()
    {
        var path = Greys("clip.mp4", 30, 48_000);
        using var reader = new VideoClipReader(path);
        Assert.Equal((W, H), (reader.Width, reader.Height));
        Assert.Equal(1, reader.Seconds, 1);

        // The grey of the picture showing at a moment (H.264 keeps a flat grey within a level or two).
        int GreyAt(double seconds)
        {
            var pixels = reader.FrameAt(seconds, out var width, out var height);
            Assert.NotNull(pixels);
            Assert.Equal((W, H), (width, height));
            return pixels[(H / 2 * W + W / 2) * 4 + 1];
        }
        static int Expected(int frame) => (int)Math.Round((40 + 3 * frame - 16) * 255 / 219.0);
        Assert.InRange(GreyAt(0), Expected(0) - 4, Expected(0) + 4);
        Assert.InRange(GreyAt(10.5 / Fps), Expected(10) - 4, Expected(10) + 4);
        // The same moment again, and a later one.
        Assert.InRange(GreyAt(10.5 / Fps), Expected(10) - 4, Expected(10) + 4);
        Assert.InRange(GreyAt(20.5 / Fps), Expected(20) - 4, Expected(20) + 4);
        // Going back starts the clip again; past its end the last picture stays.
        Assert.InRange(GreyAt(3.5 / Fps), Expected(3) - 4, Expected(3) + 4);
        Assert.InRange(GreyAt(5), Expected(29) - 4, Expected(29) + 4);
        Assert.InRange(GreyAt(9), Expected(29) - 4, Expected(29) + 4);
    }

    [Fact]
    public void AFileThatIsNotAVideoSaysWhy()
    {
        var path = Path.Combine(scratch, "not-a-video.mp4");
        File.WriteAllText(path, "nothing to see");
        Assert.Throws<IOException>(() => new VideoClipReader(path));
    }
}
