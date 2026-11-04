using System.Windows;
using System.Windows.Controls;
using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Core.Stage;
using MxU.Slides.Windows.ViewModels;
using MxU.Slides.Windows.Views;

namespace MxU.Slides.Windows.Infrastructure;

// Phase 7's editors, driven through their own windows: the theme editor (a new theme, a design, a
// kinetic preset on its placeholder, the preview), Apply Theme (and the question about slides with a
// theme of their own), the media editor (trim, speed, volume, crop and zoom, effects, seen live), the
// Auto Advance sheets, effects on a slide object, the stage layout editor (boxes, notes, safe margin,
// live data) and the overlay editor's extras (a Change posed on the canvas, a custom start, camera
// push, the timeline, drag-and-drop). Each saves a picture: "e7-…".
public sealed partial class SelfTest
{
    private async Task Editors7Check()
    {
        var deck = Editors7Deck();
        var ask = model.AskReplaceOwnThemes;
        try
        {
            model.Library.Save(deck);
            var theme = await ThemeEditor7Check();
            if (theme is not null) await ApplyTheme7Check(deck, theme);
            await MediaEditor7Check();
            await AutoAdvance7Check(deck);
            await Effects7Check();
            await StageLayout7Check(deck);
            await OverlayExtras7Check();
        }
        finally
        {
            model.AskReplaceOwnThemes = ask;
            foreach (var open in Application.Current.Windows.OfType<Window>()
                         .Where(w => w is ThemeEditorWindow or MediaEditorWindow or AutoAdvanceWindow or ThemePickerWindow or StageLayoutEditorWindow or OverlayEditorWindow).ToList())
                open.Close();
            model.ClearAllCommand.Execute(null);
            model.IsEditing = false;
            if (model.Library.Get<Presentation>(deck.Id) is not null) model.Library.Delete<Presentation>(deck.Id);
        }
    }

    /// <summary>A small deck to restyle and time: two verses and a chorus, the first verse with notes for the stage.</summary>
    private static Presentation Editors7Deck()
    {
        static Slide Lyric(string id, string section, string text, string? notes = null) => new()
        {
            Id = id, Name = id, SectionId = section, Notes = notes,
            Objects = [new SlideObject { Id = $"{id}-t", ObjectKind = SlideObjectKind.Text, Name = "Lyrics", Text = text }],
        };
        return new Presentation
        {
            Id = "selftest-editors7", Name = "Self-test Editors",
            Sections = [new PresentationSection { Id = "v", Name = "Verse" }, new PresentationSection { Id = "c", Name = "Chorus" }],
            Slides =
            [
                Lyric("e7-v1", "v", "Great is thy faithfulness\nO God my Father", "Self-test note: slow down here"),
                Lyric("e7-v2", "v", "There is no shadow of turning\nWith thee"),
                Lyric("e7-c1", "c", "Morning by morning\nNew mercies I see"),
                Lyric("e7-c2", "c", "All I have needed\nThy hand hath provided"),
            ],
        };
    }

    // MARK: Theme editor

    private async Task<Theme?> ThemeEditor7Check()
    {
        var editor = ThemeEditorWindow.Open(window, model, null);
        var vm = editor.Model;
        await Delay(900);
        Save(editor, "e7-01-theme-editor-open");
        Check("the theme editor opens on a theme and its designs", vm.Current is not null && vm.Designs.Count > 0);

        vm.NewThemeCommand.Execute(null);
        var theme = vm.Current;
        Check("New Theme makes a theme with the Mac's four designs, saved", theme is { Slides.Count: 4 } && model.Library.Get<Theme>(theme.Id) is not null
            && vm.Designs.Select(d => d.Name).SequenceEqual(["Lyrics", "Bible", "Announcements", "Sermon Points"]));
        if (theme is null) return null;
        Check("renaming the theme saves it", vm.RenameTheme("Self-test Theme") && model.Library.Get<Theme>(theme.Id)?.Name == "Self-test Theme");

        vm.AddDesignCommand.Execute(null);
        Check("+ Design adds a design and shows it", theme.Slides!.Count == 5 && vm.Design is { } added && vm.Designs.Any(d => d.Name == added.Name));
        Check("a design can be renamed", vm.RenameDesign("Self-test Welcome", renameUses: false) && vm.Design?.Name == "Self-test Welcome");
        vm.DeleteDesignCommand.Execute(null);
        Check("…and deleted", theme.Slides!.Count == 4 && theme.Slides.All(d => d.Name != "Self-test Welcome"));

        vm.SelectedDesign = vm.Designs.First(d => d.Name == "Lyrics");
        var design = vm.Design!;
        var placeholder = design.Objects.First(o => o.ObjectKind == SlideObjectKind.Text);
        Check("the design has its placeholder", vm.Placeholders.Count == 1 && vm.Placeholders[0].Object == placeholder);
        vm.ShowingObjectTab = true;
        vm.SelectObjects([placeholder.Id]);
        if (vm.Inspector is { } inspector)
        {
            inspector.FontSize = "90";
            inspector.TextColor = "#FDE68AFF";
        }
        Check("the inspector styles the placeholder", placeholder.TextStyle?.FontSize == 90 && placeholder.TextStyle?.ColorHex == "#FDE68AFF");
        vm.DesignBackgroundColor = "#1E3A8AFF";
        Check("the design gets a background of its own", design.BackgroundFill is { FillKind: FillKind.Solid, ColorHex: "#1E3A8AFF" });
        vm.SampleText = "Self-test sample words\nOn a second line";
        Check("the sample slide shows the design with your words", vm.SampleScene is not null);
        await Delay(600);
        Save(editor, "e7-02-theme-editor-design");

        vm.ShowingAnimationTab = true;
        vm.Animation.SelectedPreset = vm.Animation.PresetChoices.FirstOrDefault(p => p.KineticRecipe is not null);
        vm.Animation.ApplyPresetCommand.Execute("replace");
        Check("a kinetic preset on the placeholder adds steps a Mac plays, and a Windows kinetic build",
            placeholder.AnimationSteps?.Any(s => s.Kind == AnimationKind.In) == true && vm.HasKinetic);
        Check("the timeline shows the design's steps", vm.Animation.Timeline is { } timeline && vm.Animation.StepRows.Count > 0 && timeline is not null);
        await Delay(600);
        Save(editor, "e7-03-theme-editor-animation");
        vm.PlayPreviewCommand.Execute(null);
        var previewing = editor.CanvasEditor.IsPreviewing;
        await Delay(500);
        Save(editor, "e7-04-theme-editor-preview");
        Check("Play previews the design's animation with the sample words", previewing && vm.PreviewDuration > 0.1);
        vm.StopPreviewCommand.Execute(null);
        if (vm.SampleScene is { } sample) await SnapshotAt("e7-05-theme-sample-midway", sample, Math.Min(0.6, vm.PreviewDuration / 2));

        vm.ShowingThemeTab = true;
        await Delay(500);
        Save(editor, "e7-06-theme-editor-theme");
        vm.Flush();
        var saved = model.Library.Get<Theme>(theme.Id)?.Design("Lyrics")?.Objects.FirstOrDefault(o => o.Id == placeholder.Id);
        Check("saving keeps the placeholder's look and its kinetic build", saved?.TextStyle?.FontSize == 90
            && saved is not null && new KineticExtras(model.Library.Extras).For(saved) is not null);
        editor.Close();
        return model.Library.Get<Theme>(theme.Id);
    }

    // MARK: Apply Theme

    private async Task ApplyTheme7Check(Presentation deck, Theme theme)
    {
        var starter = model.Library.All<Theme>().First(t => t.Id != theme.Id);
        var other = model.Library.All<Theme>().First(t => t.Id != theme.Id && t.Id != starter.Id);
        deck.ThemeId = starter.Id;
        model.Library.Save(deck);

        Check("Apply Theme gives a slide a theme of its own", model.ApplySlidesTheme(deck, ["e7-c1"], theme.Id, "Lyrics")
            && deck.Slides.First(s => s.Id == "e7-c1").ThemeId == theme.Id && deck.ThemeId == starter.Id);
        var picker = ThemePickerWindow.ForSlides(window, model, deck, ["e7-c1"]);
        if (picker is not null)
        {
            picker.Show();
            await Delay(700);
            Save(picker, "e7-07-apply-theme-to-slides");
            picker.Close();
        }

        var asked = 0;
        model.AskReplaceOwnThemes = (_, _, count) =>
        {
            asked = count;
            return false;
        };
        Check("a new deck theme first asks about slides with their own; Keep keeps theirs",
            model.ApplyDeckTheme(deck, other.Id) && asked == 1 && deck.ThemeId == other.Id && deck.Slides.First(s => s.Id == "e7-c1").ThemeId == theme.Id);
        model.AskReplaceOwnThemes = (_, _, _) => null;
        Check("…Cancel changes nothing", !model.ApplyDeckTheme(deck, starter.Id) && deck.ThemeId == other.Id);
        model.AskReplaceOwnThemes = (_, _, _) => true;
        Check("…and Replace restyles them too, as on a Mac", model.ApplyDeckTheme(deck, starter.Id) && deck.ThemeId == starter.Id
            && deck.Slides.All(s => string.IsNullOrEmpty(s.ThemeId)));
        Check("the deck is saved with its theme", model.Library.Get<Presentation>(deck.Id)?.ThemeId == starter.Id);
    }

    // MARK: Media editor

    private async Task MediaEditor7Check()
    {
        var video = model.Library.All<MediaItem>().FirstOrDefault(m => m.MediaKind == MediaKind.Video && model.Library.BlobPath(m) is not null);
        if (video is not null)
        {
            var editor = MediaEditorWindow.Open(window, model, video);
            if (editor is not null)
            {
                var vm = editor.Model;
                await WhenShown(() => vm.Duration > 0, 10);
                Check("the media editor opens the video and reads its length", vm.Duration > 0 && vm.IsVideo);
                if (vm.Duration > 0)
                {
                    vm.TrimmedTo((0.1, 0.8));
                    vm.PlayRate = vm.RateChoices.First(c => Math.Abs(c.Value - 1.5) < 1e-9);
                    vm.Volume = 50;
                    vm.Flush();
                    var stored = model.Library.Get<MediaItem>(video.Id);
                    Check("dragging the trim handles sets in and out points", stored?.InPoint is { } inPoint && Math.Abs(inPoint - vm.Duration * 0.1) < 0.2
                        && stored.OutPoint is { } outPoint && Math.Abs(outPoint - vm.Duration * 0.8) < 0.2);
                    Check("the speed is saved in the shared field", stored?.PlayRate == 1.5 && vm.PlaysText.StartsWith("Plays", StringComparison.Ordinal));
                    Check("the video's own volume is kept (Windows)", Math.Abs(model.MediaLookFor(video.Id).Volume - 0.5) < 1e-6);
                    vm.SeekTo(vm.Duration * 0.4);
                    await Delay(700);
                    Save(editor, "e7-08-media-editor-video");
                    vm.ClearInPointCommand.Execute(null);
                    vm.ClearOutPointCommand.Execute(null);
                    vm.PlayRate = vm.RateChoices.First(c => c.Value == 1);
                    vm.ResetLookCommand.Execute(null);
                    vm.Flush();
                    Check("clearing the trim and speed puts the video back", model.Library.Get<MediaItem>(video.Id) is { InPoint: null, OutPoint: null } back
                        && (back.PlayRate ?? 1) == 1 && model.MediaLookFor(video.Id).IsDefault);
                }
                editor.Close();
            }
        }
        else Skip("the media editor on a video", "no test video on this PC");

        var image = model.Library.All<MediaItem>().FirstOrDefault(m => m.MediaKind == MediaKind.Image && model.Library.BlobPath(m) is not null);
        if (image is null)
        {
            Skip("the media editor on a picture", "no picture in the library");
            return;
        }
        var pictureEditor = MediaEditorWindow.Open(window, model, image);
        if (pictureEditor is null) return;
        var picture = pictureEditor.Model;
        picture.FitIndex = 1;
        picture.Zoom = 80;
        picture.CropLeft = 10;
        picture.CropBottom = 20;
        picture.Effects.Add(EffectKind.ColorAdjust);
        if (picture.Effects.Chain.Count == 1)
            picture.Effects.SetValue(0, EffectEditing.Dials(EffectKind.ColorAdjust).First(d => d.Field == EffectField.Saturation), 0);
        picture.Flush();
        var look = model.MediaLookFor(image.Id);
        Check("fit, zoom and crop are kept as the picture's look (Windows)", look is { Fit: MediaLookFit.Fit, Crop: { } crop } && Math.Abs(look.Scale - 0.8) < 1e-6
            && Math.Abs(crop.Left - 0.1) < 1e-6 && Math.Abs(crop.Bottom - 0.2) < 1e-6);
        Check("its effects are saved in the shared field", model.Library.Get<MediaItem>(image.Id)?.Effects is [{ EffectKind: EffectKind.ColorAdjust }]);
        await Delay(700);
        Save(pictureEditor, "e7-09-media-editor-picture");

        model.FireMedia(image);
        await Delay(800);
        var dressed = model.DressMedia(model.Show.AnimatedScene());
        var shown = dressed.Layers.SelectMany(l => l.Items).FirstOrDefault(i => i.Render.Content is ItemContent.Media m && m.Id == image.Id);
        Check("live, the picture is drawn cropped, fitted and zoomed, in its effects",
            shown?.Render.Content is ItemContent.Media { SourceRect: { } source } && Math.Abs(source.X - 0.1) < 1e-6 && Math.Abs(source.Height - 0.8) < 1e-6
            && shown.Effects.Count > 0);
        await Snapshot("e7-10-media-look-live", dressed);
        if (model.Outputs?[Outputs.OutputRole.Audience] is { } audience) Save(audience, "e7-10-media-look-audience-window");

        picture.ResetLookCommand.Execute(null);
        picture.Effects.Remove(0);
        picture.Flush();
        Check("Reset puts the picture back as a Mac shows it", model.MediaLookFor(image.Id).IsDefault && model.Library.Get<MediaItem>(image.Id)?.Effects is null or { Count: 0 });
        pictureEditor.Close();
        model.ClearAllCommand.Execute(null);
    }

    // MARK: Auto Advance

    private async Task AutoAdvance7Check(Presentation deck)
    {
        var sheet = AutoAdvanceWindow.ForSlides(window, model, deck, ["e7-v1"]);
        if (sheet is null)
        {
            Check("the Auto Advance sheet opens for a slide", false);
            return;
        }
        sheet.Show();
        sheet.EnabledBox.IsChecked = true;
        Check("Auto Advance on a slide: 7 seconds, as on a Mac", deck.Slides[0].AutoAdvance is { DelaySeconds: 7 } && deck.Slides[1].AutoAdvance is null);
        if (sheet.ApplyTo is { } applyTo) applyTo.SelectedIndex = 1;
        Check("Apply to its group reaches the other verse", sheet.Reach?.Invoke() == AutoAdvanceEditing.Reach.Groups
            && deck.Slides[1].AutoAdvance is { DelaySeconds: 7 } && deck.Slides[2].AutoAdvance is null);
        Check("…and it's saved", model.Library.Get<Presentation>(deck.Id)?.Slides[1].AutoAdvance is not null);
        await Delay(500);
        Save(sheet, "e7-11-auto-advance-slides");
        if (sheet.ApplyTo is { } every) every.SelectedIndex = 2;
        sheet.EnabledBox.IsChecked = false;
        Check("turning it off for every slide clears them all", deck.Slides.All(s => s.AutoAdvance is null));
        sheet.Close();

        var show = AutoAdvanceWindow.ForPresentation(window, model, deck);
        show.Show();
        show.EnabledBox.IsChecked = true;
        Check("Slide Show: the whole deck moves on by itself", deck.AutoAdvance is { DelaySeconds: > 0 } && model.Library.Get<Presentation>(deck.Id)?.AutoAdvance is not null);
        await Delay(500);
        Save(show, "e7-12-slide-show");
        show.EnabledBox.IsChecked = false;
        show.Close();

        if (model.Library.All<MediaItem>().FirstOrDefault(m => m.MediaKind == MediaKind.Image) is { } item && AutoAdvanceWindow.ForMedia(window, model, item) is { } media)
        {
            media.Show();
            media.EnabledBox.IsChecked = true;
            Check("a media item's Auto Advance is saved", model.Library.Get<MediaItem>(item.Id)?.AutoAdvance is not null);
            await Delay(400);
            Save(media, "e7-13-auto-advance-media");
            media.EnabledBox.IsChecked = false;
            Check("…and turned off again", model.Library.Get<MediaItem>(item.Id)?.AutoAdvance is null);
            media.Close();
        }
    }

    // MARK: Effects on a slide object

    private async Task Effects7Check()
    {
        var welcome = model.ServiceItems.FirstOrDefault(r => r.Name == "Welcome to MxU Slides");
        if (welcome is null)
        {
            Skip("effects in the slide inspector", "no welcome deck");
            return;
        }
        model.SelectedServiceItem = welcome;
        model.IsEditing = true;
        model.SelectedSlide = model.Slides.FirstOrDefault();
        await Delay(500);
        var slide = model.SelectedSlide?.Slide;
        var target = slide?.Objects.FirstOrDefault(o => o.ObjectKind == SlideObjectKind.Text) ?? slide?.Objects.FirstOrDefault();
        if (slide is null || target is null)
        {
            Skip("effects in the slide inspector", "the welcome slide has no objects");
            return;
        }
        window.CanvasEditor.Select([target.Id]);
        var effects = model.Inspector?.Effects;
        Check("the slide inspector has an Effects section", effects is not null);
        if (effects is null) return;
        effects.Add(EffectKind.Blur);
        effects.Add(EffectKind.Vignette);
        Check("effects are added to the object, in order", target.Effects is [{ EffectKind: EffectKind.Blur }, { EffectKind: EffectKind.Vignette }]);
        effects.Move(1, 0);
        Check("…reordered", target.Effects is [{ EffectKind: EffectKind.Vignette }, { EffectKind: EffectKind.Blur }]);
        effects.SetEnabled(1, false);
        Check("…and switched off without losing them", target.Effects?[1].Enabled == false);
        Check("the editor says when this PC can't draw pixel shaders",
            EffectsEditorViewModel.CanDrawShaders ? effects.SoftwareNote is null : effects.SoftwareNote is { Length: > 0 });
        report.AppendLine($"NOTE  pixel shaders on this PC: {(EffectsEditorViewModel.CanDrawShaders ? "yes" : "no")}");
        await Delay(600);
        Save(window, "e7-14-effects-inspector");
        effects.Remove(0);
        effects.Remove(0);
        Check("removing them leaves the object as it was", target.Effects is null or { Count: 0 });
        model.IsEditing = false;
    }

    // MARK: Stage layout editor

    private async Task StageLayout7Check(Presentation deck)
    {
        var editor = StageLayoutEditorWindow.Open(window, model, null);
        var vm = editor.Model;
        await Delay(800);
        var before = vm.Layouts.Count;
        vm.NewLayout(ConfidenceLayoutTemplate.Blank);
        var layout = vm.Current;
        Check("New makes a blank stage layout, saved", layout is { Objects.Count: 0 } && vm.Layouts.Count == before + 1 && model.Library.Get<ConfidenceLayout>(layout.Id) is not null);
        if (layout is null) return;
        Check("renaming the layout saves it", vm.RenameLayout("Self-test Stage") && model.Library.Get<ConfidenceLayout>(layout.Id)?.Name == "Self-test Stage");

        var current = vm.AddBox(StageBoxKind.CurrentSlide);
        var next = vm.AddBox(StageBoxKind.NextSlide);
        var clock = vm.AddBox(StageBoxKind.Clock);
        var notes = vm.AddBox(StageBoxKind.Notes);
        var serviceTimer = vm.ServiceTimers.FirstOrDefault()?.Value;
        var timer = vm.AddBox(StageBoxKind.ServiceTimer, serviceTimer);
        Check("boxes are added as cards with their words linked", current.Any(o => o.TextLink?.Source == TextSourceKind.CurrentSlide)
            && next.Any(o => o.TextLink?.Source == TextSourceKind.NextSlide) && clock.Any(o => o.TextLink?.Source == TextSourceKind.Clock)
            && current.Count > 1);
        Check("a notes box reads the current slide's \"Notes\" (no new kind in the shared layout)",
            notes.Any(o => o.TextLink is { Source: TextSourceKind.CurrentSlide, SourceObjectName: StageLayoutEditing.NotesObjectName }));
        Check("a service-tracking timer box shows that timer", serviceTimer is not null && timer.Any(o => o.TextLink?.TimerId == serviceTimer));
        var frames = layout.Objects.Where(o => o.TextLink is not null).Select(o => new SceneRect(o.X ?? 0, o.Y ?? 0, o.Width ?? 0, o.Height ?? 0)).ToList();
        Check("new boxes don't land on one another", frames.Count == 5 && frames.SelectMany((a, i) => frames.Skip(i + 1).Select(b => (a, b)))
            .All(pair => pair.a.X + pair.a.Width <= pair.b.X + 0.5 || pair.b.X + pair.b.Width <= pair.a.X + 0.5 || pair.a.Y + pair.a.Height <= pair.b.Y + 0.5 || pair.b.Y + pair.b.Height <= pair.a.Y + 0.5));

        // What a box shows is picked in its SHOWS panel.
        var words = notes.First(o => o.TextLink is not null);
        vm.SelectObjects([words.Id]);
        Check("selecting a box shows what it reads", vm.Link?.Source?.Label == "Slide notes" && vm.Inspector is not null);
        vm.SafeMargin = vm.SafeMargins.First(m => Math.Abs(m.Value - 0.10) < 1e-9);
        words.X = 4;
        vm.SelectObjects([words.Id]);
        if (vm.Inspector is { } inspector) inspector.X = "4";
        Check("the safe margin is drawn, and a box past it is pointed out", vm.SafeArea is not null && vm.OutsideSafeNote is { Length: > 0 });
        await Delay(700);
        Save(editor, "e7-15-stage-layout-editor");

        // What's live now: the verse with notes, on the layout.
        model.Show.Fire(deck.Slides[0], deck);
        model.RenderOutputs(Rendering.SceneTransition.Cut);
        await Delay(600);
        vm.DataIndex = 1;
        await Delay(700);
        Save(editor, "e7-16-stage-layout-live");
        var stage = model.StageScene(layout);
        var stageText = TextOf(stage);
        Check("on stage, the layout shows the live slide, what's next and the slide's notes", stageText.Contains("Great is thy faithfulness", StringComparison.Ordinal)
            && stageText.Contains("There is no shadow", StringComparison.Ordinal) && stageText.Contains("Self-test note: slow down here", StringComparison.Ordinal));
        await Snapshot("e7-17-stage-layout-frame", stage);
        vm.DataIndex = 0;

        vm.DuplicateLayoutCommand.Execute(null);
        var copy = vm.Current;
        Check("Duplicate copies the layout with its boxes", copy is not null && copy.Id != layout.Id && copy.Objects.Count == layout.Objects.Count);
        if (copy is not null)
        {
            vm.DeleteLayout();
            Check("…and Delete takes the copy away", model.Library.Get<ConfidenceLayout>(copy.Id) is null);
        }
        vm.Open(layout.Id);
        vm.SafeMargin = vm.SafeMargins.First(m => Math.Abs(m.Value - 0.05) < 1e-9);
        editor.Close();
        model.ClearAllCommand.Execute(null);
    }

    // MARK: Overlay editor extras

    private async Task OverlayExtras7Check()
    {
        var editor = OverlayEditorWindow.Open(window, model, null);
        var vm = editor.Model;
        await Delay(800);
        vm.NewOverlayCommand.Execute(null);
        var overlay = vm.Current;
        if (overlay is null)
        {
            Check("the overlay editor makes an overlay for the extras", false);
            return;
        }
        vm.Rename("Self-test Extras");
        vm.AddBoxCommand.Execute(null);
        var plate = overlay.Objects.Last();
        vm.AddTextCommand.Execute(null);
        var name = overlay.Objects.Last();
        vm.ShowingAnimationTab = true;

        // A Change (morph): pose where the plate ends on the canvas.
        vm.SelectObjects([plate.Id]);
        vm.Animation.GalleryKindIndex = 3;
        vm.Animation.AddEffectCommand.Execute(vm.Animation.GalleryTiles[0]);
        Check("Change adds a morph step and starts posing it", vm.Animation.Posing && vm.IsPosing
            && plate.AnimationSteps?.FirstOrDefault(s => s.Kind == AnimationKind.Morph) is { ToObject: not null });
        var restingX = plate.X;
        if (vm.Inspector is { } poseInspector)
        {
            poseInspector.X = "1200";
            poseInspector.W = "500";
        }
        var morph = plate.AnimationSteps?.FirstOrDefault(s => s.Kind == AnimationKind.Morph);
        Check("posing moves where the Change ends, not where the plate rests", morph?.ToObject is { X: 1200, Width: 500 } && plate.X == restingX);
        await Delay(600);
        Save(editor, "e7-18-overlay-change-posed");
        vm.Animation.TogglePoseCommand.Execute(null);
        Check("Done posing goes back to editing the overlay", !vm.Animation.Posing && !vm.IsPosing);

        // An In with a custom start.
        vm.SelectObjects([name.Id]);
        vm.Animation.GalleryKindIndex = 0;
        vm.Animation.AddEffectCommand.Execute(vm.Animation.GalleryTiles.First(t => t.Animation == StepAnimation.Move));
        vm.Animation.SetCustomStart(true);
        if (vm.Inspector is { } startInspector) startInspector.Y = "100";
        var entrance = name.AnimationSteps?.FirstOrDefault(s => s.Kind == AnimationKind.In);
        Check("a custom start poses where the text comes in from", entrance?.FromObject is { Y: 100 } && vm.Animation.StepEditor?.HasCustomStart == true);
        vm.Animation.TogglePoseCommand.Execute(null);

        // Camera push on the entrance.
        if (vm.Animation.StepEditor is { } step)
        {
            step.CameraPush = true;
            step.PushAlignmentIndex = 1;
            step.PushMargin = "40";
            step.PushZoom = "1.2";
            step.PushBackdrop = true;
            step.PushBlur = "48";
        }
        Check("camera push keeps where the video sits, its margin, zoom and blur", entrance?.VideoPush is { Alignment: VideoPushAlignment.Left, Margin: 40, Zoom: 1.2, Backdrop: true, BlurRadius: 48 });

        // The timeline: drag a block, stretch it, link it; then drag a step in the list.
        if (entrance is not null)
        {
            vm.Animation.ResizeStep(entrance.Id, 1.5);
            Check("stretching a block on the timeline sets the step's length", Math.Abs(entrance.DurationSeconds - 1.5) < 1e-6);
            var block = vm.Animation.Timeline?.Rows.SelectMany(r => r.Blocks).FirstOrDefault(b => b.StepId == entrance.Id);
            vm.Animation.PlaceStep(entrance.Id, 0.5);
            var moved = vm.Animation.Timeline?.Rows.SelectMany(r => r.Blocks).FirstOrDefault(b => b.StepId == entrance.Id);
            Check("dragging a block along its row starts it later", block is not null && moved is not null && moved.StartOffset > block.StartOffset + 0.2);
        }
        await Delay(600);
        Save(editor, "e7-19-overlay-timeline");

        var rows = vm.Animation.StepRows.ToList();
        if (rows.Count >= 2)
        {
            var last = rows[^1].StepId;
            var first = rows.First(r => r.Group == rows[^1].Group);
            vm.Animation.DropStep(last, first, after: false);
            Check("dropping a step on another moves it before it", vm.Animation.StepRows.First(r => r.Group == first.Group).StepId == last);
        }
        vm.Animation.SelectedStepRow = vm.Animation.StepRows.FirstOrDefault(r => r.StepId == entrance?.Id);
        await Delay(500);
        Save(editor, "e7-20-overlay-camera-push");
        vm.PlayPreviewCommand.Execute(null);
        await Delay(400);
        Save(editor, "e7-21-overlay-preview");
        vm.StopPreviewCommand.Execute(null);

        vm.Delete();
        Check("the self-test overlay is deleted", model.Library.Get<Overlay>(overlay.Id) is null);
        editor.Close();
    }
}
