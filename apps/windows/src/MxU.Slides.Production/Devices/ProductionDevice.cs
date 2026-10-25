using System.Diagnostics;
using System.Globalization;
using MxU.Slides.Core.Threading;

namespace MxU.Slides.Production.Devices;

/// <summary>The colour of a device's status light.</summary>
public enum DeviceState
{
    /// <summary>Grey: turned off in Settings.</summary>
    Off,

    /// <summary>Grey: ready, nothing to do yet (DMX before the first lighting step).</summary>
    Idle,

    /// <summary>Amber: trying to reach it.</summary>
    Connecting,

    /// <summary>Green: connected and answering.</summary>
    Online,

    /// <summary>Red: it doesn't answer, or refused us (a wrong password). The app keeps trying quietly.</summary>
    Problem,
}

/// <summary>A device's status light and the words beside it ("ATEM Mini Pro · 8 inputs").</summary>
public sealed record DeviceStatus(DeviceState State, string Detail)
{
    public static DeviceStatus Off { get; } = new(DeviceState.Off, "Off");

    public bool IsOnline => State == DeviceState.Online;
}

/// <summary>
/// What every controllable device (an ATEM, OBS, vMix) has in common: its settings, its
/// status light, and its own thread that commands run on, one at a time and in order.
/// <para>
/// Why its own thread: a device on the network can be slow, or missing. Commands are handed
/// to the device's queue and the caller carries straight on, so a switcher that doesn't
/// answer never holds up a slide, the lights or another device. A command that waited too long
/// behind a slow one is dropped (<see cref="StaleAfter"/>): a cut that happens half a minute
/// late is worse than none. A command for a device that isn't connected is skipped at once,
/// never saved up for later, for the same reason.
/// </para>
/// </summary>
public abstract class ProductionDevice : IDisposable
{
    /// <summary>How late a queued command may still run.</summary>
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromSeconds(3);

    private readonly WorkQueue queue;
    private readonly Lock statusGate = new();
    private DeviceStatus status = DeviceStatus.Off;
    private volatile bool disposed;

    protected ProductionDevice(DeviceSettings settings)
    {
        Settings = settings;
        queue = new WorkQueue($"MxU {DeviceSettings.KindName(settings.Kind)}: {settings.Name}");
    }

    /// <summary>A copy of the settings it was made with (a change in Settings makes a new device).</summary>
    public DeviceSettings Settings { get; }

    public string Id => Settings.Id;

    public string Name => Settings.Name;

    public DeviceKind Kind => Settings.Kind;

    protected bool IsDisposed => disposed;

    public DeviceStatus Status
    {
        get
        {
            lock (statusGate) return status;
        }
    }

    /// <summary>The status light changed. Raised on a device thread: the UI must hop to its own.</summary>
    public event Action<ProductionDevice>? StatusChanged;

    /// <summary>A line for the log ("skipped Cut: not connected"). Never holds a password.</summary>
    public event Action<ProductionDevice, string>? Noted;

    /// <summary>Starts connecting in the background and returns at once.</summary>
    public abstract void Start();

    /// <summary>What a command picker can offer from the device itself (its inputs, scenes, macros).</summary>
    public virtual DeviceCatalog Catalog => DeviceCatalog.Empty;

    protected void SetStatus(DeviceState state, string detail)
    {
        var next = new DeviceStatus(state, detail);
        lock (statusGate)
        {
            if (status == next) return;
            status = next;
        }
        try { StatusChanged?.Invoke(this); }
        catch (Exception ex) { Note($"status listener failed: {ex.Message}"); }
    }

    protected void Note(string text)
    {
        try { Noted?.Invoke(this, text); }
        catch
        {
            // A broken log listener must not stop a device.
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> on this device's own thread, after what's already queued.
    /// The task says whether it worked: tests and the Local API can wait on it; the show never does.
    /// </summary>
    protected Task<bool> Enqueue(string what, Func<bool> work)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (disposed)
        {
            done.SetResult(false);
            return done.Task;
        }
        var queued = Stopwatch.GetTimestamp();
        queue.Async(() =>
        {
            var late = Stopwatch.GetElapsedTime(queued);
            if (late > StaleAfter)
            {
                Note(string.Create(CultureInfo.InvariantCulture, $"skipped {what}: {late.TotalSeconds:0.0} s late (the device was slow)"));
                done.TrySetResult(false);
                return;
            }
            try
            {
                done.TrySetResult(work());
            }
            catch (Exception ex)
            {
                Note($"{what} failed: {ex.Message}");
                done.TrySetResult(false);
            }
        });
        return done.Task;
    }

    /// <summary>Skips a command because the device isn't connected (said once in the log per command).</summary>
    protected bool NotConnected(string what)
    {
        Note($"skipped {what}: not connected ({Status.Detail})");
        return false;
    }

    public virtual void Dispose()
    {
        disposed = true;
        queue.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>Names a device reports about itself, for the editors' pickers: inputs, scenes, macros, transitions.</summary>
public sealed record DeviceCatalog(
    IReadOnlyList<CatalogEntry> Inputs,
    IReadOnlyList<CatalogEntry> Macros,
    IReadOnlyList<string> Scenes,
    IReadOnlyList<string> Transitions,
    IReadOnlyDictionary<string, IReadOnlyList<string>> SceneSources)
{
    public static DeviceCatalog Empty { get; } = new([], [], [], [], new Dictionary<string, IReadOnlyList<string>>());
}

/// <summary>One numbered thing on a device: an ATEM source (2, "Camera 2"), a macro, a vMix input.</summary>
public sealed record CatalogEntry(int Number, string Name);
