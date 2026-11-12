using System.Text.RegularExpressions;
using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Notes;

/// <summary>
/// A note's slide cards (blocks of type slideRef that show a slide inside the note) and its
/// "[slide]" markers, which say "a slide goes here". Port of NoteSlides.swift.
/// </summary>
public static partial class NoteSlides
{
    /// <summary>What a card points at, found in the library.</summary>
    public abstract record Resolution
    {
        public sealed record Found(Presentation Presentation, Slide Slide, int Index) : Resolution;

        public sealed record MissingPresentation : Resolution;

        public sealed record MissingSlide(Presentation Presentation) : Resolution;

        /// <summary>A card from another app's notes (ProPresenter), which points at nothing here.</summary>
        public sealed record NotPresenter : Resolution;
    }

    /// <summary>The slide a card shows: by its id, or by its place in the deck when the id is gone.</summary>
    public static Resolution Resolve(NoteSlideRef reference, Func<string, Presentation?> lookup)
    {
        if (reference.PresentationId is not { } presentationId) return new Resolution.NotPresenter();
        if (lookup(presentationId) is not { } presentation) return new Resolution.MissingPresentation();
        if (reference.SlideId is { } slideId && presentation.Slides.FindIndex(s => s.Id == slideId) is var byId and >= 0)
            return new Resolution.Found(presentation, presentation.Slides[byId], byId);
        if (reference.SlideIndex is { } index && index >= 0 && index < presentation.Slides.Count)
            return new Resolution.Found(presentation, presentation.Slides[index], index);
        return new Resolution.MissingSlide(presentation);
    }

    /// <summary>The card for the live slide, so the note can scroll to it.</summary>
    public static int? BlockIndexOfLiveSlide(string slideId, NoteBody body)
    {
        var index = body.Blocks.FindIndex(b => b.Type == NoteBlockType.SlideRef && b.Slide?.SlideId == slideId);
        return index >= 0 ? index : null;
    }

    /// <summary>A card for a slide dropped into the note: its deck, words and number, shown as a picture.</summary>
    public static NoteSlideRef? DropRef(string presentationId, string slideId, Func<string, Presentation?> lookup)
    {
        if (lookup(presentationId) is not { } presentation) return null;
        var index = presentation.Slides.FindIndex(s => s.Id == slideId);
        if (index < 0) return null;
        var slide = presentation.Slides[index];
        var text = string.Join("\n", slide.Objects.Select(o => o.Text).Where(t => t.Length > 0));
        return new NoteSlideRef
        {
            PresentationId = presentationId, SlideId = slideId, SlideIndex = index,
            PresentationName = presentation.Name,
            SlideLabel = slide.Name.Length == 0 ? null : slide.Name,
            SlideText = text.Length == 0 ? null : text,
            ShowThumbnail = true, ShowText = false, ShowNotes = true, ShowLabels = true, ShowName = false, ShowNumber = true,
        };
    }

    /// <summary>A card someone placed by hand, which the note keeps when it remakes its slides.</summary>
    public static NoteSlideRef? HandRef(string presentationId, string slideId, Func<string, Presentation?> lookup)
    {
        if (DropRef(presentationId, slideId, lookup) is not { } reference) return null;
        reference.PlacedByHand = true;
        return reference;
    }

    // Markers: "[slide]" on a line of its own, or "Slide 3:", "Slides #4 -", "Slide (video)". A line
    // that starts "Slide 2: Grace upon grace" is a marker and the slide's words at once.

    public const string DefaultMarker = "[slide]";

    private static readonly Regex InlineLabelPattern = new(@"^\s*slides?(\s*#?\s*\d+)?\s*:\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex LabelPattern = new(@"^slides?(\s*#?\s*\d+)?\s*[:.\-–—]?(\s*\([^)]*\))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static List<int> MarkerBlockIndexes(NoteBody body, string marker = DefaultMarker) =>
        Enumerable.Range(0, body.Blocks.Count).Where(i => IsMarker(body.Blocks[i], marker)).ToList();

    /// <summary>A paragraph that is only a marker: "[slide]", or, for the default marker, "Slide 4:" and its kin.</summary>
    public static bool IsMarker(NoteBlock block, string marker)
    {
        if (block.Type != NoteBlockType.Paragraph) return false;
        var text = NoteReading.Text(block.Runs).TrimmingWhitespacesAndNewlines();
        if (string.Equals(text, marker, StringComparison.OrdinalIgnoreCase)) return true;
        return marker == DefaultMarker && LabelPattern.IsMatch(text);
    }

    /// <summary>How long "Slide 2: " is at the start of a paragraph that carries its slide's words after it.</summary>
    public static int? InlineMarkerLabelLength(NoteBlock block) =>
        block.Type == NoteBlockType.Paragraph ? InlineMarkerLabelLength(NoteReading.Text(block.Runs)) : null;

    public static int? InlineMarkerLabelLength(string text)
    {
        var label = InlineLabelPattern.Match(text);
        if (!label.Success) return null;
        var rest = text[(label.Index + label.Length)..].TrimmingWhitespacesAndNewlines();
        if (rest.Length == 0 || (rest.StartsWith('(') && rest.EndsWith(')'))) return null;
        return label.Index + label.Length;
    }

    /// <summary>A block that starts a slide: a marker line, or a "Slide 2: …" line.</summary>
    public static bool StartsSlide(NoteBlock block) => IsMarker(block, DefaultMarker) || InlineMarkerLabelLength(block) is not null;

    /// <summary>A deck's slides put in place of the note's markers, one each, in order.</summary>
    public static NoteBody PlaceSlides(NoteBody body, Presentation presentation, string marker = DefaultMarker)
    {
        var slideIndex = 0;
        var blocks = body.Blocks.Select(b => b.Copy()).ToList();
        for (var index = 0; index < blocks.Count; index++)
        {
            if (!IsMarker(blocks[index], marker)) continue;
            if (slideIndex >= presentation.Slides.Count) break;
            var slide = presentation.Slides[slideIndex];
            if (DropRef(presentation.Id, slide.Id, _ => presentation) is { } reference)
                blocks[index] = new NoteBlock { Type = NoteBlockType.SlideRef, Slide = reference };
            slideIndex++;
        }
        return new NoteBody { Blocks = blocks, PlainText = NoteReading.PlainText(blocks) };
    }
}
