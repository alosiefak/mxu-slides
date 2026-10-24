using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MxU.Slides.Core.Media;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Core.Stage;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// The live video transport strip under the audience preview: play/pause, a scrub bar, time
/// gone and left, back to the start, jump to near the end, and typing a time to go to. It
/// controls one video at a time: the one on the layer the operator looks at (Foreground Videos
/// unless they pick another), chosen by Core's <see cref="TransportTarget"/>. Port of the Mac's
/// MediaTransportController.swift and MediaTransportStrip.swift.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The video players the audience outputs and preview share (SceneView.PlaybackScope).</summary>
    private const string LivePlaybackScope = "live";

    // When each live video was first seen: the earliest one keeps the strip (a later fire never steals it).
    private readonly Dictionary<string, DateTimeOffset> transportFiredAt = new(StringComparer.Ordinal);
    private string? transportPinnedId;
    private string? transportTargetId;

    /// <summary>One video the strip could control, for the layer picker.</summary>
    public sealed record TransportRow(TransportCandidate Candidate, string Name, MediaTransportState State);

    /// <summary>The player showing a media item on the audience outputs, by its playback key ("live:item-id:media-id").</summary>
    private string? LivePlayerKey(string mediaId) =>
        Media.ActiveVideos.Where(v => v.MediaId == mediaId && v.Key.StartsWith(LivePlaybackScope + ":", StringComparison.Ordinal))
            .Select(v => v.Key).FirstOrDefault();

    /// <summary>
    /// The live video for a media item, read now, in the pass the operator sees (from the item's
    /// in point to its out point, at its play rate). Null when no live player has it open.
    /// <para>
    /// This relies on MediaHub.Transport reporting the position in the file and the file's
    /// length, and MediaHub.Seek taking a time in the file; if MediaHub starts reporting the pass
    /// itself, drop the <see cref="MediaTransportState.Pass"/> step here and the seek's FileTime.
    /// </para>
    /// </summary>
    private MediaTransportState? LiveTransport(string mediaId, bool? loops = null)
    {
        if (LivePlayerKey(mediaId) is not { } key || Media.Transport(key) is not { Duration: { } duration } transport) return null;
        var item = Library.Get<MediaItem>(mediaId);
        var position = transport.Position.TotalSeconds;
        // A player that said it reached the end still reads there (a frame short, or at the out
        // point): count it as at the end, so "finished" doesn't depend on the last frame's timing.
        if (endedPlayers.TryGetValue(key, out var endedAt))
        {
            if (!transport.Playing && Math.Abs(position - endedAt) < 0.05)
                position = item?.OutPoint is { } outPoint && outPoint < duration.TotalSeconds ? outPoint : duration.TotalSeconds;
            else
                endedPlayers.Remove(key);
        }
        return MediaTransportState.Pass(position, duration.TotalSeconds, item?.InPoint, item?.OutPoint,
            transport.Playing, loops ?? LoopsLive(mediaId), DateTimeOffset.Now, item?.PlayRate);
    }

    // Players that raised VideoEnded, with where they stopped (in the file).
    private readonly Dictionary<string, double> endedPlayers = new(StringComparer.Ordinal);

    private void NoteVideoEnded(string playbackKey)
    {
        if (Media.Transport(playbackKey) is { } transport) endedPlayers[playbackKey] = transport.Position.TotalSeconds;
    }

    /// <summary>Whether the live copy of a media item loops: its cue's (or slide object's) own setting, else yes, as the scene builder draws it.</summary>
    private bool LoopsLive(string mediaId)
    {
        foreach (var cue in Show.LiveMedia.Values)
            if (cue.MediaId == mediaId) return cue.Loops ?? true;
        var objects = (Show.Live?.Slide.Objects ?? []).Concat(Show.LiveOverlays.SelectMany(o => o.Objects));
        foreach (var o in objects.Select(SlideObjectNormalization.Normalized))
            if (o.Fill is { FillKind: FillKind.Media } fill && fill.MediaId == mediaId) return fill.Loops ?? true;
        return true;
    }

    /// <summary>Every live video with a player, top layers first, as the strip's picker lists them.</summary>
    private List<TransportRow> TransportRows()
    {
        var now = DateTimeOffset.Now;
        var rows = new List<TransportRow>();
        foreach (var seed in Show.TransportSeeds())
        {
            if (LiveTransport(seed.MediaId) is not { Duration: > 0 } state) continue;
            if (!transportFiredAt.TryGetValue(seed.MediaId, out var firedAt)) transportFiredAt[seed.MediaId] = firedAt = now;
            rows.Add(new TransportRow(new TransportCandidate(seed.MediaId, seed.Layer, seed.Origin, state.IsLooping, firedAt),
                Library.Get<MediaItem>(seed.MediaId)?.Name ?? "Video", state));
        }
        var live = rows.Select(r => r.Candidate.MediaId).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in transportFiredAt.Keys.Where(id => !live.Contains(id)).ToList()) transportFiredAt.Remove(gone);
        if (transportPinnedId is not null && !live.Contains(transportPinnedId)) transportPinnedId = null;
        return rows;
    }

    /// <summary>The layer the strip looks at.</summary>
    public LayerKind TransportViewedLayer => LayerKinds.Parse(Settings.TransportLayer) ?? TransportTarget.DefaultLayer;

    // MARK: What the strip shows

    [ObservableProperty]
    public partial bool TransportHasVideo { get; set; }

    /// <summary>The strip has its controls (a looping background only shows its time left, unless picked).</summary>
    [ObservableProperty]
    public partial bool TransportShowsControls { get; set; }

    [ObservableProperty]
    public partial string TransportName { get; set; } = "No video playing";

    [ObservableProperty]
    public partial string TransportLayerName { get; set; } = TransportTarget.DefaultLayer.DisplayName();

    [ObservableProperty]
    public partial bool TransportPlaying { get; set; }

    /// <summary>The video played to its end: the play button becomes "back to the start".</summary>
    [ObservableProperty]
    public partial bool TransportFinished { get; set; }

    [ObservableProperty]
    public partial bool TransportLooping { get; set; }

    [ObservableProperty]
    public partial double TransportPosition { get; set; }

    [ObservableProperty]
    public partial double TransportDuration { get; set; } = 1;

    [ObservableProperty]
    public partial string TransportElapsedText { get; set; } = "0:00";

    [ObservableProperty]
    public partial string TransportTotalText { get; set; } = "0:00";

    [ObservableProperty]
    public partial string TransportRemainingText { get; set; } = "";

    /// <summary>The jump button's caption: "-30", "-2m", or "End".</summary>
    public string TransportSkipCaption => OffsetCaption(Settings.TransportSkipToEndSeconds);

    /// <summary>Set while the operator drags the scrub bar, so the refresh doesn't fight the thumb.</summary>
    public bool TransportScrubbing { get; set; }

    /// <summary>Brings the strip up to date (four times a second while anything is live).</summary>
    private void RefreshTransport()
    {
        var rows = TransportRows();
        var candidates = rows.Select(r => r.Candidate).ToList();
        var target = TransportTarget.Resolve(candidates, TransportViewedLayer, transportPinnedId, preferNonLooping: transportPinnedId is null);
        var row = target is null ? null : rows.First(r => r.Candidate.MediaId == target.MediaId);
        transportTargetId = row?.Candidate.MediaId;
        TransportHasVideo = row is not null;
        TransportLayerName = (row?.Candidate.Layer ?? TransportViewedLayer).DisplayName();
        if (row is null)
        {
            TransportShowsControls = false;
            TransportName = "No video playing";
            TransportElapsedText = TransportTotalText = "0:00";
            TransportRemainingText = "";
            TransportPlaying = TransportFinished = TransportLooping = false;
            return;
        }
        var now = DateTimeOffset.Now;
        var state = row.State;
        TransportShowsControls = !row.Candidate.IsLooping || transportPinnedId == row.Candidate.MediaId;
        TransportName = row.Name;
        TransportPlaying = state.IsPlaying;
        TransportFinished = state.IsFinished(now);
        TransportLooping = state.IsLooping;
        TransportDuration = Math.Max(state.Duration, 0.01);
        if (!TransportScrubbing) TransportPosition = state.PositionAt(now);
        TransportElapsedText = TransportClock(state.PositionAt(now));
        TransportTotalText = TransportClock(state.Duration);
        TransportRemainingText = $"{TransportClock(state.RemainingAt(now))} left";
    }

    /// <summary>m:ss, rounded down, as the Mac's scrub bar shows it.</summary>
    internal static string TransportClock(double seconds)
    {
        var total = (int)Math.Floor(Math.Max(0, seconds));
        return $"{total / 60}:{total % 60:00}";
    }

    internal static string OffsetCaption(int seconds) => seconds == 0 ? "End" : seconds < 60 ? $"-{seconds}" : $"-{seconds / 60}m";

    // MARK: What the strip does

    [RelayCommand]
    private void TransportPlayPause()
    {
        if (transportTargetId is not { } id || LivePlayerKey(id) is not { } key || LiveTransport(id) is not { } state) return;
        if (state.IsFinished(DateTimeOffset.Now)) TransportSeek(0);
        else if (state.IsPlaying) Media.Pause(key);
        else Media.Play(key);
        RefreshTransport();
    }

    /// <summary>Back to the start; playing or paused stays as it was.</summary>
    [RelayCommand]
    private void TransportBackToStart() => TransportSeek(0);

    /// <summary>Jumps to the chosen amount before the end (30 s unless changed), to check the ending or skip a long intro.</summary>
    [RelayCommand]
    private void TransportSkipToEnd()
    {
        if (transportTargetId is { } id && LiveTransport(id) is { } state) TransportSeek(Math.Max(0, state.Duration - Settings.TransportSkipToEndSeconds));
    }

    /// <summary>Goes to a time in the pass (the scrub bar, a typed time); playing or paused stays as it was.</summary>
    public void TransportSeek(double seconds)
    {
        if (transportTargetId is not { } id || LivePlayerKey(id) is not { } key || LiveTransport(id) is not { } state) return;
        var pass = Math.Clamp(seconds, 0, state.Duration);
        Media.Seek(key, TimeSpan.FromSeconds(MediaTransportState.FileTime(pass, Library.Get<MediaItem>(id)?.InPoint)));
        RefreshTransport();
        AutoAdvance.PollNow();
    }

    /// <summary>A typed time ("1:30", "90", "1:02:03"): goes there. False when it can't be read.</summary>
    public bool TransportGoTo(string typed)
    {
        if (ParseTime(typed) is not { } seconds) return false;
        TransportSeek(seconds);
        return true;
    }

    /// <summary>Seconds from "ss", "m:ss" or "h:mm:ss".</summary>
    internal static double? ParseTime(string typed)
    {
        var parts = typed.Trim().Split(':');
        if (parts.Length is < 1 or > 3) return null;
        double total = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0) return null;
            total = total * 60 + value;
        }
        return total;
    }

    /// <summary>The jump amount (right-click the jump button): 0 = the very end.</summary>
    public void SetTransportSkip(int seconds)
    {
        Settings.TransportSkipToEndSeconds = Math.Max(0, seconds);
        Settings.Save();
        OnPropertyChanged(nameof(TransportSkipCaption));
    }

    /// <summary>The layer picker: which layer the strip looks at (a pinned video is let go).</summary>
    public void SelectTransportLayer(LayerKind layer)
    {
        Settings.TransportLayer = layer.Raw();
        Settings.Save();
        transportPinnedId = null;
        RefreshTransport();
    }

    /// <summary>The layer picker: control this video, whatever layer it's on (loops included).</summary>
    public void PinTransport(string mediaId)
    {
        transportPinnedId = mediaId;
        if (TransportRows().FirstOrDefault(r => r.Candidate.MediaId == mediaId) is { } row) Settings.TransportLayer = row.Candidate.Layer.Raw();
        RefreshTransport();
    }

    /// <summary>What the layer picker lists: each layer (top first), with the videos playing on it.</summary>
    public IReadOnlyList<(LayerKind Layer, bool Viewed, IReadOnlyList<(string MediaId, string Label, bool Pinned)> Videos)> TransportPicker()
    {
        var now = DateTimeOffset.Now;
        var rows = TransportRows();
        LayerKind[] fixedLayers = [LayerKind.Alerts, LayerKind.Overlays, LayerKind.Slide, LayerKind.Videos, LayerKind.LoopingVideos];
        return
        [
            .. LayerKinds.All.Reverse()
                .Where(layer => fixedLayers.Contains(layer) || rows.Any(r => r.Candidate.Layer == layer))
                .Select(layer => (layer, layer == TransportViewedLayer,
                    (IReadOnlyList<(string, string, bool)>)[.. rows.Where(r => r.Candidate.Layer == layer)
                        .Select(r => (r.Candidate.MediaId, $"{r.Name}  ·  {TransportClock(r.State.RemainingAt(now))} left", transportPinnedId == r.Candidate.MediaId))])),
        ];
    }

    /// <summary>
    /// What the stage display's video countdown reads: the Foreground Videos layer's video, as on
    /// the Mac (a background loop never counts down there).
    /// </summary>
    private VideoCountdown? TransportCountdown(LayerKind layer = TransportTarget.DefaultLayer)
    {
        var rows = TransportRows();
        if (TransportTarget.CountdownTarget([.. rows.Select(r => r.Candidate)], layer) is not { } target) return null;
        var row = rows.First(r => r.Candidate.MediaId == target.MediaId);
        var now = DateTimeOffset.Now;
        return new VideoCountdown(row.Name, row.State.Duration, row.State.PositionAt(now), now, row.State.IsPlaying);
    }
}
