using MxU.Slides.Core.Model;
using MxU.Slides.Core.Stage;

namespace MxU.Slides.Core.Automation;

/// <summary>This computer's timecode settings (kept out of backups; see <see cref="TimecodeStation"/>).</summary>
public sealed record TimecodeSettings(
    TimecodeSource Source = TimecodeSource.Ltc,
    string? DeviceId = null,
    string? MidiSourceId = null,
    int Channel = 0,
    int OffsetFrames = 0,
    bool Follows = true,
    bool PlaysAudio = false);

/// <summary>What the follower needs from the app: the service, the stage display, and the live slide.</summary>
public interface ITimecodeHost
{
    /// <summary>The service timecode follows (the current one).</summary>
    Service? CurrentService { get; }

    /// <summary>The service's items as the run of show lists them (the Mac's runOfShow: visible items, in order).</summary>
    IReadOnlyList<ServiceItem> RunOfShow(Service service);

    Presentation? Presentation(string id);

    /// <summary>The timecode the stage display shows (ConfidenceInfo.Timecode).</summary>
    TimecodeDisplay? PublishedTimecode { get; set; }

    /// <summary>The service item of the live slide.</summary>
    string? LiveContextId { get; }

    /// <summary>Scrolls the presenter to the live slide (timecode moved to a new song).</summary>
    void RevealLive() { }
}

/// <summary>How the timecode status word is coloured.</summary>
public enum TimecodeStatusTone { Secondary, Red, Green }

/// <summary>Timecode in as the Local API reports it (the same shape as ApiTimecodeStatus).</summary>
public sealed record TimecodeStatus(
    bool HasInput, string Source, bool Follows, string State, string? Label = null, string? Rate = null, bool Reverse = false,
    int OffsetFrames = 0, string? ServiceItemId = null, TimecodeStatus.AnchorInfo? Anchor = null, TimecodeStatus.TimelineInfo? Timeline = null)
{
    public sealed record AnchorInfo(double Seconds, DateTimeOffset AnchoredAt, bool Running, bool Reverse = false);

    public sealed record TimelineInfo(string ServiceItemId, double Position, DateTimeOffset AnchoredAt, bool Running, double Length);
}

/// <summary>
/// Timecode in: reads the chosen LTC or MTC input 30 times a second, turns it into a
/// clock, shows it on the stage display, and (when Follow is on) drives the
/// <see cref="TimelinePlayer"/> through the service's timecode songs. It also notices an
/// input with sound but no timecode. Port of TimecodeFollower in Sources/TimecodeInput.swift.
/// Drive it from the UI thread.
/// </summary>
public sealed class TimecodeFollower
{
    /// <summary>The input chosen: none, an audio device (LTC) or a MIDI source (MTC).</summary>
    public abstract record Input
    {
        private Input() { }

        public sealed record None : Input;

        public sealed record Audio(string DeviceId) : Input;

        public sealed record Midi(string SourceId) : Input;
    }

    /// <summary>A service song timecode can follow, and what it plays.</summary>
    public sealed record FollowedSong(TimecodeFollow.Song Song, ServiceItem Item, Presentation Presentation, PresentationTimeline Timeline)
    {
        public string? ArrangementId => Item.ArrangementId;
    }

    private readonly ITimecodeHost host;
    private readonly ITimecodeInputs inputs;
    private readonly TimeProvider clock;
    private readonly SynchronizationContext? ui;

    private TimecodeSettings settings;
    private ITimecodeReader? reader;
    private ShowTimer? timer;
    private TimecodeClock timecodeClock = new();
    private TimecodeFollow follow = new();
    private TimecodeSignalCheck signalCheck = new();
    private string? revealPending;
    private bool audioRunning;

    /// <param name="timeline">The timeline player timecode drives (its <see cref="TimelinePlayer.Timecode"/> is set to this follower).</param>
    /// <param name="saved">This computer's settings from last time.</param>
    /// <param name="ui">Where ticks run; pass the UI thread's <see cref="SynchronizationContext.Current"/>.</param>
    public TimecodeFollower(
        ITimecodeHost host, ITimecodeInputs inputs, TimelinePlayer? timeline, TimecodeSettings? saved = null,
        TimeProvider? clock = null, SynchronizationContext? ui = null)
    {
        this.host = host;
        this.inputs = inputs;
        Timeline = timeline;
        settings = saved ?? new TimecodeSettings();
        this.clock = clock ?? TimeProvider.System;
        this.ui = ui;
        if (timeline is not null) timeline.Timecode = this;
    }

    public TimelinePlayer? Timeline { get; }

    /// <summary>Fires when <see cref="Settings"/> should be saved.</summary>
    public event Action? SettingsChanged;

    /// <summary>Fires when the readout (reading, level, status) changes.</summary>
    public event Action? Changed;

    /// <summary>The diagnostics trail: (crumb, detail).</summary>
    public event Action<string, string>? Noted;

    public TimecodeSettings Settings => settings;
    public TimecodeSource Source => settings.Source;
    public int Channel => settings.Channel;
    public int OffsetFrames => settings.OffsetFrames;
    public bool Follows => settings.Follows;
    public bool PlaysAudio => settings.PlaysAudio;

    /// <summary>"Not connected" when the input couldn't be opened.</summary>
    public string? Problem { get; private set; }

    /// <summary>The clock as last read (with the offset added).</summary>
    public TimecodeClock.Reading Reading { get; private set; } = new(TimecodeClock.State.NoInput);

    /// <summary>The input's peak level, falling off slowly, for the meter.</summary>
    public float Level { get; private set; }

    public bool IsLocked { get; private set; }

    /// <summary>Sound on the input that isn't timecode.</summary>
    public bool NoTimecode { get; private set; }

    public TimecodeRate? DetectedRate { get; private set; }

    /// <summary>The display last published to the stage (without the offset).</summary>
    public TimecodeDisplay? AnchoredDisplay { get; private set; }

    /// <summary>The service item timecode last put in.</summary>
    public string? SongItemId { get; private set; }

    private double HostNow => clock.GetTimestamp() / (double)clock.TimestampFrequency;

    /// <summary>The status word and its colour: Off, No timecode, Locked or Stopped.</summary>
    public static (string Word, TimecodeStatusTone Tone) StatusWord(TimecodeClock.Reading reading, bool follows, bool noTimecode)
    {
        if (!follows) return ("Off", TimecodeStatusTone.Secondary);
        if (noTimecode) return ("No timecode", TimecodeStatusTone.Red);
        return reading.State == TimecodeClock.State.Locked ? ("Locked", TimecodeStatusTone.Green) : ("Stopped", TimecodeStatusTone.Secondary);
    }

    /// <summary>The readout's label ("01:00:04:12", ";" before the frames at drop-frame), or dashes with nothing heard.</summary>
    public static string LabelText(TimecodeClock.Reading reading) =>
        reading.Label is { } label ? label.Text(dropFrame: reading.Rate == TimecodeRate.Fps2997DropFrame) : "--:--:--:--";

    /// <summary>"+2 frames", "-1 frames", "0 frames".</summary>
    public static string OffsetText(int frames) => $"{(frames > 0 ? "+" : "")}{frames} frames";

    public Input CurrentInput => settings.Source switch
    {
        TimecodeSource.Mtc => settings.MidiSourceId is { } source ? new Input.Midi(source) : new Input.None(),
        _ => settings.DeviceId is { } device ? new Input.Audio(device) : new Input.None(),
    };

    public bool HasInput => CurrentInput is not Input.None;

    /// <summary>Opens the input and starts ticking (does nothing without an input, or when already open).</summary>
    public void Start()
    {
        if (reader is not null) return;
        switch (CurrentInput)
        {
            case Input.Audio audio:
                try
                {
                    reader = inputs.OpenLtc(audio.DeviceId, settings.Channel);
                    Problem = null;
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    Problem = "Not connected";
                    Noted?.Invoke("timecode.input", $"{audio.DeviceId}: {error.Message}");
                    Changed?.Invoke();
                    return;
                }
                Noted?.Invoke("timecode.input", $"{audio.DeviceId} ch {settings.Channel + 1}");
                break;
            case Input.Midi midi:
                reader = inputs.OpenMtc(midi.SourceId);
                Problem = null;
                Noted?.Invoke("timecode.input", $"MTC {midi.SourceId}");
                break;
            default:
                return;
        }
        timecodeClock = new TimecodeClock();
        follow = new TimecodeFollow();
        timer = ShowTimer.Every(clock, ui, TimeSpan.FromSeconds(1.0 / 30), Tick);
        Changed?.Invoke();
    }

    /// <summary>Closes the input; a take armed against timecode stops.</summary>
    public void StopInput()
    {
        timer?.Dispose();
        timer = null;
        reader?.Stop();
        reader = null;
        host.PublishedTimecode = null;
        AnchoredDisplay = null;
        if (Timeline?.TimecodeRecording is not null) Timeline.Stop();
        Reading = new TimecodeClock.Reading(TimecodeClock.State.NoInput);
        Level = 0;
        IsLocked = false;
        signalCheck = new TimecodeSignalCheck();
        NoTimecode = false;
        Changed?.Invoke();
    }

    public void SetInput(Input input)
    {
        if (input == CurrentInput) return;
        StopInput();
        settings = input switch
        {
            Input.Audio audio => settings with { Source = TimecodeSource.Ltc, DeviceId = audio.DeviceId },
            Input.Midi midi => settings with { Source = TimecodeSource.Mtc, MidiSourceId = midi.SourceId },
            _ => settings with { DeviceId = null, MidiSourceId = null },
        };
        SettingsChanged?.Invoke();
        Problem = null;
        Start();
    }

    /// <summary>The audio channel (0-based) LTC is read from.</summary>
    public void SetChannel(int channel)
    {
        settings = settings with { Channel = channel };
        SettingsChanged?.Invoke();
        reader?.SetChannel(channel);
    }

    /// <summary>Nudges timecode by whole frames (−30 to +30), for a rig that runs early or late.</summary>
    public void SetOffset(int frames)
    {
        settings = settings with { OffsetFrames = Math.Clamp(frames, TimecodeStation.OffsetRange.Lower, TimecodeStation.OffsetRange.Upper) };
        SettingsChanged?.Invoke();
    }

    public void SetFollows(bool on)
    {
        settings = settings with { Follows = on };
        SettingsChanged?.Invoke();
        follow = new TimecodeFollow();
        if (!on && Timeline?.TimecodeRecording is not null) Timeline.Stop();
    }

    /// <summary>Under timecode, the timeline's music lane plays only with this on.</summary>
    public void SetPlaysAudio(bool on)
    {
        settings = settings with { PlaysAudio = on };
        SettingsChanged?.Invoke();
        if (!on && audioRunning) Timeline?.StopMusic();
    }

    /// <summary>Tries the input again (the device list polls this while it is missing).</summary>
    public void Retry()
    {
        if (reader is null) Start();
    }

    /// <summary>The clock now, with the offset added.</summary>
    public TimecodeClock.Reading ReadingNow()
    {
        var reading = timecodeClock.ReadingAt(HostNow);
        if (reading.Rate is { } rate && reading.Position is { } position)
            reading = reading with { Position = position + settings.OffsetFrames / rate.Actual() };
        return reading;
    }

    private void StopTimecodeAudio()
    {
        if (settings.PlaysAudio) Timeline?.StopTracks();
        else Timeline?.StopStems();
        audioRunning = false;
    }

    /// <summary>One tick: drains the reader, updates the clock and readout, and follows.</summary>
    internal void Tick()
    {
        if (reader is null) return;
        var (heard, peak) = reader.Drain();
        foreach (var frame in heard) timecodeClock.Receive(frame);

        var readAt = HostNow;
        var reading = timecodeClock.ReadingAt(readAt);
        PublishDisplay(reading);
        if (reading.Rate is { } rate && reading.Position is { } position)
        {
            reading = reading with { Position = position + settings.OffsetFrames / rate.Actual() };
            DetectedRate = rate;
        }
        Reading = reading;
        IsLocked = reading.State == TimecodeClock.State.Locked;
        var level = Math.Max(peak, Level * 0.85f);
        if (Math.Abs(level - Level) > 0.002f) Level = level < 0.001f ? 0 : level;
        NoTimecode = signalCheck.Update(level, TimecodeSignalCheck.Decoding(reading.State), HostNow);

        if (audioRunning && (reading.State is TimecodeClock.State.Stopped or TimecodeClock.State.NoInput
            || (reading.State == TimecodeClock.State.Locked && reading.Reverse)))
            StopTimecodeAudio();
        try
        {
            if (!settings.Follows || Timeline is not { } timeline) return;
            var take = timeline.TimecodeRecordingTick(reading);
            if (!reading.Live)
            {
                if (reading.State != TimecodeClock.State.Freewheel) timeline.TimecodeHeld();
                return;
            }
            var songs = host.CurrentService is { } service ? Songs(service, host.RunOfShow(service), host.Presentation) : [];
            if (audioRunning && reading.Position is { } seconds && reading.Rate is { } heardRate
                && TimecodeFollow.SongContaining(seconds, heardRate, songs.Select(s => s.Song).ToList()) is null)
                StopTimecodeAudio();
            if (take is not null)
                songs = songs.Select(followed => take.Songs([followed.Song]).FirstOrDefault() is { } song ? followed with { Song = song } : null)
                    .OfType<FollowedSong>().ToList();
            if (follow.Step(reading, songs.Select(s => s.Song).ToList()) is { } command
                && songs.FirstOrDefault(s => s.Song.ItemId == command.ItemId) is { } item)
            {
                var newSong = timeline.ItemId != command.ItemId;
                if (newSong) revealPending = command.ItemId;
                SongItemId = command.ItemId;
                var at = command.Position.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
                if (command is TimecodeFollow.Command.Land) Noted?.Invoke("timecode.land", $"{command.ItemId} at {at}");
                else if (newSong) Noted?.Invoke("timecode.song", $"{command.ItemId} at {at}");
                timeline.Follow(command, item.Song.ItemId, item.Presentation, item.ArrangementId, settings.PlaysAudio, readAt);
                audioRunning = true;
            }
            if (revealPending is { } pending && host.LiveContextId == pending)
            {
                revealPending = null;
                host.RevealLive();
            }
        }
        finally
        {
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Shows the incoming timecode on the stage display, re-anchoring only when it drifts by
    /// half a frame or changes (so the display counts on smoothly between readings).
    /// </summary>
    private void PublishDisplay(TimecodeClock.Reading reading)
    {
        var now = clock.GetUtcNow();
        TimecodeDisplay? display = null;
        if (reading.Position is { } seconds && reading.Rate is { } rate)
            display = new TimecodeDisplay(seconds, rate, now, reading.State is TimecodeClock.State.Locked or TimecodeClock.State.Freewheel, reading.Reverse);
        var held = host.PublishedTimecode;
        if (held is not null && display is not null && held.Rate == display.Rate && held.IsRunning == display.IsRunning
            && held.Reverse == display.Reverse && Math.Abs(held.SecondsAt(now) - display.Seconds) < 0.5 / display.Rate.Actual())
            return;
        if (held == display) return;
        host.PublishedTimecode = display;
        AnchoredDisplay = display;
    }

    /// <summary>The service's songs that play from timecode, at their starts in this service.</summary>
    public static List<FollowedSong> Songs(Service service, IReadOnlyList<ServiceItem> runOfShow, Func<string, Presentation?> presentation)
    {
        var starts = Starts(service, runOfShow, presentation);
        var songs = new List<FollowedSong>();
        foreach (var item in runOfShow)
        {
            if (item.ItemKind != ServiceItemKind.Presentation || presentation(item.RefId) is not { } deck
                || PresentationTimelines.Timeline(item.ArrangementId, deck) is not { TimecodeEnabled: true } timeline)
                continue;
            songs.Add(new FollowedSong(
                TimecodeFollow.Song.For(item.Id, timeline, starts.TryGetValue(item.Id, out var start) ? start : null), item, deck, timeline));
        }
        return songs;
    }

    /// <summary>Each song's timecode start in this service ("in service order" gives each an hour).</summary>
    public static Dictionary<string, int> Starts(Service service, IReadOnlyList<ServiceItem> runOfShow, Func<string, Presentation?> presentation) =>
        PresentationTimelines.ServiceStarts(
            runOfShow.Where(i => i.ItemKind == ServiceItemKind.Presentation)
                .Select(i => (i.Id, presentation(i.RefId) is { } deck ? PresentationTimelines.Timeline(i.ArrangementId, deck) : null)),
            service.TimecodeByOrder == true);

    /// <summary>The rate as the Local API names it.</summary>
    public static string WireRate(TimecodeRate rate) => rate switch
    {
        TimecodeRate.Fps24 => "24",
        TimecodeRate.Fps25 => "25",
        TimecodeRate.Fps2997 => "29.97",
        TimecodeRate.Fps2997DropFrame => "29.97df",
        _ => "30",
    };

    /// <summary>Timecode for the Local API (port of LocalAPIBridgeAdapter.timecodeStatus).</summary>
    public TimecodeStatus Status()
    {
        var reading = ReadingNow();
        var state = reading.State.ToString();
        state = char.ToLowerInvariant(state[0]) + state[1..];
        return new TimecodeStatus(
            HasInput, settings.Source.Raw(), settings.Follows, state,
            reading.Label?.Text(dropFrame: reading.Rate == TimecodeRate.Fps2997DropFrame),
            reading.Rate is { } rate ? WireRate(rate) : null, reading.Reverse, settings.OffsetFrames, SongItemId,
            AnchoredDisplay is { } shown ? new TimecodeStatus.AnchorInfo(shown.Seconds, shown.AnchoredAt, shown.IsRunning, shown.Reverse) : null,
            Timeline?.Anchor is { } anchor ? new TimecodeStatus.TimelineInfo(anchor.ItemId, anchor.Position, anchor.At, anchor.Running, anchor.Length) : null);
    }
}
