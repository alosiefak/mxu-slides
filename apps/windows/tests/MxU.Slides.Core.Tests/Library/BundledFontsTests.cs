using System.Security.Cryptography;
using MxU.Slides.Core.Library;

namespace MxU.Slides.Core.Tests.Library;

// The fonts the app ships (apps/windows/assets/fonts, copied beside these tests as Fonts\): every
// library gets them, under their hashes, and the credits are read from the files themselves.
public class BundledFontsTests : ScratchFolderTest
{
    private static string Shipped => BundledFonts.ShippedFolder;

    [Fact]
    public void TheFifteenApprovedFontsShip()
    {
        var names = BundledFonts.Files(Shipped).Select(Path.GetFileName).ToList();
        Assert.Equal(15, names.Count);
        foreach (var expected in new[] { "Poppins-Black.ttf", "BebasNeue-Regular.ttf", "DMSerifDisplay-Italic.ttf", "Montserrat-ExtraBold.ttf" })
            Assert.Contains(expected, names);
        Assert.True(File.Exists(Path.Combine(Shipped, BundledFonts.LicenceFileName)), "the OFL ships beside them");
    }

    [Fact]
    public void ALibraryGetsEachFontOnceUnderItsHash()
    {
        var root = PathFor("library");
        Assert.Equal(15, BundledFonts.AddTo(root, Shipped));
        Assert.Equal(0, BundledFonts.AddTo(root, Shipped));

        var stored = FontFiles.Stored(root);
        Assert.Equal(15, stored.Count);
        foreach (var file in stored)
        {
            var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));
            Assert.Equal(hash + ".ttf", Path.GetFileName(file));
        }
        Assert.Contains("Montserrat", FontFiles.Families(root));
        Assert.Contains("DM Serif Display", FontFiles.Families(root));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, FontFiles.FolderName), "*.tmp"));
    }

    [Fact]
    public void AFontDeletedFromTheLibraryComesBack()
    {
        var root = PathFor("library");
        BundledFonts.AddTo(root, Shipped);
        File.Delete(FontFiles.Stored(root)[0]);
        Assert.Equal(1, BundledFonts.AddTo(root, Shipped));
    }

    [Fact]
    public void EachFaceIsTheWeightAndStyleItsNameSays()
    {
        // What FontMap relies on to pick "Montserrat-Black" exactly: the PostScript name, and the OS/2 weight and slant.
        var faces = BundledFonts.Files(Shipped).SelectMany(f => FontFiles.Faces(File.ReadAllBytes(f))).ToDictionary(f => f.PostScriptName);
        Assert.Equal(900, faces["Montserrat-Black"].Weight);
        Assert.Equal(800, faces["Montserrat-ExtraBold"].Weight);
        Assert.Equal(600, faces["Poppins-SemiBold"].Weight);
        Assert.Equal(500, faces["Poppins-Medium"].Weight);
        Assert.True(faces["Poppins-Italic"].Italic);
        Assert.True(faces["DMSerifDisplay-Italic"].Italic);
        Assert.False(faces["DMSerifDisplay-Regular"].Italic);
        Assert.Equal("Montserrat", faces["Montserrat-Black"].Family);
        Assert.Equal("Montserrat Black", faces["Montserrat-Black"].FullName);
    }

    [Fact]
    public void CreditsComeFromTheFontsNameTables()
    {
        var credits = BundledFonts.Credits(Shipped);
        Assert.Equal(["Bebas Neue", "DM Serif Display", "Montserrat", "Poppins"], credits.Select(c => c.Family));
        var poppins = credits.Single(c => c.Family == "Poppins");
        Assert.Equal(["Regular", "Italic", "Medium", "SemiBold", "Bold", "ExtraBold", "Black"], poppins.Styles);
        Assert.Contains("The Poppins Project Authors", poppins.Copyright);
        Assert.All(credits, c => Assert.StartsWith("Copyright", c.Copyright));
        Assert.All(credits, c => Assert.Contains("Open Font License", c.Licence));
    }

    [Fact]
    public void TheShippedNoticeMatchesTheFonts()
    {
        var licence = File.ReadAllText(Path.Combine(Shipped, BundledFonts.LicenceFileName));
        var notice = BundledFonts.Notice(BundledFonts.Credits(Shipped), licence);
        foreach (var credit in BundledFonts.Credits(Shipped)) Assert.Contains(credit.Copyright, notice);
        Assert.Contains("SIL OPEN FONT LICENSE Version 1.1", notice);

        // The notice in the repo is generated from the fonts; set MXU_WRITE_FONT_NOTICES=1 to rewrite it after changing them.
        var committed = Path.Combine(Shipped, BundledFonts.NoticeFileName);
        if (Environment.GetEnvironmentVariable("MXU_WRITE_FONT_NOTICES") == "1" && RepoFontsFolder() is { } repo)
            File.WriteAllText(Path.Combine(repo, BundledFonts.NoticeFileName), notice);
        else
            Assert.Equal(notice, File.ReadAllText(committed).Replace("\r\n", "\n"));
    }

    /// <summary>apps/windows/assets/fonts in the checkout these tests were built from.</summary>
    private static string? RepoFontsFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "assets", "fonts");
            if (File.Exists(Path.Combine(candidate, "SOURCES.txt"))) return candidate;
        }
        return null;
    }
}
