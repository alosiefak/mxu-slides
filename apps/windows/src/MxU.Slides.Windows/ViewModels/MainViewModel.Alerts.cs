using System.Windows.Threading;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Core.Stage;
using MxU.Slides.Core.Threading;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.Rendering;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// Alerts: putting one up (with its target, layer, theme and Flash or Persist), the Flash
/// blink, taking it down, and the saved alerts the Alerts panel lists (Views/Operator/AlertsPanel.cs).
/// Port of the alert half of the Mac's ServiceControls (fireAlert, show(alert:), dismissAlert,
/// syncFlashClock) and the AlertsModule's state.
/// </summary>
public sealed partial class MainViewModel
{
    private AlertLibrary? alertLibrary;

    /// <summary>Making, changing and deleting saved alerts, with their board (Core/Stage/AlertLibrary.cs).</summary>
    public AlertLibrary Alerts => alertLibrary ??= new AlertLibrary(Library);

    /// <summary>
    /// What the operator has typed into each alert's blanks (alert id → blank → text). Kept while the
    /// app runs, as the Mac keeps them while its module is open, so Send Update resends what's typed.
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> AlertBlanks { get; } = new(StringComparer.Ordinal);

    /// <summary>Raised when the alert up (or its visibility) changes, or a saved alert or the board does: the Alerts panel redraws.</summary>
    public event Action? AlertsChanged;

    /// <summary>The id of the alert up now (a saved alert's id, or "stage-message"), or null.</summary>
    public string? LiveAlertId => Show.LiveAlert?.Id;

    /// <summary>The timers' names: a {token} naming one is a live timer, not a blank to fill.</summary>
    public List<string> TimerNames => [.. Timers.Timers.Select(t => t.Name)];

    private string? shownAlertKey;

    private void InitializeAlerts()
    {
        // The audience band reads "{Sermon}" as the timer's digits, like the stage does.
        Show.AlertMessageResolver = message => LinkedText.ResolvedAlertMessage(message, new ConfidenceInfo { Timers = Timers.Timers }, DateTimeOffset.Now);
        Show.Changed += () =>
        {
            // Only the alert matters here; Show changes on every slide.
            var key = Show.LiveAlert is { } alert ? $"{alert.Id}|{alert.Message}|{Show.AlertIsLeaving}" : null;
            if (key == shownAlertKey) return;
            shownAlertKey = key;
            OnPropertyChanged(nameof(StageMessageStatus));
            SyncFlashClock();
            AlertsChanged?.Invoke();
        };
        Library.Changed += (type, _) =>
        {
            if (type != typeof(AlertPreset) && type != typeof(ControlBoard) && type != typeof(Theme)) return;
            if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
                dispatcher.BeginInvoke(() => AlertsChanged?.Invoke());
            else AlertsChanged?.Invoke();
        };
    }

    /// <summary>
    /// Puts an alert up (replacing any other), as the Mac's fireAlert does: an empty message does
    /// nothing; the theme only styles what reaches the audience; Flash starts the blink.
    /// </summary>
    public void FireAlert(string id, string message, AlertBehavior behavior, AlertTarget target, string? themeId = null, string? layer = null)
    {
        var trimmed = message.Trim();
        if (trimmed.Length == 0) return;
        // The theme only dresses what the audience sees; the stage always shows the plain band.
        var theme = target != AlertTarget.Confidence && !string.IsNullOrEmpty(themeId) ? Library.Get<Theme>(themeId) : null;
        Show.FireAlert(new CueAlert(trimmed, behavior, target) { Id = id, Theme = theme, Layer = layer }, ShowState.HostClock());
        liveAlertPresetId = Library.Get<AlertPreset>(id) is not null ? id : null;
        RenderOutputs(DefaultTransition());
        SyncFlashClock();
    }

    /// <summary>
    /// The Alerts panel's Go Live and Send Update: the saved alert with the blanks as typed now
    /// (timer tokens stay live: the outputs keep counting them).
    /// </summary>
    public void FireAlertPreset(AlertPreset preset) =>
        ShowAlertPreset(preset, AlertBlanks.GetValueOrDefault(preset.Id));

    /// <summary>Turn Off: takes the alert down (a themed one plays its Outs first), as F6 does.</summary>
    public void DismissAlert() => ClearAlertsCommand.Execute(null);

    /// <summary>The live alert blinks while it is a Flash one: on and off every half second, timed by the show clock.</summary>
    private ITimer? flashClock;

    private void SyncFlashClock()
    {
        var flashing = Show.LiveAlert?.Behavior == AlertBehavior.Flash;
        if (flashing && flashClock is null)
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            // The callback runs on the show clock's thread: it only hands the blink to the UI thread.
            flashClock = ShowTimeProvider.Shared.CreateTimer(_ => dispatcher.BeginInvoke(FlashBeat), null,
                TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(0.5));
        }
        else if (!flashing && flashClock is not null)
        {
            flashClock.Dispose();
            flashClock = null;
            if (!Show.AlertVisible)
            {
                Show.AlertVisible = true;
                RenderOutputs(SceneTransition.Cut, refreshOnly: true);
            }
        }
    }

    private void FlashBeat()
    {
        if (Show.LiveAlert?.Behavior != AlertBehavior.Flash)
        {
            SyncFlashClock();
            return;
        }
        Show.AlertVisible = !Show.AlertVisible;
        RenderOutputs(SceneTransition.Cut, refreshOnly: true);
    }
}
