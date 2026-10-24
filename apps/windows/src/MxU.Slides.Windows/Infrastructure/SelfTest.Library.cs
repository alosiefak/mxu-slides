using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.ViewModels;
using MxU.Slides.Windows.Views;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>
/// Library exchange with the Mac, and missing media, driven through the real windows:
/// exporting a song as .mxuslides and importing it back (Keep Both, Skip, Replace), importing a
/// file laid out as the Mac writes one, and a dropped one; a picture whose file goes missing
/// (badges on the media tile, the slide and the service rows), the delete warning, Locate
/// (a folder scan finds a renamed copy), Replace everywhere and Replace Anyway, a deleted
/// item brought back; videos flagged at import and one transcoded; the duplicate prompt.
/// Every dialog is the real one, pictured and then answered (<see cref="LibraryDialogs.Showing"/>).
/// Pictures 70-… to 84-….
/// </summary>
public sealed partial class SelfTest
{
    private readonly Queue<(string Answer, string Picture)> dialogAnswers = new();
    private readonly List<string> dialogHeadlines = [];
    private string libraryScratch = "";

    private async Task LibraryCheck()
    {
        libraryScratch = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"MxU self-test exchange {Guid.NewGuid():N}")).FullName;
        LibraryDialogs.Showing = AnswerDialog;
        var chooseFile = model.ChooseMediaFile;
        model.ShowingMedia = true;
        try
        {
            await PresentationExchangeCheck();
            await MissingMediaCheck();
            await FlaggedMediaCheck();
            await DuplicateImportCheck();
        }
        finally
        {
            LibraryDialogs.Showing = null;
            model.ChooseMediaFile = chooseFile;
            dialogAnswers.Clear();
            model.ClearAllCommand.Execute(null);
            try { Directory.Delete(libraryScratch, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>The next dialog is pictured, then answered with this button.</summary>
    private void ExpectDialog(string answer, string picture) => dialogAnswers.Enqueue((answer, picture));

    private void AnswerDialog(ChoiceWindow dialog)
    {
        dialogHeadlines.Add(dialog.Headline);
        var (answer, picture) = dialogAnswers.Count > 0 ? dialogAnswers.Dequeue() : ("", "");
        dialog.ContentRendered += async (_, _) =>
        {
            await Delay(350);
            if (picture.Length > 0) Save(dialog, picture);
            if (answer.Length == 0)
            {
                // Not expected: say so, and close it rather than leave the test waiting on it.
                Check($"no unexpected question (“{dialog.Headline}”)", false);
                dialog.Close();
            }
            else
            {
                dialog.Choose(answer);
            }
        };
    }

    private string? LastHeadline => dialogHeadlines.LastOrDefault();

    // MARK: .mxuslides

    private async Task PresentationExchangeCheck()
    {
        var song = model.Library.All<Presentation>().FirstOrDefault(p => p.Name == "Amazing Grace");
        Check("the sample song is in the library to export", song is not null);
        if (song is null) return;
        var file = Path.Combine(libraryScratch, SlidesPresentationFile.FileName(song));
        var summary = await model.ExportPresentationAsync(song, file);
        Check("a song exports as a .mxuslides file", summary is not null && File.Exists(file));
        if (summary is null) return;
        using (var zip = ZipFile.OpenRead(file))
        {
            var names = zip.Entries.Select(e => e.FullName).ToList();
            Check("the file has the Mac's layout (manifest.json, presentation.json, blobs/, fonts/ at the top)",
                names.Contains("manifest.json") && names.Contains("presentation.json") && names.Contains("blobs/") && names.Contains("fonts/"));
            using var stream = zip.GetEntry("manifest.json")!.Open();
            var manifest = JsonNode.Parse(stream)!;
            Check("its manifest names the song, counts its slides and dates it the Mac's way",
                manifest["name"]?.GetValue<string>() == "Amazing Grace" && manifest["slideCount"]?.GetValue<int>() == song.Slides.Count
                && System.Text.RegularExpressions.Regex.IsMatch(manifest["createdAt"]!.GetValue<string>(), @"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$"));
        }

        // Back in: it's already here, so the real "Keep Both / Replace / Skip" question comes up.
        var before = model.Library.Count<Presentation>();
        ExpectDialog("Keep Both", "70-library-import-already-here");
        var landed = await model.ImportPresentationFilesAsync([file]);
        Check("importing it again asks first (“already in your library”)", LastHeadline == "“Amazing Grace” is already in your library");
        var copyId = landed.FirstOrDefault();
        Check("Keep Both adds a copy under a new id and opens it",
            copyId is not null && copyId != song.Id && model.Library.Count<Presentation>() == before + 1 && model.CurrentPresentation?.Id == copyId);
        await Step("71-library-imported-copy", () => { }, settle: 700);

        ExpectDialog("Skip", "");
        await model.ImportPresentationFilesAsync([file]);
        Check("Skip imports nothing", model.Library.Count<Presentation>() == before + 1);

        song.Name = "Amazing Grace (edited here)";
        model.SavePresentation(song);
        ExpectDialog("Replace", "");
        await model.ImportPresentationFilesAsync([file]);
        Check("Replace puts the file's version back", model.Library.Get<Presentation>(song.Id)?.Name == "Amazing Grace"
            && model.Library.Count<Presentation>() == before + 1);
        if (copyId is not null) model.Library.Delete<Presentation>(copyId);

        // A file laid out the way the Mac writes one, carrying a theme this PC hasn't got, a picture, and a video it left out.
        var macFile = WriteMacStyleFile(Path.Combine(libraryScratch, "From the Mac.mxuslides"));
        await window.ImportFiles([macFile]); // the same path a file dropped on the library takes
        var deck = model.Library.Get<Presentation>("SELFTEST-MAC-DECK");
        Check("a Mac-made .mxuslides imports (dropped on the library)", deck?.Slides.Count == 2 && model.CurrentPresentation?.Id == deck.Id);
        Check("its theme and picture come with it", model.Library.Get<Theme>("SELFTEST-MAC-THEME") is not null
            && model.Library.Get<MediaItem>("SELFTEST-MAC-PICTURE") is { } picture && model.Library.BlobPath(picture) is not null);
        Check("the video it didn't include is reported", model.Problem?.Contains("the file didn't include Walk-in Loop", StringComparison.Ordinal) == true);
        Check("that slide says its media is missing", model.Slides.ElementAtOrDefault(1)?.MissingMedia is not null && model.Slides[0].MissingMedia is null);
        await Step("72-library-mac-file", () => { }, settle: 900);
        if (model.Slides.Count == 2)
        {
            model.GoLiveCommand.Execute(model.Slides[0]);
            await Delay(1200);
            Check("the Mac's slide goes live with its background", AudienceText().Contains("Made on a Mac", StringComparison.Ordinal)
                && model.Show.Scene().MediaIds.Contains("SELFTEST-MAC-PICTURE"));
            await Snapshot("72b-library-mac-file-live", model.Show.Scene());
            model.ClearAllCommand.Execute(null);
        }
        model.Problem = null;
    }

    /// <summary>A .mxuslides as the Mac's JSONEncoder (pretty, sorted keys, "\/") and ditto would make it.</summary>
    private string WriteMacStyleFile(string path)
    {
        var picture = Path.Combine(libraryScratch, "mac-background.png");
        WritePicture(picture, Color.FromRgb(0x14, 0x5A, 0x8C), Color.FromRgb(0xF2, 0xB1, 0x34), 480, 270);
        var bytes = File.ReadAllBytes(picture);
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var manifest = """
            {
              "createdAt" : "2026-10-07T09:30:00Z",
              "formatVersion" : 1,
              "name" : "From the Mac",
              "slideCount" : 2
            }
            """;
        var payload = $$"""
            {
              "audio" : [

              ],
              "media" : [
                {
                  "classification" : "background",
                  "collections" : [

                  ],
                  "favorite" : false,
                  "fileHash" : "{{hash}}",
                  "fileName" : "Mac Background.png",
                  "fileStatus" : "ready",
                  "folder" : "Team Media\/Backgrounds",
                  "id" : "SELFTEST-MAC-PICTURE",
                  "loops" : false,
                  "mediaKind" : "image",
                  "name" : "Mac Background",
                  "pixelHeight" : 270,
                  "pixelWidth" : 480,
                  "statusDetail" : "",
                  "tags" : [

                  ]
                },
                {
                  "classification" : "background",
                  "collections" : [

                  ],
                  "favorite" : false,
                  "fileHash" : "{{new string('7', 64)}}",
                  "fileName" : "Walk-in Loop.mov",
                  "fileStatus" : "ready",
                  "id" : "SELFTEST-MAC-LOOP",
                  "loops" : true,
                  "mediaKind" : "video",
                  "name" : "Walk-in Loop",
                  "statusDetail" : "",
                  "tags" : [

                  ]
                }
              ],
              "presentation" : {
                "folder" : "Songs\/Imported",
                "id" : "SELFTEST-MAC-DECK",
                "name" : "From the Mac",
                "presentationKind" : "deck",
                "slides" : [
                  {
                    "background" : {
                      "mediaId" : "SELFTEST-MAC-PICTURE"
                    },
                    "id" : "SELFTEST-MAC-S1",
                    "name" : "Welcome",
                    "objects" : [
                      {
                        "height" : 300,
                        "id" : "SELFTEST-MAC-O1",
                        "name" : "Title",
                        "objectKind" : "text",
                        "text" : "Made on a Mac",
                        "width" : 1600,
                        "x" : 160,
                        "y" : 390
                      }
                    ]
                  },
                  {
                    "background" : {
                      "mediaId" : "SELFTEST-MAC-LOOP"
                    },
                    "id" : "SELFTEST-MAC-S2",
                    "name" : "Walk in",
                    "objects" : [

                    ]
                  }
                ],
                "themeId" : "SELFTEST-MAC-THEME"
              },
              "themes" : [
                {
                  "backgroundColorHex" : "#202060",
                  "fontFamily" : "Helvetica Neue",
                  "fontSize" : 110,
                  "id" : "SELFTEST-MAC-THEME",
                  "name" : "Mac Theme",
                  "textColorHex" : "#FFFFFF"
                }
              ]
            }
            """;
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        zip.CreateEntry("blobs/");
        using (var entry = zip.CreateEntry($"blobs/{hash}.png").Open()) entry.Write(bytes);
        zip.CreateEntry("fonts/");
        using (var entry = zip.CreateEntry("manifest.json").Open()) entry.Write(Encoding.UTF8.GetBytes(manifest));
        using (var entry = zip.CreateEntry("presentation.json").Open()) entry.Write(Encoding.UTF8.GetBytes(payload));
        return path;
    }

    // MARK: Missing media

    private async Task MissingMediaCheck()
    {
        var first = Path.Combine(libraryScratch, "Self-test Sunrise.png");
        WritePicture(first, Color.FromRgb(0xE8, 0x6A, 0x2C), Color.FromRgb(0xFF, 0xD2, 0x6E), 640, 360);
        var imported = await model.ImportMediaFilesAsync([first]);
        var item = imported.FirstOrDefault();
        Check("a picture imports with its size read by Windows", item is { MediaKind: MediaKind.Image, PixelWidth: 640, PixelHeight: 360, FileStatus: MediaFileStatus.Ready });
        if (item is null) return;

        // A song that uses it: as slide 1's background, and on the service as a media row.
        var deck = new Presentation
        {
            Id = "SELFTEST-MISSING-DECK", Name = "Self-test Missing Media", PresentationKind = PresentationKind.Deck, ThemeId = "",
            Slides =
            [
                new Slide { Id = "SELFTEST-MISSING-S1", Name = "One", Background = new CueMedia { MediaId = item.Id },
                    Objects = [new SlideObject { Id = "SELFTEST-MISSING-O1", ObjectKind = SlideObjectKind.Text, Name = "Title", Text = "Sunrise", X = 160, Y = 400, Width = 1600, Height = 280 }] },
                new Slide { Id = "SELFTEST-MISSING-S2", Name = "Two", Objects = [] },
            ],
        };
        model.Library.Save(deck);
        model.Open(deck, null, null);
        model.AddCurrentToServiceCommand.Execute(null);
        model.AddMediaToService(item);

        File.Delete(model.Library.BlobPath(item)!);
        model.RefreshMedia();
        model.RebuildSlides();
        model.RefreshServiceItems();
        var tile = model.MediaItems.FirstOrDefault(t => t.Item.Id == item.Id);
        Check("a media tile whose file is gone says FILE MISSING", tile is { Badge: "FILE MISSING", IsFileMissing: true });
        Check("the slide that shows it is marked", model.Slides.ElementAtOrDefault(0)?.MissingMedia?.Contains("Self-test Sunrise", StringComparison.Ordinal) == true
            && model.Slides.ElementAtOrDefault(1)?.MissingMedia is null);
        Check("the service rows say so", model.ServiceItems.Any(r => r.Item.RefId == item.Id && r.Detail == "File missing")
            && model.ServiceItems.Any(r => r.Item.RefId == deck.Id && r.Detail == "1 media file missing"));
        Check("MediaHub draws nothing for it (no file), rather than a stale picture", model.Library.BlobPath(item) is null);
        await Step("73-library-missing-badges", () => { }, settle: 900);

        // Deleting something still used says where, first.
        ExpectDialog("Cancel", "74-library-delete-warning");
        var deleted = await model.DeleteMediaAsync(item);
        Check("deleting used media warns first, naming where it's used", !deleted && LastHeadline == "Delete “Self-test Sunrise”?"
            && model.Library.Get<MediaItem>(item.Id) is not null);

        // Locate: a renamed copy on "a USB stick" is found by its content.
        var stick = Directory.CreateDirectory(Path.Combine(libraryScratch, "USB stick", "Photos")).FullName;
        File.Copy(first, Path.Combine(stick, "IMG_0042 (renamed).png"));
        var locate = LocateMissingMediaWindow.Open(window, model);
        await Delay(900);
        Check("Locate Missing Media lists the item", locate.Missing.Any(m => m.Id == item.Id));
        Save(locate, "75-library-locate-window");
        await locate.ScanAsync(Path.Combine(libraryScratch, "USB stick"));
        Check("a folder scan relinks the renamed copy by content", locate.Summary.StartsWith("Relinked 1 item", StringComparison.Ordinal)
            && model.Library.BlobPath(item) is not null);
        await Delay(500);
        Save(locate, "75b-library-locate-scanned");
        locate.Close();
        tile = model.MediaItems.FirstOrDefault(t => t.Item.Id == item.Id);
        Check("its badges go once it's found", tile is { Badge: null } && model.Slides.ElementAtOrDefault(0)?.MissingMedia is null
            && !model.ServiceItems.Any(r => r.Detail is "File missing" or "1 media file missing"));
        await Step("76-library-relinked", () => { }, settle: 900);

        // Replace everywhere: the real "Replace … everywhere?" list, then the new file.
        var second = Path.Combine(libraryScratch, "Self-test Night.png");
        WritePicture(second, Color.FromRgb(0x10, 0x16, 0x3A), Color.FromRgb(0x6C, 0x8C, 0xFF), 800, 450);
        model.ChooseMediaFile = _ => second;
        ExpectDialog("Replace Everywhere…", "77-library-replace-everywhere");
        var replaced = await model.ReplaceMediaFileEverywhereAsync(item);
        var swapped = model.Library.Get<MediaItem>(item.Id);
        Check("Replace everywhere lists where it's used, then swaps the file under the same item",
            replaced && LastHeadline == "Replace “Self-test Sunrise” everywhere?" && swapped is { PixelWidth: 800, FileName: "Self-test Night.png" }
            && swapped.FileHash != item.FileHash);
        Check("every use follows (the slide still points at the item)", model.Library.Get<Presentation>(deck.Id)?.Slides[0].Background?.MediaId == item.Id);
        await Snapshot("77b-library-replaced-slide", SlideSceneBuilderScene(deck.Id));

        // A file another item already has: "Replace Anyway?", answered Cancel.
        var other = model.Library.All<MediaItem>().FirstOrDefault(m => m.Id != item.Id && m.MediaKind == MediaKind.Image && model.Library.BlobPath(m) is not null);
        if (other is not null)
        {
            model.ChooseMediaFile = _ => model.Library.BlobPath(other);
            ExpectDialog("Replace Everywhere…", "");
            ExpectDialog("Cancel", "78-library-replace-anyway");
            var anyway = await model.ReplaceMediaFileEverywhereAsync(item);
            Check("choosing another item's file asks Replace Anyway (and Cancel keeps the file)", !anyway
                && LastHeadline == "This file is already in your library" && model.Library.Get<MediaItem>(item.Id)?.FileHash == swapped?.FileHash);
        }

        // Deleted for real, then brought back from the Locate window under its own id.
        ExpectDialog("Move to Trash", "");
        await model.DeleteMediaAsync(item);
        Check("Move to Trash deletes it", model.Library.Get<MediaItem>(item.Id) is null);
        model.Open(model.Library.Get<Presentation>(deck.Id)!, null, null);
        Check("the slide that used it says so", model.Slides.ElementAtOrDefault(0)?.MissingMedia is not null);
        locate = LocateMissingMediaWindow.Open(window, model);
        await Delay(900);
        var tombstone = locate.Tombstones.FirstOrDefault(t => t.Id == item.Id);
        Check("Locate lists it under deleted items still in use, from the Trash", tombstone is { Name: "Self-test Sunrise" });
        Save(locate, "79-library-locate-deleted");
        if (tombstone is not null) await locate.UseFileAsync(tombstone, isTombstone: true, first);
        locate.Close();
        Check("Choose File brings it back under its own id, healing the slide", model.Library.Get<MediaItem>(item.Id) is { Name: "Self-test Sunrise" }
            && model.Slides.ElementAtOrDefault(0)?.MissingMedia is null);
        await Step("79b-library-restored", () => { }, settle: 700);

        model.Library.Delete<Presentation>(deck.Id);
        foreach (var row in model.ServiceItems.Where(r => r.Item.RefId == deck.Id || r.Item.RefId == item.Id).ToList())
            model.RemoveServiceItemCommand.Execute(row);
    }

    private Core.Scene.RenderScene SlideSceneBuilderScene(string presentationId)
    {
        var p = model.Library.Get<Presentation>(presentationId)!;
        return Core.Scene.SlideSceneBuilder.Scene(p.Slides[0], model.Library.Get<Theme>(p.ThemeId), p);
    }

    // MARK: Flagged at import, and transcoding

    private async Task FlaggedMediaCheck()
    {
        var broken = Path.Combine(libraryScratch, "Self-test Broken.mp4");
        File.WriteAllBytes(broken, [.. Enumerable.Range(0, 8192).Select(i => (byte)(i * 37 % 251))]);
        var raw = Path.Combine(libraryScratch, "Self-test Camera.avi");
        WriteUncompressedAvi(raw, 320, 180, 45);
        var unreadable = Path.Combine(libraryScratch, "Self-test Unreadable.png");
        File.WriteAllBytes(unreadable, [1, 2, 3, 4, 5, 6, 7, 8, 9]);

        var imported = await model.ImportMediaFilesAsync([broken, raw, unreadable]);
        var brokenItem = imported.FirstOrDefault(m => m.FileName == "Self-test Broken.mp4");
        var rawItem = imported.FirstOrDefault(m => m.FileName == "Self-test Camera.avi");
        Check("a video Windows can't open is flagged at import, not refused", brokenItem is { FileStatus: MediaFileStatus.NeedsTranscode }
            && brokenItem.StatusDetail.StartsWith("unreadable by Windows", StringComparison.Ordinal));
        Check("a video Windows decodes but plays poorly is flagged with its codec", rawItem is { FileStatus: MediaFileStatus.NeedsTranscode, PixelWidth: 320 }
            && rawItem.StatusDetail.StartsWith("unsupported codec", StringComparison.Ordinal));
        Check("a picture Windows can't read is skipped, saying why", imported.All(m => m.FileName != "Self-test Unreadable.png")
            && model.Problem?.Contains("Windows can't read this picture", StringComparison.Ordinal) == true);
        Check("their tiles say NEEDS TRANSCODE", model.MediaItems.Count(t => t.Badge == "NEEDS TRANSCODE" && t.Item.FileName.StartsWith("Self-test", StringComparison.Ordinal)) == 2);
        await Step("80-library-flagged-at-import", () => { }, settle: 900);
        model.Problem = null;
        if (rawItem is null) return;

        Check("Transcode now is offered", model.MediaItems.FirstOrDefault(t => t.Item.Id == rawItem.Id)?.CanTranscode == true);
        var status = await model.TranscodeMediaAsync(rawItem);
        var copy = model.Library.Get<MediaItem>(rawItem.Id);
        Check("Transcode now makes an H.264 copy and the item is ready", status == MediaFileStatus.Ready && copy is { FileStatus: MediaFileStatus.Ready, StatusDetail: "" }
            && copy.FileHash != rawItem.FileHash && copy.DurationSeconds is > 1.2 and < 1.8);
        await Step("81-library-transcoded", () => { }, settle: 2500); // time for the new poster
        if (copy is not null)
        {
            copy.Classification = MediaClassification.Foreground;
            copy.Loops = false;
            model.Library.Save(copy);
            model.FireMedia(copy);
            var key = $"live:live-media-videos:{copy.Id}";
            for (var i = 0; i < 150 && model.Media.Transport(key) is null && !model.Media.IsVideoFailed(key); i++) await Delay(100);
            Check("the transcoded copy plays", model.Media.Transport(key) is not null && !model.Media.IsVideoFailed(key));
            await Delay(400);
            await Snapshot("81b-library-transcoded-playing", model.Show.Scene());
            model.ClearMediaCommand.Execute(null);
        }
        if (brokenItem is not null)
        {
            var failed = await model.TranscodeMediaAsync(brokenItem);
            Check("a file that can't be decoded says why its transcode failed", failed == MediaFileStatus.TranscodeFailed
                && model.MediaItems.FirstOrDefault(t => t.Item.Id == brokenItem.Id)?.Badge == "TRANSCODE FAILED");
            model.Problem = null;
        }
    }

    // MARK: The duplicate prompt

    private async Task DuplicateImportCheck()
    {
        var logo = Path.Combine(libraryScratch, "Self-test Logo.png");
        WritePicture(logo, Color.FromRgb(0x22, 0x22, 0x22), Color.FromRgb(0x7E, 0xD9, 0x57), 400, 225);
        var first = (await model.ImportMediaFilesAsync([logo])).FirstOrDefault();
        Check("a new picture imports without a question", first is not null && dialogHeadlines.LastOrDefault() != "“Self-test Logo.png” is already in your library");
        if (first is null) return;
        var count = model.Library.Count<MediaItem>();

        ExpectDialog("Use Existing", "82-library-duplicate-same-file");
        var again = await model.ImportMediaFilesAsync([logo]);
        Check("the same file again asks, and Use Existing adds nothing", LastHeadline == "“Self-test Logo.png” is already in your library"
            && again.FirstOrDefault()?.Id == first.Id && model.Library.Count<MediaItem>() == count);

        ExpectDialog("Add as New Copy", "");
        var copy = (await model.ImportMediaFilesAsync([logo])).FirstOrDefault();
        Check("Add as New Copy adds it numbered like Finder", copy is { Name: "Self-test Logo 2" } && copy.Id != first.Id && copy.FileHash == first.FileHash);

        var elsewhere = Directory.CreateDirectory(Path.Combine(libraryScratch, "Another folder")).FullName;
        var sameName = Path.Combine(elsewhere, "Self-test Logo.png");
        WritePicture(sameName, Color.FromRgb(0x50, 0x10, 0x10), Color.FromRgb(0xEF, 0x44, 0x44), 400, 225);
        ExpectDialog("Use Existing", "83-library-duplicate-same-name");
        await model.ImportMediaFilesAsync([sameName]);
        Check("a different file under a name the library has asks too (with both pictures)",
            LastHeadline == "“Self-test Logo” is already in your library" && model.Library.Count<MediaItem>() == count + 1);
        await Step("84-library-after-imports", () => { }, settle: 600);
    }

    // MARK: Files for the checks

    /// <summary>A picture: a diagonal two-colour gradient with a circle, so a swap is easy to see.</summary>
    private static void WritePicture(string path, Color from, Color to, int width, int height)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(from, to, 45), null, new Rect(0, 0, width, height));
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)), null, new Point(width * 0.7, height * 0.4), height * 0.22, height * 0.22);
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    /// <summary>An AVI of raw (uncompressed) pictures: Windows reads it without a codec, but it's no file to play in a service.</summary>
    private static void WriteUncompressedAvi(string path, int width, int height, int frames)
    {
        var frameBytes = width * height * 3;
        using var file = new BinaryWriter(File.Create(path));
        void Code(BinaryWriter w, string code) => w.Write(Encoding.ASCII.GetBytes(code));
        var header = new MemoryStream();
        using (var h = new BinaryWriter(header, Encoding.ASCII, leaveOpen: true))
        {
            Code(h, "hdrl");
            Code(h, "avih");
            h.Write(56);
            foreach (var value in new[] { 33_333, frameBytes * 30, 0, 0x10, frames, 0, 1, frameBytes, width, height, 0, 0, 0, 0 }) h.Write(value);
            var stream = new MemoryStream();
            using (var s = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
            {
                Code(s, "strl");
                Code(s, "strh");
                s.Write(56);
                Code(s, "vids");
                Code(s, "DIB ");
                foreach (var value in new[] { 0, 0, 0, 1, 30, 0, frames, frameBytes, -1, 0 }) s.Write(value);
                s.Write((short)0);
                s.Write((short)0);
                s.Write((short)width);
                s.Write((short)height);
                Code(s, "strf");
                s.Write(40);
                foreach (var value in new[] { 40, width, height }) s.Write(value);
                s.Write((short)1);
                s.Write((short)24);
                foreach (var value in new[] { 0, frameBytes, 0, 0, 0, 0 }) s.Write(value);
            }
            Code(h, "LIST");
            h.Write((int)stream.Length);
            h.Write(stream.ToArray());
        }
        var chunk = 8 + frameBytes;
        var movi = 4 + frames * chunk;
        Code(file, "RIFF");
        file.Write(4 + 8 + (int)header.Length + 8 + movi + 8 + frames * 16);
        Code(file, "AVI ");
        Code(file, "LIST");
        file.Write((int)header.Length);
        file.Write(header.ToArray());
        Code(file, "LIST");
        file.Write(movi);
        Code(file, "movi");
        var picture = new byte[frameBytes];
        for (var f = 0; f < frames; f++)
        {
            for (var i = 0; i < picture.Length; i += 3)
            {
                var x = i / 3 % width;
                var lit = Math.Abs(x - f * 6 % width) < 10;
                (picture[i], picture[i + 1], picture[i + 2]) = lit ? ((byte)240, (byte)240, (byte)240) : ((byte)140, (byte)70, (byte)30);
            }
            Code(file, "00db");
            file.Write(frameBytes);
            file.Write(picture);
        }
        Code(file, "idx1");
        file.Write(frames * 16);
        for (var f = 0; f < frames; f++)
        {
            Code(file, "00db");
            file.Write(0x10);
            file.Write(4 + f * chunk);
            file.Write(frameBytes);
        }
    }
}
