using System.Diagnostics;

namespace MxU.Slides.Production.Lighting;

/// <summary>
/// The lights as the show drives them: the library's looks and cue list, where the cue list
/// is up to, and the <see cref="DmxMixer"/> that turns it all into levels. Production steps,
/// the lighting window and the Local API all come through here, from any thread; one lock
/// keeps them and the DMX engine's 40 frames a second from treading on each other. Every
/// call is quick (it only changes numbers): the sending happens on the engine's thread.
/// </summary>
public sealed class LightingController
{
    private readonly Lock gate = new();
    private readonly DmxMixer mixer = new();
    private readonly Func<double> clock;
    private LightingShow show = new();
    private int cueIndex = -1;

    /// <param name="clock">Seconds, from any fixed start (tests pass their own; the app uses the Stopwatch).</param>
    public LightingController(Func<double>? clock = null) =>
        this.clock = clock ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);

    /// <summary>Something changed that the lighting window shows (a cue went, blackout, the master).</summary>
    public event Action? Changed;

    /// <summary>Levels changed: the DMX engine sends a frame now rather than at its next tick.</summary>
    public event Action? Nudged;

    /// <summary>The looks and cues (a copy: change them through <see cref="Load"/>).</summary>
    public LightingShow Show
    {
        get
        {
            lock (gate) return show.Copy();
        }
    }

    /// <summary>Takes in the library's lighting (at start, and after every edit).</summary>
    public void Load(LightingShow loaded)
    {
        lock (gate)
        {
            show = loaded.Copy();
            mixer.SetAttributes(show.AttributeChannels());
            if (cueIndex >= show.Cues.Count) cueIndex = show.Cues.Count - 1;
        }
        Changed?.Invoke();
    }

    public bool IsActive
    {
        get
        {
            lock (gate) return mixer.IsActive;
        }
    }

    public IReadOnlyDictionary<int, byte[]> Frame()
    {
        lock (gate) return mixer.Frame(clock());
    }

    public bool Blackout
    {
        get
        {
            lock (gate) return mixer.Blackout;
        }
    }

    /// <summary>The grand master now, 0 to 1.</summary>
    public double GrandMaster
    {
        get
        {
            lock (gate) return mixer.GrandMaster(clock());
        }
    }

    public string? MainLookId
    {
        get
        {
            lock (gate) return mixer.MainLookId;
        }
    }

    public IReadOnlyList<string> LooksOnTop
    {
        get
        {
            lock (gate) return mixer.LayerLookIds;
        }
    }

    public bool IsMoving
    {
        get
        {
            lock (gate) return mixer.IsMoving(clock());
        }
    }

    /// <summary>The cue the list is on (null before the first GO).</summary>
    public LightingCue? CurrentCue
    {
        get
        {
            lock (gate) return cueIndex >= 0 && cueIndex < show.Cues.Count ? show.Cues[cueIndex] : null;
        }
    }

    public LightingCue? NextCue
    {
        get
        {
            lock (gate) return cueIndex + 1 < show.Cues.Count ? show.Cues[cueIndex + 1] : null;
        }
    }

    // MARK: Doing things

    /// <summary>Fires a look: as the main look (a crossfade), or on top. False when there's no such look.</summary>
    public bool FireLook(string lookId, double? fadeSeconds = null, bool onTop = false)
    {
        lock (gate)
        {
            if (show.Look(lookId) is not { } look) return false;
            var fade = fadeSeconds ?? look.FadeSeconds;
            if (onTop) mixer.Add(look, fade, clock());
            else mixer.Fire(look, fade, clock());
        }
        Touched();
        return true;
    }

    public bool ReleaseLook(string lookId, double? fadeSeconds = null)
    {
        lock (gate)
        {
            if (show.Look(lookId) is not { } look) return false;
            mixer.Release(lookId, fadeSeconds ?? look.FadeSeconds, clock());
        }
        Touched();
        return true;
    }

    /// <summary>GO: the next cue in the list (or, with an id, that cue). False at the end of the list.</summary>
    public bool Go(string? cueId = null)
    {
        lock (gate)
        {
            var index = cueId is null ? cueIndex + 1 : show.Cues.FindIndex(c => c.Id == cueId);
            if (index < 0 || index >= show.Cues.Count) return false;
            if (!FireCue(index)) return false;
        }
        Touched();
        return true;
    }

    /// <summary>Back: the cue before the one showing.</summary>
    public bool Back()
    {
        lock (gate)
        {
            if (cueIndex <= 0 || !FireCue(cueIndex - 1)) return false;
        }
        Touched();
        return true;
    }

    // Called with the lock held.
    private bool FireCue(int index)
    {
        var cue = show.Cues[index];
        if (show.Look(cue.LookId) is not { } look) return false;
        mixer.Fire(look, cue.FadeSeconds ?? look.FadeSeconds, clock());
        cueIndex = index;
        return true;
    }

    public void AllOff(double fadeSeconds)
    {
        lock (gate) mixer.AllOff(fadeSeconds, clock());
        Touched();
    }

    /// <summary>Blackout on, off, or (null) the other way round.</summary>
    public void SetBlackout(bool? on)
    {
        lock (gate) mixer.Blackout = on ?? !mixer.Blackout;
        Touched();
    }

    /// <summary>The grand master, in per cent, over a fade.</summary>
    public void SetGrandMaster(double percent, double fadeSeconds = 0)
    {
        lock (gate) mixer.SetGrandMaster(percent / 100, fadeSeconds, clock());
        Touched();
    }

    /// <summary>The editor's live levels (null lets them go). They override every look until let go.</summary>
    public void SetProgrammer(IEnumerable<LookLevel>? levels)
    {
        lock (gate) mixer.SetProgrammer(levels?.ToList());
        Touched();
    }

    private void Touched()
    {
        Nudged?.Invoke();
        Changed?.Invoke();
    }
}
