using System.Text.Json;
using System.Text.Json.Serialization;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;

namespace MxU.Slides.Production.Devices;

/// <summary>The kinds of production device the app can talk to.</summary>
[JsonConverter(typeof(CamelCaseEnum<DeviceKind>))]
public enum DeviceKind
{
    /// <summary>A Blackmagic ATEM video switcher (UDP 9910).</summary>
    Atem,

    /// <summary>OBS Studio, through obs-websocket v5 (TCP 4455).</summary>
    Obs,

    /// <summary>vMix, through its web API (HTTP 8088).</summary>
    Vmix,

    /// <summary>A DMX lighting node that speaks Art-Net (UDP 6454).</summary>
    ArtNet,

    /// <summary>A DMX lighting node or desk that speaks sACN, E1.31 (UDP 5568).</summary>
    Sacn,
}

/// <summary>How DMX reaches the lights: to one address, or to everyone on the network.</summary>
[JsonConverter(typeof(CamelCaseEnum<DmxDelivery>))]
public enum DmxDelivery
{
    /// <summary>Straight to one node's address (the most reliable on a busy network).</summary>
    Unicast,

    /// <summary>Art-Net: to every node on the network (the address is a broadcast address, or 255.255.255.255).</summary>
    Broadcast,

    /// <summary>sACN: to the universe's multicast group (239.255.x.y), which any listening node joins.</summary>
    Multicast,
}

/// <summary>
/// One production device, as this PC knows it: what it is, where it is and how to reach it.
/// Kept in production.json beside settings.json, never in the library, because addresses
/// belong to the room this PC is in. Passwords aren't here either: they're encrypted
/// separately (<see cref="DeviceSecrets"/>).
/// </summary>
public sealed class DeviceSettings
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("D").ToUpperInvariant();

    [JsonPropertyName("kind")]
    public DeviceKind Kind { get; set; }

    /// <summary>What the operator calls it ("Main ATEM", "Stream PC").</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>The device's address (an IP address or a computer name). For DMX broadcast or multicast it may be empty.</summary>
    [JsonPropertyName("host")]
    public string Host { get; set; } = "";

    /// <summary>The port, or null for the usual one (<see cref="DefaultPort"/>).</summary>
    [JsonPropertyName("port")]
    public int? Port { get; set; }

    /// <summary>Off: the app leaves it alone (no connecting, no sending) but keeps its settings.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>vMix: the web controller's user name, when it asks for one.</summary>
    [JsonPropertyName("user")]
    public string? User { get; set; }

    /// <summary>DMX: to one address, broadcast (Art-Net) or multicast (sACN).</summary>
    [JsonPropertyName("delivery")]
    public DmxDelivery? Delivery { get; set; }

    /// <summary>DMX: the show's universes this node is sent (1 is the first).</summary>
    [JsonPropertyName("universes")]
    public List<int>? Universes { get; set; }

    /// <summary>sACN: the priority receivers merge by (0 to 200; 100 is the usual).</summary>
    [JsonPropertyName("priority")]
    public int? Priority { get; set; }

    [JsonIgnore]
    public bool IsDmx => Kind is DeviceKind.ArtNet or DeviceKind.Sacn;

    /// <summary>Whether this kind can have a password (OBS's server password, vMix's web controller).</summary>
    [JsonIgnore]
    public bool TakesPassword => Kind is DeviceKind.Obs or DeviceKind.Vmix;

    [JsonIgnore]
    public int EffectivePort => Port is > 0 and < 65536 ? Port.Value : DefaultPort(Kind);

    [JsonIgnore]
    public IReadOnlyList<int> EffectiveUniverses => Universes is { Count: > 0 } list ? list : [1];

    [JsonIgnore]
    public DmxDelivery EffectiveDelivery => Delivery ?? (Kind == DeviceKind.Sacn ? DmxDelivery.Multicast : DmxDelivery.Unicast);

    public static int DefaultPort(DeviceKind kind) => kind switch
    {
        DeviceKind.Atem => 9910,
        DeviceKind.Obs => 4455,
        DeviceKind.Vmix => 8088,
        DeviceKind.ArtNet => 6454,
        _ => 5568,
    };

    /// <summary>The kind as the operator reads it.</summary>
    public static string KindName(DeviceKind kind) => kind switch
    {
        DeviceKind.Atem => "Blackmagic ATEM",
        DeviceKind.Obs => "OBS Studio",
        DeviceKind.Vmix => "vMix",
        DeviceKind.ArtNet => "Art-Net (DMX)",
        _ => "sACN / E1.31 (DMX)",
    };

    /// <summary>A new device of a kind with the usual name and address (OBS and vMix usually run on this PC).</summary>
    public static DeviceSettings New(DeviceKind kind) => new()
    {
        Kind = kind,
        Name = kind switch
        {
            DeviceKind.Atem => "ATEM",
            DeviceKind.Obs => "OBS",
            DeviceKind.Vmix => "vMix",
            DeviceKind.ArtNet => "Art-Net lights",
            _ => "sACN lights",
        },
        Host = kind is DeviceKind.Obs or DeviceKind.Vmix ? "127.0.0.1" : kind == DeviceKind.Atem ? "192.168.10.240" : "",
        Delivery = kind switch { DeviceKind.ArtNet => DmxDelivery.Broadcast, DeviceKind.Sacn => DmxDelivery.Multicast, _ => null },
        Universes = kind is DeviceKind.ArtNet or DeviceKind.Sacn ? [1] : null,
    };

    public DeviceSettings Copy() => MxuJson.Clone(this);
}

/// <summary>Everything production.json holds: this PC's devices, and its sACN source id.</summary>
public sealed class ProductionSettings
{
    [JsonPropertyName("devices")]
    public List<DeviceSettings> Devices { get; set; } = [];

    /// <summary>
    /// sACN's "CID": a number that names this PC as a lighting source, the same every time, so
    /// a node that merges several sources can tell us apart. Made the first time it's needed.
    /// </summary>
    [JsonPropertyName("sacnSourceId")]
    public Guid? SacnSourceId { get; set; }

    /// <summary>Reads production.json (a missing or unreadable file is an empty setup, never a crash).</summary>
    public static ProductionSettings Load(string path)
    {
        try
        {
            if (File.Exists(path)) return MxuJson.Deserialize<ProductionSettings>(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Fall through: start empty; the next save writes a good file.
        }
        return new ProductionSettings();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        LibraryStore.AtomicWrite(path, MxuJson.SerializeToUtf8(this));
    }
}

/// <summary>Writes an enum as its camelCase name ("artNet"), the way the shared documents spell their kinds.</summary>
public sealed class CamelCaseEnum<T>() : JsonStringEnumConverter<T>(JsonNamingPolicy.CamelCase) where T : struct, Enum;
