using NAudio.Wave;

namespace MxU.Slides.Audio.Decoding;

/// <summary>
/// Reads an audio file as stereo float at the rate the engine wants, between a start and
/// an end frame. WAV and AIFF are read directly (no codecs needed, so they work on any
/// machine); MP3, AAC/M4A, FLAC, WMA and the rest go through Windows Media Foundation.
/// Mono is copied to both sides, more than two channels fold down to stereo.
/// One thread at a time. The Windows stand-in for AVAudioFile + AVAudioConverter.
/// </summary>
internal sealed class AudioFileDecoder : IDisposable
{
    private const int ChunkFrames = 4096;

    private readonly WaveStream reader;
    private readonly ISampleProvider samples;
    private readonly SincResampler? resampler;
    private readonly float[] interleaved;
    private readonly float[][] folded = [new float[ChunkFrames], new float[ChunkFrames]];
    private readonly float[][] pending;
    private int pendingStart;
    private int pendingEnd;
    private long position;
    private long endFrame;
    private bool sourceDone;
    private bool flushed;

    private AudioFileDecoder(string path, WaveStream reader, int targetRate)
    {
        Path = path;
        this.reader = reader;
        samples = reader.ToSampleProvider();
        SourceRate = reader.WaveFormat.SampleRate;
        SourceChannels = Math.Max(1, reader.WaveFormat.Channels);
        TargetRate = targetRate;
        var blockAlign = Math.Max(1, reader.WaveFormat.BlockAlign);
        SourceFrames = Math.Max(0, reader.Length / blockAlign);
        interleaved = new float[ChunkFrames * SourceChannels];
        if (SourceRate != targetRate) resampler = new SincResampler(SourceRate, targetRate, 2);
        var room = resampler?.MaxOutput(ChunkFrames) ?? ChunkFrames;
        pending = [new float[room * 2 + 64], new float[room * 2 + 64]];
        endFrame = SourceFrames;
    }

    public string Path { get; }

    public int SourceRate { get; }

    public int SourceChannels { get; }

    public int TargetRate { get; }

    /// <summary>The file's length in its own frames (Media Foundation's figure is from the header, so close, not exact).</summary>
    public long SourceFrames { get; }

    public double Duration => SourceRate > 0 ? SourceFrames / (double)SourceRate : 0;

    /// <summary>Opens a file; throws <see cref="AudioEngineException"/> when it can't be read.</summary>
    public static AudioFileDecoder Open(string path, int targetRate)
    {
        if (!File.Exists(path)) throw AudioEngineException.Unreadable(path, "the file is missing");
        WaveStream? reader = null;
        try
        {
            reader = OpenReader(path);
            return new AudioFileDecoder(path, reader, targetRate);
        }
        catch (AudioEngineException)
        {
            reader?.Dispose();
            throw;
        }
        catch (Exception error)
        {
            reader?.Dispose();
            throw AudioEngineException.Unreadable(path, error.Message);
        }
    }

    /// <summary>The file's length in seconds, or null when it can't be read.</summary>
    public static double? ProbeDuration(string path)
    {
        try
        {
            using var reader = OpenReader(path);
            var seconds = reader.TotalTime.TotalSeconds;
            return seconds > 0 && double.IsFinite(seconds) ? seconds : null;
        }
        catch
        {
            return null;
        }
    }

    private static WaveStream OpenReader(string path)
    {
        var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".wav" or ".wave")
        {
            var wave = new WaveFileReader(path);
            if (IsPlainPcmOrFloat(wave.WaveFormat)) return wave;
            wave.Dispose();
        }
        else if (extension is ".aif" or ".aiff" or ".aifc")
        {
            return new AiffFileReader(path);
        }
        return new NAudio.Wave.MediaFoundationReader(path, new NAudio.Wave.MediaFoundationReader.MediaFoundationReaderSettings
        {
            RequestFloatOutput = true,
        });
    }

    private static bool IsPlainPcmOrFloat(WaveFormat format) => format.Encoding switch
    {
        WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat => true,
        WaveFormatEncoding.Extensible => format is WaveFormatExtensible extensible
            && (extensible.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_PCM
                || extensible.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT),
        _ => false,
    };

    /// <summary>
    /// Reads from <paramref name="startFrame"/> up to (not including) <paramref name="endSourceFrame"/>,
    /// both in the file's own frames.
    /// </summary>
    public void Seek(long startFrame, long? endSourceFrame = null)
    {
        startFrame = Math.Clamp(startFrame, 0, Math.Max(0, SourceFrames));
        endFrame = Math.Max(startFrame, endSourceFrame ?? long.MaxValue);
        pendingStart = pendingEnd = 0;
        sourceDone = false;
        flushed = false;
        if (resampler is null)
        {
            SeekReader(startFrame);
            position = startFrame;
            return;
        }
        // Feed the resampler a little real audio from before the start, so the first
        // output samples aren't softened by the filter seeing silence on its left.
        const int historyFrames = 32;
        var historyStart = Math.Max(0, startFrame - historyFrames);
        SeekReader(historyStart);
        position = historyStart;
        var history = (int)(startFrame - historyStart);
        if (history > 0)
        {
            var got = ReadSource(history);
            resampler.Prime(folded, 0, got);
        }
        else
        {
            resampler.Reset();
        }
        position = startFrame;
    }

    private void SeekReader(long frame)
    {
        var blockAlign = Math.Max(1, reader.WaveFormat.BlockAlign);
        reader.Position = frame * blockAlign;
    }

    /// <summary>The next frames as stereo at the target rate; fewer than asked (0) at the end.</summary>
    public int Read(float[] left, float[] right, int offset, int count)
    {
        var done = 0;
        while (done < count)
        {
            if (pendingStart < pendingEnd)
            {
                var take = Math.Min(count - done, pendingEnd - pendingStart);
                Array.Copy(pending[0], pendingStart, left, offset + done, take);
                Array.Copy(pending[1], pendingStart, right, offset + done, take);
                pendingStart += take;
                done += take;
                continue;
            }
            if (!Refill()) break;
        }
        return done;
    }

    // Decodes the next chunk into `pending`; false at the end.
    private bool Refill()
    {
        pendingStart = pendingEnd = 0;
        if (!sourceDone)
        {
            var want = (int)Math.Min(ChunkFrames, Math.Max(0, endFrame - position));
            var got = want > 0 ? ReadSource(want) : 0;
            if (got == 0) sourceDone = true;
            if (got > 0)
            {
                if (resampler is null)
                {
                    Array.Copy(folded[0], pending[0], got);
                    Array.Copy(folded[1], pending[1], got);
                    pendingEnd = got;
                }
                else
                {
                    pendingEnd = resampler.Process(folded, 0, got, pending, 0);
                }
                return true;
            }
        }
        if (resampler is not null && !flushed)
        {
            flushed = true;
            pendingEnd = resampler.Flush(pending, 0);
            return pendingEnd > 0;
        }
        return false;
    }

    // Reads up to `frames` source frames into `folded` (stereo); returns how many.
    private int ReadSource(int frames)
    {
        var channels = SourceChannels;
        var wanted = frames * channels;
        var got = 0;
        while (got < wanted)
        {
            int read;
            try
            {
                read = samples.Read(interleaved, got, wanted - got);
            }
            catch
            {
                break;
            }
            if (read <= 0) break;
            got += read;
        }
        var frameCount = got / channels;
        Fold(interleaved, frameCount, channels, folded[0], folded[1]);
        position += frameCount;
        return frameCount;
    }

    /// <summary>Interleaved → stereo: mono to both sides, 5.1 by the ITU fold, other layouts even/odd.</summary>
    internal static void Fold(float[] source, int frames, int channels, float[] left, float[] right)
    {
        switch (channels)
        {
            case 1:
                for (var frame = 0; frame < frames; frame++) left[frame] = right[frame] = source[frame];
                break;
            case 2:
                for (var frame = 0; frame < frames; frame++)
                {
                    left[frame] = source[frame * 2];
                    right[frame] = source[frame * 2 + 1];
                }
                break;
            case 6:
                // FL FR FC LFE BL BR: centre and surrounds at -3 dB, LFE left out.
                const float minus3 = 0.70710677f;
                for (var frame = 0; frame < frames; frame++)
                {
                    var at = frame * 6;
                    var centre = source[at + 2] * minus3;
                    left[frame] = source[at] + centre + source[at + 4] * minus3;
                    right[frame] = source[at + 1] + centre + source[at + 5] * minus3;
                }
                break;
            default:
                var scale = 1 / MathF.Sqrt(channels / 2f);
                for (var frame = 0; frame < frames; frame++)
                {
                    float l = 0, r = 0;
                    for (var channel = 0; channel < channels; channel++)
                    {
                        if (channel % 2 == 0) l += source[frame * channels + channel];
                        else r += source[frame * channels + channel];
                    }
                    left[frame] = l * scale;
                    right[frame] = r * scale;
                }
                break;
        }
    }

    public void Dispose() => reader.Dispose();
}
