using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Media;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Infrastructure;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// Auto-advance in the app: slides that move on by themselves, Slide Show loops, videos that
/// advance when they end, and the chip under the live preview that counts down and holds. The
/// rules are Core's <see cref="AutoAdvanceController"/> (ported from the Mac's ServiceControls);
/// this is its view of the live show (<see cref="IAutoAdvanceHost"/>) and the chip.
/// </summary>
public sealed partial class MainViewModel : IAutoAdvanceHost
{
    private AutoAdvanceController? autoAdvance;

    /// <summary>Made on the UI thread, so its timers (on the show clock) post back to it.</summary>
    public AutoAdvanceController AutoAdvance => autoAdvance ??= CreateAutoAdvance();

    private AutoAdvanceController CreateAutoAdvance()
    {
        // Its timers fire on the show clock's thread and must hand back to the UI thread, even if
        // this runs before WPF has installed its synchronization context.
        var ui = SynchronizationContext.Current ?? new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var created = new AutoAdvanceController(this, ui: ui);
        created.Changed += UpdateAutoAdvanceChip;
        created.Noted += (crumb, detail) => Log.Info($"Auto-advance: {crumb} {detail}");
        return created;
    }

    // MARK: The chip

    [ObservableProperty]
    public partial bool AutoAdvanceVisible { get; set; }

    [ObservableProperty]
    public partial bool AutoAdvanceHeld { get; set; }

    /// <summary>"Auto advance" or "Advance held".</summary>
    [ObservableProperty]
    public partial string AutoAdvanceCaption { get; set; } = "Auto advance";

    /// <summary>The time left, "0:07" (or "–:––" while it waits for a video that hasn't opened).</summary>
    [ObservableProperty]
    public partial string AutoAdvanceTime { get; set; } = "";

    /// <summary>Click the chip: hold the advance (the video keeps playing, the room stays), or let it go again.</summary>
    [RelayCommand]
    private void ToggleAutoAdvanceHold() => AutoAdvance.SetPaused(!AutoAdvance.IsPaused);

    private void UpdateAutoAdvanceChip()
    {
        var controller = AutoAdvance;
        AutoAdvanceVisible = controller.Pending is not null;
        AutoAdvanceHeld = controller.IsPaused;
        AutoAdvanceCaption = controller.IsPaused ? "Advance held" : "Auto advance";
        AutoAdvanceTime = controller.Remaining() is { } seconds ? ChipTime(seconds) : "–:––";
    }

    /// <summary>Whole seconds, rounded up, as the Mac's chip shows them.</summary>
    internal static string ChipTime(double seconds)
    {
        var whole = Math.Max((int)Math.Ceiling(seconds), 0);
        return $"{whole / 60}:{whole % 60:00}";
    }

    // MARK: Hooks the live show calls

    /// <summary>A slide landed: its timer (if any) takes over from the last one, and the service clock notes its row.</summary>
    private void ShowFlowSlideLanded(Slide slide, Presentation presentation, string? itemId)
    {
        AutoAdvance.SlideLanded(slide, presentation);
        NoteServiceItemFired(itemId);
    }

    /// <summary>A video player reached the end of its pass: check now rather than at the next poll.</summary>
    private void OnVideoEnded(string playbackKey)
    {
        if (!playbackKey.StartsWith(LivePlaybackScope + ":", StringComparison.Ordinal)) return;
        NoteVideoEnded(playbackKey);
        AutoAdvance.PollNow();
    }

    // MARK: IAutoAdvanceHost

    ShowState IAutoAdvanceHost.Show => Show;

    double IAutoAdvanceHost.HostNow => ShowState.HostClock();

    /// <summary>The live slide's row: only while <see cref="Live"/> still describes the slide on screen (a media row takes it over).</summary>
    string? IAutoAdvanceHost.LiveContextId => LiveDescribesSlide ? Live!.ItemId : null;

    int? IAutoAdvanceHost.LiveOccurrence => LiveDescribesSlide ? Live!.Occurrence : null;

    private bool LiveDescribesSlide => Live is { } live && Show.Live?.Presentation?.Id == live.PresentationId
        && (live.ItemId is null || SelectedService?.Items.FirstOrDefault(i => i.Id == live.ItemId)?.ItemKind is not (ServiceItemKind.Media or ServiceItemKind.Audio or ServiceItemKind.Playlist));

    MediaItem? IAutoAdvanceHost.Media(string id) => Library.Get<MediaItem>(id);

    MediaTransportState? IAutoAdvanceHost.Transport(string mediaId) => LiveTransport(mediaId);

    bool IAutoAdvanceHost.IsMediaLive(string mediaId) => Show.LiveMedia.Values.Any(m => m.MediaId == mediaId);

    void IAutoAdvanceHost.FireSlide(Slide slide, Presentation presentation, string? arrangementId, string? contextId, int occurrence)
    {
        // Follow along in the slide grid, as Next does.
        if (contextId is not null && SelectedServiceItem?.Item.Id != contextId && ServiceItems.FirstOrDefault(r => r.Item.Id == contextId) is { } row)
            SelectedServiceItem = row;
        Fire(presentation, arrangementId, occurrence, contextId);
    }

    bool IAutoAdvanceHost.ServiceContains(string itemId) => SelectedService?.Items.Any(i => i.Id == itemId) == true;

    void IAutoAdvanceHost.FireNextServiceItem(string itemId) => FireNextServiceItem(itemId);

    /// <summary>
    /// The service moves on by itself (the last slide's timer, a media row's video ending): the next
    /// row that fires by itself, skipping headers, music and rows that can't fire (an empty
    /// presentation, missing media), as the Mac's fireNextServiceItem does.
    /// </summary>
    private void FireNextServiceItem(string afterItemId)
    {
        if (SelectedService is not { } service) return;
        var fireable = ServiceRunOrder.Fireable(service.Items);
        for (var next = ServiceRunOrder.NextFireable(fireable, afterItemId); next is not null; next = ServiceRunOrder.NextFireable(fireable, next.Id))
        {
            if (ServiceItems.FirstOrDefault(r => r.Item.Id == next.Id) is { } row && SelectedServiceItem != row) SelectedServiceItem = row;
            if (FireRow(next)) return;
            Log.Info($"Auto-advance: skipped \"{next.Name}\" (nothing to fire)");
        }
    }
}
