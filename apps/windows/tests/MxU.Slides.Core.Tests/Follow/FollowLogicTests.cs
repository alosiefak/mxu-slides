using System.Globalization;
using MxU.Slides.Core.Follow;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Tests.Library;

namespace MxU.Slides.Core.Tests.Follow;

// Ports of the Mac's FollowLogicTests.swift (MxU PR #12). The Mac's banners say "Mac"; these say
// "computer" (a Windows PC and a Mac back each other up). Times are in the culture's own pattern.

public sealed class FollowLogicTests
{
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly FollowLogic.State Lost = new FollowLogic.State.Lost(false);
    private static readonly FollowLogic.State Froze = new FollowLogic.State.Lost(true);

    [Fact]
    public void ABackupHoldsGearTheBoothAlreadyDroveAndRunsThePicture()
    {
        Assert.True(FollowLogic.Holds(SlideActionKind.MidiOut) && FollowLogic.Holds(SlideActionKind.CaptureStart) && FollowLogic.Holds(SlideActionKind.CaptureStop));
        Assert.False(FollowLogic.Holds(SlideActionKind.FireLiveInput) || FollowLogic.Holds(SlideActionKind.EnableAudioInput) || FollowLogic.Holds(SlideActionKind.SwitchOutputPreset));
    }

    [Fact]
    public void TheBannerSaysWhereThePairStands()
    {
        var fire = DateTimeOffset.FromUnixTimeSeconds(1_793_361_730);
        var live = FollowLogic.Banner("Booth Mac", FollowLogic.State.IsLive, fire, culture: Us, zone: NewYork);
        Assert.Matches(@"^Last fire 8:02:10\s?AM\.", live.Detail.Replace((char)0x202F, ' '));
        Assert.Equal("Backing up Booth Mac.", FollowLogic.Banner("Booth Mac", FollowLogic.State.IsLive, null).Title);
        Assert.Equal("In step. This computer's triggers wait until it takes over.", FollowLogic.Banner("Booth Mac", FollowLogic.State.IsLive, null).Detail);
        Assert.Equal("Booth Mac froze.", FollowLogic.Banner("Booth Mac", Froze, null).Title);
        Assert.Equal("Booth Mac stopped answering.", FollowLogic.Banner("Booth Mac", Lost, null).Title);
        Assert.Equal("Can't reach Booth Mac.", FollowLogic.Banner("Booth Mac", FollowLogic.State.HasGivenUp, null).Title);
        Assert.Equal("Booth Mac quit.", FollowLogic.Banner("Booth Mac", Lost, null, boothQuit: true).Title);
        Assert.Equal("Booth Mac quit.", FollowLogic.Banner("Booth Mac", FollowLogic.State.HasGivenUp, null, boothQuit: true).Title); // still waiting after the retries ran out
        var ready = FollowLogic.Banner("Booth Mac", FollowLogic.State.IsLive, null, standby: true);
        Assert.Equal("Ready to take over from Booth Mac.", ready.Title);
        Assert.Equal("Nothing shows here until this computer takes over.", ready.Detail);
        Assert.Equal("Take Over puts up what Booth Mac last showed.", FollowLogic.Banner("Booth Mac", Lost, null, standby: true).Detail);
        Assert.StartsWith("Nothing shows here", FollowLogic.Banner("Booth Mac", Lost, null, boothQuit: true, standby: true).Detail);
        Assert.Equal("Connecting to Booth Mac…", FollowLogic.Banner("Booth Mac", FollowLogic.State.IsConnecting, null).Title);
    }

    [Fact]
    public void TheHigherTakeoverNumberStands()
    {
        var start = new FollowRole(0, "booth");
        var tookOver = start.Handed("backup");
        Assert.Equal(new FollowRole(1, "backup"), tookOver);
        Assert.Equal(tookOver, start.Newer(tookOver)); // a booth that unfreezes hears it and steps down
        Assert.Null(tookOver.Newer(start)); // an older word never wins
        Assert.Equal(new FollowRole(2, "booth"), tookOver.Handed("booth"));
    }

    [Fact]
    public void ABackupTakesOverOnItsOwnOnlyAfterLosingALiveBooth()
    {
        Assert.True(FollowLogic.TakesOverOnItsOwn(automatic: true, backingUp: true, wasLive: true, Froze));
        Assert.True(FollowLogic.TakesOverOnItsOwn(automatic: true, backingUp: true, wasLive: true, Lost));
        Assert.False(FollowLogic.TakesOverOnItsOwn(automatic: false, backingUp: true, wasLive: true, Lost)); // Wait for Take Over
        Assert.False(FollowLogic.TakesOverOnItsOwn(automatic: true, backingUp: true, wasLive: false, Lost)); // never reached it
        Assert.False(FollowLogic.TakesOverOnItsOwn(automatic: true, backingUp: false, wasLive: true, Lost)); // already in charge
        Assert.False(FollowLogic.TakesOverOnItsOwn(automatic: true, backingUp: true, wasLive: true, Lost, boothQuit: true)); // a Quit isn't a failure
        Assert.False(FollowLogic.TakesOverOnItsOwn(automatic: true, backingUp: true, wasLive: true, FollowLogic.State.IsConnecting));
    }

    [Fact]
    public void TheInChargeAndSteppedDownBannersNameTheOtherComputer()
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(1_793_375_520);
        string Plain(string s) => s.Replace((char)0x202F, ' ');
        Assert.Equal("This computer took over from Booth Mac at 11:52 AM.", Plain(FollowLogic.InChargeBanner("Booth Mac", at, culture: Us, zone: NewYork).Title));
        Assert.Equal("Booth Mac stopped answering, so this computer took over at 11:52 AM.",
            Plain(FollowLogic.InChargeBanner("Booth Mac", at, onItsOwn: true, culture: Us, zone: NewYork).Title));
        Assert.Equal("Booth Mac was streaming. Start Stream Here to send it from this computer.", FollowLogic.InChargeBanner("Booth Mac", at, boothStreamed: true).Detail);
        Assert.Equal("Backup Mac took over.", FollowLogic.SteppedDownBanner("Backup Mac").Title);
        Assert.Equal("", FollowLogic.CopiedLine(0));
        Assert.Equal(" 1 item copied here over the network.", FollowLogic.CopiedLine(1));
        Assert.Equal(" 3 items copied here over the network.", FollowLogic.CopiedLine(3));
    }

    [Fact]
    public void AFreshBackupTakesTheBoothsNumberSoItsTakeoverStands()
    {
        var fresh = FollowRole.Start("booth");
        var booth = new FollowRole(4, "booth");
        var joined = fresh.Newer(booth) ?? fresh;
        Assert.Equal(4, joined.Term);
        Assert.Null(joined.Handed("backup").Newer(booth)); // the booth's old number never wins back a takeover
        Assert.Equal("backup", booth.Newer(joined.Handed("backup"))?.InCharge);
    }

    [Fact]
    public void ADroppedLinkGivesWayToTheSameBooth()
    {
        var took = DateTimeOffset.FromUnixTimeSeconds(1000);
        var soon = took.AddSeconds(12);
        Assert.True(FollowLogic.HandsBackOnReturn(onItsOwn: true, frozen: false, took, soon, "a", "a"));
        Assert.False(FollowLogic.HandsBackOnReturn(onItsOwn: true, frozen: false, took, soon, "a", "b")); // it relaunched: a crash
        Assert.False(FollowLogic.HandsBackOnReturn(onItsOwn: true, frozen: true, took, soon, "a", "a")); // it froze
        Assert.False(FollowLogic.HandsBackOnReturn(onItsOwn: false, frozen: false, took, soon, "a", "a")); // Take Over by hand
        Assert.False(FollowLogic.HandsBackOnReturn(onItsOwn: true, frozen: false, took, took.AddSeconds(61), "a", "a")); // gone over a minute
        Assert.False(FollowLogic.HandsBackOnReturn(onItsOwn: true, frozen: false, took, soon, null, "a")); // never heard its launch
        Assert.Equal("Booth Mac came back after 12 seconds, so it answers again.", FollowLogic.CameBackLine("Booth Mac", TimeSpan.FromSeconds(12.4)));
    }

    [Fact]
    public void TheBoothNamesItsBackup()
    {
        Assert.StartsWith("Backup Mac is backing up this computer", FollowLogic.BoothBackupLine("Backup Mac", connected: true));
        Assert.StartsWith("Backup Mac isn't connected.", FollowLogic.BoothBackupLine("Backup Mac", connected: false));
        Assert.Equal("Backup Mac stopped following.", FollowLogic.BackupDroppedBanner("Backup Mac").Title);
        Assert.StartsWith("Backup Mac took over", FollowLogic.BoothBackupLine("Backup Mac", connected: true, inCharge: true));
    }

    [Fact]
    public void SettingsSaysHowThePairStandsOnTheBooth()
    {
        var none = new HashSet<string>();
        Assert.Equal("No backup computer", FollowLogic.BoothSettingsLine([], none, null).Title);
        Assert.Equal("Backup Mac is following this computer", FollowLogic.BoothSettingsLine(["Backup Mac"], new HashSet<string> { "Backup Mac" }, null).Title);
        Assert.Equal("Backup Mac isn't connected", FollowLogic.BoothSettingsLine(["Backup Mac"], none, null).Title);
        var two = FollowLogic.BoothSettingsLine(["A", "B"], new HashSet<string> { "B" }, null);
        Assert.Equal(("2 backups follow this computer", "Connected now: B."), two);
        Assert.Equal("Backup Mac took over", FollowLogic.BoothSettingsLine(["Backup Mac"], none, "Backup Mac").Title);
    }

    [Fact]
    public void SettingsSaysHowThePairStandsOnTheBackup()
    {
        var live = FollowLogic.BackupSettingsLine("Booth", FollowLogic.State.IsLive, inCharge: false, standby: false, automatic: true, copied: 2);
        Assert.Equal(("Backing up Booth", "Shows the same thing live. Takes over after 3 seconds. 2 items copied."), live);
        var ready = FollowLogic.BackupSettingsLine("Booth", FollowLogic.State.IsLive, inCharge: false, standby: true, automatic: false, copied: 0);
        Assert.Equal(("Ready to take over from Booth", "Stays ready with clear screens. Waits for Take Over."), ready);
        Assert.Equal("Booth froze", FollowLogic.BackupSettingsLine("Booth", Froze, inCharge: false, standby: false, automatic: true, copied: 0).Title);
        Assert.Equal("Took over from Booth", FollowLogic.BackupSettingsLine("Booth", FollowLogic.State.IsLive, inCharge: true, standby: false, automatic: true, copied: 0).Title);
    }
}

/// <summary>Windows' half of FollowCopyTests.swift: a booth's JSON saves land on its backup, and nothing else does.</summary>
public sealed class FollowCopyTests : ScratchFolderTest
{
    private static Presentation Deck(string id, string name = "Prelude") => new()
    {
        Id = id, Name = name, PresentationKind = PresentationKind.Deck, ThemeId = "", Slides = [],
    };

    [Fact]
    public void ABoothsSaveLandsHereAndAChangeThereComesAcross()
    {
        var booth = NewLibrary("booth");
        var backup = NewLibrary("backup");
        booth.Save(Deck("d1"));
        Assert.Null(FollowCopy.HeldHeads(backup, "presentation", "d1")); // nothing here yet
        var offer = Assert.Single(FollowCopy.Offered(booth));
        Assert.Equal(("presentation", "d1"), (offer.Kind, offer.Id));
        Assert.True(FollowCopy.IsJson(offer.Heads));

        Assert.True(FollowCopy.Land(backup, "presentation", "d1", FollowCopy.Save(booth, "presentation", "d1")!));
        Assert.Equal("Prelude", backup.Get<Presentation>("d1")?.Name);
        Assert.Equal(offer.Heads, FollowCopy.HeldHeads(backup, "presentation", "d1")); // in step: nothing to fetch again

        var renamed = Deck("d1", "Prelude (9am)");
        booth.Save(renamed);
        var changed = Assert.Single(FollowCopy.Offered(booth));
        Assert.NotEqual(offer.Heads, changed.Heads);
        FollowCopy.Land(backup, "presentation", "d1", FollowCopy.Save(booth, "presentation", "d1")!);
        Assert.Equal("Prelude (9am)", backup.Get<Presentation>("d1")?.Name);
    }

    [Fact]
    public void ASaveForAnotherIdOrKindNeverLands()
    {
        var booth = NewLibrary("booth");
        var backup = NewLibrary("backup");
        booth.Save(Deck("d1"));
        var bytes = FollowCopy.Save(booth, "presentation", "d1")!;
        Assert.False(FollowCopy.Land(backup, "presentation", "other", bytes));
        Assert.False(FollowCopy.Land(backup, "stationSettings", "d1", bytes)); // this computer's own settings never come across
        Assert.False(FollowCopy.Land(backup, "presentation", "d1", "not json"u8.ToArray()));
        Assert.Empty(backup.All<Presentation>());
        Assert.False(FollowCopy.IsJson(["9f86d081884c7d65"])); // a Mac's Automerge heads
    }

    [Fact]
    public void MediaOffersItsFileAndACopyTheBoothDroppedGoes()
    {
        var booth = NewLibrary("booth");
        var backup = NewLibrary("backup");
        var clip = WriteFile("clip.mp4", "clip");
        var hash = LibraryStore.HashFile(clip);
        booth.Blobs.Store(clip, hash);
        booth.Save(Media("m1", hash));
        var offer = Assert.Single(FollowCopy.Offered(booth));
        Assert.Equal(((string?)hash, (string?)"mp4"), (offer.File, offer.Ext));

        FollowCopy.Land(backup, "media", "m1", FollowCopy.Save(booth, "media", "m1")!);
        Assert.NotNull(backup.Get<MediaItem>("m1"));
        FollowCopy.Remove(backup, "media/m1");
        Assert.Null(backup.Get<MediaItem>("m1"));
        FollowCopy.Remove(backup, "nonsense"); // never throws
    }
}
