using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Automation;

/// <summary>What a row of the timeline editor holds.</summary>
public enum TimelineRowKind
{
    /// <summary>One of the song's lanes (slides, music, overlays…).</summary>
    Lane,

    /// <summary>The tracks header row: every stem folded into one bar, or the row to drop a song's stems on.</summary>
    Tracks,

    /// <summary>One track channel's stems (click, keys, bass…).</summary>
    Track,
}

/// <summary>A row of the editor, top to bottom, in points from the top of the lanes area.</summary>
public sealed record TimelineRow(TimelineRowKind Kind, TimelineLane? Lane, string? ChannelId, double Top, double Height)
{
    public double Bottom => Top + Height;

    /// <summary>Music clips and stems can carry fades and waveforms.</summary>
    public bool HoldsAudio => Lane == TimelineLane.Audio || Kind != TimelineRowKind.Lane;
}

/// <summary>
/// One cue as the editor draws it on a row. <see cref="X"/> is where its time is; a pin's
/// card starts half a pin before it. <see cref="Title"/> replaces the cue's own label (the
/// folded bar says "12 stems"); <see cref="Group"/> is whose sound its waveform shows.
/// </summary>
public sealed record TimelineCard(TimelineCue Cue, TimelineRow Row, double X, double Width, bool IsPin, string? Title = null,
    IReadOnlyList<TimelineCue>? Group = null)
{
    /// <summary>The card's left edge: a pin's starts half a pin before its time.</summary>
    public double Left => IsPin ? X - TimelineEditing.PinSize / 2 : X;

    /// <summary>A block card stands 2 points in from its row's top and bottom; a pin fills the row.</summary>
    public double Top => IsPin ? Row.Top : Row.Top + 2;

    public double Height => IsPin ? Row.Height : Math.Max(Row.Height - 4, 1);

    public double Right => Left + Width;
}

/// <summary>What a press on a card grabbed.</summary>
public sealed record TimelineCardHit(TimelineCard Card, TimelineBlockEdit.Mode Mode);

/// <summary>
/// The editor's rows and cards, and hit-testing: which row and card are under the pointer
/// and which edge of a card it grabbed. The WPF lanes view draws exactly these rectangles,
/// so what you see is what you can click. Port of the layout in PresentationTimelineLanes
/// (Sources/PresentationTimelineView.swift), where SwiftUI did the hit-testing.
/// </summary>
public sealed class TimelineEditorLayout
{
    /// <summary>The ruler across the top (click or drag it to move the playhead).</summary>
    public const double RulerHeight = 16;

    /// <summary>The thin coloured bar of the song's sections under the ruler.</summary>
    public const double SectionsHeight = 18;

    /// <summary>Rows are a point apart.</summary>
    public const double RowGap = 1;

    /// <summary>Every lane but music is this tall; music and the tracks follow the row height control.</summary>
    public const double DefaultRowHeight = 26;

    /// <summary>The row height control's range.</summary>
    public static readonly ClosedRange RowHeights = new(20, 96);

    /// <summary>A press this close outside a card still grabs it.</summary>
    public const double HitSlop = 4;

    /// <summary>Room under the last row (for the "add a layer" button beside it).</summary>
    public const double BottomRoom = 28;

    public TimelineEditorLayout(IReadOnlyList<TimelineRow> rows, IReadOnlyList<TimelineCard> cards, TimelineMap map)
    {
        Rows = rows;
        Cards = cards;
        Map = map;
    }

    public IReadOnlyList<TimelineRow> Rows { get; }

    /// <summary>Every card, in drawing order (a later card is drawn over an earlier one).</summary>
    public IReadOnlyList<TimelineCard> Cards { get; }

    public TimelineMap Map { get; }

    /// <summary>Where the first row starts: under the ruler and the sections bar.</summary>
    public static double LanesTop => RulerHeight + RowGap + SectionsHeight + RowGap;

    /// <summary>The whole height of the lanes area, rows and room below included.</summary>
    public double Height => (Rows.Count > 0 ? Rows[^1].Bottom : LanesTop) + BottomRoom;

    /// <summary>A track row's height: the row height times its own scale (dragged), 20 to 192 points.</summary>
    public static double TrackHeight(double rowHeight, double? scale) =>
        Math.Min(Math.Max(rowHeight * (scale ?? 1), RowHeights.Lower), RowHeights.Upper * 2);

    /// <summary>
    /// The rows for these lanes: each lane (music at the row height, the rest at 26 points),
    /// then, when the song shows tracks, the tracks header and (unfolded) a row per channel.
    /// </summary>
    public static List<TimelineRow> RowsFor(
        IReadOnlyList<TimelineLane> lanes, double rowHeight, bool showsTracks, bool expanded, IReadOnlyList<string> channels,
        Func<string, double>? trackHeight = null)
    {
        var rows = new List<TimelineRow>();
        var top = LanesTop;
        void Add(TimelineRowKind kind, TimelineLane? lane, string? channel, double height)
        {
            rows.Add(new TimelineRow(kind, lane, channel, top, height));
            top += height + RowGap;
        }
        foreach (var lane in lanes) Add(TimelineRowKind.Lane, lane, null, lane == TimelineLane.Audio ? rowHeight : DefaultRowHeight);
        if (showsTracks)
        {
            Add(TimelineRowKind.Tracks, null, null, rowHeight);
            if (expanded)
                foreach (var channel in channels) Add(TimelineRowKind.Track, null, channel, trackHeight?.Invoke(channel) ?? rowHeight);
        }
        return rows;
    }

    /// <summary>
    /// Lays out the cards: each lane's cues on its row; with the tracks folded, one bar for
    /// all the stems ("12 stems"); unfolded, each stem on its channel's row.
    /// <paramref name="musicStems"/> picks the stems whose sound the folded bar's waveform shows.
    /// </summary>
    public static TimelineEditorLayout Build(
        IReadOnlyList<TimelineRow> rows, IReadOnlyList<TimelineCue> cues, TimelineMap map, bool expanded,
        Func<IReadOnlyList<TimelineCue>, IReadOnlyList<TimelineCue>>? musicStems = null)
    {
        var cards = new List<TimelineCard>();
        var stems = TimelineTracks.Stems(cues);
        foreach (var row in rows)
        {
            switch (row.Kind)
            {
                case TimelineRowKind.Lane:
                    foreach (var cue in cues.Where(c => c.Kind() == row.Lane)) cards.Add(CardFor(cue, cues, row, map));
                    break;
                case TimelineRowKind.Tracks when !expanded:
                    if (TimelineTracks.Folded(stems, cues) is { } folded)
                    {
                        var (x, width) = TimelineEditing.Card(folded, cues, map);
                        cards.Add(new TimelineCard(folded, row, x, Math.Max(width, TimelineBlockEdit.MinimumWidth), false,
                            stems.Count == 1 ? null : $"{stems.Count} stems", musicStems?.Invoke(stems) ?? stems));
                    }
                    break;
                case TimelineRowKind.Track:
                    foreach (var stem in stems.Where(s => s.TrackChannelId() == row.ChannelId)) cards.Add(CardFor(stem, cues, row, map));
                    break;
            }
        }
        return new TimelineEditorLayout(rows, cards, map);
    }

    private static TimelineCard CardFor(TimelineCue cue, IReadOnlyList<TimelineCue> cues, TimelineRow row, TimelineMap map)
    {
        // Every card is at least 26 points wide, so even a short one can be grabbed (as on the Mac).
        var (x, width) = TimelineEditing.Card(cue, cues, map);
        return new TimelineCard(cue, row, x, Math.Max(width, TimelineBlockEdit.MinimumWidth), !cue.IsBlock());
    }

    /// <summary>True in the ruler strip along the top.</summary>
    public static bool InRuler(double y) => y >= 0 && y < RulerHeight;

    /// <summary>The row under a y position (gaps count with the row above), or null above the rows or below the last.</summary>
    public TimelineRow? RowAt(double y)
    {
        foreach (var row in Rows)
            if (y >= row.Top && y < row.Bottom + RowGap) return row;
        return null;
    }

    /// <summary>
    /// The card under a point and what the press grabbed: the right edge stretches, the left
    /// edge trims (on a wide enough block), the middle moves; a pin always moves. The card
    /// drawn last wins where cards overlap.
    /// </summary>
    public TimelineCardHit? CardAt(double x, double y)
    {
        for (var index = Cards.Count - 1; index >= 0; index--)
        {
            var card = Cards[index];
            if (x < card.Left - HitSlop || x > card.Right + HitSlop || y < card.Top - HitSlop || y > card.Top + card.Height + HitSlop) continue;
            if (y < card.Row.Top - HitSlop || y > card.Row.Bottom + HitSlop) continue;
            var mode = card.IsPin ? TimelineBlockEdit.Mode.Move : TimelineBlockEdit.ModeFor(x, card.Left, card.Width);
            return new TimelineCardHit(card, mode);
        }
        return null;
    }

    /// <summary>The card for a cue id, if it is on a row.</summary>
    public TimelineCard? CardFor(string cueId) => Cards.FirstOrDefault(c => c.Cue.Id == cueId);

    /// <summary>The time under an x (clamped to the song's drawn length; never before zero).</summary>
    public double TimeAt(double x) => Map.Locate(Math.Max(x, 0))?.Time ?? 0;
}

/// <summary>
/// One press on a card, from mouse down to mouse up: it turns into a drag once the pointer
/// has moved three points; the edge grabbed sets whether it moves, stretches or trims; the
/// live distance snaps. Port of the TimelineBlockGestures modifier in TimelineControls.swift.
/// </summary>
public sealed class TimelineCardDrag
{
    public TimelineCardDrag(TimelineCardHit hit, double pressX)
    {
        Card = hit.Card;
        Grabbed = hit.Mode;
        PressX = pressX;
    }

    public TimelineCard Card { get; }

    /// <summary>What the press would do once it moves.</summary>
    public TimelineBlockEdit.Mode Grabbed { get; }

    public double PressX { get; }

    /// <summary>Null until the pointer has moved far enough to make it a drag.</summary>
    public TimelineBlockEdit.Mode? Mode { get; private set; }

    /// <summary>How far the card has been dragged, in points, after snapping.</summary>
    public double Delta { get; private set; }

    public bool IsDragging => Mode is not null;

    /// <summary>
    /// The pointer moved to <paramref name="x"/>. Returns true on the move that turned the
    /// press into a drag (the editor then selects the card if it wasn't selected).
    /// <paramref name="snap"/> adjusts the distance; pass null to drag freely.
    /// </summary>
    public bool MovedTo(double x, Func<TimelineBlockEdit.Mode, double, double>? snap)
    {
        var dx = x - PressX;
        var engaging = false;
        if (Mode is null)
        {
            if (Math.Abs(dx) <= TimelineBlockEdit.EngageDistance) return false;
            Mode = Grabbed;
            engaging = true;
        }
        Delta = snap?.Invoke(Mode.Value, dx) ?? dx;
        return engaging;
    }

    /// <summary>The press let go at <paramref name="x"/>: what it did, and how far (snapped). A click has no mode.</summary>
    public (TimelineBlockEdit.Mode? Mode, double Delta) Released(double x, Func<TimelineBlockEdit.Mode, double, double>? snap)
    {
        if (Mode is not { } mode) return (null, 0);
        var dx = x - PressX;
        return (mode, snap?.Invoke(mode, dx) ?? dx);
    }

    /// <summary>Where a card is drawn while this drag moves it (other selected cards move along when it moves).</summary>
    public (double Left, double Width) LiveSpan(TimelineCard card, bool movesWithIt)
    {
        var mode = card.Cue.Id == Card.Cue.Id ? Mode : movesWithIt && Mode == TimelineBlockEdit.Mode.Move ? Mode : null;
        // A pin only ever moves (it has no length to stretch or trim).
        if (card.IsPin && mode is not null) mode = TimelineBlockEdit.Mode.Move;
        var (x, width) = TimelineBlockEdit.LiveSpan(card.X, card.Width, mode, mode is null ? 0 : Delta);
        return (card.IsPin ? x - TimelineEditing.PinSize / 2 : x, width);
    }
}
