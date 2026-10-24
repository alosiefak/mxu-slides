using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.LocalApi;
using MxU.Slides.Windows.Outputs;
using MxU.Slides.Windows.Rendering;
using MxU.Slides.Windows.ViewModels;
using MxU.Slides.Windows.Views;
using RV = Pro.SerializationInterop.RVProtoData;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>
/// "MxUSlides.exe --self-test &lt;folder&gt;" drives the real app through a short
/// service (open a song, go live, next, edit, stage, clear), saves a picture
/// of each step and writes report.txt with pass/fail checks, then quits.
/// Use it to confirm a build works on a new computer.
/// </summary>
public sealed partial class SelfTest(MainWindow window, MainViewModel model, string folder)
{
    private readonly StringBuilder report = new();
    private int failures;

    public static string? FolderFromArgs(string[] args)
    {
        var index = Array.IndexOf(args, "--self-test");
        if (index < 0) return null;
        return index + 1 < args.Length ? args[index + 1] : Path.Combine(AppPaths.LocalRoot, "SelfTest");
    }

    public async Task RunAsync()
    {
        Directory.CreateDirectory(folder);
        report.AppendLine($"MxU Slides self-test {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"Displays: {string.Join("; ", Displays.All().Select(d => d.Label))}");
        try
        {
            await Step("01-start", () => { });
            Check("starter themes are in the library", model.Themes.Count >= 5);
            Check("the welcome service is open", model.SelectedService?.Name == "Getting Started");

            var songRow = model.ServiceItems.FirstOrDefault(r => r.Name == "Amazing Grace");
            Check("the sample song is in the service", songRow is not null);
            await Step("02-song-open", () => model.SelectedServiceItem = songRow);
            Check("the song shows its slides", model.Slides.Count >= 5);
            Check("slide 2 is Verse 1", model.Slides.ElementAtOrDefault(1)?.Label == "Verse 1");

            await Step("03-live-verse1", () => model.GoLiveCommand.Execute(model.Slides[1]), settle: 1200);
            var live = AudienceText();
            Check("the audience shows Verse 1", live.Contains("Amazing grace", StringComparison.OrdinalIgnoreCase));
            Check("the stage shows the current lyric", StageText().Contains("Amazing grace", StringComparison.OrdinalIgnoreCase));
            Check("the stage shows what's next", StageText().Contains("I once was lost", StringComparison.OrdinalIgnoreCase));
            Check("the live tile is marked", model.Slides[1].IsLive);
            await Snapshot("03-audience-frame", model.Show.Scene());
            await Snapshot("03-stage-frame", model.StageScene());

            // The starter lyric designs animate: the words rise in when a slide fires, and their Out is a click,
            // so (as on the Mac) the first Next plays the Out and the second moves on.
            Check("the lyric design brings the words in on its own", model.Show.SlideAnimationContext is not null && model.Show.SlideClickCount == 1);
            Check("a fired slide settles (no frames needed once its In finishes)",
                !Core.Animation.AnimationTimeline.IsAnimating(model.Show.AnimatedScene(), Core.Scene.ShowState.HostClock() + 5));
            await Step("04-build-out", () => model.NextCommand.Execute(null), settle: 900);
            Check("Next first plays the words' Out and stays on the slide", model.Show.SlideAnimationStep == (1, 1)
                && AudienceText().Contains("Amazing grace", StringComparison.OrdinalIgnoreCase) && VisibleAudienceText().Length == 0);
            Check("the live label shows the build", model.LiveDetail.Contains("build 1 of 1"));
            model.PreviousCommand.Execute(null);
            Check("Previous takes the Out back", model.Show.SlideAnimationStep == (0, 1));
            await Step("04-next", () => model.NextSkippingBuildsCommand.Execute(null), settle: 1200);
            Check("Alt+Right skips the builds to the following slide", AudienceText().Contains("I once was lost", StringComparison.OrdinalIgnoreCase)
                && VisibleAudienceText().Contains("I once was lost", StringComparison.OrdinalIgnoreCase));

            if (model.Settings.MidiMap.Resolve(0x90, 12, 100) is { } midiNext)
            {
                model.HandleMidi(midiNext.Command, midiNext.Velocity);
                model.HandleMidi(midiNext.Command, midiNext.Velocity);
                await Delay(400);
                Check("MIDI note C-1 (12) advances (Out, then the next slide)", AudienceText().Contains("'Twas grace", StringComparison.OrdinalIgnoreCase));
                var previous = model.Settings.MidiMap.Resolve(0x90, 13, 100)!.Value;
                model.HandleMidi(previous.Command, previous.Velocity);
                await Delay(400);
            }
            Check("hot key S jumps to Verse 2", model.FireHotKey("s") && AudienceText().Contains("'Twas grace", StringComparison.OrdinalIgnoreCase));
            Check("hot key C misses quietly when the song has no chorus", !model.FireHotKey("c"));
            model.AppendGoToDigit('9');
            Check("typing 9 then Enter goes to slide 9", model.CommitGoTo() && AudienceText().Contains("We've no less days", StringComparison.OrdinalIgnoreCase));
            model.HandleMidi(Core.Control.MidiCommand.TriggerSlide, 3);
            await Delay(300);

            var triggered = model.Settings.MidiMap.Resolve(0x90, 19, 3);
            Check("MIDI Trigger Slide with velocity 3 picks the third slide", triggered is { Command: Core.Control.MidiCommand.TriggerSlide, Velocity: 3 });

            var background = model.MediaItems.FirstOrDefault(m => m.Name == "Deep Blue");
            Check("starter backgrounds are in the media bin", model.MediaItems.Count >= 4 && background is not null);
            if (background is not null)
            {
                await Step("04-background", () => model.MediaClickedCommand.Execute(background), settle: 1500);
                Check("the background sits behind the lyrics",
                    model.Show.LiveMedia.TryGetValue(LayerKind.LoopingVideos, out var cue) && cue.MediaId == background.Item.Id
                    && AudienceText().Contains("I once was lost", StringComparison.OrdinalIgnoreCase));
                Check("the background image decodes", model.Media.Image(background.Item.Id, 1280) is not null);
                await Snapshot("04-background-frame", model.Show.Scene());
                model.NextCommand.Execute(null);
                await Delay(400);
                Check("the background stays when the slide changes", model.Show.LiveMedia.ContainsKey(LayerKind.LoopingVideos));
                model.PreviousCommand.Execute(null);
            }

            await ProgramFeedCheck();
            await VideoCheck();

            await Step("04-overlays", () => model.ShowingOverlays = true, settle: 800);
            Check("the starter overlays are in the bin", model.Overlays.Count >= 11);
            var namePlate = model.Overlays.FirstOrDefault(o => o.Name == "Name + Title");
            Check("there is a Name + Title lower third", namePlate is not null);
            if (namePlate is not null)
            {
                await Step("04-overlay-live", () =>
                {
                    model.ToggleOverlayCommand.Execute(namePlate);
                    var nameField = model.OverlayTextFields.FirstOrDefault(f => f.Name == "Name");
                    if (nameField is not null) nameField.Text = "Pastor Ade Johnson";
                }, settle: 1200);
                Check("the lower third is live over the lyrics", AudienceText().Contains("Pastor Ade Johnson") && AudienceText().Contains("I once was lost"));
                Check("mattes are not drawn", model.Show.Scene().AllItems.Where(i => i.MatteGroup is not null).All(i => i.Id.Contains("mask")));
                await Snapshot("04-overlay-frame", model.Show.Scene());
                foreach (var t in new[] { 0.15, 0.35, 0.6 })
                    await SnapshotAt($"04-overlay-in-{t:0.00}s", model.Show.AnimatedScene(), t);
                model.ClearOverlaysCommand.Execute(null);
                Check("F4 plays the lower third's Out before taking it down", model.Show.IsLeaving(namePlate.Overlay.Id));
                await Delay(2500);
                Check("F4 clears the overlay once its Out finishes", !AudienceText().Contains("Pastor Ade Johnson"));
            }
            model.ShowingOverlays = false;

            await Step("05-alert", () =>
            {
                model.AlertText = "Parent of child 47, please come to the nursery";
                model.AlertOnAudience = true;
                model.ShowAlertCommand.Execute(null);
            }, settle: 800);
            Check("the message shows on the audience", AudienceText().Contains("child 47"));
            Check("the message shows on the stage", StageText().Contains("child 47"));
            await Snapshot("05-audience-alert", model.Show.Scene());
            model.ClearAlertsCommand.Execute(null);

            await Step("06-themes", () => { });
            foreach (var theme in model.Library.All<Theme>())
            {
                var song = model.CurrentPresentation!;
                var slide = SlideSceneBuilder.ArrangedSlides(song, model.SelectedArrangement?.Id)[1];
                await Snapshot($"06-theme-{Safe(theme.Name)}", SlideSceneBuilder.Scene(slide, theme, song));
                foreach (var design in theme.Slides ?? [])
                {
                    var designed = Core.Json.MxuJson.Clone(slide);
                    designed.ThemeSlideName = design.Name;
                    await Snapshot($"06-theme-{Safe(theme.Name)}-{Safe(design.Name)}", SlideSceneBuilder.Scene(designed, theme, song));
                }
            }

            await Step("07-edit-mode", () =>
            {
                model.IsEditing = true;
                model.SelectedSlide = model.Slides[2];
            });
            Check("editing shows the slide's text box", model.SlideTextFields.Count == 1);

            var editedSlide = model.SelectedSlide!.Slide;
            var lyric = editedSlide.Objects.First(o => o.ObjectKind == SlideObjectKind.Text);
            window.CanvasEditor.Select([lyric.Id]);
            Check("selecting on the canvas opens the inspector", model.Inspector is { IsText: true, InheritsTheme: true });
            if (model.Inspector is { } inspector)
            {
                Check("the inspector shows the theme's size", inspector.FontSize == "84");
                Check("the font box lists the slide's own font (even one this PC hasn't got)",
                    inspector.FontFamily.Length > 0 && inspector.FontChoices.Contains(inspector.FontFamily));
                await Step("07-text-inspector", () => { }, settle: 400);
                inspector.FontSize = "100";
                inspector.TextColor = "#FACC15FF";
                Check("changing size writes an override", lyric.TextStyle?.FontSize == 100 && lyric.TextStyle?.ColorHex == "#FACC15FF");
            }
            var before = editedSlide.Objects.Count;
            model.AddBoxCommand.Execute(null);
            Check("+ Box adds a shape and selects it", editedSlide.Objects.Count == before + 1 && model.Inspector is { IsShape: true });
            model.AlignCommand.Execute("CenterX");
            var box = editedSlide.Objects[^1];
            Check("align centres the box on the slide", box.X is { } bx && Math.Abs(bx + (box.Width ?? 0) / 2 - 960) < 0.5);
            await Step("07-canvas", () => { }, settle: 600);
            model.DeleteSelectedObjectsCommand.Execute(null);
            window.CanvasEditor.Select([lyric.Id]);
            model.ResetToThemeCommand.Execute(null);
            Check("reset to theme removes the overrides", lyric.TextStyle is null && editedSlide.Objects.Count == before);

            var editor = new LyricsEditorWindow(model, model.CurrentPresentation) { Owner = window };
            editor.Show();
            await Delay(900);
            Save(editor, "08-lyrics-editor");
            editor.Close();
            model.IsEditing = false;

            model.Outputs?.ShowPreviewWindow(OutputRole.Audience);
            model.Outputs?.ShowPreviewWindow(OutputRole.Stage);
            model.RenderOutputs(SceneTransition.Cut);
            await Delay(1200);
            if (model.Outputs is { } outputs)
            {
                if (outputs[OutputRole.Audience] is { } audienceWindow) Save(audienceWindow, "09-audience-window");
                if (outputs[OutputRole.Stage] is { } stageWindow) Save(stageWindow, "09-stage-window");
                Check("the audience preview window is showing", outputs.IsShowing(OutputRole.Audience));
            }

            await Step("10-cleared", () => model.ClearAllCommand.Execute(null), settle: 1000);
            Check("Clear All empties the audience", model.Show.IsEmpty && AudienceText().Length == 0);

            var welcome = model.ServiceItems.FirstOrDefault(r => r.Name == "Welcome to MxU Slides");
            await Step("11-welcome-deck", () =>
            {
                model.SelectedServiceItem = welcome;
                model.GoLiveCommand.Execute(model.Slides.FirstOrDefault());
            }, settle: 1200);
            Check("a deck slide goes live", AudienceText().Contains("Welcome to MxU Slides"));
            await Snapshot("11-welcome-frame", model.Show.Scene());
            model.ClearAllCommand.Execute(null);

            await EffectsCheck();
            await MusicCheck();
            await LiveInputCheck();
            await TimelineCheck();
            await RecordingCheck();
            await MidiDevicesCheck();
            await ActionsCheck();
            await ScreensCheck();
            await ApiCheck();
            await PowerPointCheck();
            await ProPresenterCheck();
            await RenderCheck();
            await EditorsCheck();
            await TimelineEditorCheck();
            await OverlayEditorChecks();
            await BroadcastExtrasCheck();
            await LibraryCheck();
        }
        catch (Exception ex)
        {
            failures++;
            report.AppendLine($"CRASH {ex}");
        }
        report.AppendLine(failures == 0 ? "RESULT: PASS" : $"RESULT: FAIL ({failures} problem{(failures == 1 ? "" : "s")})");
        await File.WriteAllTextAsync(Path.Combine(folder, "report.txt"), report.ToString());
        Log.Info($"Self-test finished with {failures} failure(s); report in {folder}");
    }

    /// <summary>Writes a two-slide ProPresenter 7 song (Verse 1, Chorus) as a .pro file, imports it and re-imports it.</summary>
    private async Task ProPresenterCheck()
    {
        var path = Path.Combine(Path.GetTempPath(), $"MxU self-test {Guid.NewGuid():N}.pro");
        var chordPro = Path.Combine(Path.GetTempPath(), $"MxU self-test {Guid.NewGuid():N}.pro");
        var asked = new List<string>();
        var ask = model.AskYesNo;
        model.AskYesNo = question =>
        {
            asked.Add(question);
            return true;
        };
        try
        {
            File.WriteAllText(chordPro, "{title: Test}\n[G]Amazing [C]grace");
            WriteProSong(path);
            Check("a ChordPro .pro is read as lyrics, a ProPresenter .pro as ProPresenter",
                MainViewModel.IsProPresenterFile(path) && !MainViewModel.IsProPresenterFile(chordPro));
            var result = await model.ImportProPresenterAsync([path]);
            var summary = result?.Documents.FirstOrDefault();
            Check("a ProPresenter 7 song imports", summary is { Imported: true });
            var song = model.Library.Get<Presentation>(summary?.PresentationId);
            Check("its slides and groups come across", song?.Slides.Count == 2 && song.Sections?.Select(g => g.Name).SequenceEqual(["Verse 1", "Chorus"]) == true);
            Check("it lands in the ProPresenter Import folder and opens", song?.Folder == "ProPresenter Import" && model.CurrentPresentation?.Id == song.Id);
            if (song is not null && model.Slides.Count == 2)
            {
                await Step("14-propresenter", () => model.GoLiveCommand.Execute(model.Slides[1]), settle: 1000);
                Check("an imported ProPresenter slide goes live", AudienceText().Contains("Sing it again"));
                model.ClearAllCommand.Execute(null);
            }
            var again = await model.ImportProPresenterAsync([path]);
            Check("importing it again asks before replacing", asked.Count == 1 && again?.Documents.FirstOrDefault()?.PresentationId == song?.Id);
        }
        finally
        {
            model.AskYesNo = ask;
            foreach (var file in new[] { path, chordPro })
            {
                try { File.Delete(file); }
                catch (IOException) { }
            }
        }

        static void WriteProSong(string path)
        {
            static RV.UUID Id(string value) => new() { String = value };
            RV.Cue Cue(string id, string text) => new()
            {
                Uuid = Id(id),
                Actions =
                {
                    new RV.Action
                    {
                        Uuid = Id(id + "-A"),
                        Slide = new()
                        {
                            Presentation = new()
                            {
                                BaseSlide = new()
                                {
                                    Size = new() { Width = 1920, Height = 1080 },
                                    Elements =
                                    {
                                        new RV.Slide.Types.Element
                                        {
                                            Element_ = new()
                                            {
                                                Uuid = Id(id + "-E"),
                                                Bounds = new() { Origin = new() { X = 160, Y = 240 }, Size = new() { Width = 1600, Height = 600 } },
                                                Opacity = 1,
                                                Text = new()
                                                {
                                                    RtfData = Google.Protobuf.ByteString.CopyFromUtf8(@"{\rtf1\ansi{\fonttbl\f0\fnil Arial;}\f0\fs144 " + text + "}"),
                                                    Attributes = new() { Font = new() { Name = "Arial", Size = 72 } },
                                                },
                                            },
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            };
            var document = new RV.Presentation
            {
                Uuid = Id("5E1F7E57-0000-4000-8000-000000000001"),
                Name = "Self-test Hymn",
                Cues = { Cue("CUE-V1", "Verse one line"), Cue("CUE-C1", "Sing it again") },
            };
            foreach (var (id, name, cue) in new[] { ("GRP-V1", "Verse 1", "CUE-V1"), ("GRP-C1", "Chorus", "CUE-C1") })
            {
                var group = new RV.Presentation.Types.CueGroup { Group = new() { Uuid = Id(id), Name = name } };
                group.CueIdentifiers.Add(Id(cue));
                document.CueGroups.Add(group);
            }
            File.WriteAllBytes(path, Google.Protobuf.MessageExtensions.ToByteArray(document));
        }
    }

    /// <summary>Builds a two-slide .pptx (title + fading body text), imports it and puts its second slide live.</summary>
    private async Task PowerPointCheck()
    {
        var path = Path.Combine(Path.GetTempPath(), $"MxU self-test deck {Guid.NewGuid():N}.pptx");
        try
        {
            WriteDeck(path);
            var results = await model.ImportPowerPointAsync([path]);
            var result = results.FirstOrDefault();
            Check("a PowerPoint deck imports", result is { Succeeded: true });
            var deck = model.Library.Get<Presentation>(result?.PresentationId);
            Check("the deck has both slides", deck?.Slides.Count == 2);
            Check("the deck lands in the PowerPoint Import folder", deck?.Folder == "PowerPoint Import");
            Check("the imported deck opens", model.CurrentPresentation?.Id == deck?.Id && model.Slides.Count == 2);
            if (deck is null || model.Slides.Count < 2) return;
            await Step("13-powerpoint", () => model.GoLiveCommand.Execute(model.Slides[1]), settle: 1200);
            Check("an imported slide goes live", AudienceText().Contains("Welcome, everyone"));
            Check("its PowerPoint click animation waits for Next", model.Show.SlideClickCount == 1 && !VisibleAudienceText().Contains("Welcome, everyone"));
            model.NextCommand.Execute(null);
            await Delay(120);
            if (model.Outputs?[OutputRole.Audience] is { } fading) Save(fading, "13-powerpoint-fading-in");
            await Delay(900);
            Check("Next fades the text in", VisibleAudienceText().Contains("Welcome, everyone"));
            if (model.Outputs?[OutputRole.Audience] is { } faded) Save(faded, "13-powerpoint-faded-in");
            model.ClearAllCommand.Execute(null);
            var again = await model.ImportPowerPointAsync([path]);
            Check("importing the same deck again replaces it", again.FirstOrDefault()?.PresentationId == deck.Id
                && model.Library.All<Presentation>().Count(p => p.Folder == "PowerPoint Import") == 1);
        }
        finally
        {
            try { File.Delete(path); }
            catch (IOException) { }
        }

        static void WriteDeck(string path)
        {
            const string ns = "xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";
            const string rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            static string Slide(string text, int y, string timing) =>
                $"<p:sld {ns}><p:cSld><p:spTree><p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Text\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>"
                + $"<p:spPr><a:xfrm><a:off x=\"914400\" y=\"{y}\"/><a:ext cx=\"10363200\" cy=\"1371600\"/></a:xfrm><a:prstGeom prst=\"rect\"/></p:spPr>"
                + $"<p:txBody><a:bodyPr/><a:p><a:pPr algn=\"ctr\"/><a:r><a:rPr sz=\"5400\"><a:solidFill><a:srgbClr val=\"FFFFFF\"/></a:solidFill><a:latin typeface=\"Arial\"/></a:rPr><a:t>{text}</a:t></a:r></a:p></p:txBody>"
                + $"</p:sp></p:spTree></p:cSld>{timing}</p:sld>";
            const string fade = "<p:timing><p:tnLst><p:par><p:cTn id=\"1\" dur=\"indefinite\" nodeType=\"tmRoot\"><p:childTnLst><p:seq concurrent=\"1\" nextAc=\"seek\"><p:cTn id=\"2\" dur=\"indefinite\" nodeType=\"mainSeq\"><p:childTnLst>"
                + "<p:par><p:cTn id=\"3\" fill=\"hold\"><p:stCondLst><p:cond delay=\"indefinite\"/></p:stCondLst><p:childTnLst><p:par><p:cTn id=\"4\" fill=\"hold\"><p:stCondLst><p:cond delay=\"0\"/></p:stCondLst><p:childTnLst>"
                + "<p:par><p:cTn id=\"5\" presetID=\"10\" presetClass=\"entr\" presetSubtype=\"0\" fill=\"hold\" nodeType=\"clickEffect\"><p:stCondLst><p:cond delay=\"0\"/></p:stCondLst><p:childTnLst>"
                + "<p:set><p:cBhvr><p:cTn id=\"6\" dur=\"1\" fill=\"hold\"><p:stCondLst><p:cond delay=\"0\"/></p:stCondLst></p:cTn><p:tgtEl><p:spTgt spid=\"2\"/></p:tgtEl><p:attrNameLst><p:attrName>style.visibility</p:attrName></p:attrNameLst></p:cBhvr><p:to><p:strVal val=\"visible\"/></p:to></p:set>"
                + "<p:animEffect transition=\"in\" filter=\"fade\"><p:cBhvr><p:cTn id=\"7\" dur=\"500\"/><p:tgtEl><p:spTgt spid=\"2\"/></p:tgtEl></p:cBhvr></p:animEffect>"
                + "</p:childTnLst></p:cTn></p:par></p:childTnLst></p:cTn></p:par></p:childTnLst></p:cTn></p:par></p:childTnLst></p:cTn></p:seq></p:childTnLst></p:cTn></p:par></p:tnLst></p:timing>";
            var parts = new Dictionary<string, string>
            {
                ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>"
                    + "<Override PartName=\"/ppt/presentation.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml\"/>"
                    + "<Override PartName=\"/ppt/slides/slide1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slide+xml\"/>"
                    + "<Override PartName=\"/ppt/slides/slide2.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slide+xml\"/></Types>",
                ["_rels/.rels"] = $"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"{rel}/officeDocument\" Target=\"ppt/presentation.xml\"/></Relationships>",
                ["ppt/presentation.xml"] = $"<p:presentation {ns}><p:sldIdLst><p:sldId id=\"256\" r:id=\"rId2\"/><p:sldId id=\"257\" r:id=\"rId3\"/></p:sldIdLst><p:sldSz cx=\"12192000\" cy=\"6858000\"/></p:presentation>",
                ["ppt/_rels/presentation.xml.rels"] = $"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId2\" Type=\"{rel}/slide\" Target=\"slides/slide1.xml\"/><Relationship Id=\"rId3\" Type=\"{rel}/slide\" Target=\"slides/slide2.xml\"/></Relationships>",
                ["ppt/slides/slide1.xml"] = Slide("Sunday Service", 2743200, ""),
                ["ppt/slides/slide2.xml"] = Slide("Welcome, everyone", 2743200, fade),
            };
            using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
            foreach (var (name, xml) in parts)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" + xml);
            }
        }
    }

    /// <summary>
    /// Turns the Local API on (on a free port), then runs the show over HTTP and a
    /// WebSocket the way a phone remote or Companion would, and turns it back off.
    /// </summary>
    private async Task ApiCheck()
    {
        var s = model.Settings;
        var (wasOn, wasPort) = (s.LocalApiEnabled, s.LocalApiPort);
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        s.LocalApiEnabled = true;
        s.LocalApiPort = port;
        model.ApplyApiSettings();
        try
        {
            Check("the Local API starts", model.ApiStatus.StartsWith("On", StringComparison.Ordinal));
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
            Check("the API refuses a request without a key", (await http.GetAsync("/v1/status")).StatusCode == HttpStatusCode.Unauthorized);
            var key = model.ApiTokens.EnsureDefaultKey(evenIfPopulated: true)!;
            http.DefaultRequestHeaders.Authorization = new("Bearer", key);

            Check("GET /v1/library counts the songs", (await http.GetStringAsync("/v1/library")).Contains("\"presentations\""));
            var song = model.Library.All<Presentation>().First(p => p.Name == "Amazing Grace");
            Check("GET /v1/library/presentations lists Amazing Grace", (await http.GetStringAsync("/v1/library/presentations")).Contains(song.Id));

            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/v1/ws?token={key}"), CancellationToken.None);
            await socket.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"subscribe\",\"topics\":[\"show\"]}"), WebSocketMessageType.Text, true, CancellationToken.None);
            var frames = new List<string>();
            var reading = ReadFrames(socket, frames);
            await Delay(300);

            var fired = await http.PostAsync("/v1/show/slide", Json($"{{\"presentationId\":\"{song.Id}\",\"slideIndex\":1}}"));
            Check("POST /v1/show/slide puts Verse 1 live", fired.IsSuccessStatusCode && AudienceText().Contains("Amazing grace", StringComparison.OrdinalIgnoreCase));
            var status = await http.GetStringAsync("/v1/status");
            Check("GET /v1/status reports the live slide", status.Contains(song.Id) && status.Contains("\"slideIndex\":1"));
            await http.PostAsync("/v1/show/advance", Json("{\"steps\":1}"));
            Check("POST /v1/show/advance plays the slide's build first", model.Show.SlideAnimationStep == (1, 1)
                && (await http.GetStringAsync("/v1/status")).Contains("\"stepIndex\":1"));
            await http.PostAsync("/v1/show/advance", Json("{\"steps\":-1}"));
            await http.PostAsync("/v1/show/advance", Json("{\"steps\":1,\"settled\":true}"));
            Check("POST /v1/show/advance with settled skips to the next slide", AudienceText().Contains("I once was lost", StringComparison.OrdinalIgnoreCase));
            var outOfRange = await http.PostAsync("/v1/show/slide", Json($"{{\"presentationId\":\"{song.Id}\",\"slideIndex\":99}}"));
            Check("an out-of-range slide is a 400 with a reason", outOfRange.StatusCode == HttpStatusCode.BadRequest
                && (await outOfRange.Content.ReadAsStringAsync()).Contains("out of range"));
            await http.PostAsync("/v1/alerts/fire", Json("{\"message\":\"Sent from the API\",\"target\":\"both\"}"));
            Check("POST /v1/alerts/fire shows a message", AudienceText().Contains("Sent from the API"));
            await Step("12-api", () => { }, settle: 600);
            await http.PostAsync("/v1/show/clear-all", null);
            Check("POST /v1/show/clear-all clears everything", model.Show.IsEmpty);

            await Delay(500);
            lock (frames) Check("the WebSocket pushes show changes", frames.Any(f => f.Contains("\"topic\":\"show\"") && f.Contains(song.Id)));
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            await Task.WhenAny(reading, Task.Delay(2000));

            var (viewer, viewerSecret) = model.ApiTokens.Create("self-test viewer", ApiScope.View);
            using var viewOnly = new HttpClient { BaseAddress = http.BaseAddress, Timeout = http.Timeout };
            viewOnly.DefaultRequestHeaders.Authorization = new("Bearer", viewerSecret);
            Check("a View key can read", (await viewOnly.GetAsync("/v1/status")).IsSuccessStatusCode);
            Check("a View key can't run the show", (await viewOnly.PostAsync("/v1/show/clear-all", null)).StatusCode == HttpStatusCode.Forbidden);
            model.ApiTokens.Revoke(viewer.Id);
            Check("a revoked key stops working", (await viewOnly.GetAsync("/v1/status")).StatusCode == HttpStatusCode.Unauthorized);
        }
        catch (Exception ex)
        {
            Check($"the Local API answers ({ex.Message})", false);
        }
        finally
        {
            s.LocalApiEnabled = wasOn;
            s.LocalApiPort = wasPort;
            model.ApplyApiSettings();
        }

        static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

        static async Task ReadFrames(ClientWebSocket socket, List<string> frames)
        {
            var buffer = new byte[64 * 1024];
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    using var message = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                        message.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    lock (frames) frames.Add(Encoding.UTF8.GetString(message.ToArray()));
                }
            }
            catch (WebSocketException)
            {
            }
        }
    }

    /// <summary>A destination that counts frames and keeps the last one, like a recorder would see them.</summary>
    private sealed class CountingSink : Core.Media.IProgramSink
    {
        private readonly Lock gate = new();
        public int Frames;
        public byte[]? Last;
        public int Width, Height;

        public void WriteVideo(Core.Media.VideoFrame frame)
        {
            lock (gate)
            {
                Frames++;
                Width = frame.Width;
                Height = frame.Height;
                Last = frame.Pixels.ToArray();
            }
        }

        public void WriteAudio(Core.Media.AudioBuffer buffer) { }
    }

    /// <summary>Listens to the program feed for three seconds with lyrics over a background, and checks rate and content.</summary>
    private async Task ProgramFeedCheck()
    {
        if (model.Program is not { } feed) return;
        var sink = new CountingSink();
        feed.Add(sink);
        await Delay(500);
        var start = sink.Frames;
        await Delay(3000);
        var fps = (sink.Frames - start) / 3.0;
        var (pictures, late, slowCopy, problem) = (feed.FramesCaptured, feed.FramesLate, feed.UsesSoftwareCopy, feed.Problem);
        feed.Remove(sink);
        report.AppendLine($"INFO  program feed: {fps:0.0} fps at {sink.Width}x{sink.Height}, {pictures} new pictures, {late} late"
            + (problem is not null ? $"; {problem}" : ""));
        if (slowCopy)
        {
            // A PC (or CI machine) without Windows' capture: the slower copy must still work.
            Skip("the program feed at 25+ frames a second", $"{problem} It delivered {fps:0.0} a second");
            Check("the slower software copy still delivers frames", fps > 0);
        }
        else
        {
            Check("the program feed delivers frames steadily (25+ per second)", fps >= 25);
        }
        Check("program frames are full HD", sink.Width == 1920 && sink.Height == 1080);
        if (sink.Last is not { } pixels) return;
        // The lyrics are white, centred on the canvas: some pixels there must be bright.
        var bright = 0;
        for (var y = sink.Height * 2 / 5; y < sink.Height * 3 / 5; y += 2)
            for (var x = sink.Width / 4; x < sink.Width * 3 / 4; x += 2)
            {
                var i = (y * sink.Width + x) * 4;
                if (pixels[i] > 200 && pixels[i + 1] > 200 && pixels[i + 2] > 200) bright++;
            }
        Check("a program frame shows the lyrics", bright > 200);
        var bitmap = BitmapSource.Create(sink.Width, sink.Height, 96, 96, PixelFormats.Pbgra32, null, pixels, sink.Width * 4);
        Write(bitmap, "04-program-frame");
    }

    /// <summary>
    /// Live inputs, end to end over NDI: the test sends a magenta picture as an NDI source, adds
    /// an input that receives it, puts it live under the slides, and checks magenta reaches the
    /// program feed. Skipped where NDI Tools isn't installed.
    /// </summary>
    private async Task LiveInputCheck()
    {
        if (!VideoIO.Ndi.NdiLibrary.TryLoad(out _, out var error))
        {
            Skip("live inputs over NDI", error.ToString() ?? "NDI isn't installed");
            return;
        }
        const string source = "MxU self-test camera";
        using var sender = new VideoIO.Ndi.NdiOutput(source);
        sender.Start();
        int w = 640, h = 360;
        var pixels = new byte[w * h * 4];
        for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (230, 20, 230, 255);
        using var stop = new CancellationTokenSource();
        var pump = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                sender.WriteVideo(new Core.Media.VideoFrame(w, h, w * 4, pixels, ShowState.HostClock()));
                await Task.Delay(33);
            }
        });
        var entry = model.Inputs.Create("Self-test camera", CaptureSourceKind.Ndi, source);
        try
        {
            model.RefreshInputs();
            var tile = model.InputTiles.FirstOrDefault(t => t.Entry.Id == entry.Id);
            Check("a live input appears as a tile in the Media tab", tile is not null);
            if (tile is null) return;
            model.InputClickedCommand.Execute(tile);
            for (var i = 0; i < 100 && model.Media.LiveInputPicture(entry.EngineId) is null; i++) await Delay(100);
            Check("an NDI source shows as a live input", model.Media.LiveInputPicture(entry.EngineId) is { PixelWidth: 640 });
            if (model.Program is { } feed)
            {
                var sink = new CountingSink();
                feed.Add(sink);
                await Delay(1200);
                feed.Remove(sink);
                if (sink.Last is { } frame && sink.Width > 0)
                {
                    var c = (sink.Height / 2 * sink.Width + sink.Width / 2) * 4;
                    Check("the input's picture reaches the program (under the slides)", frame[c + 2] > 180 && frame[c + 1] < 80 && frame[c] > 180);
                    Write(BitmapSource.Create(sink.Width, sink.Height, 96, 96, PixelFormats.Pbgra32, null, frame, sink.Width * 4), "18-live-input");
                }
            }
        }
        finally
        {
            stop.Cancel();
            await pump;
            model.ClearAllCommand.Execute(null);
            model.Inputs.Remove(entry.Id);
            model.RefreshInputs();
        }
    }

    /// <summary>
    /// MIDI devices: a listed device shows in the Local API, and once a device takes input, MIDI
    /// control listens only to it (here it isn't plugged in, so nothing counts); removing it goes
    /// back to listening to every input.
    /// </summary>
    private async Task MidiDevicesCheck()
    {
        await model.RefreshMidiDevicesAsync();
        Check("with no MIDI devices listed, every input counts", model.MidiDevices.Count > 0 || model.AcceptsMidiForTest("any-port", 0x90));
        var device = model.AddMidiDevice("Self-test desk", MIDIDeviceItemDirection.Both, null, null);
        try
        {
            var status = ((IApiBridge)model).MidiDevices().FirstOrDefault(d => d.Id == device.Id);
            Check("a listed MIDI device shows in the Local API (not connected)", status is { Name: "Self-test desk", Enabled: true, Connected: false, Direction: "both" });
            Check("once a device takes input, other ports don't count", !model.AcceptsMidiForTest("any-port", 0x90));
            model.UpdateMidiDevice(device.Id, d => d.Direction = MIDIDeviceItemDirection.Output);
            Check("an output-only device leaves MIDI control listening to every input", model.AcceptsMidiForTest("any-port", 0x90));
            ((IApiBridge)model).SetMidiDeviceEnabled(device.Id, false);
            Check("the Local API turns a MIDI device off", model.MidiDevices.Single(d => d.Id == device.Id).IsEnabled == false);
        }
        finally
        {
            model.RemoveMidiDevice(device.Id);
        }
        Check("removing it goes back to every input", model.MidiDevices.Count > 0 || model.AcceptsMidiForTest("any-port", 0x90));
    }

    /// <summary>
    /// Recording: a record-only preset records the program (a slide, with its fade) for a few
    /// seconds into the sandbox, and stopping it leaves a finished MP4 (not a .partial one).
    /// The Stream &amp; Record window is pictured while it records.
    /// </summary>
    private async Task RecordingCheck()
    {
        if (model.Program is null)
        {
            Skip("recording", "no program feed");
            return;
        }
        var folder = Path.Combine(Path.GetTempPath(), "MxU Slides self-test recordings");
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        model.Settings.RecordingsFolder = folder;
        var preset = new StreamRecordPreset
        {
            Name = "Self-test recording", PresetKind = StreamPresetKind.RecordOnly,
            Width = 1280, Height = 720, FrameRate = 30, VideoBitrateKbps = 3000,
        };
        model.Library.Save(preset);
        BroadcastWindow? window = null;
        try
        {
            var problem = model.StartBroadcast(preset);
            if (problem is not null && (problem.Contains("encoder", StringComparison.OrdinalIgnoreCase) || problem.Contains("Media Foundation", StringComparison.OrdinalIgnoreCase)))
            {
                Skip("recording", problem);
                return;
            }
            Check("a record-only preset starts recording", problem is null && model.IsBroadcasting);
            if (model.ServiceItems.FirstOrDefault(r => r.Name == "Amazing Grace") is { } song)
            {
                model.SelectedServiceItem = song;
                model.GoLiveCommand.Execute(model.Slides.ElementAtOrDefault(1));
            }
            window = new BroadcastWindow(model) { Owner = Application.Current.MainWindow };
            window.Show();
            await Delay(2500);
            Check("the toolbar shows REC and a running clock", model.BroadcastButtonText.Contains("REC", StringComparison.Ordinal));
            Save(window, "20-recording");
            model.StopBroadcast(preset.Id);
            var session = model.Broadcast.Recording.Sessions.LastOrDefault(r => r.PresetId == preset.Id);
            for (var i = 0; i < 100 && session?.Status.State.Kind is Broadcast.Recording.RecorderStateKind.Recording or Broadcast.Recording.RecorderStateKind.Finishing; i++)
                await Delay(100);
            var file = await RecordedFileAsync(session);
            var size = file is not null && File.Exists(file) ? new FileInfo(file).Length : 0;
            report.AppendLine($"      recording: {file} ({size} bytes, {session?.Status.State.Kind}, {session?.Status.DroppedFrames} dropped)");
            Check("stopping leaves a finished MP4 with pictures in it", session?.Status.State.Kind == Broadcast.Recording.RecorderStateKind.Finished
                && file?.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) == true && !file.EndsWith(".partial.mp4", StringComparison.OrdinalIgnoreCase)
                && size > 10_000);
            Check("the toolbar goes back to Stream & Record", !model.IsBroadcasting && model.BroadcastButtonText == "Stream & Record");
        }
        finally
        {
            window?.Close();
            model.StopBroadcast(null);
            model.ClearAllCommand.Execute(null);
            model.Library.Delete<StreamRecordPreset>(preset.Id);
        }
    }

    /// <summary>
    /// Song timelines: records a take (slides 2 and 3 clicked a moment apart), plays it back
    /// from the start and checks the slides change by themselves, in order, then puts the
    /// song back as it was. The timeline bar is pictured while it plays.
    /// </summary>
    private async Task TimelineCheck()
    {
        var songRow = model.ServiceItems.FirstOrDefault(r => r.Name == "Amazing Grace");
        if (songRow is null)
        {
            Skip("song timelines", "the sample song isn't in the service");
            return;
        }
        model.SelectedServiceItem = songRow;
        await Delay(300);
        if (model.CurrentPresentation is not { } song || model.Slides.Count < 3) return;
        var before = song.Timelines?.ToList();
        var second = model.Slides[1].Slide.Id;
        var third = model.Slides[2].Slide.Id;
        try
        {
            model.TimelineRecordCommand.Execute(null);
            Check("recording a timeline starts and shows the timeline bar", model.TimelineRecording && model.ShowsTimelineBar);
            await Delay(400);
            model.GoLiveCommand.Execute(model.Slides[1]);
            await Delay(700);
            model.GoLiveCommand.Execute(model.Slides[2]);
            await Delay(300);
            model.TimelineRecordCommand.Execute(null);
            var cues = model.CurrentTimeline?.Cues.Where(c => c.Lane == "slide").OrderBy(c => c.At).ToList() ?? [];
            Check("the take recorded the two clicks as cues", cues.Count == 2 && cues[1].At - cues[0].At is > 0.4 and < 1.2);

            model.ClearAllCommand.Execute(null);
            model.TimelineRewindCommand.Execute(null);
            model.TimelinePlayPauseCommand.Execute(null);
            Check("the timeline plays", model.TimelinePlaying);
            var seen = new List<string>();
            var pictured = false;
            var deadline = DateTime.Now.AddSeconds(4);
            while (DateTime.Now < deadline && !seen.Contains(third))
            {
                if (model.Show.Live?.Slide.Id is { } live && (seen.Count == 0 || seen[^1] != live)) seen.Add(live);
                if (!pictured && seen.Count > 0)
                {
                    pictured = true;
                    await Step("19-timeline", () => { }, settle: 100);
                }
                await Delay(40);
            }
            Check("playing it fires the slides by themselves, in order", seen.SequenceEqual([second, third]));
            Check("the Local API reports timecode in (none on this PC)", ((IApiBridge)model).TimecodeStatus() is { HasInput: false, Source: "ltc" });
        }
        finally
        {
            model.TimelineRewindCommand.Execute(null);
            model.ClearAllCommand.Execute(null);
            song.Timelines = before;
            model.Library.Save(song);
            model.TimelineBarOpen = false;
        }
    }

    /// <summary>
    /// Music: a short generated tone imports into the library, plays from its tile (even on a PC
    /// with no sound card the engine keeps time), and F5 stops it.
    /// </summary>
    private async Task MusicCheck()
    {
        var path = Path.Combine(Path.GetTempPath(), $"MxU self-test tone {Guid.NewGuid():N}.wav");
        try
        {
            WriteTone(path, seconds: 6);
            model.ImportAudioFiles([path]);
            var tile = model.AudioTiles.FirstOrDefault(t => t.Item is not null && t.Name.StartsWith("MxU self-test tone", StringComparison.Ordinal));
            Check("a music file imports into the library", tile is { Item.DurationSeconds: > 5 });
            if (tile is null) return;
            model.Bin = BinTab.Music;
            model.AudioTileClickedCommand.Execute(tile);
            await Delay(1200);
            Check("music plays from its tile", tile.IsPlaying && model.Audio.Status().Any(b => b.Playing && b.AudioItemId == tile.Item!.Id));
            Check("now playing shows the song and time", model.NowPlaying.Contains("MxU self-test tone"));
            await Step("17-music", () => { }, settle: 300);
            model.ClearAudioCommand.Execute(null);
            await Delay(2500);
            Check("F5 stops the music", !model.Audio.Status().Any(b => b.Playing));
            model.Bin = BinTab.Media;
        }
        finally
        {
            try { File.Delete(path); }
            catch (IOException) { }
        }

        static void WriteTone(string path, double seconds)
        {
            const int rate = 48000;
            var frames = (int)(rate * seconds);
            using var file = new BinaryWriter(File.Create(path));
            file.Write("RIFF"u8);
            file.Write(36 + frames * 4);
            file.Write("WAVEfmt "u8);
            file.Write(16);
            file.Write((short)1);
            file.Write((short)2);
            file.Write(rate);
            file.Write(rate * 4);
            file.Write((short)4);
            file.Write((short)16);
            file.Write("data"u8);
            file.Write(frames * 4);
            for (var i = 0; i < frames; i++)
            {
                var sample = (short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 1200);
                file.Write(sample);
                file.Write(sample);
            }
        }
    }

    /// <summary>
    /// Show automation: an action combo puts up a message and starts a timer; a slide whose
    /// action fires a background brings it up when the slide goes live; Clear All takes it all down.
    /// </summary>
    private async Task ActionsCheck()
    {
        var timer = model.TimerRows.FirstOrDefault();
        var alert = new AlertPreset { Id = "selftest-alert", Name = "Self-test", Message = "Welcome, from an action combo" };
        model.Library.Save(alert);
        var combo = new ActionCombo
        {
            Id = "selftest-combo", Name = "Self-test combo",
            Actions =
            [
                new SlideAction { Id = "a1", Kind = SlideActionKind.FireAlert, AlertId = alert.Id },
                .. timer is null ? Array.Empty<SlideAction>() : [new SlideAction { Id = "a2", Kind = SlideActionKind.TimerStart, TimerId = timer.Id }],
            ],
        };
        model.Library.Save(combo);
        model.RunActionCombo(combo.Id);
        await Delay(400);
        Check("an action combo puts up a message", model.Show.AlertMessage == alert.Message);
        if (timer is not null) Check("an action combo starts a timer", model.Timers.Snapshot(timer.Id)?.IsRunning == true);

        var background = model.MediaItems.FirstOrDefault(m => m.Name == "Dusk");
        if (background is not null)
        {
            var slide = new Slide
            {
                Id = "selftest-action-slide", Name = "Announcements",
                Objects = [new SlideObject { Id = "t", ObjectKind = SlideObjectKind.Text, Name = "Text", Text = "Announcements" }],
                Actions = [new SlideAction { Id = "s1", Kind = SlideActionKind.FireMedia, MediaId = background.Item.Id }],
            };
            var deck = new Presentation { Id = "selftest-action-deck", Name = "Announcements", Slides = [slide] };
            model.Library.Save(deck);
            model.Open(deck, null, null);
            await Delay(300);
            model.GoLiveCommand.Execute(model.Slides.FirstOrDefault());
            await Delay(600);
            Check("a slide's action fires its background when it goes live",
                model.Show.LiveMedia.Values.Any(cue => cue.MediaId == background.Item.Id));
        }
        model.ClearAllCommand.Execute(null);
        if (timer is not null) model.Timers.Reset(timer.Id);
        Check("Clear All takes the message and the background down", !model.Show.HasAlert && model.Show.IsEmpty);

        // The scheduler: "when the sermon timer reaches 29:58, show a message".
        if (timer is null || model.Timers.Snapshot(timer.Id) is not { DurationSeconds: >= 10 } snapshot) return;
        var trigger = new ScheduleTrigger
        {
            Id = "selftest-trigger", Name = "Self-test trigger",
            Conditions = [new ScheduleCondition { Id = "c", Kind = ScheduleConditionKind.TimerReaches, TimerId = timer.Id, TimerSeconds = snapshot.DurationSeconds - 2 }],
            Actions = [new SlideAction { Id = "t1", Kind = SlideActionKind.FireAlert, AlertId = alert.Id }],
        };
        model.Library.Save(trigger);
        model.Timers.Start(timer.Id);
        for (var i = 0; i < 60 && !model.Show.HasAlert; i++) await Delay(100);
        Check("a schedule trigger fires when a timer reaches its time", model.Show.AlertMessage == alert.Message);
        model.Timers.Reset(timer.Id);
        model.Library.Delete<ScheduleTrigger>(trigger.Id);
        model.ClearAllCommand.Execute(null);
    }

    /// <summary>
    /// With NDI Tools installed: a screen sent to NDI appears on the network as a source other
    /// NDI programs can pick (found here with NDI's own finder). Skipped where NDI isn't installed.
    /// </summary>
    private async Task NdiCheck(OutputManager outputs)
    {
        if (!VideoIO.Ndi.NdiLibrary.TryLoad(out var ndi, out var error))
        {
            Skip("NDI output", error.ToString() ?? "NDI isn't installed");
            return;
        }
        var config = outputs.Config;
        var screen = config.AddScreen("NDI self-test", 1280, 720)!;
        const string source = "MxU Slides self-test";
        try
        {
            config.AssignDevice(screen.Id, Core.Outputs.OutputDevice.Ndi(source), "NDI self-test");
            outputs.Apply();
            model.RenderOutputs(SceneTransition.Cut);
            var finder = VideoIO.Ndi.NdiFinder.Create(ndi);
            var names = await Task.Run(() => finder?.CurrentSourceNames(4000) ?? []);
            (finder as IDisposable)?.Dispose();
            Check("a screen sent to NDI shows up as an NDI source on the network", names.Any(n => n.Contains(source, StringComparison.Ordinal)));
        }
        finally
        {
            config.RemoveScreen(screen.Id);
            outputs.Apply();
        }
    }

    /// <summary>
    /// The screen setup: first run makes an Audience and a Stage screen; and a screen sent to a
    /// device goes through its slice's corrections on the graphics card. A temporary screen,
    /// brightened all the way and with a left edge blend, must come out white on the right and
    /// fade to black at the left edge.
    /// </summary>
    private async Task ScreensCheck()
    {
        if (model.Outputs is not { } outputs) return;
        var config = outputs.Config;
        Check("the screen setup starts with an Audience and a Stage screen",
            config.Screens.Count(s => config.Role(s.Id) == Core.Outputs.ScreenRole.Audience) >= 1
            && config.Screens.Any(s => config.Role(s.Id) == Core.Outputs.ScreenRole.Confidence));
        if (RenderCapability.Tier >> 16 == 0 || OutputAdjustEffect.Shader is null)
        {
            Skip("output corrections", "no graphics card with pixel shader 3.0");
            return;
        }
        var sink = new CountingSink();
        var factory = outputs.DeviceSinkFactory;
        outputs.DeviceSinkFactory = device => device == "ndi:MxU self-test" ? sink : factory?.Invoke(device);
        var screen = config.AddScreen("Self-test", 1280, 720)!;
        try
        {
            config.AssignDevice(screen.Id, "ndi:MxU self-test", "Self-test");
            config.SetAdjustments(screen.Id, new Core.Outputs.OutputAdjustments
            {
                Brightness = 1,
                BlendLeft = new Core.Outputs.OutputEdgeBlend(0.25, curve: 1),
            });
            outputs.Apply();
            model.RenderOutputs(SceneTransition.Cut);
            // Up to 5 seconds: a PC without Windows' capture switches to the slower copy after 2.
            for (var i = 0; i < 50 && sink.Frames <= 10; i++) await Delay(100);
            Check("a screen on a device gets frames at its own size", sink.Frames > 10 && sink.Width == 1280 && sink.Height == 720);
            if (HiddenCapture.FastCaptureFailed)
                Skip("brightness and an edge blend on the graphics card", "this PC can't use Windows' fast capture; the slower copy draws without output corrections");
            else if (sink.Last is { } pixels && sink.Width == 1280)
            {
                byte Red(int x, int y) => pixels[(y * sink.Width + x) * 4 + 2];
                Check("brightness and an edge blend are applied on the graphics card",
                    Red(1200, 360) > 230 && Red(2, 360) < 25 && Red(160, 360) is > 60 and < 200);
                Write(BitmapSource.Create(sink.Width, sink.Height, 96, 96, PixelFormats.Pbgra32, null, pixels, sink.Width * 4), "15-screen-corrections");
            }
        }
        finally
        {
            config.RemoveScreen(screen.Id);
            outputs.Apply();
            outputs.DeviceSinkFactory = factory;
        }
        Check("removing a screen stops its output", outputs.StatusText(OutputRole.Audience) != "2 outputs");
        await NdiCheck(outputs);

        var editor = new ScreensWindow(outputs) { Owner = window };
        editor.Show();
        await Delay(900);
        Save(editor, "16-screens-window");
        editor.Close();
    }

    /// <summary>
    /// The GPU shaders compile, and an object's effect really draws: a white box with an
    /// Invert effect comes out black in the program feed.
    /// </summary>
    private async Task EffectsCheck()
    {
        if (RenderCapability.Tier >> 16 == 0 || !RenderCapability.IsPixelShaderVersionSupported(3, 0))
        {
            Skip("GPU effects", "no graphics card with pixel shader 3.0");
            return;
        }
        Check("the effect and output-correction shaders compile", ItemEffect.IsSupported && OutputAdjustEffect.Shader is not null);
        if (model.Program is not { } feed || !ItemEffect.IsSupported) return;
        if (HiddenCapture.FastCaptureFailed)
        {
            Skip("GPU effects in the program feed", "this PC can't use Windows' fast capture; the slower copy draws without GPU effects");
            return;
        }
        var slide = new Slide
        {
            Id = "selftest-effects", Name = "Effects",
            Objects =
            [
                new SlideObject
                {
                    Id = "box", ObjectKind = SlideObjectKind.Shape, Name = "Box", Text = "", X = 0, Y = 0, Width = 1920, Height = 1080,
                    Fill = new ObjectFill { FillKind = FillKind.Solid, ColorHex = "#FFFFFFFF" },
                    Effects = [new Effect { EffectKind = EffectKind.Invert }],
                },
            ],
        };
        var sink = new CountingSink();
        feed.Add(sink);
        model.Show.Fire(slide, new Presentation { Id = "selftest-effects", Name = "Effects", Slides = [slide] });
        model.RenderOutputs(SceneTransition.Cut);
        await Delay(1200);
        feed.Remove(sink);
        if (sink.Last is { } pixels && sink.Width > 0)
        {
            var i = (sink.Height / 2 * sink.Width + sink.Width / 2) * 4;
            Check("an Invert effect turns a white box black", pixels[i] < 40 && pixels[i + 1] < 40 && pixels[i + 2] < 40);
            Write(BitmapSource.Create(sink.Width, sink.Height, 96, 96, PixelFormats.Pbgra32, null, pixels, sink.Width * 4), "04-effect-invert");
        }
        model.ClearAllCommand.Execute(null);
    }

    /// <summary>
    /// Makes a short MP4 from a starter background with Windows' own encoder,
    /// imports it, plays it as a foreground video and checks it opens, has a
    /// length and ends cleanly. Skipped (with a note) where Windows can't encode.
    /// </summary>
    private async Task VideoCheck()
    {
        string? path = null;
        try
        {
            var still = model.MediaItems.FirstOrDefault(m => !m.IsVideo);
            if (still is null || model.Library.BlobPath(still.Item) is not { } image) return;
            path = Path.Combine(Path.GetTempPath(), $"MxU self-test video {Guid.NewGuid():N}.mp4");
            var file = await global::Windows.Storage.StorageFile.GetFileFromPathAsync(image);
            var composition = new global::Windows.Media.Editing.MediaComposition();
            composition.Clips.Add(await global::Windows.Media.Editing.MediaClip.CreateFromImageFileAsync(file, TimeSpan.FromSeconds(3)));
            var folder = await global::Windows.Storage.StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path)!);
            var target = await folder.CreateFileAsync(Path.GetFileName(path), global::Windows.Storage.CreationCollisionOption.ReplaceExisting);
            var profile = global::Windows.Media.MediaProperties.MediaEncodingProfile.CreateMp4(global::Windows.Media.MediaProperties.VideoEncodingQuality.HD720p);
            var result = await composition.RenderToFileAsync(target, global::Windows.Media.Editing.MediaTrimmingPreference.Fast, profile);
            if (result != global::Windows.Media.Transcoding.TranscodeFailureReason.None)
            {
                report.AppendLine($"NOTE  couldn't make a test video ({result}); video playback not checked");
                return;
            }
        }
        catch (Exception ex)
        {
            report.AppendLine($"NOTE  couldn't make a test video ({ex.Message}); video playback not checked");
            return;
        }

        var imported = model.ImportMediaFiles([path]);
        File.Delete(path);
        var video = imported.FirstOrDefault();
        Check("a video imports", video is { MediaKind: MediaKind.Video });
        if (video is null) return;
        video.Classification = MediaClassification.Foreground;
        video.Loops = false;
        model.Library.Save(video);
        model.FireMedia(video);
        var key = $"live:live-media-videos:{video.Id}";
        // Slow machines (CI) decode in software; give the video up to 15 seconds to open.
        for (var i = 0; i < 150 && model.Media.Transport(key) is null && !model.Media.IsVideoFailed(key); i++) await Delay(100);
        Check("the video opens and plays" + (model.Media.VideoFailureReason(key) is { } why ? $" (failed: {why})" : ""),
            model.Media.Transport(key) is { Duration.TotalSeconds: > 2, Playing: true });
        Check("the video's size is known", model.Media.NaturalSize(video.Id) is { Width: > 0 });
        await Step("04-video", () => { }, settle: 800);
        Check("the video is drawn on the audience preview", model.Show.LiveMedia.ContainsKey(LayerKind.Videos));
        model.ClearMediaCommand.Execute(null);
        await Delay(300);
        Check("clearing media stops the video", !model.Show.LiveMedia.ContainsKey(LayerKind.Videos));
        model.Media.Poster(video.Id);
        for (var i = 0; i < 50 && model.Media.Poster(video.Id) is null; i++) await Delay(100);
        Check("a poster frame is made for the video's thumbnail", model.Media.Poster(video.Id) is { PixelWidth: > 0 });
    }

    private static string Safe(string name) => string.Concat(name.Where(char.IsLetterOrDigit));

    private string AudienceText() => TextOf(model.Show.Scene());

    /// <summary>The audience text actually visible right now, after animation (hidden builds left out).</summary>
    private string VisibleAudienceText()
    {
        var resolved = Core.Animation.AnimationEvaluator.Resolve(model.Show.AnimatedScene(), Core.Scene.ShowState.HostClock());
        return string.Join("\n", resolved.Layers.SelectMany(l => l.Items)
            .Where(r => r.Item.Render.Opacity > 0.05 && r.Item.HiddenRuns.Count == 0)
            .Select(r => r.Item.Render.Content is ItemContent.Text t ? t.Styled.String : null).OfType<string>());
    }

    private string StageText() => TextOf(model.StageScene());

    private static string TextOf(RenderScene scene) => string.Join("\n", scene.AllItems
        .Select(i => i.Content is ItemContent.Text t ? t.Styled.String : null)
        .OfType<string>()
        .Where(s => s.Length > 0));

    /// <summary>A check this computer can't run (no graphics card, no device): reported, not failed.</summary>
    private void Skip(string what, string why) => report.AppendLine($"SKIP  {what} ({why})");

    private void Check(string what, bool ok)
    {
        report.AppendLine($"{(ok ? "PASS" : "FAIL")}  {what}");
        if (!ok) failures++;
    }

    private async Task Step(string name, Action action, int settle = 600)
    {
        action();
        await Delay(settle);
        Save(window, name);
    }

    private static Task Delay(int ms) => Task.Delay(ms);

    /// <summary>Saves a picture of a window's content.</summary>
    private void Save(Window target, string name)
    {
        try
        {
            if (target.Content is not FrameworkElement content || content.ActualWidth < 1) return;
            var dpi = VisualTreeHelper.GetDpi(content);
            var bitmap = new RenderTargetBitmap((int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var backdrop = new DrawingVisual();
            using (var dc = backdrop.RenderOpen())
                dc.DrawRectangle(target.Background ?? Brushes.Black, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            bitmap.Render(backdrop);
            bitmap.Render(content);
            Write(bitmap, name);
        }
        catch (Exception ex)
        {
            report.AppendLine($"NOTE  couldn't capture {name}: {ex.Message}");
        }
    }

    /// <summary>Renders a scene offscreen at 1280×720, exactly as an output would draw it.</summary>
    private Task Snapshot(string name, RenderScene scene) => Snapshot(name, Core.Animation.AnimatedScene.From(scene));

    /// <summary>A picture of the audience scene as it looks <paramref name="seconds"/> after its animations' anchors (frozen there).</summary>
    private Task SnapshotAt(string name, Core.Animation.AnimatedScene scene, double seconds)
    {
        var frozen = scene.Copy();
        foreach (var layer in frozen.Layers)
            for (var i = 0; i < layer.Items.Count; i++)
                if (layer.Items[i].AnimationContext is { } context)
                    layer.Items[i] = layer.Items[i] with { AnimationContext = context with { FrozenHostTime = context.AnchorHostTime + seconds } };
        return Snapshot(name, frozen);
    }

    private async Task Snapshot(string name, Core.Animation.AnimatedScene scene)
    {
        var view = new SceneView { Media = model.Media, PlaysVideo = false, Width = 1280, Height = 720 };
        view.Measure(new Size(1280, 720));
        view.Arrange(new Rect(0, 0, 1280, 720));
        view.Present(scene, SceneTransition.Cut);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        var bitmap = new RenderTargetBitmap(1280, 720, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        Write(bitmap, name);
    }

    private void Write(BitmapSource bitmap, string name)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(folder, name + ".png"));
        encoder.Save(file);
    }
}
