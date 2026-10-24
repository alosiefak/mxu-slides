using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Stage;

/// <summary>
/// Making, changing and deleting saved alerts in the library, and keeping the alert board
/// (the folders and order the Alerts panel shows) in step. Port of the alert methods of the
/// Mac's AppModel (createAlertPreset, updateAlertPreset, alertBoard, updateAlertBoard).
/// Imported ProPresenter messages are alert presets too, so they join the board like any other.
/// <para>
/// As in <see cref="AutomationLibrary"/>, every change is made to a copy of the document and
/// the copy is saved: Swift's documents are values, so nobody holding the old one sees a half-made change.
/// </para>
/// </summary>
public sealed class AlertLibrary(LibraryStore store)
{
    /// <summary>What "New Alert" writes: a phrase with one fill-in blank, flashing on the confidence monitors.</summary>
    public const string NewAlertName = "New Alert";
    public const string NewAlertMessage = "New alert {message}";
    public const string NewFolderName = "New Folder";

    public LibraryStore Store { get; } = store;

    private List<string> AlertIds() => [.. Store.All<AlertPreset>().Select(a => a.Id)];

    /// <summary>
    /// The alert board: the stored folders and order, with alerts that have gone removed and
    /// new ones (made here, imported, or sent over the Local API) added at the end. Reading it never saves.
    /// </summary>
    public ControlBoard AlertBoard()
    {
        var board = Store.Get<ControlBoard>(ControlBoardLogic.AlertBoardId) is { } stored
            ? MxuJson.Clone(stored)
            : new ControlBoard { Id = ControlBoardLogic.AlertBoardId };
        board.Reconcile(AlertIds());
        return board;
    }

    /// <summary>Changes the alert board (reconciled first, so every alert is on it) and saves it.</summary>
    public void UpdateAlertBoard(Action<ControlBoard> mutate)
    {
        var board = AlertBoard();
        mutate(board);
        Store.Save(board);
    }

    /// <summary>A new alert named after its phrase; null when the phrase trims to nothing. Confidence is the default and isn't stored.</summary>
    public string? CreateAlertPreset(string message, AlertBehavior behavior, AlertTarget target, string? themeId, Func<string>? newId = null)
    {
        var trimmed = message.TrimmingWhitespacesAndNewlines();
        if (trimmed.Length == 0) return null;
        var id = (newId ?? LibraryStore.NewId)();
        Store.Save(new AlertPreset
        {
            Id = id, Name = trimmed, Message = trimmed, Behavior = behavior,
            Target = target == AlertTarget.Confidence ? null : target,
            ThemeId = themeId,
        });
        return id;
    }

    /// <summary>The add menu's New Alert: write the phrase once, fill the blank at fire time.</summary>
    public string? CreateNewAlert(Func<string>? newId = null)
    {
        var id = CreateAlertPreset(NewAlertMessage, AlertBehavior.Flash, AlertTarget.Confidence, null, newId);
        if (id is not null) UpdateAlertPreset(id, a => a.Name = NewAlertName);
        return id;
    }

    /// <summary>Changes an alert (a copy of it) and saves it; false when there's no such alert.</summary>
    public bool UpdateAlertPreset(string id, Action<AlertPreset> mutate)
    {
        if (Store.Get<AlertPreset>(id) is not { } stored) return false;
        var preset = MxuJson.Clone(stored);
        mutate(preset);
        if (MxuJson.StructurallyEqual(preset, stored)) return true;
        Store.Save(preset);
        return true;
    }

    /// <summary>Deletes an alert (to the library's Trash) and takes it off the board.</summary>
    public void DeleteAlertPreset(string id)
    {
        if (Store.Get<AlertPreset>(id) is null) return;
        Store.Delete<AlertPreset>(id);
        if (Store.Get<ControlBoard>(ControlBoardLogic.AlertBoardId) is not null) UpdateAlertBoard(_ => { });
    }

    /// <summary>The add menu's New Folder (an empty folder at the end of the board); returns its id.</summary>
    public string AddFolder(string name = NewFolderName, string? id = null)
    {
        string? made = null;
        UpdateAlertBoard(board => made = board.AddFolder(name, id));
        return made!;
    }
}
