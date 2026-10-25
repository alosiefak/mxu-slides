using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Editing;

/// <summary>Slides on the clipboard, with the presentation they came from.</summary>
public sealed record SlideClip(string? SourcePresentationId, IReadOnlyList<Slide> Slides);

/// <summary>Slide objects on the clipboard, with the slide they came from (pasting back onto it steps them aside).</summary>
public sealed record ObjectClip(string? SourceSlideId, IReadOnlyList<SlideObject> Objects);

/// <summary>
/// The rules for cut, copy, paste and duplicate of slides and slide objects, kept apart from the
/// Windows clipboard itself (Windows/Infrastructure/AppClipboard.cs) so they can be tested. They
/// are the Mac's:
/// <list type="bullet">
/// <item>whatever is pasted gets fresh ids: a slide, its objects and its actions (SlideBulkEdit's
/// freshIDCopy), and a mask still points at its matte;</item>
/// <item>media references are kept as they are: a pasted slide shows the same background;</item>
/// <item>pasted slides join the group (section) of the slide they land after, so a song's groups
/// and arrangements stay consistent (SlideBulkEdit.insertSlides);</item>
/// <item>objects pasted back onto the slide they came from step 24 points down and right each time
/// (SlideEditorModel.pasteObjects), and objects grouped together stay grouped, in a new group.</item>
/// </list>
/// Two more, for text from another app: plain text pasted on a slide becomes a text box, and
/// lyrics pasted into a song become slides through the lyrics pipeline (Lyrics/Reflow.cs).
/// </summary>
public static class EditorClipboard
{
    /// <summary>The Windows clipboard format slides are copied as (JSON of a <see cref="SlideClip"/>).</summary>
    public const string SlidesFormat = "MxU Slides: slides";

    /// <summary>The Windows clipboard format slide objects are copied as (JSON of an <see cref="ObjectClip"/>).</summary>
    public const string ObjectsFormat = "MxU Slides: slide objects";

    /// <summary>How far each repeated paste onto the same slide steps (the Mac's 24 points).</summary>
    public const double PasteCascade = 24;

    // MARK: On the clipboard

    public static string Encode(SlideClip clip) => MxuJson.Serialize(clip, indented: false);

    public static string Encode(ObjectClip clip) => MxuJson.Serialize(clip, indented: false);

    /// <summary>The slides in a clipboard text, or null when it isn't ours or can't be read.</summary>
    public static SlideClip? DecodeSlides(string? json) => Decode<SlideClip>(json) is { Slides.Count: > 0 } clip ? clip : null;

    public static ObjectClip? DecodeObjects(string? json) => Decode<ObjectClip>(json) is { Objects.Count: > 0 } clip ? clip : null;

    private static T? Decode<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return MxuJson.Deserialize<T>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// What another app gets when slides are pasted there: each slide's words, a blank line
    /// between slides.
    /// </summary>
    public static string PlainText(IEnumerable<Slide> slides) =>
        string.Join("\n\n", slides.Select(s => PlainText(s.Objects)).Where(t => t.Length > 0));

    public static string PlainText(IEnumerable<SlideObject> objects) =>
        string.Join("\n", objects.Where(o => o.ObjectKind == SlideObjectKind.Text && o.Text.Length > 0).Select(o => o.Text));

    // MARK: Fresh copies

    /// <summary>
    /// A copy of a slide with new ids for it, its objects and its actions; masks follow their
    /// mattes. A port of the Mac's Slide.freshIDCopy.
    /// </summary>
    public static Slide FreshCopy(Slide slide, Func<string>? newId = null)
    {
        newId ??= LibraryStore.NewId;
        var copy = MxuJson.Clone(slide);
        copy.Id = newId();
        var fresh = copy.Objects.ToDictionary(o => o.Id, _ => newId(), StringComparer.Ordinal);
        foreach (var o in copy.Objects)
        {
            o.Id = fresh.GetValueOrDefault(o.Id) ?? newId();
            if (o.MaskObjectId is { } mask && fresh.TryGetValue(mask, out var moved)) o.MaskObjectId = moved;
        }
        foreach (var action in copy.Actions ?? []) action.Id = newId();
        return copy;
    }

    // MARK: Pasting slides

    /// <summary>A pasted slide and the slide it was copied from (to copy its Windows extras too).</summary>
    public sealed record PastedSlide(Slide Source, Slide Copy);

    /// <summary>
    /// Inserts fresh copies of <paramref name="slides"/> after <paramref name="afterSlideId"/> (at the
    /// end when it's null or not found), each joining that slide's group. Returns them in order.
    /// </summary>
    public static IReadOnlyList<PastedSlide> InsertSlides(Presentation presentation, IReadOnlyList<Slide> slides, string? afterSlideId, Func<string>? newId = null)
    {
        var anchor = afterSlideId is null ? -1 : presentation.Slides.FindIndex(s => s.Id == afterSlideId);
        if (anchor < 0) anchor = presentation.Slides.Count - 1;
        var neighbour = anchor >= 0 && anchor < presentation.Slides.Count ? presentation.Slides[anchor] : presentation.Slides.LastOrDefault();
        var known = (presentation.Sections ?? []).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var pasted = slides.Select(source =>
        {
            var copy = FreshCopy(source, newId);
            // Into the neighbour's group; with no neighbour, only a group this presentation has.
            copy.SectionId = neighbour is not null ? neighbour.SectionId : copy.SectionId is { } s && known.Contains(s) ? s : null;
            return new PastedSlide(source, copy);
        }).ToList();
        presentation.Slides.InsertRange(Math.Min(anchor + 1, presentation.Slides.Count), pasted.Select(p => p.Copy));
        return pasted;
    }

    /// <summary>Duplicates each slide in place, the copy straight after it (SlideBulkEdit.duplicateSlides).</summary>
    public static IReadOnlyList<PastedSlide> DuplicateSlides(Presentation presentation, IReadOnlyCollection<string> slideIds, Func<string>? newId = null)
    {
        var made = new List<PastedSlide>();
        for (var index = presentation.Slides.Count - 1; index >= 0; index--)
        {
            var source = presentation.Slides[index];
            if (!slideIds.Contains(source.Id)) continue;
            var copy = FreshCopy(source, newId);
            presentation.Slides.Insert(index + 1, copy);
            made.Insert(0, new PastedSlide(source, copy));
        }
        return made;
    }

    /// <summary>
    /// Removes the slides (cut or delete). A presentation keeps at least one slide, as the Windows
    /// grid always has. Returns whether any went.
    /// </summary>
    public static bool DeleteSlides(Presentation presentation, IReadOnlyCollection<string> slideIds)
    {
        var remaining = presentation.Slides.Where(s => !slideIds.Contains(s.Id)).ToList();
        if (remaining.Count == presentation.Slides.Count || remaining.Count == 0) return false;
        presentation.Slides = remaining;
        return true;
    }

    // MARK: Pasting objects

    /// <summary>
    /// Adds fresh copies of the clip's objects to <paramref name="slide"/> (on top). Pasted back
    /// onto the slide they came from, they step <see cref="PasteCascade"/> × <paramref name="cascade"/>
    /// down and right from where they show (<paramref name="frameOf"/>: their own frame, or their
    /// theme placeholder's). Returns the new objects.
    /// </summary>
    public static IReadOnlyList<SlideObject> PasteObjects(Slide slide, ObjectClip clip, int cascade, Func<SlideObject, SceneRect>? frameOf = null, Func<string>? newId = null)
    {
        newId ??= LibraryStore.NewId;
        var ontoSource = clip.SourceSlideId == slide.Id;
        var offset = ontoSource ? cascade * PasteCascade : 0;
        var ids = clip.Objects.ToDictionary(o => o.Id, _ => newId(), StringComparer.Ordinal);
        var groups = new Dictionary<string, string>(StringComparer.Ordinal);
        var pasted = clip.Objects.Select(source =>
        {
            var copy = MxuJson.Clone(source);
            if (ontoSource && frameOf is not null)
            {
                var frame = frameOf(source);
                (copy.X, copy.Y, copy.Width, copy.Height) = (frame.X + offset, frame.Y + offset, frame.Width, frame.Height);
            }
            copy.Id = ids[source.Id];
            if (copy.GroupId is { Length: > 0 } group)
            {
                if (!groups.TryGetValue(group, out var fresh)) groups[group] = fresh = newId();
                copy.GroupId = fresh;
            }
            // Windows: a mask pasted with its matte keeps it (the Mac's object paste leaves the old id).
            if (copy.MaskObjectId is { } mask && ids.TryGetValue(mask, out var matte)) copy.MaskObjectId = matte;
            return copy;
        }).ToList();
        slide.Objects.AddRange(pasted);
        return pasted;
    }

    /// <summary>
    /// The objects to copy for a selection: everything selected, in the slide's order (bottom
    /// first, so a paste keeps their stacking).
    /// </summary>
    public static ObjectClip CopyObjects(Slide slide, IReadOnlyCollection<string> selected) =>
        new(slide.Id, [.. slide.Objects.Where(o => selected.Contains(o.Id)).Select(MxuJson.Clone)]);

    // MARK: Media

    /// <summary>A new slide showing a media item as its background, named after it (the Mac's Slide.droppedMedia).</summary>
    public static Slide MediaSlide(MediaItem item, string? sectionId, Func<string>? newId = null) => new()
    {
        Id = (newId ?? LibraryStore.NewId)(),
        Name = item.Name,
        Objects = [],
        Background = MediaCueRouting.DroppedAsNewSlide(item),
        SectionId = sectionId,
    };

    /// <summary>
    /// A picture or video box for a media item, centred, 960 wide (or less, to fit) in its own shape
    /// (+ Media on the canvas). <paramref name="at"/> centres it there instead, kept on the slide.
    /// </summary>
    public static SlideObject MediaObject(MediaItem item, SceneSize canvas, EditorGeometry.Point? at = null, Func<string>? newId = null)
    {
        var width = Math.Min(960.0, canvas.Width);
        var height = item is { PixelWidth: > 0 and var pw, PixelHeight: > 0 and var ph } ? width * ph / pw : width * 9 / 16;
        if (height > canvas.Height)
        {
            width *= canvas.Height / height;
            height = canvas.Height;
        }
        var centre = at ?? new EditorGeometry.Point(canvas.Width / 2, canvas.Height / 2);
        var x = Math.Clamp(centre.X - width / 2, 0, Math.Max(0, canvas.Width - width));
        var y = Math.Clamp(centre.Y - height / 2, 0, Math.Max(0, canvas.Height - height));
        return new SlideObject
        {
            Id = (newId ?? LibraryStore.NewId)(), ObjectKind = SlideObjectKind.Shape, Name = item.Name, Text = "",
            X = Math.Round(x), Y = Math.Round(y), Width = Math.Round(width), Height = Math.Round(height), ShapeKind = ShapeKind.Rectangle,
            Fill = new ObjectFill { FillKind = FillKind.Media, MediaId = item.Id, MediaScaleMode = MediaScaleMode.Fit, Loops = item.Loops },
        };
    }

    // MARK: Text from elsewhere

    /// <summary>Plain text pasted on a slide: a centred text box holding it (like + Text).</summary>
    public static SlideObject TextBox(string text, SceneSize canvas, Func<string>? newId = null)
    {
        var width = Math.Round(canvas.Width * 0.625);
        var height = Math.Round(canvas.Height * 0.185);
        return new SlideObject
        {
            Id = (newId ?? LibraryStore.NewId)(),
            ObjectKind = SlideObjectKind.Text,
            Name = "Text",
            Text = text.ReplaceLineEndings("\n").Trim(),
            X = Math.Round((canvas.Width - width) / 2), Y = Math.Round((canvas.Height - height) / 2), Width = width, Height = height,
            TextStyle = new TextStyle { FontSize = 72, HorizontalAlignment = TextHorizontalAlignment.Center, VerticalAlignment = TextVerticalAlignment.Middle, AutoShrink = true },
        };
    }

    /// <summary>
    /// Lyrics pasted into a song become slides after <paramref name="afterSlideId"/>, cut as the song's
    /// own slides are (the most lines any of its slides shows) and in its lyric design. A labelled
    /// part ("Chorus") joins the song's group of that name, or starts one; unlabelled words join the
    /// group of the slide they land after. Returns the new slides (none for blank text).
    /// </summary>
    public static IReadOnlyList<Slide> InsertLyrics(Presentation song, string text, string? afterSlideId, Func<string>? newId = null)
    {
        newId ??= LibraryStore.NewId;
        var normalized = LyricTextImporter.Normalize(text.ReplaceLineEndings("\n").Trim());
        if (normalized.Body.Trim().Length == 0) return [];
        var labelled = normalized.Body.Split('\n').Any(line => Reflow.LabelName(line.Trim(), normalized.ExtraLabels) is not null);
        var parsed = Reflow.Parse(normalized.Body, SongEditing.LinesPerSlide(song), normalized.ExtraLabels, normalized.Format != LyricTextFormat.PlainText);
        var built = Reflow.Build(parsed, Reflow.LyricDesign(song.Slides));

        var anchor = afterSlideId is null ? song.Slides.Count - 1 : song.Slides.FindIndex(s => s.Id == afterSlideId);
        if (anchor < 0) anchor = song.Slides.Count - 1;
        var neighbourSection = anchor >= 0 && anchor < song.Slides.Count ? song.Slides[anchor].SectionId : null;
        var sections = song.Sections ??= [];
        var sectionFor = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var made in built.Sections)
        {
            if (!labelled)
            {
                sectionFor[made.Id] = neighbourSection;
                continue;
            }
            var existing = sections.FirstOrDefault(s => string.Equals(s.Name, made.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                existing = new PresentationSection { Id = newId(), Name = made.Name };
                sections.Add(existing);
            }
            sectionFor[made.Id] = existing.Id;
        }
        if (sections.Count == 0) song.Sections = null;
        var slides = built.Slides.Select(slide =>
        {
            slide.Id = newId();
            foreach (var o in slide.Objects) o.Id = newId();
            slide.SectionId = slide.SectionId is { } id ? sectionFor.GetValueOrDefault(id) : neighbourSection;
            return slide;
        }).ToList();
        song.Slides.InsertRange(Math.Min(anchor + 1, song.Slides.Count), slides);
        return slides;
    }
}
