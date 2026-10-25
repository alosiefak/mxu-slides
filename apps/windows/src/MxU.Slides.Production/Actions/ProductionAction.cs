using System.Text.Json.Serialization;
using MxU.Slides.Production.Devices;

namespace MxU.Slides.Production.Actions;

/// <summary>What a production step does. Stored by its camelCase name ("atemProgram").</summary>
public enum ProductionKind
{
    // Blackmagic ATEM
    AtemProgram,
    AtemPreview,
    AtemCut,
    AtemAuto,
    AtemFadeToBlack,
    AtemMacro,
    AtemAux,
    AtemUpstreamKey,
    AtemDownstreamKey,

    // OBS Studio
    ObsProgramScene,
    ObsPreviewScene,
    ObsTransition,
    ObsStreaming,
    ObsRecording,
    ObsSourceVisible,

    // vMix
    VmixCut,
    VmixFade,
    VmixOverlay,
    VmixStreaming,
    VmixRecording,
    VmixPreview,
    VmixActive,

    // Lights
    LightsLook,
    LightsRelease,
    LightsCue,
    LightsBlackout,
    LightsMaster,
    LightsOff,
}

/// <summary>
/// One production step: "Switch the ATEM to camera 2", "OBS: scene Worship", "vMix: Cut",
/// "Lights: Sermon look, 3 s fade". Slides, media items, combos, schedule triggers and timeline
/// cues can each have a list of these, run alongside their own (shared) steps, each after its
/// own delay. They are Windows-only, so they never go in the shared documents: the
/// <see cref="ProductionSteps"/> store keeps each owner's list in "Windows extras/production".
/// <para>
/// Like the shared SlideAction it is one flat record whose kind says which fields matter.
/// Numbers a person sees count from 1, as on the devices' own panels (M/E 1, macro 1, aux 1,
/// keyer 1); ATEM sources keep the switcher's numbers (1 = Camera 1, 3010 = Media Player 1).
/// The kind is kept as text, so a list written by a newer version with kinds this one
/// doesn't know still loads: those steps are skipped, never the whole list.
/// </para>
/// </summary>
public sealed class ProductionAction
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("D").ToUpperInvariant();

    [JsonPropertyName("kind")]
    public string KindName { get; set; } = "";

    /// <summary>The device it goes to (null: the first device of its kind). Lights need none.</summary>
    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; set; }

    /// <summary>Seconds to wait first, after the owner runs (as a shared step's delay).</summary>
    [JsonPropertyName("delaySeconds")]
    public double? DelaySeconds { get; set; }

    /// <summary>ATEM: the M/E (1 is the first).</summary>
    [JsonPropertyName("mixEffect")]
    public int? MixEffect { get; set; }

    /// <summary>ATEM: the video source, by the switcher's number.</summary>
    [JsonPropertyName("source")]
    public int? Source { get; set; }

    /// <summary>ATEM macro, aux or keyer; vMix overlay channel (all from 1).</summary>
    [JsonPropertyName("number")]
    public int? Number { get; set; }

    /// <summary>On or off: a keyer, streaming, recording, a source's visibility, blackout (null toggles blackout).</summary>
    [JsonPropertyName("on")]
    public bool? On { get; set; }

    /// <summary>vMix: the input, by number, title or key.</summary>
    [JsonPropertyName("input")]
    public string? Input { get; set; }

    /// <summary>OBS: the scene.</summary>
    [JsonPropertyName("scene")]
    public string? Scene { get; set; }

    /// <summary>OBS: the source in the scene to show or hide.</summary>
    [JsonPropertyName("sceneItem")]
    public string? SceneItem { get; set; }

    /// <summary>OBS: the transition to use (null: OBS's current one).</summary>
    [JsonPropertyName("transition")]
    public string? Transition { get; set; }

    /// <summary>OBS transition or vMix fade length, in milliseconds (null: the device's own).</summary>
    [JsonPropertyName("durationMs")]
    public int? DurationMs { get; set; }

    /// <summary>Lights: the look.</summary>
    [JsonPropertyName("lookId")]
    public string? LookId { get; set; }

    /// <summary>Lights: a cue to jump to (null: GO, the next cue).</summary>
    [JsonPropertyName("cueId")]
    public string? CueId { get; set; }

    /// <summary>Lights: the fade in seconds (null: the look's own).</summary>
    [JsonPropertyName("fadeSeconds")]
    public double? FadeSeconds { get; set; }

    /// <summary>Lights: add the look on top of what's showing, instead of crossfading to it.</summary>
    [JsonPropertyName("onTop")]
    public bool? OnTop { get; set; }

    /// <summary>Lights: the grand master, in per cent.</summary>
    [JsonPropertyName("level")]
    public double? Level { get; set; }

    /// <summary>The kind, or null for one this version doesn't know.</summary>
    [JsonIgnore]
    public ProductionKind? Kind
    {
        get => Enum.TryParse<ProductionKind>(KindName, ignoreCase: true, out var kind) && Enum.IsDefined(kind) ? kind : null;
        set => KindName = value is { } kind ? Camel(kind) : "";
    }

    public static string Camel(ProductionKind kind)
    {
        var name = kind.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>The device kind a step needs (null for lights, which go to every DMX node).</summary>
    public static DeviceKind? DeviceFor(ProductionKind kind) => kind switch
    {
        <= ProductionKind.AtemDownstreamKey => DeviceKind.Atem,
        <= ProductionKind.ObsSourceVisible => DeviceKind.Obs,
        <= ProductionKind.VmixActive => DeviceKind.Vmix,
        _ => null,
    };

    public ProductionAction Copy() => Core.Json.MxuJson.Clone(this);
}
