using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Library;

// Windows-only transitions on shared documents (Phase 6): the document keeps a Mac-readable
// fallback, the Windows kind waits beside the library, and a Mac edit always wins.
public sealed class TransitionExtrasTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "TransitionExtras-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private TransitionExtras Extras() => new(new WindowsExtras(root));

    private static readonly WindowsTransition Push = new() { Kind = WindowsTransitionKind.Push, DurationSeconds = 0.8, Direction = TransitionDirection.Up };

    private static Slide NewSlide(string id = "slide-1") => new() { Id = id, Name = "Verse" };

    [Fact]
    public void AWindowsKindLeavesAMacReadableFallbackInTheDocument()
    {
        var extras = Extras();
        var slide = NewSlide();
        extras.Choose(slide, Push);
        Assert.Equal(TransitionKind.Dissolve, slide.Transition!.TransitionKind); // the nearest Mac kind…
        Assert.Equal(0.8, slide.Transition.DurationSeconds); // …at the same length
        Assert.Equal(Push, extras.ForSlide(slide));
        Assert.Equal(Push, extras.ChosenFor(slide));
        Assert.True(File.Exists(Path.Combine(root, WindowsExtras.FolderName, TransitionExtras.SlideKind, "slide-1.json")));
        // The document itself never names a Windows kind.
        var json = MxuJson.Serialize(new Presentation { Id = "p", Name = "Song", Slides = [slide] });
        Assert.Contains("\"dissolve\"", json);
        Assert.DoesNotContain("push", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AMacEditWins()
    {
        var extras = Extras();
        var slide = NewSlide();
        extras.Choose(slide, Push);
        // A Mac changes the slide's transition (the document syncs back with a Fade Black).
        slide.Transition = new Transition { TransitionKind = TransitionKind.FadeBlack, DurationSeconds = 0.8 };
        Assert.Null(extras.ForSlide(slide));
        Assert.Equal(WindowsTransitionKind.FadeBlack, extras.ChosenFor(slide)!.Kind);
        // Even a change of length alone is a Mac edit.
        slide.Transition = new Transition { TransitionKind = TransitionKind.Dissolve, DurationSeconds = 1.2 };
        Assert.Null(extras.ForSlide(slide));
        // And a Mac setting it back to the default leaves the slide with none of its own.
        slide.Transition = null;
        Assert.Null(extras.ForSlide(slide));
        Assert.Null(extras.ChosenFor(slide));
    }

    [Fact]
    public void AReadDocumentWithTheSameFallbackStillGetsItsWindowsKind()
    {
        var extras = Extras();
        var slide = NewSlide();
        extras.Choose(slide, Push);
        // The document is saved and read back (a fresh object, as after a restart or a sync).
        var reread = MxuJson.Deserialize<Slide>(MxuJson.Serialize(slide));
        Assert.Equal(Push, Extras().ForSlide(reread));
    }

    [Fact]
    public void AMissingExtraMeansTheSharedKind()
    {
        var extras = Extras();
        var slide = NewSlide();
        slide.Transition = new Transition { TransitionKind = TransitionKind.BlurDissolve, DurationSeconds = 2 };
        Assert.Null(extras.ForSlide(slide));
        var chosen = extras.ChosenFor(slide)!;
        Assert.Equal((WindowsTransitionKind.BlurDissolve, 2.0), (chosen.Kind, chosen.DurationSeconds));
        Assert.Null(extras.ChosenFor(NewSlide("plain"))); // none of its own: it follows its presentation
    }

    [Fact]
    public void ChoosingAMacKindOrTheDefaultRemovesTheExtra()
    {
        var extras = Extras();
        var slide = NewSlide();
        extras.Choose(slide, Push);
        extras.Choose(slide, new WindowsTransition { Kind = WindowsTransitionKind.FadeWhite, DurationSeconds = 1 });
        Assert.Equal(TransitionKind.FadeWhite, slide.Transition!.TransitionKind);
        Assert.Empty(extras.Store.Owners(TransitionExtras.SlideKind));
        extras.Choose(slide, Push);
        extras.Choose(slide, null);
        Assert.Null(slide.Transition);
        Assert.Empty(extras.Store.Owners(TransitionExtras.SlideKind));
    }

    [Fact]
    public void PresentationsAndMediaItemsKeepTheirsSeparately()
    {
        var extras = Extras();
        var cube = new WindowsTransition { Kind = WindowsTransitionKind.Cube, DurationSeconds = 1 };
        var iris = new WindowsTransition { Kind = WindowsTransitionKind.Iris, DurationSeconds = 0.6, Softness = 0.8 };
        // The same id on a slide, a presentation and a media item never collide.
        var presentation = new Presentation { Id = "same", Name = "Song" };
        var media = new MediaItem { Id = "same", Name = "Loop" };
        extras.Choose(presentation, cube);
        extras.Choose(media, iris);
        Assert.Equal(cube, extras.ForPresentation(presentation));
        Assert.Equal(iris, extras.ForMedia(media));
        Assert.Null(extras.ForSlide(new Slide { Id = "same", Transition = presentation.Transition }));
        Assert.Equal(TransitionKind.Dissolve, presentation.Transition!.TransitionKind);
        Assert.Equal(TransitionKind.Dissolve, media.Transition!.TransitionKind);
    }

    [Fact]
    public void AnswersAreRememberedUntilAnExtraChanges()
    {
        var extras = Extras();
        var slide = NewSlide();
        extras.Choose(slide, Push);
        Assert.Equal(Push, extras.ForSlide(slide));
        var other = Push with { Direction = TransitionDirection.Left };
        extras.Choose(slide, other); // same fallback, so the same fingerprint: the remembered answer must go
        Assert.Equal(other, extras.ForSlide(slide));
    }

    [Fact]
    public void ADuplicateKeepsItsWindowsTransition()
    {
        var extras = Extras();
        var slide = NewSlide();
        extras.Choose(slide, Push);
        var copy = MxuJson.Clone(slide);
        copy.Id = "slide-2";
        extras.Copied(slide, copy);
        Assert.Equal(Push, extras.ForSlide(copy));

        var cube = new WindowsTransition { Kind = WindowsTransitionKind.Cube };
        var deck = new Presentation { Id = "deck", Name = "Song", Slides = [slide] };
        extras.Choose(deck, cube);
        var deckCopy = MxuJson.Clone(deck);
        deckCopy.Id = "deck-copy";
        deckCopy.Slides[0].Id = "slide-3";
        extras.Copied(deck, deckCopy);
        Assert.Equal(cube, extras.ForPresentation(deckCopy));
        Assert.Equal(Push, extras.ForSlide(deckCopy.Slides[0]));
    }

    [Fact]
    public void EditedLyricsKeepTheirSlidesWindowsTransitions()
    {
        var extras = Extras();
        var iris = new WindowsTransition { Kind = WindowsTransitionKind.Iris, DurationSeconds = 1.2 };
        var before = new Presentation { Id = "song", Name = "Song", Slides = [NewSlide("v1"), NewSlide("v2"), NewSlide("c1")] };
        extras.Choose(before.Slides[0], Push);
        extras.Choose(before.Slides[2], iris);
        // The editor rebuilds the slides with new ids, carrying each one's transition by section (SongEditing.CarryOver).
        var after = new Presentation
        {
            Id = "song", Name = "Song",
            Slides =
            [
                new Slide { Id = "n1", Transition = before.Slides[0].Transition },
                new Slide { Id = "n2" },
                new Slide { Id = "n3", Transition = before.Slides[2].Transition },
            ],
        };
        extras.CarryOver(before, after);
        Assert.Equal(Push, extras.ForSlide(after.Slides[0]));
        Assert.Null(extras.ForSlide(after.Slides[1]));
        Assert.Equal(iris, extras.ForSlide(after.Slides[2]));
    }

    [Fact]
    public void AnExtraNamingAKindThisBuildDoesntKnowIsIgnored()
    {
        var extras = Extras();
        var slide = NewSlide();
        extras.Choose(slide, Push);
        var path = Path.Combine(root, WindowsExtras.FolderName, TransitionExtras.SlideKind, "slide-1.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"push\"", "\"hyperspace\"", StringComparison.Ordinal));
        Assert.Null(Extras().ForSlide(slide)); // a newer build wrote it: play the shared fallback
    }
}
