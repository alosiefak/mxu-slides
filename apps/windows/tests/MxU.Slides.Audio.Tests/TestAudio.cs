using MxU.Slides.Audio.Multitrack;
using NAudio.Wave;

[assembly: AssemblyFixture(typeof(MxU.Slides.Audio.Tests.TestFiles))]

namespace MxU.Slides.Audio.Tests;

/// <summary>
/// The test tones live in a folder for this run, removed afterwards; anything an earlier
/// run couldn't remove (a file still open when it ended) is swept up at the start.
/// </summary>
public sealed class TestFiles : IDisposable
{
    public static readonly string Folder = Path.Combine(Path.GetTempPath(), "mxu-audio-tests");

    public static readonly string RunFolder = Path.Combine(Folder, $"run-{Environment.ProcessId}");

    public TestFiles()
    {
        if (!Directory.Exists(Folder)) return;
        foreach (var leftover in Directory.EnumerateFileSystemEntries(Folder))
        {
            try
            {
                if (Directory.Exists(leftover)) Directory.Delete(leftover, recursive: true);
                else File.Delete(leftover);
            }
            catch
            {
                // Still in use (another run, or a file held open): next time.
            }
        }
    }

    public void Dispose()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        try { Directory.Delete(RunFolder, recursive: true); }
        catch { }
    }
}

/// <summary>Writes test tones as 32-bit float WAV files (what AVAudioFile writes in the Mac tests).</summary>
internal static class TestAudio
{
    private static string Root => TestFiles.RunFolder;

    /// <summary>A WAV whose channel 1 (right) can differ; frame index → sample.</summary>
    public static string Wav(string name, double rate, int channels, double seconds, Func<int, float> sample, Func<int, float>? right = null)
    {
        Directory.CreateDirectory(Root);
        var path = Path.Combine(Root, $"{name}-{Guid.NewGuid():N}.wav");
        var frames = (int)(seconds * rate);
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat((int)rate, channels));
        var block = new float[Math.Min(frames, 48_000) * channels];
        var done = 0;
        while (done < frames)
        {
            var count = Math.Min(frames - done, block.Length / channels);
            for (var frame = 0; frame < count; frame++)
                for (var channel = 0; channel < channels; channel++)
                    block[frame * channels + channel] = channel == 1 && right is not null ? right(done + frame) : sample(done + frame);
            writer.WriteSamples(block, 0, count * channels);
            done += count;
        }
        return path;
    }

    public static string Tone(string name, double seconds, double hertz, double rate = 44_100, int channels = 1, float amplitude = 0.1f) =>
        Wav(name, rate, channels, seconds, i => (float)Math.Sin(2 * Math.PI * hertz * i / rate) * amplitude);

    public static void Delete(string path)
    {
        try { File.Delete(path); }
        catch { }
    }

    /// <summary>Skips a test that needs a real output device on a machine without one (GitHub's runners).</summary>
    public static AudioOutputDevice RequireOutputDevice() =>
        AudioDeviceList.DefaultOutputDevice() ?? throw SkipException("This machine has no audio output device");

    private static Exception SkipException(string reason)
    {
        Assert.Skip(reason);
        return new InvalidOperationException(reason);
    }

    /// <summary>Runs <paramref name="work"/> on its own thread (like Thread.detachNewThread); true if it finished in time.</summary>
    public static bool FinishesWithin(Action work, double seconds = 2)
    {
        var thread = new Thread(() => work()) { IsBackground = true };
        thread.Start();
        return thread.Join(TimeSpan.FromSeconds(seconds));
    }

    /// <summary>Runs <paramref name="work"/> on its own thread and returns its result (waiting up to 5 s).</summary>
    public static T OnOtherThread<T>(Func<T> work)
    {
        T result = default!;
        var thread = new Thread(() => result = work()) { IsBackground = true };
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(5));
        return result;
    }

    /// <summary>Polls <paramref name="condition"/> for up to <paramref name="seconds"/>.</summary>
    public static bool WaitFor(Func<bool> condition, double seconds = 3)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(20);
        }
        return condition();
    }

    public static async Task<bool> WaitForAsync(Func<bool> condition, double seconds, CancellationToken token)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(25, token);
        }
        return condition();
    }

    public static float BiggestStep(IReadOnlyList<float> samples, int from = 0, int? to = null)
    {
        float biggest = 0;
        var end = Math.Min(to ?? samples.Count, samples.Count);
        for (var index = from + 1; index < end; index++) biggest = MathF.Max(biggest, MathF.Abs(samples[index] - samples[index - 1]));
        return biggest;
    }

    public static int PeakIndex(IReadOnlyList<float> samples)
    {
        var best = -1;
        float peak = -1;
        for (var index = 0; index < samples.Count; index++)
            if (MathF.Abs(samples[index]) > peak)
            {
                peak = MathF.Abs(samples[index]);
                best = index;
            }
        return best;
    }

    /// <summary>
    /// Pulls <paramref name="seconds"/> from a multitrack engine in <paramref name="buffer"/>-frame
    /// blocks, ticking (decoding) before each, like the Mac tests' render helper.
    /// </summary>
    public static List<float>[] Render(MultitrackEngine engine, double seconds, int buffer = 512, Func<double, bool>? tickAt = null,
        double start = 0, Func<double, double>? hostTime = null, Action<float[][], int>? perBuffer = null,
        IReadOnlyList<(double At, Action Action)>? at = null)
    {
        var channels = engine.DeviceChannels;
        var output = Enumerable.Range(0, channels).Select(_ => new List<float>()).ToArray();
        var storage = Enumerable.Range(0, channels).Select(_ => new float[buffer]).ToArray();
        var total = (int)(seconds * engine.SampleRate);
        var pending = at?.ToList() ?? [];
        var done = 0;
        while (done < total)
        {
            while (pending.Count > 0 && done / engine.SampleRate >= pending[0].At)
            {
                pending[0].Action();
                pending.RemoveAt(0);
            }
            var now = start + done / engine.SampleRate;
            if (tickAt?.Invoke(now) ?? true) engine.TickNow();
            var count = Math.Min(buffer, total - done);
            engine.Renderer.Render(count, hostTime?.Invoke(now) ?? 0, storage);
            perBuffer?.Invoke(storage, count);
            for (var channel = 0; channel < channels; channel++) output[channel].AddRange(storage[channel].AsSpan(0, count));
            done += count;
        }
        return output;
    }

    /// <summary>Each channel to a single-stem mix landing on device channel n.</summary>
    public static MultitrackRouting Solo(params string[] ids) => new(
        ids.Select(id => new MultitrackRouting.Channel(id, sends: new() { [$"mix-{id}"] = 1 })).ToList(),
        ids.Select((id, index) => new MultitrackRouting.Mix($"mix-{id}", outputs: [new MultitrackRouting.Output([index])])).ToList());
}
