using System.Threading;
using MxU.Slides.Core.Media;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Threading;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// Video Actions: a video's named marks run their actions as playback crosses them. Each live
/// video is read every refresh (four times a second) and once more just after its next mark is
/// due, so a mark fires within a few milliseconds of its time rather than up to a quarter-second
/// late. A jump (scrubbing, a typed time) fires nothing; a loop fires its marks every pass.
/// Port of the mark half of the Mac's MediaTransportController.swift (fireVideoActions).
/// </summary>
public sealed partial class MainViewModel
{
    // Each live video's last reading: where it was, and when.
    private Dictionary<string, (double Position, DateTimeOffset At)> markReads = new(StringComparer.Ordinal);
    private ITimer? markTimer;

    /// <summary>How many marks have fired this session (the self-test reads it).</summary>
    internal int VideoMarksFired { get; private set; }

    /// <summary>Fired with the media id and the mark's name each time a mark runs.</summary>
    internal event Action<string, string>? VideoMarkFired;

    private void FireVideoMarks()
    {
        var now = DateTimeOffset.Now;
        var reads = new Dictionary<string, (double Position, DateTimeOffset At)>(StringComparer.Ordinal);
        var due = new List<(string MediaId, VideoMark Mark)>();
        double? wait = null;
        foreach (var seed in Show.TransportSeeds())
        {
            if (LiveTransport(seed.MediaId) is not { Duration: > 0 } state) continue;
            markReads.TryGetValue(seed.MediaId, out var last);
            var hadLast = markReads.ContainsKey(seed.MediaId);
            var position = state.PositionAt(now);
            // A parked video that was never seen playing has crossed nothing yet.
            if (!hadLast && !state.IsPlaying) continue;
            reads[seed.MediaId] = (position, now);
            var item = Library.Get<MediaItem>(seed.MediaId);
            var points = VideoMarks.Points(item?.Marks, item?.InPoint, state.Duration);
            if (points.Count == 0) continue;
            var crossed = VideoMarks.Crossed(points, hadLast ? last.Position : null, position,
                hadLast ? (now - last.At).TotalSeconds : 0, state.Rate, state.Duration, state.IsLooping);
            foreach (var point in crossed)
            {
                Infrastructure.Log.Info($"video.mark {seed.MediaId} {point.Name} at {point.At:0.000} read {position:0.000}");
                if (item?.Marks?.FirstOrDefault(m => m.Id == point.Id) is { } mark) due.Add((seed.MediaId, mark));
            }
            if (state.IsPlaying && state.Rate > 0 && VideoMarks.UntilNext(points, position, state.Duration, state.IsLooping) is { } ahead)
                wait = Math.Min(wait ?? double.PositiveInfinity, ahead / state.Rate);
        }
        markReads = reads;
        markTimer?.Dispose();
        markTimer = null;
        if (wait is { } seconds)
        {
            // The show clock, not the thread pool: a busy PC mustn't make a mark late.
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            markTimer = ShowTimeProvider.Shared.CreateTimer(_ => dispatcher.BeginInvoke(FireVideoMarks), null,
                TimeSpan.FromSeconds(seconds + 0.002), Timeout.InfiniteTimeSpan);
        }
        foreach (var (mediaId, mark) in due)
        {
            VideoMarksFired++;
            VideoMarkFired?.Invoke(mediaId, mark.Name);
            if (mark.Actions.Count > 0) Actions.Execute(mark.Id, mark.Actions);
        }
    }

    /// <summary>What the stage display reads now (the self-test checks a video mark's countdown and name with it).</summary>
    internal Core.Stage.ConfidenceInfo StageInfoNow() => BuildConfidenceInfo();

    /// <summary>The marks of a live video in pass time, for its stage countdown.</summary>
    private IReadOnlyList<VideoMarks.Point> CountdownMarks(string mediaId, double duration)
    {
        var item = Library.Get<MediaItem>(mediaId);
        return VideoMarks.Points(item?.Marks, item?.InPoint, duration);
    }
}
