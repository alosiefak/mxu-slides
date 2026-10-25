using System.Globalization;
using MxU.Slides.Production.Devices;
using MxU.Slides.Production.Lighting;

namespace MxU.Slides.Production.Actions;

/// <summary>One thing a picker can offer: its stored value and what the person reads.</summary>
public sealed record ProductionChoice(string Id, string Name);

/// <summary>A device as the editors see it.</summary>
public sealed record DeviceInfo(string Id, string Name, DeviceKind Kind);

/// <summary>What the editors' pickers can offer: this PC's devices, what each reports, the library's lighting.</summary>
public sealed record ProductionContext(IReadOnlyList<DeviceInfo> Devices, Func<string?, DeviceKind, DeviceCatalog> Catalog, LightingShow Lighting)
{
    public static ProductionContext Empty { get; } = new([], (_, _) => DeviceCatalog.Empty, new LightingShow());
}

/// <summary>One setting of a production step, for the editor to draw. The rules live here; the window only draws them.</summary>
public abstract record ProductionField(string Label);

/// <summary>Which device of a kind (none chosen: the first one).</summary>
public sealed record DeviceField(DeviceKind Kind) : ProductionField("Device");

/// <summary>Text with suggestions from the device (an OBS scene, a vMix input, an ATEM source): pick one or type.</summary>
public sealed record SuggestField(
    string Label, Func<ProductionAction, string?> Get, Action<ProductionAction, string?> Set,
    Func<ProductionContext, ProductionAction, IReadOnlyList<ProductionChoice>> Suggestions, string Hint) : ProductionField(Label);

/// <summary>One of a fixed list (a look, a cue).</summary>
public sealed record ListField(
    string Label, Func<ProductionAction, string?> Get, Action<ProductionAction, string?> Set,
    Func<ProductionContext, IReadOnlyList<ProductionChoice>> Choices) : ProductionField(Label);

/// <summary>A whole number in a range (an M/E, a macro, a keyer).</summary>
public sealed record WholeNumberField(
    string Label, Func<ProductionAction, int> Get, Action<ProductionAction, int> Set, int Min, int Max) : ProductionField(Label);

/// <summary>A decimal (seconds or per cent); empty means "the usual" (<see cref="EmptyText"/>).</summary>
public sealed record DecimalField(
    string Label, Func<ProductionAction, double?> Get, Action<ProductionAction, double?> Set, double Min, double Max, string Unit, string EmptyText) : ProductionField(Label);

/// <summary>On, off (and, when allowed, "the other way round").</summary>
public sealed record SwitchField(
    string Label, string OnText, string OffText, Func<ProductionAction, bool?> Get, Action<ProductionAction, bool?> Set, string? ToggleText = null) : ProductionField(Label);

/// <summary>A heading and the commands under it, for the Add menu.</summary>
public sealed record ProductionMenuGroup(string Title, IReadOnlyList<ProductionMenuItem> Items);

/// <summary>An Add menu command (it makes a fresh step), or a greyed note when <see cref="Make"/> is null.</summary>
public sealed record ProductionMenuItem(string Title, Func<ProductionAction>? Make, IReadOnlyList<ProductionMenuItem>? Children = null);

/// <summary>
/// What each kind of production step lets you set, how it reads in a list, and the Add menu:
/// the production twin of Core's ActionEditing, kept free of WPF so the rules can be tested.
/// </summary>
public static class ProductionEditing
{
    // MARK: Words

    public static string DeviceWord(DeviceKind? kind) => kind switch
    {
        DeviceKind.Atem => "ATEM",
        DeviceKind.Obs => "OBS",
        DeviceKind.Vmix => "vMix",
        _ => "Lights",
    };

    /// <summary>The name of the kind of step ("Program input").</summary>
    public static string KindTitle(ProductionKind kind) => kind switch
    {
        ProductionKind.AtemProgram => "Program input",
        ProductionKind.AtemPreview => "Preview input",
        ProductionKind.AtemCut => "Cut",
        ProductionKind.AtemAuto => "Auto transition",
        ProductionKind.AtemFadeToBlack => "Fade to black",
        ProductionKind.AtemMacro => "Run macro",
        ProductionKind.AtemAux => "Aux source",
        ProductionKind.AtemUpstreamKey => "Upstream key",
        ProductionKind.AtemDownstreamKey => "Downstream key",
        ProductionKind.ObsProgramScene => "Program scene",
        ProductionKind.ObsPreviewScene => "Preview scene",
        ProductionKind.ObsTransition => "Transition preview to program",
        ProductionKind.ObsStreaming => "Streaming",
        ProductionKind.ObsRecording => "Recording",
        ProductionKind.ObsSourceVisible => "Show or hide a source",
        ProductionKind.VmixCut => "Cut",
        ProductionKind.VmixFade => "Fade",
        ProductionKind.VmixOverlay => "Overlay",
        ProductionKind.VmixStreaming => "Streaming",
        ProductionKind.VmixRecording => "Recording",
        ProductionKind.VmixPreview => "Preview input",
        ProductionKind.VmixActive => "Output input",
        ProductionKind.LightsLook => "Look",
        ProductionKind.LightsRelease => "Release a look",
        ProductionKind.LightsCue => "Cue list",
        ProductionKind.LightsBlackout => "Blackout",
        ProductionKind.LightsMaster => "Grand master",
        _ => "All off",
    };

    /// <summary>The card's heading: "ATEM · PROGRAM INPUT".</summary>
    public static string Header(ProductionAction step) => step.Kind is { } kind
        ? $"{DeviceWord(ProductionAction.DeviceFor(kind))} · {KindTitle(kind)}".ToUpperInvariant()
        : $"UNKNOWN STEP ({step.KindName})";

    /// <summary>One line for a list or a log: "ATEM: Program Camera 2", "Lights: Sermon, 3 s fade".</summary>
    public static string Label(ProductionAction step, ProductionContext context)
    {
        if (step.Kind is not { } kind) return $"Unknown step \"{step.KindName}\" (made by a newer version)";
        var device = DeviceWord(ProductionAction.DeviceFor(kind));
        var me = step.MixEffect is > 1 ? $" (M/E {step.MixEffect})" : "";
        string Source(int? number) => number is { } n ? SourceName(context, step.DeviceId, n) : "?";
        string Text(string? value) => string.IsNullOrWhiteSpace(value) ? "?" : value.Trim();
        string Look() => context.Lighting.Look(step.LookId)?.Name is { Length: > 0 } name ? name : "a missing look";
        string Fade() => step.FadeSeconds is { } fade ? $", {Number(fade)} s fade" : "";
        string OnOff(bool? on, string yes = "on", string no = "off") => on == false ? no : yes;
        var words = kind switch
        {
            ProductionKind.AtemProgram => $"Program {Source(step.Source)}{me}",
            ProductionKind.AtemPreview => $"Preview {Source(step.Source)}{me}",
            ProductionKind.AtemCut => $"Cut{me}",
            ProductionKind.AtemAuto => $"Auto{me}",
            ProductionKind.AtemFadeToBlack => $"Fade to black{me}",
            ProductionKind.AtemMacro => $"Run macro {step.Number ?? 1}" + (MacroName(context, step) is { } macro ? $" ({macro})" : ""),
            ProductionKind.AtemAux => $"Aux {step.Number ?? 1} to {Source(step.Source)}",
            ProductionKind.AtemUpstreamKey => $"Key {step.Number ?? 1} {OnOff(step.On)}{me}",
            ProductionKind.AtemDownstreamKey => $"DSK {step.Number ?? 1} {OnOff(step.On)}",
            ProductionKind.ObsProgramScene => $"Scene {Text(step.Scene)}" + (step.Transition is { Length: > 0 } t ? $" ({t})" : ""),
            ProductionKind.ObsPreviewScene => $"Preview {Text(step.Scene)}",
            ProductionKind.ObsTransition => "Transition" + (step.Transition is { Length: > 0 } tr ? $" ({tr})" : ""),
            ProductionKind.ObsStreaming or ProductionKind.VmixStreaming => OnOff(step.On, "Start streaming", "Stop streaming"),
            ProductionKind.ObsRecording or ProductionKind.VmixRecording => OnOff(step.On, "Start recording", "Stop recording"),
            ProductionKind.ObsSourceVisible => $"{OnOff(step.On, "Show", "Hide")} {Text(step.SceneItem)} in {Text(step.Scene)}",
            ProductionKind.VmixCut => "Cut" + (step.Input is { Length: > 0 } cut ? $" to {VmixInputName(context, step.DeviceId, cut)}" : ""),
            ProductionKind.VmixFade => "Fade" + (step.Input is { Length: > 0 } fadeTo ? $" to {VmixInputName(context, step.DeviceId, fadeTo)}" : "")
                + (step.DurationMs is { } ms ? $", {Number(ms / 1000.0)} s" : ""),
            ProductionKind.VmixOverlay => $"Overlay {step.Number ?? 1} {OnOff(step.On, "in", "out")}"
                + (step.On != false && step.Input is { Length: > 0 } overlay ? $" ({VmixInputName(context, step.DeviceId, overlay)})" : ""),
            ProductionKind.VmixPreview => $"Preview {VmixInputName(context, step.DeviceId, step.Input)}",
            ProductionKind.VmixActive => $"Output {VmixInputName(context, step.DeviceId, step.Input)}",
            ProductionKind.LightsLook => $"{Look()}{Fade()}" + (step.OnTop == true ? ", on top" : ""),
            ProductionKind.LightsRelease => $"Release {Look()}{Fade()}",
            ProductionKind.LightsCue => step.CueId is { } cue ? $"Cue {CueName(context.Lighting, cue)}" : "GO (next cue)",
            ProductionKind.LightsBlackout => step.On is { } on ? $"Blackout {(on ? "on" : "off")}" : "Blackout on/off",
            ProductionKind.LightsMaster => $"Grand master {Number(step.Level ?? 100)} %{Fade()}",
            _ => $"All off{Fade()}",
        };
        return $"{device}: {words}";
    }

    private static string Number(double value) => value.ToString("0.##", CultureInfo.CurrentCulture);

    /// <summary>An ATEM source's name: "Camera 2" when the switcher said, else "source 2".</summary>
    public static string SourceName(ProductionContext context, string? deviceId, int source) =>
        context.Catalog(deviceId, DeviceKind.Atem).Inputs.FirstOrDefault(i => i.Number == source)?.Name ?? $"source {source}";

    private static string? MacroName(ProductionContext context, ProductionAction step) =>
        context.Catalog(step.DeviceId, DeviceKind.Atem).Macros.FirstOrDefault(m => m.Number == (step.Number ?? 1))?.Name;

    private static string VmixInputName(ProductionContext context, string? deviceId, string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "?";
        return int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            && context.Catalog(deviceId, DeviceKind.Vmix).Inputs.FirstOrDefault(i => i.Number == number) is { } known
            ? known.Name
            : input.Trim();
    }

    public static string CueName(LightingShow show, string cueId)
    {
        var index = show.Cues.FindIndex(c => c.Id == cueId);
        if (index < 0) return "(missing)";
        var cue = show.Cues[index];
        var name = cue.Name.Length > 0 ? cue.Name : show.Look(cue.LookId)?.Name ?? "";
        return $"{index + 1}" + (name.Length > 0 ? $" · {name}" : "");
    }

    // MARK: Settings

    /// <summary>The settings a step shows, in order (the delay is always last and drawn by the editor).</summary>
    public static IReadOnlyList<ProductionField> Fields(ProductionAction step)
    {
        if (step.Kind is not { } kind) return [];
        var fields = new List<ProductionField>();
        if (ProductionAction.DeviceFor(kind) is { } deviceKind) fields.Add(new DeviceField(deviceKind));
        var mixEffect = new WholeNumberField("M/E", s => s.MixEffect ?? 1, (s, v) => s.MixEffect = v, 1, 4);
        var source = new SuggestField("Source", s => s.Source?.ToString(CultureInfo.InvariantCulture),
            (s, v) => s.Source = int.TryParse(Leading(v), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? Math.Clamp(n, 0, 65535) : s.Source,
            (c, s) => [.. c.Catalog(s.DeviceId, DeviceKind.Atem).Inputs.Select(i => new ProductionChoice(i.Number.ToString(CultureInfo.InvariantCulture), $"{i.Number} · {i.Name}"))],
            "The switcher's number: 1 is Camera 1, 3010 Media Player 1, 0 Black");
        var scene = new SuggestField("Scene", s => s.Scene, (s, v) => s.Scene = v?.Trim(),
            (c, s) => [.. c.Catalog(s.DeviceId, DeviceKind.Obs).Scenes.Select(n => new ProductionChoice(n, n))],
            "The scene's name in OBS");
        var transition = new SuggestField("Transition", s => s.Transition, (s, v) => s.Transition = string.IsNullOrWhiteSpace(v) ? null : v.Trim(),
            (c, s) => [.. c.Catalog(s.DeviceId, DeviceKind.Obs).Transitions.Select(n => new ProductionChoice(n, n))],
            "Empty: OBS's current transition");
        var obsLength = new DecimalField("Length", s => s.DurationMs / 1000.0, (s, v) => s.DurationMs = v is { } sec ? (int)Math.Round(sec * 1000) : null,
            0.05, 20, "s", "OBS's own");
        var vmixInput = new SuggestField("Input", s => s.Input, (s, v) => s.Input = string.IsNullOrWhiteSpace(v) ? null : Leading(v),
            (c, s) => [.. c.Catalog(s.DeviceId, DeviceKind.Vmix).Inputs.Select(i => new ProductionChoice(i.Number.ToString(CultureInfo.InvariantCulture), $"{i.Number} · {i.Name}"))],
            "The input's number, title or key");
        var look = new ListField("Look", s => s.LookId, (s, v) => s.LookId = v,
            c => [.. c.Lighting.Looks.Select(l => new ProductionChoice(l.Id, l.Name.Length > 0 ? l.Name : "Untitled look"))]);
        var fade = new DecimalField("Fade", s => s.FadeSeconds, (s, v) => s.FadeSeconds = v, 0, 600, "s", "the look's own");
        switch (kind)
        {
            case ProductionKind.AtemProgram or ProductionKind.AtemPreview:
                fields.Add(source);
                fields.Add(mixEffect);
                break;
            case ProductionKind.AtemCut or ProductionKind.AtemAuto or ProductionKind.AtemFadeToBlack:
                fields.Add(mixEffect);
                break;
            case ProductionKind.AtemMacro:
                fields.Add(new SuggestField("Macro", s => (s.Number ?? 1).ToString(CultureInfo.InvariantCulture),
                    (s, v) => s.Number = int.TryParse(Leading(v), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? Math.Clamp(n, 1, 100) : s.Number,
                    (c, s) => [.. c.Catalog(s.DeviceId, DeviceKind.Atem).Macros.Select(m => new ProductionChoice(m.Number.ToString(CultureInfo.InvariantCulture), $"{m.Number} · {m.Name}"))],
                    "The macro's number, from 1"));
                break;
            case ProductionKind.AtemAux:
                fields.Add(new WholeNumberField("Aux", s => s.Number ?? 1, (s, v) => s.Number = v, 1, 24));
                fields.Add(source);
                break;
            case ProductionKind.AtemUpstreamKey:
                fields.Add(new WholeNumberField("Keyer", s => s.Number ?? 1, (s, v) => s.Number = v, 1, 4));
                fields.Add(mixEffect);
                fields.Add(new SwitchField("On air", "On", "Off", s => s.On ?? true, (s, v) => s.On = v));
                break;
            case ProductionKind.AtemDownstreamKey:
                fields.Add(new WholeNumberField("Keyer", s => s.Number ?? 1, (s, v) => s.Number = v, 1, 4));
                fields.Add(new SwitchField("On air", "On", "Off", s => s.On ?? true, (s, v) => s.On = v));
                break;
            case ProductionKind.ObsProgramScene:
                fields.Add(scene);
                fields.Add(transition);
                fields.Add(obsLength);
                break;
            case ProductionKind.ObsPreviewScene:
                fields.Add(scene);
                break;
            case ProductionKind.ObsTransition:
                fields.Add(transition);
                fields.Add(obsLength);
                break;
            case ProductionKind.ObsStreaming or ProductionKind.VmixStreaming or ProductionKind.ObsRecording or ProductionKind.VmixRecording:
                fields.Add(new SwitchField("Do", "Start", "Stop", s => s.On ?? true, (s, v) => s.On = v));
                break;
            case ProductionKind.ObsSourceVisible:
                fields.Add(scene);
                fields.Add(new SuggestField("Source", s => s.SceneItem, (s, v) => s.SceneItem = v?.Trim(),
                    (c, s) => s.Scene is { } chosen && c.Catalog(s.DeviceId, DeviceKind.Obs).SceneSources.TryGetValue(chosen, out var items)
                        ? [.. items.Select(n => new ProductionChoice(n, n))]
                        : [],
                    "The source's name in the scene"));
                fields.Add(new SwitchField("Make it", "Visible", "Hidden", s => s.On ?? true, (s, v) => s.On = v));
                break;
            case ProductionKind.VmixCut:
                fields.Add(vmixInput with { Hint = "Empty: what's on preview" });
                break;
            case ProductionKind.VmixFade:
                fields.Add(vmixInput with { Hint = "Empty: what's on preview" });
                fields.Add(new DecimalField("Length", s => s.DurationMs / 1000.0, (s, v) => s.DurationMs = v is { } sec ? (int)Math.Round(sec * 1000) : null,
                    0.05, 20, "s", "vMix's own"));
                break;
            case ProductionKind.VmixOverlay:
                fields.Add(new WholeNumberField("Overlay", s => s.Number ?? 1, (s, v) => s.Number = v, 1, 4));
                fields.Add(new SwitchField("Do", "In", "Out", s => s.On ?? true, (s, v) => s.On = v));
                fields.Add(vmixInput with { Hint = "Empty: what's on preview (for In)" });
                break;
            case ProductionKind.VmixPreview or ProductionKind.VmixActive:
                fields.Add(vmixInput);
                break;
            case ProductionKind.LightsLook:
                fields.Add(look);
                fields.Add(fade);
                fields.Add(new SwitchField("How", "Add it on top", "Crossfade to it", s => s.OnTop ?? false, (s, v) => s.OnTop = v == true ? true : null));
                break;
            case ProductionKind.LightsRelease:
                fields.Add(look);
                fields.Add(fade);
                break;
            case ProductionKind.LightsCue:
                fields.Add(new ListField("Cue", s => s.CueId ?? "", (s, v) => s.CueId = string.IsNullOrEmpty(v) ? null : v,
                    c => [new ProductionChoice("", "GO: the next cue"), .. c.Lighting.Cues.Select(q => new ProductionChoice(q.Id, CueName(c.Lighting, q.Id)))]));
                break;
            case ProductionKind.LightsBlackout:
                fields.Add(new SwitchField("Blackout", "On", "Off", s => s.On, (s, v) => s.On = v, "On/off (the other way)"));
                break;
            case ProductionKind.LightsMaster:
                fields.Add(new DecimalField("Level", s => s.Level, (s, v) => s.Level = v, 0, 100, "%", "100"));
                fields.Add(fade with { EmptyText = "at once" });
                break;
            case ProductionKind.LightsOff:
                fields.Add(fade with { EmptyText = "at once" });
                break;
        }
        return fields;
    }

    /// <summary>The number at the front of a picked suggestion ("2 · Camera 2" is 2), or the text as typed.</summary>
    public static string? Leading(string? text)
    {
        if (text is null) return null;
        var trimmed = text.Trim();
        var dot = trimmed.IndexOf(" · ", StringComparison.Ordinal);
        return dot > 0 && int.TryParse(trimmed[..dot], NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ? trimmed[..dot] : trimmed;
    }

    /// <summary>Reads a typed delay or fade ("2", "1.5", "1,5"); null when it isn't a number.</summary>
    public static double? ParseSeconds(string text, double min = 0, double max = 3600)
    {
        var cleaned = text.Trim().TrimEnd('s', 'S', '%').Trim();
        if (cleaned.Length == 0) return null;
        if (!double.TryParse(cleaned, NumberStyles.Float, CultureInfo.CurrentCulture, out var value)
            && !double.TryParse(cleaned.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return null;
        return Math.Clamp(Math.Round(value, 2), min, max);
    }

    public static string SecondsText(double? value) => value is { } v ? Number(v) : "";

    /// <summary>Sets a step's delay (0 is stored as none, as shared steps do), in tenths of a second, up to an hour.</summary>
    public static void SetDelay(ProductionAction step, double seconds)
    {
        var rounded = Math.Round(Math.Clamp(seconds, 0, 3600), 1);
        step.DelaySeconds = rounded > 0 ? rounded : null;
    }

    // MARK: New steps

    /// <summary>A new step of a kind, with the usual settings (the first scene, the first look…).</summary>
    public static ProductionAction New(ProductionKind kind, ProductionContext context, string? lookId = null)
    {
        var step = new ProductionAction { Kind = kind };
        var deviceKind = ProductionAction.DeviceFor(kind);
        if (deviceKind is { } dk && context.Devices.Count(d => d.Kind == dk) > 1)
            step.DeviceId = context.Devices.First(d => d.Kind == dk).Id;
        var obs = context.Catalog(step.DeviceId, DeviceKind.Obs);
        switch (kind)
        {
            case ProductionKind.AtemProgram or ProductionKind.AtemPreview:
                step.Source = context.Catalog(step.DeviceId, DeviceKind.Atem).Inputs.FirstOrDefault(i => i.Number > 0)?.Number ?? 1;
                break;
            case ProductionKind.AtemMacro or ProductionKind.AtemAux:
                step.Number = 1;
                if (kind == ProductionKind.AtemAux) step.Source = 1;
                break;
            case ProductionKind.AtemUpstreamKey or ProductionKind.AtemDownstreamKey:
                step.Number = 1;
                step.On = true;
                break;
            case ProductionKind.ObsProgramScene or ProductionKind.ObsPreviewScene:
                step.Scene = obs.Scenes.FirstOrDefault() ?? "";
                break;
            case ProductionKind.ObsSourceVisible:
                step.Scene = obs.Scenes.FirstOrDefault() ?? "";
                step.SceneItem = step.Scene is { } s && obs.SceneSources.TryGetValue(s, out var items) ? items.FirstOrDefault() ?? "" : "";
                step.On = true;
                break;
            case ProductionKind.ObsStreaming or ProductionKind.ObsRecording or ProductionKind.VmixStreaming or ProductionKind.VmixRecording:
                step.On = true;
                break;
            case ProductionKind.VmixOverlay:
                step.Number = 1;
                step.On = true;
                break;
            case ProductionKind.VmixPreview or ProductionKind.VmixActive:
                step.Input = context.Catalog(step.DeviceId, DeviceKind.Vmix).Inputs.FirstOrDefault()?.Number.ToString(CultureInfo.InvariantCulture) ?? "1";
                break;
            case ProductionKind.LightsLook or ProductionKind.LightsRelease:
                step.LookId = lookId ?? context.Lighting.Looks.FirstOrDefault()?.Id;
                break;
            case ProductionKind.LightsBlackout:
                step.On = true;
                break;
            case ProductionKind.LightsMaster:
                step.Level = 100;
                break;
            case ProductionKind.LightsOff:
                step.FadeSeconds = 3;
                break;
        }
        return step;
    }

    /// <summary>The Add menu: ATEM ›, OBS ›, vMix ›, Lights ›, each kind of step under its device.</summary>
    public static IReadOnlyList<ProductionMenuGroup> AddMenu(ProductionContext context)
    {
        ProductionMenuItem Item(ProductionKind kind, string? title = null) => new(title ?? KindTitle(kind), () => New(kind, context));
        var looks = context.Lighting.Looks.Count == 0
            ? new List<ProductionMenuItem> { new("No looks yet: make one in Lighting…", null) }
            : [.. context.Lighting.Looks.Select(l => new ProductionMenuItem(l.Name.Length > 0 ? l.Name : "Untitled look", () => New(ProductionKind.LightsLook, context, l.Id)))];
        return
        [
            new("ATEM", [
                Item(ProductionKind.AtemProgram), Item(ProductionKind.AtemPreview), Item(ProductionKind.AtemCut), Item(ProductionKind.AtemAuto),
                Item(ProductionKind.AtemFadeToBlack), Item(ProductionKind.AtemMacro), Item(ProductionKind.AtemAux),
                Item(ProductionKind.AtemUpstreamKey, "Upstream key on/off"), Item(ProductionKind.AtemDownstreamKey, "Downstream key on/off"),
            ]),
            new("OBS", [
                Item(ProductionKind.ObsProgramScene), Item(ProductionKind.ObsPreviewScene), Item(ProductionKind.ObsTransition, "Transition (studio mode)"),
                Item(ProductionKind.ObsStreaming, "Start or stop streaming"), Item(ProductionKind.ObsRecording, "Start or stop recording"),
                Item(ProductionKind.ObsSourceVisible),
            ]),
            new("vMix", [
                Item(ProductionKind.VmixCut), Item(ProductionKind.VmixFade), Item(ProductionKind.VmixOverlay, "Overlay in or out"),
                Item(ProductionKind.VmixPreview), Item(ProductionKind.VmixActive, "Output (active) input"),
                Item(ProductionKind.VmixStreaming, "Start or stop streaming"), Item(ProductionKind.VmixRecording, "Start or stop recording"),
            ]),
            new("Lights", [
                new ProductionMenuItem("Look", null, looks),
                Item(ProductionKind.LightsCue, "Cue list: GO"), Item(ProductionKind.LightsRelease),
                Item(ProductionKind.LightsBlackout), Item(ProductionKind.LightsMaster), Item(ProductionKind.LightsOff),
            ]),
        ];
    }
}
