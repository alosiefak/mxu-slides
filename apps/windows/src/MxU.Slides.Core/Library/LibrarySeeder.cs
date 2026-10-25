using System.Globalization;
using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Stage;

namespace MxU.Slides.Core.Library;

/// <summary>
/// Fills a new library with the starter themes and overlays, welcome deck, a
/// sample song, stage layouts and a service, and refreshes the starter
/// themes and overlays when a newer build ships updated ones.
/// </summary>
public static class LibrarySeeder
{
    private const string RevisionFile = "starter-revision.txt";

    public static void Seed(LibraryStore library, DateOnly? today = null)
    {
        RefreshStarterPacks(library);

        if (library.Get<Presentation>(StarterContent.WelcomePresentationId) is null)
            library.Save(StarterContent.WelcomePresentation());

        var song = library.Get<Presentation>(StarterContent.SampleSongId);
        if (song is null)
        {
            song = LyricTextImporter.MakePresentation(StarterContent.AmazingGraceLyrics, "Amazing Grace",
                id: StarterContent.SampleSongId, themeId: StarterPack.CleanGeometric.ThemeId());
            song.Name = "Amazing Grace";
            song.PresentationKind = PresentationKind.Song;
            song.Ccli = new CCLIInfo { SongTitle = "Amazing Grace", Author = "John Newton", Copyright = "Public Domain" };
            library.Save(song);
        }

        if (library.Count<ConfidenceLayout>() == 0)
            foreach (var template in ConfidenceLayoutTemplates.WorkspaceStarters.Prepend(ConfidenceLayoutTemplate.Classic))
                library.Save(template.Make(template.StarterName()));

        if (library.Get<Service>(StarterContent.WelcomeServiceId) is null)
            library.Save(StarterContent.WelcomeService(today ?? DateOnly.FromDateTime(DateTime.Today), song));
    }

    /// <summary>
    /// Saves the starter themes and overlays when they are missing or older than
    /// <see cref="StarterPacks.Revision"/>. Only documents with the packs' own ids
    /// are touched; copies you made and renamed are left alone.
    /// </summary>
    public static void RefreshStarterPacks(LibraryStore library)
    {
        var marker = Path.Combine(library.Root, RevisionFile);
        var current = File.Exists(marker) && int.TryParse(File.ReadAllText(marker).Trim(), out var r) ? r : 0;
        var outdated = current < StarterPacks.Revision;
        foreach (var pack in StarterPacks.All)
        {
            if (outdated || library.Get<Theme>(pack.ThemeId()) is null) library.Save(pack.MakeTheme());
            foreach (var overlay in pack.MakeOverlays())
                if (outdated || library.Get<Overlay>(overlay.Id) is null) library.Save(overlay);
        }
        if (outdated)
            LibraryStore.AtomicWrite(marker, System.Text.Encoding.UTF8.GetBytes(StarterPacks.Revision.ToString(CultureInfo.InvariantCulture)));
        AddWindowsStarters(library);
    }

    private const string WindowsStartersFile = "windows-starters.txt";

    /// <summary>
    /// What Windows adds beyond the Mac's starters (Phase 6): the worship looks' kinetic builds,
    /// saved beside their themes as Windows extras, and the pre-service countdown deck. A new pack
    /// needs no revision bump: like any missing starter theme, <see cref="RefreshStarterPacks"/>
    /// saves it the first time a library opens without it, so an existing library gains the new
    /// looks and nothing anyone edited is touched. The deck is added once per library (a list in
    /// windows-starters.txt remembers it), so deleting it isn't undone at the next launch.
    /// </summary>
    private static void AddWindowsStarters(LibraryStore library)
    {
        new KineticExtras(library.Extras).Seed(StarterPacks.All.SelectMany(p => p.KineticBuilds()));

        var list = Path.Combine(library.Root, WindowsStartersFile);
        var added = File.Exists(list) ? File.ReadAllLines(list).Select(l => l.Trim()).Where(l => l.Length > 0).ToHashSet(StringComparer.Ordinal) : [];
        if (added.Contains(StarterContent.CountdownPresentationId)) return;
        if (library.Get<Presentation>(StarterContent.CountdownPresentationId) is null) library.Save(StarterContent.CountdownPresentation());
        added.Add(StarterContent.CountdownPresentationId);
        LibraryStore.AtomicWrite(list, System.Text.Encoding.UTF8.GetBytes(string.Join("\n", added.Order(StringComparer.Ordinal)) + "\n"));
    }
}
