using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Tests.Library;

// Port of starterPacksAreConsistentDocuments (PresenterCoreTests/AnimationPresetTests.swift), run
// over the Mac's four packs and the four worship looks Windows adds, plus the Windows seeding rules.
public class StarterPackTests : ScratchFolderTest
{
    private static readonly string[] BundledFamilies = ["Poppins", "Montserrat", "BebasNeue", "DMSerifDisplay"];

    private static void CheckOrder(IReadOnlyList<SlideObject> objects, IReadOnlyList<string>? order, string label)
    {
        var ids = objects.SelectMany(o => (o.AnimationSteps ?? []).Select(s => s.Id)).ToList();
        Assert.True(ids.Count == ids.Distinct().Count(), $"{label}: step ids unique");
        if (order is null) return;
        Assert.True(order.ToHashSet().SetEquals(ids), $"{label}: animationOrder names every step exactly");
        Assert.True(order.Count == ids.Count, $"{label}: no duplicates in animationOrder");
    }

    [Theory]
    [MemberData(nameof(Packs))]
    public void StarterPacksAreConsistentDocuments(StarterPack pack)
    {
        var theme = pack.MakeTheme();
        Assert.Equal(pack.ThemeId(), theme.Id);
        Assert.Equal(pack.Title(), theme.Name);
        var slides = theme.Slides!;
        var names = slides.Select(s => s.Name).ToHashSet();
        Assert.Superset(new HashSet<string> { "Points", "Verse + Reference", "Name + Title", "Point (Side Third)", "Lyrics", "Lyrics (Lower Third)", "Lyrics (Side Third)" }, names);
        Assert.Equal("Lyrics", slides[0].Name);
        foreach (var folder in new[] { "Lower Thirds", "Side Thirds" })
        {
            var run = slides.Where(s => s.Folder == folder).ToList();
            Assert.False(run[0].Name.StartsWith("Lyrics", StringComparison.Ordinal), $"{pack}/{folder}: the generic default still leads");
            Assert.Contains(run, s => s.Name.StartsWith("Lyrics", StringComparison.Ordinal));
        }
        Assert.Equal(new HashSet<string> { "Full Slide", "Lower Thirds", "Side Thirds" }, slides.Select(s => s.Folder).OfType<string>().ToHashSet());
        Assert.Equal(slides.Count, names.Count); // theme slide names stay unique (themeSlideName matches by name)
        foreach (var slide in slides) CheckOrder(slide.Objects, slide.AnimationOrder, $"{pack}/{slide.Name}");

        var overlays = pack.MakeOverlays();
        Assert.Contains("Verse + Reference", overlays.Select(o => o.Name));
        Assert.Contains("Point", overlays.Select(o => o.Name));
        Assert.Equal(new[] { "Lower Thirds", "Side Thirds", "Full Screen" }.Select(f => $"{pack.Title()}/{f}").ToHashSet(), overlays.Select(o => o.Folder!).ToHashSet());
        Assert.Equal(StarterPacks.OverlayItems.Length, overlays.Count);
        Assert.Equal(overlays.Count, overlays.Select(o => o.Id).Distinct().Count());
        foreach (var overlay in overlays)
        {
            CheckOrder(overlay.Objects, overlay.AnimationOrder, $"{pack}/{overlay.Name}");
            var steps = overlay.Objects.SelectMany(o => o.AnimationSteps ?? []).ToList();
            Assert.True(steps.Any(s => s.Kind == AnimationKind.In) && steps.Any(s => s.Kind == AnimationKind.Out), $"{overlay.Name} has In + Out");
            foreach (var o in overlay.Objects)
            {
                var own = o.AnimationSteps ?? [];
                if (o.ObjectKind == SlideObjectKind.Text)
                    Assert.True(own.All(s => s.Animation != StepAnimation.Move || s.Edge is null), $"{overlay.Name}/{o.Name} text floats in place");
                if (own.Any(s => s.Kind == AnimationKind.In))
                    Assert.True(own.Any(s => s.Kind == AnimationKind.Out), $"{overlay.Name}/{o.Name} leaves too");
            }
            // An overlay never gets a click, so nothing but an Out may wait for one.
            var warnings = AnimationEditing.OverlayWarnings(overlay);
            Assert.True(warnings.Count == 0, $"{pack}/{overlay.Name}: " + string.Join("; ", warnings.Select(w => $"{w.Key}: {w.Value}")));
        }
        Assert.Contains(overlays.Single(o => o.Name == "Ticker").Objects, o => o.TextStyle?.Scroll?.Axis == BlockScrollAxis.Left);
        var timed = overlays.Single(o => o.Name == "Name + Title (timed)").Objects.SelectMany(o => o.AnimationSteps ?? []);
        Assert.Contains(timed, s => s.Kind == AnimationKind.Out && s.Trigger == AnimationTrigger.AfterPrevious && s.DelaySeconds == 6);
        foreach (var name in new[] { "Point (Side Third)", "Verse + Reference (Side Third)" })
        {
            var pushes = overlays.Single(o => o.Name == name).Objects.SelectMany(o => o.AnimationSteps ?? []).Where(s => s.VideoPush is not null).ToList();
            Assert.True(pushes.Count == 2 && pushes.Select(s => s.Kind).ToHashSet().SetEquals([AnimationKind.In, AnimationKind.Out]), $"{pack}/{name}");
            Assert.All(pushes, s => Assert.True(s.VideoPush!.Backdrop));
        }
        Assert.Contains(overlays.Single(o => o.Name == "Search").Objects.SelectMany(o => o.AnimationSteps ?? []),
            s => s.VideoPush?.Mode == VideoPushMode.BlurBackground && s.Kind == AnimationKind.In);
        Assert.All(overlays.Single(o => o.Name == "Verse + Reference").Objects.SelectMany(o => o.AnimationSteps ?? []), s => Assert.Null(s.VideoPush)); // lower thirds don't push
    }

    public static TheoryData<StarterPack> Packs() => [.. StarterPacks.All];

    [Fact]
    public void ThePacksAreDistinct()
    {
        Assert.Equal(8, StarterPacks.All.Length);
        Assert.Equal(8, StarterPacks.All.Select(p => p.ThemeId()).Distinct().Count());
        Assert.Equal(8, StarterPacks.All.Select(p => p.MakeTheme().FontFamily).Distinct().Count());
        Assert.Equal(["cleanGeometric", "editorialSerif", "boldBlocks", "digital"], StarterPacks.All.Where(p => !p.IsWorshipLook()).Select(p => p.Raw()));
    }

    [Fact]
    public void TheWorshipLooksUseOnlyTheBundledFonts()
    {
        foreach (var pack in StarterPacks.All.Where(p => p.IsWorshipLook()))
        {
            var fonts = FontSharing.Names(pack.MakeTheme()).Concat(pack.MakeOverlays().SelectMany(FontSharing.Names)).ToHashSet();
            Assert.All(fonts, f => Assert.True(BundledFamilies.Any(f.StartsWith), $"{pack}: {f} isn't a bundled font"));
            // Each font is a PostScript name the bundled files really have (so the Mac's Core Text finds the same face).
            var faces = BundledFonts.Files(BundledFonts.ShippedFolder).SelectMany(f => FontFiles.Faces(File.ReadAllBytes(f))).Select(f => f.PostScriptName).ToHashSet();
            Assert.All(fonts.Where(f => f.Contains('-')), f => Assert.Contains(f, faces));
        }
    }

    [Fact]
    public void EveryWorshipLookHasACountdownLinkedToATimer()
    {
        foreach (var pack in StarterPacks.All.Where(p => p.IsWorshipLook()))
        {
            var countdown = pack.MakeTheme().Slides!.Single(s => s.Name == "Countdown");
            Assert.Equal("Full Slide", countdown.Folder);
            var digits = countdown.Objects.Single(o => o.TextLink is not null);
            Assert.Equal(TextSourceKind.Timer, digits.TextLink!.Source);
            Assert.Null(digits.TextLink.TimerId); // the running timer, or the first
            Assert.Equal("MM:ss", digits.TextLink.TimerPattern);
            Assert.True(digits.TextStyle?.TabularFigures);
            Assert.Equal("BebasNeue-Regular", digits.TextStyle?.FontName); // its figures are all one width, so the clock never shifts
            Assert.Contains(countdown.Objects, o => o.Name == "Kicker");
            Assert.Contains(countdown.Objects, o => o.Name == "Message");
        }
    }

    [Fact]
    public void TheWorshipLooksAskForKineticLyrics()
    {
        foreach (var pack in StarterPacks.All)
        {
            var builds = pack.KineticBuilds();
            if (!pack.IsWorshipLook())
            {
                Assert.Empty(builds);
                continue;
            }
            Assert.NotEmpty(builds);
            var theme = pack.MakeTheme();
            var lyrics = theme.Slides!.Single(s => s.Name == "Lyrics");
            var (placeholder, build) = builds.Single(b => b.Placeholder.Id == lyrics.Objects[0].Id);
            Assert.Equal(KineticSplit.Lines, build.Split);
            Assert.True(build.Stagger > 0);
            // The build was made against the theme exactly as saved, so its basis matches.
            Assert.Equal(KineticExtras.BasisOf(lyrics.Objects[0]), KineticExtras.BasisOf(placeholder));
            Assert.All(build.In, s => Assert.Null(s.Ranges));
        }
    }

    [Fact]
    public void AnExistingLibraryGainsTheNewLooksAndKeepsItsEdits()
    {
        var library = NewLibrary();
        LibrarySeeder.Seed(library, new DateOnly(2026, 10, 25));
        Assert.Equal(8, StarterPacks.All.Count(p => library.Get<Theme>(p.ThemeId()) is not null));
        Assert.NotNull(library.Get<Presentation>(StarterContent.CountdownPresentationId));

        // An edit to a starter overlay, and an older library without the worship looks.
        var name = library.Get<Overlay>(StarterPack.CleanGeometric.OverlayId("name"))!;
        name.Objects[0].Fill!.ColorHex = "#FF0000FF";
        library.Save(name);
        library.Delete<Theme>(StarterPack.Arena.ThemeId());
        library.Delete<Presentation>(StarterContent.CountdownPresentationId);

        var reopened = NewLibrary();
        LibrarySeeder.RefreshStarterPacks(reopened);
        Assert.NotNull(reopened.Get<Theme>(StarterPack.Arena.ThemeId())); // the new look came back
        Assert.Equal("#FF0000FF", reopened.Get<Overlay>(StarterPack.CleanGeometric.OverlayId("name"))!.Objects[0].Fill!.ColorHex); // the edit stayed
        Assert.Null(reopened.Get<Presentation>(StarterContent.CountdownPresentationId)); // a deleted countdown deck stays deleted

        // Windows' kinetic lyrics are saved beside the theme, not in it.
        var lyric = reopened.Get<Theme>(StarterPack.Arena.ThemeId())!.Slides!.Single(s => s.Name == "Lyrics").Objects[0];
        Assert.Equal(KineticSplit.Lines, new KineticExtras(reopened.Extras).For(lyric)?.Split);
        Assert.DoesNotContain("kinetic", File.ReadAllText(reopened.DocumentPath(typeof(Theme), StarterPack.Arena.ThemeId())));
    }

    [Fact]
    public void TheCountdownDeckDrawsItsDigitsFromTheDesign()
    {
        var deck = StarterContent.CountdownPresentation();
        Assert.Equal(4, deck.Slides.Count);
        foreach (var slide in deck.Slides)
        {
            var theme = StarterPacks.All.Single(p => p.ThemeId() == slide.ThemeId).MakeTheme();
            var design = SlideSceneBuilder.ThemeSlide(slide, theme)!;
            Assert.Equal("Countdown", design.Name);
            var stack = SlideSceneBuilder.ComposedStack(slide.Objects, design);
            Assert.Contains(stack, e => e.FromTheme && e.Object.TextLink?.Source == TextSourceKind.Timer); // the digits, from the design
            Assert.Contains(stack, e => !e.FromTheme && e.Object.Name == "Kicker");
            Assert.Contains(stack, e => !e.FromTheme && e.Object.Name == "Message");
        }
    }
}
