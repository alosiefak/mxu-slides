namespace MxU.Slides.Production.Lighting;

/// <summary>
/// Works out every DMX level at a moment in time, from what has been asked of the lights:
/// <list type="bullet">
/// <item><b>The main look</b> (a cue, or "Lights: Sermon look"): firing a new one crossfades from
/// whatever is showing now (even half way through another fade) to the new look over its fade
/// time. Brightness channels the new look doesn't mention fade out; attribute channels
/// (colour, position) it doesn't mention stay where they are, so a mover doesn't swing home.</item>
/// <item><b>Looks on top</b> ("add on top": house lights, a special): each fades in over the main,
/// and out again when released. Brightness merges <i>highest takes precedence</i> (HTP): the
/// brighter of the two wins, so adding a look never dims anything. Attributes merge <i>latest
/// takes precedence</i> (LTP): the look added last sets the colour, blended by how far it has
/// faded in, so its colour arrives with it.</item>
/// <item><b>The programmer</b>: the levels being edited in the lighting window, when it's live.
/// They override everything (as a lighting desk's programmer does) until it's let go.</item>
/// <item><b>The grand master</b> scales all brightness (0-100 %, with a fade), and <b>blackout</b>
/// takes it to zero at once. Neither touches attributes.</item>
/// </list>
/// Pure arithmetic on numbers: it never looks at a clock itself (the time is passed in), so
/// every fade and merge can be tested exactly. Not thread-safe: <see cref="LightingController"/> locks round it.
/// </summary>
public sealed class DmxMixer
{
    public const int Channels = 512;

    private sealed class Layer(LightingLook look, double start, double fade, long order)
    {
        public LightingLook Look { get; } = look;
        public double Start { get; set; } = start;
        public double Fade { get; set; } = fade;
        public long Order { get; set; } = order;
        public double? ReleaseStart { get; set; }
        public double ReleaseFade { get; set; }
        public double ReleaseFrom { get; set; }

        /// <summary>How much of the look shows, 0 to 1: rising as it fades in, falling once released.</summary>
        public double Factor(double now) => ReleaseStart is { } released
            ? ReleaseFrom * (1 - Progress(released, ReleaseFade, now))
            : Progress(Start, Fade, now);

        public bool Gone(double now) => ReleaseStart is { } released && Progress(released, ReleaseFade, now) >= 1;
    }

    private readonly Dictionary<int, double[]> mainFrom = [];
    private readonly Dictionary<int, double[]> mainTo = [];
    private double mainStart;
    private double mainFade;
    private readonly List<Layer> layers = [];
    private long nextOrder;
    private Dictionary<(int Universe, int Channel), int>? programmer;
    private double masterFrom = 1;
    private double masterTo = 1;
    private double masterStart;
    private double masterFade;
    private HashSet<(int Universe, int Channel)> attributes = [];
    private readonly SortedSet<int> universes = [];

    /// <summary>The main look's id (null before any, or after All Off).</summary>
    public string? MainLookId { get; private set; }

    /// <summary>The looks on top, oldest first (including ones still fading out).</summary>
    public IReadOnlyList<string> LayerLookIds => [.. layers.OrderBy(l => l.Order).Select(l => l.Look.Id)];

    public bool Blackout { get; set; }

    /// <summary>
    /// Every universe anything has touched. Only these are sent, and nothing is sent at all
    /// until something touches one: a PC that has never run a lighting step leaves the rig alone.
    /// </summary>
    public IReadOnlyCollection<int> Universes => universes;

    public bool IsActive => universes.Count > 0;

    /// <summary>Which channels are attributes (from the show's groups; see <see cref="LightingShow.AttributeChannels"/>).</summary>
    public void SetAttributes(HashSet<(int Universe, int Channel)> channels) => attributes = channels;

    private bool IsAttribute(int universe, int channel) => attributes.Contains((universe, channel));

    // MARK: What can be asked

    /// <summary>Crossfades the main look to <paramref name="look"/> over <paramref name="fade"/> seconds.</summary>
    public void Fire(LightingLook look, double fade, double now)
    {
        var touched = new SortedSet<int>(universes);
        foreach (var level in look.Levels) touched.Add(level.Universe);
        foreach (var universe in touched)
        {
            var from = new double[Channels];
            var to = new double[Channels];
            for (var c = 0; c < Channels; c++)
            {
                from[c] = Main(universe, c, now);
                // Not in the new look: brightness goes out, an attribute stays put.
                to[c] = IsAttribute(universe, c + 1) ? from[c] : 0;
            }
            foreach (var level in look.Levels.Where(l => l.Universe == universe && l.Channel is >= 1 and <= Channels))
                to[level.Channel - 1] = level.Value;
            mainFrom[universe] = from;
            mainTo[universe] = to;
            universes.Add(universe);
        }
        mainStart = now;
        mainFade = Math.Max(0, fade);
        MainLookId = look.Levels.Count == 0 ? null : look.Id;
    }

    /// <summary>Fades <paramref name="look"/> in on top of the main look (again from where it is, if it was fading out).</summary>
    public void Add(LightingLook look, double fade, double now)
    {
        fade = Math.Max(0, fade);
        var existing = layers.FirstOrDefault(l => l.Look.Id == look.Id);
        var already = existing?.Factor(now) ?? 0;
        if (existing is not null) layers.Remove(existing);
        // Start the fade in as if it had begun earlier, so a look being let go turns round without a jump.
        layers.Add(new Layer(look, now - (already * fade), fade, nextOrder++));
        foreach (var level in look.Levels) universes.Add(level.Universe);
    }

    /// <summary>Fades out a look that's on top; if it's the main look instead, the main look fades out.</summary>
    public void Release(string lookId, double fade, double now)
    {
        if (layers.FirstOrDefault(l => l.Look.Id == lookId && l.ReleaseStart is null) is { } layer)
        {
            layer.ReleaseFrom = layer.Factor(now);
            layer.ReleaseStart = now;
            layer.ReleaseFade = Math.Max(0, fade);
        }
        else if (MainLookId == lookId)
        {
            Fire(new LightingLook(), fade, now);
        }
    }

    /// <summary>Everything out: the main look fades to nothing and every look on top is let go.</summary>
    public void AllOff(double fade, double now)
    {
        Fire(new LightingLook(), fade, now);
        foreach (var layer in layers.Where(l => l.ReleaseStart is null).Select(l => l.Look.Id).ToList()) Release(layer, fade, now);
    }

    /// <summary>Moves the grand master to <paramref name="level"/> (0 to 1) over <paramref name="fade"/> seconds.</summary>
    public void SetGrandMaster(double level, double fade, double now)
    {
        masterFrom = GrandMaster(now);
        masterTo = Math.Clamp(level, 0, 1);
        masterStart = now;
        masterFade = Math.Max(0, fade);
    }

    public double GrandMaster(double now) => masterFrom + ((masterTo - masterFrom) * Progress(masterStart, masterFade, now));

    /// <summary>The editor's live levels (they override everything), or null to let them go.</summary>
    public void SetProgrammer(IEnumerable<LookLevel>? levels)
    {
        if (levels is null)
        {
            programmer = null;
            return;
        }
        programmer = [];
        foreach (var level in levels.Where(l => l.Channel is >= 1 and <= Channels))
        {
            programmer[(level.Universe, level.Channel)] = level.Value;
            universes.Add(level.Universe);
        }
    }

    public bool HasProgrammer => programmer is not null;

    /// <summary>Whether anything is still fading (the lighting window redraws while it is).</summary>
    public bool IsMoving(double now) =>
        Progress(mainStart, mainFade, now) < 1 || Progress(masterStart, masterFade, now) < 1
        || layers.Any(l => l.ReleaseStart is { } r ? Progress(r, l.ReleaseFade, now) < 1 : Progress(l.Start, l.Fade, now) < 1);

    // MARK: The levels

    /// <summary>Every touched universe's 512 levels at <paramref name="now"/>, ready to send.</summary>
    public IReadOnlyDictionary<int, byte[]> Frame(double now)
    {
        layers.RemoveAll(l => l.Gone(now));
        var master = Blackout ? 0 : GrandMaster(now);
        var frame = new Dictionary<int, byte[]>();
        var ordered = layers.OrderBy(l => l.Order).Select(l => (Layer: l, Factor: l.Factor(now))).Where(l => l.Factor > 0).ToList();
        foreach (var universe in universes)
        {
            var levels = new double[Channels];
            for (var c = 0; c < Channels; c++) levels[c] = Main(universe, c, now);
            foreach (var (layer, factor) in ordered)
            {
                foreach (var level in layer.Look.Levels)
                {
                    if (level.Universe != universe || level.Channel is < 1 or > Channels) continue;
                    var c = level.Channel - 1;
                    levels[c] = IsAttribute(universe, level.Channel)
                        ? levels[c] + ((level.Value - levels[c]) * factor)   // LTP: the newest wins, blended in as it fades
                        : Math.Max(levels[c], level.Value * factor);         // HTP: the brightest wins
                }
            }
            if (programmer is not null)
                foreach (var ((u, channel), value) in programmer)
                    if (u == universe) levels[channel - 1] = value;
            var bytes = new byte[Channels];
            for (var c = 0; c < Channels; c++)
            {
                var value = IsAttribute(universe, c + 1) ? levels[c] : levels[c] * master;
                bytes[c] = (byte)Math.Round(Math.Clamp(value, 0, 255), MidpointRounding.AwayFromZero);
            }
            frame[universe] = bytes;
        }
        return frame;
    }

    /// <summary>The main look's level for a channel (0-based) at a moment: a straight-line fade from "from" to "to".</summary>
    private double Main(int universe, int channel, double now)
    {
        if (!mainTo.TryGetValue(universe, out var to)) return 0;
        var from = mainFrom[universe];
        return from[channel] + ((to[channel] - from[channel]) * Progress(mainStart, mainFade, now));
    }

    /// <summary>How far through a fade, 0 to 1 (a zero-length fade is done at once).</summary>
    internal static double Progress(double start, double fade, double now) =>
        fade <= 0 ? 1 : Math.Clamp((now - start) / fade, 0, 1);
}
