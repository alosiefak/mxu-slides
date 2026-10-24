using System.Globalization;
using MxU.Slides.Audio.Library;
using MxU.Slides.Core.Threading;

namespace MxU.Slides.Audio.Timeline;

/// <summary>
/// Waveforms for the timeline editor's music clips and stems. Reading a song's audio to draw
/// its shape takes a moment, so it never happens while drawing: the editor asks for a clip's
/// waveform, gets null the first time (it draws the clip without one), and this store works
/// it out on its own background thread, then raises <see cref="Changed"/> on the UI thread so
/// the editor draws again. Each file's peaks are also kept on disk (<see cref="WaveformCache"/>),
/// so the next time it opens at once. Port of WaveformStore and TimelineWaveform's loading
/// in Sources/TimelineWaveforms.swift.
/// </summary>
public sealed class TimelineWaveformStore : IDisposable
{
    /// <summary>One stem (or music clip) placed on the song: its file, where it starts, how far into the file, for how long.</summary>
    public sealed record Source(string Hash, string? Path, double At, double InPoint, double? Duration);

    private readonly WaveformCache cache;
    private readonly SynchronizationContext? ui;

    // Its own thread (not .NET's shared pool), below normal priority, so decoding a long
    // song never delays anything the show depends on.
    private readonly WorkQueue work = new("MxU waveforms", ThreadPriority.BelowNormal);
    private readonly Lock gate = new();
    private readonly Dictionary<string, WaveformLevels?> ready = new(StringComparer.Ordinal);
    private readonly Queue<string> readyOrder = new();
    private readonly HashSet<string> pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WaveformLevels> latest = new(StringComparer.Ordinal);
    private bool disposed;

    /// <param name="cacheFolder">Where the .peaks files go (machine-local: they can always be made again).</param>
    /// <param name="ui">Where <see cref="Changed"/> is raised; the UI thread's context.</param>
    public TimelineWaveformStore(string cacheFolder, SynchronizationContext? ui)
    {
        cache = new WaveformCache(cacheFolder);
        this.ui = ui;
    }

    /// <summary>How many combined waveforms are kept in memory.</summary>
    public const int Capacity = 64;

    /// <summary>A waveform finished: draw again (raised on the UI thread).</summary>
    public event Action? Changed;

    /// <summary>
    /// The waveform of these sources mixed together, from <paramref name="origin"/> for
    /// <paramref name="length"/> seconds; null while it is being worked out (or when nothing
    /// could be read). <paramref name="holder"/> names what is drawing it (a cue id): while a
    /// new shape is worked out (the clip was just trimmed), its last one is returned.
    /// </summary>
    public WaveformLevels? Levels(IReadOnlyList<Source> sources, double origin, double length, string? holder = null)
    {
        if (sources.Count == 0 || length <= 0) return null;
        var key = Key(sources, origin, length);
        lock (gate)
        {
            if (ready.TryGetValue(key, out var made))
            {
                if (holder is not null && made is not null) latest[holder] = made;
                return made;
            }
            if (!disposed && pending.Add(key)) work.Async(() => Make(key, sources, origin, length, holder));
            return holder is not null && latest.TryGetValue(holder, out var last) ? last : null;
        }
    }

    private void Make(string key, IReadOnlyList<Source> sources, double origin, double length, string? holder)
    {
        WaveformLevels? levels = null;
        try
        {
            var parts = new List<WaveformPart>();
            foreach (var source in sources)
                if (source.Path is { } path && cache.Get(source.Hash, path) is { } wave)
                    parts.Add(new WaveformPart(wave, source.At, source.InPoint, source.Duration));
            if (parts.Count > 0) levels = new WaveformLevels(Waveforms.Combined(parts, origin, length));
        }
        catch (Exception)
        {
            // A file that can't be read just draws without a waveform.
        }
        lock (gate)
        {
            pending.Remove(key);
            ready[key] = levels;
            readyOrder.Enqueue(key);
            while (readyOrder.Count > Capacity) ready.Remove(readyOrder.Dequeue());
            if (holder is not null && levels is not null) latest[holder] = levels;
            if (latest.Count > Capacity * 4) latest.Clear();
        }
        if (ui is { } context) context.Post(_ => Changed?.Invoke(), null);
        else Changed?.Invoke();
    }

    /// <summary>Waits for the work asked for so far (tests and the self-test).</summary>
    public void Settle(TimeSpan? timeout = null) => work.Sync(() => { }, timeout ?? TimeSpan.FromSeconds(30));

    private static string Key(IReadOnlyList<Source> sources, double origin, double length)
    {
        var parts = sources.Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.Hash}{(s.Path is null ? "?" : "")}@{s.At:R}+{s.InPoint:R}/{s.Duration:R}"));
        return string.Join("|", parts) + string.Create(CultureInfo.InvariantCulture, $"#{origin:R}:{length:R}");
    }

    public void Dispose()
    {
        lock (gate) disposed = true;
        work.Dispose();
    }
}
