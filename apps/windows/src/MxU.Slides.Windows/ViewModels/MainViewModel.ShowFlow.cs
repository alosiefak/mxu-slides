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

    /// <summary>Four times a second: cheap reads of what's live, published only when they change.</summary>
    private void RefreshShowFlow()
    {
        try
        {
            UpdateAutoAdvanceChip();
            RefreshTransport();
            RefreshClearState();
            if (MusicCardVisible) RefreshMusicCard();
            RefreshServiceTracking();
            foreach (var row in ServiceItems)
                if (IsMusicRow(row.Item)) row.IsLive = IsMusicLive(row.Item);
        }
        catch (Exception ex)
        {
            Infrastructure.Log.Warn($"Show flow refresh: {ex.Message}");
        }
    }
}
