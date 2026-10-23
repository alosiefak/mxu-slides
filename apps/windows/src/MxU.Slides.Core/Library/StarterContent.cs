using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Library;

/// <summary>What a brand-new library starts with: themes, a welcome deck, a sample song and a service.</summary>
public static class StarterContent
{
    public const string WelcomePresentationId = "welcome.presentation";
    public const string WelcomeServiceId = "welcome.service";
    public const string SampleSongId = "welcome.song.amazing-grace";

    /// <summary>Group names and colours for song sections. Same palette as the Mac app.</summary>
    public static readonly IReadOnlyList<GroupDefinition> GroupPalette =
    [
        G("verse", "Verse", "#0077CCFF"), G("verse-1", "Verse 1", "#0077CCFF"), G("verse-2", "Verse 2", "#005999FF"),
        G("verse-3", "Verse 3", "#003C66FF"), G("verse-4", "Verse 4", "#0068B3FF"), G("verse-5", "Verse 5", "#004A80FF"),
        G("verse-6", "Verse 6", "#002D4DFF"), G("chorus", "Chorus", "#CC004EFF"), G("chorus-1", "Chorus 1", "#CC004EFF"),
        G("chorus-2", "Chorus 2", "#99003BFF"), G("chorus-3", "Chorus 3", "#660027FF"), G("chorus-4", "Chorus 4", "#B30044FF"),
        G("bridge", "Bridge", "#7600CCFF"), G("bridge-1", "Bridge 1", "#7600CCFF"), G("bridge-2", "Bridge 2", "#590099FF"),
        G("bridge-3", "Bridge 3", "#3B0066FF"), G("pre-chorus", "Pre-Chorus", "#CC298BFF"), G("tag", "Tag", "#CC2929FF"),
        G("intro", "Intro", "#B3A724FF"), G("ending", "Ending", "#998F1FFF"), G("outro", "Outro", "#7E7619FF"),
        G("interlude", "Interlude", "#24B34CFF"), G("vamp", "Vamp", "#24B34CFF"), G("turnaround", "Turnaround", "#24B34CFF"),
        G("blank", "Blank", "#000000FF"),
    ];

    private static GroupDefinition G(string id, string name, string hex) => new() { Id = id, Name = name, ColorHex = hex };

    /// <summary>The colour for a section name ("Verse 2" → its verse-2 colour, "Chorus 5" → chorus, unknown → grey).</summary>
    public static string GroupColor(string? sectionName, string? explicitHex = null)
    {
        if (!string.IsNullOrEmpty(explicitHex)) return explicitHex;
        if (string.IsNullOrWhiteSpace(sectionName)) return "#5C5C5CFF";
        var name = sectionName.Trim();
        var exact = GroupPalette.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact.ColorHex;
        var family = GroupPalette.FirstOrDefault(g => name.StartsWith(g.Name, StringComparison.OrdinalIgnoreCase) && !g.Name.Contains(' '));
        return family?.ColorHex ?? "#5C5C5CFF";
    }

    private sealed record Page(string Key, string Kicker, string Title, string Body);

    private static readonly Page[] WelcomePages =
    [
        new("welcome", "Getting Started", "Welcome to MxU Slides",
            "This deck is a two-minute tour. Click this slide to take it live, then press the Right Arrow or Space for the next one."),
        new("live", "Step 1", "Click a slide to go live",
            "Whatever you click shows on your audience screen. Left and Right Arrow move through the deck. F1 clears everything, F2 the slide, F3 media."),
        new("screens", "Step 2", "Pick your screens",
            "Screens assigns each display a role: Audience for the room, Stage for the band. With one display, the preview shows what the room would see."),
        new("services", "Step 3", "Build a service",
            "A service is your run order, like a ProPresenter playlist. Add songs, presentations and media from the library, then run it top to bottom."),
        new("edit", "Step 4", "Edit and restyle",
            "Edit a song's lyrics as plain text with section labels like Verse 1 and Chorus. Themes restyle a whole presentation at once."),
        new("import", "Step 5", "Bring what you have",
            "Import lyrics from SongSelect, ChordPro or plain text files, and drag in images and videos for backgrounds."),
    ];

    private static SlideObject Text(string id, string name, string text, double y, double height, string font, double size,
        string color, double tracking = 0, bool uppercase = false, TextVerticalAlignment valign = TextVerticalAlignment.Middle,
        double lineHeight = 1) => new()
    {
        Id = id, ObjectKind = SlideObjectKind.Text, Name = name, Text = text, X = 200, Y = y, Width = 1520, Height = height,
        TextStyle = new TextStyle
        {
            FontName = font, FontSize = size, ColorHex = color, Tracking = tracking, LineHeightMultiple = lineHeight,
            HorizontalAlignment = TextHorizontalAlignment.Left, VerticalAlignment = valign,
            TextTransform = uppercase ? TextTransform.Uppercase : null, InsetLeft = 0, InsetRight = 0,
        },
    };

    public static Presentation WelcomePresentation() => new()
    {
        Id = WelcomePresentationId,
        Name = "Welcome to MxU Slides",
        PresentationKind = PresentationKind.Deck,
        ThemeId = "",
        Slides = WelcomePages.Select(page =>
        {
            var b = $"{WelcomePresentationId}.{page.Key}";
            return new Slide
            {
                Id = b,
                Name = page.Title,
                Objects =
                [
                    Text($"{b}.kicker", "Kicker", page.Kicker, 250, 60, "HelveticaNeue", 32, "#3B82F6FF", 4, uppercase: true),
                    new SlideObject
                    {
                        Id = $"{b}.rule", ObjectKind = SlideObjectKind.Shape, Name = "Rule", Text = "", X = 200, Y = 325, Width = 160, Height = 4,
                        ShapeKind = ShapeKind.Rectangle, Fill = new ObjectFill { FillKind = FillKind.Solid, ColorHex = "#3B82F6FF" },
                    },
                    Text($"{b}.title", "Title", page.Title, 350, 170, "HelveticaNeue-Bold", 104, "#FFFFFFFF"),
                    Text($"{b}.body", "Body", page.Body, 540, 330, "HelveticaNeue", 46, "#CBD5E1FF", valign: TextVerticalAlignment.Top, lineHeight: 1.3),
                ],
                BackgroundFill = new ObjectFill { FillKind = FillKind.Solid, ColorHex = "#0B1220FF" },
            };
        }).ToList(),
    };

    /// <summary>"Amazing Grace" (John Newton, 1779, public domain), as lyrics text with section labels.</summary>
    public const string AmazingGraceLyrics = """
        Verse 1
        Amazing grace, how sweet the sound
        That saved a wretch like me
        I once was lost, but now am found
        Was blind, but now I see

        Verse 2
        'Twas grace that taught my heart to fear
        And grace my fears relieved
        How precious did that grace appear
        The hour I first believed

        Verse 3
        Through many dangers, toils and snares
        I have already come
        'Tis grace hath brought me safe thus far
        And grace will lead me home

        Verse 4
        When we've been there ten thousand years
        Bright shining as the sun
        We've no less days to sing God's praise
        Than when we first begun
        """;

    public static Service WelcomeService(DateOnly date, Presentation? song) => new()
    {
        Id = WelcomeServiceId,
        Name = "Getting Started",
        ServiceDate = date.ToString("yyyy-MM-dd"),
        Items =
        [
            new ServiceItem { Id = $"{WelcomeServiceId}.welcome", ItemKind = ServiceItemKind.Presentation, Name = "Welcome to MxU Slides", RefId = WelcomePresentationId },
            .. song is null ? Array.Empty<ServiceItem>() :
            [
                new ServiceItem { Id = $"{WelcomeServiceId}.header", ItemKind = ServiceItemKind.Header, Name = "Worship", RefId = "", ColorHex = "#3B82F6FF" },
                new ServiceItem { Id = $"{WelcomeServiceId}.song", ItemKind = ServiceItemKind.Presentation, Name = song.Name, RefId = song.Id },
            ],
        ],
    };
}
