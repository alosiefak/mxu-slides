using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Core.Stage;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.Outputs;
using MxU.Slides.Windows.Rendering;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>A timer in the Timers panel.</summary>
public sealed partial class TimerRowViewModel(string id) : ObservableObject
{
    public string Id { get; } = id;

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string Display { get; set; } = "";

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial Brush DisplayBrush { get; set; } = Brushes.White;
}

/// <summary>The stage display (confidence monitor), timers and alerts.</summary>
public sealed partial class MainViewModel
{
    public TimerController Timers { get; } = new();

    public ObservableCollection<TimerRowViewModel> TimerRows { get; } = [];

    public ObservableCollection<ConfidenceLayout> StageLayouts { get; } = [];

    [ObservableProperty]
    public partial ConfidenceLayout? SelectedStageLayout { get; set; }

    partial void OnSelectedStageLayoutChanged(ConfidenceLayout? value)
    {
        if (value is null) return;
        Settings.StageLayoutId = value.Id;
        Settings.Save();
        RenderStage(SceneTransition.Cut, refreshOnly: false);
    }

    [ObservableProperty]
    public partial string AlertText { get; set; } = "";

    [ObservableProperty]
    public partial bool AlertOnStage { get; set; } = true;

    [ObservableProperty]
    public partial bool AlertOnAudience { get; set; }

    [ObservableProperty]
    public partial int NewTimerMinutes { get; set; } = 5;

    private static string TimersFile => Path.Combine(AppPaths.DataRoot, "timers.json");

    private void InitializeStage()
    {
        try
        {
            if (File.Exists(TimersFile))
                Timers.Restore(JsonSerializer.Deserialize<List<PersistedTimer>>(File.ReadAllText(TimersFile)) ?? []);
        }
        catch (Exception ex)
        {
            Log.Warn($"Saved timers unreadable: {ex.Message}");
        }
        if (Timers.Timers.Count == 0)
        {
            Timers.AddCountdown("Sermon", 30 * 60);
            Timers.AddCountdown("Countdown", 5 * 60);
        }
        Timers.Changed += () =>
        {
            SaveTimers();
            SyncTimerRows();
            RenderStage(SceneTransition.Cut, refreshOnly: true);
        };
        SyncTimerRows();
        RefreshStageLayouts();
    }

    private void SaveTimers()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataRoot);
            LibraryStore.AtomicWrite(TimersFile, JsonSerializer.SerializeToUtf8Bytes(Timers.Export()));
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not save timers: {ex.Message}");
        }
    }

    private void RefreshStageLayouts()
    {
        var selectedId = SelectedStageLayout?.Id ?? Settings.StageLayoutId;
        StageLayouts.Clear();
        foreach (var layout in Library.All<ConfidenceLayout>().OrderBy(l => l.Name != "Classic")) StageLayouts.Add(layout);
        SelectedStageLayout = StageLayouts.FirstOrDefault(l => l.Id == selectedId) ?? StageLayouts.FirstOrDefault();
    }

    private void SyncTimerRows()
    {
        var ids = Timers.Timers.Select(t => t.Id).ToList();
        for (var i = TimerRows.Count - 1; i >= 0; i--)
            if (!ids.Contains(TimerRows[i].Id)) TimerRows.RemoveAt(i);
        for (var i = 0; i < ids.Count; i++)
        {
            var row = TimerRows.FirstOrDefault(r => r.Id == ids[i]);
            if (row is null) TimerRows.Insert(Math.Min(i, TimerRows.Count), new TimerRowViewModel(ids[i]));
        }
        TickTimers();
    }

    /// <summary>Refreshes the timer readouts (called four times a second).</summary>
    private void TickTimers()
    {
        var now = DateTimeOffset.Now;
        foreach (var row in TimerRows)
        {
            if (Timers.Snapshot(row.Id) is not { } timer) continue;
            row.Name = timer.Name;
            row.Display = timer.DisplayString(now);
            row.IsRunning = timer.IsRunning || timer.Mode == TimerMode.CountdownToTime;
            row.DisplayBrush = timer.ActiveWarning(now) is { } warning
                ? Painting.Brush(warning.ColorHex, SceneColor.White)
                : row.IsRunning ? Painting.Brush("#7ED957FF", SceneColor.White) : Painting.Brush("#EDEDEDFF", SceneColor.White);
        }
    }

    [RelayCommand]
    private void ToggleTimer(TimerRowViewModel? row)
    {
        if (row is not null) Timers.Toggle(row.Id);
    }

    [RelayCommand]
    private void ResetTimer(TimerRowViewModel? row)
    {
        if (row is not null) Timers.Reset(row.Id);
    }

    [RelayCommand]
    private void RemoveTimer(TimerRowViewModel? row)
    {
        if (row is not null) Timers.Remove(row.Id);
    }

    [RelayCommand]
    private void AddCountdownTimer()
    {
        var minutes = Math.Clamp(NewTimerMinutes, 1, 600);
        Timers.AddCountdown($"{minutes} min", minutes * 60);
    }

    [RelayCommand]
    private void AddCountUpTimer() => Timers.AddCountUp("Elapsed");

    public void RenameTimer(TimerRowViewModel row, string name)
    {
        if (!string.IsNullOrWhiteSpace(name)) Timers.Rename(row.Id, name.Trim());
    }

    public void SetTimerMinutes(TimerRowViewModel row, double minutes)
    {
        if (minutes > 0) Timers.SetDuration(row.Id, minutes * 60);
    }

    public void SetTimerUntil(TimerRowViewModel row, int hour, int minute) =>
        Timers.Configure(row.Id, TimerMode.CountdownToTime, hour: hour, minute: minute);

    // MARK: Alerts / stage messages

    [RelayCommand]
    private void ShowAlert()
    {
        if (string.IsNullOrWhiteSpace(AlertText)) return;
        Show.FireAlert(AlertText.Trim(), AlertOnAudience, AlertOnStage);
        liveAlertPresetId = null;
        RenderOutputs(DefaultTransition());
    }

    // MARK: What the stage display knows

    /// <summary>Everything linked text can show: live and next slide, timers, the alert, service position.</summary>
    private ConfidenceInfo BuildConfidenceInfo()
    {
        SlideText? current = null, next = null, last = null;
        SlidePosition? position = null;
        string? currentItem = null, nextItem = null, group = null, presentationName = null;

        if (Show.Live is { } live)
        {
            current = ConfidenceSceneBuilder.SlideText(live.Slide, live.Presentation);
            presentationName = live.Presentation?.Name;
            if (live.Presentation is { } p)
            {
                group = GroupNames.For(live.Slide, p);
                var arranged = SlideSceneBuilder.ArrangedSlides(p, live.ArrangementId);
                var index = Live?.Occurrence ?? arranged.FindIndex(s => s.Id == live.Slide.Id);
                position = new SlidePosition(index + 1, arranged.Count);
                var upcoming = arranged.Skip(index + 1).ToList();
                if (ConfidenceSceneBuilder.NextSlide(upcoming, skippingBlanks: true) is { } within)
                    next = ConfidenceSceneBuilder.SlideText(within, p);
            }
        }
        if (Show.Last is { } previous) last = ConfidenceSceneBuilder.SlideText(previous.Slide, previous.Presentation);

        if (SelectedService is { } service && Live?.ItemId is { } itemId)
        {
            var items = service.Items.Where(i => i.ItemKind != ServiceItemKind.Header && i.HiddenInPresenter != true).ToList();
            var index = items.FindIndex(i => i.Id == itemId);
            currentItem = index >= 0 ? items[index].Name : null;
            nextItem = index >= 0 && index + 1 < items.Count ? items[index + 1].Name : null;
            // At the end of a song, the stage shows the next song's first lyric.
            if (next is null && index >= 0 && index + 1 < items.Count
                && Library.Get<Presentation>(items[index + 1].RefId) is { } following)
            {
                var slides = SlideSceneBuilder.ArrangedSlides(following, items[index + 1].ArrangementId ?? following.DefaultArrangementId);
                if (ConfidenceSceneBuilder.NextSlide(slides, skippingBlanks: true) is { } first)
                    next = ConfidenceSceneBuilder.SlideText(first, following);
            }
        }

        CueAlert? alert = Show.AlertMessage is { } message
            ? new CueAlert(message, AlertBehavior.Persist,
                Show.AlertOnAudience && Show.AlertOnStage ? AlertTarget.Both : Show.AlertOnAudience ? AlertTarget.Audience : AlertTarget.Confidence)
            : null;

        return new ConfidenceInfo
        {
            Current = current,
            Next = next,
            Last = last,
            Alert = alert,
            Timers = Timers.Timers,
            VideoCountdown = LiveVideoCountdown(),
            Timecode = publishedTimecode,
            SlidePosition = position,
            CurrentItemName = currentItem ?? presentationName,
            NextItemName = nextItem,
            CurrentGroupName = group,
            CurrentPresentationName = presentationName,
            NextServiceTitle = SelectedService?.Name,
        };
    }

    /// <summary>Time left on the live foreground (else background) video, for the stage's video countdown.</summary>
    private VideoCountdown? LiveVideoCountdown()
    {
        foreach (var layer in new[] { LayerKind.Videos, LayerKind.LoopingVideos })
        {
            if (!Show.LiveMedia.TryGetValue(layer, out var cue)) continue;
            var key = $"live:live-media-{layer.Raw()}:{cue.MediaId}";
            if (Media.Transport(key) is not { Duration: { } duration } transport) continue;
            return new VideoCountdown(Media.NameOf(cue.MediaId) ?? "Video", duration.TotalSeconds, transport.Position.TotalSeconds,
                DateTimeOffset.Now, transport.Playing);
        }
        return null;
    }

    private List<SlideObject> ResolveLinkedText(IReadOnlyList<SlideObject> objects)
    {
        if (!objects.Any(o => o.TextLink is not null)) return objects.ToList();
        return LinkedText.ResolvedObjects(objects, BuildConfidenceInfo(), DateTimeOffset.Now);
    }

    /// <summary>The stage scene: the chosen layout's objects with live text filled in, plus the alert band.</summary>
    public RenderScene StageScene()
    {
        var layout = SelectedStageLayout ?? ConfidenceSceneBuilder.BuiltInLayout;
        var canvas = new SceneSize(layout.CanvasWidth ?? 1920, layout.CanvasHeight ?? 1080);
        var scene = new RenderScene(canvas);
        var info = BuildConfidenceInfo();
        var now = DateTimeOffset.Now;
        var resolved = LinkedText.ResolvedObjects(layout.Objects, info, now);
        SlideSceneBuilder.AddObjects(scene, resolved.Where(o => VisibilityRules.IsShown(o, resolved, info, now)).ToList(),
            LayerKind.Slide, canvas, $"stage-{layout.Id}-");
        if (info.Alert is { ShowsOnConfidence: true } alert && !layout.HasStageMessageObject())
            foreach (var item in AlertBanner.Items(LinkedText.ResolvedAlertMessage(alert.Message, info, now), canvas, "stage"))
                scene.AddItem(item, LayerKind.Alerts);
        return scene;
    }

    private void RenderStage(SceneTransition transition, bool refreshOnly)
    {
        if (Outputs?.IsShowing(OutputRole.Stage) != true && StagePreview is null) return;
        var scene = StageScene();
        if (refreshOnly)
        {
            Outputs?.Update(OutputRole.Stage, scene);
            StagePreview?.Update(scene);
        }
        else
        {
            Outputs?.Present(OutputRole.Stage, scene, SceneTransition.Cut);
            StagePreview?.Present(scene, SceneTransition.Cut);
        }
    }

    /// <summary>Clocks and timers change every second; anything else only changes when a cue fires.</summary>
    private bool NeedsTicking() =>
        Outputs?.IsShowing(OutputRole.Stage) == true || StagePreview is not null
        || Show.Live?.Slide.Objects.Any(o => o.TextLink is { } link && LinkedText.IsTimeVarying(link)) == true
        || Show.LiveOverlays.Any(o => o.Objects.Any(x => x.TextLink is { } link && LinkedText.IsTimeVarying(link)))
        || Show.Live?.Theme?.Slides?.Any(s => s.Objects.Any(x => x.TextLink is { } link && LinkedText.IsTimeVarying(link))) == true;
}
