using MxU.Slides.Core.Control;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Infrastructure;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// Volunteer (run-only) mode (Phase 8): a locked-down way to run the show. Turned on in
/// Settings › Present, it adds Volunteer beside Show and Edit. While it's on, the main window
/// shows the volunteer layout (Views/Volunteer/VolunteerView.cs): the service, its slides as one
/// scroll, the live preview, Next and Previous, the clears and the panels the volunteer keeps.
/// Nothing that edits is in it, and every command that changes something asks <see cref="Permit"/>
/// first (the keys too: Views/MainWindow.Keys.cs), so nothing reaches an edit by another way.
/// Leaving needs this PC's PIN when one is set. The rules are Core's (<see cref="VolunteerMode"/>,
/// <see cref="RunOnly"/>); the Mac's version is VolunteerModeLogic.swift and PresentLayoutController.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Raised when volunteer mode starts or ends, or its settings change (the window swaps its layout; panels redraw).</summary>
    public event Action? VolunteerChanged;

    /// <summary>Volunteer mode is on: only running the show is allowed.</summary>
    public bool IsVolunteer => Settings.VolunteerModeOn;

    /// <summary>Settings › Present › Volunteer Mode: the switcher offers Volunteer.</summary>
    public bool VolunteerModeEnabled
    {
        get => Settings.VolunteerModeEnabled;
        set
        {
            if (Settings.VolunteerModeEnabled == value || !Permit(OperatorAction.ChangeSettings)) return;
            Settings.VolunteerModeEnabled = value;
            Settings.Save();
            OnPropertyChanged();
            VolunteerChanged?.Invoke();
        }
    }

    public bool HasVolunteerPin => !string.IsNullOrEmpty(Settings.VolunteerPinHash);

    /// <summary>The panels a volunteer keeps (Settings › Present › Panels volunteers keep).</summary>
    public IReadOnlyList<VolunteerPanel> VolunteerPanelsKept => VolunteerPanels.Parse(Settings.VolunteerPanels);

    public bool VolunteerKeeps(VolunteerPanel panel) => VolunteerPanelsKept.Contains(panel);

    public void SetVolunteerPanel(VolunteerPanel panel, bool kept)
    {
        if (!Permit(OperatorAction.ChangeSettings)) return;
        var panels = VolunteerPanelsKept.ToList();
        if (kept && !panels.Contains(panel)) panels.Add(panel);
        if (!kept) panels.Remove(panel);
        Settings.VolunteerPanels = VolunteerPanels.Format(panels);
        Settings.Save();
        VolunteerChanged?.Invoke();
    }

    /// <summary>Sets (or with null/empty, removes) the PIN that leaves volunteer mode. Returns what's wrong with it, or null when it was saved.</summary>
    public string? SetVolunteerPin(string? pin)
    {
        if (!Permit(OperatorAction.ChangeSettings)) return RunOnly.LockedNotice;
        if (string.IsNullOrEmpty(pin))
        {
            Settings.VolunteerPinHash = null;
        }
        else
        {
            if (VolunteerMode.PinProblem(pin) is { } problem) return problem;
            Settings.VolunteerPinHash = VolunteerMode.HashPin(pin);
        }
        Settings.Save();
        OnPropertyChanged(nameof(HasVolunteerPin));
        return null;
    }

    /// <summary>
    /// Starts volunteer mode: Show mode (an edit in progress is saved first), then the locked layout.
    /// Kept in this PC's settings, so it's still on after the app restarts.
    /// </summary>
    public void EnterVolunteer()
    {
        if (IsVolunteer) return;
        FlushPendingSave();
        IsEditing = false;
        Settings.VolunteerModeOn = true;
        Settings.Save();
        Log.Info("Volunteer mode on");
        OnPropertyChanged(nameof(IsVolunteer));
        OnPropertyChanged(nameof(CanChangeArrangement));
        VolunteerChanged?.Invoke();
        RefreshContinuous();
    }

    /// <summary>Leaves volunteer mode when the PIN matches (or there is none). False for a wrong PIN.</summary>
    public bool TryLeaveVolunteer(string? pin)
    {
        if (!IsVolunteer) return true;
        if (!VolunteerMode.PinMatches(Settings.VolunteerPinHash, pin ?? ""))
        {
            Log.Info("Volunteer mode: a wrong PIN was typed");
            return false;
        }
        Settings.VolunteerModeOn = false;
        Settings.Save();
        Log.Info("Volunteer mode off");
        OnPropertyChanged(nameof(IsVolunteer));
        OnPropertyChanged(nameof(CanChangeArrangement));
        VolunteerChanged?.Invoke();
        RefreshContinuous();
        return true;
    }

    /// <summary>
    /// Whether an action may happen now. In volunteer mode a locked one doesn't, and the operator
    /// is told why, so a click that does nothing never looks like a fault.
    /// </summary>
    public bool Permit(OperatorAction action)
    {
        if (RunOnly.Allows(action, IsVolunteer)) return true;
        Notice = RunOnly.LockedNotice;
        Log.Info($"Volunteer mode: blocked {action}");
        return false;
    }

    /// <summary>
    /// Shows a deck from the library on its own, as picking it in the library does: its slides take
    /// the middle, ready to fire (the Mac's "Libraries volunteers keep", where Presentations is one).
    /// It doesn't add it to the service or change it. False when it's gone or the bin isn't kept.
    /// </summary>
    public bool PresentFromLibrary(string presentationId)
    {
        if (!Permit(OperatorAction.PlayFromLibrary) || Library.Get<Presentation>(presentationId) is not { } deck) return false;
        if (IsVolunteer && !VolunteerKeeps(VolunteerPanel.Presentations)) return false;
        SelectedServiceItem = null;
        Open(deck, null, null);
        return true;
    }

    /// <summary>F-key, MIDI and remote "Clear Inputs" (the Clear bar's Inputs button does the same).</summary>
    public void ClearInputsNow() => Clear(() => Show.Clear(ShowFunction.Inputs));
}
