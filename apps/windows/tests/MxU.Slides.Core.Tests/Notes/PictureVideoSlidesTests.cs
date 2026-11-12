using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Notes;

namespace MxU.Slides.Core.Tests.Notes;

/// <summary>Port of the Mac's PictureVideoSlidesTests.swift.</summary>
public class PictureVideoSlidesTests
{
    private static NoteBlock Paragraph(string text, string? href = null) => N.Para(new NoteRun { Text = text, Href = href });

    private static NoteBlock PictureBlock() => new() { Type = NoteBlockType.Image, Url = "/rails/active_storage/blobs/x/lake.jpg", Width = 640 };

    private static NoteBlock VideoFile() => new() { Type = NoteBlockType.Attachment, AttachmentKind = "video", HexId = "abc", Filename = "recap.mp4" };

    [Fact]
    public void AMarkedPictureIsAPassageHoldingItsKey()
    {
        var block = PictureBlock();
        block.MarkRun = new NoteRun { Text = "", SlideGroup = "g1", SlideType = "picture" };
        var declined = VideoFile();
        declined.MarkRun = new NoteRun { Text = "", SlideGroup = "g2", SlideType = "video", SlideState = NoteSlideMarks.Declined };
        var groups = NoteSlideMarks.Groups(N.Body(block, declined));
        Assert.Equal(["g1"], groups.Select(g => g.Id));
        Assert.Equal([PictureBlock().Url!], groups[0].Media);
        Assert.Equal("", groups[0].Text);
        Assert.Equal("picture", groups[0].Type);
    }

    [Fact]
    public void RunRulesApplyToAPictureBlocksMark()
    {
        var marked = NoteSlideMarks.Marking(PictureBlock(), "g1", "picture", NoteSlideMarks.Suggested);
        Assert.True(marked.SlideGroup == "g1" && marked.SlideState == NoteSlideMarks.Suggested);
        var accepted = NoteSlideMarks.AcceptingSuggestions(N.Body(marked));
        Assert.True(accepted.Blocks[0].SlideState is null && accepted.Blocks[0].SlideGroup == "g1");
        var stamped = NoteSlideMarks.Stamping("s1", "g1", accepted);
        Assert.Equal("s1", stamped.Blocks[0].SlideId);
        Assert.Equal(PictureBlock(), NoteSlideMarks.Unmarking("g1", stamped).Blocks[0]);
        Assert.Null(Paragraph("words").MarkRun);
    }

    [Fact]
    public void LinksOnAPassagesWordsAreKept()
    {
        var line = N.Para(new NoteRun { Text = "Video: ", SlideGroup = "g1" }, new NoteRun { Text = "Testimony", Href = "https://vimeo.com/1", SlideGroup = "g1" });
        Assert.Equal(["https://vimeo.com/1"], NoteSlideMarks.Groups(N.Body(line))[0].Links);
    }

    [Fact]
    public void PicturesAndVideosAreSuggestedAsTheirOwnSlides()
    {
        var note = N.Body(Paragraph("Big idea: What lasts."), PictureBlock(), Paragraph("Video: Sarah's story (2:40)"),
            Paragraph("Video: Testimony", "https://vimeo.com/1"), Paragraph("Picture: the lake at dawn"), VideoFile());
        var groups = NoteSlideMarks.Groups(NoteSlideMarks.Suggesting(note));
        Assert.Equal(["points", "picture", "video", "video"], groups.Select(g => g.Type));
        Assert.Equal([PictureBlock().Url!], groups[1].Media);
        Assert.Equal("Video: Testimony", groups[2].Text);
        Assert.Equal(["hex:abc"], groups[3].Media);
    }

    [Fact]
    public void BoldLinesOnlyKeepsPicturesAndVideos()
    {
        static NoteBlock Bold(string text) => N.Para(new NoteRun { Text = text, Bold = true });
        var note = N.Body(Bold("One bold line"), Bold("Another bold line"), Paragraph("Plain words here"), PictureBlock(), VideoFile());
        var types = NoteSlideMarks.Groups(NoteSlideMarks.Suggesting(note, NoteSlideMarks.SuggestMode.BoldLines)).Select(g => g.Type);
        Assert.Equal(["picture", "video"], types.Where(t => t is "picture" or "video"));
    }

    [Fact]
    public void APictureRowTakesTheTeamsScale()
    {
        var group = new MarkedGroup("g", "picture", NoteSlideMarks.Shape.Paragraph, "", [], ["k"], []);
        var row = new DeterministicSlidePlanner().Plan([group], null, N.NoOverrides).Rows[0];
        Assert.True(row.Kind == SlideKind.Picture && row.Media.SequenceEqual(["k"]) && row.Scale == MediaScaleMode.ScaleAndBlur && row.Warning is null);
        Assert.Equal(MediaScaleMode.Fit, new DeterministicSlidePlanner(new SlideOptions { PictureScale = MediaScaleMode.Fit }).Plan([group], null, N.NoOverrides).Rows[0].Scale);
    }

    [Fact]
    public void AVideoWithOnlyALinkIsAPlaceholderHoldingIt()
    {
        var planner = new DeterministicSlidePlanner();
        var inWords = planner.Plan([new MarkedGroup("g", "Video: Testimony – https://vimeo.com/1")], null, N.NoOverrides).Rows[0];
        Assert.True(inWords.Kind == SlideKind.Video && inWords.Notes == "https://vimeo.com/1" && inWords.Warning is not null);
        var onWords = planner.Plan([new MarkedGroup("g", null, NoteSlideMarks.Shape.Paragraph, "Video: Testimony", [], [], ["https://vimeo.com/2"])], null, N.NoOverrides).Rows[0];
        Assert.Equal("https://vimeo.com/2", onWords.Notes);
        var built = NoteSlideBuilder.Build(new SlidePlan([inWords]));
        Assert.True(built.Slides.Count == 1 && built.Slides[0].Background is null && built.Slides[0].Notes == "https://vimeo.com/1");
    }

    [Fact]
    public void APictureSlideIsFullScreenOnStillGraphics()
    {
        var row = new SlideRow { GroupId = "g", Kind = SlideKind.Picture, LinesPerSlide = 1, Media = ["k1", "k2"], Scale = MediaScaleMode.ScaleAndBlur };
        var built = NoteSlideBuilder.Build(new SlidePlan([row]), new Dictionary<string, BlankPlacement>(), new NoteSlideBuilder.BlankStyle(), media: k => k == "k1" ? "m1" : null);
        Assert.Equal(2, built.Slides.Count);
        Assert.True(MxuJson.StructurallyEqual(new CueMedia { MediaId = "m1", Layer = CueMediaLayer.StillGraphics, Classification = MediaClassification.Foreground, ScaleMode = MediaScaleMode.ScaleAndBlur },
            built.Slides[0].Background));
        Assert.Equal(NoteMedia.PendingId("k2"), built.Slides[1].Background?.MediaId);
        Assert.All(built.Slides, s => Assert.Equal("g", built.GroupBySlideId[s.Id]));
        row.Scale = MediaScaleMode.Fill;
        Assert.Null(NoteSlideBuilder.Build(new SlidePlan([row])).Slides[0].Background?.ScaleMode);
    }

    public static TheoryData<SlideOptions.VideoEnds> Ends() => new(Enum.GetValues<SlideOptions.VideoEnds>());

    [Theory]
    [MemberData(nameof(Ends))]
    public void AVideoSlideEndsAsChosen(SlideOptions.VideoEnds end)
    {
        var row = new SlideRow { GroupId = "g", Kind = SlideKind.Video, LinesPerSlide = 1, Media = ["hex:abc"], VideoEnd = end };
        var slide = NoteSlideBuilder.Build(new SlidePlan([row]), new Dictionary<string, BlankPlacement>(), new NoteSlideBuilder.BlankStyle(), media: _ => "v1").Slides[0];
        var cue = slide.Background!;
        Assert.True(cue.Layer == CueMediaLayer.Videos && cue.Classification == MediaClassification.Foreground);
        Assert.Equal(end == SlideOptions.VideoEnds.Next, slide.AutoAdvance is { DelaySeconds: 0, AfterPlayback: true });
        if (end != SlideOptions.VideoEnds.Next) Assert.Null(slide.AutoAdvance);
        Assert.Equal(end == SlideOptions.VideoEnds.Loop, cue.Loops == true);
        Assert.Equal(end switch { SlideOptions.VideoEnds.FadeToBlack => MediaEndFade.Black, SlideOptions.VideoEnds.FadeToClear => MediaEndFade.Clear, _ => (MediaEndFade?)null }, cue.EndFade);
    }

    [Fact]
    public void TheImportFillsPendingSlides()
    {
        var pending = new Slide { Id = "a", Name = "", Objects = [], Background = new CueMedia { MediaId = NoteMedia.PendingId("k1") } };
        var lost = new Slide { Id = "b", Name = "", Objects = [], Background = new CueMedia { MediaId = NoteMedia.PendingId("hex:z") }, AutoAdvance = new AutoAdvance { DelaySeconds = 0, AfterPlayback = true } };
        var plain = new Slide { Id = "c", Name = "", Objects = [], Background = new CueMedia { MediaId = "m9" } };
        var filled = NoteMedia.Filling([pending, lost, plain], [new NoteMediaImport { Key = "k1", MediaId = "m1" }])!;
        Assert.Equal("m1", filled[0].Background?.MediaId);
        Assert.True(filled[1].Background is null && filled[1].AutoAdvance is null && filled[1].Notes!.StartsWith("Needs media", StringComparison.Ordinal));
        Assert.True(MxuJson.StructurallyEqual(plain, filled[2]));
        Assert.Null(NoteMedia.Filling([plain], []));
        Assert.Null(NoteMedia.KeyOfPendingId("m9"));
        Assert.Equal(NoteMedia.PendingId("k1"), pending.Background.MediaId); // the caller's slides are untouched
    }

    [Fact]
    public void PictureAndVideoOptionsDecodeWithDefaults()
    {
        var old = SlideDesignMap.Decode("""{"options":{}}""").Options;
        Assert.True(old.PictureScale == MediaScaleMode.ScaleAndBlur && old.VideoEnd == SlideOptions.VideoEnds.Next);
        var set = SlideDesignMap.Decode("""{"options":{"pictureScale":"fit","videoEnd":"fadeToClear"}}""");
        Assert.True(set.Options.PictureScale == MediaScaleMode.Fit && set.Options.VideoEnd == SlideOptions.VideoEnds.FadeToClear);
        Assert.Equal(set.Options, SlideDesignMap.Decode(set.Encoded()).Options);
    }

    [Fact]
    public void UpdatingAPictureKeepsItsSlideAndTakesTheScale()
    {
        var group = new MarkedGroup("g", "picture", NoteSlideMarks.Shape.Paragraph, "", [], ["k"], []);
        var row = new DeterministicSlidePlanner(new SlideOptions { PictureScale = MediaScaleMode.Fit }).Plan([group], null, N.NoOverrides).Rows[0];
        var made = new Slide { Id = "s", Name = "", Objects = [], Background = new CueMedia { MediaId = "m", ScaleMode = MediaScaleMode.ScaleAndBlur } };
        var result = NoteSlideUpdate.Updating([made], row);
        Assert.True(result.RemovedIds.Count == 0 && result.Appended.Count == 0);
        Assert.Equal(MediaScaleMode.Fit, result.Updated[0].Background?.ScaleMode);
        Assert.False(NoteSlideUpdate.IsBehind(group, [made], null));
    }

    [Fact]
    public void ADropFillsAPlaceholderFullScreen()
    {
        var placeholder = new Slide { Id = "s", Name = "", Objects = [], Notes = "https://vimeo.com/1" };
        Assert.True(placeholder.WaitsForMedia());
        var video = new MediaItem
        {
            Id = "v", Name = "Clip", MediaKind = MediaKind.Video, Classification = MediaClassification.Background, FileHash = "h", FileName = "clip.mov",
            FileStatus = MediaFileStatus.Ready, StatusDetail = "", Tags = [], Favorite = false, Collections = [], Loops = true,
        };
        placeholder.FillPlaceholder(video, new SlideOptions { VideoEnd = SlideOptions.VideoEnds.FadeToBlack });
        Assert.True(placeholder.Background?.Layer == CueMediaLayer.Videos && placeholder.Background.EndFade == MediaEndFade.Black);
        Assert.True(placeholder.Notes == "https://vimeo.com/1" && !placeholder.WaitsForMedia());

        var lost = new Slide { Id = "l", Name = "", Objects = [], Notes = SlideMediaFacts.NeedsMediaNote + " the note's video did not come in" };
        video.MediaKind = MediaKind.Image;
        lost.FillPlaceholder(video, new SlideOptions());
        Assert.True(lost.Background?.ScaleMode == MediaScaleMode.ScaleAndBlur && lost.Background.Layer == CueMediaLayer.StillGraphics && lost.Notes is null);

        var blank = new Slide { Id = "b", Name = "", Objects = [] };
        Assert.False(blank.WaitsForMedia());
        blank.Notes = "Pastor: hold here";
        Assert.False(blank.WaitsForMedia());
    }
}

/// <summary>Port of the Mac's NoteMediaTests.swift.</summary>
public class NoteMediaTests
{
    private static NoteBlock PictureBlock() => new() { Type = NoteBlockType.Image, Url = "file:///Users/me/Notes/photo.png", Width = 400 };
    private static NoteBlock Video() => new() { Type = NoteBlockType.Attachment, AttachmentKind = "video", HexId = "v1", Filename = "baptism.mp4" };
    private static NoteBlock Outline() => new() { Type = NoteBlockType.Attachment, AttachmentKind = "file", HexId = "f1", Filename = "outline.pdf" };
    private static NoteBlock ClipAsFile() => new() { Type = NoteBlockType.Attachment, AttachmentKind = "file", HexId = "f2", Filename = "clip.mov" };

    private static NoteBody Body() => new()
    {
        Blocks =
        [
            N.Para("Grace"), PictureBlock(), new NoteBlock { Type = NoteBlockType.Quote, Blocks = [Video()] },
            new NoteBlock { Type = NoteBlockType.List, Items = [new NoteListItem { Runs = [], Blocks = [ClipAsFile()] }] },
            Outline(), new NoteBlock { Type = NoteBlockType.Image, Url = "" },
        ],
        PlainText = "Grace",
    };

    private static NoteMediaImport Import(string key, string id) => new() { Key = key, MediaId = id };

    private static List<(string, string)> Pairs(IEnumerable<NoteMediaImport> imports) => imports.Select(i => (i.Key, i.MediaId)).ToList();

    [Fact]
    public void MediaBlocksArePicturesAndVideosNestedToo()
    {
        Assert.Equal([PictureBlock(), Video(), ClipAsFile()], NoteMedia.Blocks(Body())); // a pdf and an empty picture are not media
        Assert.Equal("file:///Users/me/Notes/photo.png", NoteMedia.Key(PictureBlock()));
        Assert.Equal("hex:v1", NoteMedia.Key(Video()));
        Assert.Null(NoteMedia.Key(Body().Blocks[0]));
        Assert.Equal("photo.png", NoteMedia.FileName(PictureBlock()));
        Assert.Equal("baptism.mp4", NoteMedia.FileName(Video()));
    }

    [Fact]
    public void MailChromeAndCidPicturesAreNoMedia()
    {
        var icon = new NoteBlock { Type = NoteBlockType.Image, Url = "https://ssl.gstatic.com/docs/doclist/images/icon_10_generic_list.png" };
        var screenshot = new NoteBlock { Type = NoteBlockType.Image, Url = "cid:ii_mvikieid0", Alt = "Screenshot.png" };
        Assert.Equal([PictureBlock()], NoteMedia.Blocks(N.Body(icon, screenshot, PictureBlock())));
        Assert.True(NoteMedia.IsMailChrome("https://drive-thirdparty.googleusercontent.com/16/type/video/mp4"));
        Assert.True(NoteMedia.IsMailChrome("https://mail.google.com/mail/u/0/images/cleardot.gif"));
        Assert.False(NoteMedia.IsMailChrome("https://lh3.googleusercontent.com/photo.jpg")); // a real Google Photos picture
        Assert.False(NoteMedia.IsMailChrome(PictureBlock().Url!));
        Assert.Null(NoteMedia.Resolve("cid:ii_mvikieid0")); // only Mail can read it
    }

    [Fact]
    public void ASecondRunImportsOnlyWhatTheLibraryLacks()
    {
        var pending = NoteMedia.Pending(Body(), [Import("hex:v1", "m-video"), Import("hex:f2", "m-gone")], id => id == "m-video");
        Assert.Equal([PictureBlock(), ClipAsFile()], pending); // the video is in the library; the clip's item left it; the picture was never brought in
        Assert.Empty(NoteMedia.Pending(Body(), [Import(PictureBlock().Url!, "m1"), Import("hex:v1", "m2"), Import("hex:f2", "m3")], _ => true));
    }

    [Fact]
    public void TheMemoryFollowsTheNoteAndTakesNewLandings()
    {
        var remembered = NoteMedia.Remembering([Import("hex:f2", "m-gone"), Import("hex:removed", "m-old")],
            [Import(PictureBlock().Url!, "m-pic"), Import("hex:f2", "m-clip")], Body());
        Assert.Equal([(PictureBlock().Url!, "m-pic"), ("hex:f2", "m-clip")], Pairs(remembered));
    }

    [Fact]
    public void PicturesResolveOnlyFromWhatThisComputerCanRead()
    {
        Assert.Equal("https://cdn.example/a.png", NoteMedia.Resolve("https://cdn.example/a.png")?.AbsoluteUri);
        Assert.True(NoteMedia.Resolve("file:///Users/me/a.png")?.IsFile); // a note's own copy
        Assert.Null(NoteMedia.Resolve("cid:ii_mvikieid0"));
    }

    [Fact]
    public void APictureNeverRunsPastTheColumn()
    {
        Assert.Equal((240.0, 135.0), NoteMedia.Fitted((480, 270), 240)); // narrower column: scaled, same shape
        Assert.Equal((200.0, 100.0), NoteMedia.Fitted((200, 100), 240)); // fits already: unchanged
        Assert.Equal((480.0, 270.0), NoteMedia.Fitted((480, 270), 0)); // no layout width yet
    }

    [Fact]
    public void TheFooterNamesWhatARunBringsIn()
    {
        var clip = new NoteBlock { Type = NoteBlockType.Attachment, Url = "file:///Users/me/Notes/clip.mov", AttachmentKind = "video", Filename = "clip.mov" };
        var local = new NoteBody { Blocks = [.. Body().Blocks, clip], PlainText = "" };
        var all = NoteMedia.Importable(local, [], _ => false);
        Assert.Equal([PictureBlock(), clip], all); // an attachment with no file here is never fetched
        Assert.Equal("Also adds 1 picture and 1 video to Media › Needs Sorted.", NoteMedia.FooterLine(all));
        Assert.Null(NoteMedia.FooterLine([]));
    }

    [Fact]
    public void KeptFilesAreFoundAndSavedOnce()
    {
        var video = new NoteBlock { Type = NoteBlockType.Attachment, Url = "file:///tmp/Snoop.mp4", AttachmentKind = "video", Filename = "Snoop.mp4", SlideGroup = "g1" };
        var picture = new NoteBlock { Type = NoteBlockType.Image, Url = "file:///tmp/a.png" };
        var web = new NoteBlock { Type = NoteBlockType.Image, Url = "https://cdn.example/b.png" };
        var body = N.Body(video, new NoteBlock { Type = NoteBlockType.Quote, Blocks = [picture] }, web);
        Assert.Equal("file:///tmp/Snoop.mp4", NoteMedia.Key(video));
        Assert.Equal(3, NoteMedia.Importable(body, [], _ => false).Count);
        Assert.Equal(["file:///tmp/Snoop.mp4", "file:///tmp/a.png"], NoteMedia.LocalFiles(body).Select(b => b.Url));

        var folder = Path.Combine(Path.GetTempPath(), "note-saver-" + Guid.NewGuid());
        try
        {
            var save = NoteMedia.Saver(folder);
            var first = save("png"u8.ToArray(), "a.png");
            Assert.NotNull(first);
            Assert.Equal(first, save("png"u8.ToArray(), "a.png"));
            Assert.NotEqual(first, save("other"u8.ToArray(), "a.png"));
            Assert.Equal("a.png", Path.GetFileName(new Uri(first).LocalPath));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void AVideosPosterIsASecondIn()
    {
        Assert.Equal(1, NoteMedia.PosterTime(42));
        Assert.Equal(0.1, NoteMedia.PosterTime(1.5));
    }
}
