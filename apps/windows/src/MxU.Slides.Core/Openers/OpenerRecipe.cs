using System.Text.Json.Serialization;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Motion;

namespace MxU.Slides.Core.Openers;

/// <summary>The four things "Make an opener…" makes.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OpenerKind>))]
public enum OpenerKind
{
    /// <summary>A sermon-series bumper: a title and subtitle that animate in and out (5–15 s).</summary>
    Bumper,

    /// <summary>A running countdown to the service (1–10 min), optionally ending on a Welcome card.</summary>
    Countdown,

    /// <summary>Photos and clips cut to a music track.</summary>
    Montage,

    /// <summary>Announcement cards, each held a few seconds, looping seamlessly.</summary>
    Announcements,
}

/// <summary>One card of an announcement loop.</summary>
public sealed record AnnouncementCard(string Title = "", string Body = "", string? Picture = null);

/// <summary>
/// Everything that makes one opener: its kind, its look, the motion background behind it and the
/// words and files the volunteer filled in. Like a motion background's recipe
/// (Core/Motion/MotionRecipe.cs) it's kept in the library's Windows extras beside the video it
/// made (<see cref="ExtraKind"/>), so the window can open it again and change one thing: next
/// week's series title, say. The video itself is an ordinary media item; nothing here ever goes
/// into a shared document, so a Mac just sees a video.
/// <para>
/// Pictures, clips and music are named by their full path on this PC. A file that has since
/// moved is left out when the opener is made again.
/// </para>
/// </summary>
public sealed record OpenerRecipe
{
    /// <summary>The Windows-extras kind recipes are kept under, by media item id.</summary>
    public const string ExtraKind = "opener";

    /// <summary>The media library folder openers go in.</summary>
    public const string Folder = "Openers";

    public const int MaxCountdownSeconds = 600;
    public const int MaxMontageSeconds = 600;

    public OpenerKind Kind { get; init; }

    /// <summary>The look's id (<see cref="OpenerLooks"/>).</summary>
    public string Look { get; init; } = OpenerLooks.All[0].Id;

    /// <summary>The motion background behind it: a style id from <see cref="MotionStyles"/>; null for the look's own.</summary>
    public string? Background { get; init; }

    /// <summary>The background's palette (a <see cref="MotionPalette"/> preset's name); null for the look's own.</summary>
    public string? Palette { get; init; }

    /// <summary>Bumper: the series title. Countdown: the line over the clock. Montage: the opening card's title (blank for none).</summary>
    public string Title { get; init; } = "";

    /// <summary>The smaller line under the title: a subtitle, the speaker, the church's name.</summary>
    public string Subtitle { get; init; } = "";

    /// <summary>Bumper: an optional picture or logo.</summary>
    public string? Picture { get; init; }

    /// <summary>
    /// How long, in seconds. Bumper: 5–15. Countdown: what it counts down from (the video is that
    /// long, plus the Welcome card). Montage: 0 for "as long as the music" (or four seconds a
    /// picture without music).
    /// </summary>
    public double Seconds { get; init; } = 8;

    /// <summary>Countdown: end on a Welcome card. Montage: the closing card's words (blank for none).</summary>
    public string EndTitle { get; init; } = "";

    /// <summary>Montage: the pictures and clips, in order.</summary>
    public IReadOnlyList<string> Shots { get; init; } = [];

    /// <summary>Montage: the music track; null for a silent montage cut evenly.</summary>
    public string? Music { get; init; }

    /// <summary>Announcement loop: the cards.</summary>
    public IReadOnlyList<AnnouncementCard> Cards { get; init; } = [];

    /// <summary>Announcement loop: how long each card is up, in whole seconds (3–30).</summary>
    public int CardSeconds { get; init; } = 8;

    /// <summary>The picture's height: 1080 (the default) or 2160. The width is 16:9.</summary>
    public int Height { get; init; } = 1080;

    public int FrameRate { get; init; } = 30;

    /// <summary>The name in the Media bin; null uses <see cref="SuggestedName"/>.</summary>
    public string? Name { get; init; }

    /// <summary>The recipe format's version, should it ever need to change.</summary>
    public int Version { get; init; } = 1;

    [JsonIgnore]
    public int Width => Height * 16 / 9;

    /// <summary>A starting recipe for <paramref name="kind"/>, with sample words to replace.</summary>
    public static OpenerRecipe For(OpenerKind kind) => kind switch
    {
        OpenerKind.Bumper => new() { Kind = kind, Title = "A New Beginning", Subtitle = "A sermon series in the book of Ruth", Seconds = 8 },
        OpenerKind.Countdown => new() { Kind = kind, Look = "bold", Title = "Service starts in", Seconds = 300, EndTitle = "Welcome" },
        OpenerKind.Montage => new() { Kind = kind, Look = "serif", Title = "", Seconds = 0 },
        _ => new()
        {
            Kind = kind, Look = "broadcast",
            Cards =
            [
                new("Welcome", "We're so glad you're here. Service begins shortly."),
                new("Small Groups", "New groups start this month. Sign up at the welcome desk."),
            ],
        },
    };

    /// <summary>
    /// The recipe with everything in range, so a damaged or hand-edited one can't make a broken
    /// video: a known look, background and palette; lengths within each kind's limits; 1080 or
    /// 2160; 30 or 60 frames a second.
    /// </summary>
    public OpenerRecipe Normalized()
    {
        var look = OpenerLooks.Find(Look) ?? OpenerLooks.All[0];
        var background = MotionStyles.Find(Background)?.Id ?? look.Background;
        var palette = MotionPalette.Find(Palette)?.Name ?? (Background is null || MotionStyles.Find(Background) is null ? look.Palette : MotionStyles.Find(background)!.DefaultPalette);
        var seconds = double.IsFinite(Seconds) ? Seconds : 0;
        return this with
        {
            Look = look.Id,
            Background = background,
            Palette = palette,
            Title = Title ?? "",
            Subtitle = Subtitle ?? "",
            EndTitle = EndTitle ?? "",
            Picture = string.IsNullOrWhiteSpace(Picture) ? null : Picture,
            Music = string.IsNullOrWhiteSpace(Music) ? null : Music,
            Seconds = Kind switch
            {
                OpenerKind.Bumper => Math.Clamp(seconds, 5, 15),
                OpenerKind.Countdown => Math.Clamp(Math.Round(seconds), 10, MaxCountdownSeconds),
                OpenerKind.Montage => seconds <= 0 ? 0 : Math.Clamp(seconds, 5, MaxMontageSeconds),
                _ => 0,
            },
            Shots = Shots?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? [],
            Cards = Cards?.Where(c => c is not null).Select(c => c with { Title = c.Title ?? "", Body = c.Body ?? "", Picture = string.IsNullOrWhiteSpace(c.Picture) ? null : c.Picture }).ToList() ?? [],
            CardSeconds = Math.Clamp(CardSeconds, 3, 30),
            Height = Height >= 2160 ? 2160 : 1080,
            FrameRate = FrameRate >= 60 ? 60 : 30,
            Name = string.IsNullOrWhiteSpace(Name) ? null : Name.Trim(),
        };
    }

    /// <summary>The name a new opener gets: "A New Beginning · Bumper", "Countdown 5:00", "Montage", "Announcements (3)".</summary>
    public string SuggestedName() => Kind switch
    {
        OpenerKind.Bumper => string.IsNullOrWhiteSpace(Title) ? "Bumper" : $"{Title.Trim()} · Bumper",
        OpenerKind.Countdown => $"Countdown {OpenerPlan.CountdownText(Math.Clamp(Math.Round(Seconds), 10, MaxCountdownSeconds))}",
        OpenerKind.Montage => string.IsNullOrWhiteSpace(Title) ? "Montage" : $"{Title.Trim()} · Montage",
        _ => $"Announcements ({Math.Max(1, Cards.Count)})",
    };

    /// <summary>
    /// How the finished video is filed: an announcement loop is a looping background (the bin's
    /// "Loop" kind); the others are videos that play once.
    /// </summary>
    [JsonIgnore]
    public bool Loops => Kind == OpenerKind.Announcements;

    [JsonIgnore]
    public MediaClassification Classification => Loops ? MediaClassification.Background : MediaClassification.Foreground;

    /// <summary>The motion background drawn behind it, as a recipe for <see cref="MotionRenderer"/>, looping every <paramref name="loopSeconds"/>.</summary>
    public MotionRecipe BackgroundRecipe(double loopSeconds = 20)
    {
        var normal = Normalized();
        var palette = MotionPalette.Find(normal.Palette) ?? MotionPalette.Presets[0];
        return new MotionRecipe
        {
            Style = normal.Background!, Palette = palette.Name, Colors = palette.Colors, Seconds = (int)Math.Round(loopSeconds),
            // A little calmer and dimmer than a background made for lyrics: here it sits behind big titles.
            Speed = 0.8, Intensity = 0.55, FrameRate = normal.FrameRate, Height = normal.Height,
        }.Normalized();
    }
}
