using System.IO.Compression;
using System.Text;
using MxU.Slides.Core.MotionTemplates;

namespace MxU.Slides.Core.Tests.MotionTemplates;

// Reading Lottie files from disk (Phase 11): a .json with its pictures beside it, and .lottie
// packages (versions 1 and 2), each into one self-contained animation; never reading outside
// the file's own folder or package.
public sealed class LottieFilesTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "LottieFiles-" + Guid.NewGuid().ToString("N"));

    public LottieFilesTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
    }

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

    /// <summary>A small animation whose picture is kept outside it, as Bodymovin writes it with "include in json" off.</summary>
    private static string WithOutsidePicture(string u, string p) => $$"""
        { "v": "5.7.4", "fr": 30, "ip": 0, "op": 30, "w": 400, "h": 300, "nm": "Comp 1",
          "assets": [ { "id": "image_0", "w": 100, "h": 100, "u": "{{u}}", "p": "{{p}}", "e": 0 } ],
          "layers": [ { "ty": 2, "nm": "Logo", "ind": 1, "ip": 0, "op": 30, "st": 0, "ks": {}, "refId": "image_0" } ] }
        """;

    private static string PictureOf(LottieDocument document) => document.Root["assets"]![0]!["p"]!.GetValue<string>();

    [Fact]
    public void AJsonFilesPicturesBesideItAreTakenIn()
    {
        Directory.CreateDirectory(Path.Combine(folder, "images"));
        File.WriteAllBytes(Path.Combine(folder, "images", "img_0.png"), Png);
        var path = Path.Combine(folder, "Welcome Bumper.json");
        File.WriteAllText(path, WithOutsidePicture("images/", "img_0.png"));

        var (document, name) = LottieFiles.Read(path);
        Assert.Equal("data:image/png;base64," + Convert.ToBase64String(Png), PictureOf(document));
        Assert.Equal(1, document.Root["assets"]![0]!["e"]!.GetValue<int>());
        // "Comp 1" is After Effects' default name, so the file's name is used.
        Assert.Equal("Welcome Bumper", name);
    }

    [Fact]
    public void APictureOutsideTheFilesFolderIsNeverRead()
    {
        File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "outside-" + Path.GetFileName(folder) + ".png"), Png);
        var path = Path.Combine(folder, "sneaky.json");
        File.WriteAllText(path, WithOutsidePicture("../", "outside-" + Path.GetFileName(folder) + ".png"));
        try
        {
            var (document, _) = LottieFiles.Read(path);
            Assert.Equal("outside-" + Path.GetFileName(folder) + ".png", PictureOf(document));
        }
        finally
        {
            File.Delete(Path.Combine(Path.GetTempPath(), "outside-" + Path.GetFileName(folder) + ".png"));
        }
    }

    private string Package(params (string Name, string Content)[] entries) => Package(entries.Select(e => (e.Name, Encoding.UTF8.GetBytes(e.Content))).ToArray());

    private string Package(params (string Name, byte[] Content)[] entries)
    {
        var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".lottie");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(content);
        }
        return path;
    }

    [Fact]
    public void ADotLottiePackageVersionOneIsUnpackedWithItsPictures()
    {
        var path = Package(
            ("manifest.json", Encoding.UTF8.GetBytes("""{ "version": "1", "animations": [ { "id": "strap" } ] }""")),
            ("animations/strap.json", Encoding.UTF8.GetBytes(WithOutsidePicture("/images/", "img_0.png").Replace("Comp 1", "Name Strap"))),
            ("images/img_0.png", Png));
        var (document, name) = LottieFiles.Read(path);
        Assert.Equal("Name Strap", name);
        Assert.StartsWith("data:image/png;base64,", PictureOf(document));
    }

    [Fact]
    public void ADotLottiePackageVersionTwoUsesItsInitialAnimation()
    {
        var path = Package(
            ("manifest.json", """{ "version": "2", "animations": [ { "id": "a" }, { "id": "b" } ], "initial": { "animation": "b" } }"""),
            ("a/a.json", LottieSamples.Strap.Replace("Speaker Strap", "First")),
            ("a/b.json", LottieSamples.Strap.Replace("Speaker Strap", "Second")));
        Assert.Equal("Second", LottieFiles.Read(path).Document.Name);
    }

    [Fact]
    public void APackageWithNoAnimationOrNotAZipIsRefused()
    {
        var empty = Package(("manifest.json", """{ "version": "1" }"""));
        Assert.Contains("no animation", Assert.Throws<LottieFormatException>(() => LottieFiles.Read(empty)).Message);
        var fake = Path.Combine(folder, "fake.lottie");
        File.WriteAllText(fake, "{}");
        Assert.Contains("isn't a zip", Assert.Throws<LottieFormatException>(() => LottieFiles.Read(fake)).Message);
    }

    [Fact]
    public void ADataUriIsTypedByItsFileName()
    {
        Assert.StartsWith("data:image/jpeg;base64,", LottieFiles.DataUri([1], "photo.JPG"));
        Assert.StartsWith("data:font/ttf;base64,", LottieFiles.DataUri([1], "Montserrat-Bold.ttf"));
        Assert.StartsWith("data:image/png;base64,", LottieFiles.DataUri([1], "img_0"));
    }

    [Theory]
    [InlineData("Comp 1", "file", "file")]
    [InlineData("Main Comp", "file", "Main Comp")]
    [InlineData("", "file", "file")]
    [InlineData("Lower Third v2", "file", "Lower Third v2")]
    public void ATemplateIsNamedAfterItsCompositionUnlessThatsADefault(string comp, string file, string expected) =>
        Assert.Equal(expected, LottieFiles.NameFor(LottieDocument.Parse(LottieSamples.Strap.Replace("Speaker Strap", comp)), file));

    [Fact]
    public void BothExtensionsAreLottieFiles()
    {
        Assert.True(LottieFiles.IsLottieFile(@"C:\x\strap.JSON"));
        Assert.True(LottieFiles.IsLottieFile("strap.lottie"));
        Assert.False(LottieFiles.IsLottieFile("strap.mp4"));
    }
}
