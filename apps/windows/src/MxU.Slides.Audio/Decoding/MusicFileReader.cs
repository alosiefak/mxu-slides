namespace MxU.Slides.Audio.Decoding;

/// <summary>
/// Reads a music file from start to end as stereo sound at one sample rate, for code outside the
/// audio engine that needs the samples themselves rather than to play them: the opener maker
/// (Phase 13) reads a montage's track once to find its beats and once more to put it in the
/// video. It's the engine's own <see cref="AudioFileDecoder"/> (WAV and AIFF read directly; MP3,
/// M4A, FLAC and the rest through Windows), so anything the Music tab plays can be read. Use from
/// one thread.
/// </summary>
public sealed class MusicFileReader : IDisposable
{
    private readonly AudioFileDecoder decoder;
    private float[] left = [];
    private float[] right = [];

    private MusicFileReader(AudioFileDecoder decoder) => this.decoder = decoder;

    /// <summary>The rate the sound comes out at.</summary>
    public int SampleRate => decoder.TargetRate;

    /// <summary>The file's length (from its header, so close rather than exact for MP3 and the like).</summary>
    public double Seconds => decoder.Duration;

    /// <exception cref="IOException">The file is missing or isn't sound Windows can read; the message says why.</exception>
    public static MusicFileReader Open(string path, int sampleRate = 48_000)
    {
        try
        {
            var decoder = AudioFileDecoder.Open(path, sampleRate);
            // From the very start to wherever the sound really ends (a header's length can be a little short).
            decoder.Seek(0);
            return new MusicFileReader(decoder);
        }
        catch (AudioEngineException e)
        {
            throw new IOException(e.Message, e);
        }
    }

    /// <summary>
    /// The next sound, left and right in turn, filling as much of <paramref name="interleavedStereo"/>
    /// as there is left to read. Returns how many frames (pairs) were written; 0 at the end.
    /// </summary>
    public int Read(Span<float> interleavedStereo)
    {
        var frames = interleavedStereo.Length / 2;
        if (left.Length < frames) (left, right) = (new float[frames], new float[frames]);
        var read = decoder.Read(left, right, 0, frames);
        for (var i = 0; i < read; i++)
        {
            interleavedStereo[i * 2] = left[i];
            interleavedStereo[i * 2 + 1] = right[i];
        }
        return read;
    }

    public void Dispose() => decoder.Dispose();
}
