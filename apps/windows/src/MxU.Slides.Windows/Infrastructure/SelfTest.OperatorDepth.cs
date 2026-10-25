using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Control;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Outputs;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.ViewModels;
using MxU.Slides.Windows.Views;
using MxU.Slides.Windows.Views.Keys;
using MxU.Slides.Windows.Views.Volunteer;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>
/// Phase 8's operator depth, driven the way the operator drives it: a song's arrangements (made,
/// typed, moved, picked per row and live), the continuous view, keyboard shortcuts (changed,
/// unbound, a clash, a combo's key, the cheat sheet), volunteer mode (the locked layout, the
/// panels it keeps, a wrong PIN and the right one), the service list's collapsing, several rows
/// at once and the bulk menu, the picker's Upcoming and Earlier, plan times, a board's drop line,
/// a 4:3 preview, the transition sliders and the Stage panel's folder submenus. Pictures p8o-…
/// </summary>
public sealed partial class SelfTest
{
    private const string DepthLyrics = """
        Verse 1
        Morning has broken
        Like the first morning

        Chorus
        Praise for the singing
        Praise for the morning

        Verse 2
        Sweet the rain's new fall
        Sunlit from heaven

        Bridge
        Mine is the sunlight
        Mine is the morning
        """;

    private async Task OperatorDepthCheck()
    {
        var song = SongEditing.Build(null, DepthLyrics, 2, new SongEditing.Details("Self-test Morning", "", null, "", ""), model.Settings.DefaultThemeId, "V1, C, V2, C");
        song.Id = "selftest-depth-song";
        model.Library.Save(song);
        var grace = model.Library.All<Presentation>().FirstOrDefault(p => p.Name == "Amazing Grace");
        var blue = model.MediaItems.FirstOrDefault(m => m.Name == "Deep Blue")?.Item;
        ServiceItem Row(string id, ServiceItemKind kind, string name, string refId, string? colour = null) =>
            new() { Id = id, ItemKind = kind, Name = name, RefId = refId, ColorHex = colour };
        var service = new Service
        {
            Id = "selftest-depth-service", Name = "Self-test operator depth", ServiceDate = ServicePicking.Today(DateTime.Now),
            Items =
            [
                Row("d-worship", ServiceItemKind.Header, "Worship", "", "#E5484DFF"),
                Row("d-morning", ServiceItemKind.Presentation, song.Name, song.Id),
                .. grace is null ? Array.Empty<ServiceItem>() : [Row("d-grace", ServiceItemKind.Presentation, grace.Name, grace.Id)],
                Row("d-word", ServiceItemKind.Header, "Word", "", "#3B82F6FF"),
                .. blue is null ? Array.Empty<ServiceItem>() : [Row("d-blue", ServiceItemKind.Media, blue.Name, blue.Id)],
            ],
        };
        model.Library.Save(service);
        model.RefreshServices();
        var before = model.SelectedService;
        model.SelectedService = model.Services.FirstOrDefault(s => s.Id == service.Id);
        await Delay(400);
        try
        {
            await ArrangementsCheck(song);
            await ContinuousCheck();
            await ShortcutsCheck();
            await VolunteerCheck();
            await ServiceListExtrasCheck(service);
            await OperatorExtrasCheck();
        }
        finally
        {
            model.ShowsContinuous = false;
            model.Settings.KeyMap = new KeyCommandMap();
            model.Settings.VolunteerModeOn = false;
            model.Settings.VolunteerModeEnabled = false;
            model.Settings.VolunteerPinHash = null;
            model.Settings.CollapsedHeaders.Clear();
            model.Settings.PlanTimes.Clear();
            model.Settings.Save();
            model.ClearAllCommand.Execute(null);
            model.SelectedService = before ?? model.Services.FirstOrDefault();
        }
    }

    // MARK: Arrangements

    private async Task ArrangementsCheck(Presentation song)
    {
        model.SelectedServiceItem = model.ServiceItems.FirstOrDefault(r => r.Item.Id == "d-morning");
        await Delay(400);
        var picker = Descendant<ArrangementPicker>(window);
        var strip = Descendant<ArrangementStrip>(window);
        Check("the song opens with its typed order as its default arrangement (Custom)", model.CurrentArrangement?.Name == "Custom" && model.Slides.Count == 8);
        Check("the header has the arrangement chip and the section pills",
            picker is { Visibility: Visibility.Visible } && strip?.PillNames.SequenceEqual(["Verse 1", "Chorus", "Verse 2", "Chorus"]) == true);
        var custom = model.CurrentArrangement!.Id;

        var made = model.NewArrangement();
        Check("New Arrangement starts from each section once, in slide order, and is picked",
            made is not null && model.CurrentArrangement?.Id == made.Id && made.SectionIds.Count == 4 && made.Name == "Arrangement 2");
        Check("typing an order sets its blocks (V1, C, V2, C x2, B, C)", made is not null && model.SetArrangementOrder(made.Id, "V1, C, V2, C x2, B, C"));
        await Delay(300);
        Check("the grid plays the new order, and the pills follow",
            model.Slides.Count == 14 && strip?.PillNames.SequenceEqual(["Verse 1", "Chorus", "Verse 2", "Chorus", "Chorus", "Bridge", "Chorus"]) == true);
        Check("the order reads back folded", made is not null && ArrangementEditing.OrderText(song, made.Id) == "Verse 1, Chorus, Verse 2, Chorus x2, Bridge, Chorus");
        Check("the service row remembers the arrangement it plays",
            model.Library.Get<Service>("selftest-depth-service")?.Items.FirstOrDefault(i => i.Id == "d-morning")?.ArrangementId == made?.Id
            && model.ServiceItems.FirstOrDefault(r => r.Item.Id == "d-morning")?.Detail == "Arrangement 2");
        await Step("p8o-01-arrangement-header", () => { }, settle: 500);
        var menu = picker?.Open();
        await Delay(400);
        if (menu is not null)
        {
            SaveElement(menu, "p8o-02-arrangement-menu");
            menu.IsOpen = false;
        }

        // The editor: its blocks, moving one, and the order text keeping step.
        var editor = ArrangementEditorWindow.Open(window, model);
        await Delay(700);
        if (editor is not null)
        {
            Check("the arrangement editor lists the blocks and the order", editor.BlockNames.Count == 7 && editor.OrderText.StartsWith("Verse 1, Chorus", StringComparison.Ordinal));
            model.MoveArrangementBlock(made!.Id, 5, 0); // the Bridge to the front
            await Delay(400);
            Check("moving a block moves it in the editor and in the order text",
                editor.BlockNames.FirstOrDefault() == "Bridge" && editor.OrderText.StartsWith("Bridge, Verse 1", StringComparison.Ordinal));
            Save(editor, "p8o-03-arrangement-editor");
            editor.Close();
        }
        var copy = made is null ? null : model.DuplicateArrangement(made.Id);
        Check("Duplicate makes a copy with the same blocks, picked", copy is { Name: "Arrangement 2 copy" } && copy.SectionIds.SequenceEqual(made!.SectionIds));

        // The song editor's order box reads the same arrangement.
        model.ChooseArrangement(custom);
        model.SetArrangementOrder(custom, "V1, C x2, V2");
        var lyrics = new LyricsEditorViewModel(model, model.CurrentPresentation);
        Check("the song editor's order box keeps step with the arrangement (Verse 1, Chorus x2, Verse 2)", lyrics.Order == "Verse 1, Chorus x2, Verse 2");

        // Picked live: the second chorus is up, then the arrangement changes under it.
        if (made is not null)
        {
            model.ChooseArrangement(made.Id);
            await Delay(200);
            var secondChorus = model.Slides.Where(t => t.Label == "Chorus").Skip(2).FirstOrDefault();
            if (secondChorus is not null) model.GoLiveCommand.Execute(secondChorus);
            await Delay(400);
            var liveSlide = model.Show.Live?.Slide.Id;
            model.ChooseArrangement(custom);
            await Delay(300);
            Check("picking another arrangement while the song is live keeps the slide and moves the position across",
                model.Live?.ArrangementId == custom && SlideSceneBuilder.ArrangedSlides(model.CurrentPresentation!, custom).ElementAtOrDefault(model.Live.Occurrence)?.Id == liveSlide
                && strip?.LivePill == "Chorus");
            model.NextSkippingBuildsCommand.Execute(null);
            await Delay(300);
            Check("…and Next walks the new order from there", model.Live?.ArrangementId == custom && model.Live.Occurrence > 0);
            model.ClearAllCommand.Execute(null);
        }
        if (copy is not null) model.DeleteArrangement(copy.Id);
        Check("deleting an arrangement takes it out of the chip's list", model.Arrangements.All(a => a.Id != copy?.Id));
    }

    // MARK: The continuous view

    private async Task ContinuousCheck()
    {
        model.ShowsContinuous = true;
        var view = window.ContinuousView;
        var cards = await Until(() => model.ContinuousCards.Count(c => c.IsSlides && c.Sections.Count > 0), n => n >= 1, 8000);
        Check("the continuous view shows the whole service as cards (headers, songs, media)",
            view.Visibility == Visibility.Visible && cards >= 1 && view.CardTitles.Contains("Worship") && view.CardTitles.Contains("Self-test Morning"));
        await Until(() => view.RealizedTiles, n => n > 0, 5000);
        await Step("p8o-04-continuous", () => { }, settle: 600);

        var card = model.ContinuousCards.FirstOrDefault(c => c.ItemId == "d-grace") ?? model.ContinuousCards.FirstOrDefault(c => c.IsSlides);
        await Until(() => card?.Tiles.Count() ?? 0, n => n > 2, 5000);
        var tile = card?.Tiles.ElementAtOrDefault(2);
        if (card is not null && tile is not null)
        {
            model.FireContinuous(card, tile);
            await Delay(500);
            Check("clicking a slide in the scroll takes it live from its row", model.Live is { } live && live.ItemId == card.ItemId && live.Occurrence == 2 && tile.IsLive);
            Check("its card says where the live slide is", card.Position.StartsWith("Slide 3 of", StringComparison.Ordinal) && card.IsLive);
            model.NextSkippingBuildsCommand.Execute(null);
            await Delay(400);
            Check("Next moves on in the scroll too", card.Tiles.ElementAtOrDefault(3)?.IsLive == true && !tile.IsLive);
            await Step("p8o-05-continuous-live", () => { }, settle: 500);
        }
        model.ClearAllCommand.Execute(null);
        model.ShowsContinuous = false;
        Check("Grid brings the one-presentation grid back", view.Visibility == Visibility.Collapsed);
    }

    // MARK: Keyboard shortcuts

    /// <summary>A key pressed in the main window, as Windows delivers it (no modifiers held).</summary>
    private void Press(Key key)
    {
        Keyboard.ClearFocus();
        if (PresentationSource.FromVisual(window) is not { } source) return;
        window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    }

    private async Task ShortcutsCheck()
    {
        var plate = model.Overlays.FirstOrDefault(o => o.Name == "Name + Title");
        if (plate is null)
        {
            Skip("keyboard shortcuts with an overlay", "the starter overlays are missing");
            return;
        }
        Check("the defaults have no clashes", model.KeyMap.ConflictedKeys().Count == 0);
        model.UpdateKeyMap(map => map.SetChord(new KeyChord("f8"), KeyCommand.ClearOverlays));
        model.ToggleOverlayCommand.Execute(plate);
        await Delay(300);
        Press(Key.F4);
        await Delay(200);
        Check("a command given another key no longer answers to its old one (F4)", model.IsOverlayLive(plate.Overlay.Id));
        Press(Key.F8);
        await Delay(2500);
        Check("…and answers to the new one (F8 clears the overlays)", !model.IsOverlayLive(plate.Overlay.Id));

        var toggle = new GeneratedKey(GeneratedKeyKind.OverlayToggle, plate.Overlay.Id);
        model.UpdateKeyMap(map => map.SetChord(new KeyChord("f9"), toggle));
        Press(Key.F9);
        await Delay(400);
        Check("an overlay can have its own key (F9 puts it up)", model.IsOverlayLive(plate.Overlay.Id));
        model.ClearOverlaysCommand.Execute(null);

        // A clash, shown in the window's Conflicts filter.
        model.UpdateKeyMap(map => map.SetChord(new KeyChord("f1"), KeyCommand.ClearMedia));
        Check("using F1 twice is a clash on both", model.KeyMap.ConflictedKeys().SetEquals([KeyCommand.ClearAll.Raw(), KeyCommand.ClearMedia.Raw()]));
        var shortcuts = ShortcutsWindow.Open(window, model);
        await Delay(800);
        if (shortcuts is not null)
        {
            Check("the shortcuts window lists the commands with their keys", shortcuts.RowTexts.Contains("Clear All: F1") && shortcuts.RowTexts.Contains("Next Slide: → or Space or Enter or PgDn or ↓"));
            Save(shortcuts, "p8o-06-shortcuts");
            shortcuts.ShowFilter("Conflicts");
            await Delay(300);
            Check("…and the Conflicts filter shows just the clash", shortcuts.RowTexts.SequenceEqual(["Clear All: F1", "Clear Media: F1"]));
            Save(shortcuts, "p8o-07-shortcuts-conflicts");
            shortcuts.Close();
        }
        model.UpdateKeyMap(map => map.ResetToDefault(KeyCommand.ClearMedia));

        // While typing, the show's keys stay out of the way.
        model.SelectedServiceItem = model.ServiceItems.FirstOrDefault(r => r.Item.Id == "d-morning");
        await Delay(300);
        model.GoLiveCommand.Execute(model.Slides.FirstOrDefault());
        await Delay(300);
        var at = model.Live?.Occurrence;
        var steps = model.Show.SlideAnimationStep;
        window.LibrarySearchBox.Focus();
        if (Keyboard.FocusedElement != window.LibrarySearchBox) Skip("→ while typing in a box doesn't move the show", "the window doesn't have the keyboard");
        else if (PresentationSource.FromVisual(window) is { } source)
        {
            window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Right) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Check("→ while typing in a box doesn't move the show", model.Live?.Occurrence == at && model.Show.SlideAnimationStep == steps);
        }
        Press(Key.Right);
        await Delay(300);
        Check("→ in the window moves the show on", model.Live?.Occurrence != at || model.Show.SlideAnimationStep != steps);

        window.RunCommand(KeyCommand.ShowShortcuts);
        await Delay(700);
        var sheet = Application.Current.Windows.OfType<CheatSheet>().FirstOrDefault();
        Check("? opens the cheat sheet, with the changed key", sheet?.Lines.Contains("Clear Overlays") == true && sheet.Lines.Contains("Toggle “Name + Title”"));
        if (sheet is not null)
        {
            Save(sheet, "p8o-08-cheat-sheet");
            sheet.Close();
        }
        model.ClearAllCommand.Execute(null);
        model.UpdateKeyMap(map => map.Chords.Clear());
    }

    // MARK: Volunteer mode

    private async Task VolunteerCheck()
    {
        model.VolunteerModeEnabled = true;
        Check("a PIN is refused unless it's 4 to 8 digits", model.SetVolunteerPin("12") is not null && model.SetVolunteerPin("2468") is null && model.HasVolunteerPin);
        Check("…and is kept as a hash, never the digits", model.Settings.VolunteerPinHash?.Contains("2468") == false);
        var button = Descendant<VolunteerModeButton>(window);
        Check("Volunteer appears beside Show and Edit once it's turned on", button is not null);
        await Step("p8o-09-toolbar-volunteer", () => { }, settle: 300);

        model.IsEditing = true;
        model.EnterVolunteer();
        await Delay(900);
        var view = VolunteerLayout.Current(window);
        Check("volunteer mode shows the locked layout, in Show mode", view is not null && !model.IsEditing && model.Settings.VolunteerModeOn);
        Check("…with the live preview moved into it", view is not null && ReferenceEquals(model.AudiencePreview, view.Preview));
        await Until(() => model.ContinuousCards.Count, n => n > 0, 5000);
        model.SelectedServiceItem = model.ServiceItems.FirstOrDefault(r => r.Item.Id == "d-morning");
        await Delay(300);
        var card = model.ContinuousCards.FirstOrDefault(c => c.ItemId == "d-morning");
        await Until(() => card?.Tiles.Count() ?? 0, n => n > 1, 5000);
        if (card?.Tiles.ElementAtOrDefault(1) is { } tile) model.FireContinuous(card, tile);
        await Delay(500);
        Check("a volunteer can take a slide live and clear it", AudienceText().Contains("Morning has broken") || AudienceText().Contains("Praise for"));
        Check("…but can't edit: a new arrangement is refused, with a notice",
            model.NewArrangement() is null && model.Notice == RunOnly.LockedNotice && !model.Permit(OperatorAction.EditService));
        Press(Key.Right);
        await Delay(300);
        Check("the show keys still work (→)", model.Live is not null);
        await Step("p8o-10-volunteer-layout", () => { }, settle: 700);

        Check("settings can't be changed in volunteer mode", model.SetVolunteerPin(null) is not null && model.HasVolunteerPin);

        view?.Leave("1111");
        await Delay(300);
        Check("a wrong PIN leaves volunteer mode on", model.IsVolunteer && VolunteerLayout.Current(window) is not null);
        view?.Leave("2468");
        await Delay(600);
        Check("the right PIN goes back to the usual window, as it was", !model.IsVolunteer && VolunteerLayout.Current(window) is null
            && ReferenceEquals(model.AudiencePreview, window.AudiencePreview));
        model.ClearAllCommand.Execute(null);

        // Fewer panels: the volunteer layout shows only the ones kept.
        model.SetVolunteerPanel(VolunteerPanel.Alerts, false);
        model.SetVolunteerPanel(VolunteerPanel.Music, false);
        model.EnterVolunteer();
        await Delay(800);
        view = VolunteerLayout.Current(window);
        Check("the panels volunteers keep are the only ones shown", view is not null && !view.ShownPanels.Contains("Alerts") && !view.ShownPanels.Contains("Music")
            && view.ShownPanels.Contains("Timers") && view.ShownPanels.Contains("Overlays"));
        await Step("p8o-11-volunteer-fewer-panels", () => { }, settle: 400);
        view?.Leave("2468");
        await Delay(500);

        var settings = new SettingsWindow(model, model.Outputs!) { Owner = window };
        settings.Show();
        await Delay(900);
        if (Descendant<OperatorSettingsSection>(settings) is { } section)
        {
            section.BringIntoView();
            await Delay(400);
            Check("Settings › Present has Volunteer Mode and Keyboard Shortcuts", true);
            Save(settings, "p8o-12-settings-volunteer");
        }
        else Check("Settings › Present has Volunteer Mode and Keyboard Shortcuts", false);
        settings.Close();
        model.SetVolunteerPin(null);
        model.SetVolunteerPanel(VolunteerPanel.Alerts, true);
        model.SetVolunteerPanel(VolunteerPanel.Music, true);
        model.VolunteerModeEnabled = false;
    }

    // MARK: The service list

    private async Task ServiceListExtrasCheck(Service service)
    {
        model.SetHeaderCollapsed("d-worship", true);
        await Delay(300);
        var folded = model.ServiceItems.Where(r => r.IsCollapsedAway).Select(r => r.Item.Id).ToList();
        Check("collapsing a header folds its rows away", folded.Contains("d-morning") && !folded.Contains("d-word")
            && model.ServiceItems.First(r => r.Item.Id == "d-worship").IsCollapsed);
        Check("…kept on this PC", model.Settings.CollapsedHeaders.GetValueOrDefault(service.Id)?.Contains("d-worship") == true);
        await Step("p8o-13-service-collapsed", () => { }, settle: 400);
        // A folded row goes live (MIDI's "select service item", say): its group opens.
        model.FireServiceItemCommand.Execute(model.ServiceItems.First(r => r.Item.Id == "d-morning"));
        await Delay(400);
        Check("going live on a folded row opens its group", !model.ServiceItems.First(r => r.Item.Id == "d-morning").IsCollapsedAway);
        model.ClearAllCommand.Execute(null);

        // Several rows at once.
        var list = window.ServiceList;
        list.SelectedItems.Clear();
        foreach (var id in new[] { "d-morning", "d-grace" })
            if (model.ServiceItems.FirstOrDefault(r => r.Item.Id == id) is { } row) list.SelectedItems.Add(row);
        await Delay(200);
        Check("Ctrl-click chooses several rows", model.ChosenServiceRows.Count == 2 || model.ServiceItems.All(r => r.Item.Id != "d-grace"));
        if (list.ContextMenu is { } rowMenu && model.ChosenServiceRows.Count == 2)
        {
            model.SelectedServiceItem = model.ChosenServiceRows[0];
            ServiceMenus.AddItemOptions(rowMenu, model);
            rowMenu.PlacementTarget = list;
            rowMenu.IsOpen = true;
            await Delay(400);
            Check("the right-click menu becomes the bulk menu", rowMenu.Items.OfType<MenuItem>().Any(i => i.Header as string == "2 Items" && i.Visibility == Visibility.Visible)
                && rowMenu.Items.OfType<MenuItem>().Where(i => i.Header as string == "Rename…").All(i => i.Visibility == Visibility.Collapsed));
            SaveElement(rowMenu, "p8o-14-service-bulk-menu");
            rowMenu.IsOpen = false;
            var ids = model.ServiceBatch(model.ChosenServiceRows[0].Item.Id);
            model.SetServiceTransition(ids, new SongTransition { Kind = "crossfade", Seconds = 5 });
            await Delay(300);
            Check("bulk Transition gives both songs a crossfade", model.Library.Get<Service>(service.Id)!.Items.Count(i => i.Transition?.Kind == "crossfade") == 2);
            model.HideServiceRows(ids);
            await Delay(300);
            Check("bulk Hide hides both", model.HiddenServiceItems.Count == 2);
            foreach (var id in ids) model.SetServiceItemHidden(id, false);
            await Delay(300);
        }

        // The picker: today's service first, older ones under Earlier.
        foreach (var (id, days) in new[] { ("selftest-depth-old", -14), ("selftest-depth-next", 7) })
            model.Library.Save(new Service { Id = id, Name = days < 0 ? "Self-test two weeks ago" : "Self-test next week", ServiceDate = ServicePicking.Today(DateTime.Now.AddDays(days)) });
        model.RefreshServices(keepSelection: true);
        var groups = model.ServicePickerGroups();
        Check("the picker puts upcoming services first, in date order, and older ones under Earlier",
            groups.Current.Any(s => s.Id == "selftest-depth-next") && groups.Earlier.Any(s => s.Id == "selftest-depth-old")
            && groups.Current.ToList().FindIndex(s => s.Id == service.Id) < groups.Current.ToList().FindIndex(s => s.Id == "selftest-depth-next"));
        if (Descendant<ServicePicker>(window) is { } picker && picker.OpenWithEarlier() is { } popup)
        {
            await Delay(500);
            if (popup.Child is FrameworkElement shown) SaveElement(shown, "p8o-15-service-picker");
            popup.IsOpen = false;
        }

        // Plan times: 11:00 leaves Amazing Grace out and runs the Word first.
        var planned = MxuJsonCloneService(service.Id);
        if (planned is not null)
        {
            foreach (var item in planned.Items) item.MxuItemHexId = "h-" + item.Id;
            if (planned.Items.FirstOrDefault(i => i.Id == "d-grace") is { } graceRow) graceRow.ExcludedTimeHexIds = ["eleven"];
            planned.MxuPlanTimes =
            [
                new MxUPlanTime { HexId = "nine", Name = "9:00", StartsAt = "2026-07-19T09:00:00Z" },
                new MxUPlanTime { HexId = "eleven", Name = "11:00", StartsAt = "2026-07-19T11:00:00Z" },
            ];
            planned.MxuTimeOrders = [new MxUPlanTimeOrder { TimeHexId = "eleven", ItemHexIds = ["h-d-word", "h-d-blue", "h-d-worship", "h-d-morning", "h-d-grace"] }];
            model.Library.Save(planned);
            await Delay(300);
            model.ChoosePlanTime("eleven");
            await Delay(400);
            var order = model.ServiceItems.Select(r => r.Item.Id).ToList();
            Check("a plan time runs its own rows in its own order", order.FirstOrDefault() == "d-word" && !order.Contains("d-grace"));
            Check("…kept on this PC", model.Settings.PlanTimes.GetValueOrDefault(service.Id) == "eleven");
            if (Descendant<ServicePicker>(window) is { } timePicker && timePicker.OpenTimes() is { } times)
            {
                await Delay(400);
                SaveElement(times, "p8o-16-plan-times");
                times.IsOpen = false;
            }
            await Step("p8o-17-plan-time-rows", () => { }, settle: 300);
            model.ChoosePlanTime(null);
            await Delay(300);
            Check("All times runs every row again", model.ServiceItems.Count == planned.Items.Count(i => i.HiddenInPresenter != true));
        }
        model.Library.Delete<Service>("selftest-depth-old");
        model.Library.Delete<Service>("selftest-depth-next");
    }

    private Service? MxuJsonCloneService(string id) => model.Library.Get<Service>(id) is { } found ? Core.Json.MxuJson.Clone(found) : null;

    // MARK: Boards, the preview, the transition sliders and the Stage panel

    private async Task OperatorExtrasCheck()
    {
        // Boards: a drag's line over a timer row, and a drop's move.
        var timers = Descendant<TimersPanel>(window);
        var sermon = model.Timers.AddCountdown("Self-test Sermon", 1800);
        var walkIn = model.Timers.AddCountdown("Self-test Walk-in", 600);
        await Delay(400);
        if (timers is not null && FirstDescendant<Border>(timers, b => b.AllowDrop && b.ActualHeight > 20) is { } card)
        {
            var line = DropLine.Show(card, DropEdge.Top);
            await Delay(200);
            SaveElement(timers, "p8o-18-timers-drop-line");
            line?.Remove();
            Check("timer rows take drops", true);
        }
        else Check("timer rows take drops", false);
        model.Timers.Apply(BoardDrops.Resolve(new BoardDrag.Item(walkIn), new BoardDropTarget.BeforeRow(sermon))!);
        Check("dropping Walk-in above Sermon moves it there", model.Timers.Board.AllTimerIds is var order && order.IndexOf(walkIn) == order.IndexOf(sermon) - 1);
        model.Timers.Remove(sermon);
        model.Timers.Remove(walkIn);

        // The preview keeps a 4:3 screen's shape.
        var config = model.Outputs!.Config;
        var previewBefore = model.Settings.PreviewTarget;
        var square = config.AddScreen("Self-test 4:3", 1024, 768);
        try
        {
            if (square is not null)
            {
                model.SetPreviewTarget(SwiftUuid.Format(square.Id));
                await Delay(500);
                var preview = window.AudiencePreview;
                Check("the live panel's preview takes a 4:3 screen's shape", preview.ActualWidth > 0 && Math.Abs(preview.ActualHeight - preview.ActualWidth * 0.75) < 2);
                await Step("p8o-19-preview-4x3", () => { }, settle: 300);
            }
        }
        finally
        {
            if (square is not null) config.RemoveScreen(square.Id);
            var audience = config.Screens.FirstOrDefault(s => config.Role(s.Id) == ScreenRole.Audience);
            model.SetPreviewTarget(previewBefore ?? (audience is null ? "" : SwiftUuid.Format(audience.Id)));
        }
        await Delay(300);
        Check("…and 16:9 again for a 16:9 screen", Math.Abs(window.AudiencePreview.ActualHeight - window.AudiencePreview.ActualWidth * 9 / 16) < 2);

        // The transition chips' sliders.
        if (Descendant<LiveToolbar>(window) is { } toolbar)
        {
            var transition = model.RoomTransitionSetting(media: false);
            model.SetRoomTransition(media: false, OutputTransition.Dissolve, 0.5);
            await Delay(200);
            toolbar.LengthSliderFor(media: false).Value = 1.2;
            await Delay(200);
            Check("the slide chip's slider sets the room default's length", Math.Abs(model.Settings.TransitionSeconds - 1.2) < 0.01 && toolbar.SlidesText == "Dissolve 1.2 s");
            SaveElement(toolbar, "p8o-20-transition-sliders");
            model.SetRoomTransition(media: false, transition.Kind, transition.Seconds);
        }

        // The Stage panel's layouts in folders.
        var made = new List<string>();
        if (Descendant<StagePanel>(window) is { } stage && model.StageLayouts.FirstOrDefault() is { } template
            && config.Screens.FirstOrDefault(s => config.Role(s.Id) == ScreenRole.Confidence) is { } stageScreen)
        {
            foreach (var (name, folder) in new[] { ("Self-test Band", "Band"), ("Self-test Keys", "Band/Keys"), ("Self-test Pastor", "Pulpit") })
            {
                var layout = Core.Json.MxuJson.Clone(template);
                layout.Id = $"selftest-{name}";
                layout.Name = name;
                layout.Folder = folder;
                model.Library.Save(layout);
                made.Add(layout.Id);
            }
            await Delay(500);
            var menu = stage.LayoutMenu(stageScreen.Id, null);
            var folders = menu.Items.OfType<MenuItem>().Where(i => i.Items.Count > 0).Select(i => i.Tag as string).ToList();
            Check("the Stage panel's layout menu has a submenu per folder (Band, Pulpit)", folders.Contains("Band") && folders.Contains("Pulpit"));
            var band = menu.Items.OfType<MenuItem>().FirstOrDefault(i => i.Tag as string == "Band");
            Check("…with folders inside folders (Band › Keys)", band?.Items.OfType<MenuItem>().Any(i => i.Tag as string == "Keys") == true);
            OperatorKit.Open(menu, stage);
            await Delay(300);
            if (band is not null)
            {
                band.IsSubmenuOpen = true;
                await Delay(300);
            }
            SaveElement(menu, "p8o-21-stage-folders");
            menu.IsOpen = false;
        }
        foreach (var id in made) model.Library.Delete<ConfidenceLayout>(id);
    }

    private static T? FirstDescendant<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found && match(found)) return found;
            if (FirstDescendant(child, match) is { } deeper) return deeper;
        }
        return null;
    }
}
