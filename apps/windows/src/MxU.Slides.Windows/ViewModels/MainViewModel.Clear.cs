using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>One Clear button: a function (Slides, Media, Music…) or a layer. Green while it has something live.</summary>
public sealed partial class ClearButtonViewModel(string name, string? keyHint, ShowFunction? function, LayerKind? layer) : ObservableObject
{
    public string Name { get; } = name;

    /// <summary>The key that clears it too ("F2"), shown beside its name.</summary>
    public string? KeyHint { get; } = keyHint;

    public string Label => KeyHint is null ? Name : $"{Name}  {KeyHint}";

    public ShowFunction? Function { get; } = function;

    public LayerKind? Layer { get; } = layer;

    /// <summary>The button's tooltip, as the Mac's ("Clear Function: Music", "Clear Layer: Slide").</summary>
    public string Help => Function is not null ? $"Clear function: {Name}" : $"Clear layer: {Name} (green means live)";

    [ObservableProperty]
    public partial bool IsLive { get; set; }
}

/// <summary>
/// The Clear controls, as the Mac's Clear chip has them: Clear All (only what its checklist
/// ticks), each function on its own (Slides, Media, Overlays, Music, Alerts, Inputs), and each
/// layer on its own, every one green while it has something live. Port of Sources/ClearChip.swift
/// and the Present menu's clears (PresentCommands.swift).
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The function buttons, with their keys (F2–F6, as on the Mac and ProPresenter).</summary>
    public ObservableCollection<ClearButtonViewModel> ClearFunctionButtons { get; } =
    [
        new("Slides", "F2", ShowFunction.Slides, null),
        new("Media", "F3", ShowFunction.Media, null),
        new("Overlays", "F4", ShowFunction.Overlays, null),
        new("Music", "F5", ShowFunction.Audio, null),
        new("Alerts", "F6", ShowFunction.Alerts, null),
        new("Inputs", null, ShowFunction.Inputs, null),
    ];

    /// <summary>One button per layer, top of the stack first.</summary>
    public ObservableCollection<ClearButtonViewModel> ClearLayerButtons { get; } =
        [.. LayerKinds.All.Reverse().Select(layer => new ClearButtonViewModel(layer.DisplayName(), null, null, layer))];

    /// <summary>Something Clear All would clear is live (the button turns green).</summary>
    [ObservableProperty]
    public partial bool ClearAllHasContent { get; set; }

    /// <summary>"Leaves Inputs up · right-click to change", when the checklist skips anything.</summary>
    [ObservableProperty]
    public partial string ClearAllNote { get; set; } = "";

    /// <summary>What Clear All leaves alone on this computer.</summary>
    public HashSet<ShowFunction> ClearAllSkips => ClearAllScope.Skips(Settings.ClearAllSkips, null);

    /// <summary>The checklist: tick or untick what Clear All clears.</summary>
    public void SetClearAllSweeps(ShowFunction function, bool swept)
    {
        Settings.ClearAllSkips = ClearAllScope.Set(function, swept, Settings.ClearAllSkips, null).Stored;
        Settings.Save();
        RefreshClearState();
    }

    [RelayCommand]
    private void ClearButton(ClearButtonViewModel? button)
    {
        if (button?.Function is { } function) ClearFunction(function);
        else if (button?.Layer is { } layer) ClearLayer(layer);
    }

    /// <summary>Takes live cameras, capture cards and NDI sources down (they don't go with Clear All unless it's ticked to).</summary>
    [RelayCommand]
    private void ClearInputs()
    {
        Clear(() => Show.Clear(ShowFunction.Inputs));
        MarkLiveInputs();
    }

    /// <summary>Clears one function, exactly as its key does (F2 Slides, F3 Media, F4 Overlays, F5 Music, F6 Alerts).</summary>
    public void ClearFunction(ShowFunction function)
    {
        switch (function)
        {
            case ShowFunction.Slides: ClearSlide(); break;
            case ShowFunction.Media: ClearMedia(); break;
            case ShowFunction.Overlays: ClearOverlays(); break;
            case ShowFunction.Audio: ClearAudio(); break;
            case ShowFunction.Alerts: ClearAlerts(); break;
            default: ClearInputs(); break;
        }
        RefreshClearState();
    }

    /// <summary>Clears one layer: the slide or overlays play their Outs first, as with their function's clear.</summary>
    public void ClearLayer(LayerKind layer)
    {
        Clear(() => Show.Clear(layer, ShowState.HostClock()));
        if (layer == LayerKind.VideoInput) MarkLiveInputs();
        RefreshClearState();
    }

    /// <summary>A function has something live (its button is green).</summary>
    public bool HasContent(ShowFunction function) => function switch
    {
        ShowFunction.Slides => Show.Live is not null,
        ShowFunction.Media => Show.LiveMedia.Values.Any(m => !m.IsLiveInput()),
        ShowFunction.Inputs => Show.LiveMedia.Values.Any(m => m.IsLiveInput()),
        ShowFunction.Overlays => Show.LiveOverlays.Count > 0,
        ShowFunction.Audio => audio?.Players.Count > 0,
        _ => Show.HasAlert,
    };

    /// <summary>A layer has something live.</summary>
    public bool HasContent(LayerKind layer) => layer switch
    {
        LayerKind.Slide => Show.Live is not null,
        LayerKind.Overlays => Show.LiveOverlays.Any(o => (LayerKinds.Parse(o.Layer) ?? LayerKind.Overlays) == LayerKind.Overlays),
        LayerKind.Alerts => Show.HasAlert || Show.LiveOverlays.Any(o => LayerKinds.Parse(o.Layer) == LayerKind.Alerts),
        _ => Show.LiveMedia.ContainsKey(layer),
    };

    /// <summary>Brings the buttons' green up to date (after a clear, and four times a second).</summary>
    private void RefreshClearState()
    {
        foreach (var button in ClearFunctionButtons) button.IsLive = HasContent(button.Function!.Value);
        foreach (var button in ClearLayerButtons) button.IsLive = HasContent(button.Layer!.Value);
        var skips = ClearAllSkips;
        ClearAllHasContent = ShowFunctions.All.Any(f => !skips.Contains(f) && HasContent(f));
        var left = ShowFunctions.All.Where(skips.Contains).Select(f => f.DisplayName()).ToList();
        ClearAllNote = left.Count == 0 ? "" : $"Leaves {JoinedWithAnd(left)} up · right-click to change";
    }

    private static string JoinedWithAnd(IReadOnlyList<string> names) => names.Count switch
    {
        1 => names[0],
        2 => $"{names[0]} and {names[1]}",
        _ => $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}",
    };
}
