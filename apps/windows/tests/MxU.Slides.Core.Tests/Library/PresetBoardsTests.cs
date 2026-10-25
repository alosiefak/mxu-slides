using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Library;

// The boards people's own presets are saved on (the Mac's animationPresetBoardIsASingletonDocumentKind
// and defaultInOutSurviveReopenAndMerge in AnimationPresetTests.swift), and the Windows kinetic extras.
public class PresetBoardsTests : ScratchFolderTest
{
    private static AnimationStep Fade(string id, AnimationKind kind = AnimationKind.In) => new()
    {
        Id = id, Kind = kind, Animation = StepAnimation.Fade, Trigger = AnimationTrigger.OnClick, DurationSeconds = 0.5,
    };

    [Fact]
    public void TheAnimationPresetBoardIsOneDocumentWithTheMacsId()
    {
        var library = NewLibrary();
        Assert.Empty(PresetBoards.Animations(library).Presets);
        var mine = PresetBoards.SaveAnimation(library, "  Mine ", [Fade("x")]);
        Assert.Equal("Mine", mine.Name);
        var path = library.DocumentPath(typeof(AnimationPresetBoard), AnimationPresetBoard.WellKnownId);
        Assert.EndsWith(Path.Combine("AnimationPresets", "animation-preset-board.json"), path);

        var reopened = NewLibrary();
        var board = reopened.Get<AnimationPresetBoard>(AnimationPresetBoard.WellKnownId);
        Assert.Equal(["Mine"], board!.Presets.Select(p => p.Name));
        // The same JSON shape the Mac reads: id, presets, and each preset's steps.
        var json = File.ReadAllText(path);
        Assert.Contains("\"presets\"", json);
        Assert.Contains("\"steps\"", json);

        Assert.True(PresetBoards.OverwriteAnimation(reopened, mine.Id, [Fade("y"), Fade("z", AnimationKind.Out)]));
        Assert.Equal(2, PresetBoards.Animations(reopened).Presets.Single().Steps.Count);
        Assert.False(PresetBoards.OverwriteAnimation(reopened, "nope", []));
        Assert.True(PresetBoards.DeleteAnimation(reopened, mine.Id));
        Assert.Empty(PresetBoards.Animations(reopened).Presets);
    }

    [Fact]
    public void DefaultInAndOutSurviveReopening()
    {
        var library = NewLibrary();
        library.Save(new AnimationPresetBoard
        {
            Id = AnimationPresetBoard.WellKnownId, Presets = [],
            DefaultIn = new AnimationStep { Id = "", Kind = AnimationKind.In, Animation = StepAnimation.Wipe, Trigger = AnimationTrigger.OnClick, DurationSeconds = 0.3 },
        });
        var reopened = NewLibrary().Get<AnimationPresetBoard>(AnimationPresetBoard.WellKnownId)!;
        Assert.Equal(StepAnimation.Wipe, reopened.DefaultIn?.Animation);
        Assert.Equal(0.3, reopened.DefaultIn?.DurationSeconds);
        Assert.Null(reopened.DefaultOut); // absent = the app default (Fade · Normal)
        // Saving a preset keeps the defaults someone set on a Mac.
        PresetBoards.SaveAnimation(NewLibrary(), "Another", [Fade("a")]);
        Assert.Equal(StepAnimation.Wipe, NewLibrary().Get<AnimationPresetBoard>(AnimationPresetBoard.WellKnownId)!.DefaultIn?.Animation);
    }

    [Fact]
    public void EffectPresetsSaveOnTheirOwnBoard()
    {
        var library = NewLibrary();
        var preset = PresetBoards.SaveEffect(library, "Haze", EffectPresetCatalog.Recommended.Single(p => p.Id == "builtin.heatHaze").Effects);
        Assert.EndsWith(Path.Combine("EffectPresets", "effect-presets.json"), library.DocumentPath(typeof(EffectPresetBoard), EffectPresetBoard.WellKnownId));
        Assert.Equal(EffectKind.Warp, NewLibrary().Get<EffectPresetBoard>(EffectPresetBoard.WellKnownId)!.Presets.Single().Effects.Single().EffectKind);
        Assert.True(PresetBoards.DeleteEffect(library, preset.Id));
        Assert.False(PresetBoards.DeleteEffect(library, preset.Id));
    }

    [Fact]
    public void AKineticBuildIsIgnoredOnceItsThemeChangesElsewhere()
    {
        var library = NewLibrary();
        var kinetic = new KineticExtras(library.Extras);
        var placeholder = new SlideObject
        {
            Id = "pack.x.theme.lyrics.text", ObjectKind = SlideObjectKind.Text, Name = "Lyrics", Text = "Sample",
            AnimationSteps = [Fade("in")],
        };
        var build = new KineticBuild(KineticSplit.Lines, 0.2, [Fade("tpl")]);
        Assert.Null(kinetic.For(placeholder));
        Assert.Equal(1, kinetic.Seed([(placeholder, build)]));
        Assert.Equal(0, kinetic.Seed([(placeholder, build)])); // already there
        Assert.Equal(KineticSplit.Lines, kinetic.For(placeholder)?.Split);
        Assert.Equal(0.2, new KineticExtras(NewLibrary().Extras).For(placeholder)?.Stagger); // read back from disk

        // Someone on a Mac picks another In for the lyrics: Windows shows their change, not the build.
        var edited = MxuJson.Clone(placeholder);
        edited.AnimationSteps![0].Animation = StepAnimation.Blur;
        Assert.Null(kinetic.For(edited));
        Assert.Null(kinetic.For(new SlideObject { Id = placeholder.Id, ObjectKind = SlideObjectKind.Shape, Name = "", Text = "", AnimationSteps = [Fade("in")] }));

        // A newer build of the app with a different stagger replaces the saved one.
        Assert.Equal(1, kinetic.Seed([(placeholder, build with { Stagger = 0.3 })]));
        Assert.Equal(0.3, kinetic.For(placeholder)?.Stagger);
    }
}
