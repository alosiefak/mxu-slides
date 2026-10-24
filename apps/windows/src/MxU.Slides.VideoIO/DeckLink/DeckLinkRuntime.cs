namespace MxU.Slides.VideoIO.DeckLink;

/// <summary>
/// One DeckLink card (or one connector-group "sub-device" of a multi-channel card).
/// <see cref="PersistentId"/> survives reboots and slot changes, so it's what settings store.
/// </summary>
public sealed record DeckLinkDevice(
    string Name, long PersistentId, bool SupportsPlayback, bool SupportsInternalKeying, bool SupportsExternalKeying,
    bool SupportsCapture = false, int MaxAudioChannels = 0);

/// <summary>
/// A video mode a card can play out, e.g. "1080p29.97": size, frame rate as
/// <see cref="FrameDuration"/>/<see cref="TimeScale"/> (1001/30000 for 29.97), and whether
/// it can carry a key. <see cref="SupportsBgra"/> is false for the rare mode a card only
/// takes as 8-bit YUV; <see cref="DeckLinkOutput"/> converts for those.
/// </summary>
public sealed record DeckLinkDisplayMode(
    string Name, uint ModeId, int Width, int Height, long FrameDuration, long TimeScale, bool Progressive,
    bool SupportsKeying = true, bool SupportsBgra = true)
{
    public double FramesPerSecond => (double)TimeScale / FrameDuration;

    /// <summary>
    /// The mode to use for a WxH program at a whole frame rate when the operator left the
    /// format on Auto. Same size, within 0.2% of the rate; a broadcast-fractional rate
    /// (29.97 for "30") beats the exact one, then progressive beats interlaced.
    /// Port of DeckLinkDisplayMode.bestMatch.
    /// </summary>
    public static DeckLinkDisplayMode? BestMatch(int width, int height, int framesPerSecond, IEnumerable<DeckLinkDisplayMode> modes)
    {
        double target = framesPerSecond;
        DeckLinkDisplayMode? best = null;
        foreach (var mode in modes)
        {
            if (mode.Width != width || mode.Height != height || Math.Abs(mode.FramesPerSecond - target) / target > 0.002) continue;
            if (best is null || Precedes(mode, best, framesPerSecond)) best = mode;
        }
        return best;

        static bool Exact(DeckLinkDisplayMode mode, int fps) => mode.TimeScale % mode.FrameDuration == 0 && mode.TimeScale / mode.FrameDuration == fps;

        static bool Precedes(DeckLinkDisplayMode left, DeckLinkDisplayMode right, int fps)
        {
            var leftExact = Exact(left, fps);
            var rightExact = Exact(right, fps);
            if (leftExact != rightExact) return rightExact;
            if (left.Progressive != right.Progressive) return left.Progressive;
            return false;
        }
    }
}

/// <summary>
/// Questions about the Desktop Video driver and the cards it can see. Every answer
/// degrades quietly: with no driver installed there are no devices, no modes, no
/// version, and nothing throws. Calls go through the driver's COM API on an MTA thread
/// (see <see cref="Mta"/>); they take a few milliseconds each, so the app should ask
/// off the UI thread or cache the answers. Port of DeckLinkKit/DeckLinkDevices.swift
/// and the matching parts of decklink_shim.cpp.
/// </summary>
public static class DeckLinkRuntime
{
    /// <summary>The oldest Desktop Video major version whose interfaces these declarations match.</summary>
    public const long RequiredApiMajor = 16;

    public const string NotInstalledMessage = "Desktop Video is not installed (blackmagicdesign.com/support)";

    /// <summary>True when the Desktop Video driver is installed (its device iterator can be created).</summary>
    public static bool IsAvailable => Mta.Run(() =>
    {
        var iterator = DeckLinkCom.Create<IDeckLinkIterator>(DeckLinkIds.CDeckLinkIterator);
        DeckLinkCom.Release(iterator);
        return iterator is not null;
    });

    /// <summary>The driver's API version packed as 0xMMmmpp00 (16.0 = 0x10000000), or 0 when unknown.</summary>
    public static long ApiVersion => Mta.Run(() =>
    {
        var information = DeckLinkCom.Create<IDeckLinkAPIInformation>(DeckLinkIds.CDeckLinkAPIInformation);
        try { return information is not null && information.GetInt(Bmd.APIVersion, out var version) == 0 ? version : 0; }
        finally { DeckLinkCom.Release(information); }
    });

    /// <summary>"16.0.1", or null when no driver answers.</summary>
    public static string? ApiVersionString => FormatVersion(ApiVersion);

    /// <summary>True when a driver is installed but older than <see cref="RequiredApiMajor"/>.</summary>
    public static bool RuntimeIsTooOld => IsTooOld(ApiVersion);

    internal static string? FormatVersion(long raw) => raw == 0 ? null : $"{(raw >> 24) & 0xFF}.{(raw >> 16) & 0xFF}.{(raw >> 8) & 0xFF}";

    internal static bool IsTooOld(long raw) => raw != 0 && ((raw >> 24) & 0xFF) < RequiredApiMajor;

    /// <summary>Every card the driver sees, playback and capture alike.</summary>
    public static IReadOnlyList<DeckLinkDevice> Devices() => Mta.Run(() =>
    {
        var devices = new List<DeckLinkDevice>();
        var iterator = DeckLinkCom.Create<IDeckLinkIterator>(DeckLinkIds.CDeckLinkIterator);
        if (iterator is null) return devices;
        try
        {
            for (var index = 0; iterator.Next(out var device) == 0 && device is not null; index++)
            {
                try { devices.Add(Describe(device, index)); }
                finally { DeckLinkCom.Release(device); }
            }
        }
        finally { DeckLinkCom.Release(iterator); }
        return devices;
    });

    /// <summary>The modes a card can play out (as BGRA or 8-bit YUV). Empty for a missing card or no driver.</summary>
    public static IReadOnlyList<DeckLinkDisplayMode> DisplayModes(long persistentId) => Mta.Run(() =>
    {
        var modes = new List<DeckLinkDisplayMode>();
        var device = FindDevice(persistentId);
        if (device is null) return modes;
        IDeckLinkDisplayModeIterator? iterator = null;
        try
        {
            if (device is not IDeckLinkOutput output || output.GetDisplayModeIterator(out iterator) != 0 || iterator is null) return modes;
            while (iterator.Next(out var mode) == 0 && mode is not null)
            {
                try
                {
                    if (mode.GetFrameRate(out var duration, out var scale) != 0 || duration == 0) continue;
                    var id = mode.GetDisplayMode();
                    var bgra = Supports(output, id, Bmd.Format8BitBGRA, Bmd.SupportedVideoModeDefault);
                    if (!bgra && !Supports(output, id, Bmd.Format8BitYUV, Bmd.SupportedVideoModeDefault)) continue;
                    mode.GetName(out var name);
                    modes.Add(new DeckLinkDisplayMode(
                        name ?? "", id, mode.GetWidth(), mode.GetHeight(), duration, scale,
                        mode.GetFieldDominance() == Bmd.ProgressiveFrame,
                        SupportsKeying: Supports(output, id, Bmd.Format8BitBGRA, Bmd.SupportedVideoModeKeying),
                        SupportsBgra: bgra));
                }
                finally { DeckLinkCom.Release(mode); }
            }
            return modes;
        }
        finally
        {
            DeckLinkCom.Release(iterator);
            DeckLinkCom.Release(device);
        }
    });

    /// <summary>The output mode set in Desktop Video Setup, or null when there is none (or no card).</summary>
    public static uint? ConfiguredOutputMode(long persistentId) => WithDevice(persistentId, device =>
        device is IDeckLinkConfiguration configuration && configuration.GetInt(Bmd.ConfigDefaultVideoOutputMode, out var mode) == 0 && mode != 0
            ? (uint?)mode : null);

    /// <summary>True when the card is locked to a reference (genlock) signal. External keying needs one.</summary>
    public static bool ReferenceLocked(long persistentId) => WithDevice(persistentId, device =>
        device is IDeckLinkStatus status && status.GetFlag(Bmd.StatusReferenceSignalLocked, out var locked) == 0 && locked != 0);

    /// <summary>"single", "dual" or "quad" SDI link, or null when the card doesn't say.</summary>
    public static string? OutputLinkConfiguration(long persistentId) => WithDevice(persistentId, device =>
        device is IDeckLinkConfiguration configuration && configuration.GetInt(Bmd.ConfigSDIOutputLinkConfiguration, out var link) == 0
            ? (uint)link switch
            {
                Bmd.LinkSingle => "single",
                Bmd.LinkDual => "dual",
                Bmd.LinkQuad => "quad",
                _ => null,
            }
            : null);

    /// <summary>
    /// Finds a card by persistent id. The caller owns the result and releases it with
    /// <see cref="DeckLinkCom.Release"/>. Must run on an MTA thread.
    /// </summary>
    internal static IDeckLink? FindDevice(long persistentId)
    {
        var iterator = DeckLinkCom.Create<IDeckLinkIterator>(DeckLinkIds.CDeckLinkIterator);
        if (iterator is null) return null;
        try
        {
            for (var index = 0; iterator.Next(out var device) == 0 && device is not null; index++)
            {
                if (StableId(device as IDeckLinkProfileAttributes, index) == persistentId) return device;
                DeckLinkCom.Release(device);
            }
            return null;
        }
        finally { DeckLinkCom.Release(iterator); }
    }

    /// <summary>
    /// The persistent id when the card has one, else its topological (slot) id, else its
    /// position in the list with bit 62 set so it can't collide with a real id.
    /// </summary>
    internal static long StableId(IDeckLinkProfileAttributes? attributes, int index)
    {
        if (attributes is not null && attributes.GetInt(Bmd.PersistentID, out var persistent) == 0) return persistent;
        if (attributes is not null && attributes.GetInt(Bmd.TopologicalID, out var topological) == 0) return topological;
        return (long)(uint)index | (1L << 62);
    }

    internal static bool Supports(IDeckLinkOutput output, uint mode, uint pixelFormat, uint flags) =>
        output.DoesSupportVideoMode(Bmd.VideoConnectionUnspecified, mode, pixelFormat, Bmd.NoVideoConversion, flags, out _, out var supported) == 0
        && supported != 0;

    private static DeckLinkDevice Describe(IDeckLink device, int index)
    {
        var attributes = device as IDeckLinkProfileAttributes;
        device.GetDisplayName(out var name);
        long io = 0, channels = 0;
        int internalKey = 0, externalKey = 0;
        var hasIo = attributes?.GetInt(Bmd.VideoIOSupport, out io) == 0;
        if (attributes?.GetInt(Bmd.MaximumAudioChannels, out channels) != 0) channels = 0;
        if (attributes?.GetFlag(Bmd.SupportsInternalKeying, out internalKey) != 0) internalKey = 0;
        if (attributes?.GetFlag(Bmd.SupportsExternalKeying, out externalKey) != 0) externalKey = 0;
        return new DeckLinkDevice(
            name ?? "", StableId(attributes, index),
            SupportsPlayback: hasIo && (io & Bmd.DeviceSupportsPlayback) != 0,
            SupportsInternalKeying: internalKey != 0,
            SupportsExternalKeying: externalKey != 0,
            SupportsCapture: hasIo && (io & Bmd.DeviceSupportsCapture) != 0,
            MaxAudioChannels: (int)channels);
    }

    private static T? WithDevice<T>(long persistentId, Func<IDeckLink, T?> body) => Mta.Run(() =>
    {
        var device = FindDevice(persistentId);
        if (device is null) return default;
        try { return body(device); }
        finally { DeckLinkCom.Release(device); }
    });
}
