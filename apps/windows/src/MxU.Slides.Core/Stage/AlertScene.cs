using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Stage;

// The render half of AlertSceneBuilder (SlideScene/ConfidenceScene.swift): what an alert
// looks like on the audience screens.
public static partial class AlertSceneBuilder
{
    /// <summary>The id of the text object the fired message is poured into.</summary>
    public const string MessageObjectId = "alert-message";

    /// <summary>
    /// An alert on the audience screens. With a theme that has an "Alerts" design, the message
    /// fills that design's text placeholder and the design's shapes and media come with it,
    /// each with its own In and Out steps (a ProPresenter message's template imports as such a
    /// design). Without one, it is the built-in amber band (<see cref="AlertBanner"/>).
    /// </summary>
    /// <param name="resolveMessage">Fills "{Timer}" slots with live digits; null leaves the message as fired.</param>
    /// <param name="resolveLinkedText">Fills the design's linked boxes (a clock, a timer); null leaves them as stored.</param>
    /// <param name="context">The alert's animation clock (when it went up, when it was taken down).</param>
    /// <param name="tickerAnchorHostTime">When it went up, for tickers and block scrolls.</param>
    public static List<AnimatedItem> AudienceItems(
        CueAlert alert, SceneSize canvas,
        Func<string, string>? resolveMessage = null,
        Func<IReadOnlyList<SlideObject>, List<SlideObject>>? resolveLinkedText = null,
        AnimationContext? context = null, double? tickerAnchorHostTime = null)
    {
        var message = resolveMessage?.Invoke(alert.Message) ?? alert.Message;
        if (alert.Theme is not { } theme || AlertsThemeSlide(theme) is not { } design)
            return [.. AlertBanner.Items(message, canvas, alert.Id)
                .Select(item => new AnimatedItem { Render = item, TickerAnchorHostTime = tickerAnchorHostTime })];

        // The design's own linked boxes read live, as a slide's design does.
        var template = Json.MxuJson.Clone(design);
        if (resolveLinkedText is not null) template.Objects = resolveLinkedText(template.Objects);

        // The fired message is one text object; it takes the design's text placeholder, and the
        // design's sample text never shows.
        List<SlideObject> objects = [new SlideObject { Id = MessageObjectId, ObjectKind = SlideObjectKind.Text, Name = "Message", Text = message }];
        var placeholders = SlideSceneBuilder.PlaceholderAssignments(objects, template);
        var stack = SlideSceneBuilder.ComposedStack(objects, template);
        var stacked = stack.Select(e => e.Object).ToList();
        var wiring = SlideSceneBuilder.MaskWiring(stacked);
        var steps = AnimationSequence.SceneSteps(stacked, template.AnimationOrder);
        var items = new List<AnimatedItem>();
        foreach (var entry in stack)
        {
            var placeholder = entry.FromTheme ? null : placeholders.GetValueOrDefault(entry.Object.Id);
            var rendered = SlideSceneBuilder.RenderItems(entry.Object, theme, placeholder, canvas, $"alert-{alert.Id}-{entry.Id}",
                wiring.MaskedBy.GetValueOrDefault(entry.Object.Id), wiring.Mattes.Contains(entry.Object.Id) ? entry.Object.Id : null);
            items.AddRange(AnimatedItems.For(entry.Object, rendered, steps.GetValueOrDefault(entry.Object.Id) ?? [], theme, placeholder, canvas,
                context, tickerAnchorHostTime));
        }
        return items;
    }

    /// <summary>How long the alert's design takes to play its Outs (0 for the banner, which just goes).</summary>
    public static double ExitDuration(CueAlert alert) =>
        alert.ShowsOnAudience && AlertsThemeSlide(alert.Theme) is { } design
            ? AnimationSequence.ExitDuration(design.Objects, design.AnimationOrder)
            : 0;

    /// <summary>The alert's design animates (so its items need the alert's clock).</summary>
    public static bool Animates(CueAlert alert) =>
        AlertsThemeSlide(alert.Theme) is { } design && AnimationSequence.HasAnimationSteps(design.Objects);
}
