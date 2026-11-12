using System.Windows.Threading;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// Show flow's start-up and its display refresh. The timing itself runs on the show clock
/// (auto-advance, local timing's end); this only keeps what the operator sees current: the
/// auto-advance chip, the transport strip, the Clear buttons' green, the music card and the
/// service-tracking timers when they're news.
/// </summary>
public sealed partial class MainViewModel
{
    private DispatcherTimer? showFlowTicker;

    /// <summary>Called once from the constructor.</summary>
    private void InitializeShowFlow()
    {
        Media.VideoEnded += OnVideoEnded;
        showFlowTicker = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => RefreshShowFlow(),
            Dispatcher.CurrentDispatcher);
        showFlowTicker.Start();
        RefreshClearState();
    }

    /// <summary>
    /// How long the refreshes below have kept the UI thread, in all, and how many there were. MxU
    /// found its Mac strip and chip redrawing 30 times a second while a video played, pinning the
    /// main thread; here they're property sets four times a second, and the self-test reads these
    /// to show it stays that way (SelfTest.OperatorDepth.cs).
    /// </summary>
    internal (TimeSpan Busy, int Count) ShowFlowCost => (TimeSpan.FromTicks(showFlowBusyTicks.Sum()), showFlowRefreshes);

    /// <summary>The same, by part: the chip, the strip, the clears, the music card, service tracking.</summary>
    internal IReadOnlyList<TimeSpan> ShowFlowCostByPart => [.. showFlowBusyTicks.Select(TimeSpan.FromTicks)];

    private readonly long[] showFlowBusyTicks = new long[5];
    private int showFlowRefreshes;

    /// <summary>Four times a second: cheap reads of what's live, published only when they change.</summary>
    private void RefreshShowFlow()
    {
        var mark = System.Diagnostics.Stopwatch.GetTimestamp();
        void Lap(int part)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            showFlowBusyTicks[part] += System.Diagnostics.Stopwatch.GetElapsedTime(mark, now).Ticks;
            mark = now;
        }
        try
        {
            UpdateAutoAdvanceChip();
            Lap(0);
            RefreshTransport();
            Lap(1);
            RefreshClearState();
            Lap(2);
            if (MusicCardVisible) RefreshMusicCard();
            foreach (var row in ServiceItems)
                if (IsMusicRow(row.Item)) row.IsLive = IsMusicLive(row.Item);
            Lap(3);
            RefreshServiceTracking();
            Lap(4);
        }
        catch (Exception ex)
        {
            Infrastructure.Log.Warn($"Show flow refresh: {ex.Message}");
        }
        showFlowRefreshes++;
    }
}
