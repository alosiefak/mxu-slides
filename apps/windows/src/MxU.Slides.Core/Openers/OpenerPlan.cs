using System.Globalization;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Motion;

namespace MxU.Slides.Core.Openers;

/// <summary>What the montage needs to know about its music: how long it is and where its beats fall (none when there's no clear beat).</summary>
public sealed record OpenerMusic(double Seconds, IReadOnlyList<double> Beats);

/// <summary>
/// One shot of a montage: which picture or clip (an index into the recipe's
/// <see cref="OpenerRecipe.Shots"/>), from when to when (the cuts; each dissolve straddles its
/// cut), and which gentle move it makes.
/// </summary>
public sealed record PlannedShot(int Source, double Start, double End, int Move);

/// <summary>
/// An opener laid out in time: how long it is, where each montage shot falls, and
/// (<see cref="FrameAt"/>) exactly what is on screen at any moment. Everything here is a pure
/// function of the recipe, the music's beats and the time, like the animation engine
/// (HOW-IT-WORKS section 14) and the motion backgrounds: frame <i>i</i> is drawn at
/// <i>i</i> ÷ frame rate whether the PC is fast or slow, the same recipe always makes the same
/// video, and the preview shows exactly what the render will make.
/// </summary>
public sealed class OpenerPlan
{
    /// <summary>How long the motion background takes to come round, behind everything but an announcement loop (which fits a whole number of turns into its own length).</summary>
    public const double BackgroundLoopSeconds = 20;

    /// <summary>How long the Welcome card after a countdown is up.</summary>
    public const double WelcomeSeconds = 5;

    /// <summary>How long a montage's opening and closing cards are up.</summary>
    public const double MontageCardSeconds = 3.5;

    /// <summary>How long a montage shot lasts when nothing else decides it.</summary>
    public const double PreferredShotSeconds = 4;

    private readonly double openCard;
    private readonly double endCard;
    private readonly double backgroundTurn;

    private OpenerPlan(OpenerRecipe recipe, double seconds, IReadOnlyList<PlannedShot> shots, bool onBeats, double openCard, double endCard)
    {
        Recipe = recipe;
        Look = OpenerLooks.Find(recipe.Look)!;
        FrameCount = Math.Max(1, (int)Math.Round(seconds * recipe.FrameRate));
        Seconds = FrameCount / (double)recipe.FrameRate;
        Shots = shots;
        OnBeats = onBeats;
        this.openCard = openCard;
        this.endCard = endCard;
        Dissolve = shots.Count == 0 ? 0 : Math.Clamp(shots.Min(s => s.End - s.Start) * 0.4, 0.1, 0.6);
        // A loop's background must be back where it began when the video ends, so it makes a whole
        // number of turns; anything else just keeps the background's own 20-second turn.
        backgroundTurn = recipe.Loops ? Seconds / Math.Max(1, Math.Round(Seconds / BackgroundLoopSeconds)) : BackgroundLoopSeconds;
    }

    /// <summary>The recipe, with everything in range.</summary>
    public OpenerRecipe Recipe { get; }

    public OpenerLook Look { get; }

    /// <summary>The video's length: a whole number of frames.</summary>
    public double Seconds { get; }

    public int FrameCount { get; }

    /// <summary>A montage's shots, in order (empty for the other kinds).</summary>
    public IReadOnlyList<PlannedShot> Shots { get; }

    /// <summary>True when every montage cut landed on a beat of the music.</summary>
    public bool OnBeats { get; }

    /// <summary>How long each montage dissolve takes.</summary>
    public double Dissolve { get; }

    /// <summary>A moment that shows the opener at its best, for a still picture of it: the words fully in.</summary>
    public double HeroSeconds => Recipe.Kind switch
    {
        OpenerKind.Bumper => Seconds / 2,
        OpenerKind.Countdown => Math.Min(3, Recipe.Seconds / 2),
        OpenerKind.Montage => openCard > 0 ? openCard * 0.55 : Shots.Count > 0 ? (Shots[0].Start + Shots[0].End) / 2 : Seconds / 2,
        _ => Recipe.CardSeconds * 0.4,
    };

    /// <summary>The motion background's recipe (its loop length is this plan's turn).</summary>
    public MotionRecipe Background => Recipe.BackgroundRecipe(backgroundTurn);

    /// <summary>Lays <paramref name="recipe"/> out in time. <paramref name="music"/> is the montage's track, analysed (null without music).</summary>
    public static OpenerPlan For(OpenerRecipe recipe, OpenerMusic? music = null)
    {
        recipe = recipe.Normalized();
        switch (recipe.Kind)
        {
            case OpenerKind.Bumper:
                return new OpenerPlan(recipe, recipe.Seconds, [], false, 0, 0);
            case OpenerKind.Countdown:
                return new OpenerPlan(recipe, recipe.Seconds + (HasWelcome(recipe) ? WelcomeSeconds : 0), [], false, 0, 0);
            case OpenerKind.Announcements:
                return new OpenerPlan(recipe, Math.Max(1, recipe.Cards.Count) * recipe.CardSeconds, [], false, 0, 0);
        }

        var open = string.IsNullOrWhiteSpace(recipe.Title) ? 0 : MontageCardSeconds;
        var end = string.IsNullOrWhiteSpace(recipe.EndTitle) ? 0 : MontageCardSeconds;
        var count = recipe.Shots.Count;
        double total;
        if (music is { Seconds: > 0 })
            total = Math.Min(music.Seconds, recipe.Seconds > 0 ? recipe.Seconds : OpenerRecipe.MaxMontageSeconds);
        else
            total = recipe.Seconds > 0 ? recipe.Seconds : open + end + Math.Max(1, count) * PreferredShotSeconds;
        total = Math.Max(2, total);
        // The cards never take more than half of a short montage.
        if (open + end > total * 0.5)
        {
            var shrink = total * 0.5 / (open + end);
            open *= shrink;
            end *= shrink;
        }
        var shots = PlanShots(count, open, total - end, music?.Beats ?? [], out var onBeats);
        return new OpenerPlan(recipe, total, shots, onBeats, open, end);
    }

    private static bool HasWelcome(OpenerRecipe recipe) => !string.IsNullOrWhiteSpace(recipe.EndTitle);

    // MARK: The montage's shot plan

    /// <summary>
    /// Shares the time from <paramref name="start"/> to <paramref name="end"/> among
    /// <paramref name="sources"/> pictures:
    /// <list type="bullet">
    /// <item>every picture is used at least once; when there's time for more shots than pictures
    /// (a long song, a few photos) they come round again in order;</item>
    /// <item>shots aim for <paramref name="preferred"/> seconds, and are shorter when there are
    /// too many pictures for that;</item>
    /// <item>each cut moves to the nearest beat that keeps both its neighbours at least half the
    /// even length. Without beats (no music, or music with no clear beat) the cuts stay even.</item>
    /// </list>
    /// The first shot starts at <paramref name="start"/> and the last ends at
    /// <paramref name="end"/>, so the plan always fills the time exactly.
    /// </summary>
    public static IReadOnlyList<PlannedShot> PlanShots(int sources, double start, double end, IReadOnlyList<double> beats, out bool onBeats,
        double preferred = PreferredShotSeconds)
    {
        onBeats = false;
        var window = end - start;
        if (sources <= 0 || window <= 0) return [];
        var count = Math.Max(sources, (int)Math.Round(window / preferred));
        var even = window / count;
        var shortest = even * 0.5;
        var shots = new List<PlannedShot>(count);
        var from = start;
        var snapped = 0;
        for (var k = 1; k <= count; k++)
        {
            var to = end;
            if (k < count)
            {
                // Spread what's left evenly over the shots still to come, then look for a beat near that.
                var ideal = from + (end - from) / (count - k + 1);
                var earliest = from + shortest;
                var latest = end - (count - k) * shortest;
                to = Math.Clamp(ideal, earliest, latest);
                double? best = null;
                foreach (var beat in beats)
                {
                    if (beat < earliest || beat > latest) continue;
                    if (best is null || Math.Abs(beat - ideal) < Math.Abs(best.Value - ideal)) best = beat;
                }
                if (best is { } onBeat)
                {
                    to = onBeat;
                    snapped++;
                }
            }
            shots.Add(new PlannedShot((k - 1) % sources, from, to, (k - 1) % 4));
            from = to;
        }
        onBeats = count > 1 && snapped == count - 1;
        return shots;
    }

    // MARK: The countdown's clock

    /// <summary>
    /// The clock for <paramref name="remainingSeconds"/> left: "5:00", "0:59", "10:00". It rounds
    /// up, as a kitchen timer does: "0:01" shows for the whole last second and "5:00" for the
    /// first, so the count reaches zero exactly when the time is up.
    /// </summary>
    public static string CountdownText(double remainingSeconds)
    {
        var whole = (int)Math.Max(0, Math.Ceiling(remainingSeconds - 1e-6));
        return string.Create(CultureInfo.InvariantCulture, $"{whole / 60}:{whole % 60:00}");
    }

    /// <summary>What a countdown's clock reads <paramref name="seconds"/> into the video ("0:00" once it has run out).</summary>
    public string CountdownTextAt(double seconds) => CountdownText(Recipe.Seconds - seconds);

    // MARK: Any moment

    /// <summary>Frame <paramref name="index"/> of the video. For a loop any number works: frame <see cref="FrameCount"/> is frame 0 again.</summary>
    public OpenerFrame Frame(long index)
    {
        if (Recipe.Loops) index = ((index % FrameCount) + FrameCount) % FrameCount;
        return FrameAt(index / (double)Recipe.FrameRate);
    }

    /// <summary>What's on screen <paramref name="seconds"/> in.</summary>
    public OpenerFrame FrameAt(double seconds)
    {
        var t = Recipe.Loops ? seconds - Math.Floor(seconds / Seconds) * Seconds : Math.Clamp(seconds, 0, Seconds);
        var phase = t / backgroundTurn;
        phase -= Math.Floor(phase);
        return Recipe.Kind switch
        {
            OpenerKind.Bumper => Bumper(t, phase),
            OpenerKind.Countdown => Countdown(t, phase),
            OpenerKind.Montage => Montage(t, phase),
            _ => Announcements(t, phase),
        };
    }

    /// <summary>How loud the montage's music is at <paramref name="seconds"/> (0–1): it fades out over the last second and a half.</summary>
    public double MusicGain(double seconds) => Clamp01((Seconds - seconds) / 1.5);

    private double EndsFade(double t, double fadeOut = 0.5) => Math.Max(1 - Clamp01(t / 0.5), Clamp01((t - (Seconds - fadeOut)) / fadeOut));

    private OpenerFrame Bumper(double t, double phase)
    {
        var card = Card(TitleLines(Recipe.Title, Recipe.Subtitle), Recipe.Picture, (t - 0.5) / 1.2, (t - (Seconds - 1.7)) / 1.0);
        return new OpenerFrame(phase, true, [], [card], EndsFade(t));
    }

    private OpenerFrame Countdown(double t, double phase)
    {
        var count = Recipe.Seconds;
        var cards = new List<OpenerCard>(1);
        if (t < count)
        {
            var lines = new List<(OpenerRole, string)>(3);
            if (!string.IsNullOrWhiteSpace(Recipe.Title)) lines.Add((OpenerRole.Kicker, Recipe.Title.Trim()));
            lines.Add((OpenerRole.Digits, CountdownText(count - t)));
            if (!string.IsNullOrWhiteSpace(Recipe.Subtitle)) lines.Add((OpenerRole.Subtitle, Recipe.Subtitle.Trim()));
            // With a Welcome card to follow, the clock leaves in the last third of a second; otherwise the fade to black takes it.
            cards.Add(Card(lines, null, (t - 0.3) / 1.0, HasWelcome(Recipe) ? (t - (count - 0.35)) / 0.35 : 0));
        }
        else
        {
            cards.Add(Card(TitleLines(Recipe.EndTitle, Recipe.Subtitle), null, (t - count) / 1.0, (t - (Seconds - 1.5)) / 1.0));
        }
        return new OpenerFrame(phase, true, [], cards, EndsFade(t));
    }

    private OpenerFrame Announcements(double t, double phase)
    {
        var cards = Recipe.Cards.Count > 0 ? Recipe.Cards : [new AnnouncementCard(Recipe.Title, Recipe.Subtitle)];
        double slot = Recipe.CardSeconds;
        var index = Math.Min(cards.Count - 1, (int)(t / slot));
        var into = t - index * slot;
        if (cards.Count == 1) return new OpenerFrame(phase, true, [], [Announcement(cards[0], 1, 0)], 0);

        // The change to the next card happens at the END of each card's time: the words leave, then
        // the next card's arrive. So when the video ends, the first card has just fully arrived,
        // which is exactly how frame 0 finds it: the loop has no seam.
        var change = Math.Min(1.6, slot * 0.3);
        var leaving = (into - (slot - change)) / (change / 2);
        var arriving = (into - (slot - change / 2)) / (change / 2);
        var shown = new List<OpenerCard>(2);
        if (leaving < 1) shown.Add(Announcement(cards[index], 1, leaving));
        if (arriving > 0) shown.Add(Announcement(cards[(index + 1) % cards.Count], arriving, 0));
        return new OpenerFrame(phase, true, [], shown, 0);
    }

    private OpenerCard Announcement(AnnouncementCard card, double enter, double exit)
    {
        var lines = new List<(OpenerRole, string)>(2);
        if (!string.IsNullOrWhiteSpace(card.Title)) lines.Add((OpenerRole.Title, card.Title.Trim()));
        if (!string.IsNullOrWhiteSpace(card.Body)) lines.Add((OpenerRole.Body, card.Body.Trim()));
        return Card(lines, card.Picture, enter, exit, beside: true);
    }

    private OpenerFrame Montage(double t, double phase)
    {
        var cards = new List<OpenerCard>(1);
        if (openCard > 0 && t < openCard)
            cards.Add(Card(TitleLines(Recipe.Title, Recipe.Subtitle), null, (t - 0.4) / 0.9, (t - (openCard - 0.9)) / 0.6));
        if (endCard > 0 && t > Seconds - endCard)
        {
            var into = t - (Seconds - endCard);
            cards.Add(Card(TitleLines(Recipe.EndTitle, ""), null, (into - 0.3) / 0.9, (into - (endCard - 1.3)) / 0.7));
        }

        var shots = new List<OpenerShot>(2);
        var covered = false;
        for (var k = 0; k < Shots.Count; k++)
        {
            var shot = Shots[k];
            var first = k == 0 && openCard <= 0;
            var last = k == Shots.Count - 1;
            // A shot is on screen from the start of the dissolve into it to the end of the dissolve out of it.
            var from = first ? shot.Start : shot.Start - Dissolve / 2;
            var to = last && endCard <= 0 ? shot.End : shot.End + Dissolve / 2;
            if (t < from || t >= to) continue;
            var opacity = first ? 1 : Smooth((t - from) / Dissolve);
            // Only the last shot fades away (into the closing card); the others are simply covered by the next.
            if (last && endCard > 0) opacity *= 1 - Smooth((t - (shot.End - Dissolve / 2)) / Dissolve);
            var path = Recipe.Shots[shot.Source];
            var along = (t - from) / (to - from);
            var (zoom, panX, panY) = LibraryStore.MediaKindOf(path) == MediaKind.Video ? (1, 0, 0) : Move(shot.Move, along);
            shots.Add(new OpenerShot(path, opacity, zoom, panX, panY, t - from));
            covered |= opacity >= 1;
        }
        return new OpenerFrame(phase, !covered, shots, cards, EndsFade(t, 0.8));
    }

    /// <summary>
    /// The gentle moves, taken in turn: push in, drift right, pull out, drift left. Never more
    /// than 8 % of zoom, so a face near the edge of a photo isn't lost.
    /// </summary>
    private static (double Zoom, double PanX, double PanY) Move(int move, double along) => move switch
    {
        0 => (1 + 0.08 * along, 0, 0),
        1 => (1.08, -1 + 2 * along, 0),
        2 => (1.08 - 0.08 * along, 0, 0),
        _ => (1.08, 1 - 2 * along, 0),
    };

    private static List<(OpenerRole, string)> TitleLines(string title, string subtitle)
    {
        var lines = new List<(OpenerRole, string)>(2);
        if (!string.IsNullOrWhiteSpace(title)) lines.Add((OpenerRole.Title, title.Trim()));
        if (!string.IsNullOrWhiteSpace(subtitle)) lines.Add((OpenerRole.Subtitle, subtitle.Trim()));
        return lines;
    }

    /// <summary>
    /// A block of lines <paramref name="enter"/> of the way through arriving and
    /// <paramref name="exit"/> through leaving (each 0–1; outside that range is "not yet" or
    /// "done"). Lines follow one another a quarter of the move apart, in the look's own way.
    /// </summary>
    private OpenerCard Card(IReadOnlyList<(OpenerRole Role, string Text)> lines, string? picture, double enter, double exit, bool beside = false)
    {
        const double stagger = 0.25;
        var span = 1 + stagger * Math.Max(0, lines.Count - 1);
        var result = new List<OpenerLine>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            var p = EaseOut(Clamp01(Clamp01(enter) * span - stagger * i));
            var q = EaseIn(Clamp01(Clamp01(exit) * span - stagger * i));
            double rise = 0, slide = 0, scale = 1;
            switch (Look.Entrance)
            {
                case OpenerEntrance.Rise:
                    rise = (1 - p) * 0.035 - q * 0.025;
                    break;
                case OpenerEntrance.Zoom:
                    scale = (1.14 - 0.14 * p) * (1 - 0.05 * q);
                    break;
                case OpenerEntrance.Slide:
                    slide = -(1 - p) * 0.06 + q * 0.04;
                    break;
            }
            result.Add(new OpenerLine(lines[i].Role, lines[i].Text, p * (1 - q), rise, slide, scale));
        }
        var whole = EaseOut(Clamp01(enter)) * (1 - EaseIn(Clamp01(exit)));
        return new OpenerCard(result, picture, picture is null ? 0 : whole, whole, picture is not null && (beside || Look.Left));
    }

    private static double Clamp01(double x) => double.IsNaN(x) ? 0 : Math.Clamp(x, 0, 1);

    private static double EaseOut(double x) => 1 - (1 - x) * (1 - x) * (1 - x);

    private static double EaseIn(double x) => x * x * x;

    private static double Smooth(double x)
    {
        x = Clamp01(x);
        return x * x * (3 - 2 * x);
    }
}
