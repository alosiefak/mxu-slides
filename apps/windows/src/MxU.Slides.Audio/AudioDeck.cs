using MxU.Slides.Audio.Decoding;
using MxU.Slides.Audio.Multitrack;

namespace MxU.Slides.Audio;

/// <summary>
/// One of a bus's two players ("decks": crossfades play the next track on the other one).
/// A deck owns a decoder and a ring of decoded audio about two seconds deep: loading
/// decodes the first two seconds straight away, then the engine's fill thread keeps the
/// ring topped up while the audio thread plays from it. Port of AudioDeck.swift, where
/// AVAudioPlayerNode plus scheduled buffers did the same job.
/// <para>
/// Threading: every field except the decoding <see cref="Stream"/>'s insides belongs to the
/// engine's render gate; the decoder is only touched under its stream's own lock.
/// </para>
/// </summary>
internal sealed class AudioDeck
{
    public const int GraphRate = 48_000;
    private const int RingFrames = 2 * GraphRate;
    private const int FillChunk = 4096;

    /// <summary>A loaded file: its decoder and the ring it decodes into.</summary>
    internal sealed class Stream(AudioFileDecoder decoder, StemRing ring, AudioClipShape? shape, double startSeconds)
    {
        public readonly Lock Gate = new();
        public readonly AudioFileDecoder Decoder = decoder;
        public readonly StemRing Ring = ring;
        public readonly AudioClipShape? Shape = shape;
        public readonly double StartSeconds = startSeconds;
        public long Decoded;
        public volatile bool DecodedToEnd;
        public bool Disposed;
        private readonly float[] left = new float[FillChunk];
        private readonly float[] right = new float[FillChunk];

        /// <summary>Decodes until the ring is full or the file ends. Safe to call from any thread.</summary>
        public void Fill(int limit = int.MaxValue)
        {
            lock (Gate)
            {
                while (!Disposed && !DecodedToEnd)
                {
                    var room = Math.Min(Ring.Space, limit - Ring.Buffered);
                    if (room < Math.Min(FillChunk, 1024) && Ring.Buffered > 0) break;
                    if (room <= 0) break;
                    var want = Math.Min(FillChunk, room);
                    var got = Decoder.Read(left, right, 0, want);
                    if (got == 0)
                    {
                        DecodedToEnd = true;
                        break;
                    }
                    if (Shape is { } shape)
                    {
                        // The clip's fades are applied as it is read, on the file's own clock.
                        var t0 = StartSeconds + Decoded / (double)GraphRate - shape.Start;
                        shape.Envelope.Apply([left, right], 0, got, t0, GraphRate, shape.End - shape.Start);
                    }
                    Ring.Write(left.AsSpan(0, got), right.AsSpan(0, got), got);
                    Decoded += got;
                }
            }
        }

        public void Dispose()
        {
            lock (Gate)
            {
                if (Disposed) return;
                Disposed = true;
                Decoder.Dispose();
            }
        }
    }

    /// <summary>Everything a load works out before the deck switches to it.</summary>
    internal sealed record Prepared(string Path, Stream Stream, double Duration, double TrimStart, double? TrimUntil, double ScheduleStart);

    public string? Path { get; private set; }

    /// <summary>The track's end in file seconds (its out point when trimmed).</summary>
    public double Duration { get; private set; }

    public double TrimStart { get; private set; }

    public double? TrimUntil { get; private set; }

    public AudioClipShape? Shape { get; set; }

    /// <summary>Bumped on every load and clear, so late callbacks about an old track are ignored.</summary>
    public int Generation { get; private set; }

    public Stream? Current { get; private set; }

    /// <summary>Taking audio from the ring (false when paused, stopped or ended).</summary>
    public bool Playing { get; set; }

    /// <summary>Reached the end of its file; reported once per load.</summary>
    public bool Ended { get; private set; }

    private bool endReported;

    /// <summary>A start scheduled for a host-clock time; null once started.</summary>
    public double? StartHostTime { get; set; }

    /// <summary>The gain the deck should be at by the end of the current slice.</summary>
    public float Volume { get; set; } = 1;

    /// <summary>The gain at the end of the last slice: the next slice ramps from here to <see cref="Volume"/>.</summary>
    public float AppliedVolume { get; set; } = 1;

    private double scheduleStart;
    private long consumed;

    public bool IsPlayingNow => Playing && !Ended && Current is not null;

    /// <summary>Where playback is, in file seconds.</summary>
    public double Elapsed => scheduleStart + consumed / (double)GraphRate;

    /// <summary>
    /// Opens <paramref name="path"/> and decodes its first two seconds from
    /// <paramref name="startTime"/>, stopping at <paramref name="endTime"/>. Runs on the
    /// control thread with no engine gate held. Throws <see cref="AudioEngineException"/>.
    /// </summary>
    public static Prepared Prepare(string path, double startTime, double windowStart, double? endTime, AudioClipShape? shape)
    {
        var decoder = AudioFileDecoder.Open(path, GraphRate);
        try
        {
            var fileRate = (double)decoder.SourceRate;
            var total = decoder.SourceFrames / fileRate;
            var startFrame = (long)Math.Floor(Math.Clamp(startTime, 0, Math.Max(0, total)) * fileRate);
            var cappedEnd = endTime is { } end
                ? Math.Min(decoder.SourceFrames, (long)Math.Ceiling(Math.Clamp(end, 0, Math.Max(0, total)) * fileRate))
                : decoder.SourceFrames;
            if (cappedEnd - startFrame <= 0) throw AudioEngineException.EmptySchedule(path);
            var duration = cappedEnd / fileRate;
            var trimStart = Math.Min(Math.Clamp(windowStart, 0, Math.Max(0, total)), duration);
            double? trimUntil = endTime is null ? null : duration;
            var scheduleStart = startFrame / fileRate;
            decoder.Seek(startFrame, cappedEnd);
            var stream = new Stream(decoder, new StemRing(RingFrames), shape, scheduleStart);
            stream.Fill();
            if (stream.Decoded == 0) throw AudioEngineException.EmptySchedule(path);
            return new Prepared(path, stream, duration, trimStart, trimUntil, scheduleStart);
        }
        catch
        {
            decoder.Dispose();
            throw;
        }
    }

    /// <summary>Switches to a prepared load (stopped, at full volume). Render gate held. Returns the stream it replaced.</summary>
    public Stream? Install(Prepared prepared)
    {
        var old = Current;
        Generation++;
        Current = prepared.Stream;
        Path = prepared.Path;
        Duration = prepared.Duration;
        TrimStart = prepared.TrimStart;
        TrimUntil = prepared.TrimUntil;
        scheduleStart = prepared.ScheduleStart;
        consumed = 0;
        Playing = false;
        Ended = false;
        endReported = false;
        StartHostTime = null;
        Volume = AppliedVolume = 1;
        return old;
    }

    /// <summary>Empties the deck. Render gate held. Returns the stream to dispose (off the audio thread).</summary>
    public Stream? Clear()
    {
        var old = Current;
        Generation++;
        Current = null;
        Path = null;
        Duration = 0;
        TrimStart = 0;
        TrimUntil = null;
        Shape = null;
        scheduleStart = 0;
        consumed = 0;
        Playing = false;
        Ended = false;
        endReported = false;
        StartHostTime = null;
        Volume = AppliedVolume = 1;
        return old;
    }

    /// <summary>
    /// Mixes this deck's next <paramref name="count"/> frames into the bus at
    /// <paramref name="offset"/>, ramping its gain from where the last slice ended to
    /// <see cref="Volume"/>. Returns true when the deck just reached the end of its file.
    /// Audio thread, render gate held.
    /// </summary>
    public bool RenderSlice(float[] mixLeft, float[] mixRight, int offset, int count, double sliceHostTime,
        float[] scratchLeft, float[] scratchRight, out bool wantsFill)
    {
        wantsFill = false;
        var stream = Current;
        if (!Playing || Ended || stream is null)
        {
            AppliedVolume = Volume;
            return false;
        }
        var start = 0;
        if (StartHostTime is { } at)
        {
            if (sliceHostTime > 0)
            {
                var wait = (int)Math.Round((at - sliceHostTime) * GraphRate, MidpointRounding.AwayFromZero);
                if (wait >= count) return false;
                start = Math.Max(0, wait);
            }
            StartHostTime = null;
        }
        var want = count - start;
        var got = stream.Ring.ReadAvailable(scratchLeft.AsSpan(0, want), scratchRight.AsSpan(0, want), want);
        consumed += got;
        var from = AppliedVolume;
        var to = Volume;
        if (from == to)
        {
            if (to != 0)
                for (var index = 0; index < got; index++)
                {
                    mixLeft[offset + start + index] += scratchLeft[index] * to;
                    mixRight[offset + start + index] += scratchRight[index] * to;
                }
        }
        else
        {
            var step = (to - from) / count;
            for (var index = 0; index < got; index++)
            {
                var gain = from + step * (start + index + 1);
                mixLeft[offset + start + index] += scratchLeft[index] * gain;
                mixRight[offset + start + index] += scratchRight[index] * gain;
            }
        }
        AppliedVolume = to;
        if (stream.Ring.Buffered < RingFrames / 2) wantsFill = true;
        if (got < want && stream.DecodedToEnd && stream.Ring.Buffered == 0)
        {
            Ended = true;
            Playing = false;
            if (!endReported)
            {
                endReported = true;
                return true;
            }
        }
        return false;
    }
}
