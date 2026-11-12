using MxU.Slides.Core.Library;

namespace MxU.Slides.Core.Tests.Library;

/// <summary>Opening files from Explorer: which arguments are files, what each is, the message between copies, and the welcome guide's rules.</summary>
public sealed class OpenRequestsTests : ScratchFolderTest
{
    [Fact]
    public void FilesAreTheArgumentsThatArentSwitches()
    {
        var files = OpenRequests.Files(["--self-test", @"C:\Temp\out", "Song.mxuslides", "\"D:\\Decks\\Easter.pptx\"", "Song.mxuslides", ""], @"C:\Users\me\Desktop");
        Assert.Equal([@"C:\Users\me\Desktop\Song.mxuslides", @"D:\Decks\Easter.pptx"], files); // relative made full, each once, the self-test's folder left out
    }

    [Theory]
    [InlineData("a.mxuslides", OpenRequests.Kind.SlidesFile)]
    [InlineData("A.MXUSLIDES", OpenRequests.Kind.SlidesFile)]
    [InlineData("deck.pptx", OpenRequests.Kind.PowerPoint)]
    [InlineData("song.pro", OpenRequests.Kind.ProPresenter)]
    [InlineData("Sunday.proPlaylist", OpenRequests.Kind.ProPresenter)]
    [InlineData("lyrics.txt", OpenRequests.Kind.Lyrics)]
    [InlineData("song.cho", OpenRequests.Kind.Lyrics)]
    [InlineData("Library 2026-10-25.mxubackup", OpenRequests.Kind.Backup)]
    [InlineData("virus.exe", OpenRequests.Kind.Unsupported)]
    public void EachKindOfFileIsKnownByItsExtension(string name, OpenRequests.Kind kind) => Assert.Equal(kind, OpenRequests.KindOf(name));

    [Fact]
    public void AMessageReadsBackAsItWasSent()
    {
        var files = new[] { @"C:\Songs\Amazing Grace.mxuslides", @"C:\Décks\Pâques.pptx" };
        using var stream = new MemoryStream(OpenRequests.Encode(files));
        Assert.Equal(files, OpenRequests.Decode(stream));
    }

    [Fact]
    public void ABadMessageIsIgnored()
    {
        Assert.Null(OpenRequests.Decode(new MemoryStream([1, 2])));
        Assert.Null(OpenRequests.Decode(new MemoryStream([255, 255, 255, 127]))); // claims 2 GB
        var notJson = new byte[] { 3, 0, 0, 0, (byte)'a', (byte)'b', (byte)'c' };
        Assert.Null(OpenRequests.Decode(new MemoryStream(notJson)));
        // Relative paths (which would resolve against the running copy's folder) are dropped.
        using var stream = new MemoryStream(OpenRequests.Encode(["relative.mxuslides", @"C:\ok.mxuslides"]));
        Assert.Equal([@"C:\ok.mxuslides"], OpenRequests.Decode(stream));
    }

    [Fact]
    public void ThePipeIsNamedForThisCopysDataFolder()
    {
        Assert.Equal("MxU.Slides.Windows.Open.0123456789ABCDEF", OpenRequests.PipeName(@"Local\MxU.Slides.Windows.SingleInstance.0123456789ABCDEF"));
    }

    [Fact]
    public void TooManyFilesAreCutAtTheLimit()
    {
        var many = Enumerable.Range(0, 500).Select(i => $"{i}.mxuslides").ToList();
        Assert.Equal(OpenRequests.MaxFiles, OpenRequests.Files(many, @"C:\").Count);
    }

    // MARK: The welcome guide (OnboardingFlowTests.swift)

    [Fact]
    public void TheGuideWalksItsStepsAndStopsAtTheEnds()
    {
        var flow = new OnboardingFlow(firstRun: true);
        Assert.Equal(OnboardingFlow.Step.Welcome, flow.Current);
        Assert.False(flow.CanGoBack);
        Assert.True(flow.IncludeGettingStarted);
        flow.Back();
        Assert.Equal(OnboardingFlow.Step.Welcome, flow.Current); // back stops at the first step
        foreach (var _ in OnboardingFlow.Steps) flow.Advance();
        Assert.Equal(OnboardingFlow.Step.Help, flow.Current); // advance stops at the last
        Assert.True(flow.IsLastStep);
        Assert.Equal(5, flow.Number);
    }

    [Fact]
    public void TheContentChoicesDecideTheSeed()
    {
        var importing = new OnboardingFlow(firstRun: true) { IncludeGettingStarted = false };
        importing.Choose(OnboardingFlow.ContentChoice.ImportSongs);
        Assert.Equal(OnboardingFlow.Step.Welcome, importing.Current); // it waits while the import runs
        Assert.False(importing.IncludeGettingStarted);

        var scratch = new OnboardingFlow(firstRun: false);
        Assert.False(scratch.IncludeGettingStarted);
        scratch.Choose(OnboardingFlow.ContentChoice.StartFromScratch);
        Assert.Equal(OnboardingFlow.Step.Screens, scratch.Current);
        Assert.True(scratch.IncludeGettingStarted);
    }

    [Fact]
    public void TheGuideShowsOnANewLibraryUntilItsFinishedAndNeverOnOneInUse()
    {
        var fresh = PathFor("fresh");
        Directory.CreateDirectory(fresh);
        Assert.True(OnboardingFlow.Needed(fresh, libraryIsNew: true));
        Assert.True(OnboardingFlow.Needed(fresh, libraryIsNew: false)); // closed half way: it comes back
        OnboardingFlow.MarkDone(fresh);
        Assert.False(OnboardingFlow.Needed(fresh, libraryIsNew: false));

        var inUse = PathFor("in-use");
        Directory.CreateDirectory(inUse);
        Assert.False(OnboardingFlow.Needed(inUse, libraryIsNew: false)); // an existing library, the first time this version runs
        Assert.True(File.Exists(Path.Combine(inUse, OnboardingFlow.MarkerFileName)));
    }
}
