using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Core.Stage;

namespace MxU.Slides.Core.Tests.Stage;

// The Alerts module's library half (the Mac's AppModel createAlertPreset, updateAlertPreset,
// alertBoard, updateAlertBoard) on a real library in a throwaway folder, and the module's rules
// (AlertsModule.swift, AlertInlineEditor).
public sealed class AlertLibraryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mxu-alert-tests-" + Guid.NewGuid().ToString("N"));
    private readonly LibraryStore store;
    private readonly AlertLibrary alerts;

    public AlertLibraryTests()
    {
        store = new LibraryStore(root);
        store.Load();
        alerts = new AlertLibrary(store);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
    }

    private LibraryStore Reopened()
    {
        var again = new LibraryStore(root);
        again.Load();
        return again;
    }

    [Fact]
    public void ANewAlertIsNamedAfterItsPhraseAndStoresConfidenceAsAbsent()
    {
        Assert.Null(alerts.CreateAlertPreset("  \n ", AlertBehavior.Persist, AlertTarget.Confidence, null));
        var id = alerts.CreateAlertPreset("  Car {plate}  ", AlertBehavior.Persist, AlertTarget.Confidence, null, () => "a1");
        var saved = Reopened().Get<AlertPreset>("a1")!;
        Assert.Equal(("a1", "Car {plate}", "Car {plate}", (AlertTarget?)null), (id, saved.Name, saved.Message, saved.Target));
        alerts.CreateAlertPreset("Kids", AlertBehavior.Flash, AlertTarget.Both, "theme-1", () => "a2");
        Assert.Equal((AlertTarget.Both, "theme-1"), (store.Get<AlertPreset>("a2")!.Target, store.Get<AlertPreset>("a2")!.ThemeId));
    }

    [Fact]
    public void NewAlertWritesAPhraseWithABlankThatFlashes()
    {
        var id = alerts.CreateNewAlert(() => "n1")!;
        var preset = store.Get<AlertPreset>(id)!;
        Assert.Equal(("New Alert", "New alert {message}", AlertBehavior.Flash, (AlertTarget?)null), (preset.Name, preset.Message, preset.Behavior, preset.Target));
        Assert.Equal(["message"], AlertEditing.FillInTokens(preset.Message, []));
    }

    [Fact]
    public void TheBoardListsEveryAlertWithoutSavingUntilItChanges()
    {
        alerts.CreateAlertPreset("Walk-out", AlertBehavior.Persist, AlertTarget.Confidence, null, () => "b");
        alerts.CreateAlertPreset("Announcements", AlertBehavior.Persist, AlertTarget.Confidence, null, () => "a");
        Assert.Equal(["a", "b"], alerts.AlertBoard().Nodes);
        Assert.Null(store.Get<ControlBoard>(ControlBoardLogic.AlertBoardId));

        var folder = alerts.AddFolder("Nursery", "f");
        alerts.UpdateAlertBoard(board => board.MoveItemIntoFolder("b", folder));
        var saved = Reopened().Get<ControlBoard>(ControlBoardLogic.AlertBoardId)!;
        Assert.Equal(["a", "f"], saved.Nodes);
        Assert.Equal(["b"], saved.Folder("f")?.ItemIds);

        // An alert from somewhere else (a ProPresenter import, the Local API) joins at the end.
        store.Save(new AlertPreset { Id = "imported", Name = "Parking", Message = "Car {plate}", Behavior = AlertBehavior.Persist });
        Assert.Equal(["a", "b", "imported"], alerts.AlertBoard().AllItemIds());
    }

    [Fact]
    public void UpdatingChangesACopyAndSkipsSavesThatChangeNothing()
    {
        alerts.CreateAlertPreset("Car", AlertBehavior.Persist, AlertTarget.Confidence, null, () => "c");
        var held = store.Get<AlertPreset>("c")!;
        var saves = 0;
        store.Changed += (_, _) => saves++;
        Assert.True(alerts.UpdateAlertPreset("c", p => p.Behavior = AlertBehavior.Flash));
        Assert.Equal(AlertBehavior.Persist, held.Behavior); // whoever held the old one isn't changed under them
        Assert.Equal(AlertBehavior.Flash, store.Get<AlertPreset>("c")!.Behavior);
        Assert.True(alerts.UpdateAlertPreset("c", p => p.Behavior = AlertBehavior.Flash));
        Assert.Equal(1, saves); // the same value again isn't saved again
        Assert.False(alerts.UpdateAlertPreset("missing", _ => { }));
    }

    [Fact]
    public void DeletingTakesTheAlertOffTheBoard()
    {
        alerts.CreateAlertPreset("A", AlertBehavior.Persist, AlertTarget.Confidence, null, () => "a");
        alerts.CreateAlertPreset("B", AlertBehavior.Persist, AlertTarget.Confidence, null, () => "b");
        alerts.UpdateAlertBoard(board => board.MoveItemIntoFolder("a", board.AddFolder("F", "f")));
        alerts.DeleteAlertPreset("a");
        Assert.Null(store.Get<AlertPreset>("a"));
        var saved = Reopened().Get<ControlBoard>(ControlBoardLogic.AlertBoardId)!;
        Assert.Empty(saved.Folder("f")!.ItemIds);
        Assert.Equal(["b", "f"], saved.Nodes);
    }

    // MARK: AlertEditing (the module's rules)

    [Fact]
    public void LocationsReadAsOnTheMac()
    {
        Assert.Equal("Confidence", AlertEditing.LocationCaption(AlertTarget.Confidence));
        Assert.Equal("Audience", AlertEditing.LocationCaption(AlertTarget.Audience));
        Assert.Equal("Confidence + Audience", AlertEditing.LocationCaption(AlertTarget.Both));
        Assert.Equal("Confidence Monitors", AlertEditing.LocationTitle(AlertTarget.Confidence));
        Assert.Equal("Audience Screens", AlertEditing.LocationTitle(AlertTarget.Audience));
        Assert.Equal("Confidence and Audience", AlertEditing.LocationTitle(AlertTarget.Both));
        Assert.Equal([AlertTarget.Both, AlertTarget.Confidence, AlertTarget.Audience], AlertEditing.LocationChoices);

        var preset = new AlertPreset { Id = "p", Name = "P", Message = "m" };
        Assert.Equal(AlertTarget.Confidence, AlertEditing.Target(preset));
        AlertEditing.SetTarget(preset, AlertTarget.Audience);
        Assert.Equal(AlertTarget.Audience, preset.Target);
        AlertEditing.SetTarget(preset, AlertTarget.Confidence);
        Assert.Null(preset.Target); // the default is stored as absent
        Assert.False(AlertEditing.ShowsLayerAndTheme(AlertTarget.Confidence));
        Assert.True(AlertEditing.ShowsLayerAndTheme(AlertTarget.Both));
    }

    [Fact]
    public void LayerAndThemeChoices()
    {
        Assert.Equal(LayerKind.Alerts, AlertEditing.LayerChoices[0]); // top layer first
        Assert.Equal(LayerKind.VideoInput, AlertEditing.LayerChoices[^1]);
        var preset = new AlertPreset { Id = "p", Name = "P", Message = "m" };
        Assert.Equal(LayerKind.Alerts, AlertEditing.AssignedLayer(preset));
        AlertEditing.SetLayer(preset, LayerKind.Overlays);
        Assert.Equal("overlays", preset.Layer);
        AlertEditing.SetLayer(preset, LayerKind.Alerts);
        Assert.Null(preset.Layer); // the Alerts layer is the default, stored as absent

        Assert.Equal("Banner", AlertEditing.ThemeName(preset, _ => "never asked"));
        preset.ThemeId = "t";
        Assert.Equal("Sunday", AlertEditing.ThemeName(preset, id => id == "t" ? "Sunday" : null));
        Assert.Equal("Banner", AlertEditing.ThemeName(preset, _ => null)); // a theme that's gone reads as the banner
        Assert.Equal("Flash", AlertEditing.BehaviorName(AlertBehavior.Flash));
        Assert.Equal("Persist", AlertEditing.BehaviorName(AlertBehavior.Persist));
    }

    [Fact]
    public void BlanksLeaveTimersLiveAndComposeTheMessage()
    {
        string[] timers = ["Sermon", "Walk-in"];
        Assert.Equal(["plate"], AlertEditing.FillInTokens("Car {plate}: {sermon} left", timers)); // a timer's name, any case, is no blank
        Assert.Equal("Car KD21: {Sermon} left",
            AlertEditing.Compose("Car {plate}: {Sermon} left", new Dictionary<string, string> { ["PLATE"] = "KD21", ["Sermon"] = "typed over" }, timers));
        Assert.Equal("Car : {Sermon} left", AlertEditing.Compose("Car {plate}: {Sermon} left", null, timers)); // an unfilled blank goes out empty
    }

    [Fact]
    public void TheEditorNeverBlanksAnAlert()
    {
        var preset = new AlertPreset { Id = "p", Name = "Car", Message = "Car {plate}" };
        AlertEditing.Commit(preset, "   ", " \n ");
        Assert.Equal(("Car", "Car {plate}"), (preset.Name, preset.Message));
        AlertEditing.Commit(preset, "  Parking ", "  Please move {plate}\n");
        Assert.Equal(("Parking", "Please move {plate}"), (preset.Name, preset.Message));
        Assert.Equal("Turn Off", AlertEditing.LiveButtonTitle(true));
        Assert.Equal("Go Live", AlertEditing.LiveButtonTitle(false));
    }
}
