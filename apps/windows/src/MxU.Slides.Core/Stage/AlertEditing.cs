using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Stage;

/// <summary>
/// The Alerts panel's rules: what a row and the inline editor say, which {blanks} the operator
/// fills in before sending, and how the sent message is composed. Port of the logic in
/// Sources/Modules/AlertsModule.swift (AlertsModule and AlertInlineEditor), so the panel only draws.
/// </summary>
public static class AlertEditing
{
    /// <summary>Where an alert shows when it doesn't say: the confidence monitors.</summary>
    public static AlertTarget Target(AlertPreset preset) => preset.Target ?? AlertTarget.Confidence;

    /// <summary>The short caption beside a row's name.</summary>
    public static string LocationCaption(AlertTarget target) => target switch
    {
        AlertTarget.Audience => "Audience",
        AlertTarget.Both => "Confidence + Audience",
        _ => "Confidence",
    };

    /// <summary>The OUTPUT LOCATION menu's title for a choice.</summary>
    public static string LocationTitle(AlertTarget target) => target switch
    {
        AlertTarget.Audience => "Audience Screens",
        AlertTarget.Both => "Confidence and Audience",
        _ => "Confidence Monitors",
    };

    /// <summary>The OUTPUT LOCATION menu, in the Mac's order.</summary>
    public static readonly IReadOnlyList<AlertTarget> LocationChoices = [AlertTarget.Both, AlertTarget.Confidence, AlertTarget.Audience];

    public const string LocationHelp =
        "Confidence monitors show this alert only when their layout includes Alerts, or when the alert is linked as a text object.\n\n"
        + "Audience screens show it only when the active output preset lets its layer through (the Alerts layer unless you pick another).";

    /// <summary>Confidence is the default and is stored as absent, as on the Mac.</summary>
    public static void SetTarget(AlertPreset preset, AlertTarget target) => preset.Target = target == AlertTarget.Confidence ? null : target;

    /// <summary>An alert going to the audience picks its layer and theme; a confidence-only one has neither.</summary>
    public static bool ShowsLayerAndTheme(AlertTarget target) => target != AlertTarget.Confidence;

    /// <summary>The LAYER menu: top layer first (LayerKind.allCases.reversed() on the Mac).</summary>
    public static IReadOnlyList<LayerKind> LayerChoices => [.. LayerKinds.All.Reverse()];

    public static LayerKind AssignedLayer(AlertPreset preset) => LayerKinds.Parse(preset.Layer) ?? LayerKind.Alerts;

    /// <summary>The Alerts layer is the default and is stored as absent.</summary>
    public static void SetLayer(AlertPreset preset, LayerKind layer) => preset.Layer = layer == LayerKind.Alerts ? null : layer.Raw();

    /// <summary>The THEME menu's title: "Banner" (the built-in amber band) unless a theme dresses it.</summary>
    public static string ThemeName(AlertPreset preset, Func<string, string?> themeName) =>
        preset.ThemeId is { Length: > 0 } id && themeName(id) is { } name ? name : "Banner";

    public static string BehaviorName(AlertBehavior behavior) => behavior == AlertBehavior.Flash ? "Flash" : "Persist";

    public const string BehaviorHelp = "Flash blinks for attention; Persist holds steady until taken down";

    /// <summary>
    /// The blanks a row offers before sending: the message's {tokens}, except those naming a
    /// timer ({Sermon}), which are left in for the outputs to keep counting.
    /// </summary>
    public static List<string> FillInTokens(string message, IEnumerable<string> timerNames)
    {
        var timers = timerNames.ToList();
        return [.. AlertTokens.TokenNames(message).Where(token => !IsTimerToken(token, timers))];
    }

    public static bool IsTimerToken(string token, IEnumerable<string> timerNames) =>
        timerNames.Any(name => string.Equals(name, token, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The message as sent: the typed blanks filled in, and each timer token kept as "{name}" so
    /// the outputs read the live timer (timerTokenIdentity in Swift; the identity wins over anything typed).
    /// </summary>
    public static string Compose(string template, IReadOnlyDictionary<string, string>? typed, IEnumerable<string> timerNames)
    {
        var timers = timerNames.ToList();
        var values = new Dictionary<string, string>(typed ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var name in AlertTokens.TokenNames(template).Where(n => IsTimerToken(n, timers))) values[name] = $"{{{name}}}";
        return AlertTokens.Compose(template, values);
    }

    /// <summary>
    /// The inline editor's commit: a name and phrase that trim to nothing are ignored, so a
    /// half-cleared box never blanks the alert.
    /// </summary>
    public static void Commit(AlertPreset preset, string name, string template)
    {
        var trimmedName = name.TrimmingWhitespaces();
        var trimmedTemplate = template.TrimmingWhitespacesAndNewlines();
        if (trimmedName.Length > 0) preset.Name = trimmedName;
        if (trimmedTemplate.Length > 0) preset.Message = trimmedTemplate;
    }

    /// <summary>A row's button: Go Live, or Turn Off while it is the alert up.</summary>
    public static string LiveButtonTitle(bool isLive) => isLive ? "Turn Off" : "Go Live";

    public static string LiveButtonHelp(bool isLive) => isLive ? "Take this alert down" : "Go live (Enter in a blank fires too)";

    public const string SendUpdateHelp = "Update the on-air alert with the text and blanks as they read now";

    public const string EmptyHint = "Alerts fire to confidence monitors, audience screens, or both.";
}
