using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Library;

/// <summary>
/// Port of SlidesPresentationFileTests.swift, plus what matters most on Windows: a file laid
/// out exactly as the Mac writes one imports here, and the file written here has the Mac's
/// layout, so the Mac can read it.
/// </summary>
public sealed partial class SlidesPresentationFileTests : ScratchFolderTest
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string StoreBlob(LibraryStore library, string text, string ext) =>
        library.Blobs.Store(WriteFile($"{Guid.NewGuid():N}.{ext}", text));

    private static MediaItem Ocean(string id, string hash, string name = "Ocean")
    {
        var item = Media(id, hash, name, MediaKind.Image);
        item.Folder = "Team Media/Backgrounds";
        return item;
    }

    private static Presentation Deck(string mediaId, string audioId) => new()
    {
        Id = "deck", Name = "Amazing Grace", PresentationKind = PresentationKind.Song, ThemeId = "theme", Folder = "Songs/Hymns",
        Slides =
        [
            new Slide
            {
                Id = "s1", Name = "Verse 1", Background = new CueMedia { MediaId = mediaId },
                Objects = [new SlideObject { Id = "o1", ObjectKind = SlideObjectKind.Text, Name = "Lyrics", Text = "Amazing grace" }],
            },
            new Slide { Id = "s2", Name = "Walk Out", Objects = [], Actions = [new SlideAction { Id = "a1", Kind = SlideActionKind.FireAudio, AudioItemId = audioId }] },
        ],
    };

    private static Theme Hymns() => new()
    {
        Id = "theme", Name = "Hymns", FontFamily = "Helvetica Neue", FontSize = 96, TextColorHex = "#FFFFFF", BackgroundColorHex = "#000000",
    };

    /// <summary>A library with the deck, its theme, a picture and a song, exported; returns the file.</summary>
    private string Exported(IReadOnlyList<string>? fonts = null)
    {
        var source = NewLibrary("source");
        var imageHash = StoreBlob(source, "png bytes", "png");
        var audioHash = StoreBlob(source, "wav bytes", "wav");
        source.Save(Hymns());
        source.Save(Ocean("m1", imageHash));
        source.Save(Audio("a1", audioHash, "Outro"));
        source.Save(Deck("m1", "a1"));
        var bundle = SlidesPresentationFile.Bundle(source, "deck")!;
        var file = PathFor($"{Guid.NewGuid():N}.mxuslides");
        var summary = SlidesPresentationFile.Write(bundle, source.Blobs, fonts ?? [], file, Ct);
        Assert.Empty(summary.MissingFiles);
        return file;
    }

    [Fact]
    public void ADeckRoundTripsIntoAnotherLibrary()
    {
        var file = Exported();
        var target = NewLibrary("target");
        var contents = SlidesPresentationFile.Read(file, PathFor("scratch"), Ct);
        Assert.Equal("Amazing Grace", contents.Manifest.Name);
        Assert.Equal(2, contents.Manifest.SlideCount);
        Assert.False(SlidesPresentationFile.IsInLibrary(contents, target));

        var result = SlidesPresentationFile.ImportContents(contents, target, SlidesPresentationFile.Conflict.KeepBoth, cancellationToken: Ct);
        Assert.Equal(("deck", 1, 2), (result.PresentationId, result.ThemesAdded, result.MediaAdded));
        Assert.Empty(result.Missing);

        var landed = target.Get<Presentation>("deck")!;
        Assert.Equal(["s1", "s2"], landed.Slides.Select(s => s.Id));
        Assert.Equal("m1", landed.Slides[0].Background?.MediaId);
        Assert.Null(landed.Folder); // Windows has no team drives: imports land unsorted, like the Mac's .unplaced
        Assert.Equal("Hymns", target.Get<Theme>("theme")?.Name);
        var item = target.Get<MediaItem>("m1")!;
        Assert.Null(item.Folder);
        Assert.NotNull(target.BlobPath(item));
        var audio = target.Get<AudioItem>("a1")!;
        Assert.NotNull(target.Blobs.Find(audio.FileHash));
    }

    [Fact]
    public void ImportingTheSameDeckAgainReplacesOrKeepsBoth()
    {
        var file = Exported();
        var target = NewLibrary("target");
        var contents = SlidesPresentationFile.Read(file, PathFor("scratch"), Ct);
        SlidesPresentationFile.ImportContents(contents, target, SlidesPresentationFile.Conflict.KeepBoth, cancellationToken: Ct);
        var edited = target.Get<Presentation>("deck")!;
        edited.Name = "Edited here";
        target.Save(edited);
        Assert.True(SlidesPresentationFile.IsInLibrary(contents, target));

        var copy = SlidesPresentationFile.ImportContents(contents, target, SlidesPresentationFile.Conflict.KeepBoth, cancellationToken: Ct);
        Assert.NotEqual("deck", copy.PresentationId);
        Assert.Equal((0, 0), (copy.ThemesAdded, copy.MediaAdded)); // nothing new: the theme and files are already here
        Assert.Equal("Edited here", target.Get<Presentation>("deck")!.Name);
        Assert.Equal("Amazing Grace", target.Get<Presentation>(copy.PresentationId)!.Name);

        SlidesPresentationFile.ImportContents(contents, target, SlidesPresentationFile.Conflict.Replace, cancellationToken: Ct);
        Assert.Equal("Amazing Grace", target.Get<Presentation>("deck")!.Name);
        Assert.Equal(2, target.Count<Presentation>());
    }

    [Fact]
    public void PlanPointsAtFilesTheLibraryHasAndReportsOnesNobodyHas()
    {
        var payload = new SlidesPresentationFile.Payload
        {
            Presentation = Deck("m1", "a1"),
            Themes = [Hymns()],
            Media = [Ocean("m1", "h1"), Ocean("m2", "h2", "Lost")],
            Audio = [],
        };
        var plan = SlidesPresentationFile.MakePlan(payload,
            new SlidesPresentationFile.Existing(new HashSet<string> { "theme" }, new Dictionary<string, string> { ["local"] = "h1" }, new Dictionary<string, string>()),
            new HashSet<string> { "h1" }, presentationId: "copy");
        Assert.Equal("copy", plan.Presentation.Id);
        Assert.Equal("local", plan.Presentation.Slides[0].Background?.MediaId);
        Assert.Empty(plan.Media);
        Assert.Empty(plan.Themes);
        Assert.Equal(["Lost"], plan.Missing);
        Assert.Null(plan.Presentation.Folder);
        Assert.Equal("m1", payload.Presentation.Slides[0].Background?.MediaId); // the plan changes copies, never the file's documents
    }

    [Fact]
    public void ReadRefusesOtherFilesAndNewerFormats()
    {
        var notZip = WriteFile("hello.mxuslides", "hello");
        var refused = Assert.Throws<SlidesPresentationFile.FileException>(() => SlidesPresentationFile.Read(notZip, PathFor("a"), Ct));
        Assert.Equal(SlidesPresentationFile.Problem.NotAPresentation, refused.Problem);
        Assert.Equal("“hello.mxuslides” isn't an MxU Slides presentation.", refused.Message);

        var newer = PathFor("newer.mxuslides");
        File.WriteAllBytes(newer, MacArchive.Ditto([
            new("manifest.json", Encoding.UTF8.GetBytes("""{"formatVersion":99,"createdAt":"2026-10-07T00:00:00Z","name":"x","slideCount":0}""")),
        ]));
        var tooNew = Assert.Throws<SlidesPresentationFile.FileException>(() => SlidesPresentationFile.Read(newer, PathFor("b"), Ct));
        Assert.Equal(SlidesPresentationFile.Problem.UnsupportedFormat, tooNew.Problem);
        Assert.Contains("format 99", tooNew.Message);

        var zipNoManifest = PathFor("photos.mxuslides");
        File.WriteAllBytes(zipNoManifest, MacArchive.Ditto([new("photo.jpg", [1, 2, 3])]));
        Assert.Equal(SlidesPresentationFile.Problem.NotAPresentation,
            Assert.Throws<SlidesPresentationFile.FileException>(() => SlidesPresentationFile.Read(zipNoManifest, PathFor("c"), Ct)).Problem);
    }

    // MARK: Byte for byte with the Mac

    /// <summary>
    /// The Mac's JSONEncoder with .prettyPrinted and .sortedKeys: two-space indents, " : "
    /// between key and value, keys in order, "/" written "\/", nothing after the last brace.
    /// </summary>
    private static string MacManifest => """
        {
          "createdAt" : "2026-10-07T09:30:00Z",
          "formatVersion" : 1,
          "name" : "God’s Grace",
          "slideCount" : 2
        }
        """;

    private static string MacPayload(string imageHash, string audioHash) => $$"""
        {
          "audio" : [
            {
              "favorite" : false,
              "fileHash" : "{{audioHash}}",
              "fileName" : "Outro.wav",
              "id" : "a1",
              "name" : "Outro",
              "tags" : [

              ]
            }
          ],
          "media" : [
            {
              "classification" : "background",
              "collections" : [

              ],
              "favorite" : false,
              "fileHash" : "{{imageHash}}",
              "fileName" : "Ocean.png",
              "fileStatus" : "ready",
              "folder" : "Team Media\/Backgrounds",
              "id" : "m1",
              "loops" : false,
              "mediaKind" : "image",
              "name" : "Ocean",
              "pixelHeight" : 1080,
              "pixelWidth" : 1920,
              "statusDetail" : "",
              "tags" : [

              ]
            },
            {
              "classification" : "foreground",
              "collections" : [

              ],
              "favorite" : false,
              "fileHash" : "{{new string('9', 64)}}",
              "fileName" : "Lost.mov",
              "fileStatus" : "ready",
              "id" : "m2",
              "loops" : false,
              "mediaKind" : "video",
              "name" : "Lost",
              "statusDetail" : "",
              "tags" : [

              ]
            }
          ],
          "presentation" : {
            "folder" : "Songs\/Hymns",
            "id" : "deck",
            "name" : "God’s Grace",
            "presentationKind" : "song",
            "slides" : [
              {
                "background" : {
                  "mediaId" : "m1"
                },
                "id" : "s1",
                "name" : "Verse 1",
                "objects" : [
                  {
                    "id" : "o1",
                    "name" : "Lyrics",
                    "objectKind" : "text",
                    "text" : "God’s grace \/ so free"
                  }
                ]
              },
              {
                "actions" : [
                  {
                    "audioItemId" : "a1",
                    "id" : "act1",
                    "kind" : "fireAudio"
                  }
                ],
                "background" : {
                  "mediaId" : "m2"
                },
                "id" : "s2",
                "name" : "Walk Out",
                "objects" : [

                ]
              }
            ],
            "themeId" : "theme"
          },
          "themes" : [
            {
              "backgroundColorHex" : "#000000",
              "fontFamily" : "Mac Hymn Sans",
              "fontSize" : 96,
              "id" : "theme",
              "name" : "Hymns",
              "textColorHex" : "#FFFFFF"
            }
          ]
        }
        """;

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    [Fact]
    public void AFileLaidOutAsTheMacWritesItImports()
    {
        var image = Encoding.ASCII.GetBytes("\u0089PNG pretend picture");
        var sound = Encoding.ASCII.GetBytes("RIFF pretend sound");
        var font = TestFonts.Build("Mac Hymn Sans", "MacHymnSans-Regular");
        var file = PathFor("From the Mac.mxuslides");
        File.WriteAllBytes(file, MacArchive.Ditto([
            new("blobs/", null),
            new($"blobs/{Sha(image)}.png", image),
            new($"blobs/{Sha(sound)}.wav", sound),
            new("fonts/", null),
            new($"fonts/{Sha(font)}.ttf", font),
            new("manifest.json", Encoding.UTF8.GetBytes(MacManifest)),
            new("presentation.json", Encoding.UTF8.GetBytes(MacPayload(Sha(image), Sha(sound)))),
        ]));

        var contents = SlidesPresentationFile.Read(file, PathFor("scratch"), Ct);
        Assert.Equal(("God’s Grace", 2, 1), (contents.Manifest.Name, contents.Manifest.SlideCount, contents.Manifest.FormatVersion));
        Assert.Equal(new DateTimeOffset(2026, 10, 7, 9, 30, 0, TimeSpan.Zero), contents.Manifest.CreatedAt);
        Assert.Equal("Team Media/Backgrounds", contents.Payload.Media[0].Folder);
        Assert.Equal("God’s grace / so free", contents.Payload.Presentation.Slides[0].Objects[0].Text);
        Assert.Equal(2, contents.BlobFiles.Count);
        Assert.Single(contents.FontFiles);

        var target = NewLibrary("target");
        var result = SlidesPresentationFile.ImportContents(contents, target, SlidesPresentationFile.Conflict.KeepBoth, _ => false, Ct);
        Assert.Equal(("deck", "God’s Grace", 1, 2, 1), (result.PresentationId, result.Name, result.ThemesAdded, result.MediaAdded, result.FontsAdded));
        Assert.Equal(["Lost"], result.Missing);
        var ocean = target.Get<MediaItem>("m1")!;
        Assert.Equal((1920, 1080), (ocean.PixelWidth, ocean.PixelHeight));
        Assert.Equal(image, File.ReadAllBytes(target.BlobPath(ocean)!));
        Assert.Equal("act1", target.Get<Presentation>("deck")!.Slides[1].Actions![0].Id);
        Assert.True(File.Exists(Path.Combine(target.Root, "fonts", $"{Sha(font)}.ttf")), "the font lands in the library's fonts folder");
        Assert.Null(target.Get<MediaItem>("m2")); // its file wasn't in the bundle: reported, not half-made

        // A second import finds the font already in the library and leaves it.
        var again = SlidesPresentationFile.ImportContents(SlidesPresentationFile.Read(file, PathFor("scratch2"), Ct), target,
            SlidesPresentationFile.Conflict.Replace, FontIndex.Scan([Path.Combine(target.Root, "fonts")]).IsAvailable, Ct);
        Assert.Equal(0, again.FontsAdded);
    }

    [GeneratedRegex(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$")]
    private static partial Regex InternetDate();

    [Fact]
    public void TheFileWrittenHereHasTheMacsLayout()
    {
        var fontPath = WriteFile("library fonts/Hymn.ttf", "");
        File.WriteAllBytes(fontPath, TestFonts.Build("Mac Hymn Sans", "MacHymnSans-Regular"));
        var file = Exported([fontPath]);
        using var zip = ZipFile.OpenRead(file);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Equal("manifest.json", names[0]);
        Assert.Contains("presentation.json", names);
        Assert.Contains("blobs/", names);
        Assert.Contains("fonts/", names);
        Assert.All(names, n => Assert.DoesNotContain('\\', n));
        Assert.Equal(2, names.Count(n => n.StartsWith("blobs/", StringComparison.Ordinal) && n.Length > 6));
        var fontEntry = Assert.Single(names, n => n.StartsWith("fonts/", StringComparison.Ordinal) && n.Length > 6);
        Assert.Matches("^fonts/[0-9a-f]{64}\\.ttf$", fontEntry);
        Assert.All(zip.Entries.Where(e => !e.FullName.EndsWith('/')), e => Assert.Equal(0x81A4, (e.ExternalAttributes >> 16) & 0xFFFF));

        using var manifestStream = zip.GetEntry("manifest.json")!.Open();
        var manifest = JsonNode.Parse(manifestStream)!.AsObject();
        Assert.Equal(["createdAt", "formatVersion", "name", "slideCount"], manifest.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Matches(InternetDate(), manifest["createdAt"]!.GetValue<string>()); // whole seconds: the Mac refuses fractions
        Assert.Equal(1, manifest["formatVersion"]!.GetValue<int>());

        using var payloadStream = zip.GetEntry("presentation.json")!.Open();
        var payload = JsonNode.Parse(payloadStream)!.AsObject();
        Assert.Equal(["audio", "media", "presentation", "themes"], payload.Select(p => p.Key).Order(StringComparer.Ordinal));
        var media = payload["media"]!.AsArray()[0]!.AsObject();
        Assert.Equal("ready", media["fileStatus"]!.GetValue<string>());
        Assert.False(media.ContainsKey("inPoint"), "empty optionals are left out, as the Mac writes them");
    }

    [Fact]
    public void MediaWhoseFileIsMissingIsLeftOutAndNamed()
    {
        var source = NewLibrary("source");
        source.Save(Hymns());
        source.Save(Ocean("m1", new string('0', 64), "Gone Loop"));
        source.Save(Deck("m1", "nothing"));
        var summary = SlidesPresentationFile.Write(SlidesPresentationFile.Bundle(source, "deck")!, source.Blobs, [], PathFor("out.mxuslides"), Ct);
        Assert.Equal(["Gone Loop"], summary.MissingFiles);
        Assert.Equal(0, summary.FontCount);
        Assert.Empty(Directory.EnumerateFiles(Scratch, "*.tmp")); // the half-built file was moved into place
    }

    [Fact]
    public void ABundleCarriesItsThemesMediaAndTheSongsItsActionsPlay()
    {
        var library = NewLibrary();
        library.Save(Hymns());
        library.Save(Ocean("m1", "h"));
        library.Save(Audio("a1", "ha", "Outro"));
        var deck = Deck("m1", "a1");
        deck.Slides[0].ThemeId = "other";
        deck.Slides[1].Background = new CueMedia { MediaId = "input::item::camera" };
        library.Save(deck);
        var bundle = SlidesPresentationFile.Bundle(library, "deck")!;
        Assert.Equal(["theme"], bundle.Themes.Keys);
        Assert.Equal(["other"], bundle.MissingThemes);
        Assert.Equal(["m1"], bundle.Media.Keys);
        Assert.Equal(["a1"], bundle.Audio.Keys);
        Assert.Empty(bundle.MissingMedia); // a live input isn't library media
        Assert.Null(SlidesPresentationFile.Bundle(library, "nope"));
    }

    [Fact]
    public void RemappingSwapsIdsWhereverTheyAppear()
    {
        var deck = Deck("m1", "a1");
        var map = new Dictionary<string, string> { ["m1"] = "local" };
        var swapped = SlidesPresentationFile.Remapped(deck, map)!;
        Assert.Equal("local", swapped.Slides[0].Background?.MediaId);
        Assert.Equal("m1", deck.Slides[0].Background?.MediaId);
        Assert.Null(SlidesPresentationFile.Remapped(deck, new Dictionary<string, string> { ["zzz"] = "y" }));
        Assert.Null(SlidesPresentationFile.Remapped(deck, new Dictionary<string, string>()));
    }

    [Fact]
    public void AReZippedFolderIsReadToo()
    {
        var file = PathFor("rezipped.mxuslides");
        File.WriteAllBytes(file, MacArchive.Ditto([
            new("God's Grace/", null),
            new("God's Grace/manifest.json", Encoding.UTF8.GetBytes(MacManifest)),
            new("God's Grace/presentation.json", Encoding.UTF8.GetBytes(MacPayload("a", "b"))),
            new("__MACOSX/God's Grace/._manifest.json", [0, 5, 22, 7]),
        ]));
        var contents = SlidesPresentationFile.Read(file, PathFor("scratch"), Ct);
        Assert.Equal("deck", contents.Payload.Presentation.Id);
    }

    [Fact]
    public void UnsafeEntriesAreRefused()
    {
        var escape = PathFor("escape.mxuslides");
        File.WriteAllBytes(escape, MacArchive.Ditto([new("../evil.txt", [1]), new("manifest.json", Encoding.UTF8.GetBytes(MacManifest))]));
        var refused = Assert.Throws<SlidesPresentationFile.FileException>(() => SlidesPresentationFile.Read(escape, PathFor("x"), Ct));
        Assert.Equal(SlidesPresentationFile.Problem.Archive, refused.Problem);
        Assert.False(File.Exists(PathFor("evil.txt")));

        Assert.False(SafeZip.IsSafeRelativePath("C:/Windows/evil.dll"));
        Assert.False(SafeZip.IsSafeRelativePath("blobs/CON.png"));
        Assert.False(SafeZip.IsSafeRelativePath("/root.txt"));
        Assert.True(SafeZip.IsSafeRelativePath("blobs/0f00.png"));
        Assert.True(SafeZip.IsSafeRelativePath("fonts/"));
    }

    [Fact]
    public void TheSuggestedFileNameIsSafeForWindows()
    {
        Assert.Equal("AC-DC- Live- -.mxuslides", SlidesPresentationFile.FileName(new Presentation { Name = "AC/DC: Live? |" }));
        Assert.Equal("Presentation.mxuslides", SlidesPresentationFile.FileName(new Presentation { Name = "  " }));
        Assert.Equal("Amazing Grace.mxuslides", SlidesPresentationFile.FileName(new Presentation { Name = "Amazing Grace" }));
    }
}
