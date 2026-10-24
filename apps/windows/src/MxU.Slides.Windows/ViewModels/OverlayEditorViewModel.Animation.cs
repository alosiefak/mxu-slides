using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using Slot = MxU.Slides.Core.Animation.AnimationSequence.GroupSlot;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>One effect tile in the gallery ("Slide", "Wipe"…).</summary>
public sealed record GalleryTile(StepAnimation Animation, string Title);

// The overlay editor's Animation tab: the effect gallery (the Mac's AnimationGallery), the list
// of steps in play order grouped by when they run (AnimationSequenceList), and the selected
// step's settings (AnimationStepStrip, in AnimationStepViewModel).
public sealed partial class OverlayEditorViewModel
{
    public const string GoesUp = "WHEN IT GOES UP";
    public const string ComesDown = "WHEN IT'S TAKEN DOWN";

    // MARK: Gallery

    /// <summary>Which effects the gallery offers: 0 Bring in, 1 Take out, 2 Emphasize.</summary>
    [ObservableProperty]
    public partial int GalleryKindIndex { get; set; }

    partial void OnGalleryKindIndexChanged(int value) => OnPropertyChanged(nameof(GalleryTiles));

    private AnimationKind GalleryKind => GalleryKindIndex switch
    {
        1 => AnimationKind.Out,
        2 => AnimationKind.Emphasis,
        _ => AnimationKind.In,
    };

    public IReadOnlyList<GalleryTile> GalleryTiles =>
        AnimationEditing.Choices(GalleryKind).Select(a => new GalleryTile(a, AnimationEditing.TileTitle(a, GalleryKind))).ToList();

    /// <summary>What clicking an effect will do, in words (the Mac's target line).</summary>
    public string GalleryTarget
    {
        get
        {
            var selected = SelectedObjects();
            return selected.Count switch
            {
                0 => "Select an object on the canvas (or in Objects) to animate it.",
                1 => $"Click an effect to add it to {ObjectMasks.DisplayName(selected[0])}.",
                _ => $"Click an effect to add it to the {selected.Count} selected objects (they play together).",
            };
        }
    }

    /// <summary>
    /// Adds the effect to the selected objects. On an overlay, Ins and emphasis join
    /// "when it goes up" and Outs "when it's taken down", since an overlay never gets a click.
    /// </summary>
    [RelayCommand]
    private void AddEffect(GalleryTile? tile)
    {
        if (tile is null || Current is not { } overlay) return;
        var targets = SelectedObjects().Select(o => o.Id).ToList();
        if (targets.Count == 0)
        {
            Notice = "Select an object first, then pick its effect.";
            return;
        }
        var slot = GalleryKind == AnimationKind.Out ? Slot.Exit : Slot.Auto;
        var ids = AnimationEditing.QuickAdd(overlay, targets, GalleryKind, tile.Animation, slot);
        Changed(Parts.Steps);
        if (ids.LastOrDefault() is { } last) SelectedStepRow = StepRows.FirstOrDefault(r => r.StepId == last);
    }

    // MARK: The steps, in play order

    public ObservableCollection<StepRowViewModel> StepRows { get; } = [];

    [ObservableProperty]
    public partial StepRowViewModel? SelectedStepRow { get; set; }

    partial void OnSelectedStepRowChanged(StepRowViewModel? value)
    {
        StepEditor = value is not null && Current is { } overlay ? new AnimationStepViewModel(overlay, value.StepId, StepChanged) : null;
        if (value is not null && Current is { } c && AnimationEditing.Find(c, value.StepId) is { } found && canvas is not null
            && !(canvas.Selection.Count == 1 && canvas.Selection.Contains(found.ObjectId)))
            SelectObjects([found.ObjectId]);
    }

    [ObservableProperty]
    public partial AnimationStepViewModel? StepEditor { get; set; }

    public bool HasSteps => StepRows.Count > 0;

    private void StepChanged() => Changed(Parts.Steps);

    private void RefreshSteps()
    {
        if (Current is not { } overlay)
        {
            StepRows.Clear();
            OnPropertyChanged(nameof(HasSteps));
            return;
        }
        var groups = AnimationSequence.Groups(overlay.Objects, overlay.AnimationOrder);
        var listed = new List<(AnimationSequence.Entry Entry, string Group)>();
        listed.AddRange(groups.Auto.Select(e => (e, GoesUp)));
        for (var i = 0; i < groups.Click.Count; i++)
            listed.AddRange(groups.Click[i].Select(e => (e, $"ON CLICK {i + 1} (AN OVERLAY ONLY PLAYS ITS OUTS HERE, WHEN TAKEN DOWN)")));
        listed.AddRange(groups.Exit.Select(e => (e, ComesDown)));
        var warnings = AnimationEditing.OverlayWarnings(overlay);

        void Fill(StepRowViewModel row, AnimationSequence.Entry entry)
        {
            var owner = overlay.Objects.FirstOrDefault(o => o.Id == entry.ObjectId);
            row.Title = $"{AnimationEditing.TargetLabel(owner, entry.Step)} · {AnimationEditing.Sentence(entry.Step, owner)}";
            row.Timing = $"{entry.Step.Trigger.ShortName()} · {AnimationEditing.TimingLabel(entry.Step)}";
            row.Warning = warnings.GetValueOrDefault(entry.Step.Id);
            row.KindColor = StepRowViewModel.ColorOf(entry.Step.Kind);
        }

        var same = listed.Count == StepRows.Count
            && listed.Zip(StepRows).All(pair => pair.First.Entry.Step.Id == pair.Second.StepId && pair.First.Group == pair.Second.Group);
        if (same)
        {
            for (var i = 0; i < listed.Count; i++) Fill(StepRows[i], listed[i].Entry);
        }
        else
        {
            var selectedId = SelectedStepRow?.StepId;
            StepRows.Clear();
            foreach (var (entry, group) in listed)
            {
                var row = new StepRowViewModel(entry.Step.Id, group);
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
        Current is { } overlay ? AnimationSequence.OrderedEntries(overlay.Objects, overlay.AnimationOrder).Select(e => e.Step.Id).ToList() : [];

    [RelayCommand]
    private void RemoveStep()
    {
        if (Current is not { } overlay || SelectedStepRow is not { } row) return;
        var ids = FlatStepIds();
        var index = ids.IndexOf(row.StepId);
        if (!AnimationEditing.Remove(overlay, row.StepId)) return;
        Changed(Parts.Steps);
        SelectedStepRow = StepRows.Count == 0 ? null : StepRows[Math.Clamp(index, 0, StepRows.Count - 1)];
    }

    /// <summary>Plays this step one place sooner in the order (it may join the group before).</summary>
    [RelayCommand]
    private void MoveStepEarlier()
    {
        if (Current is not { } overlay || SelectedStepRow is not { } row) return;
        var ids = FlatStepIds();
        var index = ids.IndexOf(row.StepId);
        if (index <= 0) return;
        if (AnimationEditing.MoveBefore(overlay, row.StepId, ids[index - 1])) Changed(Parts.Steps);
    }

    [RelayCommand]
    private void MoveStepLater()
    {
        if (Current is not { } overlay || SelectedStepRow is not { } row) return;
        var ids = FlatStepIds();
        var index = ids.IndexOf(row.StepId);
        if (index < 0 || index >= ids.Count - 1) return;
        if (AnimationEditing.MoveBefore(overlay, row.StepId, index + 2 < ids.Count ? ids[index + 2] : null)) Changed(Parts.Steps);
    }

    /// <summary>Moves the step to the end of "when it goes up" or "when it's taken down", taking that group's timing.</summary>
    [RelayCommand]
    private void MoveStepTo(string where)
    {
        if (Current is not { } overlay || SelectedStepRow is not { } row) return;
        var slot = where == "down" ? Slot.Exit : Slot.Auto;
        if (AnimationEditing.MoveIntoGroup(overlay, row.StepId, slot, int.MaxValue)) Changed(Parts.Steps);
    }

    /// <summary>Plays the preview from just before the selected step.</summary>
    [RelayCommand]
    private void PreviewStep()
    {
        if (Current is not { } overlay || canvas is null || SelectedStepRow is not { } row) return;
        if (AnimationSequence.PreviewStart(row.StepId, overlay.Objects, overlay.AnimationOrder) is not { } start)
        {
            PlayPreview();
            return;
        }
        previewStartedAt = ShowState.HostClock() - Math.Max(start - 0.15, 0);
        canvas.ShowPreview(OverlayPreview.PlayThrough(overlay, SceneSize.Hd, previewStartedAt, null, BackdropScene()));
        IsPlaying = true;
        previewClock.Start();
    }
}
