using MxU.Slides.Core.Model;
using MxU.Slides.OpenLpImport;
using MxU.Slides.OpenLpImport.Testing;
using MxU.Slides.Windows.ViewModels;
using MxU.Slides.Windows.Views;

namespace MxU.Slides.Windows.Infrastructure;

// Phase 15, the OpenLP import. A made-up OpenLP data folder (OpenLpSampleData: twelve invented
// songs and three custom slides, laid out as OpenLP 3 does) is built in a scratch folder and
// imported through the real window and the same view-model call the menu uses. Then a song is
// opened and put live, and the import is run again to see that nothing doubles. No real OpenLP
// folder on this PC is ever looked at. Pictures 95-openlp-….
public sealed partial class SelfTest
{
    private async Task OpenLpCheck()
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"MxU self-test openlp {Guid.NewGuid():N}");
        OpenLpImportWindow? dialog = null;
        try
        {
            var data = OpenLpSampleData.WriteDataFolder(Path.Combine(scratch, "data"), OpenLpShape.V3);
            var songsFile = Path.Combine(data, "songs", "songs.sqlite");
            var bytesBefore = File.ReadAllBytes(songsFile);
            model.LibrarySearch = "";
            model.OpenLibraryFolder([]);
            var before = model.Library.All<Presentation>().Count;

            // The window as the operator first sees it, then the import through it.
            dialog = new OpenLpImportWindow(window, model);
            dialog.Show();
            await Delay(500);
            Save(dialog, "95-openlp-1-choose");
            await dialog.RunAsync(data);
            await Delay(500);
            Save(dialog, "95-openlp-2-summary");
            var result = dialog.Result;
            report.AppendLine($"      OpenLP import: {result?.Headline() ?? model.Problem}");
            Check("a made-up OpenLP data folder imports: 10 of its 12 songs and all 3 custom slides",
                result is { SongsFound: 12, SongsImported: 10, CustomSlidesFound: 3, CustomSlidesImported: 3 });
            Check("the damaged song and the empty one are listed, not fatal",
                result is not null && result.NotImported.Count == 2
                && result.Warnings.Any(w => w.Title == OpenLpSampleData.Broken) && result.Warnings.Any(w => w.Title == OpenLpSampleData.Empty));
            Check("the summary's Copy text has the counts and the things to check",
                dialog.ReportText.Contains("Songs imported: 10 of 12") && dialog.ReportText.Contains(OpenLpSampleData.Broken));
            Check("OpenLP's own file is untouched by the import", File.ReadAllBytes(songsFile).AsSpan().SequenceEqual(bytesBefore)
                && Directory.GetFiles(Path.GetDirectoryName(songsFile)!).Length == 1);
            dialog.Close();
            dialog = null;

            // The library: every imported song, and the custom slides in a folder of their own.
            var all = model.Library.All<Presentation>();
            Check("the library holds the imported songs", all.Count == before + 13
                && all.Any(p => p.Name == OpenLpSampleData.MorningLight) && all.Any(p => p.Name == OpenLpSampleData.Yoruba));
            Check("the library list shows them", model.LibraryItems.Any(e => e.Name == OpenLpSampleData.MorningLight)
                || model.LibraryRows.OfType<LibraryEntryViewModel>().Any(e => e.Name == OpenLpSampleData.MorningLight));
            Check("custom slides are in the “OpenLP custom slides” folder",
                all.Count(p => p.Folder == OpenLpSongMapper.CustomFolder) == 3
                && model.LibraryRows.OfType<LibraryFolderRowViewModel>().Any(f => f.Name == OpenLpSongMapper.CustomFolder));
            model.LibrarySearch = "kindness";
            await Step("95-openlp-3-library-search", () => { }, settle: 900);
            Check("an imported song is found by its words", model.LibraryRows.OfType<LibraryEntryViewModel>().Any(e => e.Name == OpenLpSampleData.Rivers));
            model.LibrarySearch = "";
            await Step("95-openlp-3-library", () => { }, settle: 700);

            // One song, opened and put live: the verse order is its arrangement.
            var song = all.FirstOrDefault(p => p.Name == OpenLpSampleData.MorningLight);
            if (song is null) return;
            model.Open(song, null, song.DefaultArrangementId);
            await Delay(500);
            var sung = song.Arrangements?.FirstOrDefault(a => a.Id == song.DefaultArrangementId)?.SectionIds
                .Select(id => song.Sections?.FirstOrDefault(s => s.Id == id)?.Name).ToList() ?? [];
            report.AppendLine($"      \"{song.Name}\": {model.Slides.Count} slides in the order {string.Join(", ", sung)}");
            Check("its verse order came across as the arrangement (the chorus three times)",
                sung.SequenceEqual(["Intro", "Verse 1", "Chorus", "Verse 2", "Chorus", "Bridge", "Chorus"]) && model.Slides.Count > (song.Slides.Count));
            Check("its credits came across", song.Ccli is { SongNumber: 7654321, Author: "Ada Testwell, Bayo Sample" });
            if (model.Slides.Count > 1)
            {
                await Step("95-openlp-4-song-live", () => model.GoLiveCommand.Execute(model.Slides[1]), settle: 1000);
                Check("an imported OpenLP slide goes live", AudienceText().Contains("Morning light has come again"));
                await Snapshot("95-openlp-4-audience", model.Show.Scene());
            }

            // Yoruba letters, on the audience screen.
            if (all.FirstOrDefault(p => p.Name == OpenLpSampleData.Yoruba) is { } yoruba)
            {
                model.Open(yoruba, null, yoruba.DefaultArrangementId);
                await Delay(400);
                if (model.Slides.Count > 1) model.GoLiveCommand.Execute(model.Slides[1]);
                await Delay(800);
                Check("Yoruba letters reach the audience screen exactly", AudienceText().Contains(OpenLpSampleData.YorubaLine, StringComparison.Ordinal));
                await Snapshot("95-openlp-5-yoruba-audience", model.Show.Scene());
            }

            // A chord song: the chords are kept as chords, never shown as words.
            if (all.FirstOrDefault(p => p.Name == OpenLpSampleData.Chords) is { } chords)
            {
                var words = string.Join("\n", chords.Slides.SelectMany(s => s.Objects).Select(o => o.Text));
                Check("chords are kept as chords and not shown as words",
                    !words.Contains('[') && chords.Slides.SelectMany(s => s.Objects).Sum(o => o.Chords?.Count ?? 0) == 8);
            }
            model.ClearAllCommand.Execute(null);

            // Again, the way the menu does it: nothing doubles.
            var again = await model.ImportOpenLpAsync(data);
            Check("importing the same OpenLP data again adds nothing",
                again is { SongsImported: 0, CustomSlidesImported: 0 } && again.SkippedDuplicates.Count == 13
                && model.Library.All<Presentation>().Count == before + 13);

            // Something that isn't OpenLP's data: a plain message, no crash, nothing imported.
            var wrong = Path.Combine(scratch, "holiday photos.sqlite");
            File.WriteAllText(wrong, "not a database");
            var problemBefore = model.Problem;
            var refused = await model.ImportOpenLpAsync(wrong);
            Check("a file that isn't an OpenLP database is refused with a plain message",
                refused is null && model.Problem?.Contains("songs.sqlite") == true && model.Library.All<Presentation>().Count == before + 13);
            model.Problem = problemBefore;
        }
        finally
        {
            dialog?.Close();
            model.LibrarySearch = "";
            model.ClearAllCommand.Execute(null);
            try { Directory.Delete(scratch, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
