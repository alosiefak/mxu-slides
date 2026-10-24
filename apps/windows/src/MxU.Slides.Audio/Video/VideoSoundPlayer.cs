using MxU.Slides.Audio.Control;
using MxU.Slides.Audio.Decoding;
using MxU.Slides.Audio.Multitrack;

namespace MxU.Slides.Audio.Video;

/// <summary>Which part of the file a video plays and how fast (the cue's trim and play rate).</summary>
/// <param name="InPoint">Seconds into the file where it starts (null = the start).</param>
/// <param name="OutPoint">Where it stops (null = the end): no sound is played past it.</param>
/// <param name="Rate">1 = normal, 0.5 = half speed (the sound keeps its pitch).</param>
public sealed record VideoSoundOptions(double? InPoint = null, double? OutPoint = null, double Rate = 1);

public enum VideoSoundState
{
    /// <summary>Opening the file's sound.</summary>
    Opening,

    /// <summary>The picture is moving; the sound is getting into place (a jump is being decoded).</summary>
    Waiting,

    Playing,

    /// <summary>The picture isn't moving (paused, still opening, finished), so neither is the sound.</summary>
    Paused,

    /// <summary>The file has no sound we can read (no audio track, or a format Windows can't decode).</summary>
    NoSound,
}

/// <summary>How a video's sound is doing, for diagnostics and the self-test.</summary>
/// <param name="Position">The file second the sound is at (null before it first plays).</param>
/// <param name="SyncError">Sound minus picture at the last look (seconds; + = the sound is ahead).</param>
public sealed record VideoSoundStatus(VideoSoundState State, string? Problem, double? Position, double? SyncError, int Landings, AudioOutputStatus Output);

/// <summary>
/// One video's sound, played by the app's own audio engine instead of WPF's video player
/// (which can't choose a device), so it goes to the video sound's output: any device, a
/// channel pair or one channel, with its delay. It follows the picture's clock: the video
/// player's position is read about 25 times a second (<see cref="Follow"/>) and a
/// <see cref="VideoClockFollower"/> decides whether to hold, land or steer.
/// <para>
/// How it runs: a decode thread reads the file's audio track (<see cref="AudioFileDecoder"/>,
/// Media Foundation for MP4/MOV) into a two-second ring, stretched first when the video plays
/// at another rate (<see cref="TimeStretch"/>). An <see cref="AudioOutputHost"/> thread pulls
/// 10 ms blocks; each block is read from the ring through a varispeed (the multitrack's, so a
/// steer is a smooth 0.5% change, not a jump), faded in or out at a start or stop, delayed,
/// and handed to the device. A jump the ring can't reach asks the decode thread to seek; the
/// block stays silent until the sound for that moment is there, then starts on the exact
/// frame. With no device (unplugged, or none on the PC) the host keeps the clock running
/// silently, so the sound is still in step when the device comes back.
/// </para>
/// <para>
/// Threading: <see cref="Open"/>, <see cref="Configure"/> and <see cref="Dispose"/> block (a
/// file and a device open) and belong on a worker queue (<see cref="VideoSound"/> does that);
/// <see cref="Follow"/>, <see cref="Discontinuity"/>, the volume and the status are cheap
/// and safe from any thread. The audio thread never waits for either.
/// </para>
/// </summary>
public sealed class VideoSoundPlayer : IAudioRenderSource, IDisposable
{
    internal const int SampleRate = 48_000;
    private const int RingFrames = 2 * SampleRate;
    private const int ReadyFrames = SampleRate / 4;
    private const int Chunk = 4096;
    private const int MaxSlice = 4096;

    /// <summary>A seek asks for sound this far past where the picture will be, so it's ready when the picture gets there.</summary>
    internal const double SeekLead = 0.12;

    /// <summary>Decoded sound from one place in the file: ring frame 0 is file second <see cref="Start"/>; each ring frame stands for <see cref="Rate"/> file frames.</summary>
    private sealed class Segment(int id, double start, double rate)
    {
        public readonly int Id = id;
        public readonly double Start = start;
        public readonly double Rate = rate;
        public readonly StemRing Ring = new(RingFrames);
        public volatile bool Ended;
    }

    private sealed record SeekRequest(int Id, double Position, double Rate);

    private sealed record CommandBox(VideoSoundCommand Command);

    private enum Mode { Idle, Waiting, Playing }

    private readonly string path;
    private readonly VideoSoundOptions options;
    private readonly string name;
    private readonly bool manual;

    // Decoding: after Open, only the decode thread touches these.
    private AudioFileDecoder? decoder;
    private TimeStretch? stretch;
    private long endSourceFrame;
    private double fileSeconds;
    private readonly float[] fillLeft = new float[Chunk];
    private readonly float[] fillRight = new float[Chunk];
    private Thread? decodeThread;
    private readonly AutoResetEvent wake = new(false);
    private int madeFor;
    private Segment? segment;
    private SeekRequest? seek;
    private int seekCounter;
    private volatile int latestRequest;
    private double lastRate = 1;
    private volatile bool opened;
    private volatile bool disposed;
    private volatile string? problem;

    // Following the picture (callers' threads, under `gate`).
    private readonly Lock gate = new();
    private readonly VideoClockFollower follower = new();
    private CommandBox? latest;

    // What the audio thread heard last, for the follower.
    private readonly Lock clockGate = new();
    private SoundClockReading? heard;

    // The audio thread's own state.
    private volatile Mode mode = Mode.Idle;
    private int appliedGeneration;
    private double anchorPosition;
    private double anchorHostTime;
    private double anchorRate = 1;
    private double speed = 1;
    private double position = double.NaN;
    private Segment? playing;
    private float envelope;
    private float appliedGain;
    private readonly MultitrackVarispeed varispeed = new(1, MaxSlice);
    private readonly AudioDelayLine delay = new();
    private float[] mixLeft = new float[4096];
    private float[] mixRight = new float[4096];
    private readonly float[] sliceLeft = new float[MaxSlice];
    private readonly float[] sliceRight = new float[MaxSlice];
    private PcmBuffer mixBuffer;

    // The output.
    private readonly Lock outputGate = new();
    private AudioOutputHost? host;
    private (string? Uid, int Offset, bool Mono)? appliedOutput;
    private volatile bool monoOut;
    private volatile int channelOffset;
    private volatile bool hardwareLive = true;
    private volatile float volume = 1;
    private volatile bool muted;

    /// <param name="path">The video file (its first audio track plays).</param>
    /// <param name="manual">Tests: no device and no decode thread; they render and decode by hand.</param>
    public VideoSoundPlayer(string path, VideoSoundOptions? options = null, string name = "Video sound", bool manual = false)
    {
        this.path = path;
        this.options = options ?? new VideoSoundOptions();
        this.name = name;
        this.manual = manual;
        mixBuffer = new PcmBuffer(SampleRate, [mixLeft, mixRight]);
    }

    public string Path => path;

    public VideoSoundOptions Options => options;

    /// <summary>The sound's gain, 0…1 (linear).</summary>
    public float Volume
    {
        get => volume;
        set => volume = Math.Clamp(value, 0, 1);
    }

    public bool IsMuted
    {
        get => muted;
        set => muted = value;
    }

    /// <summary>True when the file has no sound we can play (see <see cref="VideoSoundStatus.Problem"/>).</summary>
    public bool HasNoSound => problem is not null;

    /// <summary>The output device's state changed (a thread-pool or audio thread).</summary>
    public event Action<AudioOutputStatus>? OutputStatusChanged;

    public AudioOutputStatus OutputStatus
    {
        get
        {
            if (manual) return AudioOutputStatus.Manual;
            lock (outputGate) return host?.Status ?? AudioOutputStatus.Idle;
        }
    }

    public VideoSoundStatus Status
    {
        get
        {
            var state = problem is not null ? VideoSoundState.NoSound
                : !opened ? VideoSoundState.Opening
                : mode switch
                {
                    Mode.Playing => VideoSoundState.Playing,
                    Mode.Waiting => VideoSoundState.Waiting,
                    _ => VideoSoundState.Paused,
                };
            double? error;
            int landings;
            lock (gate)
            {
                error = follower.LastError;
                landings = follower.Landings;
            }
            var at = position;
            return new VideoSoundStatus(state, problem, double.IsNaN(at) ? null : at, error, landings, OutputStatus);
        }
    }

    // MARK: Opening (worker thread)

    /// <summary>
    /// Opens the file's sound and decodes its first quarter second from the in point. Blocks for
    /// a moment (the file open): call it off the UI thread. A file without readable sound is
    /// noted in <see cref="HasNoSound"/>, not thrown.
    /// </summary>
    public void Open()
    {
        if (opened || problem is not null || disposed) return;
        try
        {
            decoder = AudioFileDecoder.Open(path, SampleRate);
            fileSeconds = decoder.SourceRate > 0 ? decoder.SourceFrames / (double)decoder.SourceRate : 0;
            endSourceFrame = options.OutPoint is { } outPoint
                ? Math.Min(decoder.SourceFrames, (long)Math.Ceiling(Math.Max(0, outPoint) * decoder.SourceRate))
                : decoder.SourceFrames;
            var first = MakeSegment(0, options.InPoint ?? 0, options.Rate);
            Volatile.Write(ref segment, first);
            opened = true;
            if (!manual)
            {
                decodeThread = new Thread(DecodeLoop) { IsBackground = true, Name = $"MxU video sound decode: {name}", Priority = ThreadPriority.AboveNormal };
                decodeThread.Start();
            }
        }
        catch (Exception error) when (error is AudioEngineException or IOException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            problem = error.Message;
            decoder?.Dispose();
            decoder = null;
        }
    }

    // MARK: Output (worker thread)

    /// <summary>
    /// Plays on <paramref name="routing"/>'s device (null = the Windows default), pair or mono
    /// channel and delay; <paramref name="reachesDevice"/> false keeps it silent on the device
    /// (an output set to "no device"). Blocks while a device opens: call it off the UI thread.
    /// </summary>
    public void Configure(AudioRouting routing, bool reachesDevice = true)
    {
        delay.DelayMilliseconds = routing.DelayMs;
        hardwareLive = reachesDevice;
        if (manual || disposed || problem is not null) return;
        lock (outputGate)
        {
            var wanted = (routing.Device?.Uid, routing.ChannelOffset, routing.Mono);
            if (host is not null && appliedOutput == wanted && host.IsRunning) return;
            channelOffset = routing.ChannelOffset;
            monoOut = routing.Mono;
            if (host is null)
            {
                host = new AudioOutputHost(name, this, Layout, SampleRate);
                host.StatusChanged += status => OutputStatusChanged?.Invoke(status);
            }
            appliedOutput = wanted;
            host.Configure(wanted.Uid, Layout, SampleRate);
        }
    }

    private AudioOutputHost.Layout Layout(int deviceChannels) =>
        new(monoOut ? 1 : 2, ChannelMap.Make(deviceChannels, channelOffset, monoOut));

    // MARK: Following the picture (any thread)

    /// <summary>What the video's clock says now; the sound holds, lands or steers to match.</summary>
    public void Follow(VideoClockSample sample)
    {
        Volatile.Write(ref lastRate, sample.Rate);
        SoundClockReading? reading;
        lock (clockGate) reading = heard;
        VideoSoundCommand command;
        lock (gate) command = follower.Step(sample, reading);
        Volatile.Write(ref latest, new CommandBox(command));
    }

    /// <summary>
    /// The video jumped (a seek, a restart, a loop): land again once it moves. Given where it
    /// jumped to (<paramref name="to"/>, file seconds), decoding starts there straight away, so
    /// the sound is ready when the picture moves (a loop doesn't lose its first moments).
    /// </summary>
    public void Discontinuity(double? to = null)
    {
        lock (gate) follower.Discontinuity();
        if (to is { } position && opened && problem is null) Request(position, Volatile.Read(ref lastRate));
    }

    // MARK: Decoding (decode thread)

    private void DecodeLoop()
    {
        while (!disposed)
        {
            wake.WaitOne(15);
            if (disposed) break;
            try
            {
                DecodeOnce();
            }
            catch (Exception error)
            {
                // A file that fails part way just goes quiet; the picture carries on.
                problem ??= error.Message;
                break;
            }
        }
    }

    private void DecodeOnce()
    {
        if (Volatile.Read(ref seek) is { } request && request.Id > madeFor)
        {
            madeFor = request.Id;
            Volatile.Write(ref segment, MakeSegment(request.Id, request.Position, request.Rate));
        }
        if (Volatile.Read(ref segment) is { } current) Fill(current, int.MaxValue);
    }

    private Segment MakeSegment(int id, double at, double rate)
    {
        var floor = Math.Max(0, options.InPoint ?? 0);
        var start = Math.Clamp(at, floor, Math.Max(floor, fileSeconds));
        decoder!.Seek((long)Math.Floor(start * decoder.SourceRate), endSourceFrame);
        stretch = StretchRate(rate) != 1 ? new TimeStretch(rate) : null;
        var made = new Segment(id, start, StretchRate(rate));
        Fill(made, ReadyFrames);
        return made;
    }

    // Tops the segment's ring up to `limit` frames, skipping whatever the audio thread had to play as silence.
    private void Fill(Segment target, int limit)
    {
        var ring = target.Ring;
        while (!disposed)
        {
            var drop = ring.FramesToDrop;
            var room = drop > 0 ? drop : Math.Min(ring.Space, limit - ring.Buffered);
            if (room < (drop > 0 ? 1 : 1024)) break;
            var count = Math.Min(Chunk, room);
            Produce(target, count);
            if (drop > 0) ring.Drop(count);
            else ring.Write(fillLeft.AsSpan(0, count), fillRight.AsSpan(0, count), count);
        }
    }

    // The next `count` frames of sound into fillLeft/fillRight; silence past the end of the file.
    private void Produce(Segment target, int count)
    {
        var got = 0;
        if (stretch is { } stretcher)
        {
            while (stretcher.Available < count && !stretcher.IsDrained)
            {
                var read = decoder!.Read(fillLeft, fillRight, 0, Chunk);
                if (read == 0) stretcher.Finish();
                else stretcher.Push(fillLeft.AsSpan(0, read), fillRight.AsSpan(0, read));
            }
            got = stretcher.Pull(fillLeft, fillRight, count);
        }
        else if (!target.Ended)
        {
            got = decoder!.Read(fillLeft, fillRight, 0, count);
        }
        if (got < count)
        {
            target.Ended = true;
            Array.Clear(fillLeft, got, count - got);
            Array.Clear(fillRight, got, count - got);
        }
    }

    // MARK: Rendering (audio thread)

    void IAudioRenderSource.Render(float[][] buffers, int frames, int sampleRate, double hostTime) =>
        RealtimeAudioCallback.Scope(() => RenderBlock(buffers, frames, hostTime));

    private void RenderBlock(float[][] buffers, int frames, double hostTime)
    {
        Grow(frames);
        Array.Clear(mixLeft, 0, frames);
        Array.Clear(mixRight, 0, frames);
        Apply(Volatile.Read(ref latest)?.Command ?? VideoSoundCommand.Idle);

        var start = 0;
        if (mode == Mode.Waiting) start = TryStart(hostTime, frames);
        if (start >= 0 && playing is { } current && (mode == Mode.Playing || envelope > 0))
        {
            var count = frames - start;
            var startPosition = position;
            RenderSound(current, start, count);
            if (mode == Mode.Playing)
                lock (clockGate) heard = new SoundClockReading(startPosition, hostTime + start / (double)SampleRate, speed, appliedGeneration);
        }

        // The playout delay, then the level (the device hears it after both; streams and
        // recordings hear what the device does, through the program tap).
        mixBuffer.FrameLength = frames;
        delay.Process(mixBuffer);
        var mono = buffers.Length == 1;
        var target = hardwareLive && !muted ? volume * (mono ? AudioEngine.MonoFold : 1) : 0;
        var from = appliedGain;
        var step = (target - from) / frames;
        for (var frame = 0; frame < frames; frame++)
        {
            var gain = from + step * (frame + 1);
            if (mono)
            {
                buffers[0][frame] = (mixLeft[frame] + mixRight[frame]) * AudioEngine.MonoFold * gain;
            }
            else
            {
                buffers[0][frame] = mixLeft[frame] * gain;
                buffers[1][frame] = mixRight[frame] * gain;
            }
        }
        appliedGain = target;
    }

    // Takes in the follower's latest instruction.
    private void Apply(VideoSoundCommand command)
    {
        switch (command.Step)
        {
            case VideoSoundStep.Hold:
                // Playing goes quiet over the next block (a 10 ms fade, no click).
                if (mode != Mode.Idle) mode = Mode.Idle;
                appliedGeneration = command.Generation;
                break;
            case VideoSoundStep.Land when command.Generation != appliedGeneration:
                appliedGeneration = command.Generation;
                anchorPosition = command.Position;
                anchorHostTime = command.HostTime;
                anchorRate = command.Speed;
                speed = command.Speed;
                envelope = 0;
                mode = Mode.Waiting;
                break;
            case VideoSoundStep.Steer when command.Generation == appliedGeneration && mode == Mode.Playing:
                speed = command.Speed;
                break;
        }
    }

    /// <summary>
    /// Where the picture is at this block, and whether the ring holds sound for it: start now
    /// (skipping to the exact frame), part way through the block, or later. Returns the frame
    /// in the block to start on, or -1 to stay silent this block.
    /// </summary>
    private int TryStart(double hostTime, int frames)
    {
        var wanted = anchorPosition + (hostTime - anchorHostTime) * anchorRate;
        if (options.OutPoint is { } outPoint && wanted >= outPoint) return -1;
        var current = Volatile.Read(ref segment);
        if (current is null || Math.Abs(current.Rate - StretchRate(anchorRate)) > 1e-6)
        {
            // Nothing decoded yet, or decoded for another play rate.
            RequestSeek(wanted, anchorRate);
            return -1;
        }
        var head = current.Start + current.Ring.ReadFrames * current.Rate / SampleRate;
        var ahead = wanted - head;
        if (ahead >= 0)
        {
            // The ring has it (skip to the exact frame), or the file has ended (play on in silence).
            var buffered = current.Ring.Buffered * current.Rate / SampleRate;
            if (ahead < buffered || current.Ended)
            {
                var skip = (int)Math.Round(Math.Min(ahead, buffered) * SampleRate / current.Rate);
                if (skip > 0) current.Ring.Skip(skip);
                Begin(current, wanted);
                return 0;
            }
            RequestSeek(wanted, anchorRate);
            return -1;
        }
        // The sound decoded for this moment starts a little ahead of the picture: start part way
        // through the block when it gets there, or wait for it (a seek asks for SeekLead ahead).
        var behind = -ahead;
        if (current.Id == latestRequest)
        {
            var offset = (int)Math.Round(behind * SampleRate / anchorRate);
            if (offset < frames)
            {
                Begin(current, head);
                return offset;
            }
            if (behind < SeekLead * 3) return -1;
        }
        RequestSeek(wanted, anchorRate);
        return -1;
    }

    // The stretch a segment is decoded with for a play rate (1 = none).
    private static double StretchRate(double rate) => Math.Abs(rate - 1) > 1e-6 ? Math.Clamp(rate, 0.25, 4) : 1;

    private void Begin(Segment current, double at)
    {
        playing = current;
        position = at;
        varispeed.Reset();
        mode = Mode.Playing;
    }

    // Asks the decode thread for sound from just past `wanted` (unless it's already working on about that).
    private void RequestSeek(double wanted, double rate)
    {
        if (Volatile.Read(ref seek) is { } pending && pending.Id == latestRequest && Volatile.Read(ref segment)?.Id != pending.Id
            && Math.Abs(pending.Position - SeekLead - wanted) < 1 && Math.Abs(pending.Rate - rate) < 1e-6)
            return;
        Request(wanted + SeekLead, rate);
    }

    // A new place for the decode thread to read from (the audio thread's seeks, and the caller's on a jump).
    private void Request(double position, double rate)
    {
        var id = Interlocked.Increment(ref seekCounter);
        Volatile.Write(ref seek, new SeekRequest(id, Math.Max(0, position), rate));
        latestRequest = id;
        wake.Set();
    }

    // Reads `count` frames through the varispeed into the mix at `offset`, fading toward the
    // envelope's target (in while playing, out once held).
    private void RenderSound(Segment current, int offset, int count)
    {
        var ringSpeed = Math.Clamp(speed / current.Rate, 0.5, 2);
        var from = envelope;
        var to = mode == Mode.Playing ? 1f : 0f;
        var done = 0;
        while (done < count)
        {
            var slice = Math.Min(MaxSlice, count - done);
            var advance = varispeed.Plan(slice, ringSpeed);
            varispeed.Render(0, current.Ring, slice, advance, sliceLeft, sliceRight);
            varispeed.Finish(slice, ringSpeed, advance);
            for (var index = 0; index < slice; index++)
            {
                var gain = from + (to - from) * ((done + index + 1) / (float)count);
                mixLeft[offset + done + index] = sliceLeft[index] * gain;
                mixRight[offset + done + index] = sliceRight[index] * gain;
            }
            done += slice;
        }
        envelope = to;
        position += count * speed / SampleRate;
        if (current.Ring.Buffered < RingFrames / 2) wake.Set();
    }

    private void Grow(int frames)
    {
        if (mixLeft.Length >= frames) return;
        mixLeft = new float[frames];
        mixRight = new float[frames];
        mixBuffer = new PcmBuffer(SampleRate, [mixLeft, mixRight]);
    }

    // MARK: Tests

    /// <summary>Tests: one pass of the decode thread (take a seek, top the ring up).</summary>
    internal void DecodeForTests() => DecodeOnce();

    /// <summary>Tests: one block as the device would ask for it, heard at <paramref name="hostTime"/>.</summary>
    internal float[][] RenderForTests(int frames, double hostTime, bool mono = false)
    {
        var buffers = Enumerable.Range(0, mono ? 1 : 2).Select(_ => new float[frames]).ToArray();
        RenderBlock(buffers, frames, hostTime);
        return buffers;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        wake.Set();
        AudioOutputHost? closing;
        lock (outputGate)
        {
            closing = host;
            host = null;
        }
        closing?.Dispose();
        decodeThread?.Join(TimeSpan.FromSeconds(2));
        decoder?.Dispose();
    }
}
