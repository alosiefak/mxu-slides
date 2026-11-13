using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Openers;

namespace MxU.Slides.Core.Tests.Openers;

/// <summary>
/// The opener and montage maker's logic (Phase 13, Windows only), none of which draws anything:
/// finding beats in synthesized music, sharing a montage's time among its pictures, the
/// countdown's clock, the announcement loop's seam, and recipes kept in Windows extras.
/// </summary>
public sealed class OpenerTests : IDisposable
{
    private const int Rate = 48_000;
    private readonly string root = Path.Combine(Path.GetTempPath(), "Openers-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    // MARK: Beats

    /// <summary>A click track: a short tick every beat, the first at <paramref name="first"/> seconds, over a quiet hiss if asked.</summary>
    private static float[] Clicks(double bpm, double seconds, double first = 0.25, double hiss = 0, double offBeat = 0)
    {
        var sound = new float[(int)(seconds * Rate)];
        var random = new Random(7);
        if (hiss > 0)
            for (var i = 0; i < sound.Length; i++) sound[i] = (float)((random.NextDouble() * 2 - 1) * hiss);
        void Tick(double at, double loud)
        {
            var start = (int)Math.Round(at * Rate);
            for (var i = 0; i < Rate * 0.04 && start + i < sound.Length; i++)
                sound[start + i] += (float)(loud * Math.Exp(-i / (Rate * 0.008)) * Math.Sin(2 * Math.PI * 1000 * i / Rate));
        }
        var beat = 60 / bpm;
        for (var at = first; at < seconds; at += beat)
        {
            Tick(at, 0.8);
            if (offBeat > 0) Tick(at + beat / 2, offBeat);
        }
        return sound;
    }

    private static IEnumerable<double> ClickTimes(double bpm, double seconds, double first = 0.25)
    {
        for (var at = first; at < seconds; at += 60 / bpm) yield return at;
    }

    private static void AssertOnTheClicks(BeatAnalysis found, double bpm, double seconds, double first = 0.25)
    {
        Assert.InRange(found.BeatsPerMinute, bpm * 0.98, bpm * 1.02);
        var clicks = ClickTimes(bpm, seconds, first).ToList();
        // Nearly every click is found, and every beat found is on a click (within 20 ms: less than a video frame).
        Assert.InRange(found.Beats.Count, clicks.Count - 2, clicks.Count + 1);
        Assert.All(found.Beats, b => Assert.True(clicks.Min(c => Math.Abs(c - b)) < 0.02, $"beat at {b:0.000} is {clicks.Min(c => Math.Abs(c - b)) * 1000:0} ms from a click"));
    }

    [Theory]
    [InlineData(120)]
    [InlineData(90)]
    [InlineData(100)]
    [InlineData(140)]
    public void BeatsAreFoundOnAClickTrack(double bpm)
    {
        var found = BeatDetector.Detect(Clicks(bpm, 30), Rate);
        Assert.True(found.Confidence > 0.6, $"confidence {found.Confidence:0.00}");
        Assert.Equal(30, found.Seconds, 3);
        AssertOnTheClicks(found, bpm, 30);
    }

    [Fact]
    public void ATempoBetweenStepsDoesNotDriftOverAMinute()
    {
        // 128 a minute is 93.75 of the detector's 5 ms steps: a tempo read as 94 would be 160 ms out by the end.
        var found = BeatDetector.Detect(Clicks(128, 60, first: 0.4), Rate);
        AssertOnTheClicks(found, 128, 60, first: 0.4);
    }

    [Fact]
    public void BeatsAreFoundUnderHissAndQuieterOffBeats()
    {
        var found = BeatDetector.Detect(Clicks(110, 40, hiss: 0.03, offBeat: 0.25), Rate);
        Assert.True(found.Confidence >= BeatDetector.ClearBeat, $"confidence {found.Confidence:0.00}");
        AssertOnTheClicks(found, 110, 40);
    }

    [Fact]
    public void MusicWithNoClearBeatHasNoBeats()
    {
        // A held organ note (with its overtones) that swells slowly, and plain hiss: nothing to cut on.
        var pad = new float[20 * Rate];
        for (var i = 0; i < pad.Length; i++)
        {
            var t = i / (double)Rate;
            pad[i] = (float)((0.3 + 0.2 * Math.Sin(2 * Math.PI * t / 7)) * (Math.Sin(2 * Math.PI * 220 * t) + Math.Sin(2 * Math.PI * 440 * t) + Math.Sin(2 * Math.PI * 660 * t)) / 3);
        }
        Assert.Empty(BeatDetector.Detect(pad, Rate).Beats);

        var random = new Random(3);
        var hiss = new float[20 * Rate];
        for (var i = 0; i < hiss.Length; i++) hiss[i] = (float)(random.NextDouble() - 0.5);
        Assert.Empty(BeatDetector.Detect(hiss, Rate).Beats);

        Assert.Empty(BeatDetector.Detect(new float[10 * Rate], Rate).Beats);
        Assert.Empty(BeatDetector.Detect(Clicks(120, 1.5), Rate).Beats); // too short to tell
    }

    [Fact]
    public void MusicFedInPiecesGivesTheSameBeats()
    {
        var sound = Clicks(120, 20);
        var whole = BeatDetector.Detect(sound, Rate);
        var pieces = new BeatDetector(Rate);
        for (var at = 0; at < sound.Length; at += 4097) pieces.Add(sound.AsSpan(at, Math.Min(4097, sound.Length - at)));
        Assert.Equal(whole.Beats, pieces.Finish().Beats);
    }

    // MARK: The montage's shots

    [Fact]
    public void CutsLandOnBeatsAndEveryPictureIsUsed()
    {
        var music = BeatDetector.Detect(Clicks(120, 24), Rate).Music;
        var shots = OpenerPlan.PlanShots(5, 0, 24, music.Beats, out var onBeats);
        Assert.True(onBeats);
        // 24 seconds at about 4 a shot is 6 shots: the 5 pictures, and the first again.
        Assert.Equal([0, 1, 2, 3, 4, 0], shots.Select(s => s.Source));
        Assert.Equal(0, shots[0].Start);
        Assert.Equal(24, shots[^1].End);
        for (var i = 1; i < shots.Count; i++)
        {
            Assert.Equal(shots[i - 1].End, shots[i].Start);
            Assert.Contains(music.Beats, b => Math.Abs(b - shots[i].Start) < 1e-9);
            Assert.InRange(shots[i].End - shots[i].Start, 2, 6);
        }
    }

    [Fact]
    public void MorePicturesThanTimeStillUsesEveryOne()
    {
        var beats = ClickTimes(120, 10).ToList();
        var shots = OpenerPlan.PlanShots(12, 0, 10, beats, out _);
        Assert.Equal(Enumerable.Range(0, 12), shots.Select(s => s.Source));
        Assert.Equal(10, shots[^1].End);
        Assert.All(shots, s => Assert.True(s.End - s.Start > 0.3, $"a shot of {s.End - s.Start:0.00} s"));
    }

    [Fact]
    public void WithoutBeatsTheCutsAreEven()
    {
        var shots = OpenerPlan.PlanShots(4, 2, 18, [], out var onBeats);
        Assert.False(onBeats);
        Assert.Equal([2.0, 6, 10, 14], shots.Select(s => s.Start));
        Assert.Equal(18, shots[^1].End);
        Assert.Empty(OpenerPlan.PlanShots(0, 0, 10, [], out _));
    }

    [Fact]
    public void AMontageIsAsLongAsItsMusicOrTheLengthChosen()
    {
        var recipe = OpenerRecipe.For(OpenerKind.Montage) with { Shots = ["a.jpg", "b.jpg", "c.jpg"] };
        var music = new OpenerMusic(42.5, [.. ClickTimes(120, 42.5)]);
        Assert.Equal(42.5, OpenerPlan.For(recipe, music).Seconds, 2);
        Assert.Equal(20, OpenerPlan.For(recipe with { Seconds = 20 }, music).Seconds, 2);
        // Music shorter than the length asked for: the montage ends with the music.
        Assert.Equal(42.5, OpenerPlan.For(recipe with { Seconds = 90 }, music).Seconds, 2);
        // No music: four seconds a picture, plus the cards.
        Assert.Equal(12, OpenerPlan.For(recipe).Seconds, 2);
        var carded = OpenerPlan.For(recipe with { Title = "Summer Camp", EndTitle = "See you next year" });
        Assert.Equal(12 + 2 * OpenerPlan.MontageCardSeconds, carded.Seconds, 2);
        Assert.Equal(OpenerPlan.MontageCardSeconds, carded.Shots[0].Start, 6);
        Assert.Equal(carded.Seconds - OpenerPlan.MontageCardSeconds, carded.Shots[^1].End, 6);
        Assert.True(OpenerPlan.For(recipe, music).OnBeats);
    }

    [Fact]
    public void AMontageDissolvesFromShotToShotWithGentleMoves()
    {
        var plan = OpenerPlan.For(OpenerRecipe.For(OpenerKind.Montage) with { Shots = ["a.jpg", "b.jpg", "clip.mp4"] });
        // Mid-shot: one picture, covering the screen, so the background needn't be drawn.
        var middle = plan.FrameAt(2);
        Assert.Equal(["a.jpg"], middle.Shots.Select(s => s.Path));
        Assert.False(middle.ShowBackground);
        Assert.InRange(middle.Shots[0].Zoom, 1.0, 1.08);
        // At the cut: both, the new one half-way in, on top.
        var cut = plan.FrameAt(plan.Shots[0].End);
        Assert.Equal(["a.jpg", "b.jpg"], cut.Shots.Select(s => s.Path));
        Assert.Equal(1, cut.Shots[0].Opacity);
        Assert.Equal(0.5, cut.Shots[1].Opacity, 3);
        // A video clip plays from its start and isn't zoomed.
        var clip = plan.FrameAt(plan.Shots[2].Start + 1);
        Assert.Equal(1, clip.Shots[^1].Zoom);
        Assert.Equal(1 + plan.Dissolve / 2, clip.Shots[^1].ClipSeconds, 6);
        // It starts and ends on black, and the music fades with it.
        Assert.Equal((1.0, 1.0), (plan.FrameAt(0).Black, plan.FrameAt(plan.Seconds).Black));
        Assert.Equal((1.0, 0.0), (plan.MusicGain(2), plan.MusicGain(plan.Seconds)));
    }

    // MARK: The countdown

    [Theory]
    [InlineData(300, "5:00")]
    [InlineData(299.99, "5:00")]
    [InlineData(299, "4:59")]
    [InlineData(60, "1:00")]
    [InlineData(59.5, "1:00")]
    [InlineData(9.01, "0:10")]
    [InlineData(0.2, "0:01")]
    [InlineData(0, "0:00")]
    [InlineData(-4, "0:00")]
    [InlineData(600, "10:00")]
    public void TheClockRoundsUpLikeAKitchenTimer(double remaining, string text) => Assert.Equal(text, OpenerPlan.CountdownText(remaining));

    [Fact]
    public void ACountdownShowsTheRightTimeAtEachMomentThenWelcome()
    {
        var plan = OpenerPlan.For(OpenerRecipe.For(OpenerKind.Countdown) with { Seconds = 120, Title = "Service starts in", EndTitle = "Welcome" });
        Assert.Equal(120 + OpenerPlan.WelcomeSeconds, plan.Seconds);
        Assert.Equal(125 * 30, plan.FrameCount);
        Assert.Equal(["2:00", "1:59", "1:00", "0:01"], new[] { 0.0, 1, 60.5, 119.5 }.Select(plan.CountdownTextAt));

        string Digits(double t) => plan.FrameAt(t).Cards.Single().Lines.Single(l => l.Role == OpenerRole.Digits).Text;
        Assert.Equal(("2:00", "1:30", "0:10"), (Digits(0.5), Digits(30.2), Digits(110.9)));
        var counting = plan.FrameAt(45).Cards.Single();
        Assert.Equal([OpenerRole.Kicker, OpenerRole.Digits], counting.Lines.Select(l => l.Role));
        Assert.All(counting.Lines, l => Assert.Equal(1, l.Opacity));

        var welcome = plan.FrameAt(122).Cards.Single();
        Assert.Equal(("Welcome", OpenerRole.Title, 1.0), (welcome.Lines[0].Text, welcome.Lines[0].Role, welcome.Lines[0].Opacity));

        // Without a Welcome card it's exactly as long as the count.
        Assert.Equal(120, OpenerPlan.For(plan.Recipe with { EndTitle = "" }).Seconds);
    }

    // MARK: The bumper

    [Fact]
    public void ABumperStartsAndEndsOnBlackWithItsWordsInBetween()
    {
        var plan = OpenerPlan.For(OpenerRecipe.For(OpenerKind.Bumper) with { Seconds = 8, Picture = "logo.png" });
        Assert.Equal(240, plan.FrameCount);
        Assert.Equal(1, plan.FrameAt(0).Black);
        Assert.Equal(1, plan.FrameAt(8).Black);
        Assert.All(plan.FrameAt(0.2).Cards.Single().Lines, l => Assert.Equal(0, l.Opacity));
        var middle = plan.FrameAt(4);
        Assert.Equal(0, middle.Black);
        Assert.Equal([(OpenerRole.Title, 1.0), (OpenerRole.Subtitle, 1.0)], middle.Cards.Single().Lines.Select(l => (l.Role, l.Opacity)));
        Assert.Equal(("logo.png", 1.0, 1.0), (middle.Cards[0].Picture, middle.Cards[0].PictureOpacity, middle.Cards[0].Accent));
        // The subtitle follows the title in, and the words are leaving near the end.
        var arriving = plan.FrameAt(1.0).Cards.Single().Lines;
        Assert.True(arriving[0].Opacity > arriving[1].Opacity);
        Assert.InRange(plan.FrameAt(7).Cards.Single().Lines[0].Opacity, 0.01, 0.99);
    }

    // MARK: The announcement loop

    private static string Json(OpenerFrame frame) => System.Text.Encoding.UTF8.GetString(MxuJson.SerializeToUtf8(frame));

    [Theory]
    [InlineData("clean")]
    [InlineData("bold")]
    [InlineData("serif")]
    [InlineData("broadcast")]
    [InlineData("minimal")]
    public void AnAnnouncementLoopHasNoSeam(string look)
    {
        var recipe = OpenerRecipe.For(OpenerKind.Announcements) with
        {
            Look = look, CardSeconds = 7,
            Cards = [new("Welcome", "Glad you're here"), new("Small Groups", "Sign up today", "groups.jpg"), new("Youth Night", "Friday 7 pm")],
        };
        var plan = OpenerPlan.For(recipe);
        Assert.Equal((21.0, 630), (plan.Seconds, plan.FrameCount));
        // The frame after the last is the first, exactly: background and words.
        Assert.Equal(Json(plan.Frame(0)), Json(plan.Frame(plan.FrameCount)));
        Assert.Equal(0, plan.Frame(plan.FrameCount).BackgroundPhase);
        // And the last frame is a small step from the first: the first card has all but arrived.
        var first = plan.Frame(0).Cards.Single();
        var last = plan.Frame(plan.FrameCount - 1).Cards.Single();
        Assert.Equal(first.Lines.Select(l => l.Text), last.Lines.Select(l => l.Text));
        for (var i = 0; i < first.Lines.Count; i++)
        {
            Assert.InRange(first.Lines[i].Opacity - last.Lines[i].Opacity, 0, 0.02);
            Assert.InRange(Math.Abs(first.Lines[i].Rise - last.Lines[i].Rise) + Math.Abs(first.Lines[i].Slide - last.Lines[i].Slide), 0, 0.002);
        }
        // No step anywhere in the loop is bigger than a gentle move allows.
        double Shown(OpenerFrame frame, string text) => frame.Cards.SelectMany(c => c.Lines).Where(l => l.Text == text).Sum(l => l.Opacity);
        for (var f = 0; f < plan.FrameCount; f++)
        {
            var (a, b) = (plan.Frame(f), plan.Frame(f + 1));
            foreach (var card in recipe.Cards)
                Assert.True(Math.Abs(Shown(a, card.Title) - Shown(b, card.Title)) < 0.2, $"{card.Title} jumps at frame {f}");
        }
        // Each card gets its turn, fully shown, and the background turns a whole number of times.
        Assert.Equal(["Welcome", "Small Groups", "Youth Night"], new[] { 2.0, 9, 16 }.Select(t => plan.FrameAt(t).Cards.Single().Lines[0].Text));
        Assert.Equal("groups.jpg", plan.FrameAt(9).Cards.Single().Picture);
        Assert.Equal(21, plan.Background.Seconds);
        Assert.Equal(0, plan.FrameAt(5).Black);
    }

    [Fact]
    public void ALongLoopsBackgroundStillComesRoundExactly()
    {
        var cards = Enumerable.Range(1, 9).Select(i => new AnnouncementCard($"Card {i}")).ToList();
        var plan = OpenerPlan.For(OpenerRecipe.For(OpenerKind.Announcements) with { Cards = cards, CardSeconds = 10 });
        // 90 seconds: four turns of 22.5 s (the nearest whole number of turns to one every 20 s).
        Assert.Equal(90, plan.Seconds);
        Assert.Equal(0.5, plan.FrameAt(11.25).BackgroundPhase, 6);
        Assert.Equal(0, plan.Frame(plan.FrameCount).BackgroundPhase);
        Assert.Equal(Json(plan.Frame(7)), Json(plan.Frame(plan.FrameCount + 7)));
    }

    // MARK: Recipes

    [Fact]
    public void ARecipeIsKeptInWindowsExtrasAndComesBackTheSame()
    {
        var extras = new WindowsExtras(root);
        var recipe = new OpenerRecipe
        {
            Kind = OpenerKind.Montage, Look = "serif", Background = "aurora", Palette = "Advent", Title = "Summer Camp “26”", Subtitle = "Ages 8–12",
            EndTitle = "See you there", Seconds = 45, Shots = [@"C:\Photos\one.jpg", @"C:\Photos\two.mp4"], Music = @"C:\Music\song.mp3",
            Cards = [new("Welcome", "Body", @"C:\Photos\card.png")], CardSeconds = 12, Name = "Camp montage",
        }.Normalized();
        extras.Save(OpenerRecipe.ExtraKind, "media-1", recipe, basis: "hash-1");

        var back = extras.Get<OpenerRecipe>(OpenerRecipe.ExtraKind, "media-1", basis: "hash-1");
        Assert.NotNull(back);
        Assert.Equal(MxuJson.SerializeToUtf8(recipe), MxuJson.SerializeToUtf8(back));
        Assert.Equal((OpenerKind.Montage, "serif", "aurora", "Advent", 45.0, "Camp montage"), (back.Kind, back.Look, back.Background, back.Palette, back.Seconds, back.Name));
        Assert.Equal(recipe.Shots, back.Shots);
        Assert.Equal(recipe.Cards, back.Cards);
        // The kind is written as a word, so the file reads plainly and survives a reordering of the list.
        Assert.Contains("\"Montage\"", File.ReadAllText(Directory.EnumerateFiles(root, "media-1.json", SearchOption.AllDirectories).Single()));
        // The video replaced by another (on a Mac, say): the recipe no longer describes it.
        Assert.Null(extras.Get<OpenerRecipe>(OpenerRecipe.ExtraKind, "media-1", basis: "another-hash"));
    }

    [Fact]
    public void NormalizingPutsEverythingInRange()
    {
        var wild = new OpenerRecipe { Kind = OpenerKind.Bumper, Look = "lasers", Background = "nope", Palette = "Nope", Seconds = 99, Height = 720, FrameRate = 24, Name = "  " }.Normalized();
        var look = OpenerLooks.All[0];
        Assert.Equal((look.Id, look.Background, look.Palette, 15.0, 1080, 1920, 30, null), (wild.Look, wild.Background, wild.Palette, wild.Seconds, wild.Height, wild.Width, wild.FrameRate, wild.Name));
        Assert.Equal(10, (new OpenerRecipe { Kind = OpenerKind.Countdown, Seconds = 1 }).Normalized().Seconds);
        Assert.Equal(600, (new OpenerRecipe { Kind = OpenerKind.Countdown, Seconds = 5000 }).Normalized().Seconds);
        Assert.Equal(0, (new OpenerRecipe { Kind = OpenerKind.Montage, Seconds = double.NaN }).Normalized().Seconds);
        Assert.Equal((3, 30), ((new OpenerRecipe { CardSeconds = 0 }).Normalized().CardSeconds, (new OpenerRecipe { CardSeconds = 99 }).Normalized().CardSeconds));
        // A background picked without a palette comes in that background's own colours.
        Assert.Equal("Northern Lights", (new OpenerRecipe { Background = "aurora" }).Normalized().Palette);
    }

    [Fact]
    public void EachKindIsFiledAsTheBinsOwnKindAndNamedForWhatItIs()
    {
        Assert.Equal((false, MediaClassification.Foreground), (OpenerRecipe.For(OpenerKind.Bumper).Loops, OpenerRecipe.For(OpenerKind.Bumper).Classification));
        Assert.Equal((false, MediaClassification.Foreground), (OpenerRecipe.For(OpenerKind.Countdown).Loops, OpenerRecipe.For(OpenerKind.Countdown).Classification));
        Assert.Equal((false, MediaClassification.Foreground), (OpenerRecipe.For(OpenerKind.Montage).Loops, OpenerRecipe.For(OpenerKind.Montage).Classification));
        Assert.Equal((true, MediaClassification.Background), (OpenerRecipe.For(OpenerKind.Announcements).Loops, OpenerRecipe.For(OpenerKind.Announcements).Classification));
        Assert.Equal("A New Beginning · Bumper", OpenerRecipe.For(OpenerKind.Bumper).SuggestedName());
        Assert.Equal("Countdown 5:00", OpenerRecipe.For(OpenerKind.Countdown).SuggestedName());
        Assert.Equal("Announcements (2)", OpenerRecipe.For(OpenerKind.Announcements).SuggestedName());
        // Five looks, each with a background and palette that exist.
        Assert.Equal(5, OpenerLooks.All.Count);
        Assert.All(OpenerLooks.All, l => Assert.Equal((l.Background, l.Palette), (new OpenerRecipe { Look = l.Id }.Normalized().Background, new OpenerRecipe { Look = l.Id }.Normalized().Palette)));
    }
}
