using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace MxU.Slides.Assistant.Spotting;

/// <summary>
/// The app's side of the speech recognizer: it starts the worker process
/// (<see cref="SpeechWorker"/>), feeds it sound, reads what it hears, and measures what it costs.
/// <list type="bullet">
/// <item><b>Sound in:</b> <see cref="Feed"/> is called on the sound card's own thread, so it only
/// converts the block (to one channel at 16 kHz, the recognizer's format) and queues it; a writer
/// thread sends it down the pipe. If the worker falls behind, old sound is dropped, never the show.</item>
/// <item><b>Its cost:</b> the worker is its own process at below-normal priority, so Windows
/// gives the show's threads the processor first, and <see cref="SampleCpu"/> can say exactly how
/// much processor time recognition took.</item>
/// <item><b>Privacy:</b> the sound goes down a pipe to a process on this PC and nowhere else.</item>
/// </list>
/// </summary>
public sealed class SpeechListener : IDisposable
{
    private readonly string exe;
    // Blocks waiting to go down the pipe: about ten seconds' worth at a sound card's usual 20 ms a
    // block, which covers the second or two the worker takes to start.
    private readonly BlockingCollection<byte[]> sound = new(boundedCapacity: 500);
    private Process? process;
    private Thread? writer;
    private TimeSpan cpuAt;
    private long cpuWhen;
    private double carry; // where the next 16 kHz sample falls between two input samples
    private volatile bool stopped;

    /// <param name="exe">The app's own .exe (<c>Environment.ProcessPath</c>): it runs itself with the worker's switch.</param>
    public SpeechListener(string exe) => this.exe = exe;

    /// <summary>Something was heard (raised on a reader thread).</summary>
    public event Action<Utterance>? Heard;

    /// <summary>The recognizer is up (its language, "en-GB").</summary>
    public event Action<string>? Ready;

    /// <summary>The recognizer couldn't start or stopped (words for the operator).</summary>
    public event Action<string>? Failed;

    /// <summary>The worker went away.</summary>
    public event Action? Ended;

    public bool IsRunning => process is { HasExited: false };

    /// <summary>All the processor time recognition has used since it started.</summary>
    public TimeSpan CpuTotal { get; private set; }

    /// <summary>Starts the worker, which then listens to whatever <see cref="Feed"/> is given.</summary>
    public void Start()
    {
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true,
        };
        start.ArgumentList.Add(SpeechWorker.Switch);
        process = Process.Start(start) ?? throw new InvalidOperationException("The speech recognizer didn't start.");
        // Below normal: when the PC is busy, the outputs' threads get the processor first.
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        cpuWhen = Stopwatch.GetTimestamp();
        var reader = new Thread(Read) { IsBackground = true, Name = "Speech heard" };
        reader.Start();
        writer = new Thread(Write) { IsBackground = true, Name = "Speech sound out", Priority = ThreadPriority.BelowNormal };
        writer.Start();
    }

    /// <summary>
    /// Takes a block of sound (any rate, one array per channel) from the capture thread. It's
    /// mixed to one channel and thinned to 16,000 samples a second by averaging, which is plenty
    /// for speech.
    /// </summary>
    public void Feed(float[][] channels, int frames, int sampleRate)
    {
        if (stopped || channels.Length == 0 || frames <= 0) return;
        var step = sampleRate / 16000.0;
        var bytes = new byte[((int)((frames - carry) / step) + 1) * 2];
        var count = 0;
        var at = carry;
        for (; at < frames; at += step)
        {
            // Average the input samples this output sample covers, across the channels.
            var (from, to) = ((int)at, Math.Min(frames, Math.Max((int)at + 1, (int)(at + step))));
            var sum = 0f;
            for (var frame = from; frame < to; frame++)
                foreach (var channel in channels) sum += channel[frame];
            var value = (short)Math.Clamp(sum / ((to - from) * channels.Length) * 32767f, short.MinValue, short.MaxValue);
            bytes[count++] = (byte)value;
            bytes[count++] = (byte)(value >> 8);
        }
        carry = at - frames;
        if (count == 0) return;
        if (count < bytes.Length) Array.Resize(ref bytes, count);
        // Never wait on the capture thread: when the queue is full, the oldest sound goes.
        try
        {
            while (!sound.TryAdd(bytes))
                sound.TryTake(out _);
        }
        catch (InvalidOperationException) { } // stopped meanwhile
    }

    /// <summary>
    /// The share of one processor core recognition has used since the last call (0 to 100, or a
    /// little more when it used several cores at once).
    /// </summary>
    public double SampleCpu()
    {
        if (process is not { } worker) return 0;
        try
        {
            worker.Refresh();
            var used = worker.TotalProcessorTime;
            var now = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(cpuWhen, now);
            var percent = elapsed > TimeSpan.Zero ? (used - cpuAt) / elapsed * 100 : 0;
            (cpuAt, cpuWhen, CpuTotal) = (used, now, used);
            return Math.Max(0, percent);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return 0;
        }
    }

    private void Write()
    {
        try
        {
            var pipe = process!.StandardInput.BaseStream;
            foreach (var block in sound.GetConsumingEnumerable())
            {
                pipe.Write(block);
                pipe.Flush();
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private void Read()
    {
        try
        {
            while (process!.StandardOutput.ReadLine() is { } line)
            {
                JsonElement message;
                try { message = JsonDocument.Parse(line).RootElement; }
                catch (JsonException) { continue; }
                switch (message.GetProperty("t").GetString())
                {
                    case "heard":
                        Heard?.Invoke(new Utterance(
                            message.GetProperty("text").GetString() ?? "", TimeSpan.FromSeconds(message.GetProperty("at").GetDouble()),
                            message.GetProperty("conf").GetDouble(), message.GetProperty("src").GetString() == "refs"));
                        break;
                    case "ready":
                        Ready?.Invoke(message.GetProperty("recognizer").GetString() ?? "");
                        break;
                    case "error":
                        Failed?.Invoke(message.GetProperty("message").GetString() ?? "The speech recognizer stopped.");
                        break;
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
        SampleCpu();
        if (!stopped) Ended?.Invoke();
    }

    public void Dispose()
    {
        if (stopped) return;
        stopped = true;
        sound.CompleteAdding();
        try
        {
            if (process is { HasExited: false } worker)
            {
                SampleCpu();
                worker.Kill();
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        process?.Dispose();
    }
}
