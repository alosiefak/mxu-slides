using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Outputs;
using MxU.Slides.Core.Scene;
using MxU.Slides.Core.Stage;
using MxU.Slides.ProImport;
using MxU.Slides.Windows.Outputs;
using MxU.Slides.Windows.Rendering;
using MxU.Slides.Windows.Views;
using RV = Pro.SerializationInterop.RVProtoData;
using SceneTransition = MxU.Slides.Windows.Rendering.SceneTransition;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>
/// The operator panels (Phase 5), driven the way their buttons drive them: saved alerts with
/// blanks, Send Update, Flash, a theme's Alerts design, an alert on another layer and an
/// imported ProPresenter message; the Timers panel's quick lengths, folders, warning colours and
/// count-up limit; a different stage layout on each stage screen, a layout in the preview and a
/// pop-out preview window; one screen switched off while another stays on; the look switcher;
/// and the present settings and transition chips. Pictures 40-… to 47-….
/// </summary>
public sealed partial class SelfTest
{
    private async Task OperatorCheck()
    {
        var alerts = Descendant<AlertsPanel>(window);
        var timers = Descendant<TimersPanel>(window);
        var stage = Descendant<StagePanel>(window);
        var screens = Descendant<ScreensPanel>(window);
        var toolbar = Descendant<LiveToolbar>(window);
        Check("the operator panels are in the main window (live toolbar, screens, stage, timers, alerts)",
            alerts is not null && timers is not null && stage is not null && screens is not null && toolbar is not null);
        if (alerts is null || timers is null || stage is null || screens is null || toolbar is null || model.Outputs is null) return;

        // A lyric on the audience, for everything below to sit over.
        model.SelectedServiceItem = model.ServiceItems.FirstOrDefault(r => r.Name == "Amazing Grace");
        model.GoLiveCommand.Execute(model.Slides.ElementAtOrDefault(1));
        await Delay(600);
        try
        {
            await AlertsPanelCheck(alerts);
            await TimersPanelCheck(timers);
            await StageLayoutsCheck(stage);
            await ScreenLiveCheck(screens);
            await LookChipCheck(toolbar);
            await PresentSettingsCheck(toolbar);
        }
        finally
        {
            model.ClearAllCommand.Execute(null);
        }
    }

    private static T? Descendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) return found;
            if (Descendant<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    private bool AlertOnLayer(LayerKind layer) =>
        model.Show.AnimatedScene()[layer].Items.Any(i => i.Id.StartsWith("alert-", StringComparison.Ordinal));

    // MARK: Alerts

    private async Task AlertsPanelCheck(AlertsPanel panel)
    {
        var made = new List<string>();
        var theme = new Theme
        {
            Id = "selftest-alerts-theme", Name = "Self-test alert theme", FontFamily = "Segoe UI", FontSize = 60,
            TextColorHex = "#FFFFFFFF", BackgroundColorHex = "#000000FF",
            Slides =
            [
                new Slide
                {
                    Id = "selftest-alerts-design", Name = "Alerts",
                    Objects =
                    [
                        new SlideObject
                        {
                            Id = "band", ObjectKind = SlideObjectKind.Shape, Name = "Band", Text = "", X = 0, Y = 0, Width = 1920, Height = 140,
                            ShapeKind = ShapeKind.Rectangle, Fill = new ObjectFill { FillKind = FillKind.Solid, ColorHex = "#7C3AEDFF" },
                        },
                        new SlideObject { Id = "message", ObjectKind = SlideObjectKind.Text, Name = "Message", Text = "Sample", X = 60, Y = 20, Width = 1800, Height = 100 },
                    ],
                },
            ],
        };
        try
        {
            // A saved alert with a blank, to the room and the stage.
            var car = model.Alerts.CreateAlertPreset("Car {plate}, your lights are on", AlertBehavior.Persist, AlertTarget.Both, null, () => "selftest-alert-car")!;
            made.Add(car);
            model.Alerts.UpdateAlertPreset(car, a => a.Name = "Car lights");
            await Delay(300);
            Check("a saved alert is listed on the Alerts panel", panel.RowNames.Contains("Car lights"));
            Check("its {plate} is a blank to fill, but {Sermon} is a live timer",
                AlertEditing.FillInTokens("Car {plate}, {Sermon} left", model.TimerNames) is ["plate"]);
            model.AlertBlanks[car] = new(StringComparer.OrdinalIgnoreCase) { ["plate"] = "KD21 XYZ" };
            panel.Open(car);
            model.FireAlertPreset(model.Library.Get<AlertPreset>(car)!);
            await Delay(500);
            Check("Go Live sends it with the blank filled in, to the audience and the stage",
                AudienceText().Contains("Car KD21 XYZ, your lights are on") && StageText().Contains("Car KD21 XYZ, your lights are on"));
            Check("while it's up its row offers Turn Off", model.LiveAlertId == car);
            SaveElement(panel, "40-operator-alerts-live");
            var menu = panel.OpenAddMenu();
            await Delay(300);
            SaveElement(menu, "40b-operator-alerts-add-menu");
            menu.IsOpen = false;

            model.AlertBlanks[car]["plate"] = "LM07 ABC";
            model.FireAlertPreset(model.Library.Get<AlertPreset>(car)!);
            await Delay(300);
            Check("Send Update resends it with the blanks as they read now", AudienceText().Contains("Car LM07 ABC") && !AudienceText().Contains("KD21"));
            model.DismissAlert();
            await Delay(300);
            Check("Turn Off takes it down", !model.Show.HasAlert);

            // Flash, to the audience only, on the Overlays layer.
            var flash = model.Alerts.CreateAlertPreset("Please move to the front", AlertBehavior.Flash, AlertTarget.Audience, null, () => "selftest-alert-flash")!;
            made.Add(flash);
            model.Alerts.UpdateAlertPreset(flash, a => AlertEditing.SetLayer(a, LayerKind.Overlays));
            model.FireAlertPreset(model.Library.Get<AlertPreset>(flash)!);
            bool sawOn = false, sawOff = false, onOverlays = false;
            for (var i = 0; i < 20; i++)
            {
                await Delay(100);
                if (model.Show.AlertVisible)
                {
                    sawOn = true;
                    onOverlays |= AlertOnLayer(LayerKind.Overlays) && !AlertOnLayer(LayerKind.Alerts);
                }
                else
                {
                    sawOff |= !AlertOnLayer(LayerKind.Overlays);
                }
            }
            Check("a Flash alert blinks on and off", sawOn && sawOff);
            Check("an alert set to the Overlays layer draws there", onOverlays);
            Check("an audience-only alert stays off the stage", !StageText().Contains("Please move to the front"));
            model.DismissAlert();
            await Delay(700);
            Check("taking a Flash alert down leaves it steady (visible) for the next one", !model.Show.HasAlert && model.Show.AlertVisible);

            // Through a theme's Alerts design.
            model.Library.Save(theme);
            var themed = model.Alerts.CreateAlertPreset("Welcome to our guests", AlertBehavior.Persist, AlertTarget.Audience, theme.Id, () => "selftest-alert-themed")!;
            made.Add(themed);
            model.FireAlertPreset(model.Library.Get<AlertPreset>(themed)!);
            await Delay(500);
            var items = model.Show.AnimatedScene()[LayerKind.Alerts].Items;
            Check("a themed alert takes its theme's Alerts design (its band and its text box)",
                items.Any(i => i.Id == $"alert-{themed}-theme-band") && AudienceText().Contains("Welcome to our guests") && !AudienceText().Contains("Sample"));
            await Snapshot("40c-operator-alert-themed-frame", model.Show.Scene());
            model.DismissAlert();
            await Delay(300);

            // A ProPresenter message, as a library import maps it, fired by hand.
            var message = new RV.Message
            {
                Uuid = new RV.UUID { String = "SELFTEST-MSG" }, Title = "Parking", MessageText = "Car ${SELFTEST-TOKEN} please move",
                Tokens = { new RV.Message.Types.Token { Uuid = new RV.UUID { String = "SELFTEST-TOKEN" }, Text = new() { Name = "plate" } } },
            };
            var imported = ProWorkspaceMapper.MapMessages(new RV.MessageDocument { Messages = { message } }).Single().Preset;
            model.Library.Save(imported);
            made.Add(imported.Id);
            await Delay(300);
            Check("an imported ProPresenter message is listed with the alerts", panel.RowNames.Contains("Parking"));
            model.AlertBlanks[imported.Id] = new(StringComparer.OrdinalIgnoreCase) { ["plate"] = "AB12" };
            model.FireAlertPreset(imported);
            await Delay(400);
            Check("…and fires by hand with its blank filled in", StageText().Contains("Car AB12 please move"));
            panel.Open(imported.Id);
            await Delay(300);
            SaveElement(panel, "40d-operator-alerts-propresenter");
            model.DismissAlert();
        }
        finally
        {
            model.DismissAlert();
            foreach (var id in made) model.Alerts.DeleteAlertPreset(id);
            if (model.Library.Get<Theme>(theme.Id) is not null) model.Library.Delete<Theme>(theme.Id);
        }
    }

    // MARK: Timers

    private async Task TimersPanelCheck(TimersPanel panel)
    {
        var timers = model.Timers;
        var folder = timers.AddFolder("Self-test timers");
        string? countdown = null, countUp = null;
        try
        {
            var menu = panel.OpenAddMenu();
            await Delay(400);
            SaveElement(menu, "41b-operator-timers-add-menu");
            var titles = menu.Items.OfType<MenuItem>().Select(i => i.Header as string).ToList();
            Check("the + menu offers the quick lengths, a custom length, a time of day, a count up and a folder",
                TimerPanel.QuickLengths.All(m => titles.Contains($"{m} minutes"))
                && titles.Contains("Custom…") && titles.Contains("Countdown to Time…") && titles.Contains("Count Up Timer") && titles.Contains("New Folder"));
            menu.IsOpen = false;
            var before = timers.Timers.Count;
            menu.Items.OfType<MenuItem>().First(i => i.Header as string == "10 minutes").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            countdown = timers.Timers.Count == before + 1 ? timers.Timers[^1].Id : null;
            Check("10 minutes adds a ten-minute countdown", countdown is not null && timers.Snapshot(countdown) is { Mode: TimerMode.Countdown, DurationSeconds: 600 });
            if (countdown is null) return;

            timers.PutInFolder(countdown, folder);
            timers.SetWarnings(countdown, [new TimerWarning(599, "#3B82F6FF"), new TimerWarning(0, TimerWarning.RedHex)]);
            timers.Start(countdown);
            countUp = timers.AddCountUp("Walk-in");
            timers.SetLimit(countUp, 120);
            panel.Open(countdown);
            panel.Open(countUp);
            await Delay(1500);
            var now = DateTimeOffset.Now;
            Check("a warning level turns the running timer its colour", timers.Snapshot(countdown) is { } running && TimerPanel.RowTint(running, now).WarningHex == "#3B82F6FF");
            Check("a count up can count to a limit, and then has warnings", timers.Snapshot(countUp) is { DurationSeconds: 120 } limited && TimerPanel.ShowsWarnings(limited));
            Check("the timer sits in its folder on the panel", timers.Board.Folder(folder)?.TimerIds.Contains(countdown) == true && panel.RowNames.Contains("Countdown"));
            Check("the folder row reads its running timer", TimerPanel.FolderReadout(timers.Snapshots(timers.Board.Folder(folder)!.TimerIds), now).Text is { Length: > 0 });
            SaveElement(panel, "41-operator-timers");
            var saved = Path.Combine(AppPaths.DataRoot, "timer-board.json");
            Check("the folders and order are saved beside the timers",
                File.Exists(saved) && TimerBoard.FromJson(File.ReadAllText(saved)).Folder(folder)?.TimerIds.Contains(countdown) == true);
        }
        finally
        {
            if (countdown is not null) timers.Remove(countdown);
            if (countUp is not null) timers.Remove(countUp);
            timers.RemoveFolder(folder);
        }
    }

    // MARK: Stage layouts per screen, the preview picker, pop-outs

    private async Task StageLayoutsCheck(StagePanel panel)
    {
        var outputs = model.Outputs!;
        var config = outputs.Config;
        var other = model.StageLayouts.FirstOrDefault(l => l.Name != "Classic");
        var classic = model.StageLayouts.FirstOrDefault(l => l.Name == "Classic");
        if (other is null || classic is null || config.Screens.FirstOrDefault(s => config.Role(s.Id) == ScreenRole.Confidence) is not { } main)
        {
            Skip("a stage layout per stage screen", "the starter stage layouts or the Stage screen are missing");
            return;
        }
        var band = config.AddScreen("Self-test band", 1280, 720)!;
        config.SetRole(band.Id, ScreenRole.Confidence);
        PreviewPopOutWindow? popOut = null;
        var previewBefore = model.Settings.PreviewTarget;
        try
        {
            await Delay(500);
            bool Shows(Guid screen, string layoutId) =>
                outputs.ShownScene(screen)?.AllItems.Any(i => i.Id.StartsWith($"stage-{layoutId}-", StringComparison.Ordinal)) == true;
            Check("the Stage screen keeps the layout every stage screen used to share (Classic)", Shows(main.Id, classic.Id));
            Check("a new stage screen starts on the built-in layout", Shows(band.Id, "built-in"));
            model.SetStageLayout(band.Id, other.Id);
            await Delay(300);
            Check($"each stage screen shows its own layout (the band's {other.Name}, the Stage's Classic)", Shows(band.Id, other.Id) && Shows(main.Id, classic.Id));
            Check("…kept in settings.json in the Mac's shape (screen UUID → layout id)",
                model.Settings.ConfidenceScreenLayouts?.GetValueOrDefault(SwiftUuid.Format(band.Id)) == other.Id);
            ((Core.Automation.IShowActions)model).SetConfidenceLayout(classic.Id, band.Id);
            await Delay(300);
            Check("the Set Confidence Layout action changes just the screen it names", Shows(band.Id, classic.Id));
            model.SetStageLayout(band.Id, other.Id);
            Check("the Stage panel lists both stage screens", panel.ScreenNames.Contains("Self-test band") && panel.ScreenNames.Contains(main.Name));
            model.StagePreviewScreenId = band.Id;
            await Delay(400);
            SaveElement(panel, "42-operator-stage-layouts");
            await Snapshot("42b-operator-stage-band-frame", outputs.ShownScene(band.Id)!);

            // The live panel's preview, pointed at a layout no screen shows, then at the band.
            model.SetPreviewTarget(PreviewTargets.LayoutTarget(other.Id));
            await Delay(500);
            Check("the preview picker can show a stage layout", model.AudiencePreview?.Scene?.AllItems.Any(i => i.Id.StartsWith($"stage-{other.Id}-", StringComparison.Ordinal)) == true);
            await Step("43-operator-preview-layout", () => { }, settle: 300);
            model.OpenPreviewWindow(SwiftUuid.Format(band.Id), window);
            await Delay(900);
            popOut = Application.Current.Windows.OfType<PreviewPopOutWindow>().LastOrDefault();
            Check("a preview pops out into its own window, showing the screen it was opened on",
                popOut?.View.Scene?.AllItems.Any(i => i.Id.StartsWith($"stage-{other.Id}-", StringComparison.Ordinal)) == true);
            if (popOut is not null) Save(popOut, "44-operator-preview-popout");
        }
        finally
        {
            popOut?.Close();
            model.StagePreviewScreenId = null;
            model.SetStageLayout(band.Id, null);
            config.RemoveScreen(band.Id);
            var audience = config.Screens.FirstOrDefault(s => config.Role(s.Id) == ScreenRole.Audience);
            model.SetPreviewTarget(previewBefore ?? (audience is null ? "" : SwiftUuid.Format(audience.Id)));
        }
    }

    // MARK: One screen off, the others on

    private async Task ScreenLiveCheck(ScreensPanel panel)
    {
        var outputs = model.Outputs!;
        var config = outputs.Config;
        var sinks = new Dictionary<string, CountingSink> { ["ndi:MxU operator A"] = new(), ["ndi:MxU operator B"] = new() };
        var factory = outputs.DeviceSinkFactory;
        outputs.DeviceSinkFactory = device => sinks.TryGetValue(device, out var sink) ? sink : factory?.Invoke(device);
        var a = config.AddScreen("Self-test A", 640, 360)!;
        var b = config.AddScreen("Self-test B", 640, 360)!;
        try
        {
            config.AssignDevice(a.Id, "ndi:MxU operator A", "Self-test A");
            config.AssignDevice(b.Id, "ndi:MxU operator B", "Self-test B");
            outputs.Apply();
            model.RenderOutputs(SceneTransition.Cut);
            // White lyric text on the dark design: count the bright pixels. A PC on the software copy
            // (section 17 of HOW-IT-WORKS) sends fewer pictures, so wait for one that shows the change.
            static int Bright(CountingSink sink) => sink.Last is { } pixels ? Enumerable.Range(0, pixels.Length / 4).Count(i => pixels[i * 4 + 2] > 200) : -1;
            async Task<bool> Within(Func<bool> condition)
            {
                for (var i = 0; i < 40 && !condition(); i++) await Delay(100);
                return condition();
            }
            var aSink = sinks["ndi:MxU operator A"];
            var bSink = sinks["ndi:MxU operator B"];
            Check("two screens on air both get the slide", await Within(() => Bright(aSink) > 50 && Bright(bSink) > 50));
            model.SetScreenLive(a.Id, false);
            Check("switching one screen off blacks out just that one", await Within(() => Bright(aSink) == 0) && Bright(bSink) > 50);
            Check("the Screens panel shows it Off and the other Live",
                panel.Rows.Contains(("Self-test A", false)) && panel.Rows.Contains(("Self-test B", true)));
            SaveElement(panel, "45-operator-screens");
            model.SetScreenLive(a.Id, true);
            Check("switching it back on brings the slide back", await Within(() => Bright(aSink) > 50) && outputs.IsScreenLive(a.Id));
            if (!outputs.IsScreenLive(a.Id) || Bright(aSink) <= 50)
                report.AppendLine($"NOTE  screen A after switching on: live {outputs.IsScreenLive(a.Id)}, bright pixels {Bright(aSink)}, frames {aSink.Frames}");
        }
        finally
        {
            config.RemoveScreen(a.Id);
            config.RemoveScreen(b.Id);
            outputs.Apply();
            outputs.DeviceSinkFactory = factory;
        }
    }

    // MARK: The look switcher

    private async Task LookChipCheck(LiveToolbar toolbar)
    {
        var look = new OutputPreset { Id = "selftest-look", Name = "Self-test stream look", Assignments = [] };
        model.Library.Save(look);
        try
        {
            var menu = toolbar.OpenPresetMenu();
            await Delay(300);
            if (menu is not null)
            {
                SaveElement(menu, "46-operator-look-menu");
                var titles = menu.Items.OfType<MenuItem>().Select(i => i.Tag as string).ToList();
                Check("the look chip lists no preset and every preset", titles.Contains(OutputPresetMenu.DefaultName) && titles.Contains(look.Name));
                menu.IsOpen = false;
            }
            model.SwitchOutputPreset(look.Id);
            await Delay(400);
            Check("choosing a look makes it the active output preset, and the chip says so",
                model.Settings.ActiveOutputPresetId == look.Id && toolbar.PresetText == OutputPresetMenu.ChipName(look.Name));
            model.SwitchOutputPreset(null);
            await Delay(300);
            Check("Default goes back to every layer everywhere", model.Settings.ActiveOutputPresetId is null && toolbar.PresetText == OutputPresetMenu.ChipName(OutputPresetMenu.DefaultName));
        }
        finally
        {
            if (model.Settings.ActiveOutputPresetId == look.Id) model.SwitchOutputPreset(null);
            model.Library.Delete<OutputPreset>(look.Id);
        }
    }

    // MARK: Present settings and the transition chips

    private async Task PresentSettingsCheck(LiveToolbar toolbar)
    {
        Slide Lyric(string id, string section, string text) => new()
        {
            Id = id, Name = id, SectionId = section, Objects = [new SlideObject { Id = $"{id}-t", ObjectKind = SlideObjectKind.Text, Name = "Lyrics", Text = text }],
        };
        var song = new Presentation
        {
            Id = "selftest-repeats", Name = "Self-test repeats",
            Sections = [new PresentationSection { Id = "c", Name = "Chorus" }, new PresentationSection { Id = "i", Name = "Interlude" }],
            Slides = [Lyric("s-c", "c", "Glory, glory"), Lyric("s-i", "i", "")],
            Arrangements = [new Arrangement { Id = "live", Name = "Live", SectionIds = ["c", "i", "i", "c"] }],
            DefaultArrangementId = "live",
        };
        var deck = new Presentation
        {
            Id = "selftest-blanks", Name = "Self-test blanks",
            Slides = [Lyric("b1", "", "Amazing grace"), Lyric("b2", "", ""), Lyric("b3", "", "How sweet the sound")],
        };
        var transition = model.RoomTransitionSetting(media: false);
        try
        {
            model.Library.Save(song);
            model.Open(song, null, "live");
            await Delay(300);
            var plays = model.Slides.Count;
            model.EmptyRepeatsOnce = true;
            await Delay(300);
            Check("\"Show a repeated chord-only section once\" plays an Interlude twice as once", plays == 4 && model.Slides.Count == 3);
            Check("…saved under the Mac's name", File.ReadAllText(AppPaths.SettingsFile).Contains("\"present.emptyRepeatsOnce\": true", StringComparison.Ordinal));
            model.EmptyRepeatsOnce = false;
            Check("…and off, every repeat is a slide again", model.Slides.Count == 4);

            model.Library.Save(deck);
            model.Open(deck, null, null);
            await Delay(300);
            model.GoLiveCommand.Execute(model.Slides.FirstOrDefault());
            await Delay(400);
            Check("Confidence NEXT skips a blank slide to the next words (on by default)", model.NextSkipsBlanks && StageText().Contains("How sweet the sound"));
            model.NextSkipsBlanks = false;
            await Delay(300);
            Check("…and with it off, NEXT is the blank slide", !StageText().Contains("How sweet the sound"));
            model.NextSkipsBlanks = true;

            var popover = toolbar.OpenTransitionPopover(forMedia: false);
            await Delay(400);
            if (popover?.Child is FrameworkElement chip) SaveElement(chip, "47-operator-transition-chip");
            if (popover is not null) popover.IsOpen = false;
            model.SetRoomTransition(media: false, OutputTransition.FadeThroughWhite, 0.6);
            await Delay(200);
            Check("the slide transition chip changes the room default (Settings › Transitions too)",
                model.Settings.Transition == OutputTransition.FadeThroughWhite && model.Settings.TransitionSeconds == 0.6 && toolbar.SlidesText == "Fade White 0.6 s");
            await Step("47b-operator-live-toolbar", () => { }, settle: 200);

            var settings = new SettingsWindow(model, model.Outputs!) { Owner = window };
            settings.Show();
            await Delay(900);
            Check("Settings has the two Present settings", Descendant<PresentSettingsSection>(settings) is not null);
            Save(settings, "47c-operator-settings-present");
            settings.Close();
        }
        finally
        {
            model.EmptyRepeatsOnce = false;
            model.NextSkipsBlanks = true;
            model.SetRoomTransition(media: false, transition.Kind, transition.Seconds);
            model.ClearAllCommand.Execute(null);
            model.Library.Delete<Presentation>(song.Id);
            model.Library.Delete<Presentation>(deck.Id);
        }
    }
}
