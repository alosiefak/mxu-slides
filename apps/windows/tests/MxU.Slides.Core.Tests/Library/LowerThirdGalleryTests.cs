using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Tests.Library;

// The lower-thirds gallery (Windows-only): every design is a well-formed overlay, made only of
// what the shared format has, inside the safe area, animating in and out without waiting for a click.
public class LowerThirdGalleryTests
{
    public static TheoryData<string> Templates() => [.. LowerThirdGallery.All.Select(t => t.Id)];

    private static LowerThirdTemplate Template(string id) => LowerThirdGallery.All.Single(t => t.Id == id);

    [Fact]
    public void TheGalleryCoversEveryCategoryAndStyle()
    {
        var all = LowerThirdGallery.All;
        Assert.True(all.Count >= 20, $"{all.Count} designs");
        Assert.Equal(all.Count, all.Select(t => t.Id).Distinct().Count());
        foreach (var category in LowerThirdGallery.Categories) Assert.Contains(all, t => t.Category == category);
        Assert.All(all, t => Assert.Contains(t.Category, LowerThirdGallery.Categories));
        Assert.Equal(Enum.GetValues<LowerThirdStyle>().ToHashSet(), all.Select(t => t.Style).ToHashSet());
        Assert.All(all, t => Assert.False(string.IsNullOrWhiteSpace(t.Description)));
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public void EachDesignIsAWellFormedOverlay(string id)
    {
        var overlay = Template(id).Make();
        var steps = overlay.Objects.SelectMany(o => o.AnimationSteps ?? []).ToList();
        Assert.Equal(steps.Count, steps.Select(s => s.Id).Distinct().Count());
        Assert.True(overlay.AnimationOrder!.ToHashSet().SetEquals(steps.Select(s => s.Id)), "the play order names every step");
        Assert.Equal(steps.Count, overlay.AnimationOrder!.Count);

        var groups = AnimationSequence.Groups(overlay.Objects, overlay.AnimationOrder);
        Assert.NotEmpty(groups.Auto); // it builds in as it goes up…
        Assert.Empty(groups.Click); // …never waiting for a click an overlay doesn't get…
        Assert.NotEmpty(groups.Exit); // …and builds out when it's taken down
        Assert.Empty(AnimationEditing.OverlayWarnings(overlay));

        var mattes = SlideSceneBuilder.MaskWiring(overlay.Objects).Mattes;
        foreach (var o in overlay.Objects.Where(o => !mattes.Contains(o.Id)))
        {
            var own = o.AnimationSteps ?? [];
            Assert.True(own.Any(s => s.Kind == AnimationKind.In) && own.Any(s => s.Kind == AnimationKind.Out), $"{o.Name} comes in and goes out");
            if (o.ObjectKind == SlideObjectKind.Text)
                Assert.True(own.All(s => s.Animation != StepAnimation.Move || s.Edge is null), $"{o.Name}: text floats in place");
        }
        foreach (var o in overlay.Objects.Where(o => o.MaskObjectId is not null)) Assert.Contains(o.MaskObjectId!, mattes);
        Assert.True(overlay.Objects.Any(o => o.ObjectKind == SlideObjectKind.Text && o.TextLink is null), "it has a field to fill in");
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public void EachDesignSitsInsideTheSafeArea(string id)
    {
        // EBU R 95's graphics-safe area for 16:9: 5% in from each edge. Full-bleed backdrops
        // (a shadow rising from the bottom, a vignette, a strip across) may run to the edges.
        var overlay = Template(id).Make();
        foreach (var o in overlay.Objects)
        {
            var (x, y, w, h) = (o.X ?? 0, o.Y ?? 0, o.Width ?? 0, o.Height ?? 0);
            var fullBleed = o.ObjectKind == SlideObjectKind.Shape && (w >= 1920 || h >= 1080);
            if (fullBleed) continue;
            Assert.True(x >= 96 && y >= 54 && x + w <= 1824 && y + h <= 1026, $"{id}/{o.Name} at {x},{y} {w}×{h}");
        }
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public void EachDesignIsMadeOfTheSharedFormatAndTheBundledFonts(string id)
    {
        var overlay = Template(id).Make();
        // Round-trips through the documents' JSON unchanged: nothing the Mac wouldn't read.
        var json = MxuJson.Serialize(overlay);
        Assert.Equal(json, MxuJson.Serialize(MxuJson.Deserialize<Overlay>(json)));
        foreach (var font in FontSharing.Names(overlay))
            Assert.True(new[] { "Poppins-", "Montserrat-", "BebasNeue-", "DMSerifDisplay-" }.Any(font.StartsWith), $"{id}: {font}");
    }

    [Fact]
    public void AddingADesignMakesANewOverlayInThePack()
    {
        var template = Template("gallery.speaker.broadcast");
        var first = LowerThirdGallery.Instantiate(template);
        var second = LowerThirdGallery.Instantiate(template, "Sunday");
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal("Lower Thirds/Speaker", first.Folder);
        Assert.Equal("Sunday", OverlayLibrary.PackOf(second));
        Assert.Equal("Speaker", OverlayLibrary.CategoryOf(second));
        Assert.False(OverlayLibrary.IsStarter(first)); // the library's own: never refreshed over
        Assert.Equal("Lower Thirds/Speaker", LowerThirdGallery.Instantiate(template, "  ").Folder);
        second.Objects[0].X = 0;
        Assert.Equal(150, template.Make().Objects[0].X); // each make is fresh
    }

    [Fact]
    public void SongCreditsFillFromTheSongsCcliDetails()
    {
        var overlay = LowerThirdGallery.Instantiate(Template("gallery.credits.clean"));
        var song = new Presentation
        {
            Id = "s", Name = "Cornerstone", PresentationKind = PresentationKind.Song, ThemeId = "",
            Ccli = new CCLIInfo { SongTitle = "Cornerstone", Author = "Edward Mote, Eric Liljero", SongNumber = 6158927 },
        };
        Assert.True(LowerThirdGallery.FillCredits(overlay, song));
        string Field(string name) => overlay.Objects.Single(o => o.Name == name).Text;
        Assert.Equal("Cornerstone", Field("Song Title"));
        Assert.Equal("Words and music by Edward Mote, Eric Liljero", Field("Writers"));
        Assert.Equal("CCLI Song # 6158927", Field("CCLI Song"));
        Assert.Equal("CCLI License # 11223344", Field("CCLI Licence")); // the church's own number isn't in a song
        Assert.False(LowerThirdGallery.FillCredits(overlay, song)); // nothing more to change
    }

    [Fact]
    public void CountdownsFollowTheTimerTheyArePointedAt()
    {
        var overlay = LowerThirdGallery.Instantiate(Template("gallery.countdown.corner"));
        var digits = overlay.Objects.Single(o => o.TextLink is not null);
        Assert.Equal(TextSourceKind.Timer, digits.TextLink!.Source);
        Assert.Null(digits.TextLink.TimerId);
        LowerThirdGallery.UseTimer(overlay, "timer-1");
        Assert.Equal("timer-1", digits.TextLink.TimerId);
        LowerThirdGallery.UseTimer(overlay, "");
        Assert.Null(digits.TextLink.TimerId);
        Assert.Contains(LowerThirdGallery.All, t => t.Make().Objects.Any(o => o.TextLink?.Source == TextSourceKind.Clock));
    }

    [Fact]
    public void AWordByWordNameFollowsTheNameTypedIn()
    {
        var overlay = LowerThirdGallery.Instantiate(Template("gallery.speaker.glass"));
        var name = overlay.Objects.Single(o => o.Name == "Name");
        Assert.Equal(3, name.AnimationSteps!.Count(s => s.Kind == AnimationKind.In)); // "Pastor Ade Johnson"
        name.Text = "Grace Okafor";
        Assert.True(KineticText.Reflow(overlay));
        Assert.Equal(["Grace", "Okafor"], name.AnimationSteps!.Where(s => s.Kind == AnimationKind.In).Select(s => AnimationRanges.Text(s.Ranges!.Single(), name.Text)));
        Assert.Empty(AnimationEditing.OverlayWarnings(overlay));
    }
}
