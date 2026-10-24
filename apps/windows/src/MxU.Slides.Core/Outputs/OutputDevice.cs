namespace MxU.Slides.Core.Outputs;

public enum OutputDeviceKind
{
    /// <summary>A monitor or projector connected to this computer.</summary>
    Display,

    /// <summary>An NDI network sender.</summary>
    Ndi,

    /// <summary>A Blackmagic DeckLink SDI/HDMI output card.</summary>
    DeckLink,
}

/// <summary>
/// The string ids that name what carries a screen or slice out of the
/// computer: <c>display:\\.\DISPLAY2</c>, <c>ndi:Stream Feed</c>,
/// <c>decklink:&lt;persistent id&gt;</c>. These are what
/// <see cref="OutputConfiguration.AssignDevice"/> takes and what the screen
/// setup saves (in the Mac's <c>deviceDisplayUUID</c> field, which on the Mac
/// only ever names a display, so an id without a known prefix is read as one).
/// </summary>
public static class OutputDevice
{
    public const string DisplayPrefix = "display:";
    public const string NdiPrefix = "ndi:";
    public const string DeckLinkPrefix = "decklink:";

    /// <summary>A display, by its Windows GDI device name (e.g. <c>\\.\DISPLAY2</c>).</summary>
    public static string Display(string deviceName) => DisplayPrefix + deviceName;

    /// <summary>An NDI sender, by the source name receivers will see.</summary>
    public static string Ndi(string senderName) => NdiPrefix + senderName;

    /// <summary>A DeckLink output, by the card's persistent id.</summary>
    public static string DeckLink(string persistentId) => DeckLinkPrefix + persistentId;

    public static OutputDeviceKind Kind(string deviceId) =>
        deviceId.StartsWith(NdiPrefix, StringComparison.Ordinal) ? OutputDeviceKind.Ndi
        : deviceId.StartsWith(DeckLinkPrefix, StringComparison.Ordinal) ? OutputDeviceKind.DeckLink
        : OutputDeviceKind.Display;

    /// <summary>The part after the prefix: the GDI device name, NDI name or DeckLink id.</summary>
    public static string Value(string deviceId) => Kind(deviceId) switch
    {
        OutputDeviceKind.Ndi => deviceId[NdiPrefix.Length..],
        OutputDeviceKind.DeckLink => deviceId[DeckLinkPrefix.Length..],
        _ => deviceId.StartsWith(DisplayPrefix, StringComparison.Ordinal) ? deviceId[DisplayPrefix.Length..] : deviceId,
    };
}
