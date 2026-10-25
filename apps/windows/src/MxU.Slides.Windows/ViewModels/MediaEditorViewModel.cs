using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Media;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Rendering;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// The media editor: one media item's name, behaviour (background or foreground, loop, speed,
/// volume, transition, auto advance), trim with a scrubbable preview, look (fit, zoom, crop) and
/// effects, and its file. The item is the library's own copy: every change is written onto it,
/// shown in the preview straight away, saved a moment after the changes stop, and shown on the
/// outputs if the item is live.
/// <para>
/// Port of the Mac's Sources/MediaEditorView.swift (and MediaPreviewPlayer). The preview has its own
/// players (its own <see cref="MediaHub"/>), so scrubbing here never touches what the room sees, and
/// it plays the whole file so a trim can be set anywhere. The fit, zoom, crop and volume are
/// Windows-only (<see cref="MediaLook"/>).
/// </para>
/// </summary>
public sealed partial class MediaEditorViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel main;
    private readonly DispatcherTimer saveTimer;
    private readonly DispatcherTimer clock;
    private bool itemDirty;
    private bool lookDirty;
    private MediaLook look;

    public MediaEditorViewModel(MainViewModel main, MediaItem item)
    {
        this.main = main;
        Item = item;
        look = main.MediaLooks.For(item);
        // The preview plays the whole file (no trim), on players of its own.
        PreviewHub = new MediaHub(id => main.Media.Resolve(id)) { VideoVolume = main.Settings.VideoVolume };
        saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => Flush(), Dispatcher.CurrentDispatcher);
        saveTimer.Stop();
        clock = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher.CurrentDispatcher);
        if (IsTimeBased) clock.Start();
        else clock.Stop();
        Effects = new EffectsEditorViewModel(() => Item.Effects, chain => Item.Effects = chain, () => ItemChanged(), main.Library);
        main.Library.Changed += OnLibraryChanged;
    }

    public void Dispose()
    {
        clock.Stop();
        Flush();
        main.Library.Changed -= OnLibraryChanged;
    }

    /// <summary>The item being edited: the library's own copy.</summary>
    public MediaItem Item { get; private set; }

    /// <summary>The players the preview draws with (its own, untrimmed).</summary>
    public MediaHub PreviewHub { get; }

    public bool IsVideo => Item.MediaKind == MediaKind.Video;

    public bool IsTimeBased => IsVideo;

    public string Title => $"Media: {Item.Name}";

    // MARK: Saving

    // Undo: integration opens this window's own undo scope around Flush (wt\p7-editing's UndoScope),
    // so every save below lands in it; nothing else here needs to change.
    /// <summary>Saves what changed (the item, its Windows look) now, rather than after the pause.</summary>
    public void Flush()
    {
        saveTimer.Stop();
        if (itemDirty)
        {
            itemDirty = false;
            main.SaveMediaItem(Item);
        }
        if (lookDirty)
        {
            lookDirty = false;
            main.SaveMediaLook(Item, look);
        }
    }

    private void ItemChanged(bool savesNow = false)
    {
        itemDirty = true;
        PreviewChanged?.Invoke();
        if (savesNow) Flush();
        else Restart();
    }

    private void LookChanged()
    {
        lookDirty = true;
        PreviewChanged?.Invoke();
        OnPropertyChanged(nameof(LookSummary));
        Restart();
    }

    private void Restart()
    {
        saveTimer.Stop();
        saveTimer.Start();
    }

    /// <summary>The item was changed or replaced elsewhere (an import, the bin's menu): follow the library's copy.</summary>
    private void OnLibraryChanged(Type type, string id)
    {
        if (type != typeof(MediaItem) || id != Item.Id) return;
        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => OnLibraryChanged(type, id));
            return;
        }
        if (main.Library.Get<MediaItem>(id) is { } current && !ReferenceEquals(current, Item))
        {
            Item = current;
            look = main.MediaLooks.For(current);
            OnPropertyChanged(string.Empty);
            PreviewChanged?.Invoke();
        }
    }

    /// <summary>Raised when the preview has to be drawn again (an effect, the look, the item).</summary>
    public event Action? PreviewChanged;

    /// <summary>The picture the preview shows: the item full screen, in its effects and look, as an output shows it.</summary>
    public AnimatedScene PreviewScene()
    {
        var scene = new RenderScene(SceneSize.Hd);
        scene.AddItem(new RenderItem
        {
            Id = PreviewItemId, Frame = new SceneRect(SceneSize.Hd), Content = new ItemContent.Media(Item.Id, SceneMediaScaleMode.Fill), Loops = true,
        }, IsVideo ? LayerKind.Videos : LayerKind.StillGraphics);
        return MediaDressing.Dress(AnimatedScene.From(scene), _ => new MediaDress(Item.Effects, look));
    }

    public const string PreviewScope = "media-editor";
    public const string PreviewItemId = "preview";

    /// <summary>The preview player's key in <see cref="PreviewHub"/> (SceneView's "scope:item:media").</summary>
    public string PreviewKey => $"{PreviewScope}:{PreviewItemId}:{Item.Id}";

    // MARK: Name and behaviour

    public string Name
    {
        get => Item.Name;
        set
        {
            var trimmed = value.Trim();
            if (trimmed.Length == 0 || trimmed == Item.Name)
            {
                OnPropertyChanged();
                return;
            }
            Item.Name = trimmed;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Title));
            ItemChanged(savesNow: true);
        }
    }

    /// <summary>0 background (behind slides, stays across them), 1 foreground (cleared by the next slide).</summary>
    public int ClassificationIndex
    {
        get => Item.Classification == MediaClassification.Foreground ? 1 : 0;
        set
        {
            var next = value == 1 ? MediaClassification.Foreground : MediaClassification.Background;
            if (next == Item.Classification) return;
            Item.Classification = next;
            ItemChanged(savesNow: true);
        }
    }

    public bool Loops
    {
        get => Item.Loops;
        set
        {
            if (value == Item.Loops) return;
            Item.Loops = value;
            ItemChanged(savesNow: true);
        }
    }

    public IReadOnlyList<Choice<double>> RateChoices { get; } = [.. MediaTrimEdit.RateChoices.Select(r => new Choice<double>(r, MediaTrimEdit.RateLabel(r)))];

    public Choice<double>? PlayRate
    {
        get => RateChoices.FirstOrDefault(c => Math.Abs(c.Value - (Item.PlayRate ?? 1)) < 1e-6)
            ?? new Choice<double>(Item.PlayRate ?? 1, MediaTrimEdit.RateLabel(Item.PlayRate ?? 1));
        set
        {
            if (value is null) return;
            var stored = MediaTrimEdit.StoredRate(value.Value);
            if (stored == Item.PlayRate) return;
            Item.PlayRate = stored;
            OnPropertyChanged(nameof(PlaysText));
            ApplyPreviewRate();
            ItemChanged(savesNow: true);
        }
    }

    /// <summary>The video's own volume, 0–100 % (Windows).</summary>
    public double Volume
    {
        get => Math.Round(look.Volume * 100);
        set
        {
            var next = Math.Clamp(value, 0, 100) / 100;
            if (Math.Abs(next - look.Volume) < 0.001) return;
            look = look with { Volume = next };
            OnPropertyChanged();
            LookChanged();
        }
    }

    /// <summary>"Transition: Dissolve 0.7 s" (or the default), for the button that opens the picker.</summary>
    public string TransitionText => main.MediaTransitionChoice(Item) is { } chosen
        ? $"Transition: {WindowsTransitions.Describe(chosen)}…"
        : $"Transition: {main.RoomDefaultLabel(media: true)}…";

    public string AutoAdvanceText => Item.AutoAdvance is { } advance ? $"Auto Advance: {AutoAdvanceEditing.Value([advance])}…" : "Auto Advance: Off…";

    public string ActionsText => Item.Actions is { Count: > 0 } actions ? $"Actions: {actions.Count}…" : "Actions…";

    /// <summary>The transition, auto advance or actions changed in their own windows: show the new words.</summary>
    public void SettingsChanged() => OnPropertyChanged(string.Empty);

    // MARK: Trim and the preview's transport

    [ObservableProperty]
    public partial double Duration { get; set; }

    [ObservableProperty]
    public partial double Position { get; set; }

    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    /// <summary>The preview plays its sound (off by default: a preview during a service shouldn't reach the room).</summary>
    [ObservableProperty]
    public partial bool Listen { get; set; }

    partial void OnListenChanged(bool value) => PreviewChanged?.Invoke();

    private bool pausedOnOpen;

    private void Tick()
    {
        if (PreviewHub.Transport(PreviewKey) is not { } transport) return;
        if (!pausedOnOpen)
        {
            // The hub starts a video playing as it opens; the editor starts paused, at the in point.
            pausedOnOpen = true;
            PreviewHub.Pause(PreviewKey);
            ApplyPreviewRate();
            if (Item.InPoint is { } start) PreviewHub.Seek(PreviewKey, TimeSpan.FromSeconds(start));
            transport = PreviewHub.Transport(PreviewKey) ?? transport;
        }
        Duration = transport.Duration?.TotalSeconds ?? 0;
        Position = transport.Position.TotalSeconds;
        IsPlaying = transport.Playing;
        OnPropertyChanged(nameof(TimeText));
        OnPropertyChanged(nameof(PlaysText));
        OnPropertyChanged(nameof(Trim));
        // Playing inside a trim: back to the in point at the out point, as the Mac's preview loops.
        if (IsPlaying && MediaTrimEdit.Window(Item.InPoint, Item.OutPoint, Duration) is { } window && Position >= window.End - 0.06)
            PreviewHub.Seek(PreviewKey, TimeSpan.FromSeconds(window.Start));
    }

    private void ApplyPreviewRate()
    {
        // The preview's players aren't trimmed, so the speed is given to its player directly.
        PreviewHub.SetRate(PreviewKey, Item.PlayRate ?? 1);
    }

    [RelayCommand]
    private void TogglePlay()
    {
        if (PreviewHub.Transport(PreviewKey) is not { } transport) return;
        if (transport.Playing)
        {
            PreviewHub.Pause(PreviewKey);
        }
        else
        {
            var duration = transport.Duration?.TotalSeconds ?? 0;
            var at = transport.Position.TotalSeconds;
            if (MediaTrimEdit.Window(Item.InPoint, Item.OutPoint, duration) is { } window)
            {
                if (at < window.Start - 0.05 || at >= window.End - 0.1) PreviewHub.Seek(PreviewKey, TimeSpan.FromSeconds(window.Start));
            }
            else if (duration > 0 && at >= duration - 0.05)
            {
                PreviewHub.Seek(PreviewKey, TimeSpan.Zero);
            }
            PreviewHub.Play(PreviewKey);
            ApplyPreviewRate();
        }
        Tick();
    }

    /// <summary>The scrub bar moved the playhead.</summary>
    public void SeekTo(double seconds)
    {
        PreviewHub.Seek(PreviewKey, TimeSpan.FromSeconds(Math.Max(0, seconds)));
        Position = seconds;
        OnPropertyChanged(nameof(TimeText));
    }

    public string TimeText => Duration > 0 ? $"{MediaTrimEdit.Timecode(Position)} / {MediaTrimEdit.Timecode(Duration)}" : "Opening…";

    /// <summary>The scrub bar's trim handles (fractions), or null for the whole file.</summary>
    public (double Start, double End)? Trim => MediaTrimEdit.Fractions(Item.InPoint, Item.OutPoint, Duration);

    public string InPointText
    {
        get => Item.InPoint is { } v ? MediaTrimEdit.Timecode(v) : "";
        set
        {
            var text = value.Trim();
            var seconds = MediaTrimEdit.Seconds(text);
            if (text.Length > 0 && seconds is null || Duration <= 0)
            {
                OnPropertyChanged();
                return;
            }
            var points = MediaTrimEdit.TypedIn(text.Length == 0 ? null : seconds, Item.OutPoint, Duration);
            WriteTrim(points.InPoint, points.OutPoint);
        }
    }

    public string OutPointText
    {
        get => Item.OutPoint is { } v ? MediaTrimEdit.Timecode(v) : "";
        set
        {
            var text = value.Trim();
            var seconds = MediaTrimEdit.Seconds(text);
            if (text.Length > 0 && seconds is null || Duration <= 0
                || MediaTrimEdit.TypedOut(text.Length == 0 ? null : seconds, Item.InPoint, Duration) is not { } points)
            {
                OnPropertyChanged();
                return;
            }
            WriteTrim(points.InPoint, points.OutPoint);
        }
    }

    /// <summary>Set the in point at the playhead (an in point past the out point clears the out).</summary>
    [RelayCommand]
    private void SetInPoint()
    {
        var t = MediaTrimEdit.Tidy(Position);
        if (Item.OutPoint is { } o && t >= o) WriteTrim(t, null);
        else WriteTrim(t > MediaTrimEdit.EdgeSlack ? t : null, Item.OutPoint);
    }

    /// <summary>Set the out point at the playhead (not before the in point).</summary>
    [RelayCommand]
    private void SetOutPoint()
    {
        var t = MediaTrimEdit.Tidy(Position);
        if (t <= (Item.InPoint ?? 0) + MediaTrimEdit.EdgeSlack) return;
        WriteTrim(Item.InPoint, Duration > 0 && t >= Duration - MediaTrimEdit.EdgeSlack ? null : t);
    }

    [RelayCommand]
    private void ClearInPoint() => WriteTrim(null, Item.OutPoint);

    [RelayCommand]
    private void ClearOutPoint() => WriteTrim(Item.InPoint, null);

    /// <summary>The scrub bar's handles were let go at these fractions.</summary>
    public void TrimmedTo((double Start, double End) fractions)
    {
        if (Duration <= 0) return;
        var points = MediaTrimEdit.Points(fractions, Duration);
        WriteTrim(points.InPoint, points.OutPoint);
    }

    private void WriteTrim(double? inPoint, double? outPoint)
    {
        if (inPoint == Item.InPoint && outPoint == Item.OutPoint)
        {
            OnPropertyChanged(nameof(InPointText));
            OnPropertyChanged(nameof(OutPointText));
            return;
        }
        Item.InPoint = inPoint;
        Item.OutPoint = outPoint;
        OnPropertyChanged(nameof(InPointText));
        OnPropertyChanged(nameof(OutPointText));
        OnPropertyChanged(nameof(Trim));
        OnPropertyChanged(nameof(PlaysText));
        OnPropertyChanged(nameof(HasTrim));
        ItemChanged(savesNow: true);
    }

    public bool HasTrim => Item.InPoint is not null || Item.OutPoint is not null;

    /// <summary>"Plays 0:25" for a trimmed item, at its speed.</summary>
    public string PlaysText => MediaTrimEdit.PlaysFor(Item.InPoint, Item.OutPoint, Duration, Item.PlayRate) is { } plays ? $"Plays {MediaTrimEdit.Length(plays)}" : "";

    // MARK: Look (Windows-only: MediaLook)

    /// <summary>0 fill the screen, 1 fit it whole, 2 stretch.</summary>
    public int FitIndex
    {
        get => (int)look.Fit;
        set
        {
            var fit = (MediaLookFit)Math.Clamp(value, 0, 2);
            if (fit == look.Fit) return;
            look = look with { Fit = fit };
            LookChanged();
        }
    }

    /// <summary>The zoom, 10–400 %.</summary>
    public double Zoom
    {
        get => Math.Round(look.Scale * 100);
        set
        {
            var next = Math.Clamp(value, MediaLook.MinimumScale * 100, MediaLook.MaximumScale * 100) / 100;
            if (Math.Abs(next - look.Scale) < 0.001) return;
            look = look with { Scale = next };
            OnPropertyChanged();
            LookChanged();
        }
    }

    private MediaCrop Crop => look.Crop ?? MediaCrop.None;

    private void SetCrop(MediaCrop crop, string property)
    {
        var clamped = crop.Clamped();
        if (clamped == Crop) return;
        look = look with { Crop = clamped.IsNone ? null : clamped };
        OnPropertyChanged(property);
        LookChanged();
    }

    /// <summary>The crop from each edge, 0–45 % of the picture.</summary>
    public double CropLeft { get => Math.Round(Crop.Left * 100, 1); set => SetCrop(Crop with { Left = value / 100 }, nameof(CropLeft)); }

    public double CropTop { get => Math.Round(Crop.Top * 100, 1); set => SetCrop(Crop with { Top = value / 100 }, nameof(CropTop)); }

    public double CropRight { get => Math.Round(Crop.Right * 100, 1); set => SetCrop(Crop with { Right = value / 100 }, nameof(CropRight)); }

    public double CropBottom { get => Math.Round(Crop.Bottom * 100, 1); set => SetCrop(Crop with { Bottom = value / 100 }, nameof(CropBottom)); }

    /// <summary>Back to filling the screen, whole, at full volume: as a Mac shows it.</summary>
    [RelayCommand]
    private void ResetLook()
    {
        look = MediaLook.Default;
        OnPropertyChanged(string.Empty);
        LookChanged();
    }

    public string LookSummary => look.IsDefault
        ? "Fills the screen, as on a Mac."
        : "Windows only: a Mac shows it filling the screen at full volume.";

    /// <summary>The look as it is now (the self-test reads it).</summary>
    public MediaLook Look => look;

    // MARK: Effects

    public EffectsEditorViewModel Effects { get; }

    // MARK: File

    public string FileKind => Item.MediaKind == MediaKind.Video ? "Video" : "Image";

    public string FileName => Item.FileName;

    public string Added => Item.ImportedAt is { } at ? DateTimeOffset.FromUnixTimeMilliseconds((long)(at * 1000)).LocalDateTime.ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture) : "";

    public string Dimensions => Item.PixelWidth is { } w && Item.PixelHeight is { } h ? $"{w} × {h}" : "";

    public string Length => Item.DurationSeconds is { } d ? MediaTrimEdit.Length(d) : Duration > 0 ? MediaTrimEdit.Length(Duration) : "";

    public string Status => main.Library.BlobPath(Item) is null ? "Missing: the file isn't on this PC"
        : Item.FileStatus switch
        {
            MediaFileStatus.Ready => "Ready",
            MediaFileStatus.NeedsTranscode => $"Needs transcode: {Item.StatusDetail}",
            MediaFileStatus.Transcoding => "Transcoding…",
            _ => $"Transcode failed: {Item.StatusDetail}",
        };

    public string? FilePath => main.Library.BlobPath(Item);
}
