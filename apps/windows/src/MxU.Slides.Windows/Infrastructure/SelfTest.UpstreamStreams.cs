using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MxU.Slides.Broadcast;
using MxU.Slides.Broadcast.Control;
using MxU.Slides.Broadcast.Library;
using MxU.Slides.Broadcast.Rtmp;
using MxU.Slides.Broadcast.Streaming;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.Views;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>
/// The upstream port's stream checks (MxU's releases of autumn 2026): the Adaptive / Constant
/// bitrate mode in the Stream &amp; Record window, a stream to a loopback RTMP server showing its
/// network line, the bitrate stepping down on a slow upload, a stalled upload reconnecting
/// (and saying why), and a recording noting a blackout with its time in the file.
/// Called from the UpstreamCore section.
/// </summary>
public sealed partial class SelfTest
{
    private async Task UpstreamStreamsChecks()
    {
        if (model.Program is null || model.Outputs is null)
        {
            Skip("streams over a slow or stalled upload", "no program feed");
            return;
        }
        model.Settings.RecordingsFolder = Path.Combine(Path.GetTempPath(), "MxU Slides self-test recordings");
        await using var server = new LoopbackRtmpServer();
        var destination = new StreamDestination { Name = "Self-test loopback", Url = server.Url, Transport = StreamTransport.Rtmp, StreamKey = "self-test-key" };
        StreamDestinationLibrary.SaveDestination(model.Library, model.Broadcast.Keys, destination);
        var preset = new StreamRecordPreset
        {
            Name = "Self-test slow upload", PresetKind = StreamPresetKind.Stream, Width = 1280, Height = 720, FrameRate = 30,
            VideoBitrateKbps = 3000, AdaptBitrate = true, MinBitrateKbps = 2000, DestinationIds = [destination.Id],
        };
        model.Library.Save(preset);
        var record = new StreamRecordPreset
        {
            Name = "Self-test blackout recording", PresetKind = StreamPresetKind.RecordOnly, Width = 640, Height = 360, FrameRate = 30, VideoBitrateKbps = 1500,
        };
        model.Library.Save(record);
        BroadcastWindow? window = null;
        try
        {
            window = new BroadcastWindow(model) { Owner = Application.Current.MainWindow, Height = 1150 };
            window.Show();
            window.Select(preset.Id);
            await Delay(400);
            BitrateModeRoundTrips(window, preset.Id);
            Save(window, "upstream-streams-bitrate-mode");

            await NetworkLineCheck(window, preset, server);
            await BlackoutCheck(record);
        }
        finally
        {
            model.StopBroadcast(null);
            window?.Close();
            model.ClearAllCommand.Execute(null);
            model.Library.Delete<StreamRecordPreset>(preset.Id);
            model.Library.Delete<StreamRecordPreset>(record.Id);
            StreamDestinationLibrary.DeleteDestination(model.Library, model.Broadcast.Keys, destination.Id);
        }
    }

    /// <summary>The window's Bitrate mode picker writes the shared preset fields the Mac reads.</summary>
    private void BitrateModeRoundTrips(BroadcastWindow window, string presetId)
    {
        var mode = Descendants<ComboBox>(window).FirstOrDefault(c => c.Items.Count == 2 && c.Items[0]?.ToString()?.Contains("Adaptive", StringComparison.Ordinal) == true);
        Check("the Stream & Record window offers Adaptive or Constant bitrate for a stream", mode is not null && mode.SelectedIndex == 0);
        if (mode is null) return;
        mode.SelectedIndex = 1;
        Check("choosing Constant is saved in the preset (adaptBitrate false)", model.Library.Get<StreamRecordPreset>(presetId)?.AdaptBitrate == false);
        mode.SelectedIndex = 0;
        Check("choosing Adaptive again is saved", model.Library.Get<StreamRecordPreset>(presetId)?.AdaptBitrate == true);
        var floor = Descendants<ComboBox>(window).FirstOrDefault(c => c.Items.Count > 0 && c.Items[0]?.ToString()?.Contains("Mbps", StringComparison.Ordinal) == true);
        Check("Adaptive shows its lowest bitrate (2 Mbps)", floor?.SelectedItem?.ToString()?.Contains("2 Mbps", StringComparison.Ordinal) == true && floor.IsVisible);
    }

    /// <summary>A live stream's line shows the network; a slow upload lowers the bitrate; a stalled one reconnects.</summary>
    private async Task NetworkLineCheck(BroadcastWindow window, StreamRecordPreset preset, LoopbackRtmpServer server)
    {
        model.GoLiveCommand.Execute(model.Slides.ElementAtOrDefault(1));
        var problem = model.StartBroadcast(preset);
        Check("a stream to the loopback server starts", problem is null && model.Broadcast.Streaming.Live is not null);
        if (model.Broadcast.Streaming.Live?.Sessions.FirstOrDefault() is not { } session) return;
        Check("the stream's floor follows the preset (Adaptive, 2 Mbps)", session.Video.AdaptiveFloorKbps == 2000);
        await WhenShown(() => session.Status.UploadKbps is not null, 20);
        var line = BroadcastStatus.HealthLine(session);
        report.AppendLine($"      live: {line}");
        Check("a live stream's status line shows the network and what it sends",
            line.Contains("Network ", StringComparison.Ordinal) && line.Contains("sending", StringComparison.Ordinal) && server.VideoMessages > 0);
        await Delay(1200);
        Save(window, "upstream-streams-network-line");

        // A slow upload: 20 KB a second gets through.
        server.ReadLimitBytesPerSecond = 20_000;
        var lowered = await WhenShown(() => session.Status.VideoKbps < 2700, 30);
        report.AppendLine($"      slow upload: {BroadcastStatus.HealthLine(session)}");
        if (lowered is null && session.Status.BacklogSeconds is null or < 0.5)
            Skip("Adaptive lowers the bitrate on a slow upload", "the program sent less than the slowed link carries (a still picture)");
        else
            Check("Adaptive lowers the bitrate on a slow upload, not below the preset's lowest", lowered is not null && session.Status.VideoKbps >= 2000);
        Save(window, "upstream-streams-slow-upload");

        // A stalled upload: nothing gets through. Once the PC's send buffer is full, ten silent
        // seconds make the stream reconnect on its own (and say why).
        server.ReadLimitBytesPerSecond = null;
        server.Paused = true;
        var connections = server.Connections;
        await WhenShown(() => session.Status.State.Kind == StreamStateKind.Reconnecting, 90);
        var dropped = session.Status.LastDrop;
        report.AppendLine($"      stalled upload: {BroadcastStatus.HealthLine(session)} (dropped: {dropped})");
        Save(window, "upstream-streams-stalled");
        Check("a stalled upload reconnects and says why", session.Status.State.Kind == StreamStateKind.Reconnecting && dropped?.Contains("stalled", StringComparison.Ordinal) == true);
        server.Paused = false;
        await WhenShown(() => server.Connections > connections && session.Status.State.Kind == StreamStateKind.Publishing, 30);
        Check("once the upload moves again the stream goes live again", server.Connections > connections && session.Status.State.Kind == StreamStateKind.Publishing);
        model.StopBroadcast(preset.Id);
    }

    /// <summary>A recording notes a blackout above the lower third, with its time in the file.</summary>
    private async Task BlackoutCheck(StreamRecordPreset preset)
    {
        var notes = new List<string>();
        void Heard(BroadcastLogEntry entry)
        {
            if (entry.Category == "recording.black") lock (notes) notes.Add(entry.Message);
        }
        BroadcastLog.Written += Heard;
        try
        {
            model.GoLiveCommand.Execute(model.Slides.ElementAtOrDefault(1));
            var problem = model.StartBroadcast(preset);
            Check("a recording starts for the blackout check", problem is null);
            await Delay(1000);
            model.ClearAllCommand.Execute(null);
            await Delay(1500);
            model.GoLiveCommand.Execute(model.Slides.ElementAtOrDefault(1));
            await WhenShown(() => { lock (notes) return notes.Count > 0; }, 10);
            string? note;
            lock (notes) note = notes.FirstOrDefault();
            report.AppendLine($"      blackout note: {note ?? "(none)"}");
            Check("a recording notes the blackout and when it was in the file",
                note is not null && note.Contains("black above the lower third at 0:0", StringComparison.Ordinal) && note.EndsWith("ms", StringComparison.Ordinal));
        }
        finally
        {
            BroadcastLog.Written -= Heard;
            model.StopBroadcast(preset.Id);
        }
    }
}
