using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Library;

/// <summary>
/// The fonts a .mxuslides carries: which names a document uses, which files hold them, which
/// of those may travel, and installing them at the other end. Follows FontSharing.swift; the
/// Mac asks Core Text, Windows reads the files' name tables.
/// </summary>
public sealed class FontSharingTests : ScratchFolderTest
{
    private string Font(string file, string family, string postScript, string? typographic = null, ushort fsType = 0)
    {
        var path = PathFor(file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, TestFonts.Build(family, postScript, typographic, fsType));
        return path;
    }

    [Fact]
    public void NamesAreFoundWhereverADocumentKeepsThem()
    {
        var theme = new Theme
        {
            Id = "t", Name = "Look", FontFamily = "Montserrat", FontSize = 80, TextColorHex = "#FFF", BackgroundColorHex = "#000",
            Slides = [new Slide { Id = "s", Name = "Lyrics", Objects = [new SlideObject { Id = "o", ObjectKind = SlideObjectKind.Text, Name = "", Text = "",
                TextStyle = new TextStyle { FontName = "Montserrat-Bold" } }] }],
        };
        Assert.Equal(new HashSet<string> { "Montserrat", "Montserrat-Bold" }, FontSharing.Names(theme));
    }

    [Fact]
    public void TheNameTableGivesFamilyFacesAndFullName()
    {
        var path = Font("Montserrat-SemiBold.ttf", "Montserrat SemiBold", "Montserrat-SemiBold", typographic: "Montserrat");
        var names = Assert.Single(FontNames.Read(path));
        Assert.Equal(("Montserrat", "Montserrat SemiBold Regular", "Montserrat-SemiBold"), (names.Family, names.FullName, names.PostScriptName));
        var inside = FontSharing.FacesIn(path)!.Value;
        Assert.Equal("Montserrat", inside.Family);
        Assert.Equal(["Montserrat-SemiBold"], inside.Faces);
        Assert.Empty(FontNames.Read(WriteFile("not-a-font.ttf", "hello")));
    }

    [Fact]
    public void ResolvingFindsTheExactFaceElseTheWholeFamily()
    {
        Font("fonts/a.ttf", "Hymn Sans", "HymnSans-Regular");
        Font("fonts/b.ttf", "Hymn Sans", "HymnSans-Bold");
        var index = FontIndex.Scan([PathFor("fonts")]);
        Assert.Equal(["HymnSans-Bold"], index.Resolve("HymnSans-Bold").Select(f => f.Name));
        Assert.Equal(2, index.Resolve("Hymn Sans").Count);
        Assert.Empty(index.Resolve("Arial"));
        Assert.True(index.IsAvailable("HymnSans-Regular"));
        Assert.True(index.IsAvailable("Hymn Sans Regular")); // the full name counts too
        Assert.False(index.IsAvailable("Comic Sans"));
    }

    [Fact]
    public void FilesToShareSkipsSharedNamesAndSystemFonts()
    {
        var system = Font("Windows/Fonts/system.ttf", "Segoe UI", "SegoeUI");
        var own = Font("fonts/own.ttf", "Hymn Sans", "HymnSans-Regular");
        var faces = new Dictionary<string, FontSharing.Face[]>
        {
            ["Segoe UI"] = [new("SegoeUI", "Segoe UI", system)],
            ["Hymn Sans"] = [new("HymnSans-Regular", "Hymn Sans", own)],
        };
        var previous = FontSharing.SystemFontsFolder;
        FontSharing.SystemFontsFolder = PathFor("Windows/Fonts");
        try
        {
            var files = FontSharing.FilesToShare(["Segoe UI", "Hymn Sans", "Unknown"], new HashSet<string>(), n => faces.GetValueOrDefault(n) ?? []);
            Assert.Equal([own], files.Keys);
            Assert.Empty(FontSharing.FilesToShare(["Hymn Sans"], new HashSet<string> { "Hymn Sans" }, n => faces[n]));
            Assert.Null(FontSharing.Document(system)); // Windows' own fonts aren't ours to hand out
        }
        finally
        {
            FontSharing.SystemFontsFolder = previous;
        }
    }

    [Fact]
    public void ARestrictedLicenceStaysHome()
    {
        var restricted = Font("Locked.ttf", "Locked", "Locked-Regular", fsType: 0x0002);
        Assert.Null(FontSharing.Document(restricted));
        var editable = FontSharing.Document(Font("Open.ttf", "Open", "Open-Regular", fsType: 0x0008))!;
        Assert.Equal(("Open", "ttf"), (editable.Family, editable.Ext));
        Assert.Equal(["Open-Regular"], editable.Faces);
        Assert.Equal(64, editable.Id.Length);
    }

    [Fact]
    public void AFontIsInstalledOnlyWhenSomeFaceIsMissing()
    {
        Assert.True(FontSharing.NeedsInstall([], _ => true));
        Assert.True(FontSharing.NeedsInstall(["A-Bold", "A-Regular"], f => f == "A-Regular"));
        Assert.False(FontSharing.NeedsInstall(["A-Regular"], _ => true));

        var library = PathFor("library");
        var incoming = Font("bundle/fonts/abc.ttf", "Hymn Sans", "HymnSans-Regular");
        Assert.Equal(0, FontSharing.Install([incoming], library, _ => true));
        Assert.False(Directory.Exists(Path.Combine(library, "fonts")));
        Assert.Equal(1, FontSharing.Install([incoming, WriteFile("bundle/fonts/junk.ttf", "junk")], library, _ => false));
        Assert.True(File.Exists(Path.Combine(library, "fonts", "abc.ttf")));
    }

    [Fact]
    public void AnExportPacksTheFontsItsThemeNames()
    {
        var library = NewLibrary();
        var fonts = Path.Combine(library.Root, FontSharing.LibraryFolderName);
        Directory.CreateDirectory(fonts);
        File.WriteAllBytes(Path.Combine(fonts, "x.ttf"), TestFonts.Build("Hymn Sans", "HymnSans-Regular"));
        File.WriteAllBytes(Path.Combine(fonts, "y.ttf"), TestFonts.Build("Other Face", "OtherFace-Regular"));
        library.Save(new Theme { Id = "t", Name = "Hymns", FontFamily = "Hymn Sans", FontSize = 90, TextColorHex = "#FFF", BackgroundColorHex = "#000" });
        library.Save(new Presentation { Id = "p", Name = "Song", PresentationKind = PresentationKind.Song, ThemeId = "t", Slides = [] });
        var bundle = SlidesPresentationFile.Bundle(library, "p")!;
        var files = SlidesPresentationFile.FontFiles(bundle, FontIndex.Scan([fonts]));
        Assert.Equal(["x.ttf"], files.Select(Path.GetFileName));
    }
}
