using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Core.Stage;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>
/// Show flow, driven through the real app on a throwaway service: slides that advance by
/// themselves (timers, a Slide Show loop, holding, the last slide moving the service on, a video
/// advancing when it ends), the video transport strip, music rows, the Clear controls, the
/// service-tracking timers on a stage layout, hidden rows, a row's output preset, Duplicate and
/// Change Date, and service versions (including a Mac-made one). Pictures: 70-show-* to 79-show-*.
/// </summary>
public sealed partial class SelfTest
{
    private const string FlowServiceId = "selftest-flow-service";

    private static Slide FlowSlide(string id, string text, AutoAdvance? advance = null) => new()
    {
        Id = id, Name = text, AutoAdvance = advance,
        Objects =
        [
            new SlideObject
            {
                Id = $"{id}-text", ObjectKind = SlideObjectKind.Text, Name = "Text", Text = text, X = 160, Y = 390, Width = 1600, Height = 300,
                TextStyle = new TextStyle { FontSize = 110, ColorHex = "#FFFFFFFF", HorizontalAlignment = TextHorizontalAlignment.Center, VerticalAlignment = TextVerticalAlignment.Middle },
            },
        ],
    };

    // No theme, so the slides have no builds: a slide with builds waits for the operator before its timer starts.
    private static Presentation FlowDeck(string id, string name, AutoAdvance? advance, params string[] texts) => new()
    {
        Id = id, Name = name, PresentationKind = PresentationKind.Deck, ThemeId = "", AutoAdvance = advance,
        Slides = [.. texts.Select((text, i) => FlowSlide($"{id}-{i + 1}", text))],
    };

    private static ServiceItem FlowRow(string id, ServiceItemKind kind, string name, string refId, int? duration = null) =>
        new() { Id = id, ItemKind = kind, Name = name, RefId = refId, Duration = duration };

    private ServiceItemViewModel? FlowRowView(string id) =>
        model.ServiceItems.FirstOrDefault(r => r.Item.Id == id) ?? model.HiddenServiceItems.FirstOrDefault(r => r.Item.Id == id);

    private async Task ShowFlowChecks()
    {
        var previousService = model.SelectedService?.Id;
        var previousLayout = model.SelectedStageLayout;
        var video = model.Library.All<MediaItem>().FirstOrDefault(m => m.MediaKind == MediaKind.Video && m.Name.StartsWith("MxU self-test video", StringComparison.Ordinal));
        var tone = model.Library.All<AudioItem>().FirstOrDefault(a => a.Name.StartsWith("MxU self-test tone", StringComparison.Ordinal));
        var videoBefore = video is null ? null : Core.Json.MxuJson.Clone(video);
        try
        {
            model.ClearAllCommand.Execute(null);
            model.Library.Save(FlowDeck("selftest-flow-welcome", "Welcome", null, "Welcome, everyone"));
            model.Library.Save(FlowDeck("selftest-flow-announce", "Announcements", new AutoAdvance { DelaySeconds = 1.2 },
                "Announcement 1", "Announcement 2", "Announcement 3"));
            model.Library.Save(FlowDeck("selftest-flow-loop", "Walk-in loop", new AutoAdvance { DelaySeconds = 0.8, LoopToStart = true }, "Loop A", "Loop B"));
            model.Library.Save(FlowDeck("selftest-flow-song", "Closing song", null, "Song line 1", "Song line 2"));
            if (tone is not null)
                model.Library.Save(new Playlist
                {
                    Id = "selftest-flow-walkin", Name = "Walk-in music", PlaylistKind = PlaylistKind.Audio, PlaybackMode = PlaybackMode.PlayAll,
                    Entries = [new PlaylistEntry { Id = "walkin-1", RefKind = PlaylistRefKind.Audio, RefId = tone.Id }],
                });
            if (video is not null)
            {
                // Advance when it ends, to the next row (a bumper video before the closing song).
                video.AutoAdvance = new AutoAdvance { DelaySeconds = 0, AfterPlayback = true };
                video.Loops = false;
                video.Classification = MediaClassification.Foreground;
                model.Library.Save(video);
            }
            List<ServiceItem> rows =
            [
                FlowRow("flow-head", ServiceItemKind.Header, "Before the service", ""),
                FlowRow("flow-welcome", ServiceItemKind.Presentation, "Welcome", "selftest-flow-welcome", 120),
            ];
            if (tone is not null) rows.Add(FlowRow("flow-walkin", ServiceItemKind.Playlist, "Walk-in music", "selftest-flow-walkin", 300));
            rows.Add(FlowRow("flow-announce", ServiceItemKind.Presentation, "Announcements", "selftest-flow-announce", 60));
            if (video is not null) rows.Add(FlowRow("flow-video", ServiceItemKind.Media, "Bumper video", video.Id, 10));
            rows.Add(FlowRow("flow-worship", ServiceItemKind.Header, "Worship", ""));
            rows.Add(FlowRow("flow-song", ServiceItemKind.Presentation, "Closing song", "selftest-flow-song", 300));
            model.Library.Save(new Service { Id = FlowServiceId, Name = "Self-test show flow", ServiceDate = "2026-10-25", Items = rows });
            model.RefreshServices();
            model.SelectedService = model.Services.FirstOrDefault(s => s.Id == FlowServiceId);
            Check("the show-flow test service opens", model.SelectedService?.Id == FlowServiceId && FlowRowView("flow-song") is not null);

            await AutoAdvanceChecks(video);
            await MusicRowChecks(tone);
            await ClearControlChecks(tone);
            await ServiceTrackingChecks();
            await ServiceListChecks();
            await ServiceVersionChecks();
        }
        finally
        {
            model.ClearAllCommand.Execute(null);
            model.ClearAudioCommand.Execute(null);
            if (videoBefore is not null) model.Library.Save(videoBefore);
            model.SetTransportSkip(30);
            model.ChooseServiceVersion(FlowServiceId, null);
            model.RefreshServices();
            model.SelectedService = model.Services.FirstOrDefault(s => s.Id == previousService) ?? model.Services.FirstOrDefault();
            if (previousLayout is not null) model.SelectedStageLayout = model.StageLayouts.FirstOrDefault(l => l.Id == previousLayout.Id) ?? previousLayout;
            await Delay(300);
        }
    }

    /// <summary>
    /// Waits (up to <paramref name="seconds"/>) for something the show does by itself, and returns
    /// how long it took, or null if it never happened. Timers fire on the show clock, so a busy PC
    /// can make them a little late; the report notes by how much.
    /// </summary>
    private static async Task<double?> WhenShown(Func<bool> condition, double seconds)
    {
        var started = DateTime.UtcNow;
        while ((DateTime.UtcNow - started).TotalSeconds < seconds)
        {
            if (condition()) return (DateTime.UtcNow - started).TotalSeconds;
            await Delay(50);
        }
        return condition() ? seconds : null;
    }

    /// <summary>Slide timers, holding, the last slide moving the service on, a video advancing when it ends, the strip, and a Slide Show loop.</summary>
    private async Task AutoAdvanceChecks(MediaItem? video)
    {
        if (FlowRowView("flow-announce") is not { } announce) return;
        model.FireServiceItemCommand.Execute(announce);
        Check("a timed slide goes live with its auto-advance chip", AudienceText().Contains("Announcement 1") && model.AutoAdvanceVisible
            && model.AutoAdvance.Remaining() is > 1 and <= 1.2);
        await Step("70-show-auto-advance", () => { }, settle: 250);
        var took = await WhenShown(() => AudienceText().Contains("Announcement 2"), 4);
        Check("a slide timer advances to the next slide by itself", took is not null);
        report.AppendLine($"NOTE  the 1.2 s timer moved on {(took is { } t ? $"{t + 0.25:F2} s" : "never")} after the slide went live");

        await Delay(300);
        model.ToggleAutoAdvanceHoldCommand.Execute(null);
        var heldLeft = model.AutoAdvance.Remaining();
        await Step("71-show-advance-held", () => { }, settle: 1500);
        Check("holding the chip keeps the slide (and says so)", AudienceText().Contains("Announcement 2") && model.AutoAdvanceHeld
            && model.AutoAdvanceCaption == "Advance held" && model.AutoAdvance.Remaining() == heldLeft);
        model.ToggleAutoAdvanceHoldCommand.Execute(null);
        took = await WhenShown(() => AudienceText().Contains("Announcement 3"), 4);
        Check($"letting it go carries on from the time that was left ({heldLeft:F1} s)", took is not null && took < (heldLeft ?? 0) + 2);

        if (video is not null)
        {
            await WhenShown(() => model.Live?.ItemId == "flow-video", 5);
            Check("after the last slide, the service moves on to its next row (the video)", model.Show.LiveMedia.TryGetValue(LayerKind.Videos, out var cue)
                && cue.MediaId == video.Id && model.Live?.ItemId == "flow-video");
            Check("the video's advance waits for it to end", model.AutoAdvance.Pending?.MediaId == video.Id);
            await TransportChecks(video);
        }
        else
        {
            Check("after the last slide, the service moves on to its next row", await WhenShown(() => AudienceText().Contains("Song line 1"), 5) is not null);
            Skip("a video advancing after playback, and the transport strip", "no test video could be made on this PC");
        }

        // A Slide Show loop, opened from the library (not the service).
        if (model.Library.Get<Presentation>("selftest-flow-loop") is { } loop)
        {
            model.SelectedServiceItem = null;
            model.Open(loop, null, null);
            model.GoLiveCommand.Execute(model.Slides.FirstOrDefault());
            var seen = new List<string>();
            for (var i = 0; i < 80 && seen.Count < 3; i++)
            {
                var text = AudienceText().Contains("Loop B") ? "B" : AudienceText().Contains("Loop A") ? "A" : "-";
                if (seen.Count == 0 || seen[^1] != text) seen.Add(text);
                await Delay(50);
            }
            Check($"a Slide Show loops back to its first slide (saw {string.Join(" ", seen)})", seen.Take(3).SequenceEqual(["A", "B", "A"]));
            model.NextCommand.Execute(null);
            Check("pressing Next restarts the timer from the new slide", model.AutoAdvance.Remaining() is > 0.6);
            model.ClearSlideCommand.Execute(null);
            await Delay(1000);
            Check("clearing the slide stops its timer", model.Show.Live is null && model.AutoAdvance.Pending is null);
        }
    }

    /// <summary>The strip on the bumper video: pause, go to a typed time, jump near the end; then the video's end moves the service on.</summary>
    private async Task TransportChecks(MediaItem video)
    {
        // Slow PCs decode in software: give the player a moment to open.
        await WhenShown(() => model.TransportHasVideo, 8);
        Check("the transport strip shows the live video", model.TransportHasVideo && model.TransportName == video.Name && model.TransportShowsControls);
        var key = model.Media.ActiveVideos.FirstOrDefault(v => v.MediaId == video.Id && v.Key.StartsWith("live:", StringComparison.Ordinal)).Key;
        if (key is null) return;
        model.TransportPlayPauseCommand.Execute(null);
        await Delay(300);
        Check("the strip pauses the video", key is not null && model.Media.Transport(key) is { Playing: false });
        Check("typing a time goes there", model.TransportGoTo("0:01") && model.Media.Transport(key!) is { } at && Math.Abs(at.Position.TotalSeconds - 1) < 0.4);
        await Step("72-show-transport-strip", () => { }, settle: 300);
        Check("a paused video holds its advance", model.Live?.ItemId == "flow-video");
        model.TransportPlayPauseCommand.Execute(null);
        model.SetTransportSkip(1);
        model.TransportSkipToEndCommand.Execute(null);
        await Delay(200);
        Check("the jump button goes to near the end", model.Media.Transport(key!) is { Duration: { } length } near && length.TotalSeconds - near.Position.TotalSeconds < 1.5);
        for (var i = 0; i < 40 && model.Live?.ItemId == "flow-video"; i++) await Delay(100);
        Check("a video's 'advance after playback' fires the next service row", model.Live?.ItemId == "flow-song" && AudienceText().Contains("Song line 1"));
        model.SetTransportSkip(30);
    }

    /// <summary>Music rows: the card, Next arriving on a playlist row plays it, Play/Stop on the card.</summary>
    private async Task MusicRowChecks(AudioItem? tone)
    {
        if (tone is null || FlowRowView("flow-walkin") is not { } walkin || FlowRowView("flow-welcome") is not { } welcome)
        {
            Skip("music rows", "the music test file isn't in the library");
            return;
        }
        model.ClearAllCommand.Execute(null);
        model.SelectedServiceItem = walkin;
        await Step("73-show-music-card", () => { }, settle: 400);
        Check("selecting a music row shows its card with its songs", model.MusicCardVisible && model.MusicCardIsPlaylist && model.MusicCardTracks.Count == 1);
        model.FireServiceItemCommand.Execute(welcome);
        model.NextCommand.Execute(null);
        await Delay(900);
        Check("Next arrives on a playlist row and plays it", model.Audio.Status().Any(b => b.Playing && b.PlaylistId == "selftest-flow-walkin")
            && model.Live?.ItemId == "flow-walkin");
        Check("the welcome slide stays up under the music", AudienceText().Contains("Welcome, everyone"));
        await Delay(300);
        Check("the music row is marked live", walkin.IsLive && model.MusicCardIsLive);
        await Step("74-show-music-live", () => model.SelectedServiceItem = walkin, settle: 400);
        model.NextCommand.Execute(null);
        Check("the next Next moves on past the music", model.Live?.ItemId == "flow-announce");
        model.SelectedServiceItem = walkin;
        model.MusicCardPlayStopCommand.Execute(null);
        await Delay(2200);
        Check("Stop on the card stops it", !model.Audio.Status().Any(b => b.Playing && b.PlaylistId == "selftest-flow-walkin"));

        // A single-song row, as ProPresenter imports walk-in cues.
        model.UpdateService(FlowServiceId, s => s.Items.Insert(1, FlowRow("flow-song-cue", ServiceItemKind.Audio, "Walk-in song", tone.Id)));
        if (FlowRowView("flow-song-cue") is { } cueRow)
        {
            model.FireServiceItemCommand.Execute(cueRow);
            await Delay(800);
            Check("an Audio row plays its song", model.Audio.Status().Any(b => b.Playing && b.AudioItemId == tone.Id));
        }
        model.UpdateService(FlowServiceId, s => s.Items.RemoveAll(i => i.Id == "flow-song-cue"));
        model.ClearFunction(ShowFunction.Audio);
        model.ClearAllCommand.Execute(null);
    }

    /// <summary>The Clear controls: green for what's live, Clear All's checklist, Clear Music, one layer at a time.</summary>
    private async Task ClearControlChecks(AudioItem? tone)
    {
        var background = model.MediaItems.FirstOrDefault(m => !m.IsVideo);
        if (FlowRowView("flow-song") is not { } song || background is null) return;
        model.FireServiceItemCommand.Execute(song);
        model.FireMedia(background.Item);
        if (tone is not null) model.AudioTileClickedCommand.Execute(model.AudioTiles.FirstOrDefault(t => t.Item?.Id == tone.Id));
        await Delay(700);
        bool Green(string name) => model.ClearFunctionButtons.First(b => b.Name == name).IsLive;
        Check("the Clear buttons are green for what's live", Green("Slides") && Green("Media") && !Green("Inputs") && !Green("Overlays")
            && (tone is null || Green("Music")) && model.ClearLayerButtons.First(b => b.Layer == LayerKind.Slide).IsLive);
        Check("Clear All says what it leaves up", model.ClearAllNote.Contains("Inputs"));
        window.LiveControls.LayersToggle.IsChecked = true;
        await Step("75-show-clear-controls", () => { }, settle: 400);
        window.LiveControls.LayersToggle.IsChecked = false;

        model.ClearLayer(LayerKind.Slide);
        await Delay(200);
        Check("clearing one layer leaves the others", model.Show.Live is null && model.Show.LiveMedia.Count > 0);

        if (tone is not null)
        {
            model.FireServiceItemCommand.Execute(song);
            model.SetClearAllSweeps(ShowFunction.Audio, false);
            Check("unticking Music changes what Clear All says", model.ClearAllNote.Contains("Music"));
            model.ClearAllCommand.Execute(null);
            await Delay(300);
            Check("Clear All leaves what its checklist unticks (the music plays on)", model.Show.Live is null && model.Show.LiveMedia.Count == 0
                && model.Audio.Status().Any(b => b.Playing));
            model.SetClearAllSweeps(ShowFunction.Audio, true);
            model.ClearFunction(ShowFunction.Audio);
            await Delay(2200);
            Check("Clear Music stops the music", !model.Audio.Status().Any(b => b.Playing));
        }
        model.ClearInputsCommand.Execute(null);
        model.ClearAllCommand.Execute(null);
    }

    /// <summary>The service-tracking timers: running from a fire, on a stage layout, in their own panel, not in the Timers list.</summary>
    private async Task ServiceTrackingChecks()
    {
        if (FlowRowView("flow-welcome") is not { } welcome) return;
        model.ClearServiceTrackingCommand.Execute(null);
        model.FireServiceItemCommand.Execute(welcome);
        await Delay(300);
        var now = DateTimeOffset.Now;
        var item = model.Timers.Snapshot(ServiceTrackingTimers.Id(ServiceTrackingTimers.Subject.Item, ServiceTrackingTimers.Face.Remaining));
        var service = model.Timers.Snapshot(ServiceTrackingTimers.Id(ServiceTrackingTimers.Subject.Service, ServiceTrackingTimers.Face.Remaining));
        var planned = model.SelectedService!.Items.Sum(i => i.ItemKind == ServiceItemKind.Header ? 0 : i.Duration ?? 0);
        Check("firing a row starts Item Remaining from its length", item is { IsRunning: true } && item.Value(now) is > 118 and <= 120);
        Check("Service Remaining counts down every row's length", service is { IsRunning: true } && Math.Abs(service.Value(now) - planned) < 2);
        Check("the tracking timers keep out of the Timers list", model.TimerRows.All(r => !TimerController.IsPermanent(r.Id)) && model.TimerRows.Count > 0);

        var layout = new ConfidenceLayout
        {
            Id = "selftest-flow-tracking-layout", Name = "Self-test tracking",
            Objects =
            [
                new SlideObject
                {
                    Id = "label", ObjectKind = SlideObjectKind.Text, Name = "Label", Text = "ITEM", X = 100, Y = 120, Width = 1720, Height = 140,
                    TextStyle = new TextStyle { FontSize = 80, ColorHex = "#A3A3A3FF", HorizontalAlignment = TextHorizontalAlignment.Center },
                },
                new SlideObject
                {
                    Id = "item", ObjectKind = SlideObjectKind.Text, Name = "Item Remaining", Text = "", X = 100, Y = 300, Width = 1720, Height = 360,
                    TextLink = new TextLink { Source = TextSourceKind.Timer, TimerId = ServiceTrackingTimers.Id(ServiceTrackingTimers.Subject.Item, ServiceTrackingTimers.Face.Remaining) },
                    TextStyle = new TextStyle { FontSize = 300, ColorHex = "#FFFFFFFF", HorizontalAlignment = TextHorizontalAlignment.Center },
                },
                new SlideObject
                {
                    Id = "service", ObjectKind = SlideObjectKind.Text, Name = "Service Elapsed", Text = "", X = 100, Y = 720, Width = 1720, Height = 200,
                    TextLink = new TextLink { Source = TextSourceKind.Timer, TimerId = ServiceTrackingTimers.Id(ServiceTrackingTimers.Subject.Service, ServiceTrackingTimers.Face.Elapsed) },
                    TextStyle = new TextStyle { FontSize = 120, ColorHex = "#7ED957FF", HorizontalAlignment = TextHorizontalAlignment.Center },
                },
            ],
        };
        model.Library.Save(layout);
        model.SelectedStageLayout = model.StageLayouts.FirstOrDefault(l => l.Id == layout.Id);
        var stage = StageText();
        Check($"a stage layout shows Item Remaining (stage reads \"{stage.Replace('\n', ' ')}\")", stage.Contains("1:59") || stage.Contains("2:00"));
        await Snapshot("76-show-stage-tracking", model.StageScene());
        // The panel sits below the fold of the live column: scroll it into view for its picture.
        window.TrackingPanel.BringIntoView();
        await Step("77-show-tracking-panel", () => { }, settle: 500);
        window.LiveControls.BringIntoView();
        Check("the tracking panel names the item and what's next", model.TrackingItemName == "Welcome" && model.TrackingNext.StartsWith("Next ·", StringComparison.Ordinal));
        model.ClearServiceTrackingCommand.Execute(null);
        Check("Clear stops the tracking timers", !model.IsTrackingService
            && model.Timers.Snapshot(ServiceTrackingTimers.Id(ServiceTrackingTimers.Subject.Item, ServiceTrackingTimers.Face.Remaining)) is { IsRunning: false });
        model.ClearAllCommand.Execute(null);
    }

    /// <summary>Hidden rows, a row's output preset, Duplicate and Change Date.</summary>
    private async Task ServiceListChecks()
    {
        model.SetServiceItemHidden("flow-announce", true);
        Check("a hidden row leaves the run order for the Hidden group", FlowRowView("flow-announce") is { } hidden && !model.ServiceItems.Contains(hidden)
            && model.HiddenServiceItems.Contains(hidden) && model.HiddenGroupTitle == "HIDDEN (1)");
        if (FlowRowView("flow-welcome") is { } welcome)
        {
            model.FireServiceItemCommand.Execute(welcome);
            for (var i = 0; i < 3; i++) model.NextCommand.Execute(null);
            Check("Next walks past the hidden row", !AudienceText().Contains("Announcement") && model.Live?.ItemId != "flow-announce");
        }
        model.HiddenGroupOpen = true;
        await Step("78-show-hidden-group", () => { }, settle: 400);
        model.UnhideServiceItemCommand.Execute(FlowRowView("flow-announce"));
        Check("Unhide puts it back", model.ServiceItems.Any(r => r.Item.Id == "flow-announce") && !model.HasHiddenServiceItems);
        model.ClearAllCommand.Execute(null);

        var preset = new OutputPreset { Id = "selftest-flow-preset", Name = "Self-test look", Assignments = [] };
        model.Library.Save(preset);
        var before = model.Settings.ActiveOutputPresetId;
        model.SetServiceItemOutputPreset("flow-song", preset.Id);
        Check("a row can choose an output preset", model.SelectedService?.Items.FirstOrDefault(i => i.Id == "flow-song")?.OutputPresetId == preset.Id);
        if (FlowRowView("flow-song") is { } song)
        {
            model.FireServiceItemCommand.Execute(song);
            Check("firing the row switches to its output preset", model.Settings.ActiveOutputPresetId == preset.Id);
            model.ActivateOutputPreset(before is null ? null : model.Library.Get<OutputPreset>(before));
        }
        model.SetServiceItemOutputPreset("flow-song", null);
        Check("Keep Current Look clears it", model.SelectedService?.Items.FirstOrDefault(i => i.Id == "flow-song")?.OutputPresetId is null
            && model.Settings.ActiveOutputPresetId == before);
        model.ClearAllCommand.Execute(null);

        var count = model.SelectedService?.Items.Count;
        model.DuplicateServiceCommand.Execute(null);
        Check("Duplicate makes a copy of the service and opens it", model.SelectedService is { Name: "Self-test show flow Copy" } copy
            && copy.Id != FlowServiceId && copy.Items.Count == count);
        if (model.SelectedService is { } duplicate && duplicate.Id != FlowServiceId) model.Library.Delete<Service>(duplicate.Id);
        model.RefreshServices();
        model.SelectedService = model.Services.FirstOrDefault(s => s.Id == FlowServiceId);
        model.ChangeServiceDate(new DateOnly(2026, 12, 25));
        Check("Change Date sets the service's date", model.SelectedService?.ServiceDate == "2026-12-25" && model.Library.Get<Service>(FlowServiceId)?.ServiceDate == "2026-12-25");
    }

    /// <summary>Versions: one made here (edits stay in it, Changed badge, Use Main's, Main), and a Mac-made one this PC chooses.</summary>
    private async Task ServiceVersionChecks()
    {
        model.CreateServiceVersion(FlowServiceId, "11am");
        Check("a new version runs on this computer", model.SelectedServiceRunningVersion?.Name == "11am" && model.ServiceVersionName == "11am" && model.ServiceHasVersions);
        model.SetServiceItemHidden("flow-welcome", true);
        model.RenameServiceItem(FlowRowView("flow-song")!, "Closing song (11am)");
        var main = model.Library.Get<Service>(FlowServiceId)!;
        Check("edits while a version runs stay in the version", model.HiddenServiceItems.Any(r => r.Item.Id == "flow-welcome")
            && main.Items.First(i => i.Id == "flow-welcome").HiddenInPresenter is null && main.Items.First(i => i.Id == "flow-song").Name == "Closing song");
        Check("a row the version changed wears a Changed badge", FlowRowView("flow-song") is { IsChanged: true });
        await Step("79-show-version", () => model.SelectedServiceItem = FlowRowView("flow-song"), settle: 400);
        model.UseMainForServiceItem("flow-song");
        Check("Use Main's undoes the version's change", FlowRowView("flow-song") is { Name: "Closing song", IsChanged: false });
        model.ChooseServiceVersion(FlowServiceId, null);
        Check("choosing Main runs Main again", model.ServiceVersionName == "Main" && model.ServiceItems.Any(r => r.Item.Id == "flow-welcome"));
        var version = model.SelectedServiceVersions.First();
        model.ChooseServiceVersion(FlowServiceId, version.Id);
        model.RenameServiceVersion(FlowServiceId, version.Id, "11:00");
        Check("a version can be renamed and still runs here", model.ServiceVersionName == "11:00" && model.HiddenServiceItems.Any(r => r.Item.Id == "flow-welcome"));
        model.DeleteServiceVersion(FlowServiceId, version.Id);
        Check("deleting the version goes back to Main", !model.ServiceHasVersions && model.ServiceItems.Any(r => r.Item.Id == "flow-welcome"));

        // A service made on a Mac with a 9am version run by the Mac booth: this PC runs Main until it chooses.
        model.Library.Save(new Service
        {
            Id = "selftest-flow-mac", Name = "Self-test Mac service", ServiceDate = "2026-10-25",
            Items =
            [
                FlowRow("mac-1", ServiceItemKind.Presentation, "Welcome", "selftest-flow-welcome"),
                FlowRow("mac-2", ServiceItemKind.Presentation, "Song", "selftest-flow-song"),
            ],
            Versions =
            [
                new ServiceVersion
                {
                    Id = "nine", Name = "9am", Stations = [new ServiceVersionStation { Id = "MAC-BOOTH", Name = "Booth Mac" }],
                    Changes = [new ServiceItemChange { ItemId = "mac-2", RefId = "selftest-flow-loop" }],
                },
            ],
        });
        model.RefreshServices();
        model.SelectedService = model.Services.FirstOrDefault(s => s.Id == "selftest-flow-mac");
        Check("a Mac-made versioned service runs Main until this PC chooses", model.ServiceVersionName == "Main"
            && model.SelectedService?.Items.FirstOrDefault(i => i.Id == "mac-2")?.RefId == "selftest-flow-song");
        model.ChooseServiceVersion("selftest-flow-mac", "nine");
        if (model.ServiceItems.FirstOrDefault(r => r.Item.Id == "mac-2") is { } swapped)
        {
            model.FireServiceItemCommand.Execute(swapped);
            Check("it runs the version this PC chose", AudienceText().Contains("Loop A"));
        }
        Check("both computers are named on the version", model.Library.Get<Service>("selftest-flow-mac")?.Versions?[0].Stations?.Count == 2);
        model.ChooseServiceVersion("selftest-flow-mac", null);
        model.ClearAllCommand.Execute(null);
    }
}
