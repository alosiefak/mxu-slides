namespace MxU.Slides.VideoIO.DeckLink;

/// <summary>How the card says a scheduled frame went.</summary>
public enum DeckLinkCompletion { OnTime, Late, Dropped, Flushed }

/// <summary>
/// The bookkeeping between the app's frames and the card's schedule. The output keeps
/// three pixel slots: one being shown (<see cref="ShownSlot"/>) and up to two
/// <see cref="Waiting"/> to be shown. Each program frame is "latched" into a free slot;
/// each time the card finishes showing a frame (a completion), the oldest waiting slot
/// becomes the shown one and its pixels are copied into the frame the card will show
/// next.
/// <list type="bullet">
/// <item>A completion with nothing waiting shows the same slot again: a <see cref="Repeats"/>.</item>
/// <item>A latch with two already waiting throws the older away: a <see cref="Skips"/>.</item>
/// </list>
/// Repeats and skips are how the app sees its frame clock drifting against the card's.
/// The card's own late/dropped counts and schedule jumps are tallied too. Not
/// thread-safe; <see cref="DeckLinkOutput"/> locks around it. Port of dlk_latch_slot and
/// dlk_tally_completion (decklink_shim.cpp) and DeckLinkOutputTiming.
/// </summary>
public sealed class DeckLinkOutputTiming
{
    private int[] waiting = new int[2];
    private int waitingCount;

    public ulong Completions { get; private set; }
    public double LastCompletionHostTime { get; private set; }
    public ulong Latched { get; private set; }
    public int ShownSlot { get; private set; }
    public ulong Repeats { get; private set; }
    public ulong Skips { get; private set; }
    public ulong Late { get; private set; }
    public ulong Dropped { get; private set; }
    public ulong Jumps { get; private set; }

    /// <summary>The slots waiting to be shown, oldest first.</summary>
    public IReadOnlyList<int> Waiting => waiting[..waitingCount];

    /// <summary>Picks the slot for a new frame: never the shown slot or the one waiting ahead of it. Returns it.</summary>
    public int Latch()
    {
        if (waitingCount == 2)
        {
            waiting[0] = waiting[1];
            waitingCount = 1;
            Skips++;
        }
        var slot = 0;
        while (slot == ShownSlot || (waitingCount > 0 && slot == waiting[0])) slot++;
        waiting[waitingCount] = slot;
        waitingCount++;
        Latched++;
        return slot;
    }

    /// <summary>
    /// The card finished showing a frame at <paramref name="hostTime"/>. Advances to the
    /// next waiting slot (or counts a repeat) and returns the slot to show next.
    /// Completions before the first latch are the preroll, not repeats.
    /// </summary>
    public int TallyCompletion(double hostTime, DeckLinkCompletion result = DeckLinkCompletion.OnTime, bool jumped = false)
    {
        Completions++;
        LastCompletionHostTime = hostTime;
        if (waitingCount > 0)
        {
            ShownSlot = waiting[0];
            waiting[0] = waiting[1];
            waitingCount--;
        }
        else if (Latched > 0)
        {
            Repeats++;
        }
        if (result == DeckLinkCompletion.Late) Late++;
        if (result == DeckLinkCompletion.Dropped) Dropped++;
        if (jumped) Jumps++;
        return ShownSlot;
    }

    /// <summary>A copy, for handing to another thread.</summary>
    public DeckLinkOutputTiming Copy()
    {
        var copy = (DeckLinkOutputTiming)MemberwiseClone();
        copy.waiting = (int[])waiting.Clone();
        return copy;
    }
}
