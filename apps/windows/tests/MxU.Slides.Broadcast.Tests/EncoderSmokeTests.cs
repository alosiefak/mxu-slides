using MxU.Slides.Broadcast.Media;

namespace MxU.Slides.Broadcast.Tests;

/// <summary>The Media Foundation encoders work at all on this PC (software path, as on CI).</summary>
public sealed class EncoderSmokeTests
{
    [Fact]
    public void SoftwareH264EncodesKeyframeFirstWithParameterSets()
    {
        using var encoder = H264Encoder.Create(new H264Settings(320, 180, 30, 800), EncoderPreference.Software);
        Assert.False(encoder.IsHardware);
        var converter = new Nv12Converter(320, 180);
        var nv12 = new byte[converter.FrameSize];
        var bgra = Synthetic.Frame(320, 180, 0);
        var frames = new List<EncodedVideoFrame>();
        for (var i = 0; i < 10; i++)
        {
            converter.Convert(Synthetic.Frame(320, 180, i), 320, 180, 320 * 4, nv12);
            frames.AddRange(encoder.Encode(nv12, 100 + i / 30.0));
        }
        frames.AddRange(encoder.Drain());
        Assert.True(frames.Count >= 9, $"only {frames.Count} frames came out");
        Assert.True(frames[0].IsKeyframe);
        Assert.Equal(0x67, frames[0].ParameterSets.Sps[0] & 0x7F);
        Assert.Equal(100, frames[0].PresentationTime, 3);
        Assert.NotEmpty(bgra);
    }

    /// <summary>On a PC with a graphics-card encoder, the automatic choice uses it and it really encodes.</summary>
    [Fact]
    public void AutomaticEncoderProducesFramesWhicheverItPicks()
    {
        using var encoder = H264Encoder.Create(new H264Settings(640, 360, 30, 1500));
        var converter = new Nv12Converter(640, 360);
        var nv12 = new byte[converter.FrameSize];
        var frames = new List<EncodedVideoFrame>();
        for (var i = 0; i < 30; i++)
        {
            converter.Convert(Synthetic.Frame(640, 360, i), 640, 360, 640 * 4, nv12);
            frames.AddRange(encoder.Encode(nv12, 10 + i / 30.0));
            Thread.Sleep(33);
        }
        frames.AddRange(encoder.Drain());
        TestContext.Current.TestOutputHelper?.WriteLine($"encoder: {encoder.Name} hardware={encoder.IsHardware} frames={frames.Count}");
        Assert.True(frames.Count >= 25, $"{encoder.Name}: only {frames.Count} frames came out");
        Assert.True(frames[0].IsKeyframe, $"{encoder.Name}: first frame isn't a keyframe");
        for (var i = 1; i < frames.Count; i++)
            Assert.True(frames[i].PresentationTime > frames[i - 1].PresentationTime, "frames came out of order");
    }

    [Fact]
    public void AacEncodesATone()
    {
        using var encoder = AacEncoder.Create(48_000, 2);
        var frames = new List<EncodedAudioFrame>();
        for (var i = 0; i < 20; i++)
            frames.AddRange(encoder.Encode(Synthetic.Tone(48_000, 2, 1024, i * 1024), 50 + i * 1024 / 48_000.0));
        frames.AddRange(encoder.Drain());
        Assert.True(frames.Count >= 15, $"only {frames.Count} AAC frames");
        Assert.Equal([0x11, 0x90], encoder.Config.Bytes);
        Assert.Equal(50, frames[0].PresentationTime, 2);
    }
}
