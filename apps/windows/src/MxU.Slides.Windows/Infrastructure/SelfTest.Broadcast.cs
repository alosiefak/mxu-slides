using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MxU.Slides.Broadcast.Control;
using MxU.Slides.Broadcast.Library;
using MxU.Slides.Broadcast.Media;
using MxU.Slides.Broadcast.Recording;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Outputs;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Rendering;
using MxU.Slides.Windows.ViewModels;
using MxU.Slides.Windows.Views;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>
/// The Phase 4 broadcast checks: recording one screen (the stage display) instead of the
/// program, recording the Music tab's sound, 60 fps, filing recordings into the media library,
/// replaying the newest recording to a stream, and pictures of the Stream &amp; Record window.
/// Recordings go into the sandbox library; nothing is written to the Videos folder.
/// </summary>
public sealed partial class SelfTest
{
    private async Task BroadcastExtrasCheck()
    {
        if (model.Program is null || model.Outputs is null)
        {
            Skip("broadcast extras", "no program feed");
            return;
        }
        model.Settings.RecordingsFolder = Path.Combine(Path.GetTempPath(), "MxU Slides self-test recordings");
        var made = new List<StreamRecordPreset>();
        try
        {
            if (!await StageRecordingCheck(made)) return;
            await MusicRecordingCheck(made);
            await SixtyFpsRecordingCheck(made);
            await ReplayCheck(made);
            await BroadcastWindowPictures(made);
        }
        finally
        {
            model.StopBroadcast(null);
            model.ClearAllCommand.Execute(null);
            model.ClearAudioCommand.Execute(null);
            foreach (var preset in made) model.Library.Delete<StreamRecordPreset>(preset.Id);
            foreach (var destination in model.Library.All<StreamDestination>().Where(d => d.Name.StartsWith("Self-test", StringComparison.Ordinal)).ToList())
                StreamDestinationLibrary.DeleteDestination(model.Library, model.Broadcast.Keys, destination.Id);
        }
    }

    /// <summary>Records the stage screen while a song is live and checks the file shows the stage display, not the audience program.</summary>
    private async Task<bool> StageRecordingCheck(List<StreamRecordPreset> made)
    {
        var config = model.Outputs!.Config;
        if (config.Screens.FirstOrDefault(s => config.Role(s.Id) == ScreenRole.Confidence) is not { } stageScreen)
        {
            Skip("recording the stage screen", "there's no stage screen in the setup");
            return true;
        }
        var preset = Remember(made, new StreamRecordPreset
        {
            Name = "Self-test stage recording", PresetKind = StreamPresetKind.RecordOnly,
            Width = 1280, Height = 720, FrameRate = 30, VideoBitrateKbps = 4000, CanvasScreenId = SwiftUuid.Format(stageScreen.Id),
        });
        if (model.ServiceItems.FirstOrDefault(r => r.Name == "Amazing Grace") is { } song)
        {
            model.SelectedServiceItem = song;
            model.GoLiveCommand.Execute(model.Slides.ElementAtOrDefault(1));
        }
        var problem = model.StartBroadcast(preset);
        if (problem is not null && (problem.Contains("encoder", StringComparison.OrdinalIgnoreCase) || problem.Contains("Media Foundation", StringComparison.OrdinalIgnoreCase)))
        {
            Skip("broadcast extras", problem);
            return false;
        }
        var session = model.Broadcast.Recording.Sessions.LastOrDefault(s => s.PresetId == preset.Id);
        Check("a preset records the stage screen instead of the program", problem is null && session?.TargetId == preset.CanvasScreenId);
        Check("the stage screen gets its own picture feed while it's recorded", model.ScreenFeeds?.RunningCount >= 1);
        if (model.ScreenFeeds?.Problem is { } cannot && RenderCapability.Tier >> 16 == 0)
        {
            Skip("the stage recording's pictures", $"no graphics card: {cannot}");
            model.StopBroadcast(preset.Id);
            return true;
        }

        var program = new CountingSink();
        model.Program!.Add(program);
        await Delay(2500);
        model.Program.Remove(program);
        var stageNow = await RenderAsync(model.StageScene(), 1280, 720);
        model.StopBroadcast(preset.Id);
        await WaitUntilStopped(session);
        Check("the stage screen's feed closes when the recording stops", model.ScreenFeeds?.RunningCount == 0);

        var file = await RecordedFileAsync(session);
        var probe = file is null ? null : MediaFileProbe.Read(file);
        report.AppendLine($"      stage recording: {file} ({(file is null ? 0 : new FileInfo(file).Length)} bytes, {probe?.Width}x{probe?.Height}, {probe?.DurationSeconds:0.0}s)");
        Check("the stage recording is a 1280×720 MP4 that plays", probe is { IsPlayable: true, Width: 1280, Height: 720, DurationSeconds: > 1.5 });
        if (file is null || await FrameFromAsync(file, 30) is not { } frame || program.Last is not { } programPixels) return true;
        Write(BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Pbgra32, null, frame.Pixels, frame.Width * 4), "21-stage-recording-frame");
        Write(BitmapSource.Create(program.Width, program.Height, 96, 96, PixelFormats.Pbgra32, null, programPixels, program.Width * 4), "21-program-meanwhile");
        var toStage = Difference(frame.Pixels, frame.Width, frame.Height, stageNow, 1280, 720);
        var toProgram = Difference(frame.Pixels, frame.Width, frame.Height, programPixels, program.Width, program.Height);
        report.AppendLine($"      stage recording differs from the stage display by {toStage:0.0} and from the program by {toProgram:0.0} (0–255)");
        Check("the stage recording shows the stage display, not the audience program", toStage < toProgram && toStage < 25);
        return true;
    }

    /// <summary>Plays a tone in the Music tab and records the main mix, then the program audio; each file must have audible sound.</summary>
    private async Task MusicRecordingCheck(List<StreamRecordPreset> made)
    {
        var tonePath = Path.Combine(Path.GetTempPath(), $"MxU self-test broadcast tone {Guid.NewGuid():N}.wav");
        try
        {
            WriteTone(tonePath, seconds: 20);
            model.ImportAudioFiles([tonePath]);
            var tile = model.AudioTiles.FirstOrDefault(t => t.Item is not null && t.Name.StartsWith("MxU self-test broadcast tone", StringComparison.Ordinal));
            if (tile is null)
            {
                Check("a tone imports for the sound checks", false);
                return;
            }
            model.Bin = BinTab.Music;
            model.AudioTileClickedCommand.Execute(tile);
            await Delay(600);

            var mix = Remember(made, new StreamRecordPreset
            {
                Name = "Self-test music recording", PresetKind = StreamPresetKind.RecordOnly, Width = 640, Height = 360, FrameRate = 30,
                VideoBitrateKbps = 1500, AudioMixId = Audio.Control.AudioRouting.MainKey,
            });
            var (mixPeak, mixFile) = await RecordSoundAsync(mix);
            report.AppendLine($"      main-mix recording: {mixFile} peak {mixPeak:0.000}");
            Check("a recording of the main mix has the music in it (not silent)", mixPeak > 0.01);

            if (Audio.AudioDeviceList.DefaultOutputDevice() is null)
            {
                Skip("recording the program audio", "this PC has no sound output, so Windows has nothing to capture");
            }
            else
            {
                var programAudio = Remember(made, new StreamRecordPreset
                {
                    Name = "Self-test program audio recording", PresetKind = StreamPresetKind.RecordOnly, Width = 640, Height = 360, FrameRate = 30,
                    VideoBitrateKbps = 1500,
                });
                var (programPeak, programFile) = await RecordSoundAsync(programAudio);
                report.AppendLine($"      program-audio recording: {programFile} peak {programPeak:0.000}");
                Check("a recording of the program audio has the music in it (not silent)", programPeak > 0.01);
            }
        }
        finally
        {
            model.ClearAudioCommand.Execute(null);
            model.Bin = BinTab.Media;
            try { File.Delete(tonePath); }
            catch (IOException) { }
        }
    }

    /// <summary>Records a preset for three seconds; returns the loudest sample in the finished file (0 when silent or missing).</summary>
    private async Task<(float Peak, string? File)> RecordSoundAsync(StreamRecordPreset preset)
    {
        if (model.StartBroadcast(preset) is { } problem)
        {
            report.AppendLine($"      {preset.Name}: {problem}");
            return (0, null);
        }
        var session = model.Broadcast.Recording.Sessions.LastOrDefault(s => s.PresetId == preset.Id);
        await Delay(3000);
        model.StopBroadcast(preset.Id);
        await WaitUntilStopped(session);
        var file = await RecordedFileAsync(session);
        if (file is null) return (0, null);
        var peak = 0f;
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new MediaFileSource(file, null, null, _ => { }, buffer =>
        {
            foreach (var value in buffer.Samples.Span) peak = Math.Max(peak, Math.Abs(value));
        }, _ => ended.TrySetResult());
        source.Start();
        await Task.WhenAny(ended.Task, Task.Delay(8000));
        source.Stop();
        return (peak, file);
    }

    /// <summary>A 60 fps preset records the program at 60 pictures a second (through a screen feed of the program).</summary>
    private async Task SixtyFpsRecordingCheck(List<StreamRecordPreset> made)
    {
        var preset = Remember(made, new StreamRecordPreset
        {
            Name = "Self-test 60 fps recording", PresetKind = StreamPresetKind.RecordOnly, Width = 1280, Height = 720, FrameRate = 60, VideoBitrateKbps = 6000,
        });
        if (model.StartBroadcast(preset) is { } problem)
        {
            Check($"a 60 fps preset records ({problem})", false);
            return;
        }
        var session = model.Broadcast.Recording.Sessions.LastOrDefault(s => s.PresetId == preset.Id);
        await Delay(3000);
        var status = session?.Status;
        model.StopBroadcast(preset.Id);
        await WaitUntilStopped(session);
        var fps = status is { ElapsedSeconds: > 0 } s ? s.AppendedFrames / s.ElapsedSeconds : 0;
        report.AppendLine($"      60 fps recording: {fps:0.0} pictures a second, {status?.DroppedFrames} dropped");
        Check("a 60 fps recording gets about 60 pictures a second", fps >= 50);
        _ = await RecordedFileAsync(session);
    }

    /// <summary>A stream preset set to "the newest recording" replays the stage recording, decoding it while it streams.</summary>
    private async Task ReplayCheck(List<StreamRecordPreset> made)
    {
        var destination = new StreamDestination { Name = "Self-test (nowhere)", Url = "rtmp://127.0.0.1:1/live", Transport = StreamTransport.Rtmp, StreamKey = "self-test-key" };
        StreamDestinationLibrary.SaveDestination(model.Library, model.Broadcast.Keys, destination);
        var preset = Remember(made, new StreamRecordPreset
        {
            Name = "Self-test replay", PresetKind = StreamPresetKind.Stream, SourceKind = StreamSourceKind.LatestRecording,
            Width = 640, Height = 360, FrameRate = 30, VideoBitrateKbps = 1500, DestinationIds = [destination.Id], RecordWhileStreaming = true,
        });
        var preview = model.Broadcast.CapturePreview(preset);
        report.AppendLine($"      replay preview: {preview?.Text}");
        Check("a replay preset names the newest recording it will play", preview is { IsProblem: false } && preview.Text.StartsWith("Restreams “Self-test", StringComparison.Ordinal));
        var problem = model.StartBroadcast(preset);
        var live = model.Broadcast.Streaming.Live;
        Check("a stream preset replays the newest recording", problem is null && live?.FileSource is not null && live.MediaDurationSeconds > 1);
        Check("replaying doesn't record the replay again", model.Broadcast.Recording.Sessions.All(s => s.PresetId != preset.Id));
        // Up to 20 seconds: opening the file and starting Windows' decoders can take a while on a slow PC.
        await WhenShown(() => live?.FileSource?.FramesDelivered > 20, 20);
        report.AppendLine($"      replay: {live?.FileSource?.FramesDelivered} pictures decoded, {live?.FileSource?.FramesDropped} dropped, toolbar \"{model.BroadcastButtonText}\"");
        Check("the replayed file is decoded as it streams", live?.FileSource?.FramesDelivered > 20);
        model.StopBroadcast(preset.Id);
        Check("stopping the replay ends the stream", model.Broadcast.Streaming.Live is null);
    }

    /// <summary>Pictures of the Stream &amp; Record window showing a screen source, a sound input and a replay.</summary>
    private async Task BroadcastWindowPictures(List<StreamRecordPreset> made)
    {
        var stage = made.FirstOrDefault(p => p.CanvasScreenId is not null);
        var music = made.FirstOrDefault(p => p.AudioMixId is not null);
        var replay = made.FirstOrDefault(p => p.SourceKind == StreamSourceKind.LatestRecording);
        BroadcastWindow? broadcastWindow = null;
        try
        {
            broadcastWindow = new BroadcastWindow(model) { Owner = Application.Current.MainWindow, Height = 1150 };
            broadcastWindow.Show();
            var shots = new (StreamRecordPreset? Preset, string Name)[] { (stage, "22-stream-record-stage"), (music, "22-stream-record-sound"), (replay, "22-stream-record-replay") };
            foreach (var (preset, name) in shots)
            {
                if (preset is null) continue;
                broadcastWindow.Select(preset.Id);
                await Delay(400);
                Save(broadcastWindow, name);
            }
            Check("the Stream & Record window shows the new picture, sound and recording settings", broadcastWindow.IsVisible);
        }
        finally
        {
            broadcastWindow?.Close();
        }
    }

    /// <summary>Where a finished recording ended up: its library file once filed (waiting up to 15 s), else where it was written.</summary>
    private async Task<string?> RecordedFileAsync(RecordingSession? session)
    {
        if (session is null) return null;
        var name = Path.GetFileNameWithoutExtension(session.FilePath);
        for (var i = 0; i < 150; i++)
        {
            if (session.LibraryFolder is null && File.Exists(session.FilePath)) return session.FilePath;
            if (model.Library.All<MediaItem>().FirstOrDefault(m => m.RecordedAt is not null && m.Name == name) is { } item
                && model.Library.BlobPath(item) is { } blob)
            {
                // The library files it on a background thread; the Media tab's list follows on the UI thread a moment later.
                await WhenShown(() => model.MediaItems.Any(m => m.Item.Id == item.Id), 10);
                Check($"a finished recording appears in the Media tab ({item.Folder}, recorded by its preset)",
                    model.MediaItems.Any(m => m.Item.Id == item.Id) && item.RecordedByPresetId is { Length: > 0 } by && by == session.PresetId);
                return blob;
            }
            await Delay(100);
        }
        return File.Exists(session.FilePath) ? session.FilePath : null;
    }

    private static async Task WaitUntilStopped(RecordingSession? session)
    {
        for (var i = 0; i < 100 && session?.Status.State.Kind is RecorderStateKind.Recording or RecorderStateKind.Finishing; i++)
            await Delay(100);
    }

    /// <summary>Saves a test preset in the (sandbox) library, so the window and "recorded by" can find it; it's deleted at the end.</summary>
    private StreamRecordPreset Remember(List<StreamRecordPreset> made, StreamRecordPreset preset)
    {
        model.Library.Save(preset);
        made.Add(preset);
        return preset;
    }

    /// <summary>Decodes a recording and returns its picture number <paramref name="index"/> (or the last, if it's shorter).</summary>
    private static async Task<(byte[] Pixels, int Width, int Height)?> FrameFromAsync(string path, int index)
    {
        (byte[] Pixels, int Width, int Height)? latest = null;
        var count = 0;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new MediaFileSource(path, null, null, frame =>
        {
            latest = (frame.Pixels.ToArray(), frame.Width, frame.Height);
            if (count++ >= index) done.TrySetResult();
        }, _ => { }, _ => done.TrySetResult());
        source.Start();
        await Task.WhenAny(done.Task, Task.Delay(10_000));
        source.Stop();
        return latest;
    }

    /// <summary>Draws a scene offscreen the way an output would, as BGRA pixels.</summary>
    private async Task<byte[]> RenderAsync(RenderScene scene, int width, int height)
    {
        var view = new SceneView { Media = model.Media, PlaysVideo = false, Width = width, Height = height };
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.Present(scene, SceneTransition.Cut);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    /// <summary>How different two pictures look (average brightness difference over a 32×18 grid, 0–255), whatever their sizes.</summary>
    private static double Difference(byte[] a, int aWidth, int aHeight, byte[] b, int bWidth, int bHeight)
    {
        double total = 0;
        const int columns = 32, rows = 18;
        for (var row = 0; row < rows; row++)
            for (var column = 0; column < columns; column++)
                total += Math.Abs(Brightness(a, aWidth, aHeight, column, row) - Brightness(b, bWidth, bHeight, column, row));
        return total / (columns * rows);

        static double Brightness(byte[] pixels, int width, int height, int column, int row)
        {
            double sum = 0;
            var count = 0;
            for (var y = row * height / rows; y < (row + 1) * height / rows; y += 4)
                for (var x = column * width / columns; x < (column + 1) * width / columns; x += 4)
                {
                    var i = (y * width + x) * 4;
                    sum += 0.114 * pixels[i] + 0.587 * pixels[i + 1] + 0.299 * pixels[i + 2];
                    count++;
                }
            return count == 0 ? 0 : sum / count;
        }
    }

    private static void WriteTone(string path, double seconds)
    {
        const int rate = 48000;
        var frames = (int)(rate * seconds);
        using var file = new BinaryWriter(File.Create(path));
        file.Write("RIFF"u8);
        file.Write(36 + frames * 4);
        file.Write("WAVEfmt "u8);
        file.Write(16);
        file.Write((short)1);
        file.Write((short)2);
        file.Write(rate);
        file.Write(rate * 4);
        file.Write((short)4);
        file.Write((short)16);
        file.Write("data"u8);
        file.Write(frames * 4);
        for (var i = 0; i < frames; i++)
        {
            var sample = (short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 6000);
            file.Write(sample);
            file.Write(sample);
        }
    }
}
