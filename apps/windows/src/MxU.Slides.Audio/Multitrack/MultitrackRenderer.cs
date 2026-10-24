namespace MxU.Slides.Audio.Multitrack;

/// <summary>
/// One cue of a song: its stems' rings, which channel each plays on, where it starts on
/// the song, and (for a chase) the timecode instant it was anchored to. Also holds the
/// stems' meters, written by the audio thread. Port of MultitrackSession.
/// </summary>
internal sealed class MultitrackSession
{
    public const int MeterSlots = 5;

    public MultitrackSession(StemRing[] rings, IReadOnlyList<string> stemChannels, long startFrame,
        (long Frame, double HostTime)? anchor = null, MultitrackVarispeed? varispeed = null, long? entryFrames = null)
    {
        Rings = rings;
        StemChannels = stemChannels;
        StartFrame = startFrame;
        Anchor = anchor;
        Varispeed = varispeed;
        EntryFrames = entryFrames;
        Meters = new long[Math.Max(1, rings.Length * MeterSlots)];
    }

    public StemRing[] Rings { get; }

    public IReadOnlyList<string> StemChannels { get; }

    public long StartFrame { get; }

    /// <summary>The song frame that was playing at a host-clock time (timecode chase), so the renderer can line up.</summary>
    public (long Frame, double HostTime)? Anchor { get; }

    public MultitrackVarispeed? Varispeed { get; }

    /// <summary>Crossfade in over this many frames instead of the 10 ms click guard (a manual jump).</summary>
    public long? EntryFrames { get; }

    // Per stem: left peak, right peak, left squares, right squares (float bits), frames.
    public long[] Meters { get; }
}

/// <summary>
/// Everything the audio thread needs for one song, frozen: the session, its gain table,
/// output delays, and what happens at its end. The engine publishes a new one for every
/// change; the renderer picks it up at its next block and ramps gains across. Port of MultitrackProgram.
/// </summary>
internal sealed class MultitrackProgram(
    MultitrackSession session, MultitrackMatrix matrix, MultitrackDelays? delays = null,
    MultitrackHandover? handover = null, IReadOnlyList<MultitrackProgram>? companions = null)
{
    public MultitrackSession Session { get; } = session;
    public MultitrackMatrix Matrix { get; } = matrix;
    public float[] Gains { get; } = matrix.Gains.Length == 0 ? [0] : (float[])matrix.Gains.Clone();
    public MultitrackDelays? Delays { get; } = delays;
    public MultitrackHandover? Handover { get; } = handover;

    /// <summary>Fresh tables for songs still fading out, so a fader move reaches them too.</summary>
    public IReadOnlyList<MultitrackProgram> Companions { get; } = companions ?? [];
}

/// <summary>A gain shape over song frames: a fade in, a fade out, and a hard end (with a 10 ms ramp). Port of MultitrackShape.</summary>
internal struct MultitrackShape
{
    public enum Curve { Linear, EqualPower, Cosine }

    public long InFrom;
    public long InFrames;
    public Curve InCurve;
    public long FadeFrom = long.MaxValue;
    public long FadeFrames = 1;
    public Curve FadeCurve;
    public long End = long.MaxValue;
    public long EndRamp = 480;

    public MultitrackShape() { }

    public static MultitrackShape Flat => new();

    public readonly float Gain(long frame)
    {
        float gain = 1;
        if (InFrames > 0 && frame < InFrom + InFrames)
        {
            var t = Math.Max(frame - InFrom, 0) / (float)InFrames;
            gain *= Rise(t, InCurve);
        }
        if (frame >= FadeFrom)
        {
            var t = MathF.Min((frame - FadeFrom) / (float)Math.Max(FadeFrames, 1), 1);
            gain *= Rise(1 - t, FadeCurve);
        }
        if (frame >= End) gain = 0;
        else if (End != long.MaxValue && End - frame < EndRamp) gain *= (End - frame) / (float)EndRamp;
        return gain;
    }

    public readonly bool IsDone(long frame) => frame >= End || (FadeFrom != long.MaxValue && frame >= FadeFrom + FadeFrames);

    /// <summary>Frames until the next point where the shape changes character.</summary>
    public readonly long FramesToBreak(long frame)
    {
        var nearest = long.MaxValue;
        unchecked
        {
            foreach (var point in (ReadOnlySpan<long>)[InFrom + InFrames, FadeFrom, FadeFrom + FadeFrames, End - EndRamp, End])
                if (point > frame) nearest = Math.Min(nearest, point - frame);
        }
        return nearest;
    }

    /// <summary>True when any gain change falls in the next <paramref name="count"/> frames.</summary>
    public readonly bool Shapes(long frame, int count) =>
        (InFrames > 0 && frame < InFrom + InFrames) || frame + count > FadeFrom
        || (End != long.MaxValue && frame + count > End - EndRamp);

    private static float Rise(float t, Curve curve) => curve switch
    {
        Curve.EqualPower => MathF.Sin(t * MathF.PI / 2),
        Curve.Cosine => 0.5f * (1 - MathF.Cos(t * MathF.PI)),
        _ => t,
    };
}

/// <summary>At song frame <see cref="At"/>, hand over to <see cref="Next"/> (or stop), shaping both sides. Port of MultitrackHandover.</summary>
internal sealed class MultitrackHandover(MultitrackProgram? next, long at, MultitrackShape outgoing, MultitrackShape incoming)
{
    public MultitrackProgram? Next { get; } = next;
    public long At { get; } = at;
    public MultitrackShape Outgoing { get; } = outgoing;
    public MultitrackShape Incoming { get; } = incoming;
}

/// <summary>Per device channel delay lines (an output's lip-sync delay). Port of MultitrackDelays.</summary>
internal sealed class MultitrackDelays
{
    private readonly float[]?[] lines;
    private readonly int[] heads;

    public MultitrackDelays(int[] frames)
    {
        Frames = frames;
        lines = new float[frames.Length][];
        heads = new int[frames.Length];
        for (var channel = 0; channel < frames.Length; channel++)
            lines[channel] = frames[channel] > 0 ? new float[frames[channel]] : null;
    }

    public int[] Frames { get; }

    public void Process(float[][] outputs, int channels, int count)
    {
        for (var channel = 0; channel < Math.Min(channels, Frames.Length); channel++)
        {
            if (lines[channel] is not { } line) continue;
            var delay = Frames[channel];
            var samples = outputs[channel];
            var head = heads[channel];
            for (var frame = 0; frame < count; frame++)
            {
                var delayed = line[head];
                line[head] = samples[frame];
                samples[frame] = delayed;
                if (++head == delay) head = 0;
            }
            heads[channel] = head;
        }
    }
}

/// <summary>
/// The tracks' audio-thread mixer. The engine publishes programs and play/stop requests
/// through atomic fields; the renderer adopts them at the top of each block, never waiting
/// on a lock. It plays the current song sample-accurately, keeps up to two outgoing songs
/// sounding through crossfades and handovers, ramps gain tables when the mixer moves, and
/// writes each fed mix into the share for streams. Port of MultitrackRenderer.swift.
/// </summary>
internal sealed class MultitrackRenderer
{
    internal const int OutgoingSlots = 2;
    private const int ShapedSlice = 256;
    private static int MaxFeeds => TracksShare.Slots;

    // Published by the engine, read by the audio thread.
    private MultitrackProgram? wanted;
    private long playRequest;
    private double playHostTime;
    private long stopRequest;
    private double speed = 1;

    // Published by the audio thread, read by the engine.
    private MultitrackProgram? usingCurrent;
    private readonly MultitrackProgram?[] usingOutgoing = new MultitrackProgram?[OutgoingSlots];
    private long publishedFrame;
    private double publishedHostTime;
    private int publishedPlaying;
    private long handoversMade;

    // The audio thread's own state.
    private struct Voice
    {
        public MultitrackProgram Program;
        public long Frame;
        public MultitrackShape Shape;
        public MultitrackProgram? RampFrom;
    }

    private MultitrackProgram? current;
    private MultitrackShape currentShape = MultitrackShape.Flat;
    private MultitrackProgram? rampFrom;
    private readonly Voice?[] outgoing = new Voice?[OutgoingSlots];
    private MultitrackProgram? handedOverFrom;
    private long frame;
    private bool playing;
    private float envelope;
    private float envelopeTarget;
    private long seenPlay;
    private long seenStop;
    private readonly float[] scratchLeft;
    private readonly float[] scratchRight;
    private readonly float[] feedScratch;
    private readonly List<int> feedSlotList = new(MaxFeeds);

    public MultitrackRenderer(int deviceChannels, double sampleRate, int maxFrames = 4096, double fadeSeconds = 0.01, TracksShare? share = null)
    {
        Share = share;
        MaxFrames = maxFrames;
        DeviceChannels = deviceChannels;
        SampleRate = sampleRate;
        FadeFrames = Math.Max(1, (int)(sampleRate * fadeSeconds));
        scratchLeft = new float[maxFrames];
        scratchRight = new float[maxFrames];
        feedScratch = new float[maxFrames * 2 * MaxFeeds];
    }

    public int MaxFrames { get; }
    public int DeviceChannels { get; }
    public double SampleRate { get; }

    /// <summary>The click guard: how long a start, stop or cut ramps (10 ms).</summary>
    public int FadeFrames { get; }

    public TracksShare? Share { get; }

    public void Publish(MultitrackProgram? program) => Volatile.Write(ref wanted, program);

    /// <summary>Starts at <paramref name="hostTime"/> (host-clock seconds; 0 = at once).</summary>
    public void Play(double hostTime = 0)
    {
        Volatile.Write(ref playHostTime, hostTime);
        Interlocked.Increment(ref playRequest);
    }

    public void Stop() => Interlocked.Increment(ref stopRequest);

    /// <summary>Varispeed (1 = normal), when a chased session has a varispeed.</summary>
    public double Speed
    {
        get => Volatile.Read(ref speed);
        set => Volatile.Write(ref speed, value);
    }

    /// <summary>True while the audio thread may still read this program (current, wanted, or fading out).</summary>
    public bool IsHolding(MultitrackProgram program)
    {
        if (Volatile.Read(ref usingCurrent) == program || Volatile.Read(ref wanted) == program) return true;
        for (var slot = 0; slot < OutgoingSlots; slot++)
            if (Volatile.Read(ref usingOutgoing[slot]) == program) return true;
        return false;
    }

    public bool IsCurrent(MultitrackProgram program) => Volatile.Read(ref usingCurrent) == program;

    public long Handovers => Interlocked.Read(ref handoversMade);

    /// <summary>The song frame at the start of the last block, that block's host time, and whether it's playing.</summary>
    public (long Frame, double HostTime, bool Playing) Published =>
        (Interlocked.Read(ref publishedFrame), Volatile.Read(ref publishedHostTime), Volatile.Read(ref publishedPlaying) != 0);

    /// <summary>The song position (seconds) at a host-clock time, extrapolated from the last block.</summary>
    public double Position(double hostTime)
    {
        var state = Published;
        var seconds = state.Frame / SampleRate;
        if (state.Playing && state.HostTime > 0) seconds += hostTime - state.HostTime;
        return seconds;
    }

    /// <summary>Fills <paramref name="total"/> frames of each device channel. Audio thread.</summary>
    public void Render(int total, double hostTime, float[][] outputs)
    {
        for (var channel = 0; channel < DeviceChannels; channel++) Array.Clear(outputs[channel], 0, total);
        AdoptWanted(hostTime);
        var offset = 0;
        var playAsked = Interlocked.Read(ref playRequest);
        if (playAsked != seenPlay)
        {
            var start = Volatile.Read(ref playHostTime);
            var wait = start == 0 || hostTime == 0 ? 0 : (int)Math.Round((start - hostTime) * SampleRate, MidpointRounding.AwayFromZero);
            if (wait < total)
            {
                seenPlay = playAsked;
                if (!playing || envelopeTarget == 0)
                {
                    offset = playing ? 0 : Math.Max(0, wait);
                    playing = true;
                    envelopeTarget = 1;
                    // From the very top there's nothing to click against: start at full level.
                    envelope = frame <= 0 && envelope == 0 ? 1 : envelope;
                }
            }
        }
        var stopAsked = Interlocked.Read(ref stopRequest);
        if (stopAsked != seenStop)
        {
            seenStop = stopAsked;
            if (playing) envelopeTarget = 0;
        }
        Interlocked.Exchange(ref publishedFrame, frame - (playing ? offset : 0));
        Volatile.Write(ref publishedHostTime, hostTime);
        Volatile.Write(ref publishedPlaying, playing && envelopeTarget > 0 ? 1 : 0);

        try
        {
            if (current is not { } first) return;
            if (playing)
            {
                if (offset > 0 && Share is { } share)
                    foreach (var slot in first.Matrix.FeedSlots.Take(MaxFeeds)) share.WriteSilence(slot, offset);
                var done = offset;
                while (done < total && playing && current is { } program)
                {
                    var count = Math.Min(MaxFrames, total - done);
                    if (program.Handover is { } handover && handedOverFrom is null)
                    {
                        if (frame >= handover.At)
                        {
                            HandOver(handover);
                            continue;
                        }
                        count = (int)Math.Min(count, handover.At - frame);
                    }
                    if (Shaping(count)) count = Math.Min(count, ShapedSlice);
                    count = Math.Min(count, BreakFrames());
                    RenderSlice(count, done, outputs);
                    done += count;
                }
            }
            (current ?? first).Delays?.Process(outputs, DeviceChannels, total);
        }
        finally
        {
            rampFrom = null;
        }
    }

    private bool Shaping(int count)
    {
        if (envelope != envelopeTarget || currentShape.Shapes(frame, count)) return true;
        foreach (var voice in outgoing)
            if (voice is { } v && v.Shape.Shapes(v.Frame, count)) return true;
        return false;
    }

    private int BreakFrames()
    {
        var nearest = currentShape.FramesToBreak(frame);
        foreach (var voice in outgoing)
            if (voice is { } v) nearest = Math.Min(nearest, v.Shape.FramesToBreak(v.Frame));
        return (int)Math.Min(nearest, MaxFrames);
    }

    private void HandOver(MultitrackHandover handover)
    {
        if (current is not { } leaving) return;
        handedOverFrom = leaving;
        Interlocked.Increment(ref handoversMade);
        if (handover.Next is not { } next)
        {
            // Nothing next: the song has faded or ended; the transport stops here.
            envelopeTarget = 0;
            envelope = 0;
            playing = false;
            ClearOutgoing();
            return;
        }
        AddOutgoing(new Voice { Program = leaving, Frame = frame, Shape = currentShape });
        current = next;
        Volatile.Write(ref usingCurrent, next);
        frame = next.Session.StartFrame;
        currentShape = handover.Incoming;
    }

    private void AddOutgoing(Voice voice)
    {
        var slot = Array.FindIndex(outgoing, v => v is null);
        if (slot < 0)
        {
            // Both busy: the older one makes room.
            outgoing[0] = outgoing[1];
            Volatile.Write(ref usingOutgoing[0], outgoing[0]?.Program);
            slot = 1;
        }
        outgoing[slot] = voice;
        Volatile.Write(ref usingOutgoing[slot], voice.Program);
    }

    private void ClearOutgoing()
    {
        for (var slot = 0; slot < OutgoingSlots; slot++)
        {
            outgoing[slot] = null;
            Volatile.Write(ref usingOutgoing[slot], null);
        }
    }

    private void AdoptWanted(double hostTime)
    {
        var pointer = Volatile.Read(ref wanted);
        if (pointer == current) return;
        var previous = current;
        if (pointer is not { } program)
        {
            current = null;
            Volatile.Write(ref usingCurrent, null);
            playing = false;
            ClearOutgoing();
            return;
        }
        if (handedOverFrom is not null)
        {
            // Already handed over: the engine hasn't caught up yet.
            if (pointer == handedOverFrom) return;
            if (program.Handover?.Next is { } incoming && previous is not null && incoming.Session == previous.Session)
            {
                // A new table for the song we handed over from, carrying a new one for the song we're on.
                handedOverFrom = pointer;
                for (var slot = 0; slot < OutgoingSlots; slot++)
                {
                    if (outgoing[slot] is not { } voice || voice.Program.Session != program.Session) continue;
                    voice.Program = program;
                    outgoing[slot] = voice;
                    Volatile.Write(ref usingOutgoing[slot], pointer);
                }
                rampFrom = previous;
                current = incoming;
                Volatile.Write(ref usingCurrent, incoming);
                return;
            }
            handedOverFrom = null;
        }
        if (previous?.Session != program.Session)
        {
            // A new cue: crossfade from what's playing (10 ms, or the jump's entry time), or start clean.
            var crossfading = playing && envelopeTarget > 0 && previous is not null;
            var entry = program.Session.EntryFrames;
            if (crossfading && previous is not null)
            {
                var shape = currentShape;
                shape.FadeFrom = Math.Min(shape.FadeFrom, frame);
                shape.FadeFrames = entry ?? FadeFrames;
                shape.FadeCurve = entry is null ? MultitrackShape.Curve.Linear : MultitrackShape.Curve.EqualPower;
                AddOutgoing(new Voice { Program = previous, Frame = frame, Shape = shape });
            }
            frame = program.Session.StartFrame;
            if (program.Session.Anchor is { } anchor && hostTime > 0 && anchor.HostTime > 0)
            {
                // Chased: the anchor was earlier, so jump the stems ahead to where timecode is now.
                var since = hostTime - anchor.HostTime;
                var target = anchor.Frame + (long)Math.Round(since * SampleRate, MidpointRounding.AwayFromZero);
                if (target > frame)
                {
                    foreach (var ring in program.Session.Rings) ring.Skip((int)(target - frame));
                    frame = target;
                }
            }
            currentShape = crossfading
                ? new MultitrackShape
                {
                    InFrom = frame, InFrames = entry ?? FadeFrames,
                    InCurve = entry is null ? MultitrackShape.Curve.Linear : MultitrackShape.Curve.EqualPower,
                }
                : MultitrackShape.Flat;
            if (!crossfading) envelope = frame <= 0 ? 1 : 0;
        }
        else if (previous is not null)
        {
            // Same song, new table (a fader moved): ramp from the old gains.
            rampFrom = previous;
        }
        AdoptCompanions(program);
        var leaving = program.Handover?.Outgoing ?? MultitrackShape.Flat;
        currentShape.FadeFrom = leaving.FadeFrom;
        currentShape.FadeFrames = leaving.FadeFrames;
        currentShape.FadeCurve = leaving.FadeCurve;
        currentShape.End = leaving.End;
        current = program;
        Volatile.Write(ref usingCurrent, pointer);
    }

    private void AdoptCompanions(MultitrackProgram program)
    {
        foreach (var companion in program.Companions)
        {
            for (var slot = 0; slot < OutgoingSlots; slot++)
            {
                if (outgoing[slot] is not { } voice || voice.Program.Session != companion.Session) continue;
                voice.RampFrom = voice.Program;
                voice.Program = companion;
                outgoing[slot] = voice;
                Volatile.Write(ref usingOutgoing[slot], companion);
            }
        }
    }

    private void RenderSlice(int count, int offset, float[][] outputs)
    {
        if (current is not { } program) return;
        foreach (var slot in FeedSlots(program)) Array.Clear(feedScratch, slot * 2 * count, 2 * count);
        var startGain = envelope;
        var step = 1 / (float)FadeFrames;
        var endGain = envelopeTarget > envelope
            ? MathF.Min(envelopeTarget, envelope + step * count)
            : MathF.Max(envelopeTarget, envelope - step * count);
        for (var slot = 0; slot < OutgoingSlots; slot++)
        {
            if (outgoing[slot] is not { } voice) continue;
            var from = voice.Shape.Gain(voice.Frame);
            var to = voice.Shape.Gain(voice.Frame + count);
            Mix(voice.Program, count, offset, startGain * from, endGain * to, outputs, ref voice.Frame, voice.RampFrom);
            voice.RampFrom = null;
            if (voice.Shape.IsDone(voice.Frame))
            {
                outgoing[slot] = null;
                Volatile.Write(ref usingOutgoing[slot], null);
            }
            else
            {
                outgoing[slot] = voice;
            }
        }
        var fromGain = currentShape.Gain(frame);
        var toGain = currentShape.Gain(frame + count);
        Mix(program, count, offset, startGain * fromGain, endGain * toGain, outputs, ref frame, rampFrom);
        rampFrom = null;
        envelope = endGain;
        // (Asked again: an outgoing song that just finished no longer writes its own slots.)
        if (Share is { } share)
            foreach (var slot in FeedSlots(program))
                share.Write(slot, feedScratch.AsSpan(slot * 2 * count, count), feedScratch.AsSpan((slot * 2 + 1) * count, count), count);
        if (envelopeTarget == 0 && envelope == 0)
        {
            playing = false;
            ClearOutgoing();
        }
    }

    // The share slots this slice writes: the current song's, then any only an outgoing song feeds.
    private List<int> FeedSlots(MultitrackProgram program)
    {
        feedSlotList.Clear();
        foreach (var slot in program.Matrix.FeedSlots)
            if (slot < MaxFeeds && !feedSlotList.Contains(slot)) feedSlotList.Add(slot);
        foreach (var voice in outgoing)
        {
            if (voice is not { } v) continue;
            foreach (var slot in v.Program.Matrix.FeedSlots)
                if (slot < MaxFeeds && !feedSlotList.Contains(slot)) feedSlotList.Add(slot);
        }
        return feedSlotList;
    }

    private void Mix(MultitrackProgram program, int count, int offset, float envelopeFrom, float envelopeTo,
        float[][] outputs, ref long position, MultitrackProgram? rampFromProgram)
    {
        // Ramp from the old table only when it has the same shape.
        var from = rampFromProgram is { } old && old.Matrix.Destinations == program.Matrix.Destinations && old.Matrix.Stems == program.Matrix.Stems
            ? old : null;
        var session = program.Session;
        var matrix = program.Matrix;
        var destinations = matrix.Destinations;
        var currentSpeed = Speed;
        var advance = session.Varispeed?.Plan(count, currentSpeed) ?? count;
        for (var stem = 0; stem < session.Rings.Length; stem++)
        {
            var ring = session.Rings[stem];
            if (session.Varispeed is { } varispeed) varispeed.Render(stem, ring, count, advance, scratchLeft, scratchRight);
            else ring.Read(scratchLeft.AsSpan(0, count), scratchRight.AsSpan(0, count), count);
            Meter(session, stem, count);
            var rampStep = (envelopeTo - envelopeFrom) / count;
            if (envelopeFrom != 1 || rampStep != 0)
            {
                for (var index = 0; index < count; index++)
                {
                    var gain = envelopeFrom + rampStep * index;
                    scratchLeft[index] *= gain;
                    scratchRight[index] *= gain;
                }
            }
            for (var side = 0; side < 2; side++)
            {
                var source = side == 0 ? scratchLeft : scratchRight;
                var row = (stem * 2 + side) * destinations;
                for (var destination = 0; destination < destinations; destination++)
                {
                    var gain = program.Gains[row + destination];
                    var oldGain = from is not null ? from.Gains[row + destination] : gain;
                    if (gain == 0 && oldGain == 0) continue;
                    float[] target;
                    int at;
                    if (destination < matrix.DeviceChannels)
                    {
                        if (destination >= DeviceChannels) continue;
                        target = outputs[destination];
                        at = offset;
                    }
                    else
                    {
                        var feed = (destination - matrix.DeviceChannels) / 2;
                        if (feed >= matrix.FeedSlots.Length || matrix.FeedSlots[feed] >= MaxFeeds) continue;
                        target = feedScratch;
                        at = (matrix.FeedSlots[feed] * 2 + (destination - matrix.DeviceChannels) % 2) * count;
                    }
                    if (oldGain == gain)
                    {
                        for (var index = 0; index < count; index++) target[at + index] += source[index] * gain;
                    }
                    else
                    {
                        var gainStep = (gain - oldGain) / count;
                        for (var index = 0; index < count; index++) target[at + index] += source[index] * (oldGain + gainStep * index);
                    }
                }
            }
        }
        session.Varispeed?.Finish(count, currentSpeed, advance);
        position += advance;
    }

    private void Meter(MultitrackSession session, int stem, int count)
    {
        var slot = stem * MultitrackSession.MeterSlots;
        MeterSide(scratchLeft, session.Meters, slot, slot + 2, count);
        MeterSide(scratchRight, session.Meters, slot + 1, slot + 3, count);
        Interlocked.Add(ref session.Meters[slot + 4], count);
    }

    private static void MeterSide(float[] samples, long[] meters, int peakSlot, int squaresSlot, int count)
    {
        float peak = 0, squares = 0;
        for (var index = 0; index < count; index++)
        {
            var sample = samples[index];
            peak = MathF.Max(peak, MathF.Abs(sample));
            squares += sample * sample;
        }
        var held = BitConverter.Int32BitsToSingle((int)Interlocked.Read(ref meters[peakSlot]));
        Interlocked.Exchange(ref meters[peakSlot], BitConverter.SingleToInt32Bits(MathF.Max(peak, held)));
        var sum = BitConverter.Int32BitsToSingle((int)Interlocked.Read(ref meters[squaresSlot])) + squares;
        Interlocked.Exchange(ref meters[squaresSlot], BitConverter.SingleToInt32Bits(sum));
    }

    /// <summary>Each stem's left and right level since the last take (and resets them).</summary>
    public static (LevelReading Left, LevelReading Right)[] TakeMeters(MultitrackSession session)
    {
        var result = new (LevelReading, LevelReading)[session.Rings.Length];
        for (var stem = 0; stem < session.Rings.Length; stem++)
        {
            var slot = stem * MultitrackSession.MeterSlots;
            float Take(int index) => BitConverter.Int32BitsToSingle((int)Interlocked.Exchange(ref session.Meters[slot + index], 0));
            var peaks = (Take(0), Take(1));
            var squares = (Take(2), Take(3));
            var frames = (float)Interlocked.Exchange(ref session.Meters[slot + 4], 0);
            float Rms(float sum) => frames > 0 ? MathF.Sqrt(sum / frames) : 0;
            result[stem] = (new LevelReading(Rms(squares.Item1), peaks.Item1), new LevelReading(Rms(squares.Item2), peaks.Item2));
        }
        return result;
    }
}
