using MxU.Slides.Core.Automation;
using MxU.Slides.Audio.Decoding;

namespace MxU.Slides.Audio.Multitrack;

/// <summary>
/// Decodes one stem onto the song's clock: silence before its <c>at</c>, the file from its
/// in point for its duration (shaped by its envelope), silence after. It fills the stem's
/// ring a little at a time on the engine's tick, and when the ring reports frames the
/// audio thread already played as silence (it starved), it skips exactly that many so
/// every stem stays sample-locked. Port of StemReader.swift.
/// </summary>
internal sealed class StemReader : IDisposable
{
    private const int Chunk = 4096;
    private static readonly float[] Zeros = new float[Chunk];

    private readonly AudioFileDecoder? decoder;
    private readonly ClipEnvelope? envelope;
    private readonly double sampleRate;
    private readonly long audioStart;
    private readonly float[] decodedLeft = new float[Chunk];
    private readonly float[] decodedRight = new float[Chunk];
    private int decodedLength;
    private int decodedOffset;
    private long songFrame;

    public StemReader(MultitrackStem stem, double sampleRate, long startFrame, int capacity)
    {
        Ring = new StemRing(capacity);
        envelope = stem.Envelope;
        this.sampleRate = sampleRate;
        songFrame = startFrame;
        audioStart = (long)Math.Round(stem.At * sampleRate, MidpointRounding.AwayFromZero);
        try
        {
            decoder = AudioFileDecoder.Open(stem.Path, (int)Math.Round(sampleRate));
        }
        catch (Exception error)
        {
            Failure = $"{stem.Path}: {error.Message}";
            AudioEnd = audioStart;
            return;
        }
        var fileRate = (double)decoder.SourceRate;
        var inFrames = (long)Math.Round((stem.InPoint ?? 0) * fileRate, MidpointRounding.AwayFromZero);
        var playable = stem.Duration is { } duration
            ? (long)Math.Round(duration * fileRate, MidpointRounding.AwayFromZero)
            : decoder.SourceFrames - inFrames;
        var fileEndFrame = Math.Min(decoder.SourceFrames, inFrames + Math.Max(0, playable));
        AudioEnd = audioStart + (long)Math.Round((fileEndFrame - inFrames) / fileRate * sampleRate, MidpointRounding.AwayFromZero);
        var into = Math.Max(0, startFrame - audioStart);
        decoder.Seek(Math.Min(fileEndFrame, inFrames + (long)Math.Round(into / sampleRate * fileRate, MidpointRounding.AwayFromZero)), fileEndFrame);
    }

    public StemRing Ring { get; }

    /// <summary>The song frame where this stem's audio ends.</summary>
    public long AudioEnd { get; }

    /// <summary>Why the file couldn't be opened (the stem then plays silence).</summary>
    public string? Failure { get; }

    /// <summary>Tops the ring up (to <paramref name="limit"/> frames buffered); returns frames produced.</summary>
    public int Fill(int limit = int.MaxValue)
    {
        var produced = 0;
        while (true)
        {
            var drop = Ring.FramesToDrop;
            var wanted = drop > 0 ? drop : Math.Min(Ring.Space, limit - Ring.Buffered);
            if (wanted < (drop > 0 ? 1 : 1024)) break;
            var count = Math.Min(Chunk, wanted);
            Produce(count, dropping: drop > 0);
            produced += count;
        }
        return produced;
    }

    private void Produce(int count, bool dropping)
    {
        var done = 0;
        while (done < count)
        {
            var frame = songFrame + done;
            float[] left, right;
            int offset;
            int run;
            if (frame < audioStart || frame >= AudioEnd)
            {
                var edge = frame < audioStart ? audioStart : long.MaxValue;
                run = (int)Math.Min(count - done, edge - frame);
                (left, right, offset) = (Zeros, Zeros, 0);
            }
            else
            {
                if (decodedOffset >= decodedLength && !DecodeMore())
                {
                    run = (int)Math.Min(Math.Min(count - done, AudioEnd - frame), Chunk);
                    Emit(Zeros, Zeros, 0, run, dropping);
                    done += run;
                    continue;
                }
                run = (int)Math.Min(Math.Min(count - done, decodedLength - decodedOffset), Math.Min(AudioEnd - frame, Chunk));
                if (envelope is not null && !dropping)
                    envelope.Apply([decodedLeft, decodedRight], decodedOffset, run, (frame - audioStart) / sampleRate, sampleRate,
                        (AudioEnd - audioStart) / sampleRate);
                (left, right, offset) = (decodedLeft, decodedRight, decodedOffset);
                decodedOffset += run;
            }
            run = Math.Min(run, Chunk);
            Emit(left, right, offset, run, dropping);
            done += run;
        }
        songFrame += count;
    }

    private void Emit(float[] left, float[] right, int offset, int run, bool dropping)
    {
        if (dropping) Ring.Drop(run);
        else Ring.Write(left.AsSpan(offset, run), right.AsSpan(offset, run), run);
    }

    private bool DecodeMore()
    {
        if (decoder is null) return false;
        decodedOffset = 0;
        decodedLength = decoder.Read(decodedLeft, decodedRight, 0, Chunk);
        return decodedLength > 0;
    }

    public void Dispose() => decoder?.Dispose();
}
