using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.MotionTemplates;
using MxU.Slides.Core.Scene;
using MxU.Slides.Core.Tests.Library;

namespace MxU.Slides.Core.Tests.MotionTemplates;

// Motion templates in the library (Phase 11): the catalogue and the animations live in the
// Windows extras; each use is an ordinary shared document a Mac can open (a still overlay, or a
// video), with the Windows part beside it, fingerprinted so a Mac edit wins.
public sealed class MotionTemplateStoreTests : ScratchFolderTest
{
    private static LottieDocument Strap() => LottieDocument.Parse(LottieSamples.Strap);

    [Fact]
    public void ImportedTemplatesAreListedAndTheSameAnimationIsntListedTwice()
    {
        var store = new MotionTemplateStore(NewLibrary());
        var first = store.Add(Strap(), "Speaker Strap", "strap.json");
        var again = store.Add(Strap(), "Speaker Strap (copy)", "strap-2.json");
        Assert.Equal(first.Id, again.Id);
        var listed = Assert.Single(store.All());
        Assert.Equal(("Speaker Strap", "strap.json", 1920.0, 30.0, 5.0), (listed.Name, listed.SourceFileName, listed.Width, listed.FrameRate, listed.Seconds));
        Assert.Equal(MotionUse.Overlay, listed.SuggestedUse);
        // It lives in the Windows extras, where a Mac never looks.
        Assert.True(File.Exists(Path.Combine(store.Library.Root, WindowsExtras.FolderName, MotionTemplateStore.CatalogueKind, first.Id + ".json")));
    }

    [Fact]
    public void TheAnimationIsKeptByItsHashAndReadsBackAfterARestart()
    {
        var library = NewLibrary();
        var hash = new MotionTemplateStore(library).Add(Strap(), "Strap").LottieHash;
        Assert.Matches("^[0-9a-f]{64}$", hash);
        var reopened = new MotionTemplateStore(NewLibrary());
        var document = reopened.Lottie(hash);
        Assert.NotNull(document);
        Assert.Equal(Strap().Layers, document.Layers);
        Assert.Null(reopened.Lottie("0000"));
    }

    [Fact]
    public void TheStartersAreSeededOnceAndRefreshedWhenTheyChange()
    {
        var store = new MotionTemplateStore(NewLibrary());
        StarterMotionTemplates.Seed(store);
        StarterMotionTemplates.Seed(store);
        var starters = store.All();
        Assert.Equal(["Name Strap", "Scripture Bar", "Title Card"], starters.Select(s => s.Name).Order());
        Assert.All(starters, s => Assert.True(s.Starter));
        Assert.Equal(MotionUse.Background, starters.Single(s => s.Name == "Title Card").SuggestedUse);
        // A user's own import sorts after them.
        store.Add(Strap(), "A Strap");
        Assert.Equal("A Strap", store.All()[^1].Name);
        // A starter that has been changed (say by an older app) is put back.
        store.Library.Extras.Save(MotionTemplateStore.CatalogueKind, "starter.name-strap", starters[0] with { LottieHash = "old" });
        StarterMotionTemplates.Seed(store);
        Assert.NotEqual("old", store.Get("starter.name-strap")!.LottieHash);
    }

    private static MotionTimeline Timeline() => MotionTimeline.For(Strap(), MotionPlayback.Loop);

    /// <summary>A lower-third use as the app makes one: its still saved, the overlay saved, then the use.</summary>
    private (LibraryStore Library, MotionTemplateStore Store, Overlay Overlay, MotionTemplateUse Use) LowerThird()
    {
        var library = NewLibrary();
        var store = new MotionTemplateStore(library);
        var info = store.Add(Strap(), "Speaker Strap");
        library.Save(Media("still-1", new string('a', 64), "Speaker Strap (still)", MediaKind.Image));
        var overlay = MotionTemplateStore.FallbackOverlay("Pastor Grace", "still-1", Timeline(), MotionTemplateStore.Fit(1920, 1080, SceneSize.Hd));
        library.Save(overlay);
        var use = new MotionTemplateUse
        {
            TemplateId = info.Id, LottieHash = info.LottieHash, Use = MotionUse.Overlay, FallbackMediaId = "still-1",
            Values = new() { ["text:L0"] = "Pastor Grace Okafor" },
        };
        store.SaveUse(overlay, use);
        return (library, store, overlay, use);
    }

    [Fact]
    public void ALowerThirdIsAnOverlayAMacCanReadShowingTheStill()
    {
        var (library, _, overlay, _) = LowerThird();
        Assert.Equal("Motion Templates/Lower Thirds", overlay.Folder);
        var picture = Assert.Single(overlay.Objects);
        Assert.Equal((SlideObjectKind.Media, "still-1", MediaScaleMode.Fit), (picture.ObjectKind, picture.MediaId, picture.MediaScaleMode));
        Assert.Equal((0.0, 0.0, 1920.0, 1080.0), (picture.X, picture.Y, picture.Width, picture.Height));
        // The still arrives and leaves gently on a Mac: a fade over the In (0.67 s) and over the Out (1 s).
        Assert.Equal([(AnimationKind.In, StepAnimation.Fade, AnimationTrigger.WithPrevious, 0.67), (AnimationKind.Out, StepAnimation.Fade, AnimationTrigger.OnDismiss, 1.0)],
            picture.AnimationSteps!.Select(s => (s.Kind, s.Animation, s.Trigger, s.DurationSeconds)));

        // Every field in the saved file is one the shared schema has, so the Mac's strict reader takes it.
        var saved = JsonNode.Parse(File.ReadAllText(library.DocumentPath(typeof(Overlay), overlay.Id)))!;
        AssertOnlySchemaFields(saved, typeof(Overlay));
        Assert.DoesNotContain("lottie", saved.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        // And it reads back through the generated models (whose enums refuse unknown values).
        var back = MxuJson.Deserialize<Overlay>(File.ReadAllBytes(library.DocumentPath(typeof(Overlay), overlay.Id)));
        Assert.Equal(overlay.Objects[0].MediaId, back.Objects[0].MediaId);
    }

    [Fact]
    public void TheUseIsFoundUntilAMacChangesTheOverlay()
    {
        var (library, store, overlay, use) = LowerThird();
        Assert.True(MxuJson.StructurallyEqual(use, store.UseOf(overlay)));
        // A Mac moves the still: its edit wins, and Windows shows the overlay as the Mac left it.
        var moved = MxuJson.Clone(overlay);
        moved.Objects[0].Y = 40;
        Assert.Null(store.UseOf(moved));
        // So does a Mac giving the still another picture (same item, new file).
        var still = library.Get<MediaItem>("still-1")!;
        still.FileHash = new string('b', 64);
        library.Save(still);
        Assert.Null(store.UseOf(overlay));
        // Windows re-saving the use after making a new still (as the app does) finds it again.
        store.SaveUse(overlay, use);
        Assert.NotNull(store.UseOf(overlay));
    }

    [Fact]
    public void ABackgroundIsAVideoAndItsUseFollowsItsFile()
    {
        var library = NewLibrary();
        var store = new MotionTemplateStore(library);
        var video = Media("video-1", new string('c', 64), "Title Card");
        library.Save(video);
        var use = new MotionTemplateUse { LottieHash = "h", Use = MotionUse.Background, FallbackMediaId = "video-1" };
        store.SaveUse(video, use);
        Assert.NotNull(store.UseOf(video));
        // An overlay use isn't mistaken for a background one, and vice versa.
        Assert.Null(store.UseOf(new Overlay { Id = "video-1", Name = "x", Objects = [] }));
        video.FileHash = new string('d', 64);
        Assert.Null(store.UseOf(video));
        store.ForgetUse("video-1");
        video.FileHash = new string('c', 64);
        Assert.Null(store.UseOf(video));
    }

    [Fact]
    public void TheLiveCopyShowsTheAnimationAndHoldsForItsOut()
    {
        var (_, _, overlay, use) = LowerThird();
        overlay.Objects.Add(new SlideObject { Id = "note", ObjectKind = SlideObjectKind.Text, Name = "Note", Text = "added on a Mac" });
        var live = MotionTemplateStore.LiveCopy(overlay, use, "motion::abc", Timeline());
        Assert.Equal("motion::abc", live.Objects[0].MediaId);
        var step = Assert.Single(live.Objects[0].AnimationSteps!);
        Assert.Equal((AnimationKind.Out, AnimationTrigger.OnDismiss, 0.95, 0.05), (step.Kind, step.Trigger, step.DelaySeconds, step.DurationSeconds));
        // So the show keeps it up exactly as long as the template's Out (ShowState.SweepFinishedExits waits for it).
        Assert.Equal(1.0, AnimationSequence.ExitDuration(live.Objects, live.AnimationOrder), 6);
        Assert.DoesNotContain(AnimationSequence.Groups(live.Objects, live.AnimationOrder).Auto, e => e.ObjectId == live.Objects[0].Id);
        // Anything else on the overlay is untouched, and the shared overlay is unchanged.
        Assert.Equal("added on a Mac", live.Objects[1].Text);
        Assert.Equal("still-1", overlay.Objects[0].MediaId);
        Assert.Equal(2, overlay.Objects[0].AnimationSteps!.Count);
    }

    [Fact]
    public void ATemplateWithoutAnOutFadesWhenTakenDown()
    {
        var builder = new LottieBuilder("Plain", 1920, 1080, 30, 60);
        builder.Layer(builder.ShapeLayer("Box", LottieBuilder.Transform(LottieBuilder.Static(0, 0, 0)), LottieBuilder.Group("Box", LottieBuilder.Rect(0, 0, 10, 10))));
        var timeline = MotionTimeline.For(builder.Document(), MotionPlayback.HoldLast);
        var step = MotionTemplateStore.HoldThenGo(timeline);
        Assert.Equal((null, MotionTimeline.FadeSeconds), (step.DelaySeconds, step.DurationSeconds));
    }

    [Fact]
    public void TheFallbackIsRemadeWhenTheValuesChange()
    {
        var use = new MotionTemplateUse { LottieHash = "h", Use = MotionUse.Overlay, FallbackMediaId = "m", Values = new() { ["text:L0"] = "A" } };
        var stamped = use with { FallbackStamp = MotionTemplateStore.FallbackStamp(use) };
        Assert.False(MotionTemplateStore.FallbackIsStale(stamped));
        Assert.True(MotionTemplateStore.FallbackIsStale(stamped with { Values = new() { ["text:L0"] = "B" } }));
        Assert.True(MotionTemplateStore.FallbackIsStale(stamped with { Playback = MotionPlayback.Once }));
        // The order values were typed in doesn't matter.
        var two = use with { Values = new() { ["a"] = "1", ["b"] = "2" } };
        Assert.Equal(MotionTemplateStore.FallbackStamp(two), MotionTemplateStore.FallbackStamp(two with { Values = new() { ["b"] = "2", ["a"] = "1" } }));
    }

    [Fact]
    public void AnAnimationIsPlacedAsBigAsFitsCentred()
    {
        Assert.Equal(new SceneRect(0, 0, 1920, 1080), MotionTemplateStore.Fit(1280, 720, SceneSize.Hd));
        Assert.Equal(new SceneRect(420, 0, 1080, 1080), MotionTemplateStore.Fit(500, 500, SceneSize.Hd));
    }

    [Fact]
    public void AFullFrameSolidSuggestsABackground()
    {
        Assert.Equal(MotionUse.Background, MotionTemplateStore.SuggestUse(StarterMotionTemplates.TitleCard()));
        Assert.Equal(MotionUse.Overlay, MotionTemplateStore.SuggestUse(StarterMotionTemplates.NameStrap()));
    }

    /// <summary>Fails if the JSON has a field (at any depth) that the generated model <paramref name="type"/> doesn't declare.</summary>
    private static void AssertOnlySchemaFields(JsonNode? node, Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        switch (node)
        {
            case JsonArray array:
                var element = type.IsGenericType ? type.GetGenericArguments()[0] : type.GetElementType() ?? typeof(object);
                foreach (var item in array) AssertOnlySchemaFields(item, element);
                break;
            case JsonObject obj when type.Namespace == typeof(Overlay).Namespace && type.IsClass:
                var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Select(p => (Name: p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name, p.PropertyType))
                    .Where(p => p.Name is not null).ToDictionary(p => p.Name!, p => p.PropertyType);
                foreach (var (name, value) in obj)
                {
                    Assert.True(properties.TryGetValue(name, out var propertyType), $"{type.Name} has no field \"{name}\"");
                    AssertOnlySchemaFields(value, propertyType);
                }
                break;
        }
    }
}
