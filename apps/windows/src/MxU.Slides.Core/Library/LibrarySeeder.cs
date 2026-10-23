using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Stage;

namespace MxU.Slides.Core.Library;

/// <summary>Fills a new library with the starter themes, welcome deck, a sample song, stage layouts and a service.</summary>
public static class LibrarySeeder
{
    public static void Seed(LibraryStore library, DateOnly? today = null)
    {
        foreach (var pack in StarterPacks.All)
            if (library.Get<Theme>(pack.ThemeId()) is null) library.Save(pack.MakeTheme());

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
}
