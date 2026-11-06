using System.Windows.Media;
using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.Rendering;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>An effect preset as the Presets menu lists it: the Mac's recommended ones and your own.</summary>
public sealed record EffectPresetChoice(string Group, string Name, EffectPreset Preset, bool IsMine);

/// <summary>
/// The Effects section of an inspector: one chain of picture effects (a slide object's, a theme
/// design's object's, or a media item's), shown as a card per effect with its sliders. Every change
/// makes a new chain (<see cref="EffectEditing"/>) and hands it to <c>write</c>, then calls
/// <c>changed</c> so the owner redraws and saves. Port of the effects section of the Mac's
/// SlideObjectInspector.swift and MediaEditorView.swift (with EffectPresetsMenu).
/// </summary>
public sealed class EffectsEditorViewModel
{
    private readonly Func<IReadOnlyList<Effect>?> read;
    private readonly Action<List<Effect>?> write;
    private readonly Action changed;

    /// <param name="read">The chain as it is now.</param>
    /// <param name="write">Stores a new chain (null for none).</param>
    /// <param name="changed">Called after every change (the owner redraws and saves).</param>
    /// <param name="library">Where your saved effect presets are (null: only the recommended ones).</param>
    public EffectsEditorViewModel(Func<IReadOnlyList<Effect>?> read, Action<List<Effect>?> write, Action changed, LibraryStore? library)
    {
        this.read = read;
        this.write = write;
        this.changed = changed;
        Library = library;
    }

    public LibraryStore? Library { get; }

    /// <summary>For a shape: "Apply to content below" (read and set); null hides the switch.</summary>
    public Func<bool>? ApplyBelowRead { get; init; }

    public Action<bool>? ApplyBelowWrite { get; init; }

    /// <summary>Raised when the chain changes (the panel draws its cards again).</summary>
    public event Action? ChainChanged;

    public IReadOnlyList<Effect> Chain => read() ?? [];

    private void Commit(List<Effect>? chain, bool redraw = true)
    {
        write(chain);
        changed();
        if (redraw) ChainChanged?.Invoke();
    }

    public void Add(EffectKind kind) => Commit(EffectEditing.Added(Chain, kind));

    public void Remove(int index) => Commit(EffectEditing.Removed(Chain, index));

    /// <summary>Drag and drop: the effect lands before the row it's dropped on.</summary>
    public void Move(int from, int onto) => Commit(EffectEditing.Moved(Chain, from, onto));

    public void Step(int index, int by) => Commit(EffectEditing.Stepped(Chain, index, by));

    public void SetEnabled(int index, bool on) => Commit(EffectEditing.Enabled(Chain, index, on));

    /// <summary>A slider moved: the value is kept in range and stored; the cards aren't drawn again (the slider is the one being dragged).</summary>
    public double SetValue(int index, EffectDial dial, double value)
    {
        var chain = Chain.Select(Core.Json.MxuJson.Clone).ToList();
        if (index < 0 || index >= chain.Count) return dial.Default;
        var clamped = EffectEditing.Clamp(dial, value);
        EffectEditing.Write(chain[index], dial.Field, clamped);
        Commit(chain, redraw: false);
        return clamped;
    }

    public bool ShowsApplyBelow => ApplyBelowRead is not null && Chain.Count > 0;

    public bool ApplyBelow
    {
        get => ApplyBelowRead?.Invoke() ?? false;
        set
        {
            ApplyBelowWrite?.Invoke(value);
            changed();
        }
    }

    // MARK: Drawing without the graphics card

    /// <summary>True when this PC's graphics card draws pixel shader 3.0 effects (false on a PC that draws in software).</summary>
    public static bool CanDrawShaders => RenderCapability.Tier >> 16 > 0 && ItemEffect.IsSupported;

    /// <summary>The warning under the cards when this PC can't draw some of the chain, else null.</summary>
    public string? SoftwareNote => EffectEditing.SoftwareNote(Chain, CanDrawShaders);

    // MARK: Presets (the Mac's EffectPresetsMenu)

    public IReadOnlyList<EffectPresetChoice> Presets
    {
        get
        {
            var list = new List<EffectPresetChoice>();
            foreach (var group in EffectPresetCatalog.RecommendedGroups)
                foreach (var preset in group.Presets)
                    list.Add(new(group.Name, group.Presets.Count == 1 ? group.Name : preset.Name, preset, false));
            if (Library is { } library)
                foreach (var preset in PresetBoards.Effects(library).Presets) list.Add(new("My effects", preset.Name, preset, true));
            return list;
        }
    }

    /// <summary>Puts a preset's effects on, replacing the chain ("apply") or after it ("add").</summary>
    public void ApplyPreset(EffectPreset preset, bool add)
    {
        var effects = preset.Effects.Select(Core.Json.MxuJson.Clone).ToList();
        Commit(add ? [.. Chain.Select(Core.Json.MxuJson.Clone), .. effects] : effects.Count == 0 ? null : effects);
    }

    public EffectPreset? SavePreset(string name)
    {
        if (Library is not { } library || Chain.Count == 0 || string.IsNullOrWhiteSpace(name)) return null;
        return PresetBoards.SaveEffect(library, name, Chain);
    }

    public bool SaveOver(EffectPreset preset) => Library is { } library && Chain.Count > 0 && PresetBoards.OverwriteEffect(library, preset.Id, Chain);

    public bool DeletePreset(EffectPreset preset) => Library is { } library && PresetBoards.DeleteEffect(library, preset.Id);
}
