using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>Keyboard jumps: group hot keys (C = Chorus…) and typing a slide number then Enter.</summary>
public sealed partial class MainViewModel
{
    private readonly Dictionary<string, List<string>> hotKeyTargets = HotKeyJump.HotKeyTargets(StarterContent.GroupPalette);

    /// <summary>Jumps the live song (or the open one) to the next block of the section on this key. False when nothing matches.</summary>
    public bool FireHotKey(string letter)
    {
        if (!hotKeyTargets.TryGetValue(letter.ToLowerInvariant(), out var names)) return false;
        var (presentation, arrangementId, live, itemId) = Show.Live?.Presentation is { } livePresentation && Live is not null
            ? (livePresentation, Live.ArrangementId, (int?)Live.Occurrence, Live.ItemId)
            : (CurrentPresentation, SelectedArrangement?.Id, null, CurrentContextItemId);
        if (presentation is null || HotKeyJump.JumpStart(presentation, arrangementId, names, live) is not { } start) return false;
        Fire(presentation, arrangementId, start, itemId);
        return true;
    }

    [ObservableProperty]
    public partial string GoToBuffer { get; set; } = "";

    private DispatcherTimer? goToExpiry;

    public void AppendGoToDigit(char digit)
    {
        if (GoToBuffer.Length >= 4) return;
        GoToBuffer += digit;
        goToExpiry ??= new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Normal, (_, _) => CancelGoTo(), Dispatcher.CurrentDispatcher);
        goToExpiry.Stop();
        goToExpiry.Start();
    }

    public void CancelGoTo()
    {
        goToExpiry?.Stop();
        GoToBuffer = "";
    }

    /// <summary>Takes slide N of the live (or open) presentation live. True when a number was waiting.</summary>
    public bool CommitGoTo()
    {
        if (GoToBuffer.Length == 0) return false;
        var number = int.Parse(GoToBuffer);
        CancelGoTo();
        var (presentation, arrangementId, itemId) = Show.Live?.Presentation is { } livePresentation && Live is not null
            ? (livePresentation, Live.ArrangementId, Live.ItemId)
            : (CurrentPresentation, SelectedArrangement?.Id, CurrentContextItemId);
        if (presentation is null) return true;
        var count = SlideSceneBuilder.ArrangedSlides(presentation, arrangementId).Count;
        if (number >= 1 && number <= count) Fire(presentation, arrangementId, number - 1, itemId);
        else Notice = $"There's no slide {number} — this one has {count}.";
        return true;
    }
}
