using System.IO.Compression;
using MxU.Slides.Core.Model;
using static MxU.Slides.ProImport.Tests.Proto;
using G = Pro.SerializationInterop.RVProtoData.Graphics.Types;
using RV = Pro.SerializationInterop.RVProtoData;

namespace MxU.Slides.ProImport.Tests;

public class ProPlaylistTests
{
    private static RV.PlaylistItem PresentationItem(string id, string name, string path, string? arrangement = null)
    {
        var presentation = new RV.PlaylistItem.Types.Presentation
        {
            DocumentPath = new() { AbsoluteString = FileUrl(path), Local = new() { Path = string.Join("/", path.Split('/').TakeLast(3)) } },
        };
        if (arrangement is not null) presentation.Arrangement = Uuid(arrangement);
        return new RV.PlaylistItem { Uuid = Uuid(id), Name = name, Presentation = presentation };
    }

    private static RV.PlaylistItem HeaderItem(string id, string name) =>
        new() { Uuid = Uuid(id), Name = name, Header = new() { Color = new() { Red = 1, Alpha = 1 } } };

    private static RV.PlaylistItem MediaCueItem(string id, string name, string mediaPath) => new()
    {
        Uuid = Uuid(id),
        Name = name,
        Cue = new()
        {
            Uuid = Uuid($"cue-{id}"),
            Name = name,
            Actions =
            {
                new RV.Action
                {
                    Uuid = Uuid($"action-{id}"),
                    Media = new() { Element = new() { Uuid = Uuid($"media-{id}"), Url = new() { AbsoluteString = FileUrl(mediaPath) }, Image = new() } },
                },
            },
        },
    };

    private static RV.Playlist PlaylistNode(string id, string name, params RV.PlaylistItem[] items)
    {
        var node = new RV.Playlist { Uuid = Uuid(id), Name = name, Items = new() };
        node.Items.Items.Add(items);
        return node;
    }

    private static RV.PlaylistDocument Document(RV.Playlist[] leaves, bool foldered = true)
    {
        var folder = new RV.Playlist { Uuid = Uuid("FOLDER-1"), Name = "Folder", Playlists = new() };
        folder.Playlists.Playlists.Add(leaves);
        var root = new RV.Playlist { Uuid = Uuid("ROOT-1"), Name = "PLAYLIST", Playlists = new() };
        root.Playlists.Playlists.Add(foldered ? [folder] : leaves);
        return new RV.PlaylistDocument { Type = RV.PlaylistDocument.Types.Type.Presentation, RootNode = root };
    }

    [Fact]
    public void PlaylistsBecomeServicesThroughFolders()
    {
        var doc = Document([PlaylistNode("PL-1", "Weekend Services",
            HeaderItem("IT-H", "Worship"),
            PresentationItem("IT-P", "Opener", "/Users/anna/Documents/Show/Libraries/Default/Opener.pro", "ARR-1"),
            PresentationItem("IT-GONE", "Missing", "/Users/anna/Documents/Show/Libraries/Default/Missing.pro"),
            MediaCueItem("IT-M", "Bumper", "/Users/anna/Media/bumper.png"))]);

        var mapped = ProPlaylistMapper.MapPresentationPlaylists(doc, r => r.AbsolutePath?.EndsWith("Opener.pro") == true ? "pres-opener" : null);
        var service = Assert.Single(mapped.Services).Service;
        Assert.Equal("pl-1", service.Id);
        Assert.Equal("Weekend Services", service.Name);
        Assert.Empty(service.ServiceDate);
        Assert.Equal(3, service.Items.Count);
        Assert.Equal(ServiceItemKind.Header, service.Items[0].ItemKind);
        Assert.Equal("Worship", service.Items[0].Name);
        Assert.Equal("#FF0000FF", service.Items[0].ColorHex);
        Assert.Equal(ServiceItemKind.Presentation, service.Items[1].ItemKind);
        Assert.Equal("pres-opener", service.Items[1].RefId);
        Assert.Equal("arr-1", service.Items[1].ArrangementId);
        Assert.Equal(ServiceItemKind.Media, service.Items[2].ItemKind);
        Assert.StartsWith(ProDocumentMapper.PlaceholderPrefix, service.Items[2].RefId);
        Assert.Single(mapped.MediaWants);
        Assert.Contains(mapped.Services[0].Warnings, w => w.Contains("Missing"));
    }

    [Fact]
    public void ReplacingMediaIDsResolvesDropsAndFlipsAudio()
    {
        var service = new Service
        {
            Id = "s", Name = "S", ServiceDate = "",
            Items =
            [
                new ServiceItem { Id = "1", ItemKind = ServiceItemKind.Media, Name = "Track", RefId = "pro-media://a" },
                new ServiceItem { Id = "2", ItemKind = ServiceItemKind.Media, Name = "Lost", RefId = "pro-media://b" },
                new ServiceItem { Id = "3", ItemKind = ServiceItemKind.Presentation, Name = "Song", RefId = "pres-1" },
            ],
        };
        var (resolved, warnings) = ProPlaylistMapper.ReplacingMediaIds(
            service, new Dictionary<string, string> { ["pro-media://a"] = "audio-1" }, new HashSet<string> { "audio-1" });
        Assert.Equal(2, resolved.Items.Count);
        Assert.Equal(ServiceItemKind.Audio, resolved.Items[0].ItemKind);
        Assert.Equal("audio-1", resolved.Items[0].RefId);
        Assert.Equal("pres-1", resolved.Items[1].RefId);
        Assert.Single(warnings);
    }

    private static RV.Presentation MinimalPresentation(string id, string name) => new()
    {
        Uuid = Uuid(id),
        Name = name,
        Cues =
        {
            new RV.Cue
            {
                Uuid = Uuid($"CUE-{id}"),
                Actions =
                {
                    new RV.Action
                    {
                        Uuid = Uuid($"ACT-{id}"),
                        Slide = new()
                        {
                            Presentation = new()
                            {
                                BaseSlide = new()
                                {
                                    Elements =
                                    {
                                        Wrap(new G.Element
                                        {
                                            Uuid = Uuid($"EL-{id}"), Opacity = 1,
                                            Text = new() { RtfData = RtfBytes("{\\rtf1\\ansi{\\fonttbl\\f0\\fnil Helvetica;}\\f0 Line}") },
                                        }),
                                    },
                                },
                            },
                        },
                    },
                },
            },
        },
    };

    [Fact]
    public async Task WorkspaceImportBringsPlaylistsAsServices()
    {
        using var library = new TestLibrary();
        using var show = new TempFolder();
        Write(MinimalPresentation("PRES-1", "Opener"), show.Combine("Libraries", "Default", "Opener.pro"));
        var item = PresentationItem("IT-1", "Opener", "/Users/original-machine/Show/Libraries/Default/Opener.pro");
        item.Presentation.DocumentPath.Local = new() { Path = "Libraries/Default/Opener.pro" };
        Write(Document([PlaylistNode("PL-1", "Sunday", item)]), show.Combine("Playlists", "Library"));

        var progress = new RecordingProgress();
        var result = await new ProWorkspaceImporter(library.Store).ImportWorkspaceAsync(
            show.Path, progress: progress, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(["Sunday"], result.ServicesImported);
        Assert.Contains("Presentations", progress.Phases);
        Assert.Contains("Playlists", progress.Phases);

        var service = library.Store.Get<Service>("pl-1")!;
        Assert.Equal("Sunday", service.Name);
        var only = Assert.Single(service.Items);
        Assert.Equal(ServiceItemKind.Presentation, only.ItemKind);
        Assert.Equal("pres-1", only.RefId);
    }

    [Fact]
    public async Task WorkspaceWithoutPlaylistsWarnsInsteadOfSilence()
    {
        using var library = new TestLibrary();
        using var show = new TempFolder();
        Directory.CreateDirectory(show.Combine("Libraries", "Default"));
        var importer = new ProWorkspaceImporter(library.Store);
        var noFolder = await importer.ImportWorkspaceAsync(show.Path, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(noFolder.ServicesImported);
        Assert.Contains(noFolder.Warnings, w => w.Contains("no Playlists folder"));

        Directory.CreateDirectory(show.Combine("Playlists"));
        var noLibrary = await importer.ImportWorkspaceAsync(show.Path, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(noLibrary.Warnings, w => w.Contains("no Playlists/Library document"));
    }

    [Fact]
    public async Task PlaylistBundleImportsAsService()
    {
        using var library = new TestLibrary();
        using var stage = new TempFolder();
        const string originalPro = "Users/test/Show/Libraries/Default/Opener.pro";
        const string originalMedia = "Users/test/Show/Media/Assets/bumper.png";
        Write(MinimalPresentation("PRES-9", "Opener"), stage.Combine(originalPro.Split('/')));
        WriteImage(stage.Combine(originalMedia.Split('/')));
        Write(Document([PlaylistNode("PL-9", "Exported Set",
            PresentationItem("IT-1", "Opener", "/" + originalPro),
            MediaCueItem("IT-2", "Bumper", "/" + originalMedia))]), stage.Combine("playlist.data"));
        using var archives = new TempFolder();
        var bundle = archives.Combine($"{Guid.NewGuid()}.proPlaylist");
        ZipFile.CreateFromDirectory(stage.Path, bundle);

        var summary = await new ProPresenterImporter(library.Store).ImportPlaylistBundleAsync(
            bundle, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(summary.FailureReason);
        Assert.Equal(["Exported Set"], summary.Services);
        Assert.Single(summary.Documents);
        Assert.Equal(1, summary.MediaImported);

        var service = library.Store.Get<Service>("pl-9")!;
        Assert.Equal(2, service.Items.Count);
        Assert.Equal(ServiceItemKind.Presentation, service.Items[0].ItemKind);
        Assert.Equal("pres-9", service.Items[0].RefId);
        Assert.Equal(ServiceItemKind.Media, service.Items[1].ItemKind);
        Assert.False(service.Items[1].RefId.StartsWith(ProDocumentMapper.PlaceholderPrefix));
    }
}
