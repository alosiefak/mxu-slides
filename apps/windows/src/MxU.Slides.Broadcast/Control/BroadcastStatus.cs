using System.Globalization;
using MxU.Slides.Broadcast.Streaming;

namespace MxU.Slides.Broadcast.Control;

/// <summary>
/// The words and colour of the toolbar's broadcast chip and its status menu, worked out from
/// the controllers so the WPF view only draws them. Port of the non-UI parts of the Mac's
/// BroadcastStatusChip.swift. Call from the UI thread (it reads the controllers).
/// </summary>
public static class BroadcastStatus
{
    public enum ChipTint { None, Green, Orange, Red }

    /// <summary>Red when a stream failed, orange when one is struggling or a recording stopped by itself, green when anything is on.</summary>
    public static ChipTint Tint(StreamingController streaming, RecordingController recording)
    {
        switch (streaming.WorstTint)
        {
            case StreamHealthRollup.Tint.Red: return ChipTint.Red;
            case StreamHealthRollup.Tint.Amber: return ChipTint.Orange;
        }
        if (recording.AnyStoppedAbnormally) return ChipTint.Orange;
        if (streaming.IsLive || recording.AnyRecording) return ChipTint.Green;
        if (streaming.StartNotice is not null) return ChipTint.Orange;
        return ChipTint.None;
    }

    /// <summary>
    /// The chip's caption, e.g. "LIVE 12:04 · REC 12:03", "RECONNECTING 0:41", "NOT STARTED"; null
    /// when nothing is happening. Refresh it every second while it isn't null.
    /// </summary>
    public static string? Caption(StreamingController streaming, RecordingController recording, DateTimeOffset now)
    {
        var parts = new List<string>();
        if (streaming.Live is { } live)
        {
            var total = (int)Math.Max(0, (now - live.StartedAt).TotalSeconds);
            switch (streaming.WorstTint)
            {
                case StreamHealthRollup.Tint.Amber:
                    parts.Add($"{streaming.CaptionWord} {Clock(total)}");
                    break;
                case StreamHealthRollup.Tint.Red:
                    parts.Add("STREAM FAILED");
                    break;
                default:
                    if (live.MediaDurationSeconds is { } duration)
                        parts.Add($"REPLAY {Clock(Math.Max(0, (int)duration - total))}");
                    else
                        parts.Add($"LIVE {Clock(total)}");
                    break;
            }
        }
        var recordings = recording.Sessions.Where(s => s.IsRecording).ToList();
        if (recordings.Count > 0)
        {
            parts.Add($"REC {Clock((int)recordings[0].Status.ElapsedSeconds)}");
            if (recordings.Count > 1) parts[0] += $" +{recordings.Count - 1}";
        }
        if (recording.AnyStoppedAbnormally) parts.Add("stopped");
        if (parts.Count == 0 && streaming.StartNotice is not null) parts.Add("NOT STARTED");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static string Clock(int seconds) => string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}");

    /// <summary>One destination's line in the status menu, e.g. "YouTube — Reconnecting (try 2) · 14 dropped".</summary>
    public static string HealthLine(StreamSession session)
    {
        var status = session.Status;
        var state = status.State.Kind switch
        {
            StreamStateKind.Publishing => "Live",
            StreamStateKind.Connecting => "Connecting…",
            StreamStateKind.Reconnecting => $"Reconnecting (try {status.State.Attempt})",
            StreamStateKind.Failed => $"Failed — {status.State.Reason}",
            _ => "Stopped",
        };
        var parts = new List<string> { $"{session.Destination.Name} — {state}" };
        if (status.FramesDropped > 0) parts.Add($"{status.FramesDropped} dropped");
        return string.Join(" · ", parts);
    }

    /// <summary>A recording's line, e.g. "Sunday — Recording · 41.2 GB free · 3 dropped".</summary>
    public static string RecordingHealthLine(RecordingSession session)
    {
        var status = session.Status;
        var parts = new List<string> { $"{session.TargetName} — Recording" };
        if (status.FreeDiskBytes != long.MaxValue) parts.Add(FormatBytes(status.FreeDiskBytes) + " free");
        if (status.DroppedFrames > 0) parts.Add($"{status.DroppedFrames} dropped");
        return string.Join(" · ", parts);
    }

    /// <summary>File sizes in decimal units, like the Mac's ByteCountFormatter (.file): "980 KB", "4.7 GB".</summary>
    public static string FormatBytes(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }
        var format = unit <= 1 || value >= 100 ? "0" : value >= 10 ? "0.#" : "0.##";
        return value.ToString(format, CultureInfo.CurrentCulture) + " " + units[unit];
    }
}
