using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MxU.Slides.Audio;
using MxU.Slides.Broadcast;
using MxU.Slides.Broadcast.Control;
using MxU.Slides.Broadcast.Library;
using MxU.Slides.Core.Media;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Outputs;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.Outputs;
using MxU.Slides.Windows.Rendering;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// Recording and streaming. The broadcast engine (MxU.Slides.Broadcast, ported from the Mac)
/// encodes pictures and sound with Windows' own Media Foundation encoders, and sends them over
/// RTMP/RTMPS or HLS, or writes an MP4. This file gives it what to record:
/// <list type="bullet">
/// <item><b>Pictures</b>: the audience program feed (section 17 of HOW-IT-WORKS), or one screen
/// (the stage display, a lower-thirds screen) through <see cref="ScreenFeeds"/>, which also
/// serves the program at 60 frames a second.</item>
/// <item><b>Sound</b>: what the preset asks for, opened by <see cref="SessionAudioFeed"/>: the
/// program audio (<see cref="ProgramAudioTap"/>: everything this app plays and nothing else on
/// the PC), the main mix (the Music tab), or a sound input such as the mixing desk, maybe with
/// the program added and a delay.</item>
/// <item><b>Files</b>: finished recordings are filed into the media library, so they appear in the
/// Media tab (and a preset can restream "the latest recording").</item>
/// </list>
/// A stream/record preset says what to do.
/// </summary>
public sealed partial class MainViewModel
{
    private BroadcastController? broadcast;
    private ProgramAudioTap? audioTap;
    private ScreenFeeds? screenFeeds;
    private AudioMixFeeds? mixFeeds;
    private AudioInputInventory? audioInputs;
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

    /// <summary>Pictures of single screens while one is being recorded or streamed (null until then). The outputs' scenes are handed to it in RenderOutputs and RenderStage.</summary>
    public ScreenFeeds? ScreenFeeds => screenFeeds;

    /// <summary>The named sound inputs on this PC (kept in audio-inputs.json beside the settings).</summary>
    public AudioInputInventory AudioInputs => audioInputs ??= new AudioInputInventory(Path.Combine(AppPaths.DataRoot, "audio-inputs.json"));

    private AudioMixFeeds MixFeeds => mixFeeds ??= new AudioMixFeeds(Audio);

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
        var sources = new CaptureSources(
            ProgramSource.From(sink => Program?.Add(sink), sink => Program?.Remove(sink)),
            Program?.FrameRate ?? 30)
        {
            Screens = (target, width, height, rate) => EnsureScreenFeeds()?.Source(target, width, height, rate),
            Audio = OpenSessionAudio,
        };
        var created = new BroadcastController(Library, new StreamKeyVault(StreamKeyVault.DefaultFolder), sources, Settings.RecordingsFolder);
        created.FileRecordingsIntoLibrary();
        foreach (var line in created.Prepare()) Log.Info($"Broadcast: {line}");
        created.Streaming.LiveChanged += UpdateBroadcastStatus;
        created.Streaming.StatusChanged += _ => UpdateBroadcastStatus();
        created.Recording.SessionsChanged += UpdateBroadcastStatus;
        created.Recording.StatusChanged += _ => UpdateBroadcastStatus();
        created.RecordingFiled += item => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Notice = $"Recording saved: \"{item.Name}\" is in the Media tab.";
            BroadcastChanged?.Invoke();
        });
        return created;
    }

    private ScreenFeeds? EnsureScreenFeeds()
    {
        if (screenFeeds is not null) return screenFeeds;
        if (Outputs is not { } outputs) return null;
        screenFeeds = new ScreenFeeds(outputs, Media);
        // Give it what's live now; from here on RenderOutputs keeps it up to date.
        RenderOutputs(SceneTransition.Cut);
        return screenFeeds;
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
        // A few seconds to file them into the library; anything unfinished is filed at the next launch.
        if (recordings.Count > 0)
            await Task.Factory.StartNew(() => b.WaitForFiling(TimeSpan.FromSeconds(5)), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        b.Dispose();
        broadcast = null;
        screenFeeds?.Dispose();
        screenFeeds = null;
        mixFeeds?.Dispose();
        mixFeeds = null;
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
    /// Called when the program feed starts or stops. Sound used to travel with the program
    /// pictures; now every recording and stream opens the sound its preset asks for
    /// (<see cref="OpenSessionAudio"/>), so there is nothing to connect here.
    /// </summary>
    private void UpdateProgramAudio()
    {
    }

    // MARK: Sound for recordings and streams

    /// <summary>Opens the sound a recording or stream asked for (see <see cref="SessionAudioFeed"/>).</summary>
    private IDisposable OpenSessionAudio(SessionAudioRequest request, IProgramSink sink)
    {
        var choice = new SessionAudioChoice(
            MixId: request.Source.Kind == StreamAudioSourceKind.Mix ? request.Source.Id : null,
            InputId: request.Source.Kind == StreamAudioSourceKind.Input ? request.Source.Id : null,
            InputUid: request.InputUid,
            IncludeProgram: request.IncludesProgram,
            DelayMilliseconds: request.DelayMs);
        var feed = SessionAudioFeed.Open(choice, new SessionAudioBackends(this), (buffer, host) => sink.WriteAudio(buffer.ToAudioBuffer(host)));
        Log.Info($"Broadcast: sound is {feed.Description}");
        if (feed.FallbackReason is { } why)
        {
            Log.Warn($"Broadcast: {why}");
            Notice = $"Recording sound: {why}";
        }
        return feed;
    }

    private ProgramAudioTap ProgramTap => audioTap ??= CreateAudioTap();

    private static ProgramAudioTap CreateAudioTap()
    {
        var tap = new ProgramAudioTap();
        tap.StatusChanged += status => Log.Info($"Program audio: {status.State} {status.Message}");
        return tap;
    }

    /// <summary>Where session sound comes from on this PC: the program tap, the mix feeds and the sound inputs.</summary>
    private sealed class SessionAudioBackends(MainViewModel model) : ISessionAudioBackends
    {
        public IDisposable OpenProgram(Action<PcmBuffer, double> consumer)
        {
            var tap = model.ProgramTap;
            var token = tap.AddConsumer(consumer);
            return new Closer(() => tap.RemoveConsumer(token));
        }

        public IDisposable? OpenMix(string mixId, Action<PcmBuffer, double> consumer) => model.MixFeeds.Open(mixId, consumer);

        public IDisposable? OpenInput(string inputId, Action<PcmBuffer, double> consumer, out string? problem)
        {
            problem = null;
            if (model.AudioInputs.Source(inputId) is not { } source)
            {
                problem = "it isn't set up on this PC";
                return null;
            }
            return source.Open(consumer, out problem);
        }

        public IDisposable? OpenInputDevice(string uid, Action<PcmBuffer, double> consumer, out string? problem)
        {
            if (model.AudioInputs.ForUid(uid) is { } entry) return OpenInput(entry.Id, consumer, out problem);
            problem = null;
            if (AudioDeviceList.InputDevice(uid) is null)
            {
                problem = "that sound device isn't connected";
                return null;
            }
            return new AudioInputSource(uid, InputChannelSelection.Default).Open(consumer, out problem);
        }

        public string? InputName(string inputId) => model.AudioInputs.NameFor(inputId);
    }

    private sealed class Closer(Action close) : IDisposable
    {
        private int closed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref closed, 1) == 0) close();
        }
    }

    // MARK: For the Stream & Record window

    /// <summary>The screens a preset can record or stream (from Screens & Settings), by name.</summary>
    public IReadOnlyList<(string Id, string Name, bool IsStage)> BroadcastScreens() =>
        Outputs is not { } outputs
            ? []
            : [.. outputs.Config.Screens.Select(s => (SwiftUuid.Format(s.Id), $"{s.Name} ({s.Width}×{s.Height})", outputs.Config.Role(s.Id) == ScreenRole.Confidence))];

    /// <summary>A screen's name from its id, or null when it isn't in the setup.</summary>
    public string? BroadcastScreenName(string id) =>
        Outputs?.Config is { } config && SwiftUuid.Parse(id) is { } guid && config.Screen(guid) is { } screen ? screen.Name : null;

    /// <summary>"Program audio", "Mix · Main mix (music)", "Input · Mixing desk (channels 1–2)" — the Mac's AudioSourceMenuItems.title.</summary>
    public string AudioSourceTitle(StreamRecordPreset preset)
    {
        if (!string.IsNullOrEmpty(preset.AudioMixId))
            return "Mix · " + (AudioMixFeeds.NameOf(preset.AudioMixId) ?? "Missing");
        if (!string.IsNullOrEmpty(preset.AudioInputId))
            return "Input · " + (AudioInputs.Entry(preset.AudioInputId) is { } entry ? $"{entry.Name} ({AudioInputEntry.ChannelsLabel(entry.Channels)})" : "Missing input");
        if (!string.IsNullOrEmpty(preset.AudioInputUid))
            return "Input · " + (AudioInputs.ForUid(preset.AudioInputUid)?.Name ?? AudioDeviceList.InputDevice(preset.AudioInputUid)?.Name ?? "Missing input");
        return "Program audio";
    }

    /// <summary>The library's videos, newest first (for a preset that streams a media file).</summary>
    public IReadOnlyList<MediaItem> BroadcastVideos() =>
        [.. Library.All<MediaItem>().Where(m => m.MediaKind == MediaKind.Video).OrderByDescending(m => m.RecordedAt ?? m.ImportedAt ?? 0)];

    /// <summary>The media folders in the library, plus the default "Recordings".</summary>
    public IReadOnlyList<string> MediaFolders() =>
        [.. Library.All<MediaItem>().Select(m => m.Folder).OfType<string>().Append(RecordingLibrary.DefaultFolder)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.CurrentCultureIgnoreCase)];
}
