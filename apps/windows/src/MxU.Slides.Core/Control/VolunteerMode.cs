using System.Security.Cryptography;
using System.Text;

namespace MxU.Slides.Core.Control;

/// <summary>
/// Volunteer mode: a locked-down way to run the show, for a volunteer at the desk who should be
/// able to click slides and clear the screen but not change the songs, the service or the screens.
/// Port of PresenterCore/VolunteerModeLogic.swift (the mode switcher's choices and what picking one
/// does) and the run-only rules spread through the Mac's views (RunOnlySweepTests checks they're
/// all asked); here they live in one table, <see cref="RunOnly.Allows"/>.
///
/// One Windows difference: the Mac retired its passcode, so any volunteer can leave by picking
/// Show or Edit. Windows offers an optional PIN, for a desk PC left with a volunteer alone. It's
/// stored on this PC only, as a salted hash, never the digits. With no PIN set, leaving works
/// as on the Mac.
/// </summary>
public static class VolunteerMode
{
    /// <summary>The mode switcher's choices (the Mac's Present, Edit, Scheduler, Volunteer; Windows has Show and Edit).</summary>
    public enum Choice { Show, Edit, Volunteer }

    /// <summary>What the switcher offers: Volunteer only once it's turned on in Settings › Present.</summary>
    public static IReadOnlyList<Choice> Choices(bool enabled) => enabled ? [Choice.Show, Choice.Edit, Choice.Volunteer] : [Choice.Show, Choice.Edit];

    /// <summary>The switcher says Volunteer while volunteer mode is on, whatever the mode under it.</summary>
    public static Choice Selected(bool editing, bool volunteerOn) => volunteerOn ? Choice.Volunteer : editing ? Choice.Edit : Choice.Show;

    public static string Title(this Choice choice) => choice switch
    {
        Choice.Show => "Show",
        Choice.Edit => "Edit",
        _ => "Volunteer",
    };

    /// <summary>What picking a choice does.</summary>
    public abstract record Action
    {
        private Action() { }

        public sealed record Nothing : Action;

        public sealed record SwitchMode(Choice Mode) : Action;

        public sealed record EnterVolunteer : Action;

        /// <summary>Leave volunteer mode, then switch to <paramref name="Then"/>; <paramref name="NeedsPin"/> when this PC has a PIN.</summary>
        public sealed record LeaveVolunteer(Choice Then, bool NeedsPin) : Action;
    }

    public static Action Picking(Choice choice, bool volunteerOn, bool hasPin) =>
        choice == Choice.Volunteer
            ? volunteerOn ? new Action.Nothing() : new Action.EnterVolunteer()
            : volunteerOn ? new Action.LeaveVolunteer(choice, hasPin) : new Action.SwitchMode(choice);

    // MARK: The PIN

    /// <summary>A PIN is 4 to 8 digits: easy to type on a booth keyboard, hard to guess by watching once.</summary>
    public static string? PinProblem(string pin) =>
        pin.Length is < 4 or > 8 ? "Use 4 to 8 digits."
        : !pin.All(char.IsAsciiDigit) ? "Use digits only."
        : null;

    /// <summary>
    /// The PIN as this PC stores it: "pbkdf2:&lt;iterations&gt;:&lt;salt&gt;:&lt;hash&gt;" (base64), so the digits
    /// themselves are never written to settings.json.
    /// </summary>
    public static string HashPin(string pin, byte[]? salt = null, int iterations = 100_000)
    {
        salt ??= RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2:{iterations}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    /// <summary>Whether a typed PIN is the stored one. No stored PIN: anything leaves. A damaged one never matches.</summary>
    public static bool PinMatches(string? stored, string typed)
    {
        if (string.IsNullOrEmpty(stored)) return true;
        var parts = stored.Split(':');
        if (parts.Length != 4 || parts[0] != "pbkdf2" || !int.TryParse(parts[1], out var iterations) || iterations < 1) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(typed), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            // Compared in constant time, so timing can't reveal how much of a guess was right.
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>
/// The panels and bins a volunteer keeps in volunteer mode (the Mac's "Tabs volunteers keep").
/// The live controls (preview, Next, Previous, the clears) are always there.
/// </summary>
public enum VolunteerPanel { Timers, Alerts, ServiceTracking, StageMessage, Overlays, Media, Music, Combos }

public static class VolunteerPanels
{
    public static readonly IReadOnlyList<VolunteerPanel> All = Enum.GetValues<VolunteerPanel>();

    /// <summary>What volunteers keep until the operator picks: everything a service needs.</summary>
    public static IReadOnlyList<VolunteerPanel> Defaults => All;

    public static string Title(this VolunteerPanel panel) => panel switch
    {
        VolunteerPanel.ServiceTracking => "Service Tracking",
        VolunteerPanel.StageMessage => "Stage Message",
        var other => other.ToString(),
    };

    /// <summary>"timers,alerts" as saved in settings.json; unknown names are skipped (the Mac's modules(from:)).</summary>
    public static List<VolunteerPanel> Parse(string? raw) =>
        raw is null ? [.. Defaults]
        : [.. raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => Enum.TryParse<VolunteerPanel>(name, ignoreCase: true, out var panel) ? (VolunteerPanel?)panel : null)
            .OfType<VolunteerPanel>()
            .Distinct()
            .Order()];

    public static string Format(IEnumerable<VolunteerPanel> panels) =>
        string.Join(",", panels.Distinct().Order().Select(p => char.ToLowerInvariant(p.ToString()[0]) + p.ToString()[1..]));
}

/// <summary>What an operator can do, as volunteer mode sees it.</summary>
public enum OperatorAction
{
    // Running the show: always allowed.
    FireSlide, NextPrevious, GoToSlide, StepServiceItem, SelectServiceItem, SelectService, ChooseServiceVersion, ChoosePlanTime,
    Clear, VideoTransport, StartPauseResetTimer, FireAlert, SendStageMessage, ToggleOverlay, FireMedia, PlayMusic, RunCombo,
    ShowCheatSheet, OpenPreviewWindow, ScrollAndView,

    // Changing things: locked.
    EditSlides, EditLibrary, EditService, ChangeArrangement, EditTimers, EditAlerts, EditCombos, ReorderBoards,
    ChangeScreens, ChangeLook, ChangeTransitions, ChangeSettings, ChangeShortcuts, Broadcast, ImportFiles, OpenBible, EditTimeline,
}

/// <summary>
/// Run-only mode's rules in one place: the show can be run (Next, Previous, clicking slides,
/// the clears, timers, alerts, overlays, media, music and combos), and nothing can be changed
/// (slides, the library, the service's rows, arrangements, boards, screens, looks, settings).
/// The Mac asks <c>runOnly</c> at each control; here each control asks <see cref="Allows"/>.
/// </summary>
public static class RunOnly
{
    public static bool Allows(OperatorAction action, bool runOnly) => !runOnly || IsRunningTheShow(action);

    /// <summary>The actions that only run the show (allowed in volunteer mode).</summary>
    public static bool IsRunningTheShow(OperatorAction action) => action < OperatorAction.EditSlides;

    /// <summary>What the app says when something is locked.</summary>
    public const string LockedNotice = "That's locked in Volunteer Mode. Leave Volunteer Mode (top right) to change it.";
}
