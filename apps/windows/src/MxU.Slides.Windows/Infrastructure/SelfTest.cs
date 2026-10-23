using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Outputs;
using MxU.Slides.Windows.Rendering;
using MxU.Slides.Windows.ViewModels;
using MxU.Slides.Windows.Views;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>
/// "MxUSlides.exe --self-test &lt;folder&gt;" drives the real app through a short
/// service (open a song, go live, next, edit, stage, clear), saves a picture
/// of each step and writes report.txt with pass/fail checks, then quits.
/// Use it to confirm a build works on a new computer.
/// </summary>
public sealed class SelfTest(MainWindow window, MainViewModel model, string folder)
{
    private readonly StringBuilder report = new();
    private int failures;

    public static string? FolderFromArgs(string[] args)
    {
        var index = Array.IndexOf(args, "--self-test");
        if (index < 0) return null;
        return index + 1 < args.Length ? args[index + 1] : Path.Combine(AppPaths.LocalRoot, "SelfTest");
    }

    public async Task RunAsync()
    {
        Directory.CreateDirectory(folder);
        report.AppendLine($"MxU Slides self-test {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"Displays: {string.Join("; ", Displays.All().Select(d => d.Label))}");
        try
        {
            await Step("01-start", () => { });
            Check("starter themes are in the library", model.Themes.Count >= 5);
            Check("the welcome service is open", model.SelectedService?.Name == "Getting Started");

            var songRow = model.ServiceItems.FirstOrDefault(r => r.Name == "Amazing Grace");
            Check("the sample song is in the service", songRow is not null);
            await Step("02-song-open", () => model.SelectedServiceItem = songRow);
            Check("the song shows its slides", model.Slides.Count >= 5);
            Check("slide 2 is Verse 1", model.Slides.ElementAtOrDefault(1)?.Label == "Verse 1");

            await Step("03-live-verse1", () => model.GoLiveCommand.Execute(model.Slides[1]), settle: 1200);
            var live = AudienceText();
            Check("the audience shows Verse 1", live.Contains("Amazing grace", StringComparison.OrdinalIgnoreCase));
            Check("the stage shows the current lyric", StageText().Contains("Amazing grace", StringComparison.OrdinalIgnoreCase));
            Check("the stage shows what's next", StageText().Contains("I once was lost", StringComparison.OrdinalIgnoreCase));
            Check("the live tile is marked", model.Slides[1].IsLive);
            await Snapshot("03-audience-frame", model.Show.Scene());
            await Snapshot("03-stage-frame", model.StageScene());

            await Step("04-next", () => model.NextCommand.Execute(null), settle: 1200);
            Check("Next moved to the following slide", AudienceText().Contains("I once was lost", StringComparison.OrdinalIgnoreCase));

            var background = model.MediaItems.FirstOrDefault(m => m.Name == "Deep Blue");
            Check("starter backgrounds are in the media bin", model.MediaItems.Count >= 4 && background is not null);
            if (background is not null)
            {
                await Step("04-background", () => model.MediaClickedCommand.Execute(background), settle: 1500);
                Check("the background sits behind the lyrics",
                    model.Show.LiveMedia.TryGetValue(LayerKind.LoopingVideos, out var cue) && cue.MediaId == background.Item.Id
                    && AudienceText().Contains("I once was lost", StringComparison.OrdinalIgnoreCase));
                Check("the background image decodes", model.Media.Image(background.Item.Id, 1280) is not null);
                await Snapshot("04-background-frame", model.Show.Scene());
                model.NextCommand.Execute(null);
                await Delay(400);
                Check("the background stays when the slide changes", model.Show.LiveMedia.ContainsKey(LayerKind.LoopingVideos));
                model.PreviousCommand.Execute(null);
            }

            await Step("05-alert", () =>
            {
                model.AlertText = "Parent of child 47, please come to the nursery";
                model.AlertOnAudience = true;
                model.ShowAlertCommand.Execute(null);
            }, settle: 800);
            Check("the message shows on the audience", AudienceText().Contains("child 47"));
            Check("the message shows on the stage", StageText().Contains("child 47"));
            await Snapshot("05-audience-alert", model.Show.Scene());
            model.ClearAlertsCommand.Execute(null);

            await Step("06-themes", () => { });
            foreach (var theme in model.Library.All<Theme>())
            {
                var song = model.CurrentPresentation!;
                var slide = SlideSceneBuilder.ArrangedSlides(song, model.SelectedArrangement?.Id)[1];
                await Snapshot($"06-theme-{Safe(theme.Name)}", SlideSceneBuilder.Scene(slide, theme, song));
                foreach (var design in theme.Slides ?? [])
                {
                    var designed = Core.Json.MxuJson.Clone(slide);
                    designed.ThemeSlideName = design.Name;
                    await Snapshot($"06-theme-{Safe(theme.Name)}-{Safe(design.Name)}", SlideSceneBuilder.Scene(designed, theme, song));
                }
            }

            await Step("07-edit-mode", () =>
            {
                model.IsEditing = true;
                model.SelectedSlide = model.Slides[2];
            });
            Check("editing shows the slide's text box", model.SlideTextFields.Count == 1);

            var editor = new LyricsEditorWindow(model, model.CurrentPresentation) { Owner = window };
            editor.Show();
            await Delay(900);
            Save(editor, "08-lyrics-editor");
            editor.Close();
            model.IsEditing = false;

            model.Outputs?.ShowPreviewWindow(OutputRole.Audience);
            model.Outputs?.ShowPreviewWindow(OutputRole.Stage);
            model.RenderOutputs(SceneTransition.Cut);
            await Delay(1200);
            if (model.Outputs is { } outputs)
            {
                Save(outputs[OutputRole.Audience], "09-audience-window");
                Save(outputs[OutputRole.Stage], "09-stage-window");
                Check("the audience preview window is showing", outputs.IsShowing(OutputRole.Audience));
            }

            await Step("10-cleared", () => model.ClearAllCommand.Execute(null), settle: 1000);
            Check("Clear All empties the audience", model.Show.IsEmpty && AudienceText().Length == 0);

            var welcome = model.ServiceItems.FirstOrDefault(r => r.Name == "Welcome to MxU Slides");
            await Step("11-welcome-deck", () =>
            {
                model.SelectedServiceItem = welcome;
                model.GoLiveCommand.Execute(model.Slides.FirstOrDefault());
            }, settle: 1200);
            Check("a deck slide goes live", AudienceText().Contains("Welcome to MxU Slides"));
            await Snapshot("11-welcome-frame", model.Show.Scene());
            model.ClearAllCommand.Execute(null);
        }
        catch (Exception ex)
        {
            failures++;
            report.AppendLine($"CRASH {ex}");
        }
        report.AppendLine(failures == 0 ? "RESULT: PASS" : $"RESULT: FAIL ({failures} problem{(failures == 1 ? "" : "s")})");
        await File.WriteAllTextAsync(Path.Combine(folder, "report.txt"), report.ToString());
        Log.Info($"Self-test finished with {failures} failure(s); report in {folder}");
    }

    private static string Safe(string name) => string.Concat(name.Where(char.IsLetterOrDigit));

    private string AudienceText() => TextOf(model.Show.Scene());

    private string StageText() => TextOf(model.StageScene());

    private static string TextOf(RenderScene scene) => string.Join("\n", scene.AllItems
        .Select(i => i.Content is ItemContent.Text t ? t.Styled.String : null)
        .OfType<string>()
        .Where(s => s.Length > 0));

    private void Check(string what, bool ok)
    {
        report.AppendLine($"{(ok ? "PASS" : "FAIL")}  {what}");
        if (!ok) failures++;
    }

    private async Task Step(string name, Action action, int settle = 600)
    {
        action();
        await Delay(settle);
        Save(window, name);
    }

    private static Task Delay(int ms) => Task.Delay(ms);

    /// <summary>Saves a picture of a window's content.</summary>
    private void Save(Window target, string name)
    {
        try
        {
            if (target.Content is not FrameworkElement content || content.ActualWidth < 1) return;
            var dpi = VisualTreeHelper.GetDpi(content);
            var bitmap = new RenderTargetBitmap((int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var backdrop = new DrawingVisual();
            using (var dc = backdrop.RenderOpen())
                dc.DrawRectangle(target.Background ?? Brushes.Black, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            bitmap.Render(backdrop);
            bitmap.Render(content);
            Write(bitmap, name);
        }
        catch (Exception ex)
        {
            report.AppendLine($"NOTE  couldn't capture {name}: {ex.Message}");
        }
    }

    /// <summary>Renders a scene offscreen at 1280×720, exactly as an output would draw it.</summary>
    private async Task Snapshot(string name, RenderScene scene)
    {
        var view = new SceneView { Media = model.Media, PlaysVideo = false, Width = 1280, Height = 720 };
        view.Measure(new Size(1280, 720));
        view.Arrange(new Rect(0, 0, 1280, 720));
        view.Present(scene, SceneTransition.Cut);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        var bitmap = new RenderTargetBitmap(1280, 720, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        Write(bitmap, name);
    }

    private void Write(BitmapSource bitmap, string name)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(folder, name + ".png"));
        encoder.Save(file);
    }
}
