using MxU.Slides.Audio.Control;

namespace MxU.Slides.Audio.Routing;

/// <summary>
/// Where each kind of sound goes on this PC: the music (every playlist, song and timeline
/// clip plays on the main mix) and the video sound (the media mix, the main one unless set
/// otherwise), each through its mix's output to a device, a channel pair (or one channel) and
/// a delay. Holds the outputs, the mixes and a cached list of the devices, so the audio
/// controller can ask "where does this play?" under its lock without waiting for Windows.
/// Port of the routing half of the Mac's AudioController (routing(forMix:), defaultRouting,
/// applyLevels' hardware check, mediaMixId/setMediaMix, applyDefaultRoutingToMedia).
/// <para>
/// Threading: thread-safe. <see cref="Changed"/> is raised on the thread that made the change
/// (a device change arrives on an audio thread).
/// </para>
/// </summary>
public sealed class AudioRoutingTable : IDisposable
{
    /// <summary>The name given to the mix made when video sound gets an output of its own.</summary>
    public const string VideoMixName = "Video Sound";

    private readonly Lock gate = new();
    private readonly Func<IReadOnlyList<AudioOutputDevice>> listDevices;
    private readonly IDisposable? deviceWatch;
    private IReadOnlyList<AudioOutputDevice> devices;
    private string mediaMixId;

    /// <param name="mediaMixId">The mix video sound plays on (null = the main mix).</param>
    /// <param name="listDevices">This PC's outputs (default: ask Windows). Tests pass their own.</param>
    /// <param name="watchDevices">Re-read the devices when Windows says they changed (off in tests).</param>
    public AudioRoutingTable(AudioOutputInventory outputs, AudioMixInventory mixes, string? mediaMixId = null,
        Func<IReadOnlyList<AudioOutputDevice>>? listDevices = null, bool watchDevices = true)
    {
        Outputs = outputs;
        Mixes = mixes;
        this.listDevices = listDevices ?? AudioDeviceList.OutputDevices;
        devices = this.listDevices();
        this.mediaMixId = mixes.Entry(mediaMixId) is not null ? mediaMixId! : AudioMixInventory.MainId;
        outputs.Changed += RaiseChanged;
        mixes.Changed += RaiseChanged;
        if (watchDevices) deviceWatch = AudioDeviceList.ListenForChanges(RefreshDevices);
    }

    public AudioOutputInventory Outputs { get; }

    public AudioMixInventory Mixes { get; }

    /// <summary>Anything that moves sound changed: an output, a mix, the media mix, or the devices plugged in.</summary>
    public event Action? Changed;

    /// <summary>The output devices last read from Windows.</summary>
    public IReadOnlyList<AudioOutputDevice> Devices
    {
        get { lock (gate) return devices; }
    }

    /// <summary>Reads the devices again (Windows reported a change); raises <see cref="Changed"/>.</summary>
    public void RefreshDevices()
    {
        var found = listDevices();
        lock (gate) devices = found;
        RaiseChanged();
    }

    // MARK: Where sound plays

    /// <summary>
    /// Where a mix plays: its first output's device (null = the system default, which is also
    /// what an unplugged device falls back to, as on the Mac), its pair or mono channel (an
    /// offset the device hasn't got falls back to the first pair) and its delay. The key is the mix id.
    /// </summary>
    public AudioRouting RoutingForMix(string mixId)
    {
        var mix = Mixes.Entry(mixId) ?? Mixes.Main;
        var output = Outputs.Entry(mix.OutputIds.FirstOrDefault());
        AudioOutputDevice? device;
        lock (gate) device = output?.DeviceUid is { } uid ? devices.FirstOrDefault(d => d.Uid == uid) : null;
        var delay = output?.DelayMs ?? 0;
        var channels = Math.Max(2, device?.ChannelCount ?? 2);
        if (output?.MonoChannel is { } mono && mono >= 0 && mono < channels)
            return new AudioRouting(device, mono, mix.Id, delay, Mono: true);
        var offset = output?.ChannelOffset ?? 0;
        var clamped = offset >= 0 && offset + 1 < channels ? offset : 0;
        return new AudioRouting(device, clamped, mix.Id, delay);
    }

    /// <summary>
    /// False when a mix reaches no device: it plays on "no output", or on an output set to "no
    /// device". Its sound still reaches streams and recordings of the mix.
    /// </summary>
    public bool HardwareLive(string mixId)
    {
        var outputIds = (Mixes.Entry(mixId) ?? Mixes.Main).OutputIds;
        return !(AudioMixInventory.IsVirtual(outputIds) || Outputs.IsDeviceless(outputIds.FirstOrDefault()));
    }

    /// <summary>Where a playlist (or, with null, a single song or timeline clip) plays: the main mix. Per-playlist mixes come with the mixer.</summary>
    public AudioRouting MusicRouting(string? playlistId) => RoutingForMix(AudioMixInventory.MainId);

    /// <summary>The mix video sound plays on.</summary>
    public string MediaMixId
    {
        get { lock (gate) return mediaMixId; }
    }

    public AudioRouting MediaRouting() => RoutingForMix(MediaMixId);

    /// <summary>Plays video sound on another mix (the main one, or any other).</summary>
    public void SetMediaMix(string mixId)
    {
        var id = Mixes.Entry(mixId) is not null ? mixId : AudioMixInventory.MainId;
        lock (gate)
        {
            if (mediaMixId == id) return;
            mediaMixId = id;
        }
        RaiseChanged();
    }

    // MARK: The Sound settings' two pickers

    /// <summary>Where the music plays: an output id, or <see cref="AudioMixInventory.DefaultOutputId"/> / <see cref="AudioMixInventory.NoOutputId"/>.</summary>
    public string MusicOutputToken => Mixes.PrimaryToken(AudioMixInventory.MainId);

    public void SetMusicOutput(string token) => Mixes.SetPrimary(AudioMixInventory.MainId, token);

    /// <summary>Where video sound plays: null while it follows the music (the main mix), else its own mix's output token.</summary>
    public string? VideoOutputToken => MediaMixId == AudioMixInventory.MainId ? null : Mixes.PrimaryToken(MediaMixId);

    /// <summary>
    /// Gives video sound its own place (a token, as <see cref="MusicOutputToken"/>), or null to
    /// follow the music again. On the Mac that is the media mix: a mix of its own, sent to an
    /// output, so the same choice reads back the same way in the mixer. The mix is made the
    /// first time ("Video Sound") and kept after, so switching back and forth keeps one.
    /// </summary>
    public void SetVideoOutput(string? token)
    {
        if (token is null)
        {
            SetMediaMix(AudioMixInventory.MainId);
            return;
        }
        var mix = Mixes.Entry(MediaMixId) is { IsMain: false } current ? current
            : Mixes.Entries.FirstOrDefault(m => !m.IsMain && m.Name == VideoMixName) ?? Mixes.Create(VideoMixName);
        Mixes.SetPrimary(mix.Id, token);
        SetMediaMix(mix.Id);
    }

    private void RaiseChanged() => Changed?.Invoke();

    public void Dispose()
    {
        deviceWatch?.Dispose();
        Outputs.Changed -= RaiseChanged;
        Mixes.Changed -= RaiseChanged;
    }
}
