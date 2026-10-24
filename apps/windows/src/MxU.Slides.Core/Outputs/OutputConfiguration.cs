using System.Collections.Immutable;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Outputs;

/// <summary>What a screen shows: the program (slides, media) or the stage display.</summary>
public enum ScreenRole
{
    Audience,
    Confidence,
}

public static class ScreenRoles
{
    public static string Raw(this ScreenRole role) => role == ScreenRole.Confidence ? "confidence" : "audience";

    public static ScreenRole? Parse(string? raw) => raw switch
    {
        "audience" => ScreenRole.Audience,
        "confidence" => ScreenRole.Confidence,
        _ => null,
    };

    public static string DisplayName(this ScreenRole role) => role == ScreenRole.Confidence ? "Confidence Monitor" : "Audience";
}

/// <summary>
/// A screen: a named canvas of a fixed size and frame rate that looks and
/// routing are set up against, before (or without) any hardware behind it.
/// The Mac calls it a placeholder screen; a device is assigned to it later.
/// </summary>
public sealed record PlaceholderScreen(Guid Id, string Name, int Width, int Height, int FramesPerSecond = 30)
{
    public SceneSize Size => new(Width, Height);
}

/// <summary>One slice a device carries, and the screen it belongs to.</summary>
public sealed record OutputCarry(Guid SliceId, Guid ScreenId);

/// <summary>
/// The screen and output setup: the screens, their slices, what device
/// carries each one, and every per-output setting (role, scale mode,
/// corrections, masks, placement, video delay). This is the configuration
/// half of the Mac's OutputManager.swift; opening windows and rendering stay
/// in the WPF app, which reads this and listens to <see cref="StateChanged"/>.
/// <para>
/// Ids: a slice is addressed by its id, and a screen's own id addresses its
/// default slice, so every "for screen" setting below works for slices too.
/// Change the setup from one thread (the UI thread). The stores are immutable
/// collections replaced on every change, so a render thread can read them, or
/// call the <c>…Provider</c> functions, at any time.
/// </para>
/// </summary>
public sealed partial class OutputConfiguration
{
    /// <summary>The most video delay an output can have, in frames at its rate.</summary>
    public const int MaxVideoDelayFrames = 15;

    private static readonly OutputAdjustments NoAdjustments = new();

    private ImmutableList<DisplaySnapshot> displays = [];
    private ImmutableHashSet<string> liveDisplays = [];
    private ImmutableList<PlaceholderScreen> screens = [];
    private ImmutableHashSet<Guid> running = [];
    private ImmutableDictionary<Guid, string> devices = ImmutableDictionary<Guid, string>.Empty;
    private ImmutableDictionary<Guid, string> deviceNames = ImmutableDictionary<Guid, string>.Empty;
    private ImmutableDictionary<Guid, ScreenRole> roles = ImmutableDictionary<Guid, ScreenRole>.Empty;
    private ImmutableDictionary<Guid, OutputScaleMode> scaleModes = ImmutableDictionary<Guid, OutputScaleMode>.Empty;
    private ImmutableDictionary<Guid, OutputAdjustments> adjustments = ImmutableDictionary<Guid, OutputAdjustments>.Empty;
    private ImmutableDictionary<Guid, ImmutableList<OutputSliceState>> screenSlices = ImmutableDictionary<Guid, ImmutableList<OutputSliceState>>.Empty;
    private ImmutableDictionary<Guid, OutputPlacement> placements = ImmutableDictionary<Guid, OutputPlacement>.Empty;
    private ImmutableDictionary<Guid, ImmutableList<OutputMask>> masks = ImmutableDictionary<Guid, ImmutableList<OutputMask>>.Empty;
    private ImmutableDictionary<Guid, ImmutableHashSet<Guid>> activeMaskIds = ImmutableDictionary<Guid, ImmutableHashSet<Guid>>.Empty;
    private ImmutableDictionary<Guid, int> videoDelays = ImmutableDictionary<Guid, int>.Empty;
    private readonly OutputMaskRasterCache maskRasters = new();

    /// <summary>
    /// A saved setting changed: save the setup now (see <see cref="ScreenSetup.Capture"/>).
    /// The Mac's placeholderConfigurationChanged. No-op changes don't raise it.
    /// </summary>
    public event Action? ConfigurationChanged;

    /// <summary>Anything an output shows changed, saved or not (a preset switching masks on, a test pattern): redraw.</summary>
    public event Action? StateChanged;

    // ── Displays ────────────────────────────────────────────────────────────

    /// <summary>The displays connected now, as last given to <see cref="UpdateDisplays"/>.</summary>
    public IReadOnlyList<DisplaySnapshot> Displays => displays;

    /// <summary>Call at start-up and whenever displays are plugged in, unplugged or rearranged.</summary>
    public void UpdateDisplays(IEnumerable<DisplaySnapshot> connected)
    {
        displays = [.. connected];
        ReconcileRunning();
        Notify();
    }

    /// <summary>
    /// Displays showing the whole program directly ("System Displays — Direct"),
    /// without a screen. Not saved, as on the Mac.
    /// </summary>
    public IReadOnlySet<string> LiveDisplays => liveDisplays;

    public bool IsLive(string displayId) => liveDisplays.Contains(displayId);

    /// <summary>
    /// Takes a display live or off as a direct output. Going live takes the
    /// display away from any screen or slice it was carrying.
    /// </summary>
    public void SetLive(string displayId, bool live)
    {
        if (live)
        {
            // The Mac releases the first claimant it finds; with packed frames a display can have
            // several, and all of them must let go or two windows would fight over one display.
            var claimants = devices.Where(pair => pair.Value == displayId).Select(pair => pair.Key).ToList();
            if (claimants.Count > 0)
            {
                devices = devices.RemoveRange(claimants);
                deviceNames = deviceNames.RemoveRange(claimants);
                ConfigurationChanged?.Invoke();
            }
        }
        liveDisplays = live ? liveDisplays.Add(displayId) : liveDisplays.Remove(displayId);
        ReconcileRunning();
        Notify();
    }

    // ── Screens ─────────────────────────────────────────────────────────────

    public IReadOnlyList<PlaceholderScreen> Screens => screens;

    public PlaceholderScreen? Screen(Guid id) => screens.FirstOrDefault(screen => screen.Id == id);

    /// <summary>Adds a screen; null when the size or frame rate isn't positive.</summary>
    public PlaceholderScreen? AddScreen(string name, int width, int height, int framesPerSecond = 30, Guid? id = null)
    {
        if (width <= 0 || height <= 0 || framesPerSecond <= 0) return null;
        var screen = new PlaceholderScreen(id ?? Guid.NewGuid(), name, width, height, framesPerSecond);
        screens = screens.Add(screen);
        running = running.Add(screen.Id);
        PruneMaskRasters();
        Persist();
        return screen;
    }

    /// <summary>Renames or resizes a screen, keeping its slices and settings; null when it doesn't exist or the size is invalid.</summary>
    public PlaceholderScreen? ReconfigureScreen(Guid id, string name, int width, int height, int framesPerSecond = 30)
    {
        var index = screens.FindIndex(screen => screen.Id == id);
        if (index < 0 || width <= 0 || height <= 0 || framesPerSecond <= 0) return null;
        var screen = new PlaceholderScreen(id, name, width, height, framesPerSecond);
        screens = screens.SetItem(index, screen);
        // As on the Mac, only the default slice's device decides whether the rebuilt screen paces itself.
        running = devices.ContainsKey(id) ? running.Remove(id) : running.Add(id);
        PruneMaskRasters();
        Persist();
        return screen;
    }

    /// <summary>Removes a screen and everything set for it and its slices.</summary>
    public void RemoveScreen(Guid id)
    {
        var index = screens.FindIndex(screen => screen.Id == id);
        if (index < 0) return;
        screens = screens.RemoveAt(index);
        running = running.Remove(id);
        devices = devices.Remove(id);
        deviceNames = deviceNames.Remove(id);
        roles = roles.Remove(id);
        scaleModes = scaleModes.Remove(id);
        videoDelays = videoDelays.Remove(id);
        adjustments = adjustments.Remove(id);
        masks = masks.Remove(id);
        activeMaskIds = activeMaskIds.Remove(id);
        foreach (var slice in ExtraSlices(id)) ClearSliceState(slice.Id);
        screenSlices = screenSlices.Remove(id);
        placements = placements.Remove(id);
        ReconcileRunning();
        PruneMaskRasters();
        Persist();
    }

    /// <summary>
    /// Whether the screen renders on its own clock. A screen with no device
    /// behind it does (for previews and network feeds); once any of its slices
    /// is on a device, that device's refresh paces it instead. The Mac's
    /// PlaceholderScreen.isRunning.
    /// </summary>
    public bool IsRunning(Guid screenId) => running.Contains(screenId);

    /// <summary>Keeps a device-backed screen rendering while its settings are open, so the previews stay live.</summary>
    public void SetCanvasWake(Guid screenId, bool awake)
    {
        if (Screen(screenId) is null) return;
        if (awake) running = running.Add(screenId);
        else if (devices.ContainsKey(screenId)) running = running.Remove(screenId);
        Notify();
    }

    /// <summary>Whether any slice of the screen is carried by a device.</summary>
    public bool IsClaimed(Guid screenId) => Slices(screenId).Any(slice => devices.ContainsKey(slice.Id));

    // ── Roles and scale modes ───────────────────────────────────────────────

    /// <summary>The screens that aren't audience screens (audience is the default and isn't stored).</summary>
    public IReadOnlyDictionary<Guid, ScreenRole> Roles => roles;

    public ScreenRole Role(Guid screenId) => roles.GetValueOrDefault(screenId, ScreenRole.Audience);

    public void SetRole(Guid screenId, ScreenRole role)
    {
        if (Role(screenId) == role) return;
        roles = role == ScreenRole.Audience ? roles.Remove(screenId) : roles.SetItem(screenId, role);
        Persist();
    }

    /// <summary>The screens set to Fill (Fit is the default and isn't stored).</summary>
    public IReadOnlyDictionary<Guid, OutputScaleMode> ScaleModes => scaleModes;

    public OutputScaleMode ScaleMode(Guid screenId) => scaleModes.GetValueOrDefault(screenId, OutputScaleMode.Fit);

    public void SetScaleMode(Guid screenId, OutputScaleMode mode)
    {
        if (ScaleMode(screenId) == mode) return;
        scaleModes = mode == OutputScaleMode.Fit ? scaleModes.Remove(screenId) : scaleModes.SetItem(screenId, mode);
        Persist();
    }

    // ── Corrections ─────────────────────────────────────────────────────────

    /// <summary>The stored corrections by screen or slice id. Only a completely empty value is dropped.</summary>
    public IReadOnlyDictionary<Guid, OutputAdjustments> StoredAdjustments => adjustments;

    /// <summary>A screen's or slice's corrections (empty when none are set).</summary>
    public OutputAdjustments Adjustments(Guid id) => adjustments.GetValueOrDefault(id) ?? NoAdjustments;

    /// <summary>The corrections the renderer should apply, or null when they are neutral (skip the pass).</summary>
    public OutputAdjustments? EffectiveAdjustments(Guid id) =>
        adjustments.GetValueOrDefault(id) is { IsNeutral: false } value ? value : null;

    public void SetAdjustments(Guid id, OutputAdjustments value)
    {
        if (Adjustments(id) == value) return;
        adjustments = value == NoAdjustments ? adjustments.Remove(id) : adjustments.SetItem(id, value);
        Persist();
    }

    /// <summary>
    /// Reads an output's live corrections, for a feed (NDI, recording) that
    /// renders on its own thread. The target is a screen or slice id as text;
    /// anything else reads null.
    /// </summary>
    public Func<OutputAdjustments?> AdjustmentsProvider(string targetId)
    {
        var id = SwiftUuid.Parse(targetId);
        return () => id is { } target ? EffectiveAdjustments(target) : null;
    }

    // ── Slices ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A screen's outputs: first its default slice (the screen's own id, whole
    /// canvas, no name), then the extra slices. Empty for an unknown screen.
    /// </summary>
    public IReadOnlyList<OutputSliceState> Slices(Guid screenId)
    {
        if (Screen(screenId) is null) return [];
        return [new OutputSliceState(screenId, null, OutputPass.FullSourceRect), .. ExtraSlices(screenId)];
    }

    /// <summary>A screen's slices beyond the default one, in the order they were added.</summary>
    public IReadOnlyList<OutputSliceState> ExtraSlices(Guid screenId) => ExtraList(screenId);

    /// <summary>Resolves a screen id (its default slice) or a slice id; null when it is neither.</summary>
    public OutputSliceInfo? SliceInfo(Guid id)
    {
        if (Screen(id) is { } screen) return new OutputSliceInfo(id, OutputPass.FullSourceRect, screen.Width, screen.Height, null);
        foreach (var (screenId, extras) in screenSlices)
        {
            if (extras.FirstOrDefault(slice => slice.Id == id) is not { } slice || Screen(screenId) is not { } owner) continue;
            return new OutputSliceInfo(
                screenId, slice.SourceRect,
                Math.Max(1, (int)Math.Round(owner.Width * slice.SourceRect.Width, MidpointRounding.AwayFromZero)),
                Math.Max(1, (int)Math.Round(owner.Height * slice.SourceRect.Height, MidpointRounding.AwayFromZero)),
                slice.Name);
        }
        return null;
    }

    /// <summary>Adds an output to a screen showing <paramref name="sourceRect"/> (default: the whole canvas, i.e. a mirror).</summary>
    public OutputSliceState? AddSlice(Guid screenId, string? name = null, SceneRect? sourceRect = null, Guid? id = null)
    {
        if (Screen(screenId) is null) return null;
        var slice = new OutputSliceState(id ?? Guid.NewGuid(), name, ClampedUnit(sourceRect ?? OutputPass.FullSourceRect));
        screenSlices = screenSlices.SetItem(screenId, ExtraList(screenId).Add(slice));
        PruneMaskRasters();
        Persist();
        return slice;
    }

    /// <summary>Removes an extra slice and everything set for it. A screen's default slice can't be removed.</summary>
    public void RemoveSlice(Guid id)
    {
        if (SliceInfo(id) is not { } info || info.ScreenId == id) return;
        var remaining = ExtraList(info.ScreenId).RemoveAll(slice => slice.Id == id);
        screenSlices = remaining.IsEmpty ? screenSlices.Remove(info.ScreenId) : screenSlices.SetItem(info.ScreenId, remaining);
        ClearSliceState(id);
        ReconcileRunning();
        PruneMaskRasters();
        Persist();
    }

    /// <summary>Moves or resizes an extra slice's region of the canvas (clamped to the canvas).</summary>
    public void SetSliceSourceRect(Guid id, SceneRect rect)
    {
        if (SliceInfo(id) is not { } info || info.ScreenId == id) return;
        var list = ExtraList(info.ScreenId);
        var index = list.FindIndex(slice => slice.Id == id);
        if (index < 0) return;
        var clamped = ClampedUnit(rect);
        if (list[index].SourceRect == clamped) return;
        screenSlices = screenSlices.SetItem(info.ScreenId, list.SetItem(index, list[index] with { SourceRect = clamped }));
        Persist();
    }

    /// <summary>Names an extra slice; an empty name clears it.</summary>
    public void SetSliceName(Guid id, string? name)
    {
        if (SliceInfo(id) is not { } info || info.ScreenId == id) return;
        var list = ExtraList(info.ScreenId);
        var index = list.FindIndex(slice => slice.Id == id);
        if (index < 0) return;
        var trimmed = string.IsNullOrEmpty(name) ? null : name;
        if (list[index].Name == trimmed) return;
        screenSlices = screenSlices.SetItem(info.ScreenId, list.SetItem(index, list[index] with { Name = trimmed }));
        Persist();
    }

    /// <summary>
    /// True when a slice shows only part of the canvas. Such a screen's scene is
    /// never reshaped to the screen's aspect, because the slices' regions are in
    /// the canvas's own coordinates.
    /// </summary>
    public bool IsSpanned(Guid screenId) => ExtraList(screenId).Any(slice => slice.SourceRect != OutputPass.FullSourceRect);

    /// <summary>
    /// Keeps a region inside the unit canvas: the origin is clamped to 0–1, and
    /// the size to at least 1% and at most what's left of the canvas.
    /// </summary>
    public static SceneRect ClampedUnit(SceneRect rect)
    {
        // CGRect's minX/width are those of the standardised rect (a negative width counts from the other side).
        if (rect.Width < 0) rect = rect with { X = rect.X + rect.Width, Width = -rect.Width };
        if (rect.Height < 0) rect = rect with { Y = rect.Y + rect.Height, Height = -rect.Height };
        var x = Math.Min(Math.Max(rect.X, 0), 1);
        var y = Math.Min(Math.Max(rect.Y, 0), 1);
        return new SceneRect(x, y, Math.Min(Math.Max(rect.Width, 0.01), 1 - x), Math.Min(Math.Max(rect.Height, 0.01), 1 - y));
    }

    // ── Placements ──────────────────────────────────────────────────────────

    /// <summary>Slices whose content lands at exact pixels inside their device's frame (LED processors).</summary>
    public IReadOnlyDictionary<Guid, OutputPlacement> Placements => placements;

    public OutputPlacement? Placement(Guid id) => placements.GetValueOrDefault(id);

    /// <summary>Sets or clears a slice's placement. One that fills the whole frame is the same as none and is cleared.</summary>
    public void SetPlacement(Guid id, OutputPlacement? placement)
    {
        var normalized = placement is { IsFill: false } ? placement : null;
        if (Placement(id) == normalized) return;
        placements = normalized is null ? placements.Remove(id) : placements.SetItem(id, normalized);
        Persist();
    }

    // ── Devices ─────────────────────────────────────────────────────────────

    /// <summary>The device carrying each screen or slice (see <see cref="OutputDevice"/> for the ids).</summary>
    public IReadOnlyDictionary<Guid, string> Devices => devices;

    /// <summary>The device's name when it was assigned, so a disconnected one can still be shown by name.</summary>
    public IReadOnlyDictionary<Guid, string> DeviceNames => deviceNames;

    public string? Device(Guid id) => devices.GetValueOrDefault(id);

    public string? DeviceName(Guid id) => deviceNames.GetValueOrDefault(id);

    /// <summary>
    /// Puts a screen or slice on a device, or takes it off (<paramref name="deviceId"/> null).
    /// Normally a device carries one output, so this takes it away from any other
    /// slice; pass <paramref name="exclusive"/> false to add alongside instead and
    /// pack several outputs into one frame (each with its placement). When no
    /// name is given, the connected display's name is used if there is one.
    /// A display that was live as a direct output stops being one.
    /// </summary>
    public void AssignDevice(Guid id, string? deviceId, string? displayName = null, bool exclusive = true)
    {
        if (deviceId is not null)
        {
            if (exclusive)
                devices = devices.RemoveRange(devices.Where(pair => pair.Value == deviceId).Select(pair => pair.Key).ToList());
            devices = devices.SetItem(id, deviceId);
            if ((displayName ?? displays.FirstOrDefault(display => display.Id == deviceId)?.Name) is { } name)
                deviceNames = deviceNames.SetItem(id, name);
            if (liveDisplays.Contains(deviceId))
            {
                liveDisplays = liveDisplays.Remove(deviceId);
                ReconcileRunning();
                Persist();
                return;
            }
        }
        else
        {
            devices = devices.Remove(id);
            deviceNames = deviceNames.Remove(id);
        }
        ReconcileRunning();
        Persist();
    }

    /// <summary>
    /// Which slices each device carries, screen by screen in order: the app opens
    /// one window (or feed) per device and composites these into it.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<OutputCarry>> Carries()
    {
        var wanted = new Dictionary<string, List<OutputCarry>>(StringComparer.Ordinal);
        foreach (var screen in screens)
            foreach (var slice in Slices(screen.Id))
                if (devices.TryGetValue(slice.Id, out var device))
                {
                    if (!wanted.TryGetValue(device, out var list)) wanted[device] = list = [];
                    list.Add(new OutputCarry(slice.Id, screen.Id));
                }
        return wanted.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<OutputCarry>)pair.Value, StringComparer.Ordinal);
    }

    /// <summary>The slices one device composites, in the order the Mac packs them (by slice id).</summary>
    public IReadOnlyList<OutputCarry> CarriedSlices(string deviceId) =>
        Carries().TryGetValue(deviceId, out var carries)
            ? carries.OrderBy(carry => SwiftUuid.Format(carry.SliceId), StringComparer.Ordinal).ToList()
            : [];

    // ── Masks ───────────────────────────────────────────────────────────────

    /// <summary>A screen's mask library.</summary>
    public IReadOnlyList<OutputMask> Masks(Guid screenId) => masks.GetValueOrDefault(screenId) ?? [];

    public void SetMasks(Guid screenId, IReadOnlyList<OutputMask> library)
    {
        if (Masks(screenId).SequenceEqual(library)) return;
        masks = library.Count == 0 ? masks.Remove(screenId) : masks.SetItem(screenId, [.. library]);
        PruneMaskRasters();
        Persist();
    }

    /// <summary>The masks an output preset has switched on for a screen. Runtime state: never saved.</summary>
    public IReadOnlySet<Guid> ActiveMaskIds(Guid screenId) => activeMaskIds.GetValueOrDefault(screenId) ?? [];

    public void SetActiveMaskIds(Guid screenId, IEnumerable<Guid> ids)
    {
        var set = ids.ToImmutableHashSet();
        if (ActiveMaskIds(screenId).SetEquals(set)) return;
        activeMaskIds = set.IsEmpty ? activeMaskIds.Remove(screenId) : activeMaskIds.SetItem(screenId, set);
        PruneMaskRasters();
        Notify();
    }

    /// <summary>The masks in force on a screen: its always-on ones plus those the active preset switched on.</summary>
    public IReadOnlyList<OutputMask> ActiveMasks(Guid screenId)
    {
        var activated = ActiveMaskIds(screenId);
        return Masks(screenId).Where(mask => mask.AlwaysOn || activated.Contains(mask.Id)).ToList();
    }

    /// <summary>What a screen's or slice's mask raster is made of; null when no mask covers it.</summary>
    public OutputMaskPlan? MaskPlan(Guid id)
    {
        if (SliceInfo(id) is not { } info || Screen(info.ScreenId) is not { } screen) return null;
        var covering = ActiveMasks(info.ScreenId).Where(mask => mask.Covers(id)).ToList();
        return covering.Count == 0 ? null : new OutputMaskPlan(covering, screen.Width, screen.Height);
    }

    /// <summary>
    /// The app's path filler for mask rasters: fill a unit-space SVG path into a
    /// width×height alpha buffer (see <see cref="OutputMaskRaster.Coverage"/>).
    /// Until it is set, <see cref="MaskCoverage"/> reads null.
    /// </summary>
    public Func<string, int, int, byte[]?>? MaskPathFill { get; set; }

    /// <summary>
    /// The mask raster for a screen or slice, at the screen's canvas size (one
    /// byte per pixel, 255 = show), or null when it has no mask. Outputs with the
    /// same masks share one array, and it is only rebuilt when the masks change.
    /// </summary>
    public byte[]? MaskCoverage(Guid id) =>
        MaskPlan(id) is { } plan && MaskPathFill is { } fill ? maskRasters.Get(plan, fill) : null;

    /// <summary>Reads an output's live mask raster, like <see cref="AdjustmentsProvider"/>.</summary>
    public Func<byte[]?> MaskProvider(string targetId)
    {
        var id = SwiftUuid.Parse(targetId);
        return () => id is { } target ? MaskCoverage(target) : null;
    }

    // ── Video delay ─────────────────────────────────────────────────────────

    /// <summary>Outputs held back to line up with slower ones (0 is the default and isn't stored).</summary>
    public IReadOnlyDictionary<Guid, int> VideoDelays => videoDelays;

    public int VideoDelay(Guid id) => videoDelays.GetValueOrDefault(id);

    /// <summary>Delays an output by whole frames, 0 to <see cref="MaxVideoDelayFrames"/>.</summary>
    public void SetVideoDelay(Guid id, int frames)
    {
        var clamped = Math.Clamp(frames, 0, MaxVideoDelayFrames);
        if (VideoDelay(id) == clamped) return;
        videoDelays = clamped == 0 ? videoDelays.Remove(id) : videoDelays.SetItem(id, clamped);
        Persist();
    }

    /// <summary>Reads an output's live video delay, like <see cref="AdjustmentsProvider"/>; 0 for anything unknown.</summary>
    public Func<int> VideoDelayProvider(string targetId)
    {
        var id = SwiftUuid.Parse(targetId);
        return () => id is { } target ? VideoDelay(target) : 0;
    }

    // ── Internals ───────────────────────────────────────────────────────────

    private ImmutableList<OutputSliceState> ExtraList(Guid screenId) =>
        screenSlices.GetValueOrDefault(screenId) ?? ImmutableList<OutputSliceState>.Empty;

    /// <summary>Everything stored for one slice (not its screen's masks, which are the screen's).</summary>
    private void ClearSliceState(Guid id)
    {
        devices = devices.Remove(id);
        deviceNames = deviceNames.Remove(id);
        videoDelays = videoDelays.Remove(id);
        adjustments = adjustments.Remove(id);
        placements = placements.Remove(id);
    }

    /// <summary>A screen with a slice on a device is paced by that device; one without paces itself.</summary>
    private void ReconcileRunning()
    {
        foreach (var screen in screens)
            running = IsClaimed(screen.Id) ? running.Remove(screen.Id) : running.Add(screen.Id);
    }

    /// <summary>Drops cached mask rasters no output uses any more.</summary>
    private void PruneMaskRasters() => maskRasters.Retain(screens
        .SelectMany(screen => Slices(screen.Id))
        .Select(slice => MaskPlan(slice.Id)?.Fingerprint)
        .OfType<string>());

    private void Persist()
    {
        ConfigurationChanged?.Invoke();
        StateChanged?.Invoke();
    }

    private void Notify() => StateChanged?.Invoke();
}
