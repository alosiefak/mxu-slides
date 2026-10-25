using MxU.Slides.Core.Library;
using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.PlanningCenter;
using MxU.Slides.Core.Scene;
using MxU.Slides.Core.Tests.Library;

namespace MxU.Slides.Core.Tests.PlanningCenter;

public sealed class PlanImportTests : ScratchFolderTest
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly PlanImportOptions Options = new("theme-1", 2, TimeZoneInfo.FindSystemTimeZoneById("America/Chicago"));

    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The songs a church might already have: one by CCLI number (named differently), and two that share a title.</summary>
    private static LibraryStore WithSongs(LibraryStore library)
    {
        library.Save(Song("river", "River of Mercy (Live)", ccli: 9000202, author: "Jo Field"));
        library.Save(Song("faithful-ruth", "Faithful Still", author: "Ruth Lane"));
        library.Save(Song("faithful-tom", "Faithful Still", author: "Tom Gray"));
        return library;
    }

    private static Presentation Song(string id, string name, int? ccli = null, string? author = null) =>
        SongEditing.Build(null, "Verse 1\nA line\nAnother line", 2, new SongEditing.Details(name, author, ccli), "theme-1") is var song
            ? Retitled(song, id)
            : throw new InvalidOperationException();

    private static Presentation Retitled(Presentation song, string id)
    {
        song.Id = id;
        return song;
    }

    private static async Task<PcoPlanContents> Plan(FakePlanningCenter fake)
    {
        using var client = fake.Client();
        return await client.PlanAsync("1100001", "80000001", "Sunday Morning", Ct);
    }

    private static ServiceItem Row(Service service, string pcoItemId) => service.Items.Single(i => i.MxuPcoItemId == pcoItemId);

    [Fact]
    public async Task APlanBecomesAServiceWithItsRowsLengthsAndDate()
    {
        var library = WithSongs(NewLibrary());
        var summary = PlanImporter.Import(library, await Plan(FakePlanningCenter.WithFixtures()), Options, Now);
        var service = library.Get<Service>(summary.ServiceId)!;

        Assert.Equal("Sunday Morning · Week 3: Grace", service.Name);
        Assert.Equal("2026-11-01", service.ServiceDate);
        Assert.Equal(
            [ServiceItemKind.Header, ServiceItemKind.Info, ServiceItemKind.Info, ServiceItemKind.Header, ServiceItemKind.Presentation,
             ServiceItemKind.Presentation, ServiceItemKind.Presentation, ServiceItemKind.Info, ServiceItemKind.Info, ServiceItemKind.Presentation],
            service.Items.Select(i => i.ItemKind));
        Assert.Equal(["Pre-Service", "Countdown", "Welcome & Announcements", "Worship", "Morning Light", "River of Mercy (Live)",
            "Faithful Still", "Open Skies", "Message: Grace That Holds", "Steady Ground"], service.Items.Select(i => i.Name));
        // Item lengths become each row's Duration (the service tracking timers count them); headers have none.
        Assert.Equal([null, 300, 240, null, 312, 290, 260, 240, 1800, 275], service.Items.Select(i => i.Duration));
        Assert.Equal(["header", "media", "item", "header", "song", "song", "song", "song", "item", "song"], service.Items.Select(i => i.MxuKind));
        Assert.All(service.Items, i => Assert.NotNull(i.MxuPcoItemId));
        Assert.Equal(10, summary.Rows);
        Assert.False(summary.Updated);
    }

    [Fact]
    public async Task ARowKeepsPlanningCentersFactsInTheSharedFieldsForThem()
    {
        var library = WithSongs(NewLibrary());
        var service = library.Get<Service>(PlanImporter.Import(library, await Plan(FakePlanningCenter.WithFixtures()), Options, Now).ServiceId)!;
        var morning = Row(service, "500000005");
        Assert.Equal(("30000001", "40000001", "45000001"), (morning.MxuPcoSongId, morning.MxuPcoArrangementId, morning.MxuPcoKeyId));
        Assert.Equal(("Morning Light", 9000101, "Sam Rivers and Ada Moss"), (morning.MxuSongTitle, morning.MxuCcliNumber, morning.MxuSongAuthor));
        Assert.Equal(("Default Arrangement", "G", 72.0, "4/4"), (morning.MxuArrangementName, morning.MxuSongKey, morning.MxuBpm, morning.MxuTimeSignature));
        Assert.Equal("Audio", Assert.Single(morning.MxuNotes!).Label);
        Assert.Equal("Acoustic guitar starts alone", morning.MxuNotes![0].Text);
        var welcome = Row(service, "500000003");
        Assert.Equal(("Pastor Dan", "during"), (welcome.MxuDescription, welcome.MxuServicePosition));
        Assert.Equal("pre", Row(service, "500000001").MxuServicePosition);
        // The plan's own id isn't put in a shared field: it's a Windows extra.
        var link = PlanLink.For(library, service.Id)!;
        Assert.Equal(("1100001", "80000001", "Sunday Morning"), (link.ServiceTypeId, link.PlanId, link.ServiceTypeName));
        Assert.Null(service.MxuPlanHexId);
        Assert.Equal(10, link.Items.Count);
    }

    [Fact]
    public async Task SongsAreFoundInTheLibraryByCcliNumberThenTitleAndAuthor()
    {
        var library = WithSongs(NewLibrary());
        var summary = PlanImporter.Import(library, await Plan(FakePlanningCenter.WithFixtures()), Options, Now);
        var service = library.Get<Service>(summary.ServiceId)!;
        Assert.Equal("river", Row(service, "500000006").RefId); // CCLI 9000202, though named "(Live)"
        Assert.Equal("faithful-ruth", Row(service, "500000007").RefId); // two "Faithful Still"s: the author picks
        Assert.Equal(2, summary.SongsMatched);
        Assert.Equal(5, library.All<Presentation>().Count); // three of the church's own, two made
    }

    [Fact]
    public void ATitleSharedByDifferentSongsIsntGuessed()
    {
        var matcher = new PlanSongMatcher([Song("a", "Holy", author: "Ann Ash"), Song("b", "Holy", author: "Bo Birch")]);
        Assert.Equal(new PlanSongMatcher.Match(null, true), matcher.Find("Holy", "Cy Cole", null));
        Assert.Equal("b", matcher.Find("HOLY!", "bo birch", null).Song?.Id);
        // A single title match whose CCLI number says it's a different song isn't taken.
        var one = new PlanSongMatcher([Song("c", "Holy", ccli: 111)]);
        Assert.Null(one.Find("Holy", null, 222).Song);
        Assert.Equal("c", one.Find("Holy", null, null).Song?.Id);
        // The same CCLI number twice is the same song: the one whose title fits is taken.
        var copies = new PlanSongMatcher([Song("x", "Holy (Acoustic)", ccli: 5), Song("y", "Holy", ccli: 5)]);
        Assert.Equal("y", copies.Find("Holy", null, 5).Song?.Id);
    }

    [Fact]
    public async Task ASongTheLibraryLacksIsMadeFromItsLyricsWithItsArrangement()
    {
        var library = WithSongs(NewLibrary());
        var summary = PlanImporter.Import(library, await Plan(FakePlanningCenter.WithFixtures()), Options, Now);
        var row = Row(library.Get<Service>(summary.ServiceId)!, "500000005");
        var song = library.Get<Presentation>(row.RefId)!;

        Assert.Equal("Morning Light", song.Name);
        Assert.Equal(PresentationKind.Song, song.PresentationKind);
        Assert.Equal(PlanSongMaker.Folder, song.Folder);
        Assert.Equal("theme-1", song.ThemeId);
        Assert.Equal(PresentationOriginSource.PlanningCenterLyrics, song.Origin!.Source);
        Assert.Equal((9000101, "Sam Rivers and Ada Moss", "Morning Light"), (song.Ccli!.SongNumber, song.Ccli.Author, song.Ccli.SongTitle));
        Assert.Equal("G", song.MusicKey);
        Assert.Equal(["Intro", "Verse 1", "Chorus", "Verse 2", "Bridge"], song.Sections!.Select(s => s.Name));

        // The arrangement's sequence ("Chorus 1" finds the Chorus) is the song's arrangement, and the row plays it.
        var arrangement = song.Arrangements!.Single(a => a.Name == "Default Arrangement");
        Assert.Equal(["Verse 1", "Chorus", "Verse 2", "Chorus", "Bridge", "Chorus"],
            arrangement.SectionIds.Select(id => song.Sections!.Single(s => s.Id == id).Name));
        Assert.Equal(arrangement.Id, song.DefaultArrangementId);
        Assert.Equal(arrangement.Id, row.ArrangementId);
        var slides = SlideSceneBuilder.ArrangedSlides(song, row.ArrangementId);
        Assert.Equal("Here in the morning light\nWe lift our eyes to see", slides[0].Objects.Single().Text);
        Assert.True(summary.SongsMade >= 1);
    }

    [Fact]
    public async Task WithoutLyricsTheChordChartIsUsedAndItsLayoutCommandsAreDropped()
    {
        var library = WithSongs(NewLibrary());
        var summary = PlanImporter.Import(library, await Plan(FakePlanningCenter.WithFixtures()), Options, Now);
        var row = Row(library.Get<Service>(summary.ServiceId)!, "500000010");
        var song = library.Get<Presentation>(row.RefId)!;
        Assert.Equal(PresentationOriginSource.PlanningCenterChart, song.Origin!.Source);
        Assert.True(ChordMath.HasChords(song));
        Assert.DoesNotContain(song.Slides, s => s.Objects.Any(o => o.Text.Contains("COLUMN_BREAK")));
        Assert.Equal(["Verse 1", "Chorus", "Verse 1", "Chorus"],
            song.Arrangements!.Single(a => a.Name == "Band").SectionIds.Select(id => song.Sections!.Single(s => s.Id == id).Name));
        Assert.Equal(2, summary.SongsMade);
    }

    [Fact]
    public async Task MediaAndSongsWithoutWordsAreListedForTheOperator()
    {
        var library = WithSongs(NewLibrary());
        var summary = PlanImporter.Import(library, await Plan(FakePlanningCenter.WithFixtures()), Options, Now);
        var service = library.Get<Service>(summary.ServiceId)!;
        Assert.Equal(["5 Minute Countdown (Dark)"], summary.MediaToAdd);
        Assert.Equal(["Open Skies"], summary.SongsUnlinked);
        var open = Row(service, "500000008");
        // Unlinked, as the Mac keeps a plan's song it has nothing to play for: matched later by its title and CCLI number.
        Assert.Equal((ServiceItemKind.Info, "", "Open Skies", 9000404), (open.ItemKind, open.RefId, open.MxuSongTitle, open.MxuCcliNumber));
        Assert.Contains("media to add: 5 Minute Countdown (Dark)", summary.Text);
        Assert.Contains("to link by hand: Open Skies", summary.Text);
    }

    [Fact]
    public async Task TheSameSongTwiceInAPlanIsMadeOnce()
    {
        var library = NewLibrary();
        var contents = await Plan(FakePlanningCenter.WithFixtures());
        var again = contents.Items[4] with { Id = "500000099", Sequence = 99 };
        PlanImporter.Import(library, contents with { Items = [.. contents.Items, again] }, Options, Now);
        Assert.Single(library.All<Presentation>(), p => p.Name == "Morning Light");
    }

    [Fact]
    public async Task ImportingAgainUpdatesTheSameServiceInPlace()
    {
        var library = WithSongs(NewLibrary());
        var fake = FakePlanningCenter.WithFixtures();
        var first = PlanImporter.Import(library, await Plan(fake), Options, Now);
        var before = library.Get<Service>(first.ServiceId)!;
        var songs = library.All<Presentation>().Count;

        fake.UseUpdatedPlan();
        var second = PlanImporter.Import(library, await Plan(fake), Options, Now.AddDays(7));
        Assert.Equal(first.ServiceId, second.ServiceId);
        Assert.Single(library.All<Service>());
        Assert.Equal(songs, library.All<Presentation>().Count); // nothing made twice

        var after = library.Get<Service>(second.ServiceId)!;
        Assert.Equal(["Pre-Service", "Countdown", "Welcome & Announcements", "Worship", "River of Mercy (Live)", "Morning Light",
            "Faithful Still", "Open Skies", "Message: Grace That Holds Us", "Closing Prayer"], after.Items.Select(i => i.Name));
        Assert.Equal(300, Row(after, "500000003").Duration);
        // Rows keep their ids, so service versions and timelines still find them.
        Assert.Equal(Row(before, "500000005").Id, Row(after, "500000005").Id);
        Assert.True(second.Updated);
        Assert.Equal((1, 1), (second.Added, second.Removed));
        Assert.Equal(0, second.KeptYourChanges);
        Assert.Contains("1 added, 1 removed", second.Text);

        // Unchanged a third time: nothing to do.
        var third = PlanImporter.Import(library, await Plan(fake), Options, Now.AddDays(8));
        Assert.Equal((0, 0, 0), (third.Added, third.Removed, third.Changed));
        Assert.StartsWith("Already up to date", third.Text);
    }

    [Fact]
    public async Task ImportingAgainKeepsWhatTheOperatorChanged()
    {
        var library = WithSongs(NewLibrary());
        library.Save(Song("own-morning", "Morning Light (Our Version)"));
        var fake = FakePlanningCenter.WithFixtures();
        var serviceId = PlanImporter.Import(library, await Plan(fake), Options, Now).ServiceId;

        // The operator: renames the welcome, plays their own version of Morning Light, hides the countdown,
        // adds a baptism after the message, deletes Open Skies, renames Steady Ground, and renames the service.
        var edited = library.Get<Service>(serviceId)!;
        Row(edited, "500000003").Name = "Welcome";
        Row(edited, "500000005").RefId = "own-morning";
        Row(edited, "500000005").ArrangementId = null;
        Row(edited, "500000002").HiddenInPresenter = true;
        edited.Items.Insert(edited.Items.IndexOf(Row(edited, "500000009")) + 1,
            new ServiceItem { Id = "baptism", ItemKind = ServiceItemKind.Header, Name = "Baptism", RefId = "" });
        edited.Items.Remove(Row(edited, "500000008"));
        Row(edited, "500000010").Name = "Steady Ground (closing)";
        edited.Name = "Communion Sunday";
        library.Save(edited);
        var songs = library.All<Presentation>().Count;

        fake.UseUpdatedPlan();
        var summary = PlanImporter.Import(library, await Plan(fake), Options, Now.AddDays(7));
        var after = library.Get<Service>(serviceId)!;

        Assert.Equal("Communion Sunday", after.Name);
        Assert.Equal("Welcome", Row(after, "500000003").Name);
        Assert.Equal(300, Row(after, "500000003").Duration); // a field they didn't touch still updates
        Assert.Equal("own-morning", Row(after, "500000005").RefId);
        Assert.True(Row(after, "500000002").HiddenInPresenter);
        Assert.DoesNotContain(after.Items, i => i.MxuPcoItemId == "500000008"); // deleted stays deleted
        Assert.Equal("Steady Ground (closing)", Row(after, "500000010").Name); // dropped by the plan, but theirs now
        var names = after.Items.Select(i => i.Name).ToList();
        Assert.Equal(names.IndexOf("Message: Grace That Holds Us") + 1, names.IndexOf("Baptism"));
        Assert.True(summary.KeptYourChanges >= 3);
        Assert.Equal(songs, library.All<Presentation>().Count); // their song is played; nothing new is made
    }

    [Fact]
    public async Task ImportingAgainKeepsTheOperatorsOrderAndSlotsNewItemsIn()
    {
        var library = WithSongs(NewLibrary());
        var fake = FakePlanningCenter.WithFixtures();
        var serviceId = PlanImporter.Import(library, await Plan(fake), Options, Now).ServiceId;
        var edited = library.Get<Service>(serviceId)!;
        var message = Row(edited, "500000009");
        edited.Items.Remove(message);
        edited.Items.Insert(0, message);
        library.Save(edited);

        fake.UseUpdatedPlan();
        PlanImporter.Import(library, await Plan(fake), Options, Now.AddDays(7));
        var after = library.Get<Service>(serviceId)!;
        Assert.Equal(["Message: Grace That Holds Us", "Closing Prayer", "Pre-Service", "Countdown", "Welcome & Announcements", "Worship",
            "Morning Light", "River of Mercy (Live)", "Faithful Still", "Open Skies"], after.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task ARowKeepsItsSongWhenTheLibraryGetsASecondCopy()
    {
        var library = WithSongs(NewLibrary());
        var fake = FakePlanningCenter.WithFixtures();
        var serviceId = PlanImporter.Import(library, await Plan(fake), Options, Now).ServiceId;
        var made = Row(library.Get<Service>(serviceId)!, "500000005").RefId;
        // An exact copy (same CCLI number and author) whose id sorts first: a fresh match would pick it.
        library.Save(Song("-copy", "Morning Light", ccli: 9000101, author: "Sam Rivers and Ada Moss"));

        PlanImporter.Import(library, await Plan(fake), Options, Now.AddDays(1));
        Assert.Equal(made, Row(library.Get<Service>(serviceId)!, "500000005").RefId);
    }

    [Fact]
    public async Task ADeletedServiceIsImportedAfreshNotUpdated()
    {
        var library = WithSongs(NewLibrary());
        var fake = FakePlanningCenter.WithFixtures();
        var first = PlanImporter.Import(library, await Plan(fake), Options, Now).ServiceId;
        library.Delete<Service>(first);
        var second = PlanImporter.Import(library, await Plan(fake), Options, Now);
        Assert.NotEqual(first, second.ServiceId);
        Assert.False(second.Updated);
        Assert.Equal(second.ServiceId, PlanLink.ImportedPlans(library)["80000001"]);
    }

    [Fact]
    public void SequencesReadLikeTypedOrders()
    {
        var song = SongEditing.Build(null, "Verse 1\na\n\nChorus 1\nb\n\nBridge\nc", 2, new SongEditing.Details("S"), "t");
        string Names(params string[] sequence) => string.Join(", ",
            PlanSongMaker.SectionIds(sequence, song.Sections!).Select(id => song.Sections!.Single(s => s.Id == id).Name));
        Assert.Equal("Verse 1, Chorus 1, Bridge, Chorus 1", Names("V1", "Chorus", "B", "C"));
        Assert.Equal("Chorus 1, Chorus 1", Names("Chorus x2"));
        Assert.Equal("Verse 1", Names("Verse 1", "Instrumental", ""));
    }
}
