using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using Mode = MxU.Slides.Core.Animation.AnimationPresetApplication.Mode;
using Slot = MxU.Slides.Core.Animation.AnimationSequence.GroupSlot;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>One effect tile in the gallery ("Slide", "Wipe"…).</summary>
public sealed record GalleryTile(StepAnimation Animation, string Title);

/// <summary>
/// What an editor gives its Animation tab: the composition being animated (an overlay, a theme
/// design, a stage layout), the selection on its canvas, and the ways to redraw, save and preview.
/// </summary>
public interface IAnimationHost
{
    /// <summary>The composition whose steps are edited (null when nothing is open).</summary>
    IAnimatedComposition? Composition { get; }

    /// <summary>The objects selected on the canvas, in the composition's order.</summary>
    IReadOnlyList<SlideObject> SelectedObjects { get; }

    void SelectObjects(IEnumerable<string> ids);

    /// <summary>The steps changed (and, with <paramref name="objects"/>, the objects' looks too): redraw and save.</summary>
    void AnimationChanged(bool objects);

    /// <summary>
    /// True for an overlay: it never gets a click, so its Ins play "when it goes up" and its Outs
    /// "when it's taken down". A slide or theme design plays its clicks on Next.
    /// </summary>
    bool IsOverlay { get; }

    LibraryStore Library { get; }

    /// <summary>Plays the preview from <paramref name="seconds"/> into the play-through.</summary>
    void PlayPreviewFrom(double seconds);

    /// <summary>Shows the composition frozen <paramref name="seconds"/> into its play-through (the timeline's playhead).</summary>
    void ScrubPreview(double seconds);

    /// <summary>A line for the window's status bar.</summary>
    void Notify(string message);

    /// <summary>
    /// Puts a step's pose (a morph's end state, an In's custom start) on the canvas to be edited
    /// there, or with null goes back to editing the objects as they rest.
    /// </summary>
    void Pose(string? stepId);

    /// <summary>Applies a preset to objects; a host may do it its own way (a theme's text gets a kinetic build). Returns the new steps' ids.</summary>
    List<string> ApplyPreset(PresetChoice choice, List<string> objectIds, Mode mode);

    /// <summary>One step per line or word of a text object; returns the new steps' ids (a theme makes a kinetic build instead).</summary>
    List<string> GenerateSteps(SlideObject text, AnimationRanges.Generator by, AnimationKind kind);
}

/// <summary>
/// The Animation tab, shared by the overlay, theme and stage layout editors: the effect gallery,
/// presets, the steps in play order grouped by when they run (drag one onto another to reorder),
/// the selected step's settings, the animation timeline, and morph ("Change") steps and custom
/// starts posed on the canvas. Port of the Mac's AnimationInspector.swift (AnimationGallery,
/// AnimationSequenceList, AnimationStepStrip) and the gallery half of AnimateCanvasOverlay.swift;
/// the timeline is <see cref="Views.AnimationTimelineView"/>.
/// </summary>
public sealed partial class AnimationTabViewModel : ObservableObject
{
    public const string GoesUp = "WHEN IT GOES UP";
    public const string ComesDown = "WHEN IT'S TAKEN DOWN";
    public const string WithTheSlide = "WITH THE SLIDE";
    public const string Final = "FINAL (WHEN IT LEAVES)";

    private readonly IAnimationHost host;

    public AnimationTabViewModel(IAnimationHost host) => this.host = host;

    public IAnimationHost Host => host;

    private IAnimatedComposition? Composition => host.Composition;

    public bool IsOverlay => host.IsOverlay;

    /// <summary>The composition changed elsewhere (another object picked, the canvas moved, a new composition opened).</summary>
    public void Reload()
    {
        RefreshSteps();
        OnPropertyChanged(nameof(GalleryTarget));
        OnPropertyChanged(nameof(Timeline));
        OnPropertyChanged(nameof(GalleryTiles));
    }

    /// <summary>The selection on the canvas changed.</summary>
    public void SelectionChanged() => OnPropertyChanged(nameof(GalleryTarget));

    private void Changed(bool objects = false)
    {
        host.AnimationChanged(objects);
        RefreshSteps();
        OnPropertyChanged(nameof(Timeline));
    }

    // MARK: Gallery

    /// <summary>Which effects the gallery offers: 0 Bring in, 1 Take out, 2 Emphasize, 3 Change (morph).</summary>
    [ObservableProperty]
    public partial int GalleryKindIndex { get; set; }

    partial void OnGalleryKindIndexChanged(int value)
    {
        OnPropertyChanged(nameof(GalleryTiles));
        OnPropertyChanged(nameof(IsMorphGallery));
    }

    private AnimationKind GalleryKind => GalleryKindIndex switch
    {
        1 => AnimationKind.Out,
        2 => AnimationKind.Emphasis,
        3 => AnimationKind.Morph,
        _ => AnimationKind.In,
    };

    public bool IsMorphGallery => GalleryKind == AnimationKind.Morph;

    public IReadOnlyList<GalleryTile> GalleryTiles =>
        GalleryKind == AnimationKind.Morph
            ? [new GalleryTile(StepAnimation.Fade, "Change")]
            : AnimationEditing.Choices(GalleryKind).Select(a => new GalleryTile(a, AnimationEditing.TileTitle(a, GalleryKind))).ToList();

    /// <summary>
    /// For a slide or theme design: where a new step goes. 0 as the Mac adds it (an In, emphasis or
    /// change on a new click, an Out at the end), 1 with the slide (no click), 2 at the end (final).
    /// </summary>
    [ObservableProperty]
    public partial int AddSlotIndex { get; set; }

    /// <summary>What clicking an effect will do, in words (the Mac's target line).</summary>
    public string GalleryTarget
    {
        get
        {
            var selected = host.SelectedObjects;
            var verb = GalleryKind == AnimationKind.Morph ? "a Change step" : "it";
            return selected.Count switch
            {
                0 => "Select an object on the canvas (or in Objects) to animate it.",
                1 => GalleryKind == AnimationKind.Morph
                    ? $"Click Change to give {ObjectMasks.DisplayName(selected[0])} {verb}, then pose where it ends on the canvas."
                    : $"Click an effect to add it to {ObjectMasks.DisplayName(selected[0])}.",
                _ => $"Click an effect to add it to the {selected.Count} selected objects (they play together).",
            };
        }
    }

    /// <summary>The slot new steps join, or null for the Mac's default (In on a click, Out at the end).</summary>
    private Slot? AddSlot(AnimationKind kind)
    {
        if (host.IsOverlay) return kind == AnimationKind.Out ? Slot.Exit : Slot.Auto;
        return AddSlotIndex switch
        {
            1 => Slot.Auto,
            2 => Slot.Exit,
            _ => null,
        };
    }

    /// <summary>Adds the effect to the selected objects (a Change step, for the morph gallery, starting where each object rests).</summary>
    [RelayCommand]
    private void AddEffect(GalleryTile? tile)
    {
        if (tile is null || Composition is not { } composition) return;
        var targets = host.SelectedObjects.Select(o => o.Id).ToList();
        if (targets.Count == 0)
        {
            host.Notify("Select an object first, then pick its effect.");
            return;
        }
        List<string> ids;
        if (GalleryKind == AnimationKind.Morph)
        {
            ids = AnimationTimelineEditing.AddMorph(composition, targets);
            if (AddSlot(AnimationKind.Morph) is { } slot)
                foreach (var id in ids) AnimationEditing.MoveIntoGroup(composition, id, slot, int.MaxValue);
        }
        else
        {
            ids = AnimationEditing.QuickAdd(composition, targets, GalleryKind, tile.Animation, AddSlot(GalleryKind));
        }
        Changed();
        if (ids.LastOrDefault() is { } last) SelectedStepRow = StepRows.FirstOrDefault(r => r.StepId == last);
        if (GalleryKind == AnimationKind.Morph && ids.Count == 1)
        {
            // A change that changes nothing yet: straight to posing its end on the canvas.
            Posing = true;
            host.Notify("Move, resize or restyle the object on the canvas: that's where the Change ends. Press Done posing when it looks right.");
        }
    }

    // MARK: The steps, in play order

    public ObservableCollection<StepRowViewModel> StepRows { get; } = [];

    [ObservableProperty]
    public partial StepRowViewModel? SelectedStepRow { get; set; }

    partial void OnSelectedStepRowChanged(StepRowViewModel? value)
    {
        if (Posing && value?.StepId != posedStepId) Posing = false;
        StepEditor = value is not null && Composition is { } composition ? new AnimationStepViewModel(composition, value.StepId, StepChanged) { Tab = this } : null;
        if (value is not null && Composition is { } c && AnimationEditing.Find(c, value.StepId) is { } found
            && !(host.SelectedObjects.Count == 1 && host.SelectedObjects[0].Id == found.ObjectId))
            host.SelectObjects([found.ObjectId]);
        OnPropertyChanged(nameof(Timeline));
    }

    [ObservableProperty]
    public partial AnimationStepViewModel? StepEditor { get; set; }

    public bool HasSteps => StepRows.Count > 0;

    private void StepChanged() => Changed();

    /// <summary>The words for each group heading.</summary>
    private string GroupName(Slot slot, int clicks) => slot.Kind switch
    {
        AnimationSequence.GroupSlotKind.Auto => host.IsOverlay ? GoesUp : WithTheSlide,
        AnimationSequence.GroupSlotKind.Exit => host.IsOverlay ? ComesDown : Final,
        _ => host.IsOverlay ? $"ON CLICK {slot.Index + 1} (AN OVERLAY ONLY PLAYS ITS OUTS HERE, WHEN TAKEN DOWN)" : $"CLICK {slot.Index + 1}",
    };

    private void RefreshSteps()
    {
        if (Composition is not { } composition)
        {
            StepRows.Clear();
            StepEditor = null;
            OnPropertyChanged(nameof(HasSteps));
            return;
        }
        var groups = AnimationSequence.Groups(composition.Objects, composition.AnimationOrder);
        var listed = new List<(AnimationSequence.Entry Entry, Slot Slot, int Position)>();
        listed.AddRange(groups.Auto.Select((e, i) => (e, Slot.Auto, i)));
        for (var c = 0; c < groups.Click.Count; c++) listed.AddRange(groups.Click[c].Select((e, i) => (e, Slot.Click(c), i)));
        listed.AddRange(groups.Exit.Select((e, i) => (e, Slot.Exit, i)));
        var warnings = host.IsOverlay ? AnimationEditing.OverlayWarnings(composition) : AnimationSequence.Warnings(composition.Objects, composition.AnimationOrder);

        void Fill(StepRowViewModel row, AnimationSequence.Entry entry)
        {
            var owner = composition.Objects.FirstOrDefault(o => o.Id == entry.ObjectId);
            row.Title = $"{AnimationEditing.TargetLabel(owner, entry.Step)} · {AnimationEditing.Sentence(entry.Step, owner)}";
            row.Timing = $"{entry.Step.Trigger.ShortName()} · {AnimationEditing.TimingLabel(entry.Step)}";
            row.Warning = warnings.GetValueOrDefault(entry.Step.Id);
            row.KindColor = StepRowViewModel.ColorOf(entry.Step.Kind);
        }

        var same = listed.Count == StepRows.Count
            && listed.Zip(StepRows).All(pair => pair.First.Entry.Step.Id == pair.Second.StepId && GroupName(pair.First.Slot, groups.Click.Count) == pair.Second.Group);
        if (same)
        {
            for (var i = 0; i < listed.Count; i++)
            {
                Fill(StepRows[i], listed[i].Entry);
                StepRows[i].Slot = listed[i].Slot;
                StepRows[i].Position = listed[i].Position;
            }
        }
        else
        {
            var selectedId = SelectedStepRow?.StepId;
            StepRows.Clear();
            foreach (var (entry, slot, position) in listed)
            {
                var row = new StepRowViewModel(entry.Step.Id, GroupName(slot, groups.Click.Count)) { Slot = slot, Position = position };
                Fill(row, entry);
                StepRows.Add(row);
            }
            var again = StepRows.FirstOrDefault(r => r.StepId == selectedId);
            if (!ReferenceEquals(again, SelectedStepRow))
            {
                // Keep the same step's settings open when only its place in the list changed.
                var editor = StepEditor;
                SelectedStepRow = again;
                if (again is not null && editor?.StepId == again.StepId) StepEditor = editor;
            }
        }
        StepEditor?.Refresh();
        if (StepEditor is { Exists: false }) StepEditor = null;
        OnPropertyChanged(nameof(HasSteps));
    }

    private List<string> FlatStepIds() =>
        Composition is { } composition ? AnimationSequence.OrderedEntries(composition.Objects, composition.AnimationOrder).Select(e => e.Step.Id).ToList() : [];

    [RelayCommand]
    private void RemoveStep()
    {
        if (Composition is not { } composition || SelectedStepRow is not { } row) return;
        var ids = FlatStepIds();
        var index = ids.IndexOf(row.StepId);
        if (Posing) Posing = false;
        if (!AnimationEditing.Remove(composition, row.StepId)) return;
        Changed();
        SelectedStepRow = StepRows.Count == 0 ? null : StepRows[Math.Clamp(index, 0, StepRows.Count - 1)];
    }

    /// <summary>Plays this step one place sooner in the order (it may join the group before).</summary>
    [RelayCommand]
    private void MoveStepEarlier()
    {
        if (Composition is not { } composition || SelectedStepRow is not { } row) return;
        var ids = FlatStepIds();
        var index = ids.IndexOf(row.StepId);
        if (index <= 0) return;
        if (AnimationEditing.MoveBefore(composition, row.StepId, ids[index - 1])) Changed();
    }

    [RelayCommand]
    private void MoveStepLater()
    {
        if (Composition is not { } composition || SelectedStepRow is not { } row) return;
        var ids = FlatStepIds();
        var index = ids.IndexOf(row.StepId);
        if (index < 0 || index >= ids.Count - 1) return;
        if (AnimationEditing.MoveBefore(composition, row.StepId, index + 2 < ids.Count ? ids[index + 2] : null)) Changed();
    }

    /// <summary>Moves the step to the end of a group: "up" (going up / with the slide), "down" (taken down / final) or "click" (a new click).</summary>
    [RelayCommand]
    private void MoveStepTo(string where)
    {
        if (Composition is not { } composition || SelectedStepRow is not { } row) return;
        var groups = AnimationSequence.Groups(composition.Objects, composition.AnimationOrder);
        var slot = where switch
        {
            "down" => Slot.Exit,
            "click" => Slot.NewClick(groups.Click.Count),
            _ => Slot.Auto,
        };
        if (AnimationEditing.MoveIntoGroup(composition, row.StepId, slot, int.MaxValue)) Changed();
    }

    /// <summary>
    /// Drag and drop in the list: the dragged step joins the group of the row it's dropped on, in that
    /// row's place (or after it, dropped on its lower half), taking the group's timing.
    /// </summary>
    public bool DropStep(string stepId, StepRowViewModel onto, bool after)
    {
        if (Composition is not { } composition || stepId == onto.StepId) return false;
        var position = onto.Position + (after ? 1 : 0);
        // Moving down within the same group: the step's own place is taken out first.
        if (StepRows.FirstOrDefault(r => r.StepId == stepId) is { } dragged && dragged.Slot == onto.Slot && dragged.Position < position) position--;
        if (!AnimationEditing.MoveIntoGroup(composition, stepId, onto.Slot, position)) return false;
        Changed();
        SelectedStepRow = StepRows.FirstOrDefault(r => r.StepId == stepId);
        return true;
    }

    /// <summary>A step dropped below the last row: the end of the last group.</summary>
    public bool DropStepAtEnd(string stepId)
    {
        if (StepRows.LastOrDefault() is not { } last || last.StepId == stepId) return false;
        return DropStep(stepId, last, after: true);
    }

    /// <summary>Plays the preview from just before the selected step.</summary>
    [RelayCommand]
    private void PreviewStep()
    {
        if (Composition is not { } composition || SelectedStepRow is not { } row) return;
        var start = AnimationSequence.PreviewStart(row.StepId, composition.Objects, composition.AnimationOrder) ?? 0;
        host.PlayPreviewFrom(Math.Max(start - 0.15, 0));
    }

    [RelayCommand]
    private void DuplicateStep()
    {
        if (Composition is not { } composition || SelectedStepRow is not { } row) return;
        if (AnimationTimelineEditing.DuplicateStep(composition, row.StepId) is not { } copy) return;
        Changed();
        SelectedStepRow = StepRows.FirstOrDefault(r => r.StepId == copy);
    }

    // MARK: Posing (a morph's end state, an In's custom start) on the canvas

    private string? posedStepId;

    /// <summary>True while the canvas edits the selected step's pose instead of the objects.</summary>
    public bool Posing
    {
        get => posedStepId is not null;
        set
        {
            var want = value && SelectedStepRow?.StepId is { } id && Composition is { } c && AnimationTimelineEditing.Pose(c, id) is not null ? SelectedStepRow!.StepId : null;
            if (want == posedStepId) return;
            posedStepId = want;
            host.Pose(want);
            OnPropertyChanged();
            OnPropertyChanged(nameof(PoseButtonText));
            StepEditor?.Refresh();
        }
    }

    /// <summary>The step whose pose the canvas shows, or null.</summary>
    public string? PosedStepId => posedStepId;

    public string PoseButtonText => Posing ? "Done posing" : "Pose on the canvas";

    /// <summary>The canvas changed the pose: the step list and timeline re-read it.</summary>
    public void PoseEdited() => Changed();

    [RelayCommand]
    private void TogglePose() => Posing = !Posing;

    [RelayCommand]
    private void ResetPose()
    {
        if (Composition is not { } composition || SelectedStepRow is not { } row) return;
        if (AnimationTimelineEditing.ResetPose(composition, row.StepId))
        {
            host.Pose(posedStepId); // the canvas shows the reset pose
            Changed();
        }
    }

    /// <summary>An In's "Enters from": its effect's own start, or where you place it (a custom start, posed on the canvas).</summary>
    public void SetCustomStart(bool custom)
    {
        if (Composition is not { } composition || SelectedStepRow is not { } row) return;
        if (!custom && Posing) Posing = false;
        if (!AnimationTimelineEditing.SetCustomStart(composition, row.StepId, custom)) return;
        Changed();
        if (custom) Posing = true;
    }

    // MARK: Timeline

    /// <summary>The timeline's geometry (columns, rows, blocks), made again after every change.</summary>
    public AnimationTimelineLayout.Timeline? Timeline =>
        Composition is { } c ? AnimationTimelineLayout.TimelineOf(c.Objects, c.AnimationOrder) : null;

    /// <summary>An object's name for a timeline row.</summary>
    public string RowName(string objectId) =>
        Composition?.Objects.FirstOrDefault(o => o.Id == objectId) is { } o ? ObjectMasks.DisplayName(o) : objectId;

    /// <summary>The timeline picked a step (clicking its block).</summary>
    public void PickStep(string stepId) => SelectedStepRow = StepRows.FirstOrDefault(r => r.StepId == stepId);

    /// <summary>A block was dragged along its row to start <paramref name="offset"/> seconds into its column.</summary>
    public void PlaceStep(string stepId, double offset)
    {
        if (Composition is { } c && AnimationTimelineEditing.PlaceStep(c, stepId, offset)) Changed();
    }

    /// <summary>A block was dropped into another column, <paramref name="offset"/> seconds in.</summary>
    public void MoveStepInto(string stepId, Slot slot, double offset)
    {
        if (Composition is { } c && AnimationTimelineEditing.MoveStepInto(c, stepId, slot, offset)) Changed();
    }

    public void ResizeStep(string stepId, double duration)
    {
        if (Composition is { } c && AnimationTimelineEditing.ResizeStep(c, stepId, duration)) Changed();
    }

    public void TrimStepStart(string stepId, double delta)
    {
        if (Composition is { } c && AnimationTimelineEditing.TrimStepStart(c, stepId, delta)) Changed();
    }

    public void ToggleLink(string stepId)
    {
        if (Composition is { } c && AnimationTimelineEditing.ToggleLink(c, stepId)) Changed();
    }

    public void DeleteColumn(Slot slot)
    {
        if (Composition is { } c && AnimationTimelineEditing.DeleteColumn(c, slot)) Changed();
    }

    public void MoveColumn(Slot from, Slot to)
    {
        if (Composition is { } c && AnimationTimelineEditing.MoveColumn(c, from, to)) Changed();
    }

    public void RemoveStep(string stepId)
    {
        PickStep(stepId);
        RemoveStepCommand.Execute(null);
    }

    /// <summary>The timeline's playhead was dragged: the canvas shows that moment.</summary>
    public void Scrub(double seconds) => host.ScrubPreview(seconds);

    // MARK: Presets (the Mac's recommended set, Windows' kinetic text, your own)

    private List<PresetChoice>? presetChoices;

    /// <summary>
    /// Every preset to choose from: Windows' kinetic text first, then the Mac's recommended set, then
    /// yours. Made once and kept (the gallery's tiles and the selection point at these entries), and
    /// made again when you save or delete one of your own.
    /// </summary>
    public IReadOnlyList<PresetChoice> PresetChoices => presetChoices ??= BuildPresetChoices();

    private List<PresetChoice> BuildPresetChoices()
    {
        var list = new List<PresetChoice>();
        foreach (var kinetic in KineticPresets.All)
            list.Add(new(PresetChoice.Kinetic, kinetic.Name, kinetic.Description, null, (c, ids, mode) => KineticText.ApplyTo(c, ids, kinetic, mode)) { KineticRecipe = kinetic });
        foreach (var group in AnimationPresetCatalog.RecommendedGroups)
            foreach (var preset in group.Presets)
            {
                var name = group.Presets.Count == 1 ? group.Name : $"{group.Name} · {preset.Name}";
                list.Add(new(PresetChoice.Recommended, name, PresetLine(preset), null, (c, ids, mode) => AnimationPresetApplication.ApplyTo(c, ids, preset, mode)));
            }
        foreach (var preset in PresetBoards.Animations(host.Library).Presets)
            list.Add(new(PresetChoice.Mine, preset.Name, PresetLine(preset), preset.Id, (c, ids, mode) => AnimationPresetApplication.ApplyTo(c, ids, preset, mode)));
        return list;
    }

    /// <summary>Your presets changed: the list is made again, keeping the selection where it can.</summary>
    private void PresetsChanged(string? select = null)
    {
        var keep = select ?? SelectedPreset?.Title;
        presetChoices = null;
        OnPropertyChanged(nameof(PresetChoices));
        SelectedPreset = PresetChoices.FirstOrDefault(p => p.Title == keep);
    }

    private static string PresetLine(AnimationPreset preset) =>
        preset.Steps.Count == 0 && preset.Scroll is not null ? "Scrolls the text (credits roll)."
            : string.Join(", then ", preset.Steps.Select(s => AnimationEditing.Sentence(s).ToLowerInvariant())) is { Length: > 0 } words
                ? char.ToUpperInvariant(words[0]) + words[1..] + "."
                : "";

    /// <summary>What a gallery tile plays: a small lower third with this preset on it (Core/Animation/PresetSamples.cs).</summary>
    public static Overlay Sample(PresetChoice choice) => PresetSamples.Make((c, ids) => choice.Apply(c, ids, Mode.Replace));

    [ObservableProperty]
    public partial PresetChoice? SelectedPreset { get; set; }

    partial void OnSelectedPresetChanged(PresetChoice? value) => OnPropertyChanged(nameof(CanDeletePreset));

    public bool CanDeletePreset => SelectedPreset?.SavedId is not null;

    /// <summary>Applies the chosen preset to the selected objects: "replace" swaps their steps, "add" keeps them.</summary>
    [RelayCommand]
    private void ApplyPreset(string? how)
    {
        if (Composition is null || SelectedPreset is not { } choice) return;
        var targets = host.SelectedObjects.Select(o => o.Id).ToList();
        if (targets.Count == 0)
        {
            host.Notify("Select an object first, then apply a preset to it.");
            return;
        }
        var added = host.ApplyPreset(choice, targets, how == "add" ? Mode.Add : Mode.Replace);
        Changed(objects: true);
        if (added.LastOrDefault() is { } last) SelectedStepRow = StepRows.FirstOrDefault(r => r.StepId == last);
        host.Notify($"{choice.Title.Split(" › ").Last()} on {targets.Count} object{(targets.Count == 1 ? "" : "s")}: {added.Count} step{(added.Count == 1 ? "" : "s")}.");
    }

    /// <summary>Saves the first selected object's steps as a preset of your own (on the shared AnimationPresetBoard).</summary>
    public bool SavePreset(string name)
    {
        if (host.SelectedObjects.FirstOrDefault() is not { } o || string.IsNullOrWhiteSpace(name)) return false;
        var steps = AnimationPresetApplication.Recipe(o.AnimationSteps);
        var scroll = o.TextStyle?.Scroll;
        if (steps.Count == 0 && scroll is null)
        {
            host.Notify("That object has no steps to save yet.");
            return false;
        }
        var preset = PresetBoards.SaveAnimation(host.Library, name, steps, scroll, scroll is null ? null : o.Tilt);
        PresetsChanged();
        SelectedPreset = PresetChoices.FirstOrDefault(p => p.SavedId == preset.Id);
        host.Notify($"Saved “{preset.Name}” to your presets.");
        return true;
    }

    /// <summary>Asks for a name (the view sets this), then saves the preset.</summary>
    public Func<string?>? AskPresetName { get; set; }

    [RelayCommand]
    private void SavePresetAs()
    {
        if (AskPresetName?.Invoke() is { } name) SavePreset(name);
    }

    [RelayCommand]
    private void DeletePreset()
    {
        if (SelectedPreset?.SavedId is not { } id) return;
        PresetBoards.DeleteAnimation(host.Library, id);
        SelectedPreset = null;
        PresetsChanged();
    }

    // MARK: One step per line / per word

    /// <summary>
    /// The Mac's Generate › One Step per Line / per Word, on the selected text object's whole text
    /// (Windows' editor has no text selection). The host decides how they play (an overlay's follow
    /// one another; a theme's become a kinetic build over each slide's words).
    /// </summary>
    [RelayCommand]
    private void GenerateSteps(string? by)
    {
        if (Composition is null) return;
        if (host.SelectedObjects is not [{ ObjectKind: SlideObjectKind.Text } text])
        {
            host.Notify("Select one text object to give each of its lines or words a step.");
            return;
        }
        var generator = by == "word" ? AnimationRanges.Generator.Word : AnimationRanges.Generator.Line;
        var kind = GalleryKind == AnimationKind.Morph ? AnimationKind.In : GalleryKind;
        var ids = host.GenerateSteps(text, generator, kind);
        if (ids.Count == 0) return;
        Changed();
        SelectedStepRow = StepRows.FirstOrDefault(r => r.StepId == ids[^1]);
    }
}
