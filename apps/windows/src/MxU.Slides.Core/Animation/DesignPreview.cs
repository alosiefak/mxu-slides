using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Animation;

/// <summary>
/// The theme editor's live preview: a theme design as a real slide that uses it shows it, with
/// sample words in its text boxes, still or playing its builds (its Ins, each click in turn, a hold,
/// then its Outs). The slide is drawn exactly as the outputs draw one (the design's placeholders
/// donate their steps, Windows' kinetic builds included), so what the editor plays is what the room
/// will see. <see cref="SampleSlide"/> is a port of the Mac's NewSlide.preview(of:themeId:).
/// </summary>
public static class DesignPreview
{
    /// <summary>The words the preview shows by default: a verse, as most designs carry lyrics.</summary>
    public const string SampleLyrics = "Amazing grace, how sweet the sound\nThat saved a wretch like me";

    /// <summary>
    /// A slide that uses <paramref name="design"/>: one text box per text placeholder, named as it is,
    /// holding the placeholder's own words, or <paramref name="sample"/> in the first one.
    /// </summary>
    public static Slide SampleSlide(Slide design, string themeId, string? sample = null)
    {
        var id = $"newSlide|{themeId}|{design.Id}";
        var first = true;
        var boxes = new List<SlideObject>();
        foreach (var o in design.Objects.Where(o => o.ObjectKind == SlideObjectKind.Text && o.TextLink is null && o.Hidden != true))
        {
            var text = first && !string.IsNullOrEmpty(sample) ? sample : o.Text;
            first = false;
            boxes.Add(new SlideObject { Id = $"{id}|{o.Id}", ObjectKind = SlideObjectKind.Text, Name = o.Name, Text = text });
        }
        return new Slide { Id = id, Name = design.Name, Objects = boxes, ThemeSlideName = design.Name };
    }

    /// <summary>How long the play-through lasts (Ins, clicks, a hold, Outs) for this slide in this theme.</summary>
    public static double Duration(Slide sample, Theme theme, SceneSize canvas, TextPager? pager = null)
    {
        var template = SlideSceneBuilder.ThemeSlide(sample, theme);
        var composition = AnimatedItems.Compose(sample.Objects, template, theme, sample.AnimationOrder, canvas, pager);
        var objects = composition.Stack.Select(e => e.Object).ToList();
        return AnimationSequence.PreviewTimeline(objects, composition.Order).Duration;
    }

    /// <summary>
    /// The sample slide drawn through the theme, playing from <paramref name="startedAt"/> (a host
    /// clock time), or frozen <paramref name="time"/> seconds in; with neither moving (null context
    /// when <paramref name="still"/>), it rests as built.
    /// </summary>
    public static AnimatedScene PlayThrough(Slide sample, Theme theme, SceneSize canvas, double startedAt, double? time = null, TextPager? pager = null, bool still = false)
    {
        var scene = new AnimatedScene(canvas);
        if (ColorHex.Color(theme.BackgroundColorHex) is { } background) scene.Background = background;
        var template = SlideSceneBuilder.ThemeSlide(sample, theme);
        var composition = AnimatedItems.Compose(sample.Objects, template, theme, sample.AnimationOrder, canvas, pager);
        var objects = composition.Stack.Select(e => e.Object).ToList();
        var context = still ? null : AnimationSequence.PreviewContext(objects, composition.Order, time, startedAt);
        var wiring = SlideSceneBuilder.MaskWiring(objects);
        foreach (var entry in composition.Stack)
        {
            var placeholder = entry.FromTheme ? null : composition.Placeholders.GetValueOrDefault(entry.Object.Id);
            var items = SlideSceneBuilder.RenderItems(entry.Object, theme, placeholder, canvas, entry.FromTheme ? entry.Id : null,
                wiring.MaskedBy.GetValueOrDefault(entry.Object.Id), wiring.Mattes.Contains(entry.Object.Id) ? entry.Object.Id : null);
            foreach (var item in AnimatedItems.For(entry.Object, items, composition.StepsFor(entry.Object.Id), theme, placeholder, canvas, context, startedAt))
                scene.AddItem(item, LayerKind.Slide);
        }
        return scene;
    }
}
