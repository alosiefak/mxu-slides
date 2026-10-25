using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Core.Scripture;
using MxU.Slides.Windows.Rendering;
using MxU.Slides.Windows.Views;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>
/// The Phase 6 Bible checks, driven through the real Bible window: its keys leave a live song
/// alone (no scripture over the lyrics by accident); a typed reference goes live and Next walks
/// on past its end to the following verse (from the main window and from the Bible window's
/// keys); a lower-third and a long full-screen passage are split so nothing overflows (measured
/// with the outputs' own text layout); a word search finds the verse and puts it up; a verse the
/// WEB leaves out is explained; two translations side by side, in the window and on the slides;
/// and Add to Service makes an ordinary presentation. Pictures 60–66.
/// <para>
/// Every wait is for something to happen (the box read, the slides made), with a generous limit,
/// so a busy PC makes the checks slower but not wrong.
/// </para>
/// </summary>
public sealed partial class SelfTest
{
    private async Task ScriptureCheck()
    {
        var bible = model.Scripture;
        var made = new List<string>();
        BibleWindow? panel = null;
        try
        {
            panel = BibleWindow.Open(window, model);
            var loaded = await Until(() => BibleCatalog.BuiltIn.Loaded("kjv"), b => b is not null, 10000);
            Check("the KJV loads in the background when the Bible window opens", loaded is { VerseCount: 31102 });
            bible.Translation = BibleCatalog.BuiltIn.Find("kjv")!;
            bible.Compare = null;
            bible.BothOnSlides = false;
            bible.Layout = ScriptureLayout.FullScreen;
            bible.VersesPerSlide = 0;
            bible.VerseNumbers = true;
            bible.ReferenceLine = true;

            await KeysLeaveASongAlone(panel);
            await ReferenceGoesLive(panel);
            await PassagesFitTheirDesigns(panel);
            await SearchFindsAVerse(panel);
            await AbsentAndSideBySide(panel);
            await KeptAsAPresentation(made);

            var about = BibleAboutWindow.Make(panel);
            about.Show();
            await Delay(400);
            Save(about, "66-bible-about");
            Check("About lists each translation with its licence notes",
                BibleCatalog.BuiltIn.Translations.All(t => t.Notes.Length > 0) && BibleCatalog.BuiltIn.Find("web")!.Notes.Contains("trademark"));
            about.Close();
        }
        finally
        {
            panel?.Close();
            model.ClearAllCommand.Execute(null);
            bible.Translation = BibleCatalog.BuiltIn.Find("kjv")!;
            bible.Compare = null;
            bible.BothOnSlides = false;
            bible.Layout = ScriptureLayout.FullScreen;
            foreach (var id in made) model.Library.Delete<Presentation>(id);
        }
    }

    // Types into the Bible box as the operator does, then waits until the tool has read it (after
    // the short pause that follows typing, which a busy PC stretches) and has made its slides.
    private async Task Type(BibleWindow panel, string text)
    {
        panel.Box.Text = text;
        await Until(() => model.Scripture.HasUnreadTyping, unread => !unread, 10000);
        await model.Scripture.Ready;
    }

    // Waits until the audience shows these words (they're there at once; this only allows for a busy PC).
    private Task<string> OnScreen(string words) => Until(() => AudienceText(), text => text.Contains(words, StringComparison.Ordinal), 5000);

    /// <summary>
    /// A song is up and the Bible window has the focus: ↓ in the box only moves the preview, and a
    /// clicker's PageDown moves the song on. Neither puts the typed passage over the lyrics.
    /// </summary>
    private async Task KeysLeaveASongAlone(BibleWindow panel)
    {
        var bible = model.Scripture;
        // Chosen afresh (it may be chosen already, with another presentation open since), so the song is what's open.
        model.SelectedServiceItem = null;
        model.SelectedServiceItem = model.ServiceItems.FirstOrDefault(r => r.Name == "Amazing Grace");
        await Until(() => model.CurrentPresentation?.Name, name => name == "Amazing Grace", 3000);
        if (model.SelectedServiceItem is null || model.CurrentPresentation?.Name != "Amazing Grace" || model.Slides.Count < 3)
        {
            Check("the sample song is in the service, for the Bible window's keys to leave alone", false);
            return;
        }
        model.GoLiveCommand.Execute(model.Slides[0]);
        await Delay(300);
        var (song, slide) = (model.LiveTitle, model.LiveDetail);
        Check("a song is up for the Bible window's keys to leave alone", model.Show.Live is not null && song == "Amazing Grace");
        panel.Activate();
        panel.Box.Focus();
        await Type(panel, "Psalm 23");
        var expected = Math.Min(bible.SelectedPage + 1, bible.Pages.Count - 1);
        Press(panel, Key.Down);
        Check("with a song up, ↓ in the Bible box moves the preview, not the screen",
            !bible.IsLive && model.LiveTitle == song && model.LiveDetail == slide && bible.SelectedPage == expected);
        Press(panel, Key.PageDown);
        var moved = await Until(() => model.LiveDetail, detail => detail != slide, 3000);
        Check("a clicker's PageDown in the Bible window moves the song on, and doesn't put the passage over it",
            !bible.IsLive && model.LiveTitle == song && moved != slide);
        report.AppendLine($"      the song went from “{slide}” to “{moved}”; on screen: {model.LiveTitle}");
        model.ClearAllCommand.Execute(null);
        await Delay(300);
    }

    /// <summary>Types "Jn 3:16-18", shows it, then Next (main window, then PageDown in the Bible window) goes on verse by verse.</summary>
    private async Task ReferenceGoesLive(BibleWindow panel)
    {
        var bible = model.Scripture;
        await Type(panel, "Jn 3:16-18");
        Check("a typed reference is read and looked up (“Jn 3:16-18” → John 3:16–18, 3 verses)",
            bible.Passage?.Reference.Display == "John 3:16–18" && bible.Passage.Verses.Count == 3 && bible.Status.StartsWith("John 3:16–18 · 3 verses", StringComparison.Ordinal));
        Check("its slides are previewed before anything goes live", bible.Pages.Count >= 1 && !bible.IsLive);
        await Delay(150);
        Save(panel, "60-bible-reference");

        var watch = Stopwatch.StartNew();
        await bible.GoLive();
        watch.Stop();
        await OnScreen("For God so loved the world");
        Check("Enter puts the passage on screen straight away", AudienceText().Contains("For God so loved the world", StringComparison.Ordinal)
            && model.LiveTitle == "John 3:16–18 (KJV)" && bible.IsLive);
        report.AppendLine($"      going live took {watch.Elapsed.TotalMilliseconds:0} ms");
        Check("the slide is drawn on the theme's Verse + Reference design, with verse numbers and its reference",
            model.Show.Live?.Slide.ThemeSlideName == "Verse + Reference" && AudienceText().Contains("¹⁶", StringComparison.Ordinal)
            && AudienceText().Contains("John 3:16", StringComparison.Ordinal) && AudienceText().Contains("KJV", StringComparison.Ordinal));
        Check("the live passage isn't saved in the library", model.Library.Get<Presentation>(bible.LivePresentation?.Id) is null);
        await Delay(700); // the design's builds play in, for the picture
        await Snapshot("60b-bible-live-frame", model.Show.Scene());
        Save(panel, "60c-bible-live");

        // Past the passage's last slide, Next goes on to the next verse (the main window's Next, skipping the design's Out).
        var pages = bible.LivePresentation!.Slides.Count;
        for (var i = 1; i < pages; i++) model.NextSkippingBuildsCommand.Execute(null);
        model.NextSkippingBuildsCommand.Execute(null);
        await OnScreen("And this is the condemnation");
        Check("Next past the end of the passage shows the following verse (John 3:19)",
            AudienceText().Contains("And this is the condemnation", StringComparison.Ordinal) && bible.Query == "John 3:19" && model.LiveTitle == "John 3:19 (KJV)");
        model.PreviousCommand.Execute(null); // takes back nothing (no build played yet), so goes to the verse before
        await OnScreen("He that believeth on him is not condemned");
        Check("Previous goes back a verse (John 3:18)", AudienceText().Contains("He that believeth on him is not condemned", StringComparison.Ordinal));

        // The Bible window's own keys: PageDown (what a clicker sends) and ↓.
        watch.Restart();
        Press(panel, Key.PageDown);
        watch.Stop();
        report.AppendLine($"      the next verse took {watch.Elapsed.TotalMilliseconds:0} ms");
        Check("PageDown in the Bible window goes to the next verse", model.LiveTitle == "John 3:19 (KJV)");
        Press(panel, Key.Down);
        Check("↓ in the box goes on again (John 3:20)", model.LiveTitle == "John 3:20 (KJV)");
        await Delay(300);
        Save(panel, "61-bible-next-verse");
    }

    /// <summary>A lower third and a long full-screen passage: split by length, and no slide's text is cut off.</summary>
    private async Task PassagesFitTheirDesigns(BibleWindow panel)
    {
        var bible = model.Scripture;
        bible.Layout = ScriptureLayout.LowerThird;
        await Type(panel, "Psalm 23");
        Check("Psalm 23 on the lower third takes several slides", bible.Pages.Count >= 3 && bible.Preview!.Slides.All(s => s.ThemeSlideName == ScriptureSlides.LowerThirdDesign));
        Check("no lower-third slide's words are cut off", Overflowing(bible.Preview!) == 0);
        await bible.GoLive();
        await OnScreen("shepherd");
        Check("the lower third goes live", model.Show.Live?.Slide.ThemeSlideName == ScriptureSlides.LowerThirdDesign && AudienceText().Contains("shepherd", StringComparison.Ordinal));
        await Delay(900); // the band slides in, for the picture
        await Snapshot("62-bible-lower-third-frame", model.Show.Scene());
        Save(panel, "62b-bible-lower-third");
        // A long book name is too long for the lower third's small reference plate at the design's size.
        await Type(panel, "1 Thess 4:13-18");
        Check("a long reference is made smaller to stay on one line of the lower third's plate, none cut off",
            bible.Preview is { } thess && Overflowing(thess) == 0
            && thess.Slides.All(s => s.Objects.Single(o => o.Name == "Reference").TextStyle?.FontSize is not null));
        if (bible.Pages.Count > 0) await Snapshot("62e-bible-long-reference", bible.Pages[0].Scene);

        bible.Layout = ScriptureLayout.FullScreen;
        var psalm = BibleCatalog.BuiltIn.Load("kjv").Read(PassageReference.Parse("Psalm 119:41-80")!);
        var measuring = Stopwatch.StartNew();
        var measured = ScriptureSlides.Pages(psalm, bible.Theme, new ScriptureSlideOptions(), TextLayout.OverflowsFrame);
        measuring.Stop();
        var watch = Stopwatch.StartNew();
        panel.Box.Text = "Psalm 119:1-40";
        bible.RefreshNow();
        var uiThread = watch.Elapsed;
        await bible.Ready;
        watch.Stop();
        report.AppendLine($"      measuring 40 other verses into {measured.Count} slides took {measuring.Elapsed.TotalMilliseconds:0} ms");

        Check($"forty verses become {bible.Pages.Count} full-screen slides, none cut off", bible.Pages.Count >= 6 && Overflowing(bible.Preview!) == 0);
        report.AppendLine($"      making the 40 verses' slides took {watch.Elapsed.TotalMilliseconds:0} ms; the UI thread was busy {uiThread.TotalMilliseconds:0} ms of it");
        await Snapshot("62c-bible-long-passage-slide", bible.Pages[1].Scene);
        bible.Layout = ScriptureLayout.SideThird;
        await bible.Ready;
        Check("the side third is split to fit too", bible.Preview!.Slides.All(s => s.ThemeSlideName == ScriptureSlides.SideThirdDesign) && Overflowing(bible.Preview!) == 0);
        await Snapshot("62d-bible-side-third-slide", bible.Pages[0].Scene);
        bible.Layout = ScriptureLayout.FullScreen;
    }

    // How many of a presentation's slides have words the outputs would cut off (the slide grid's
    // warning) or would have to turn into a second page: the Bible tool should need neither.
    private int Overflowing(Presentation deck)
    {
        var theme = model.Library.Get<Theme>(deck.ThemeId);
        return deck.Slides.Count(slide =>
        {
            var scene = SlideSceneBuilder.Scene(slide, theme, deck);
            var paged = scene[LayerKind.Slide].Items.Any(item => item.Content is ItemContent.Text { Styled: var styled }
                && slide.Objects.Any(o => o.Id == item.Id)
                && TextLayout.Pages(styled, new SceneSize(item.Frame.Width, item.Frame.Height)).Count > 1);
            return paged || SlideSceneBuilder.ClippedTextObjectIds(slide, scene, TextLayout.OverflowsFrame).Count > 0;
        });
    }

    /// <summary>"love is patient" in the WEB: 1 Corinthians 13:4 comes first, and Enter shows it.</summary>
    private async Task SearchFindsAVerse(BibleWindow panel)
    {
        var bible = model.Scripture;
        bible.Translation = BibleCatalog.BuiltIn.Find("web")!;
        panel.Box.Text = "love is patient";
        var hits = await Until(() => bible.Results, r => r.Count > 0, 15000);
        Check("a word search runs in the background and ranks 1 Corinthians 13:4 first",
            bible.Mode == ViewModels.ScriptureMode.Search && hits.FirstOrDefault()?.Verse.Ref.ToString() == "1CO 13:4");
        var previewed = await Until(() => bible.PreviewedResult, r => r is not null, 5000);
        Check("the chosen result is previewed as a slide", previewed?.StartsWith("1 Corinthians 13:4", StringComparison.Ordinal) == true && bible.PreviewScene is not null);
        await Delay(200);
        Save(panel, "63-bible-search");
        await bible.GoLive();
        await OnScreen("Love is patient");
        Check("Enter on a result shows that verse", AudienceText().Contains("Love is patient", StringComparison.Ordinal) && bible.Query == "1 Corinthians 13:4");

        panel.Activate();
        panel.Box.Focus();
        await Type(panel, "1 co");
        Check("a half-typed book offers the books it could be", bible.Suggestions.Any(b => b.Code == "1CO"));
        Press(panel, Key.Tab);
        Check("Tab takes the book offered", panel.Box.Text == "1 Corinthians ");
    }

    /// <summary>Acts 8:37 isn't in the WEB, said calmly; then the WEB beside the KJV, in the window and on the slides.</summary>
    private async Task AbsentAndSideBySide(BibleWindow panel)
    {
        var bible = model.Scripture;
        await Type(panel, "Acts 8:37");
        Check("a verse the WEB leaves out is explained, not an error",
            bible.Passage is { IsEmpty: true } && bible.StatusIsNote && bible.Status.StartsWith("Acts 8:37 isn't in the WEB", StringComparison.Ordinal));
        await Delay(150);
        Save(panel, "64-bible-absent-verse");
        Check("it offers the KJV, which has it", bible.Alternative?.Id == "kjv");
        bible.UseAlternative();
        await bible.Ready;
        Check("“Show it in the KJV” shows it", bible.Passage?.Translation.Id == "kjv" && bible.Passage.Verses.Count == 1 && bible.Preview?.Name == "Acts 8:37 (KJV)");
        await Type(panel, "Acts 8:36-38");
        Check("a passage around it shows the verses there and notes the gap",
            bible.Passage?.Verses.Count == 2 && bible.Status.Contains("isn't in the WEB", StringComparison.Ordinal));

        bible.Compare = BibleCatalog.BuiltIn.Find("kjv");
        await Type(panel, "John 3:16-17");
        Check("two translations side by side", bible.Verses.Count == 2
            && bible.Verses[0].Text.StartsWith("For God so loved the world, that he gave his only born Son", StringComparison.Ordinal)
            && bible.Verses[0].CompareText?.StartsWith("For God so loved the world, that he gave his only begotten Son", StringComparison.Ordinal) == true);

        // On the slides too: each translation in half the design's verse box.
        bible.BothOnSlides = true;
        await bible.Ready;
        var deck = bible.Preview;
        Check("“Both translations on the slides” puts the WEB and the KJV side by side on each slide, none cut off",
            deck is { Name: "John 3:16–17 (WEB, KJV)" } && deck.Slides.All(s => s.Objects.Count(o => o.Name == "Verse") == 2)
            && deck.Slides[0].Objects[1].Text.Contains("only begotten Son", StringComparison.Ordinal) && Overflowing(deck) == 0);
        await Delay(200);
        Save(panel, "65-bible-side-by-side");
        await bible.GoLive();
        await OnScreen("only begotten Son");
        Check("both go live together", AudienceText().Contains("only born Son", StringComparison.Ordinal) && AudienceText().Contains("only begotten Son", StringComparison.Ordinal));
        await Delay(700);
        await Snapshot("65b-bible-side-by-side-frame", model.Show.Scene());
        foreach (var (layout, picture) in new[] { (ScriptureLayout.LowerThird, "65c-bible-side-by-side-lower-third"), (ScriptureLayout.SideThird, "65d-bible-side-by-side-side-third") })
        {
            bible.Layout = layout;
            await bible.Ready;
            Check($"side by side fits the {(layout == ScriptureLayout.LowerThird ? "lower third" : "side third")} too, none cut off",
                bible.Preview is { } fitted && fitted.Slides.All(s => s.ThemeSlideName == ScriptureSlides.DesignName(layout)) && Overflowing(fitted) == 0);
            if (bible.Pages.Count > 0) await Snapshot(picture, bible.Pages[0].Scene);
        }
        bible.Layout = ScriptureLayout.FullScreen;
        bible.BothOnSlides = false;
        bible.Compare = null;
    }

    /// <summary>Add to Service saves an ordinary presentation (once) and adds a row; it opens and goes live like any other.</summary>
    private async Task KeptAsAPresentation(List<string> made)
    {
        var bible = model.Scripture;
        bible.Translation = BibleCatalog.BuiltIn.Find("kjv")!;
        bible.Query = "Rom 8:28, 31-32";
        bible.RefreshNow();
        await bible.Ready;
        var rowsBefore = model.ServiceItems.Count;
        var name = await bible.AddToService();
        var saved = model.Library.All<Presentation>().FirstOrDefault(p => p.Name == "Romans 8:28, 31–32 (KJV)");
        if (saved is not null) made.Add(saved.Id);
        Check("Add to Service saves “Romans 8:28, 31–32 (KJV)” and adds it to the service",
            name == "Romans 8:28, 31–32 (KJV)" && saved is not null && model.ServiceItems.Count == rowsBefore + 1
            && model.ServiceItems.Any(r => r.Item.RefId == saved.Id));
        Check("it's an ordinary deck in the theme's design, its origin “Bible (KJV)”",
            saved is { PresentationKind: PresentationKind.Deck, Origin.Detail: "Bible (KJV)" } && saved.Slides.All(s => s.ThemeSlideName == ScriptureSlides.FullScreenDesign));
        await bible.SaveToLibrary();
        Check("saving the same passage again doesn't make a second copy",
            model.Library.All<Presentation>().Count(p => p.Name == "Romans 8:28, 31–32 (KJV)") == 1);
        var row = model.ServiceItems.FirstOrDefault(r => r.Item.RefId == saved?.Id);
        if (row is not null)
        {
            model.SelectedServiceItem = null;
            model.SelectedServiceItem = row;
            await Until(() => model.Slides.Count, count => count > 0, 3000);
            model.GoLiveCommand.Execute(model.Slides.FirstOrDefault());
            await OnScreen("all things work together for good");
            Check("the saved passage goes live from the service like a song", AudienceText().Contains("all things work together for good", StringComparison.Ordinal));
            model.RemoveServiceItemCommand.Execute(row);
        }
    }

    // A key pressed in a window, through its PreviewKeyDown as a real key press arrives.
    private static void Press(Window target, Key key)
    {
        var source = PresentationSource.FromVisual(target);
        if (source is null) return;
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    }
}
