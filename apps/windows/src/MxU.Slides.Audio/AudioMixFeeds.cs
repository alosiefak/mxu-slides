using MxU.Slides.Audio.Control;

namespace MxU.Slides.Audio;

/// <summary>
/// The sound of a mix, for streams and recordings that ask for one. A mix is where music buses
/// play to: every playlist, song or timeline clip on the Music tab plays to the main mix. While
/// someone listens to a mix, each bus routed to it hands its sound (after its fader) to an
/// <see cref="AudioMixFeed"/>, which adds them up on its own steady 10 ms clock. A bus that
/// starts, stops or moves to another mix is picked up straight away.
/// <para>
/// Port of the feed half of the Mac's AudioMixerController (addFeedConsumer, removeFeedConsumer,
/// syncMixFeeds). The Mac's mixer can also send sound inputs, video sound and the multitrack
/// into a mix; Windows has no mixer screen yet, so a mix carries the music buses only.
/// </para>
/// Threading: everything is thread-safe. Consumers are called on the mix clock's thread.
/// </summary>
public sealed class AudioMixFeeds : IDisposable
{
    /// <summary>Something that can feed a mix: one music bus.</summary>
    internal interface IMember
    {
        /// <summary>Who it is (the same object every time the bus is listed).</summary>
        object Key { get; }

        /// <summary>The mix it plays to.</summary>
        string MixKey { get; }

        /// <summary>Starts (or, with null, stops) handing its sound to <paramref name="feed"/>.</summary>
        void SetFeed(Action<PcmBuffer>? feed);
    }

    private sealed class PlayerMember(AudioPlayer player) : IMember
    {
        public object Key => player;

        public string MixKey => player.TargetKey;

        public void SetFeed(Action<PcmBuffer>? feed) => player.Engine.SetFeedConsumer(feed);
    }

    private sealed class Hub
    {
        public required AudioMixFeed Feed { get; init; }

        public Dictionary<Guid, Action<PcmBuffer, double>> Consumers { get; } = [];
    }

    private readonly Lock gate = new();
    private readonly Func<IReadOnlyList<IMember>> members;
    private readonly bool startsClock;
    private readonly Dictionary<string, Hub> hubs = new(StringComparer.Ordinal);
    private readonly Dictionary<object, (IMember Member, string MixKey, string Name)> attached = new(ReferenceEqualityComparer.Instance);
    private readonly AudioController? audio;
    private int nextMember;
    private bool disposed;

    /// <summary>The mixes this PC has: the main mix, which the Music tab plays to. (The Mac lets you make more in its mixer.)</summary>
    public static IReadOnlyList<(string Id, string Name)> Known { get; } = [(AudioRouting.MainKey, "Main mix (music)")];

    public AudioMixFeeds(AudioController audio)
        : this(() => [.. audio.Players.Select(p => (IMember)new PlayerMember(p))], startsClock: true)
    {
        this.audio = audio;
        audio.PlayersChanged += Sync;
    }

    /// <param name="startsClock">False for tests, which tick the feeds themselves.</param>
    internal AudioMixFeeds(Func<IReadOnlyList<IMember>> members, bool startsClock)
    {
        this.members = members;
        this.startsClock = startsClock;
    }

    public static bool Exists(string mixId) => Known.Any(mix => mix.Id == mixId);

    public static string? NameOf(string mixId) => Known.FirstOrDefault(mix => mix.Id == mixId).Name;

    /// <summary>How many mixes someone is listening to (diagnostics and tests).</summary>
    public int OpenMixes
    {
        get { lock (gate) return hubs.Count; }
    }

    /// <summary>The worst backlog among the open mixes, in milliseconds (for the recording heartbeat).</summary>
    public double WorstBacklogMilliseconds
    {
        get { lock (gate) return hubs.Values.Select(h => h.Feed.BacklogMilliseconds).DefaultIfEmpty(0).Max(); }
    }

    /// <summary>Listens to a mix (48 kHz stereo, 10 ms at a time); null when there's no such mix. Disposing the result stops listening.</summary>
    public IDisposable? Open(string mixId, Action<PcmBuffer, double> consumer)
    {
        if (!Exists(mixId)) return null;
        var token = Guid.NewGuid();
        lock (gate)
        {
            if (disposed) return null;
            if (!hubs.TryGetValue(mixId, out var hub))
            {
                Hub? made = null;
                made = new Hub { Feed = new AudioMixFeed((buffer, host) => Deliver(made!, buffer, host), startsClock) };
                hubs[mixId] = hub = made;
            }
            hub.Consumers[token] = consumer;
        }
        Sync();
        return new Listener(this, mixId, token);
    }

    private void Deliver(Hub hub, PcmBuffer buffer, double hostSeconds)
    {
        Action<PcmBuffer, double>[] consumers;
        lock (gate) consumers = [.. hub.Consumers.Values];
        foreach (var consumer in consumers)
        {
            try { consumer(buffer, hostSeconds); }
            catch { }
        }
    }

    private void Close(string mixId, Guid token)
    {
        AudioMixFeed? closing = null;
        lock (gate)
        {
            if (!hubs.TryGetValue(mixId, out var hub) || !hub.Consumers.Remove(token)) return;
            if (hub.Consumers.Count == 0)
            {
                hubs.Remove(mixId);
                closing = hub.Feed;
            }
        }
        closing?.Dispose();
        Sync();
    }

    /// <summary>
    /// Connects every bus playing to a mix someone listens to, and disconnects the rest. Runs by
    /// itself when buses come and go; call it after changing where a playlist plays.
    /// </summary>
    public void Sync()
    {
        IReadOnlyList<IMember> current;
        try { current = members(); }
        catch { return; }
        lock (gate)
        {
            var wanted = disposed ? [] : current.Where(m => hubs.ContainsKey(m.MixKey)).ToDictionary(m => m.Key, ReferenceEqualityComparer.Instance);
            foreach (var (key, (member, mixKey, name)) in attached.ToList())
            {
                if (wanted.TryGetValue(key, out var still) && still.MixKey == mixKey) continue;
                member.SetFeed(null);
                if (hubs.TryGetValue(mixKey, out var hub)) hub.Feed.RemoveMember(name);
                attached.Remove(key);
            }
            foreach (var (key, member) in wanted)
            {
                if (attached.ContainsKey(key)) continue;
                var name = $"bus-{++nextMember}";
                var feed = hubs[member.MixKey].Feed;
                member.SetFeed(buffer => feed.Push(name, buffer));
                attached[key] = (member, member.MixKey, name);
            }
        }
    }

    /// <summary>Ticks every open feed (tests, which run without the clock).</summary>
    internal void TickAll(double now)
    {
        List<AudioMixFeed> feeds;
        lock (gate) feeds = [.. hubs.Values.Select(h => h.Feed)];
        foreach (var feed in feeds) feed.Tick(now);
    }

    public void Dispose()
    {
        List<AudioMixFeed> feeds;
        lock (gate)
        {
            disposed = true;
            feeds = [.. hubs.Values.Select(h => h.Feed)];
            hubs.Clear();
        }
        if (audio is not null) audio.PlayersChanged -= Sync;
        Sync();
        foreach (var feed in feeds) feed.Dispose();
    }

    private sealed class Listener(AudioMixFeeds owner, string mixId, Guid token) : IDisposable
    {
        private int closed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref closed, 1) == 0) owner.Close(mixId, token);
        }
    }
}
