using System.IO.Compression;
using System.Text;
using MxU.Slides.Core.Model;
using static MxU.Slides.ProImport.Tests.Proto;
using DataLink = Pro.SerializationInterop.RVProtoData.Slide.Types.Element.Types.DataLink;
using G = Pro.SerializationInterop.RVProtoData.Graphics.Types;
using RV = Pro.SerializationInterop.RVProtoData;
using SlideElement = Pro.SerializationInterop.RVProtoData.Slide.Types.Element;
using Visibility = Pro.SerializationInterop.RVProtoData.Slide.Types.Element.Types.DataLink.Types.VisibilityLink;

namespace MxU.Slides.ProImport.Tests;

public class ProWorkspaceMapperTests
{
    private static SlideElement TextElement(string name, string text, string fontName = "Helvetica", DataLink? dataLink = null)
    {
        var textBox = new G.Text { Attributes = new() { Font = new() { Name = fontName, Size = 48 } } };
        if (text.Length > 0) textBox.RtfData = RtfBytes("{\\rtf1\\ansi{\\fonttbl\\f0\\fnil " + fontName + ";}\\f0 " + text + "}");
        var element = Wrap(new G.Element
        {
            Uuid = Uuid(Guid.NewGuid().ToString()), Name = name, Opacity = 1, Text = textBox,
            Bounds = new() { Origin = new() { X = 100, Y = 100 }, Size = new() { Width = 800, Height = 200 } },
        });
        if (dataLink is not null) element.DataLinks.Add(dataLink);
        return element;
    }

    private static RV.Stage.Types.Document StageDocument(string layoutId, params SlideElement[] elements)
    {
        var slide = new RV.Slide();
        slide.Elements.Add(elements);
        return new RV.Stage.Types.Document { Layouts = { new RV.Stage.Types.Layout { Uuid = Uuid(layoutId), Slide = slide } } };
    }

    private static RV.MacrosDocument Macros(string id, string name, params RV.Action[] actions)
    {
        var macro = new RV.MacrosDocument.Types.Macro { Uuid = Uuid(id), Name = name };
        macro.Actions.Add(actions);
        return new RV.MacrosDocument { Macros = { macro } };
    }

    [Fact]
    public void ThemeMapsSlidesWithEmptyPlaceholders()
    {
        var picture = new RV.Media
        {
            Uuid = Uuid("MEDIA-BG"),
            Url = new()
            {
                AbsoluteString = "file:///Users/presenter/Documents/ProPresenter/Themes/Lyrics%20Centered/Assets/bg.jpg",
                Local = new() { Path = "Assets/bg.jpg" },
            },
            Image = new(),
        };
        var slide = new RV.Template.Types.Slide
        {
            Name = "Lyrics",
            BaseSlide = new() { Uuid = Uuid("SLIDE-1"), Elements = { TextElement("Lyrics", "", "DrukWide-Medium") } },
            Actions =
            {
                new RV.Action { Media = new() { Element = picture } },
                new RV.Action { Media = new() { Element = new() { Audio = new() } } },
            },
        };
        var doc = new RV.Template.Types.Document { Slides = { slide } };

        var mapped = ProWorkspaceMapper.MapTheme(doc, "Lyrics Centered");
        Assert.Equal("pro7-theme-lyrics-centered", mapped.Theme.Id);
        Assert.Equal("Lyrics Centered", mapped.Theme.Name);
        var slides = mapped.Theme.Slides!;
        Assert.Single(slides);
        Assert.Equal("Lyrics", slides[0].Name);
        Assert.Equal(2, slides[0].Objects.Count);
        Assert.True(slides[0].Objects[0].ObjectKind == SlideObjectKind.Shape && slides[0].Objects[0].Fill?.FillKind == FillKind.Media);
        Assert.StartsWith("pro-media://", slides[0].Objects[0].Fill?.MediaId);
        Assert.True(slides[0].Objects[0].Width == 1920 && slides[0].Objects[0].Height == 1080);
        var want = Assert.Single(mapped.MediaWants);
        Assert.True(want.RelativePath == "Assets/bg.jpg" && want.Classification == MediaClassification.Background);
        Assert.Equal(SlideObjectKind.Text, slides[0].Objects[1].ObjectKind);
        Assert.Equal("DrukWide-Medium", slides[0].Objects[1].TextStyle?.FontName);
        Assert.Equal("DrukWide-Medium", mapped.Theme.FontFamily);
    }

    [Fact]
    public async Task AProThemeImportsOnItsOwn()
    {
        var doc = new RV.Template.Types.Document
        {
            Slides =
            {
                new RV.Template.Types.Slide
                {
                    Name = "Scripture",
                    BaseSlide = new() { Uuid = Uuid("SLIDE-S"), Elements = { TextElement("Verse", ""), TextElement("Reference", "") } },
                },
            },
        };
        using var staging = new TempFolder();
        var themeDir = staging.Combine("Message Series Theme");
        Directory.CreateDirectory(Path.Combine(themeDir, "Assets"));
        Write(doc, Path.Combine(themeDir, "Theme"));
        using var archives = new TempFolder();
        var protheme = archives.Combine($"{Guid.NewGuid()}.protheme");
        ZipFile.CreateFromDirectory(themeDir, protheme, CompressionLevel.Fastest, includeBaseDirectory: true);

        using var library = new TestLibrary();
        var importer = new ProPresenterImporter(library.Store);
        var options = new ProImportOptions { Policy = ImportConflictPolicy.UpdateUnedited };
        var summary = await importer.ImportThemeAsync(protheme, options, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(summary.ThemeId == "pro7-theme-message-series-theme" && summary.Name == "Message Series Theme" && summary.Skipped is null);
        var theme = library.Store.Get<Theme>("pro7-theme-message-series-theme")!;
        Assert.Equal("Message Series Theme", theme.Name);
        Assert.True(theme.Slides?[0].Name == "Scripture" && theme.Slides[0].Objects.Count(o => o.ObjectKind == SlideObjectKind.Text) == 2);

        var again = await importer.ImportThemeAsync(protheme, options, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(summary.ThemeId, again.ThemeId);
        Assert.Equal(1, library.Store.Count<Theme>());
        Assert.Equal("Theme", Path.GetFileName(ProPresenterImporter.ThemeFile(staging.Path)));
        Assert.Null((await importer.ImportThemeAsync(staging.Combine("nothing.protheme"), cancellationToken: TestContext.Current.CancellationToken)).ThemeId);
    }

    [Fact]
    public void PropBecomesOverlay()
    {
        var cue = new RV.Cue
        {
            Uuid = Uuid("PROP-1"), Name = "Lower Third",
            Actions = { new RV.Action { Slide = new() { Prop = new() { BaseSlide = new() { Elements = { TextElement("Title", "Welcome") } } } } } },
        };
        var mapped = ProWorkspaceMapper.MapProps(new RV.PropDocument { Cues = { cue } });
        var overlay = Assert.Single(mapped).Overlay;
        Assert.Equal("prop-1", overlay.Id);
        Assert.Equal("Lower Third", overlay.Name);
        Assert.Equal("Welcome", overlay.Objects[0].Text);
        Assert.Null(overlay.Layer);
    }

    [Fact]
    public void MessageBecomesAlertWithTemplateReference()
    {
        var message = new RV.Message
        {
            Uuid = Uuid("MSG-1"), Title = "Ticker", MessageText = "Service starts soon", Template = new() { Name = "Ticker", SlideName = "Ticker" },
        };
        var mapped = Assert.Single(ProWorkspaceMapper.MapMessages(new RV.MessageDocument { Messages = { message } }));
        Assert.Equal("msg-1", mapped.Preset.Id);
        Assert.Equal("Service starts soon", mapped.Preset.Message);
        Assert.Equal(AlertBehavior.Persist, mapped.Preset.Behavior);
        Assert.Equal(AlertTarget.Both, mapped.Preset.Target);
        Assert.Equal("Ticker", mapped.TemplateThemeName);
    }

    [Fact]
    public void MessageTokensBecomeFillSlots()
    {
        var message = new RV.Message { Uuid = Uuid("MSG-2"), Title = "Car", MessageText = "Please move your car:", Tokens = { new RV.Message.Types.Token { Text = new() { Name = "plate" } } } };
        var mapped = ProWorkspaceMapper.MapMessages(new RV.MessageDocument { Messages = { message } });
        Assert.Contains("{plate}", mapped[0].Preset.Message);
        Assert.Null(mapped[0].Preset.Target);
    }

    [Fact]
    public void InlineTokenUUIDsRewriteToNamedSlots()
    {
        var message = new RV.Message
        {
            Uuid = Uuid("MSG-3"), Title = "Grace Countdown", MessageText = "${F1A218F8-8926-4EB4-949E-581CD071151B} starts the service",
            Tokens = { new RV.Message.Types.Token { Uuid = Uuid("F1A218F8-8926-4EB4-949E-581CD071151B"), Timer = new() { Name = "Grace Timer" } } },
        };
        var mapped = ProWorkspaceMapper.MapMessages(new RV.MessageDocument { Messages = { message } });
        Assert.Equal("{Grace Timer} starts the service", mapped[0].Preset.Message);
        Assert.Empty(mapped[0].Warnings);
    }

    [Fact]
    public void StageLayoutMapsDataLinksToTextLinks()
    {
        var slide = new RV.Slide
        {
            Size = new() { Width = 1080, Height = 1200 },
            Elements =
            {
                TextElement("Next", "sample", dataLink: new DataLink { SlideText = new() { SourceSlide = DataLink.Types.SlideSourceType.NextSlide } }),
                TextElement("Timer", "", dataLink: new DataLink { TimerText = new() { TimerUuid = Uuid("TIMER-1"), TimerName = "Sermon" } }),
                TextElement("Clock", "", dataLink: new DataLink { ClockText = new() { Format = new() { MilitaryTimeEnabled = true } } }),
                TextElement("Message", "", dataLink: new DataLink { StageMessage = new() }),
                TextElement("Group", "", dataLink: new DataLink { GroupName = new() }),
            },
        };
        var doc = new RV.Stage.Types.Document { Layouts = { new RV.Stage.Types.Layout { Uuid = Uuid("LAYOUT-1"), Name = "Current + Next", Slide = slide } } };

        var mapped = ProWorkspaceMapper.MapStageLayouts(doc, new Dictionary<string, string> { ["timer-1"] = "mxu-timer-9" });
        var layout = Assert.Single(mapped).Layout;
        Assert.Equal("layout-1", layout.Id);
        Assert.Equal(1080, layout.CanvasWidth);
        Assert.Equal(1200, layout.CanvasHeight);
        Assert.Equal(5, layout.Objects.Count);
        Assert.Equal(TextSourceKind.NextSlide, layout.Objects[4].TextLink?.Source);
        Assert.Equal(TextSourceKind.Timer, layout.Objects[3].TextLink?.Source);
        Assert.Equal("mxu-timer-9", layout.Objects[3].TextLink?.TimerId);
        Assert.Equal(TextSourceKind.Clock, layout.Objects[2].TextLink?.Source);
        Assert.Equal("H:mm", layout.Objects[2].TextLink?.ClockFormat);
        Assert.Equal(TextSourceKind.StageMessage, layout.Objects[1].TextLink?.Source);
        Assert.Equal(TextSourceKind.CurrentGroup, layout.Objects[0].TextLink?.Source);
        Assert.Empty(mapped[0].Warnings);
        Assert.Equal(SlideObjectKind.Text, layout.Objects[3].ObjectKind);
    }

    [Fact]
    public void StageVideoCountdownKeepsTheNamePrefixedRead()
    {
        var mapped = ProWorkspaceMapper.MapStageLayouts(StageDocument("LAYOUT-VC", TextElement("Countdown", "", dataLink: new DataLink { VideoCountdown = new() })));
        Assert.Equal(TextSourceKind.VideoCountdown, mapped[0].Layout.Objects[0].TextLink?.Source);
        Assert.Null(mapped[0].Layout.Objects[0].TextLink?.ShowsVideoName);
    }

    [Fact]
    public void UnknownTimerFallsBackToPrimaryWithWarning()
    {
        var mapped = ProWorkspaceMapper.MapStageLayouts(StageDocument("LAYOUT-2",
            TextElement("Timer", "", dataLink: new DataLink { TimerText = new() { TimerUuid = Uuid("GONE"), TimerName = "Missing Timer" } })));
        Assert.Equal(TextSourceKind.Timer, mapped[0].Layout.Objects[0].TextLink?.Source);
        Assert.Null(mapped[0].Layout.Objects[0].TextLink?.TimerId);
        Assert.Contains(mapped[0].Warnings, w => w.Contains("Missing Timer"));
    }

    [Fact]
    public void MacroActionsMapOntoTheComboVocabulary()
    {
        var clear = new RV.Action { Clear = new() { TargetLayer = RV.Action.Types.ClearType.Types.ClearTargetLayer.All } };
        var timer = new RV.Action
        {
            Timer = new() { ActionType = RV.Action.Types.TimerType.Types.TimerAction.ActionResetAndStart, TimerIdentification = new() { ParameterUuid = Uuid("TIMER-1") } },
            DelayTime = 3,
        };
        var message = new RV.Action { Message = new() { MessageIdentificaton = new() { ParameterUuid = Uuid("MSG-1") } } };
        var stage = new RV.Action { Stage = new() { StageScreenAssignments = { new RV.Stage.Types.ScreenAssignment { Layout = new() { ParameterUuid = Uuid("LAYOUT-1") } } } } };
        var prop = new RV.Action { Prop = new() };
        var doc = Macros("MACRO-1", "Preservice", clear, timer, message, stage, prop);
        doc.Macros[0].Color = Color(1, 0, 0);

        var mapped = ProWorkspaceMapper.MapMacros(doc, new Dictionary<string, string> { ["timer-1"] = "mxu-timer-9" });
        var combo = Assert.Single(mapped).Combo;
        Assert.Equal("macro-1", combo.Id);
        Assert.Equal("Preservice", combo.Name);
        Assert.Equal("#FF0000FF", combo.ColorHex);
        Assert.Equal(
            new[] { SlideActionKind.ClearAll, SlideActionKind.TimerReset, SlideActionKind.TimerStart, SlideActionKind.FireAlert, SlideActionKind.SetConfidenceLayout, SlideActionKind.FireOverlay },
            combo.Actions.Select(a => a.Kind));
        Assert.Equal("mxu-timer-9", combo.Actions[1].TimerId);
        Assert.Equal(new double?[] { null, 3, 3, null, null, null }, combo.Actions.Select(a => a.DelaySeconds));
        Assert.Equal("msg-1", combo.Actions[3].AlertId);
        Assert.Equal("layout-1", combo.Actions[4].ConfidenceLayoutId);
        Assert.Empty(mapped[0].Warnings);
    }

    [Fact]
    public void PlaylistItemStepMapsToFireAudioPlaylist()
    {
        var mapped = ProWorkspaceMapper.MapMacros(Macros("MACRO-4", "Super Timer",
            new RV.Action { PlaylistItem = new() { PlaylistUuid = Uuid("PL-AUDIO-1"), PlaylistName = "Greg's Hits" } }));
        Assert.Equal(new[] { SlideActionKind.FireAudioPlaylist }, mapped[0].Combo.Actions.Select(a => a.Kind));
        Assert.Equal("pl-audio-1", mapped[0].Combo.Actions[0].PlaylistId);
        Assert.Empty(mapped[0].Warnings);
    }

    [Fact]
    public void PlaylistItemStepCarriesItsStartingEntry()
    {
        var mapped = ProWorkspaceMapper.MapMacros(Macros("MACRO-5", "Walk-in from track 3",
            new RV.Action { PlaylistItem = new() { PlaylistUuid = Uuid("PL-AUDIO-1"), ItemUuid = Uuid("ITEM-3") } }));
        Assert.Equal("pl-audio-1", mapped[0].Combo.Actions[0].PlaylistId);
        Assert.Equal("item-3", mapped[0].Combo.Actions[0].AudioEntryId);
        Assert.Empty(mapped[0].Warnings);
    }

    [Fact]
    public void ClearAudioStepMapsToClearMusic()
    {
        var mapped = ProWorkspaceMapper.MapMacros(Macros("MACRO-2", "Showstopper",
            new RV.Action { Clear = new() { TargetLayer = RV.Action.Types.ClearType.Types.ClearTargetLayer.Audio } }));
        Assert.Equal(new[] { SlideActionKind.ClearAudio }, mapped[0].Combo.Actions.Select(a => a.Kind));
        Assert.Empty(mapped[0].Warnings);
    }

    [Fact]
    public void TimerStepWithInlineConfigurationEmitsConfigure()
    {
        var timer = new RV.Action
        {
            Timer = new()
            {
                ActionType = RV.Action.Types.TimerType.Types.TimerAction.ActionResetAndStart,
                TimerIdentification = new() { ParameterUuid = Uuid("TIMER-1") },
                TimerConfiguration = new() { Countdown = new() { Duration = 450 } },
            },
        };
        var mapped = ProWorkspaceMapper.MapMacros(Macros("MACRO-5", "Super Timer 7:30", timer), new Dictionary<string, string> { ["timer-1"] = "mxu-timer-1" });
        var actions = mapped[0].Combo.Actions;
        Assert.Equal(new[] { SlideActionKind.TimerConfigure, SlideActionKind.TimerReset, SlideActionKind.TimerStart }, actions.Select(a => a.Kind));
        Assert.Equal("mxu-timer-1", actions[0].TimerId);
        Assert.Equal(TimerMode.Countdown, actions[0].TimerMode);
        Assert.Equal(450, actions[0].TimerDurationSeconds);
        Assert.Empty(mapped[0].Warnings);
    }

    [Fact]
    public void TimerStepFallsBackToTheTimerName()
    {
        var timer = new RV.Action
        {
            Timer = new()
            {
                ActionType = RV.Action.Types.TimerType.Types.TimerAction.ActionStart,
                TimerIdentification = new() { ParameterUuid = Uuid("STALE-UUID"), ParameterName = "Grace Timer" },
            },
        };
        var mapped = ProWorkspaceMapper.MapMacros(Macros("MACRO-3", "PreRoll", timer), timerIdsByName: new Dictionary<string, string> { ["grace timer"] = "mxu-timer-1" });
        Assert.Equal(new[] { SlideActionKind.TimerStart }, mapped[0].Combo.Actions.Select(a => a.Kind));
        Assert.Equal("mxu-timer-1", mapped[0].Combo.Actions[0].TimerId);
        Assert.Empty(mapped[0].Warnings);
    }

    private static RV.Timestamp Utc(int year, int month, int day, int hour, int minute, int second = 0) =>
        new() { Seconds = new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero).ToUnixTimeSeconds() };

    [Fact]
    public void CalendarEventsBecomeSchedulerTriggers()
    {
        var weekly = new RV.Calendar.Types.Event
        {
            Uuid = Uuid("EVENT-1"), Name = "Start Service 9:00", Date = Utc(2026, 3, 22, 8, 52, 30),
            RecurrenceDays = { RV.Calendar.Types.Event.Types.DayOfWeek.Sunday },
            RecurrenceExcludedDates = { Utc(2026, 12, 27, 8, 52), Utc(2027, 1, 3, 8, 52) },
            RecurrenceLimitDate = Utc(2027, 1, 31, 8, 52),
            Actions = { new RV.Action { Macro = new() { Identification = new() { ParameterUuid = Uuid("MACRO-1") } } } },
        };
        var oneOff = new RV.Calendar.Types.Event
        {
            Uuid = Uuid("EVENT-2"), Name = "Easter Service", Date = Utc(2026, 4, 5, 9, 0),
            Action = new() { Macro = new() { Identification = new() { ParameterUuid = Uuid("MACRO-2") } } },
        };
        var doc = new RV.Calendar { Events = { weekly, oneOff }, Active = true };

        var mapped = ProWorkspaceMapper.MapCalendar(doc, timeZone: TimeZoneInfo.Utc);
        Assert.Equal(2, mapped.Count);
        var sunday = mapped[0].Trigger;
        Assert.Equal("event-1", sunday.Id);
        var condition = Assert.Single(sunday.Conditions);
        Assert.Equal(ScheduleConditionKind.Weekly, condition.Kind);
        Assert.Equal([1], condition.Days!);
        Assert.Equal("08:52:30", condition.TimeOfDay);
        Assert.Equal(["2026-12-27", "2027-01-03"], condition.ExcludedDates!);
        Assert.Equal(new[] { SlideActionKind.FireCombo }, sunday.Actions.Select(a => a.Kind));
        Assert.Equal("macro-1", sunday.Actions[0].ComboId);
        Assert.Equal("2027-01-31T23:59:59", sunday.EnabledUntil);
        Assert.Empty(mapped[0].Warnings);

        var easter = mapped[1].Trigger;
        Assert.Equal(ScheduleConditionKind.OneTime, easter.Conditions[0].Kind);
        Assert.Equal("2026-04-05T09:00", easter.Conditions[0].Date);
        Assert.Equal(new[] { SlideActionKind.FireCombo }, easter.Actions.Select(a => a.Kind));
        Assert.Equal("macro-2", easter.Actions[0].ComboId);
        Assert.Empty(mapped[1].Warnings);
    }

    private static RV.Screen Output(double x = 0, double width = 1920, double height = 1080) =>
        new() { Bounds = new() { Origin = new() { X = x }, Size = new() { Width = width, Height = height } } };

    [Fact]
    public void ScreensMapWithRolesAndAssignments()
    {
        var audience = new RV.ProPresenterScreen
        {
            Uuid = Uuid("SCREEN-A"), Name = "Main", ScreenType = RV.ProPresenterScreen.Types.ScreenType.Audience,
            ArrangementSingle = new() { Screens = { Output() } },
        };
        var stage = new RV.ProPresenterScreen
        {
            Uuid = Uuid("SCREEN-S"), Name = "Stage 1", ScreenType = RV.ProPresenterScreen.Types.ScreenType.Stage,
            ArrangementSingle = new() { Screens = { Output(width: 1080, height: 1200) } },
        };
        var workspace = new RV.ProPresenterWorkspace
        {
            ProScreens = { audience, stage },
            StageLayoutMappings = { new RV.Stage.Types.ScreenAssignment { Screen = new() { ParameterUuid = Uuid("SCREEN-S") }, Layout = new() { ParameterUuid = Uuid("LAYOUT-1") } } },
        };
        var plan = ProWorkspaceMapper.MapScreens(workspace).Plan;
        Assert.Equal(2, plan.Screens.Count);
        Assert.Null(plan.Screens[0].Correction);
        Assert.False(plan.Screens[0].IsConfidence);
        Assert.True(plan.Screens[1].IsConfidence);
        Assert.Equal(1080, plan.Screens[1].Width);
        Assert.Equal(1200, plan.Screens[1].Height);
        Assert.Equal([new ProScreenPlan.StageAssignment("screen-s", "layout-1")], plan.StageAssignments);
    }

    [Fact]
    public void ScreenOutputCorrectionsImport()
    {
        var output = Output();
        output.CornerPinningEnabled = true;
        output.CornerValues = new() { TopLeft = new() { X = 24, Y = -12 } };
        output.ColorEnabled = true;
        output.ColorAdjustment = new() { Brightness = -0.2, RedLevel = 0.1 };
        output.Gamma = 0.05;
        output.Rotation = 90;
        var screen = new RV.ProPresenterScreen
        {
            Uuid = Uuid("SCREEN-P"), Name = "Side LED", ScreenType = RV.ProPresenterScreen.Types.ScreenType.Audience,
            ArrangementSingle = new() { Screens = { output } },
        };
        var workspace = new RV.ProPresenterWorkspace { ProScreens = { screen } };

        var mapped = ProWorkspaceMapper.MapScreens(workspace);
        var correction = mapped.Plan.Screens[0].Correction!;
        Assert.Equal(new ProScreenPlan.OutputCorrection.Offset(24, -12), correction.TopLeft);
        Assert.Null(correction.TopRight);
        Assert.Equal(-0.2, correction.Brightness);
        Assert.Equal(0.1, correction.RedLevel);
        Assert.Equal(0.05, correction.Gamma);
        Assert.Equal(90, correction.RotationDegrees);
        Assert.DoesNotContain(mapped.Warnings, w => w.Contains("rotation"));

        output.CornerPinningEnabled = false;
        output.Rotation = 0;
        output.ColorEnabled = false;
        output.Gamma = 0;
        Assert.Null(ProWorkspaceMapper.MapScreens(workspace).Plan.Screens[0].Correction);
    }

    [Fact]
    public void EmptyArrangementImportsWithoutSlicesOrWarnings()
    {
        var workspace = new RV.ProPresenterWorkspace
        {
            ProScreens = { new RV.ProPresenterScreen { Uuid = Uuid("SCREEN-W"), Name = "Wide", ArrangementEdgeBlend = new() { ScreenCount = 2 } } },
        };
        var mapped = ProWorkspaceMapper.MapScreens(workspace);
        Assert.Single(mapped.Plan.Screens);
        Assert.Empty(mapped.Plan.Screens[0].Slices);
        Assert.DoesNotContain(mapped.Warnings, w => w.Contains("multi-projector"));
    }

    [Fact]
    public void SpanArrangementsImportAtTheirCanvasSize()
    {
        var blended = new RV.ProPresenterScreen
        {
            Uuid = Uuid("SCREEN-EB"), Name = "Triple Wide",
            ArrangementEdgeBlend = new() { ScreenCount = 2, Screens = { Output(0), Output(1920) } },
        };
        var combined = new RV.ProPresenterScreen
        {
            Uuid = Uuid("SCREEN-CB"), Name = "Grid",
            ArrangementCombined = new() { Screens = { Output(0, 1280, 720), Output(1280, 1280, 720) } },
        };
        var mapped = ProWorkspaceMapper.MapScreens(new RV.ProPresenterWorkspace { ProScreens = { blended, combined } });
        Assert.Equal(3840, mapped.Plan.Screens[0].Width);
        Assert.Equal(1080, mapped.Plan.Screens[0].Height);
        Assert.Equal(2560, mapped.Plan.Screens[1].Width);
        Assert.Equal(720, mapped.Plan.Screens[1].Height);
        Assert.Empty(mapped.Warnings);

        var blendSlices = mapped.Plan.Screens[0].Slices;
        Assert.Equal(2, blendSlices.Count);
        Assert.True(Math.Abs(blendSlices[0].X) < 0.001 && Math.Abs(blendSlices[0].Width - 0.5) < 0.001);
        Assert.True(Math.Abs(blendSlices[1].X - 0.5) < 0.001 && Math.Abs(blendSlices[1].Width - 0.5) < 0.001);
        Assert.Equal(2, mapped.Plan.Screens[1].Slices.Count);
    }

    [Fact]
    public void EdgeBlendArrangementImportsRampsAndRegions()
    {
        static RV.Screen Unit(string id, double x)
        {
            var output = Output(x);
            output.Uuid = Uuid(id);
            output.SubscreenUnitRect = new() { Origin = new() { X = x / 3840 }, Size = new() { Width = 0.5, Height = 1 } };
            return output;
        }
        static RV.EdgeBlend.Types.Screen Edge(
            string id, RV.EdgeBlend.Types.Screen.Types.Edge edge, double radius, double gamma, RV.EdgeBlend.Types.Mode mode,
            double intensity = 0, double blackLevel = 0) =>
            new() { Uuid = Uuid(id), Edge = edge, Radius = radius, Gamma = gamma, Mode = mode, Intensity = intensity, BlackLevel = blackLevel };

        var right = Unit("OUT-R", 1920);
        right.BlendCompensation = new() { BlackLevel = 0.05 };
        var pair = new RV.EdgeBlend
        {
            FirstScreen = Edge("OUT-L", RV.EdgeBlend.Types.Screen.Types.Edge.Right, 96, 2.4, RV.EdgeBlend.Types.Mode.Linear, intensity: 0.7, blackLevel: 0.08),
            SecondScreen = Edge("OUT-R", RV.EdgeBlend.Types.Screen.Types.Edge.Left, 96, 0, RV.EdgeBlend.Types.Mode.Cubic),
        };
        var screen = new RV.ProPresenterScreen
        {
            Uuid = Uuid("SCREEN-EB2"), Name = "Blend Wall",
            ArrangementEdgeBlend = new() { ScreenCount = 2, Screens = { Unit("OUT-L", 0), right }, EdgeBlends = { pair } },
        };
        var mapped = ProWorkspaceMapper.MapScreens(new RV.ProPresenterWorkspace { ProScreens = { screen } });
        var slices = mapped.Plan.Screens[0].Slices;
        Assert.Equal(2, slices.Count);
        Assert.True(Math.Abs(slices[0].X) < 0.001 && Math.Abs(slices[1].X - 0.5) < 0.001);
        Assert.True(Math.Abs((slices[0].BlendRight?.Width ?? 0) - 0.05) < 0.001);
        Assert.True(Math.Abs((slices[0].BlendRight?.Curve ?? 0) - 2.4) < 0.001);
        Assert.Null(slices[0].BlendLeft);
        Assert.True(Math.Abs((slices[1].BlendLeft?.Width ?? 0) - 0.05) < 0.001);
        Assert.True(Math.Abs((slices[1].BlendLeft?.Curve ?? 0) - 3) < 0.001);
        Assert.True(Math.Abs((slices[0].BlendRight?.Intensity ?? 0) - 0.7) < 0.001);
        Assert.True(Math.Abs((slices[0].BlendRight?.BlackLift ?? 0) - 0.08) < 0.001);
        Assert.Null(slices[1].BlendLeft?.Intensity);
        Assert.True(Math.Abs((slices[1].BlendLeft?.BlackLift ?? 0) - 0.05) < 0.001);
        Assert.DoesNotContain(mapped.Warnings, w => w.Contains("intensity"));
    }

    [Fact]
    public void WorkspaceMasksImportAndLooksReferenceThem()
    {
        var shape = new G.Element
        {
            Uuid = Uuid("EL-MASK"), Opacity = 1,
            Bounds = new() { Origin = new() { X = 480, Y = 270 }, Size = new() { Width = 960, Height = 540 } },
            Fill = new() { Color = new() { Alpha = 1 }, Enable = true },
        };
        var mask = new RV.ProMask
        {
            Name = "Center Column",
            BaseSlide = new() { Uuid = Uuid("MASK-1"), Size = new() { Width = 1920, Height = 1080 }, Elements = { Wrap(shape) } },
        };
        var look = new RV.ProAudienceLook
        {
            Uuid = Uuid("LOOK-1"), Name = "Sermon",
            ScreenLooks = { new RV.ProAudienceLook.Types.ProScreenLook { ProScreenUuid = Uuid("SCREEN-1"), MaskUuid = Uuid("MASK-1") } },
        };
        var workspace = new RV.ProPresenterWorkspace { Masks = { mask }, AudienceLooks = { look } };

        var screens = ProWorkspaceMapper.MapScreens(workspace);
        var plan = Assert.Single(screens.Plan.Masks);
        Assert.Equal("Center Column", plan.Name);
        Assert.Equal("mask-1", plan.ProUuid);
        var maskShape = Assert.Single(plan.Shapes);
        Assert.True(Math.Abs(maskShape.X - 0.25) < 0.001);
        Assert.True(Math.Abs(maskShape.Y - 0.25) < 0.001);
        Assert.True(Math.Abs(maskShape.Width - 0.5) < 0.001);
        Assert.True(Math.Abs(maskShape.Height - 0.5) < 0.001);

        var looks = ProWorkspaceMapper.MapLooks(workspace);
        Assert.Equal("mask-1", looks.Looks[0].ScreenLooks[0].MaskProUuid);
        Assert.DoesNotContain(looks.Warnings, w => w.Contains("mask"));
    }

    [Fact]
    public void TimersMapAllThreeModes()
    {
        var doc = new RV.TimersDocument
        {
            Timers =
            {
                new RV.Timer { Uuid = Uuid("T-1"), Name = "Sermon", Configuration = new() { Countdown = new() { Duration = 1800 } } },
                new RV.Timer
                {
                    Uuid = Uuid("T-2"), Name = "Until Service",
                    Configuration = new()
                    {
                        CountdownToTime = new() { TimeOfDay = 9 * 3600 + 30 * 60, Period = RV.Timer.Types.Configuration.Types.TimerTypeCountdownToTime.Types.TimePeriod.Pm },
                    },
                },
                new RV.Timer { Uuid = Uuid("T-3"), Name = "Stopwatch", Configuration = new() { ElapsedTime = new() } },
            },
        };
        var plans = ProWorkspaceMapper.MapTimers(doc);
        Assert.Equal(3, plans.Count);
        Assert.Equal(new ProTimerMode.Countdown(1800), plans[0].Mode);
        Assert.Equal(new ProTimerMode.CountdownToTime(21, 30), plans[1].Mode);
        Assert.Equal(new ProTimerMode.CountUp(0), plans[2].Mode);
        Assert.NotEqual(plans[0].ProUuid, plans[0].NewId);

        var again = ProWorkspaceMapper.MapTimers(doc, [new ProTimerSeed("room-sermon", "sermon"), new ProTimerSeed("room-sermon-2", "Sermon")]);
        Assert.Equal("room-sermon", again[0].NewId);
        Assert.NotEqual("room-sermon", again[1].NewId);
    }

    [Fact]
    public void CommunicationDevicesBecomeMIDIDeviceItems()
    {
        static string Parser(string xml) => Convert.ToBase64String([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(xml)]);
        var json = $$"""
            [
              {"name":"Lyrics MIDI","id":"AC599D7D-D2C6-4CCD-8091-F1245556F8B3","reconnect":true,
               "parser":"{{Parser("<RVProtocolParserMIDI behavior=\"2\"><RVMIDIHardwareCommunicator sources=\"Lyrics Strip, Bus 2\" destinations=\"Bus 1\"></RVMIDIHardwareCommunicator></RVProtocolParserMIDI>")}}"},
              {"name":"Sender","id":"B","reconnect":false,
               "parser":"{{Parser("<RVProtocolParserMIDI behavior=\"2\"><RVMIDIHardwareCommunicator sources=\"\" destinations=\"Desk\"></RVMIDIHardwareCommunicator></RVProtocolParserMIDI>")}}"},
              {"name":"DMX","id":"F34372D6","reconnect":true,
               "parser":"{{Parser("<RVProtocolParserDMX behavior=\"2\"><RVDMXArtnetHardwareCommunicator interface=\"en0\" universe=\"0\"></RVDMXArtnetHardwareCommunicator></RVProtocolParserDMX>")}}"}
            ]
            """;
        var mapped = ProWorkspaceMapper.MapCommunicationDevices(Encoding.UTF8.GetBytes(json));
        Assert.Equal(2, mapped.Devices.Count);

        var lyrics = mapped.Devices[0];
        Assert.Equal("ac599d7d-d2c6-4ccd-8091-f1245556f8b3", lyrics.Id);
        Assert.Equal("Lyrics MIDI", lyrics.Name);
        Assert.Equal(["Lyrics Strip", "Bus 2"], lyrics.SourceNames!);
        Assert.Equal(["Bus 1"], lyrics.DestinationNames!);
        Assert.Null(lyrics.Direction);
        Assert.True(lyrics.AutoReconnect);

        var sender = mapped.Devices[1];
        Assert.Equal(MIDIDeviceItemDirection.Output, sender.Direction);
        Assert.Null(sender.SourceNames);
        Assert.Null(sender.AutoReconnect);
        Assert.Contains(mapped.Warnings, w => w.Contains("DMX"));
    }

    [Fact]
    public async Task MacroCollectionsBecomeComboFolders()
    {
        var walkIn = new RV.MacrosDocument.Types.Macro
        {
            Uuid = Uuid("MACRO-A"), Name = "Walk In",
            Actions = { new RV.Action { Clear = new() { TargetLayer = RV.Action.Types.ClearType.Types.ClearTargetLayer.All } } },
        };
        var walkOut = walkIn.Clone();
        walkOut.Uuid = Uuid("MACRO-B");
        walkOut.Name = "Walk Out";
        var placeholder = new RV.MacrosDocument.Types.Macro { Uuid = Uuid("MACRO-C"), Name = "Empty" };
        var collection = new RV.MacrosDocument.Types.MacroCollection { Uuid = Uuid("COLL-1"), Name = "Service Flow" };
        collection.Items.Add(new[] { walkIn, walkOut, placeholder }.Select(m => new RV.MacrosDocument.Types.MacroCollection.Types.Item { MacroId = m.Uuid.Clone() }));
        var doc = new RV.MacrosDocument { Macros = { walkIn, walkOut, placeholder }, MacroCollections = { collection } };

        using var show = new TempFolder();
        Write(doc, show.Combine("Configuration", "Macros"));
        using var library = new TestLibrary();
        var importer = new ProWorkspaceImporter(library.Store);
        var result = await importer.ImportWorkspaceAsync(show.Path, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(["macro-a", "macro-b"], result.Combos.Select(c => c.Id));
        var folder = Assert.Single(result.ComboFolders);
        Assert.Equal("Service Flow", folder.Name);
        Assert.Equal(new HashSet<string> { "macro-a", "macro-b" }, folder.ComboIds.ToHashSet());

        var again = await importer.ImportWorkspaceAsync(show.Path, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(again.ComboFolders);
        Assert.Equal(new HashSet<string> { "macro-a", "macro-b" }, again.ComboFolders[0].ComboIds.ToHashSet());
    }

    [Fact]
    public async Task RealWorkspaceSweep()
    {
        var dir = Environment.GetEnvironmentVariable("PRO_IMPORT_WORKSPACE_DIR");
        Assert.SkipWhen(string.IsNullOrEmpty(dir), "set PRO_IMPORT_WORKSPACE_DIR to a ProPresenter show directory");
        using var library = new TestLibrary();
        var result = await new ProWorkspaceImporter(library.Store).ImportWorkspaceAsync(
            Environment.ExpandEnvironmentVariables(dir!), new WorkspaceImportOptions { Policy = ImportConflictPolicy.Replace },
            cancellationToken: TestContext.Current.CancellationToken);
        var output = TestContext.Current.TestOutputHelper;
        output?.WriteLine("WORKSPACE sweep:");
        output?.WriteLine($"  presentations: {result.Presentations.Count(p => p.PresentationId is not null)}/{result.Presentations.Count}");
        output?.WriteLine($"  themes: {string.Join(", ", result.ThemesImported)}  overlays: {string.Join(", ", result.OverlaysImported)}");
        output?.WriteLine($"  alerts: {string.Join(", ", result.AlertsImported)}  layouts: {string.Join(", ", result.LayoutsImported)}");
        output?.WriteLine($"  combos: {string.Join(", ", result.Combos.Select(c => c.Name))}  timers: {string.Join(", ", result.TimerPlans.Select(t => t.Name))}");
        output?.WriteLine($"  video inputs: {string.Join(", ", result.InputPlans.Select(i => $"{i.Name} [{i.Kind}] {i.SourceId ?? "unassigned"}"))}");
        output?.WriteLine($"  screens: {string.Join(", ", result.ScreenPlan.Screens.Select(s => $"{s.Name} {s.Width}x{s.Height} {(s.IsConfidence ? "confidence" : "audience")}"))}");
        output?.WriteLine($"  schedules: {string.Join(", ", result.ScheduleTriggers.Select(t => t.Name))}");
        output?.WriteLine($"  services: {string.Join(", ", result.ServicesImported)}");
        output?.WriteLine($"  media imported: {result.MediaImported}");
        foreach (var warning in result.Warnings) output?.WriteLine($"  ! {warning}");
        Assert.NotEmpty(result.Presentations);
    }

    [Fact]
    public void StageLayoutVisibilityLinksMapToConditions()
    {
        var timerCondition = new Visibility.Types.Condition
        {
            TimerVisibility = new()
            {
                TimerUuid = Uuid("TIMER-1"), TimerName = "Worship",
                VisibilityCriterion = Visibility.Types.Condition.Types.TimerVisibility.Types.TimerVisibilityCriterion.IsRunning,
            },
        };
        var videoCondition = new Visibility.Types.Condition
        {
            VideoCountdownVisibility = new() { VisibilityCriterion = Visibility.Types.Condition.Types.VideoCountdownVisibility.Types.VideoCountdownVisibilityCriterion.HasTimeRemaining },
        };
        var link = new Visibility { VisibilityCriterion = Visibility.Types.VisibilityCriterion.Any, Conditions = { timerCondition, videoCondition } };
        var hidden = TextElement("Parked", "later");
        hidden.Element_.Hidden = true;
        var doc = StageDocument("LAYOUT-V", TextElement("Worship Timer", "0:00", dataLink: new DataLink { VisibilityLink = link }), hidden);

        var objects = ProWorkspaceMapper.MapStageLayouts(doc, new Dictionary<string, string> { ["timer-1"] = "mxu-timer-9" })[0].Layout.Objects;
        Assert.Equal(2, objects.Count);
        Assert.True(objects[0].Hidden);
        var conditioned = objects[1];
        Assert.Equal(VisibilityMatch.Any, conditioned.VisibilityMatch);
        var conditions = conditioned.VisibilityConditions!;
        Assert.Equal(2, conditions.Count);
        Assert.Equal(VisibilityConditionKind.Timer, conditions[0].ConditionKind);
        Assert.Equal(VisibilityConditionState.IsRunning, conditions[0].State);
        Assert.Equal("mxu-timer-9", conditions[0].TimerId);
        Assert.Equal(VisibilityConditionKind.VideoCountdown, conditions[1].ConditionKind);
        Assert.Equal(VisibilityConditionState.HasTimeRemaining, conditions[1].State);
    }

    [Fact]
    public void DanglingTimerVisibilityWarnsAndFollowsPrimary()
    {
        var link = new Visibility
        {
            Conditions = { new Visibility.Types.Condition { TimerVisibility = new() { TimerUuid = Uuid("TIMER-GONE"), TimerName = "Deleted Timer" } } },
        };
        var mapped = ProWorkspaceMapper.MapStageLayouts(StageDocument("LAYOUT-D", TextElement("Box", "x", dataLink: new DataLink { VisibilityLink = link })));
        var obj = mapped[0].Layout.Objects[0];
        Assert.Null(obj.VisibilityMatch);
        Assert.Null(obj.VisibilityConditions?[0].TimerId);
        Assert.Contains(mapped[0].Warnings, w => w.Contains("Deleted Timer"));
    }

    private static SlideElement LiveVideoElement(string name, string uniqueId, string deviceName = "",
        RV.Media.Types.VideoDevice.Types.Type type = RV.Media.Types.VideoDevice.Types.Type.Av) => Wrap(new G.Element
    {
        Uuid = Uuid(Guid.NewGuid().ToString()), Name = name, Opacity = 1,
        Bounds = new() { Size = new() { Width = 800, Height = 450 } },
        Fill = new() { Media = new() { LiveVideo = new() { LiveVideo = new() { VideoDevice = new() { Type = type, UniqueId = uniqueId, Name = deviceName } } } } },
    });

    [Fact]
    public void StageLayoutLiveVideoBoxBindsANamedInput()
    {
        var doc = StageDocument("LAYOUT-CAM", LiveVideoElement("Cam", "0x1111", "Center Camera"));
        var book = new ProInputPlanBook();
        book.RegisterWorkspaceInput("INPUT-1", "Center Camera",
            new RV.Media.Types.VideoDevice { Type = RV.Media.Types.VideoDevice.Types.Type.Av, UniqueId = "0x1111", Name = "Center Camera" });
        var mapped = ProWorkspaceMapper.MapStageLayouts(doc, inputs: book);
        var obj = mapped[0].Layout.Objects[0];
        Assert.Equal(FillKind.Media, obj.Fill?.FillKind);
        Assert.Equal("input-1", obj.Fill?.LiveInputId);
        Assert.Null(obj.Fill?.MediaId);
        Assert.Empty(mapped[0].MediaWants);
        Assert.Empty(mapped[0].Warnings);
        Assert.Equal([new ProInputPlan("input-1", "Center Camera", CaptureSourceKind.Camera, "0x1111")], book.Plans);
    }

    [Fact]
    public void UnconfiguredLiveVideoDeviceMintsOnePlanAcrossPhases()
    {
        var doc = StageDocument("LAYOUT-N", LiveVideoElement("NDI", "", "BIRDDOG (CAM)", RV.Media.Types.VideoDevice.Types.Type.Ndi));
        var book = new ProInputPlanBook();
        book.Seed([]);
        var mapped = ProWorkspaceMapper.MapStageLayouts(doc, inputs: book);
        var plan = Assert.Single(book.Plans);
        Assert.Equal(CaptureSourceKind.Ndi, plan.Kind);
        Assert.Equal("BIRDDOG (CAM)", plan.Name);
        Assert.Equal("BIRDDOG (CAM)", plan.SourceId);
        Assert.Equal(plan.NewId, mapped[0].Layout.Objects[0].Fill?.LiveInputId);

        var fire = new RV.Action
        {
            Media = new()
            {
                Element = new() { LiveVideo = new() { LiveVideo = new() { VideoDevice = new() { Type = RV.Media.Types.VideoDevice.Types.Type.Ndi, Name = "BIRDDOG (CAM)" } } } },
            },
        };
        var combos = ProWorkspaceMapper.MapMacros(Macros("MACRO-N", "Cam", fire), inputs: book);
        Assert.Single(book.Plans);
        Assert.Equal(book.Plans[0].NewId, combos[0].Combo.Actions[0].LiveInputId);
        Assert.Empty(combos[0].Warnings);
    }

    [Fact]
    public void SeededInventoryEntryIsReusedInsteadOfPlanned()
    {
        var book = new ProInputPlanBook();
        book.Seed([new ProInputSeed("existing-1", "Center Camera", "0x1111")]);
        book.RegisterWorkspaceInput("INPUT-1", "Center Camera", new RV.Media.Types.VideoDevice { UniqueId = "0x1111" });
        Assert.Empty(book.Plans);
        Assert.Equal("existing-1", book.WorkspaceInputId(0));
    }

    [Fact]
    public void DeviceLessVideoInputStepImportsUnassignedWithWarning()
    {
        var book = new ProInputPlanBook();
        book.Seed([]);
        var mapped = ProWorkspaceMapper.MapMacros(Macros("MACRO-E", "test", new RV.Action { Media = new() { Element = new() { LiveVideo = new() } } }), inputs: book);
        Assert.Equal(book.Plans[0].NewId, mapped[0].Combo.Actions[0].LiveInputId);
        Assert.Equal("Video Input", book.Plans[0].Name);
        Assert.Null(book.Plans[0].SourceId);
        Assert.Contains(mapped[0].Warnings, w => w.Contains("named no device"));
    }

    [Fact]
    public void VideoInputVisibilityBindsByWorkspaceIndex()
    {
        var link = new Visibility
        {
            Conditions =
            {
                new Visibility.Types.Condition
                {
                    VideoInputVisibility = new()
                    {
                        VideoInputIndex = 0,
                        VisibilityCriterion = Visibility.Types.Condition.Types.VideoInputVisibility.Types.VideoInputVisibilityCriterion.Active,
                    },
                },
            },
        };
        var doc = StageDocument("LAYOUT-V", TextElement("Cam Tag", "LIVE", dataLink: new DataLink { VisibilityLink = link }));
        var book = new ProInputPlanBook();
        book.RegisterWorkspaceInput("INPUT-1", "Center Camera", new RV.Media.Types.VideoDevice());
        var mapped = ProWorkspaceMapper.MapStageLayouts(doc, inputs: book);
        var conditions = mapped[0].Layout.Objects[0].VisibilityConditions!;
        Assert.Equal(VisibilityConditionKind.LiveInput, conditions[0].ConditionKind);
        Assert.Equal("input-1", conditions[0].LiveInputId);
        Assert.Empty(mapped[0].Warnings);
    }

    [Fact]
    public void PlaylistItemAndPresentationLinksMapToServiceSources()
    {
        static DataLink Item(DataLink.Types.PlaylistItem.Types.PlaylistItemSourceType source) => new() { PlaylistItem = new() { PlaylistItemSource = source } };
        var doc = StageDocument("LAYOUT-P",
            TextElement("Header", "PRAISE SET", dataLink: Item(DataLink.Types.PlaylistItem.Types.PlaylistItemSourceType.CurrentHeader)),
            TextElement("Next Item", "", dataLink: Item(DataLink.Types.PlaylistItem.Types.PlaylistItemSourceType.Next)),
            TextElement("Item", "", dataLink: Item(DataLink.Types.PlaylistItem.Types.PlaylistItemSourceType.Current)),
            TextElement("Song", "", dataLink: new DataLink { Presentation = new() }));
        var mapped = ProWorkspaceMapper.MapStageLayouts(doc);
        var objects = mapped[0].Layout.Objects;
        Assert.Equal(TextSourceKind.CurrentPresentation, objects[0].TextLink?.Source);
        Assert.Equal(TextSourceKind.CurrentServiceItem, objects[1].TextLink?.Source);
        Assert.Equal(TextSourceKind.NextServiceItem, objects[2].TextLink?.Source);
        Assert.Null(objects[3].TextLink);
        Assert.Contains(mapped[0].Warnings, w => w.Contains("currentHeader"));
    }
}

public class ProLookAndParityTests
{
    [Fact]
    public void LooksMapPerScreenLayerEnables()
    {
        var look = new RV.ProAudienceLook
        {
            Uuid = Uuid("LOOK-1"), Name = "No Lyrics",
            ScreenLooks =
            {
                new RV.ProAudienceLook.Types.ProScreenLook
                {
                    ProScreenUuid = Uuid("SCREEN-A"), LiveVideoEnabled = true, PresentationBackgroundEnabled = true,
                    PresentationForegroundEnabled = false, PropsLayerEnabled = true, MessagesLayerEnabled = true, AnnouncementsEnabled = false,
                },
            },
        };
        var workspace = new RV.ProPresenterWorkspace { AudienceLooks = { look }, LiveAudienceLook = new() { OriginalLookUuid = Uuid("LOOK-1") } };
        var mapped = ProWorkspaceMapper.MapLooks(workspace);
        Assert.Single(mapped.Looks);
        Assert.Equal("look-1", mapped.Looks[0].ProUuid);
        Assert.Equal("look-1", mapped.LiveLookUuid);
        var layers = mapped.Looks[0].ScreenLooks[0].EnabledLayers;
        Assert.Contains("videoInput", layers);
        Assert.Contains("loopingVideos", layers);
        Assert.DoesNotContain("stillGraphics", layers);
        Assert.DoesNotContain("slide", layers);
        Assert.DoesNotContain("videos", layers);
        Assert.Contains("overlays", layers);
        Assert.Contains("alerts", layers);
        Assert.DoesNotContain("digitalSignage", layers);
    }

    [Fact]
    public void LookAlternateTemplateMapsToItsThemeName()
    {
        var look = new RV.ProAudienceLook
        {
            Uuid = Uuid("LOOK-T"), Name = "Stream",
            ScreenLooks =
            {
                new RV.ProAudienceLook.Types.ProScreenLook
                {
                    ProScreenUuid = Uuid("SCREEN-A"), PresentationForegroundEnabled = true,
                    TemplateDocumentFilePath = new() { AbsoluteString = "file:///Users/anna/Documents/ProPresenter/Themes/Stream%20Lower%20Third/Theme" },
                },
                new RV.ProAudienceLook.Types.ProScreenLook { ProScreenUuid = Uuid("SCREEN-B"), PresentationForegroundEnabled = true },
            },
        };
        var mapped = ProWorkspaceMapper.MapLooks(new RV.ProPresenterWorkspace { AudienceLooks = { look } });
        Assert.Equal("Stream Lower Third", mapped.Looks[0].ScreenLooks[0].SlideThemeName);
        Assert.Null(mapped.Looks[0].ScreenLooks[1].SlideThemeName);
        Assert.Equal("No Bold", ProWorkspaceMapper.TemplateThemeName(new RV.URL { Local = new() { Path = "Themes/No Bold/Theme" } }));
    }

    [Fact]
    public void LookAnnouncementsLayerDropsWithAWarning()
    {
        var look = new RV.ProAudienceLook
        {
            Uuid = Uuid("LOOK-A"), Name = "Announcements",
            ScreenLooks = { new RV.ProAudienceLook.Types.ProScreenLook { ProScreenUuid = Uuid("SCREEN-A"), AnnouncementsEnabled = true } },
        };
        var mapped = ProWorkspaceMapper.MapLooks(new RV.ProPresenterWorkspace { AudienceLooks = { look } });
        Assert.DoesNotContain("digitalSignage", mapped.Looks[0].ScreenLooks[0].EnabledLayers);
        Assert.Contains(mapped.Warnings, w => w.Contains("announcements"));
    }

    [Fact]
    public void PropAndLookAndInputMacroStepsMapOntoNewActions()
    {
        var macro = new RV.MacrosDocument.Types.Macro
        {
            Uuid = Uuid("MACRO-P"), Name = "Parity",
            Actions =
            {
                new RV.Action { Prop = new() { Identification = new() { ParameterUuid = Uuid("PROP-1") }, Trigger = new() } },
                new RV.Action { Prop = new() { Clear = new() } },
                new RV.Action { AudienceLook = new() { Identification = new() { ParameterUuid = Uuid("LOOK-1") } } },
                new RV.Action
                {
                    Media = new()
                    {
                        Element = new()
                        {
                            LiveVideo = new() { LiveVideo = new() { VideoDevice = new() { Type = RV.Media.Types.VideoDevice.Types.Type.Ndi, UniqueId = "BIRDDOG-04A82 (CAM)" } } },
                        },
                    },
                },
                new RV.Action { Clear = new() { TargetLayer = RV.Action.Types.ClearType.Types.ClearTargetLayer.LiveVideo } },
            },
        };
        var mapped = ProWorkspaceMapper.MapMacros(new RV.MacrosDocument { Macros = { macro } });
        var combo = mapped[0].Combo;
        Assert.Equal(
            new[] { SlideActionKind.FireOverlay, SlideActionKind.DismissOverlay, SlideActionKind.SwitchOutputPreset, SlideActionKind.FireLiveInput, SlideActionKind.ClearLayer },
            combo.Actions.Select(a => a.Kind));
        Assert.Equal("prop-1", combo.Actions[0].OverlayId);
        Assert.Null(combo.Actions[1].OverlayId);
        Assert.Equal("look-1", combo.Actions[2].PresetId);
        Assert.Equal(CaptureSourceKind.Ndi, combo.Actions[3].InputSourceKind);
        Assert.Equal("BIRDDOG-04A82 (CAM)", combo.Actions[3].InputSourceId);
        Assert.Equal("videoInput", combo.Actions[4].Layer);
        Assert.Empty(mapped[0].Warnings);
    }
}
