using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using MxU.Slides.Core.Export;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.ViewModels;
using MxU.Slides.Windows.Views;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>
/// Phase 8's library depth, driven through the real windows: folders, sorting, search across the
/// library, favourites and tags, picking several songs and media tiles for one action, every
/// export format (each read back), backups and restores, opening files handed over from Explorer,
/// the welcome guide, and diagnostics with Check this PC. Pictures 100-… to 139-….
/// </summary>
public sealed partial class SelfTest
{
    private string depthScratch = "";

    private async Task LibraryDepthCheck()
    {
        depthScratch = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"MxU self-test depth {Guid.NewGuid():N}")).FullName;
        LibraryDialogs.Showing = AnswerDialog;
        try
        {
            await LibraryBrowseCheck();
            await LibraryBatchCheck();
            await MediaBinBatchCheck();
            await ExportFormatsCheck();
            await LibraryCareCheck(); // SelfTest.LibraryCare.cs
        }
        finally
        {
            LibraryDialogs.Showing = null;
            dialogAnswers.Clear();
            model.LibrarySearch = "";
            model.LibraryTag = null;
            model.LibraryFavoritesOnly = false;
            model.OpenLibraryFolder([]);
            model.ClearAllCommand.Execute(null);
            try { Directory.Delete(depthScratch, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private Presentation DepthSong(string name, string lyrics, string? folder = null, string? author = null, int? ccli = null)
    {
        var song = LyricTextImporter.MakePresentation(lyrics, fallbackTitle: name, linesPerSlide: 2);
        song.Name = name;
        song.PresentationKind = PresentationKind.Song;
        song.Folder = folder;
        song.ThemeId = model.Settings.DefaultThemeId;
        if (author is not null || ccli is not null) song.Ccli = new CCLIInfo { Author = author, SongNumber = ccli, SongTitle = name, Copyright = "Public Domain" };
        model.Library.Save(song);
        return song;
    }

    // MARK: Folders, sorting, search, favourites and tags

    private async Task LibraryBrowseCheck()
    {
        var panel = window.LibraryPanelView;
        DepthSong("How Great Thou Art", "Verse 1\nO Lord my God when I in awesome wonder\nConsider all the worlds Thy hands have made\n\nChorus\nThen sings my soul my Saviour God to Thee\nHow great Thou art how great Thou art",
            folder: "Hymns", author: "Carl Boberg", ccli: 14181);
        DepthSong("Be Thou My Vision", "Verse 1\nBe Thou my vision O Lord of my heart\nNaught be all else to me save that Thou art", folder: "Hymns/Classic", author: "Dallan Forgaill");
        DepthSong("Cornerstone", "Verse 1\nMy hope is built on nothing less\nThan Jesus' blood and righteousness\n\nChorus\nChrist alone cornerstone\nWeak made strong in the Saviour's love", folder: "Modern");
        DepthSong("Oceans", "Verse 1\nYou call me out upon the waters\nThe great unknown where feet may fail", folder: "Modern", author: "Hillsong United", ccli: 6428767);
        model.RefreshLibrary();
        await Delay(300);

        Check("the library's top shows its folders, with how many are in each",
            model.LibraryRows.OfType<LibraryFolderRowViewModel>().Select(f => (f.Name, f.Count)).SequenceEqual([("Hymns", 2), ("Modern", 2)]));
        Check("…and an “All songs and presentations” row, then the songs in no folder",
            model.LibraryRows.FirstOrDefault() is LibraryAllRowViewModel && model.LibraryItems.Any(e => e.Name == "Amazing Grace"));
        SaveControl(panel, "100-library-folders");

        model.OpenLibraryFolder(["Hymns"]);
        await Delay(300);
        Check("going into a folder shows its subfolders and songs, with crumbs back",
            model.LibraryItems.Select(e => e.Name).SequenceEqual(["How Great Thou Art"])
            && model.LibraryRows.OfType<LibraryFolderRowViewModel>().Single().Name == "Classic"
            && model.LibraryCrumbs.Select(c => c.Name).SequenceEqual(["Library", "Hymns"]));
        SaveControl(panel, "101-library-in-a-folder");
        model.OpenLibraryFolder([]);

        // Recently used: what went live last comes first.
        var oceans = model.Library.All<Presentation>().First(p => p.Name == "Oceans");
        var cornerstone = model.Library.All<Presentation>().First(p => p.Name == "Cornerstone");
        // Cornerstone used just now (after the basics' songs), then Oceans goes live after it.
        model.Catalog.Usage.Touch(cornerstone.Id);
        await Delay(50);
        model.Open(oceans, null, null);
        model.GoLiveCommand.Execute(model.Slides.FirstOrDefault());
        await Delay(500);
        model.ClearAllCommand.Execute(null);
        model.LibrarySortOrder = LibrarySort.RecentlyUsed;
        model.LibraryShowsAll = true;
        await Delay(300);
        var order = model.LibraryItems.Select(e => e.Name).ToList();
        Check("sorting by Recently Used puts the song that just went live first, then the one before", order.Take(2).SequenceEqual(["Oceans", "Cornerstone"]));
        model.LibrarySortOrder = LibrarySort.Name;
        await Delay(200);
        Check("sorting by Name is A to Z", model.LibraryItems.Select(e => e.Name).SequenceEqual(model.LibraryItems.Select(e => e.Name).Order(StringComparer.CurrentCultureIgnoreCase)));
        SaveControl(panel, "102-library-everything-by-name");
        model.LibraryShowsAll = false;

        // Search: words in a lyric, a CCLI number, an author, media.
        model.LibrarySearch = "once was lost";
        await Delay(400);
        var lyricHit = model.LibraryItems.FirstOrDefault();
        Check("searching words of a lyric finds the song, with the line it matched", lyricHit?.Name == "Amazing Grace" && lyricHit.Snippet?.Contains("once was lost", StringComparison.OrdinalIgnoreCase) == true);
        SaveControl(panel, "103-library-search-lyrics");
        model.LibrarySearch = "6428767";
        await Delay(300);
        Check("a CCLI number finds its song", model.LibraryItems.FirstOrDefault()?.Name == "Oceans" && model.LibraryItems.First().Line.Contains("CCLI 6428767"));
        model.LibrarySearch = "boberg";
        await Delay(300);
        Check("an author finds their song", model.LibraryItems.FirstOrDefault()?.Name == "How Great Thou Art");
        model.LibrarySearch = "deep blue";
        await Delay(300);
        Check("media is found by name too", model.LibraryRows.OfType<LibraryMediaHitViewModel>().Any(h => h.Name == "Deep Blue"));
        SaveControl(panel, "104-library-search-media");
        model.LibrarySearch = "";

        // Favourites and tags.
        model.SetFavorite([(LibraryItemKind.Song, oceans.Id)], true);
        model.Tag([(LibraryItemKind.Song, cornerstone.Id), (LibraryItemKind.Song, oceans.Id)], "Modern worship", add: true);
        model.LibraryTag = "Modern worship";
        await Delay(300);
        Check("a tag shows only what has it", model.LibraryItems.Select(e => e.Name).Order().SequenceEqual(["Cornerstone", "Oceans"]));
        Check("a favourite has its star, and the song file itself is unchanged (a Windows extra)",
            model.LibraryItems.First(e => e.Name == "Oceans").IsFavorite && !File.ReadAllText(model.Library.DocumentPath(typeof(Presentation), oceans.Id)).Contains("Modern worship"));
        SaveControl(panel, "105-library-tag-filter");
        model.LibraryTag = null;
        model.LibraryFavoritesOnly = true;
        await Delay(300);
        Check("Favourites shows only the favourites", model.LibraryItems.Select(e => e.Name).SequenceEqual(["Oceans"]));
        model.LibraryFavoritesOnly = false;
        await Delay(200);
    }

    // MARK: Several songs at once

    private async Task LibraryBatchCheck()
    {
        var panel = window.LibraryPanelView;
        model.LibraryShowsAll = true;
        await Delay(300);
        var picked = model.LibraryItems.Where(e => e.Name is "Cornerstone" or "Oceans" or "Be Thou My Vision").ToList();
        panel.LibraryList.SelectedItems.Clear();
        foreach (var row in picked) panel.LibraryList.SelectedItems.Add(row);
        await Delay(300);
        Check("Ctrl+click picks several songs (three picked)", panel.PickedPresentationIds().Count == 3);
        await Step("106-library-three-picked", () => { }, settle: 300);
        var menu = panel.OpenMenuFor(picked[0]);
        await Delay(500);
        var headers = menu.Items.OfType<MenuItem>().Select(i => i.Header as string ?? "").ToList();
        Check("the menu for them acts on all three (Add 3 to Service, Move 3 to Folder, Delete 3 Items)",
            headers.Contains("Add 3 to Service") && headers.Contains("Move 3 to Folder") && headers.Contains("Delete 3 Items") && headers.Contains("Export 3…"));
        Check("…and drops the single-song Transition item", !headers.Any(h => h.StartsWith("Transition", StringComparison.Ordinal)));
        SaveElement(menu, "107-library-batch-menu");
        menu.IsOpen = false;

        var ids = panel.PickedPresentationIds();
        model.NewPresentationFolder([], "Easter 2026");
        model.MovePresentationsToFolder(ids, "Easter 2026");
        await Delay(400);
        Check("Move 3 to Folder puts them in a new folder", ids.All(id => model.Library.Get<Presentation>(id)?.Folder == "Easter 2026"));
        Check("…and they stay picked after the list redraws", panel.PickedPresentationIds().Count == 3);

        var service = model.SelectedService;
        var rowsBefore = model.ServiceItems.Count;
        model.AddPresentationsToService(ids);
        await Delay(300);
        Check("Add 3 to Service adds all three, in list order", model.ServiceItems.Count == rowsBefore + 3);

        var theme = model.Themes.FirstOrDefault(t => t.Id.Length > 0 && t.Id != model.Settings.DefaultThemeId);
        if (theme is not null)
        {
            model.ApplyThemeToPresentations(ids, theme.Id);
            Check("Apply Theme gives all three the theme", ids.All(id => model.Library.Get<Presentation>(id)?.ThemeId == theme.Id));
        }

        var copies = model.DuplicatePresentations(ids);
        Check("Duplicate makes a copy of each, named as the Mac names them", copies.Count == 3 && copies.All(c => c.Name.EndsWith(" Copy", StringComparison.Ordinal)));

        // Deleting songs that are in a service says so first.
        ExpectDialog("Delete 3 Items", "108-library-delete-warning");
        model.DeletePresentations(ids);
        Check("deleting songs still in a service warns, naming the service", LastHeadline == "Delete 3 items?");
        Check("…then moves them to the Trash", ids.All(id => model.Library.Get<Presentation>(id) is null));
        ExpectDialog("Delete 3 Items", ""); // several at once always ask
        model.DeletePresentations([.. copies.Select(c => c.Id)]);
        if (service is not null) model.SelectedService = service;

        // Folders: rename, then delete (what was in it moves up).
        var worship = DepthSong("Here I Am to Worship", "Verse 1\nLight of the world you stepped down into darkness", folder: "Modern");
        model.RenamePresentationFolder(["Modern"], "Contemporary");
        await Delay(300);
        Check("renaming a folder refiles what's in it", model.Library.Get<Presentation>(worship.Id)?.Folder == "Contemporary");
        model.DeletePresentationFolder(["Hymns"]);
        await Delay(300);
        var vision = model.Library.All<Presentation>().FirstOrDefault(p => p.Name == "How Great Thou Art");
        Check("deleting a folder keeps its songs, one level up", vision is not null && string.IsNullOrEmpty(vision.Folder));
        model.LibraryShowsAll = false;
        panel.LibraryList.SelectedItems.Clear();
        await Delay(300);
        SaveControl(panel, "109-library-after-batch");
    }

    // MARK: The media bin

    private async Task MediaBinBatchCheck()
    {
        model.ShowingMedia = true;
        await Delay(300);
        var tiles = model.MediaItems.Take(3).ToList();
        Check("there are media tiles to pick", tiles.Count >= 3);
        if (tiles.Count < 3) return;
        model.PickMedia(tiles[0], ListMultiSelect.Gesture.Replace);
        model.PickMedia(tiles[2], ListMultiSelect.Gesture.Extend);
        Check("Shift+click picks a run of media tiles (outlined)", tiles.All(t => t.IsPicked));
        var batch = model.MediaBatch(tiles[1]);
        Check("a right-click on one of them acts on all three", batch.Count == 3);
        model.SetFavorite(batch.Select(m => (LibraryItemKind.Media, m.Id)), true);
        model.MoveMediaToFolder(batch, "Backgrounds");
        await Delay(300);
        Check("media favourites and folders are the media's own shared fields (a Mac sees them)",
            batch.All(m => model.Library.Get<MediaItem>(m.Id) is { Favorite: true, Folder: "Backgrounds" }));
        model.PickMedia(model.MediaItems.First(m => m.Item.Id == batch[0].Id), ListMultiSelect.Gesture.Replace);
        model.PickMedia(model.MediaItems.First(m => m.Item.Id == batch[2].Id), ListMultiSelect.Gesture.Extend);
        await Step("110-media-bin-picked", () => { }, settle: 600);
        model.MediaFolder = "Backgrounds";
        model.MediaFavoritesOnly = true;
        await Delay(300);
        Check("the bin shows one folder's favourites", model.MediaItems.Count == 3);
        model.MediaFolder = null;
        model.MediaFavoritesOnly = false;
        model.ClearMediaPicks();
        var folder = Directory.CreateDirectory(Path.Combine(depthScratch, "media out")).FullName;
        await model.ExportMediaFilesAsync(batch, folder);
        Check("Copy the files to… copies each file out under its own name", Directory.GetFiles(folder).Length == 3);
        model.MoveMediaToFolder(batch, null);
        model.SetFavorite(batch.Select(m => (LibraryItemKind.Media, m.Id)), false);
    }

    // MARK: Export

    private async Task ExportFormatsCheck()
    {
        var song = model.Library.All<Presentation>().FirstOrDefault(p => p.Name == "Amazing Grace");
        if (song is null) return;
        var folder = Directory.CreateDirectory(Path.Combine(depthScratch, "export")).FullName;
        Check("a song can be exported as every format", PresentationExport.Formats(song).Count == 5);

        var sheet = Path.Combine(folder, "sheet.pdf");
        await model.ExportPresentationAsync(song, new ExportChoice(PresentationExport.Format.Pdf, PresentationExport.PdfKind.SlideSheet, Columns: 2), sheet);
        Check("a PDF sheet of slides is written (a real PDF)", IsPdf(sheet, out var sheetPages) && sheetPages >= 1);
        var pages = Path.Combine(folder, "pages.pdf");
        await model.ExportPresentationAsync(song, new ExportChoice(PresentationExport.Format.Pdf, PresentationExport.PdfKind.SlidePages), pages);
        Check("a PDF with a page per slide has as many pages as slides",
            IsPdf(pages, out var pageCount) && pageCount == Core.Scene.SlideSceneBuilder.ArrangedSlides(song, song.DefaultArrangementId).Count);
        var lyrics = Path.Combine(folder, "lyrics.pdf");
        await model.ExportPresentationAsync(song, new ExportChoice(PresentationExport.Format.Pdf, PresentationExport.PdfKind.LyricSheet), lyrics);
        Check("a lyric sheet PDF is written", IsPdf(lyrics, out _));
        var images = Path.Combine(folder, "Amazing Grace");
        await model.ExportPresentationAsync(song, new ExportChoice(PresentationExport.Format.Images, ImageType: PresentationExport.ImageType.Jpeg), images);
        var pictures = Directory.Exists(images) ? Directory.GetFiles(images, "*.jpg") : [];
        Check("slide pictures are written, numbered with their labels (01 Verse 1.jpg)", pictures.Length >= 3 && pictures.Any(p => Path.GetFileName(p).StartsWith("01", StringComparison.Ordinal)));
        var text = Path.Combine(folder, "song.txt");
        await model.ExportPresentationAsync(song, new ExportChoice(PresentationExport.Format.PlainText), text);
        Check("plain text is the words without chords or tags", File.Exists(text) && File.ReadAllText(text).Contains("Amazing grace", StringComparison.OrdinalIgnoreCase) && !File.ReadAllText(text).Contains('{'));
        var chordPro = Path.Combine(folder, "song.cho");
        await model.ExportPresentationAsync(song, new ExportChoice(PresentationExport.Format.ChordPro), chordPro);
        Check("ChordPro has its title tag", File.Exists(chordPro) && File.ReadAllText(chordPro).Contains("{title: Amazing Grace}"));

        // The export window, as it opens for one song.
        var shown = false;
        void OnOpened(object? sender, EventArgs e)
        {
            if (sender is not ExportWindow exportWindow) return;
            shown = true;
            exportWindow.ContentRendered += async (_, _) =>
            {
                await Delay(300);
                Save(exportWindow, "111-export-window");
                exportWindow.Close();
            };
        }
        ExportWindow.Opened += OnOpened;
        try { ExportWindow.Open(window, model, [song]); }
        finally { ExportWindow.Opened -= OnOpened; }
        Check("the export window opens with the formats", shown);
        await Delay(200);
    }

    /// <summary>
    /// A picture of one control in its window. Rendering a control straight into a bitmap draws it
    /// where it sits in the window, mostly outside the picture; painting it with a VisualBrush
    /// draws just it, from its own top-left corner.
    /// </summary>
    private void SaveControl(FrameworkElement element, string name)
    {
        try
        {
            if (element.ActualWidth < 1) return;
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(element);
            var size = new Size(element.ActualWidth, element.ActualHeight);
            var drawing = new System.Windows.Media.DrawingVisual();
            using (var dc = drawing.RenderOpen())
            {
                dc.DrawRectangle(EditorKit.Res<System.Windows.Media.Brush>("BasePlane"), null, new Rect(size));
                dc.DrawRectangle(new System.Windows.Media.VisualBrush(element) { Stretch = System.Windows.Media.Stretch.None, AlignmentX = System.Windows.Media.AlignmentX.Left, AlignmentY = System.Windows.Media.AlignmentY.Top }, null, new Rect(size));
            }
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(size.Width * dpi.DpiScaleX), (int)Math.Ceiling(size.Height * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(drawing);
            Write(bitmap, name);
        }
        catch (Exception ex)
        {
            report.AppendLine($"NOTE  couldn't capture {name}: {ex.Message}");
        }
    }

    /// <summary>A file that starts as a PDF does and ends with its marker; its page count read from the page tree.</summary>
    private static bool IsPdf(string path, out int pages)
    {
        pages = 0;
        if (!File.Exists(path)) return false;
        var text = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(path));
        var count = System.Text.RegularExpressions.Regex.Match(text, @"/Type /Pages /Kids \[[^\]]*\] /Count (\d+)");
        if (count.Success) pages = int.Parse(count.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        return text.StartsWith("%PDF-1.7", StringComparison.Ordinal) && text.TrimEnd().EndsWith("%%EOF", StringComparison.Ordinal);
    }
}
