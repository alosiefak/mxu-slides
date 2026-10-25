using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.Views;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>
/// The automation editors, driven the way their buttons drive them: make an action combo
/// with two actions in the Action Combos window and run it; give a slide actions in the
/// Slide Actions window and fire the slide; make a schedule trigger in the Scheduler and see
/// it listed with when it fires next. A picture of each window is saved.
/// </summary>
public sealed partial class SelfTest
{
    private async Task EditorsCheck()
    {
        var alert = new AlertPreset { Id = "selftest-editor-alert", Name = "Editor check", Message = "Made in the combo editor" };
        model.Library.Save(alert);
        try
        {
            await ComboEditorCheck(alert);
            await SlideActionsEditorCheck();
            await SchedulerEditorCheck(alert);
        }
        finally
        {
            model.ClearAllCommand.Execute(null);
            model.Library.Delete<AlertPreset>(alert.Id);
        }
    }

    /// <summary>Picks a command from an editor's Add Action menu by its titles, as a click would.</summary>
    private static SlideAction? FromMenu(ActionListEditor editor, params string[] path) =>
        ActionEditing.Find(editor.AddMenu(), path)?.Make?.Invoke();

    private async Task ComboEditorCheck(AlertPreset alert)
    {
        var overlay = model.Overlays.FirstOrDefault(o => o.Name == "Name + Title")?.Overlay;
        var combos = ActionCombosWindow.Open(window, model);
        await Delay(700);
        string? comboId = null;
        try
        {
            comboId = combos.NewCombo("Editor combo");
            Check("the combo editor makes a new combo and opens it", comboId is not null && combos.Editor is not null);
            if (comboId is null || combos.Editor is not { } editor) return;

            var fireAlert = FromMenu(editor, "Alert", alert.Name);
            var fireOverlay = overlay is null ? null : FromMenu(editor, "Overlay", overlay.Name);
            Check("the Add Action menu offers the alert and the overlay", fireAlert is not null && fireOverlay is not null);
            if (fireAlert is null || fireOverlay is null || overlay is null) return;
            editor.Append(fireOverlay);
            editor.Append(fireAlert);
            editor.Move(fireAlert.Id, -1);
            editor.Edit(fireOverlay.Id, a => ActionCombos.SetDelay(a, ActionEditing.ParseDelay("0.5") ?? 0), reshape: true);
            var saved = model.Library.Get<ActionCombo>(comboId);
            Check("the combo is saved with its two actions, in the order set (alert, then the overlay half a second later)",
                saved?.Actions is [{ Kind: SlideActionKind.FireAlert } first, { Kind: SlideActionKind.FireOverlay, DelaySeconds: 0.5 } second]
                && first.AlertId == alert.Id && second.OverlayId == overlay.Id);
            await Delay(400);
            Check("the board lists the combo with its step count",
                combos.RowTexts().Any(r => r.Contains("Editor combo", StringComparison.Ordinal) && r.Contains("2 actions", StringComparison.Ordinal)));
            Save(combos, "30-editor-combos-window");
            await AddMenuCheck(editor);

            // When the delayed step really fires is read off the router's trail: a fixed pause before looking
            // can outlast the half second on a slow PC, and make the overlay look early.
            double? delayedAfter = null;
            var ran = DateTime.UtcNow;
            void Noted(string crumb, string detail)
            {
                if (crumb == "actions.delayed.fired") delayedAfter ??= (DateTime.UtcNow - ran).TotalSeconds;
            }
            model.Actions.Noted += Noted;
            combos.Run(comboId);
            var upAtOnce = model.Show.LiveOverlays.Any(o => o.Id == overlay.Id);
            await Delay(200);
            Check("running the combo puts up its message at once", model.Show.AlertMessage == alert.Message);
            await WhenShown(() => model.Show.LiveOverlays.Any(o => o.Id == overlay.Id), 10);
            model.Actions.Noted -= Noted;
            Check($"the delayed overlay waits its half second (it fired after {delayedAfter:0.00} s)", !upAtOnce && delayedAfter >= 0.45);
            Check("then the overlay goes up", model.Show.LiveOverlays.Any(o => o.Id == overlay.Id));
            model.ClearAllCommand.Execute(null);
            await Delay(300);

            // A folder: made on the board, the combo moved in, and the window keeps up.
            model.Automation.UpdateComboBoard(board => board.PutInFolder(comboId, board.AddFolder("Self-test folder", "selftest-combo-folder")));
            await Delay(400);
            var rows = combos.RowTexts();
            var folderRow = rows.ToList().FindIndex(r => r.StartsWith("Self-test folder", StringComparison.Ordinal));
            Check("a combo moved into a folder shows under the folder", folderRow >= 0 && rows.Skip(folderRow + 1).Any(r => r.Contains("Editor combo", StringComparison.Ordinal)));
        }
        finally
        {
            combos.Close();
            if (comboId is not null) model.Automation.DeleteActionCombo(comboId);
            model.Automation.UpdateComboBoard(board => board.RemoveFolder("selftest-combo-folder"));
        }
    }

    /// <summary>The Add Action menu opens, and its submenus open beside it (pictures of both).</summary>
    private async Task AddMenuCheck(ActionListEditor editor)
    {
        var menu = editor.ShowAddMenu();
        await Delay(400);
        try
        {
            var clear = menu.Items.OfType<MenuItem>().FirstOrDefault(i => i.Header as string == "Clear");
            Check("the Add Action menu opens", menu.IsOpen && clear is not null);
            if (clear is null) return;
            clear.IsSubmenuOpen = true;
            await Delay(400);
            var popup = clear.Template?.FindName("PART_Popup", clear) as Popup;
            Check("its submenus open beside it (Clear › Clear All, the layers, Clear Music)",
                popup is { IsOpen: true, Child: FrameworkElement { ActualWidth: > 0 } } && clear.Items.Count >= 9);
            SaveElement(menu, "30b-editor-add-action-menu");
            if (popup?.Child is FrameworkElement submenu) SaveElement(submenu, "30c-editor-add-action-submenu");
        }
        finally
        {
            menu.IsOpen = false;
        }
    }

    /// <summary>A picture of one element (a menu: popups aren't part of their window's picture).</summary>
    private void SaveElement(FrameworkElement element, string name)
    {
        try
        {
            if (element.ActualWidth < 1) return;
            var dpi = VisualTreeHelper.GetDpi(element);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var backdrop = new DrawingVisual();
            using (var dc = backdrop.RenderOpen())
                dc.DrawRectangle(EditorKit.Res<Brush>("BasePlane"), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            bitmap.Render(backdrop);
            bitmap.Render(element);
            Write(bitmap, name);
        }
        catch (Exception ex)
        {
            report.AppendLine($"NOTE  couldn't capture {name}: {ex.Message}");
        }
    }

    private async Task SlideActionsEditorCheck()
    {
        var background = model.MediaItems.FirstOrDefault(m => m.Name == "Deep Blue")?.Item ?? model.MediaItems.FirstOrDefault()?.Item;
        var timer = model.TimerRows.FirstOrDefault();
        if (background is null)
        {
            Skip("slide actions editor", "no media in the bin");
            return;
        }
        var slide = new Slide
        {
            Id = "selftest-editor-slide", Name = "Offering",
            Objects = [new SlideObject { Id = "t", ObjectKind = SlideObjectKind.Text, Name = "Text", Text = "Offering" }],
        };
        var deck = new Presentation { Id = "selftest-editor-deck", Name = "Offering", Slides = [slide] };
        model.Library.Save(deck);
        model.Open(deck, null, null);
        await Delay(300);

        var editorWindow = SlideActionsWindow.ForSlide(window, model, slide, deck);
        Check("the slide actions window opens for a slide", editorWindow is not null);
        if (editorWindow is null) return;
        try
        {
            editorWindow.Show();
            await Delay(500);
            var editor = editorWindow.Editor;
            var fireMedia = FromMenu(editor, "Fire Media…");
            Check("the slide's Add Action menu offers Fire Media", fireMedia is not null);
            if (fireMedia is null) return;
            editor.Append(fireMedia);
            Check("a new Fire Media step still needs its media", ActionEditing.NeedsChoice(fireMedia));
            // What picking from the media window does.
            var mediaField = ActionEditing.Fields(fireMedia).OfType<PickField>().Single();
            editor.Edit(fireMedia.Id, a => mediaField.Apply(a, background.Id), reshape: true);
            if (timer is not null && FromMenu(editor, "Timer", "Start", timer.Name) is { } startTimer) editor.Append(startTimer);
            var stored = model.Library.Get<Presentation>(deck.Id)?.Slides.FirstOrDefault(s => s.Id == slide.Id)?.Actions;
            Check("the slide's actions are saved as they're edited",
                stored is [{ Kind: SlideActionKind.FireMedia } media, ..] && media.MediaId == background.Id && stored.Count == (timer is null ? 1 : 2));
            Check("the slide's Actions menu counts them",
                SlideActionsWindow.ActionsMenu(window, model, slide, deck).Header as string == ActionEditing.ActionsMenuTitle(stored?.Count ?? 0));
            await Delay(400);
            Save(editorWindow, "31-editor-slide-actions-window");
        }
        finally
        {
            editorWindow.Close();
        }

        model.GoLiveCommand.Execute(model.Slides.FirstOrDefault(s => s.Slide.Id == slide.Id));
        await Delay(700);
        Check("firing the slide runs the actions made in the editor (its background goes up)",
            model.Show.LiveMedia.Values.Any(cue => cue.MediaId == background.Id));
        if (timer is not null)
        {
            Check("and its timer starts", model.Timers.Snapshot(timer.Id)?.IsRunning == true);
            model.Timers.Reset(timer.Id);
        }
        model.ClearAllCommand.Execute(null);
        await Delay(300);
    }

    private async Task SchedulerEditorCheck(AlertPreset alert)
    {
        var scheduler = SchedulerWindow.Open(window, model);
        await Delay(700);
        string? triggerId = null;
        try
        {
            triggerId = scheduler.NewTrigger("Editor trigger");
            Check("the Scheduler makes a new trigger and opens it", triggerId is not null && scheduler.Editor is not null);
            if (triggerId is null || scheduler.Editor is not { } editor) return;
            scheduler.AddCondition(triggerId, ScheduleConditionKind.Weekly);
            if (FromMenu(scheduler.Editor!, "Alert", alert.Name) is { } fireAlert) scheduler.Editor!.Append(fireAlert);
            var trigger = model.Library.Get<ScheduleTrigger>(triggerId);
            Check("the trigger is saved with a Sunday 9:00 condition and its action",
                trigger is { Conditions: [{ Kind: ScheduleConditionKind.Weekly, TimeOfDay: "09:00", Days: [1] }], Actions: [{ Kind: SlideActionKind.FireAlert }] });

            // The scheduler sweeps after the save; its next fire then shows on the row.
            await Delay(900);
            var due = trigger is null ? null : ScheduleMath.NextDue(trigger, Core.Threading.ShowTimeProvider.Shared.GetUtcNow(), TimeZoneInfo.Local);
            var expected = due is { } at
                ? ScheduleConditionFormat.WeekdayTime(ScheduleConditionFormat.Wall(at, TimeZoneInfo.Local), System.Globalization.CultureInfo.CurrentCulture)
                : null;
            var row = scheduler.RowTexts().FirstOrDefault(r => r.Contains("Editor trigger", StringComparison.Ordinal));
            Check("the scheduler knows when it fires next", model.Schedule.Upcoming.TryGetValue(triggerId, out var upcoming) && upcoming == due);
            Check($"the trigger is listed with its next time ({expected})", row is not null && expected is not null && row.Contains(expected, StringComparison.Ordinal));
            Check("the editor says when it fires next", scheduler.NextFireText?.StartsWith("Fires next:", StringComparison.Ordinal) == true);
            Save(scheduler, "32-editor-scheduler-window");

            model.Automation.UpdateScheduleTrigger(triggerId, t => ScheduleEditing.ToggleFromChip(t, Core.Threading.ShowTimeProvider.Shared.GetUtcNow(), TimeZoneInfo.Local));
            await Delay(600);
            Check("pausing it takes it off the upcoming list", !model.Schedule.Upcoming.ContainsKey(triggerId));
        }
        finally
        {
            scheduler.Close();
            if (triggerId is not null) model.Automation.DeleteScheduleTrigger(triggerId);
        }
    }
}
