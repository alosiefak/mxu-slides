using MxU.Slides.Core.Scene;
using MxU.Slides.VideoIO.Common;

namespace MxU.Slides.VideoIO.Ndi;

/// <summary>
/// An NDI source as a live input. A background thread hunts for a source whose name
/// contains the fragment, receives its frames into the latest-frame ring, and when the
/// source goes quiet for <c>quietSecondsBeforeRehunt</c> seconds (the sender quit, the
/// network dropped) throws the receiver away and hunts again, so the input recovers by
/// itself when the source comes back.
/// <para>
/// States: Searching until first connected, Receiving (detail: the source's full name)
/// while frames flow, Offline while it's quiet or when NDI isn't installed (detail says so).
/// </para>
/// Port of NDIKit/NDIInputSource.swift.
/// </summary>
public sealed class NdiInputSource : VideoInputSourceBase
{
    private readonly NdiLibrary? library;
    private readonly string sourceFragment;
    private readonly int quietSecondsBeforeRehunt;
    private volatile bool running;

    /// <param name="library">The loaded runtime; null makes an input that only reports NDI as not installed.</param>
    /// <param name="sourceNameContaining">Any part of the source's name, e.g. "(Program)".</param>
    /// <param name="name">What the operator calls the input; defaults to the fragment.</param>
    public NdiInputSource(NdiLibrary? library, string sourceNameContaining, int quietSecondsBeforeRehunt = 5, string? name = null)
        : base(name ?? sourceNameContaining,
            library is null ? VideoInputState.OfflineBecause(NdiError.NotInstalledMessage) : VideoInputState.Searching)
    {
        this.library = library;
        sourceFragment = sourceNameContaining;
        this.quietSecondsBeforeRehunt = Math.Max(1, quietSecondsBeforeRehunt);
        if (library is null) return;
        running = true;
        new Thread(ReceiveLoop) { IsBackground = true, Name = $"NDI input {sourceNameContaining}", Priority = ThreadPriority.AboveNormal }.Start();
    }

    /// <summary>Loads the runtime and starts receiving; if NDI isn't installed the input says so in its state.</summary>
    public static NdiInputSource Open(string sourceNameContaining, string? name = null)
    {
        if (NdiLibrary.TryLoad(out var library, out var error)) return new NdiInputSource(library, sourceNameContaining, name: name);
        VideoIOLog.Note("input.ndi", $"runtime unavailable: {error.Message}");
        var offline = new NdiInputSource(null, sourceNameContaining, name: name);
        if (error.Kind != NdiErrorKind.RuntimeNotFound) offline.SetState(VideoInputState.OfflineBecause(error.Message));
        return offline;
    }

    public override void Stop()
    {
        running = false;
        SetState(VideoInputState.Stopped);
    }

    internal void ClearLatestFrameForTesting() => Frames.Clear();

    private bool NotStopped(VideoInputState state) => state.Status != VideoInputStatus.Stopped;

    private void ReceiveLoop()
    {
        while (running)
        {
            using var receiver = NdiReceiver.Create(library);
            if (receiver is null)
            {
                SetStateIf(NotStopped, VideoInputState.Offline);
                return;
            }

            string? connected = null;
            for (var hunts = 0; running && connected is null && hunts < 5; hunts++)
                connected = receiver.Connect(sourceFragment, TimeSpan.FromSeconds(3));
            if (connected is null) continue;
            SetStateIf(NotStopped, VideoInputState.ReceivingFrom(connected));

            var quietSeconds = 0;
            while (running && quietSeconds < quietSecondsBeforeRehunt)
            {
                var arrived = receiver.CaptureVideo(TimeSpan.FromSeconds(1), (pixels, width, height, stride, hasAlpha) =>
                    Deliver(Frames.PublishCopy(pixels, stride, width, height, forceOpaque: !hasAlpha, ShowState.HostClock())));
                if (!arrived)
                {
                    quietSeconds++;
                    SetStateIf(state => state.IsReceiving, VideoInputState.Offline);
                    continue;
                }
                quietSeconds = 0;
                SetStateIf(NotStopped, VideoInputState.ReceivingFrom(connected));
            }
        }
    }
}
