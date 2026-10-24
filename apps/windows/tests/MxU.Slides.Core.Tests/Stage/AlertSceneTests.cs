using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Core.Stage;

namespace MxU.Slides.Core.Tests.Stage;

// Port of the alert cases of SlideSceneTests/ConfidenceSceneTests.swift and
// ShowStateTests.swift (alertAnchorRidesItsItems), plus the alert's own layer and its Outs.
public class AlertSceneTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.FromUnixTimeSeconds(1_000);

    private static Slide LyricSlide(string id, string text) => new()
    {
        Id = id, Name = id, Objects = [new SlideObject { Id = $"{id}-text", ObjectKind = SlideObjectKind.Text, Name = "Lyrics", Text = text }],
    };

    private static List<AnimatedItem> Items(AnimatedScene scene, LayerKind layer) => scene[layer].Items;

    private static List<string> Texts(IEnumerable<AnimatedItem> items) =>
        [.. items.Select(i => i.Render.Content is ItemContent.Text t ? t.Styled.String : null).OfType<string>()];

    private static Theme AlertsTheme(string slideName, List<AnimationStep>? bandSteps = null) => new()
    {
        Id = "t", Name = "Theme", FontFamily = "Helvetica", FontSize = 60, TextColorHex = "#FFFFFFFF", BackgroundColorHex = "#000000FF",
        Slides =
        [
            new Slide
            {
                Id = "t-alerts", Name = slideName,
                Objects =
                [
                    new SlideObject { Id = "t-decor", ObjectKind = SlideObjectKind.Shape, Name = "Band", Text = "", ShapeKind = ShapeKind.Rectangle, AnimationSteps = bandSteps },
                    new SlideObject { Id = "t-msg", ObjectKind = SlideObjectKind.Text, Name = "Message", Text = "Sample" },
                ],
            },
        ],
    };

    [Fact]
    public void AlertFiresAndReplacesTheLiveOne()
    {
        var show = new ShowState();
        show.FireAlert(new CueAlert("Mic is muted", AlertBehavior.Flash));
        show.FireAlert(new CueAlert("Slow down", AlertBehavior.Persist));
        Assert.Equal("Slow down", show.LiveAlert?.Message); // one alert at a time: firing replaces
    }

    [Fact]
    public void ClearFunctionAlertsSweepsTheAlert()
    {
        var show = new ShowState();
        show.FireAlert(new CueAlert("x", AlertBehavior.Persist));
        show.Clear(ShowFunction.Alerts);
        Assert.Null(show.LiveAlert);
    }

    [Fact]
    public void ClearLayerAlertsSweepsTheAlert()
    {
        var show = new ShowState();
        show.FireAlert(new CueAlert("x", AlertBehavior.Persist));
        show.Clear(LayerKind.Alerts);
        Assert.Null(show.LiveAlert);
    }

    [Fact]
    public void ClearAllIncludesAlerts()
    {
        var show = new ShowState();
        show.FireAlert(new CueAlert("x", AlertBehavior.Flash));
        show.ClearAll();
        Assert.Null(show.LiveAlert);
    }

    [Fact]
    public void AlertPersistsAcrossSlideAdvances()
    {
        var show = new ShowState();
        show.FireAlert(new CueAlert("x", AlertBehavior.Persist));
        show.Fire(LyricSlide("a", "verse"));
        Assert.NotNull(show.LiveAlert);
    }

    [Fact]
    public void ConfidenceAlertNeverReachesTheProgramScene()
    {
        var show = new ShowState();
        show.FireAlert(new CueAlert("Stage only", AlertBehavior.Persist, AlertTarget.Confidence));
        Assert.Empty(Items(show.AnimatedScene(), LayerKind.Alerts)); // a confidence alert must never land on audience glass
    }

    [Fact]
    public void AudienceAlertRendersOnTheProgramAlertsLayer()
    {
        var show = new ShowState();
        show.FireAlert(new CueAlert("Child 42 to nursery", AlertBehavior.Persist, AlertTarget.Audience));
        Assert.NotEmpty(Items(show.AnimatedScene(), LayerKind.Alerts));
    }

    [Fact]
    public void FlashPhaseHidesTheAlertOnItsOffBeat()
    {
        var show = new ShowState();
        show.FireAlert(new CueAlert("x", AlertBehavior.Flash, AlertTarget.Audience));
        Assert.NotEmpty(Items(show.AnimatedScene(), LayerKind.Alerts));
        show.AlertVisible = false;
        Assert.Empty(Items(show.AnimatedScene(), LayerKind.Alerts));
        show.FireAlert(new CueAlert("y", AlertBehavior.Flash, AlertTarget.Audience));
        Assert.True(show.AlertVisible); // a new alert always starts on its on beat

        var info = new ConfidenceInfo { Alert = new CueAlert("x", AlertBehavior.Flash, AlertTarget.Confidence), AlertVisible = false };
        var stageMessage = new TextLink { Source = TextSourceKind.StageMessage };
        Assert.Null(LinkedText.Resolve(stageMessage, info, Start)); // the stage's message box blanks on the off beat too
    }

    [Fact]
    public void AlertAnchorRidesItsItems()
    {
        var show = new ShowState();
        show.FireAlert(new CueAlert("Car lights on", AlertBehavior.Persist, AlertTarget.Audience) { Id = "al" }, atHostTime: 55);
        var items = Items(show.AnimatedScene(), LayerKind.Alerts);
        Assert.NotEmpty(items);
        Assert.All(items, i => Assert.Equal(55, i.TickerAnchorHostTime));
        Assert.Contains(items, i => i.Id == "alert-band-al");
    }

    [Fact]
    public void ThemedAudienceAlertComposesThroughTheAlertsThemeSlide()
    {
        var alert = new CueAlert("Child 42", AlertBehavior.Persist, AlertTarget.Audience) { Theme = AlertsTheme("Alerts") };
        var rendered = AlertSceneBuilder.AudienceItems(alert, SlideSceneBuilder.CanvasSize);
        Assert.Equal(2, rendered.Count);
        Assert.Equal(["Child 42"], Texts(rendered)); // the fired message fills the placeholder; sample text never renders
    }

    [Fact]
    public void ThemedAlertResolvesTimerLinkedText()
    {
        var theme = AlertsTheme("Alerts");
        theme.Slides![0].Objects.Add(new SlideObject
        {
            Id = "t-timer", ObjectKind = SlideObjectKind.Text, Name = "Timer", Text = "", TextLink = new TextLink { Source = TextSourceKind.Timer, TimerId = "grace" },
        });
        var info = new ConfidenceInfo
        {
            Timers = [new TimerSnapshot("Grace Timer", TimerMode.Countdown, id: "grace", isRunning: true, runningSince: Start, durationSeconds: 450)],
        };
        var alert = new CueAlert("Service starts soon", AlertBehavior.Persist, AlertTarget.Audience) { Theme = theme };
        var rendered = AlertSceneBuilder.AudienceItems(alert, SlideSceneBuilder.CanvasSize,
            resolveLinkedText: objects => LinkedText.ResolvedObjects(objects, info, Start.AddSeconds(30)));
        Assert.Contains(Texts(rendered), t => t.Contains("7:00")); // 450 s minus 30 elapsed reads 7:00, live

        var unresolved = AlertSceneBuilder.AudienceItems(alert, SlideSceneBuilder.CanvasSize);
        Assert.Equal(rendered.Count, unresolved.Count);
    }

    [Fact]
    public void AlertMessageTimerTokensResolveOnTheBannerToo()
    {
        var info = new ConfidenceInfo
        {
            Timers = [new TimerSnapshot("Grace Timer", TimerMode.Countdown, id: "grace", isRunning: true, runningSince: Start, durationSeconds: 450)],
        };
        var alert = new CueAlert("{Grace Timer}", AlertBehavior.Persist, AlertTarget.Audience);
        var banner = AlertSceneBuilder.AudienceItems(alert, SlideSceneBuilder.CanvasSize, message => LinkedText.ResolvedAlertMessage(message, info, Start.AddSeconds(30)));
        Assert.Equal(["7:00"], Texts(banner));
    }

    [Fact]
    public void ThemeWithoutAlertsSlideFallsBackToTheBanner()
    {
        var alert = new CueAlert("Child 42", AlertBehavior.Persist, AlertTarget.Audience) { Theme = AlertsTheme("Lyrics") };
        var rendered = AlertSceneBuilder.AudienceItems(alert, SlideSceneBuilder.CanvasSize);
        var banner = AlertBanner.Items("Child 42", SlideSceneBuilder.CanvasSize, alert.Id).ToList();
        Assert.Equal(banner.Select(i => i.Frame), rendered.Select(i => i.Render.Frame));
    }

    [Fact]
    public void AnAlertDrawsOnTheLayerItNamesAndClearsWithIt()
    {
        var show = new ShowState();
        show.FireAlert(new CueAlert("Sing it again", AlertBehavior.Persist, AlertTarget.Audience) { Layer = "overlays" });
        var scene = show.AnimatedScene();
        Assert.Empty(Items(scene, LayerKind.Alerts));
        Assert.NotEmpty(Items(scene, LayerKind.Overlays));
        show.Clear(LayerKind.Alerts);
        Assert.NotNull(show.LiveAlert); // clearing another layer leaves it
        show.Clear(LayerKind.Overlays);
        Assert.Null(show.LiveAlert);
    }

    [Fact]
    public void AThemedAlertPlaysItsOutsBeforeItGoes()
    {
        var outStep = new AnimationStep { Id = "out", Kind = AnimationKind.Out, Animation = StepAnimation.Fade, Trigger = AnimationTrigger.OnDismiss, DurationSeconds = 0.5 };
        var alert = new CueAlert("Welcome", AlertBehavior.Persist, AlertTarget.Audience) { Theme = AlertsTheme("Alerts", [outStep]) };
        Assert.True(AlertSceneBuilder.Animates(alert));
        Assert.Equal(0.5, AlertSceneBuilder.ExitDuration(alert), 3);

        var show = new ShowState();
        show.FireAlert(alert, atHostTime: 10);
        Assert.All(Items(show.AnimatedScene(), LayerKind.Alerts), i => Assert.Equal(10, i.AnimationContext?.AnchorHostTime));
        show.Clear(ShowFunction.Alerts, atHostTime: 20);
        Assert.True(show.AlertIsLeaving && show.HasPendingExits);
        Assert.All(Items(show.AnimatedScene(), LayerKind.Alerts), i => Assert.Equal(20, i.AnimationContext?.DismissHostTime));
        Assert.False(show.SweepFinishedExits(now: 20.3)); // still fading
        Assert.NotNull(show.LiveAlert);
        Assert.True(show.SweepFinishedExits(now: 20.6));
        Assert.Null(show.LiveAlert);

        show.FireAlert(alert, atHostTime: 30);
        show.Clear(ShowFunction.Alerts, atHostTime: 31);
        show.Clear(ShowFunction.Alerts, atHostTime: 31.1); // asked again while leaving: it cuts
        Assert.Null(show.LiveAlert);

        var banner = new ShowState();
        banner.FireAlert(new CueAlert("Plain", AlertBehavior.Persist, AlertTarget.Audience));
        banner.Clear(ShowFunction.Alerts, atHostTime: 5);
        Assert.Null(banner.LiveAlert); // the built-in band has no Outs: it goes at once
    }

    [Fact]
    public void APlainMessageKeepsTheOldTargets()
    {
        var show = new ShowState();
        show.FireAlert("Wrap up", audience: true, stage: true);
        Assert.Equal((AlertTarget.Both, AlertBehavior.Persist), (show.LiveAlert!.Target, show.LiveAlert.Behavior));
        Assert.True(show.AlertOnAudience && show.AlertOnStage);
        show.FireAlert("Wrap up", audience: false, stage: true);
        Assert.Equal(AlertTarget.Confidence, show.LiveAlert!.Target);
        Assert.Equal("Wrap up", show.AlertMessage);
    }
}
