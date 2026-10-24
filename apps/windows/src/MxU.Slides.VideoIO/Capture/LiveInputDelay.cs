using MxU.Slides.Core.Media;

namespace MxU.Slides.VideoIO.Capture;

/// <summary>
/// Holds a live input back by a few frames so its picture lines up with audio that
/// arrives later (lip-sync). Per input id, each new frame is copied into a buffer the
/// delay owns and the oldest held copy is drawn instead, so with a delay of n the
/// picture is n frames behind. The renderer pulls once per tick with the input's latest
/// frame; pulling the same frame again doesn't advance the line.
/// <para>
/// A frame returned by <see cref="Delayed"/> stays valid until the next call for the same
/// id (its buffer is reused one arrival after it leaves the line). Thread-safe.
/// Port of MediaEngine/LiveInputDelay.swift.
/// </para>
/// </summary>
public sealed class LiveInputDelay
{
    /// <summary>The longest delay, in frames (a second at 30 fps).</summary>
    public const int MaxFrames = 30;

    private readonly Lock gate = new();
    private readonly Dictionary<string, int> delays = [];
    private readonly Dictionary<string, State> states = [];

    /// <summary>Sets an input's delay, clamped to 0…<see cref="MaxFrames"/>. Zero passes frames straight through and drops held ones.</summary>
    public void SetDelay(string id, int frames)
    {
        var clamped = Math.Clamp(frames, 0, MaxFrames);
        lock (gate)
        {
            if (clamped > 0) delays[id] = clamped;
            else
            {
                delays.Remove(id);
                states.Remove(id);
            }
        }
    }

    public int Delay(string id)
    {
        lock (gate) return delays.GetValueOrDefault(id);
    }

    /// <summary>Drops the held frames but keeps the setting: a restarted input fills up again from its first frame.</summary>
    public void Clear(string id)
    {
        lock (gate) states.Remove(id);
    }

    /// <summary>The frame to draw for input <paramref name="id"/> given its newest frame.</summary>
    public VideoFrame Delayed(VideoFrame frame, string id)
    {
        lock (gate)
        {
            if (!delays.TryGetValue(id, out var target) || target <= 0) return frame;
            if (!states.TryGetValue(id, out var state)) states[id] = state = new State();
            if (!ReferenceEquals(state.LastSeen, frame))
            {
                state.LastSeen = frame;
                state.Pending.Enqueue(Copy(frame, state));
                var capacity = target + 1;
                while (state.Pending.Count > capacity) state.Recycle(state.Pending.Dequeue());
            }
            return state.Pending.TryPeek(out var oldest) ? oldest : frame;
        }
    }

    /// <summary>Copies a frame into a buffer from this input's pool (made fresh when the size changes).</summary>
    private static VideoFrame Copy(VideoFrame frame, State state)
    {
        var key = $"{frame.Width}x{frame.Height}:{frame.Stride}";
        if (state.PoolKey != key)
        {
            state.Free.Clear();
            state.PoolKey = key;
        }
        var bytes = frame.Stride * frame.Height;
        var buffer = state.Free.Count > 0 ? state.Free.Pop() : new byte[bytes];
        frame.Pixels.Span[..Math.Min(bytes, frame.Pixels.Length)].CopyTo(buffer);
        return new VideoFrame(frame.Width, frame.Height, frame.Stride, buffer.AsMemory(0, bytes), frame.HostTime);
    }

    private sealed class State
    {
        public Queue<VideoFrame> Pending { get; } = new();
        public Stack<byte[]> Free { get; } = new();
        public VideoFrame? LastSeen { get; set; }
        public string PoolKey { get; set; } = "";

        /// <summary>A frame that left the line gives its buffer back (the pool stays at most <see cref="MaxFrames"/> + 2).</summary>
        public void Recycle(VideoFrame frame)
        {
            if (Free.Count >= MaxFrames + 2) return;
            if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(frame.Pixels, out var segment) && segment.Array is { } array
                && $"{frame.Width}x{frame.Height}:{frame.Stride}" == PoolKey)
                Free.Push(array);
        }
    }
}
