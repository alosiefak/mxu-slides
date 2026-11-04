using System.Text.Json.Serialization;

namespace MxU.Slides.Core.Model;

// The note documents (NoteDocument, NoteBody, NoteBlock, NoteRun…) are generated from the shared
// schema, so they're classes. The Mac's are Swift structs: copying one makes a new value, and two
// with the same contents are equal. The note logic leans on both all the time ("var block = block;
// block.runs = …" changes a copy, never the caller's), so these partials give the C# twins the
// same habits: Copy() makes an independent copy, and Equals compares contents.

public sealed partial class NoteDocument : ILibraryDocument;

public sealed partial class SlideBuildingSettings : ILibraryDocument
{
    /// <summary>The one document's id (SlideBuildingSettings.wellKnownID on the Mac).</summary>
    public const string WellKnownId = "slide-building-settings";

    string ILibraryDocument.Name { get => "Slide building"; set { } }
}

public sealed partial class NoteRun : IEquatable<NoteRun>
{
    /// <summary>A copy: a run holds only text and flags, so a shallow copy is a whole one.</summary>
    public NoteRun Copy() => (NoteRun)MemberwiseClone();

    /// <summary>The same run with different text, as Swift's "var piece = run; piece.text = …".</summary>
    public NoteRun WithText(string text)
    {
        var copy = Copy();
        copy.Text = text;
        return copy;
    }

    /// <summary>The same marks (bold, link, slide group…), whatever the words.</summary>
    public bool SameMarks(NoteRun other) =>
        Bold == other.Bold && Italic == other.Italic && Underline == other.Underline && Strike == other.Strike
        && Code == other.Code && Highlight == other.Highlight && Color == other.Color && Size == other.Size
        && Href == other.Href && SlideGroup == other.SlideGroup && SlideType == other.SlideType
        && SlideId == other.SlideId && SlideState == other.SlideState && CommentId == other.CommentId;

    public bool Equals(NoteRun? other) => other is not null && Text == other.Text && SameMarks(other);

    public override bool Equals(object? obj) => Equals(obj as NoteRun);

    public override int GetHashCode() => HashCode.Combine(Text, SlideGroup, SlideType, SlideId, Bold, Href);
}

public sealed partial class NoteListItem : IEquatable<NoteListItem>
{
    public NoteListItem Copy() => new()
    {
        Runs = Runs.Select(r => r.Copy()).ToList(),
        Checked = Checked,
        Blocks = Blocks?.Select(b => b.Copy()).ToList(),
    };

    public bool Equals(NoteListItem? other) =>
        other is not null && Checked == other.Checked && Runs.SequenceEqual(other.Runs) && NoteEquality.Same(Blocks, other.Blocks);

    public override bool Equals(object? obj) => Equals(obj as NoteListItem);

    public override int GetHashCode() => HashCode.Combine(Runs.Count, Checked);
}

public sealed partial class NoteSlideRef : IEquatable<NoteSlideRef>
{
    /// <summary>A copy: a card holds only ids, words and flags.</summary>
    public NoteSlideRef Copy() => (NoteSlideRef)MemberwiseClone();

    public bool Equals(NoteSlideRef? other) =>
        other is not null && PresentationId == other.PresentationId && SlideId == other.SlideId && SlideIndex == other.SlideIndex
        && PpIntegrationHexId == other.PpIntegrationHexId && PpPresentationUuid == other.PpPresentationUuid
        && PpPlaylistUuid == other.PpPlaylistUuid && PpItemIndex == other.PpItemIndex && PpSlideIndex == other.PpSlideIndex
        && PresentationName == other.PresentationName && SlideText == other.SlideText && SlideLabel == other.SlideLabel
        && SlideLabelColor == other.SlideLabelColor && SlideNotes == other.SlideNotes && ThumbnailUrl == other.ThumbnailUrl
        && ShowThumbnail == other.ShowThumbnail && ShowText == other.ShowText && ShowNotes == other.ShowNotes
        && ShowLabels == other.ShowLabels && ShowName == other.ShowName && ShowNumber == other.ShowNumber
        && Width == other.Width && PlacedByHand == other.PlacedByHand;

    public override bool Equals(object? obj) => Equals(obj as NoteSlideRef);

    public override int GetHashCode() => HashCode.Combine(PresentationId, SlideId, SlideIndex);
}

public sealed partial class NoteBlock : IEquatable<NoteBlock>
{
    /// <summary>
    /// A picture's or attachment's own slide mark, as a run with no words, so the code that reads
    /// and writes marks can treat a picture like a line of text (the Mac's NoteBlock.markRun).
    /// Null for any other block, and setting it on one does nothing.
    /// </summary>
    [JsonIgnore]
    public NoteRun? MarkRun
    {
        get => Type is NoteBlockType.Image or NoteBlockType.Attachment
            ? new NoteRun { Text = "", SlideGroup = SlideGroup, SlideType = SlideType, SlideId = SlideId, SlideState = SlideState }
            : null;
        set
        {
            if (Type is not (NoteBlockType.Image or NoteBlockType.Attachment)) return;
            SlideGroup = value?.SlideGroup;
            SlideType = value?.SlideType;
            SlideId = value?.SlideId;
            SlideState = value?.SlideState;
        }
    }

    /// <summary>An independent copy of the block, its runs, items and inner blocks.</summary>
    public NoteBlock Copy()
    {
        var copy = (NoteBlock)MemberwiseClone();
        copy.Runs = Runs?.Select(r => r.Copy()).ToList();
        copy.Items = Items?.Select(i => i.Copy()).ToList();
        copy.Blocks = Blocks?.Select(b => b.Copy()).ToList();
        copy.Slide = Slide?.Copy();
        return copy;
    }

    public bool Equals(NoteBlock? other) =>
        other is not null && Type == other.Type && Level == other.Level && Ordered == other.Ordered
        && Language == other.Language && Text == other.Text && Url == other.Url && Width == other.Width && Alt == other.Alt
        && AttachmentKind == other.AttachmentKind && HexId == other.HexId && Filename == other.Filename
        && SlideGroup == other.SlideGroup && SlideType == other.SlideType && SlideId == other.SlideId && SlideState == other.SlideState
        && NoteEquality.Same(Runs, other.Runs) && NoteEquality.Same(Items, other.Items) && NoteEquality.Same(Blocks, other.Blocks)
        && Equals(Slide, other.Slide);

    public override bool Equals(object? obj) => Equals(obj as NoteBlock);

    public override int GetHashCode() => HashCode.Combine(Type, Runs?.Count, Items?.Count, Blocks?.Count, Url);
}

public sealed partial class NoteBody : IEquatable<NoteBody>
{
    public NoteBody Copy() => new() { Blocks = Blocks.Select(b => b.Copy()).ToList(), PlainText = PlainText };

    public bool Equals(NoteBody? other) => other is not null && PlainText == other.PlainText && Blocks.SequenceEqual(other.Blocks);

    public override bool Equals(object? obj) => Equals(obj as NoteBody);

    public override int GetHashCode() => HashCode.Combine(Blocks.Count, PlainText);
}

internal static class NoteEquality
{
    /// <summary>Two optional lists hold the same things (both missing counts as the same).</summary>
    public static bool Same<T>(List<T>? a, List<T>? b) =>
        a is null || b is null ? a is null && b is null : a.SequenceEqual(b);
}
