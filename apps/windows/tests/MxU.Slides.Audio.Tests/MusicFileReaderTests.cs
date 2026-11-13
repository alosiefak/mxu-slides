using MxU.Slides.Audio.Decoding;

namespace MxU.Slides.Audio.Tests;

/// <summary>
/// MusicFileReader, the public way to read a music file as plain stereo samples (the opener
/// maker reads a montage's track with it, to find its beats and to put it in the video).
/// </summary>
public sealed class MusicFileReaderTests
{
    [Fact]
    public void AFileIsReadFromStartToEndAsStereo()
    {
        // Two seconds at 44.1 kHz: a rising ramp on the left, silence on the right.
        var path = TestAudio.Wav("music-reader", 44_100, 2, 2, i => i / 88_200f, _ => 0);
        using var reader = MusicFileReader.Open(path, 44_100);
        Assert.Equal(44_100, reader.SampleRate);
        Assert.Equal(2, reader.Seconds, 3);

        var all = new List<float>();
        var piece = new float[1000 * 2];
        int read;
        while ((read = reader.Read(piece)) > 0) all.AddRange(piece.AsSpan(0, read * 2));
        Assert.Equal(88_200 * 2, all.Count);
        // Left and right in turn: the ramp, then nothing.
        Assert.Equal(0.5f, all[44_100 * 2], 3);
        Assert.Equal(0, all[44_100 * 2 + 1]);
        Assert.Equal(0, reader.Read(piece));
    }

    [Fact]
    public void AnotherRateIsConvertedAndAMonoFileGoesToBothSides()
    {
        var path = TestAudio.Wav("music-reader-mono", 44_100, 1, 1, i => MathF.Sin(2 * MathF.PI * 440 * i / 44_100f) * 0.5f);
        using var reader = MusicFileReader.Open(path, 22_050);
        var all = new float[30_000 * 2];
        var frames = 0;
        int read;
        while ((read = reader.Read(all.AsSpan(frames * 2))) > 0) frames += read;
        // One second at the new rate (give or take the converter's few samples at each end), the same on both sides.
        Assert.InRange(frames, 22_000, 22_100);
        Assert.Equal(all[10_000], all[10_001], 5);
        Assert.InRange(all.Take(frames * 2).Max(), 0.45f, 0.55f);
    }

    [Fact]
    public void AFileThatIsNotSoundSaysWhy()
    {
        var path = Path.Combine(Path.GetTempPath(), $"not-music-{Guid.NewGuid():N}.wav");
        File.WriteAllText(path, "this is not a wave file");
        try
        {
            Assert.Throws<IOException>(() => MusicFileReader.Open(path));
            Assert.Throws<IOException>(() => MusicFileReader.Open(path + ".missing"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
