using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MxU.Slides.Audio;
using MxU.Slides.Broadcast;
using MxU.Slides.Broadcast.Control;
using MxU.Slides.Broadcast.Library;
using MxU.Slides.Core.Media;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.Outputs;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// Recording and streaming the program. The broadcast engine (MxU.Slides.Broadcast, ported
/// from the Mac) takes the program feed's pictures (section 17 of HOW-IT-WORKS), encodes them
/// with Windows' own Media Foundation encoders, and sends them over RTMP/RTMPS or HLS, or
/// writes an MP4. The sound is <see cref="ProgramAudioTap"/>: everything this app plays
/// (videos, music) and nothing else on the PC. A stream/record preset says what to do.
/// </summary>
public sealed partial class MainViewModel
{
    private BroadcastController? broadcast;
    private ProgramAudioTap? audioTap;
    private ProgramAudioRelay? audioRelay;
    private DispatcherTimer? broadcastTicker;
    private static bool broadcastLogHooked;

    /// <summary>The toolbar chip's words ("REC 12:03", "LIVE 4:10 · REC 4:10"); empty when nothing runs.</summary>
    [ObservableProperty]
    public partial string BroadcastCaption { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BroadcastButtonText))]
    public partial bool IsBroadcasting { get; set; }

    /// <summary>The toolbar button: its status while anything runs, else what it opens.</summary>
    public string BroadcastButtonText => BroadcastCaption.Length > 0 ? BroadcastCaption : "Stream & Record";

    partial void OnBroadcastCaptionChanged(string value) => OnPropertyChanged(nameof(BroadcastButtonText));

    /// <summary>The chip's colour: None, Green, Orange or Red.</summary>
    [ObservableProperty]
    public partial BroadcastStatus.ChipTint BroadcastTint { get; set; }

    /// <summary>Anything about recording or streaming changed (the Stream &amp; Record window listens).</summary>
    public event Action? BroadcastChanged;

    /// <summary>The recording and streaming engine (made the first time it's needed, on the UI thread).</summary>
    public BroadcastController Broadcast => broadcast ??= CreateBroadcast();

    private BroadcastController CreateBroadcast()
    {
        if (!broadcastLogHooked)
        {
            broadcastLogHooked = true;
            BroadcastLog.Written += entry =>
            {
                if (entry.Level == BroadcastLogLevel.Info) Log.Info($"Broadcast: {entry.Category} {entry.Message}");
                else Log.Warn($"Broadcast: {entry.Category} {entry.Message}");
            };
        }
        var created = new BroadcastController(Library, new StreamKeyVault(StreamKeyVault.DefaultFolder),
            ProgramSource.From(sink => Program?.Add(sink), sink => Program?.Remove(sink)),
            Settings.RecordingsFolder);
        foreach (var line in created.Prepare()) Log.Info($"Broadcast: {line}");
        created.Streaming.LiveChanged += UpdateBroadcastStatus;
        created.Streaming.StatusChanged += _ => UpdateBroadcastStatus();
        created.Recording.SessionsChanged += UpdateBroadcastStatus;
        created.Recording.StatusChanged += _ => UpdateBroadcastStatus();
        return created;
    }

    /// <summary>The stream/record presets, by name.</summary>
    public List<StreamRecordPreset> StreamPresets() =>
        [.. Library.All<StreamRecordPreset>().OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>A preset to start from: record the program in 1080p.</summary>
    public StreamRecordPreset EnsureStreamPreset()
    {
        if (StreamPresets().FirstOrDefault() is { } existing) return existing;
        var preset = new StreamRecordPreset
        {
            Name = "Record the service", PresetKind = StreamPresetKind.RecordOnly,
            Width = 1920, Height = 1080, FrameRate = 30, VideoBitrateKbps = 8000,
        };
        Library.Save(preset);
        return preset;
    }

    /// <summary>Starts the recording or stream a preset describes (slide actions, combos and the toolbar).</summary>
    private void StartBroadcast(string presetId)
    {
        if (Library.Get<StreamRecordPreset>(presetId) is { } preset) StartBroadcast(preset);
        else Log.Warn($"Broadcast: no stream/record preset {presetId}");
    }

    /// <summary>Starts a preset; returns why it didn't start, if it didn't.</summary>
    public string? StartBroadcast(StreamRecordPreset preset)
    {
        var destinations = Broadcast.ResolvedDestinations(preset);
        if (StreamDestinationReadiness.StartRefusal(preset.ResolvedKind(), destinations) is { } refusal)
        {
            Notice = refusal;
            return refusal;
        }
        Broadcast.StartCapture(preset);
        UpdateBroadcastStatus();
        if (Broadcast.Streaming.StartNotice is { } notice)
        {
            Notice = notice;
            return notice;
        }
        Notice = preset.IsRecordOnly() ? $"Recording \"{preset.Name}\"" : $"Going live: {preset.Name}";
        return null;
    }

    /// <summary>Stops one preset's recording or stream, or (null) all of them.</summary>
    public void StopBroadcast(string? presetId)
    {
        if (broadcast is not { } b) return;
        if (presetId is null) b.EndAllCapture();
        else b.EndAll(presetId);
        UpdateBroadcastStatus();
    }

    /// <summary>On quit: stops everything and waits (up to 10 seconds) for recordings to finish their files.</summary>
    public async Task EndBroadcastsAsync()
    {
        if (broadcast is not { } b) return;
        var recordings = b.Recording.Sessions.Where(s => s.IsRecording).ToList();
        b.Streaming.EndStream();
        await Task.WhenAny(Task.WhenAll(recordings.Select(b.Recording.StopAsync)), Task.Delay(TimeSpan.FromSeconds(10)));
        b.Dispose();
        broadcast = null;
        broadcastTicker?.Stop();
        broadcastTicker = null;
        IsBroadcasting = false;
        BroadcastCaption = "";
    }

    private void UpdateBroadcastStatus()
    {
        if (broadcast is not { } b) return;
        BroadcastCaption = BroadcastStatus.Caption(b.Streaming, b.Recording, DateTimeOffset.Now) ?? "";
        IsBroadcasting = b.Streaming.IsLive || b.Recording.AnyRecording;
        BroadcastTint = BroadcastStatus.Tint(b.Streaming, b.Recording);
        // The chip counts up every second while anything runs.
        if (BroadcastCaption.Length > 0 && broadcastTicker is null)
        {
            broadcastTicker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            broadcastTicker.Tick += (_, _) => UpdateBroadcastStatus();
            broadcastTicker.Start();
        }
        else if (BroadcastCaption.Length == 0 && broadcastTicker is not null)
        {
            broadcastTicker.Stop();
            broadcastTicker = null;
        }
        BroadcastChanged?.Invoke();
    }

    /// <summary>
    /// Program sound follows the program pictures: while anything takes the program feed (a
    /// recording, a stream), the audio tap captures this app's sound into it as well.
    /// </summary>
    private void UpdateProgramAudio()
    {
        if (program is not { } feed) return;
        if (feed.IsActive && audioRelay is null)
        {
            audioTap ??= CreateAudioTap();
            audioRelay = new ProgramAudioRelay(feed);
            audioTap.AddSink(audioRelay);
        }
        else if (!feed.IsActive && audioRelay is not null)
        {
            audioTap?.RemoveSink(audioRelay);
            audioRelay = null;
        }
    }

    private static ProgramAudioTap CreateAudioTap()
    {
        var tap = new ProgramAudioTap();
        tap.StatusChanged += status => Log.Info($"Program audio: {status.State} {status.Message}");
        return tap;
    }

    /// <summary>Passes the tap's sound to the program feed, which hands it to every destination.</summary>
    private sealed class ProgramAudioRelay(ProgramFeed feed) : IProgramSink
    {
        public void WriteVideo(VideoFrame frame) { }

        public void WriteAudio(AudioBuffer buffer) => feed.WriteAudio(buffer);
    }
}
