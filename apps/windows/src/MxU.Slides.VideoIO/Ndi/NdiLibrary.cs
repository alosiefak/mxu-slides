using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using MxU.Slides.VideoIO.Common;

namespace MxU.Slides.VideoIO.Ndi;

/// <summary>Why the NDI runtime isn't usable.</summary>
public enum NdiErrorKind
{
    /// <summary>No Processing.NDI.Lib.x64.dll in any place NDI Tools installs it.</summary>
    RuntimeNotFound,

    /// <summary>A runtime file was found but would not load, or has no function table.</summary>
    LoadFailed,

    /// <summary>The function table doesn't line up with the runtime's own exports (a different ABI).</summary>
    TableMismatch,

    /// <summary>NDIlib_initialize said no (usually an unsupported CPU).</summary>
    InitializeFailed,
}

/// <summary>
/// Why NDI isn't available, with the detail (paths tried, mis-slotted functions) and a
/// sentence for the operator in <see cref="Message"/>. Port of NDIKit's NDIError.
/// </summary>
public sealed record NdiError(NdiErrorKind Kind, IReadOnlyList<string> Details)
{
    public const string NotInstalledMessage = "NDI runtime not installed — install NDI Tools from ndi.video";

    public string Message => Kind switch
    {
        NdiErrorKind.RuntimeNotFound => NotInstalledMessage,
        NdiErrorKind.LoadFailed => $"NDI runtime could not be loaded: {string.Join("; ", Details)}",
        NdiErrorKind.TableMismatch => $"NDI runtime is incompatible (mis-slotted: {string.Join(", ", Details)})",
        _ => "NDI could not start (NDIlib_initialize failed; this CPU may be unsupported)",
    };

    public bool Equals(NdiError? other) => other is not null && Kind == other.Kind && Details.SequenceEqual(other.Details);

    public override int GetHashCode() => HashCode.Combine(Kind, Details.Count);

    public override string ToString() => Message;
}

/// <summary>
/// The NDIlib_v5 function table, one entry per function this app calls, read from the
/// slots the NDI SDK's Processing.NDI.DynamicLoad.h gives them. A record, so a test can
/// make a deliberately mis-slotted copy with <c>with { … }</c>.
/// </summary>
internal sealed record NdiTable
{
    // Slot numbers: the position of each function pointer in struct NDIlib_v5. They
    // were checked against the exports of the NDI 6.3 runtime, and are checked again
    // on every load by TableMismatches.
    internal static readonly (string Name, int Slot, Func<NdiTable, nint> Entry)[] CalledSlots =
    [
        ("NDIlib_initialize", 0, t => t.Initialize),
        ("NDIlib_destroy", 1, t => t.Destroy),
        ("NDIlib_version", 2, t => t.Version),
        ("NDIlib_find_create_v2", 5, t => t.FindCreateV2),
        ("NDIlib_find_destroy", 6, t => t.FindDestroy),
        ("NDIlib_find_wait_for_sources", 42, t => t.FindWaitForSources),
        ("NDIlib_find_get_current_sources", 43, t => t.FindGetCurrentSources),
        ("NDIlib_recv_create_v3", 84, t => t.RecvCreateV3),
        ("NDIlib_recv_destroy", 23, t => t.RecvDestroy),
        ("NDIlib_recv_capture_v3", 101, t => t.RecvCaptureV3),
        ("NDIlib_recv_free_video_v2", 47, t => t.RecvFreeVideoV2),
        ("NDIlib_send_create", 8, t => t.SendCreate),
        ("NDIlib_send_destroy", 9, t => t.SendDestroy),
        ("NDIlib_send_send_video_v2", 50, t => t.SendSendVideoV2),
        ("NDIlib_send_send_audio_v2", 52, t => t.SendSendAudioV2),
    ];

    public nint Initialize { get; init; }
    public nint Destroy { get; init; }
    public nint Version { get; init; }
    public nint FindCreateV2 { get; init; }
    public nint FindDestroy { get; init; }
    public nint FindWaitForSources { get; init; }
    public nint FindGetCurrentSources { get; init; }
    public nint RecvCreateV3 { get; init; }
    public nint RecvDestroy { get; init; }
    public nint RecvCaptureV3 { get; init; }
    public nint RecvFreeVideoV2 { get; init; }
    public nint SendCreate { get; init; }
    public nint SendDestroy { get; init; }
    public nint SendSendVideoV2 { get; init; }
    public nint SendSendAudioV2 { get; init; }

    /// <summary>Reads the slots out of the struct NDIlib_v5_load returned.</summary>
    public static NdiTable Read(nint table)
    {
        nint Slot(int index) => Marshal.ReadIntPtr(table, index * IntPtr.Size);
        return new NdiTable
        {
            Initialize = Slot(0),
            Destroy = Slot(1),
            Version = Slot(2),
            FindCreateV2 = Slot(5),
            FindDestroy = Slot(6),
            FindWaitForSources = Slot(42),
            FindGetCurrentSources = Slot(43),
            RecvCreateV3 = Slot(84),
            RecvDestroy = Slot(23),
            RecvCaptureV3 = Slot(101),
            RecvFreeVideoV2 = Slot(47),
            SendCreate = Slot(8),
            SendDestroy = Slot(9),
            SendSendVideoV2 = Slot(50),
            SendSendAudioV2 = Slot(52),
        };
    }
}

/// <summary>
/// The installed NDI runtime, loaded once per process. This app doesn't ship NDI (its
/// licence governs redistribution): it loads Processing.NDI.Lib.x64.dll from the folder
/// in NDI_RUNTIME_DIR_V6 (then _V5) or the default NDI Tools install, asks it for its
/// function table with NDIlib_v5_load, and checks every slot it will call against the
/// runtime's export of the same name before calling any of them; a mis-slotted table is
/// refused instead of crashing. Port of NDIKit/NDILibrary.swift.
/// <para>
/// Nothing here throws: <see cref="TryLoad"/> answers false with an <see cref="NdiError"/>
/// whose message tells the operator to install NDI Tools.
/// </para>
/// </summary>
public sealed unsafe class NdiLibrary
{
    public const string RuntimeFileName = "Processing.NDI.Lib.x64.dll";

    private static readonly Lock sharedGate = new();
    private static NdiLibrary? shared;
    private static NdiError? sharedError;

    /// <summary>Finders, receivers and senders hold this while they live, so a reinitialize waits for them.</summary>
    private static readonly InstanceGate gate = new();

    private NdiLibrary(NdiTable table, nint handle, string path)
    {
        Table = table;
        Handle = handle;
        LoadedFrom = path;
    }

    internal NdiTable Table { get; }

    internal nint Handle { get; }

    /// <summary>The runtime file that was loaded.</summary>
    public string LoadedFrom { get; }

    /// <summary>The runtime's version line, e.g. "NDI SDK WIN64 … 6.3.2.0".</summary>
    public string Version => Marshal.PtrToStringUTF8(((delegate* unmanaged<nint>)Table.Version)()) ?? "";

    /// <summary>Where the runtime is looked for, in order.</summary>
    public static IReadOnlyList<string> SearchPaths =>
        CandidatePaths(Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));

    /// <summary>
    /// NDI Tools sets NDI_RUNTIME_DIR_V6 (and V5 for older apps) to its Runtime folder;
    /// after those come the folders the NDI Tools and stand-alone runtime installers use.
    /// </summary>
    internal static IReadOnlyList<string> CandidatePaths(Func<string, string?> environment, string programFiles)
    {
        var paths = new List<string>();
        foreach (var variable in new[] { "NDI_RUNTIME_DIR_V6", "NDI_RUNTIME_DIR_V5" })
            if (environment(variable) is { Length: > 0 } folder) paths.Add(Path.Combine(folder.Trim('"'), RuntimeFileName));
        paths.Add(Path.Combine(programFiles, "NDI", "NDI 6 Tools", "Runtime", RuntimeFileName));
        paths.Add(Path.Combine(programFiles, "NDI", "NDI 6 Runtime", "v6", RuntimeFileName));
        paths.Add(Path.Combine(programFiles, "NDI", "NDI 5 Tools", "Runtime", RuntimeFileName));
        paths.Add(Path.Combine(programFiles, "NDI", "NDI 5 Runtime", "v5", RuntimeFileName));
        return [.. paths.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// The shared runtime. The first successful load is kept for the life of the process.
    /// A missing runtime is looked for again next time (so installing NDI Tools while the
    /// app runs works on the next start of an NDI input or output); other failures stick.
    /// </summary>
    public static bool TryLoad([NotNullWhen(true)] out NdiLibrary? library, [NotNullWhen(false)] out NdiError? error)
    {
        lock (sharedGate)
        {
            if (shared is null && (sharedError is null || sharedError.Kind == NdiErrorKind.RuntimeNotFound))
                (shared, sharedError) = LoadFresh(SearchPaths);
            library = shared;
            error = shared is null ? sharedError : null;
            return shared is not null;
        }
    }

    /// <summary>
    /// Restarts the runtime (NDIlib_destroy then NDIlib_initialize) so it re-reads its
    /// config, e.g. after the operator pins NDI to one network adapter. It only happens
    /// when no finder, receiver or sender is alive; it waits up to
    /// <paramref name="timeout"/> (default 8 s) for the last to go, and answers false if
    /// they don't. True when there's nothing loaded to restart.
    /// </summary>
    public static bool Reinitialize(TimeSpan? timeout = null)
    {
        NdiLibrary? library;
        lock (sharedGate) library = shared;
        if (library is null) return true;
        return gate.TryExclusive(timeout ?? TimeSpan.FromSeconds(8), () =>
        {
            ((delegate* unmanaged<void>)library.Table.Destroy)();
            return ((delegate* unmanaged<byte>)library.Table.Initialize)() != 0;
        }, out var ok) && ok;
    }

    internal static void RetainInstance() => gate.Enter();

    internal static void ReleaseInstance() => gate.Leave();

    /// <summary>Tries each path in turn; the first that loads and has a sound table wins.</summary>
    internal static (NdiLibrary? Library, NdiError? Error) LoadFresh(IReadOnlyList<string> paths)
    {
        var failures = new List<string>();
        foreach (var path in paths)
        {
            if (!File.Exists(path)) continue;
            if (!NativeLibrary.TryLoad(path, out var handle))
            {
                failures.Add($"{path} would not load");
                continue;
            }
            if (!NativeLibrary.TryGetExport(handle, "NDIlib_v5_load", out var loadExport))
                return (null, new NdiError(NdiErrorKind.LoadFailed, ["NDIlib_v5_load missing"]));
            var table = ((delegate* unmanaged<nint>)loadExport)();
            if (table == 0) return (null, new NdiError(NdiErrorKind.LoadFailed, ["NDIlib_v5_load returned NULL"]));
            return Activate(NdiTable.Read(table), handle, path);
        }
        return failures.Count > 0
            ? (null, new NdiError(NdiErrorKind.LoadFailed, failures))
            : (null, new NdiError(NdiErrorKind.RuntimeNotFound, paths));
    }

    /// <summary>Initializes the runtime, but only once every slot has proved to be the function it claims to be.</summary>
    internal static (NdiLibrary? Library, NdiError? Error) Activate(NdiTable table, nint handle, string path)
    {
        var mismatches = TableMismatches(table, name => NativeLibrary.TryGetExport(handle, name, out var address) ? address : 0);
        if (mismatches.Count > 0) return (null, new NdiError(NdiErrorKind.TableMismatch, mismatches));
        if (((delegate* unmanaged<byte>)table.Initialize)() == 0) return (null, new NdiError(NdiErrorKind.InitializeFailed, []));
        VideoIOLog.Note("ndi.runtime", $"loaded {path}");
        return (new NdiLibrary(table, handle, path), null);
    }

    /// <summary>The names of the called slots whose entry isn't the runtime's export of that name.</summary>
    internal static IReadOnlyList<string> TableMismatches(NdiTable table, Func<string, nint> resolve) =>
        [.. NdiTable.CalledSlots.Where(slot => resolve(slot.Name) != slot.Entry(table)).Select(slot => slot.Name)];

    /// <summary>The runtime's export of <paramref name="name"/>, or 0.</summary>
    internal nint Export(string name) => NativeLibrary.TryGetExport(Handle, name, out var address) ? address : 0;
}

/// <summary>
/// Many readers or one writer: NDI instances "enter" while they live; a runtime restart
/// waits until none are alive and holds new ones off while it runs. Port of NDIKit's
/// InstanceGate (NSCondition there, Monitor here).
/// </summary>
internal sealed class InstanceGate
{
    private readonly object sync = new();
    private int active;
    private bool exclusive;

    public void Enter()
    {
        lock (sync)
        {
            while (exclusive) Monitor.Wait(sync);
            active++;
        }
    }

    public void Leave()
    {
        lock (sync)
        {
            active--;
            Monitor.PulseAll(sync);
        }
    }

    /// <summary>Runs <paramref name="body"/> alone, or answers false if instances outlive <paramref name="timeout"/>.</summary>
    public bool TryExclusive<T>(TimeSpan timeout, Func<T> body, out T result)
    {
        var clock = Stopwatch.StartNew();
        lock (sync)
        {
            while (active > 0 || exclusive)
            {
                var remaining = timeout - clock.Elapsed;
                if (remaining <= TimeSpan.Zero || !Monitor.Wait(sync, remaining))
                {
                    result = default!;
                    return false;
                }
            }
            exclusive = true;
        }
        try
        {
            result = body();
        }
        finally
        {
            lock (sync)
            {
                exclusive = false;
                Monitor.PulseAll(sync);
            }
        }
        return true;
    }
}
