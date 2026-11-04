using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;
using MxU.Slides.Sync.Automerge;
using MxU.Slides.Sync.Engine;
using MxU.Slides.Sync.Format;
using MxU.Slides.Windows.Views;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>
/// Library sync with a Mac (Phase 10), through the real app. A scratch "Mac" library folder is
/// made with documents written the way the Mac writes them (Automerge, under a Mac actor), and the
/// app is pointed at it from Settings › Library sync. Then: the first sync (both sides backed up,
/// the Mac's song and its picture brought in, this PC's library sent out, all readable by the
/// Mac's rules); one song edited on both sides at once and the merged song shown; the folder
/// going offline and coming back; a document from a "newer Mac" stopping sync untouched until
/// it's left out. Pictures sync-01 to sync-06.
/// </summary>
public sealed partial class SelfTest
{
    private const string MacSongId = "8D1F2C3B-6A5E-4F00-9C11-SELFTESTSONG";

    private async Task SyncCheck()
    {
        var macFolder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"MxU self-test Mac library {Guid.NewGuid():N}")).FullName;
        Window? settings = null;
        try
        {
            Check("sync: automerge.dll is the Automerge the Mac builds (0.7.2)", AmDocument.LibraryVersion.Contains("automerge 0.7.2"));
            var (pictureHash, pictureId) = MakeMacLibrary(macFolder);

            // Settings › Library sync › Choose a shared folder (the folder dialog's answer, given directly).
            Check("sync: the scratch Mac library is a folder sync accepts", SyncEngine.CheckFolder(macFolder, model.Library.Root) is null);
            Check("sync: this PC's own library isn't", SyncEngine.CheckFolder(model.Library.Root, model.Library.Root) is not null);
            Check("sync: the folder is chosen", model.ChooseLibrarySyncFolder(macFolder) is null);
            var engine = model.LibrarySync!;
            await Settle(engine);
            Check("sync: the first sync finishes up to date", engine.Status.State == SyncState.UpToDate);
            Check("sync: both sides were backed up before the first sync",
                Directory.EnumerateDirectories(Path.Combine(engine.StateFolder, "backups")).Any(b => File.Exists(Path.Combine(b, "Mac", "presentations", MacSongId + ".automerge"))));

            var macSong = model.LibraryItems.FirstOrDefault(e => e.Presentation.Id == MacSongId)?.Presentation;
            Check("sync: the Mac's song is in the library", macSong is not null);
            var picture = model.Library.Get<MediaItem>(pictureId);
            Check("sync: the Mac's picture came across, its media item and its file", picture is not null
                && model.Library.BlobPath(picture) is { } blob && Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(blob))) == pictureHash);
            var sent = model.Library.All<Presentation>().Where(p => p.Id != MacSongId).ToList();
            var readable = sent.Count(p => MacReads(macFolder, MacKind.ForWindowsType(typeof(Presentation))!, p.Id) is { } read
                && MxuJson.StructurallyEqual(read, p));
            Check($"sync: every Windows presentation went out, and reads back on the Mac exactly ({readable} of {sent.Count})", sent.Count > 0 && readable == sent.Count);
            if (macSong is null) return;

            model.Open(macSong, null, null);
            await Step("sync-01-mac-song-in-library", () => { }, settle: 900);

            // The same song edited on both sides at once: a line on Windows, another on the Mac.
            var windowsSong = MxuJson.Clone(model.Library.Get<Presentation>(MacSongId)!);
            windowsSong.Slides[0].Objects[0].Text = "Great is Thy faithfulness, O God my Father (edited on Windows)";
            model.SavePresentation(windowsSong);
            MacEdits(macFolder, song => song.Slides[2].Objects[0].Text = "Pardon for sin and a peace that endureth (edited on the Mac)");
            await Settle(engine);
            var merged = model.Library.Get<Presentation>(MacSongId)!;
            Check("sync: the Windows edit and the Mac edit are both in the song here",
                merged.Slides[0].Objects[0].Text.Contains("edited on Windows") && merged.Slides[2].Objects[0].Text.Contains("edited on the Mac"));
            var onMac = MacReads(macFolder, MacKind.ForWindowsType(typeof(Presentation))!, MacSongId);
            Check("sync: and the Mac's copy says the same", onMac is not null && MxuJson.StructurallyEqual(onMac, merged));
            Check("sync: the open song shows the merged words", model.CurrentPresentation?.Slides[2].Objects[0].Text.Contains("edited on the Mac") == true);
            await Step("sync-02-merged-song", () => { }, settle: 900);
            if (model.Slides.Count > 2) await Snapshot("sync-02b-merged-slide", Core.Scene.SlideSceneBuilder.Scene(model.Slides[2].Slide, model.CurrentTheme, merged));

            settings = SettingsPage();
            await Delay(700);
            Save(settings, "sync-03-settings");
            Check("sync: the settings page shows the folder, the status and the log",
                Find<TextBox>(settings, "SyncFolder")?.Text == engine.SharedFolderPath
                && Find<TextBlock>(settings, "SyncStatus")?.Text.StartsWith("Up to date", StringComparison.Ordinal) == true
                && Find<StackPanel>(settings, "SyncLog")?.Children.Count > 1);

            // The folder goes away (an unplugged drive, OneDrive offline), and comes back.
            var away = macFolder + " (offline)";
            Directory.Move(macFolder, away);
            await Task.Run(() => engine.SyncNowAndWait());
            await Delay(500);
            Check("sync: a folder that goes away pauses sync", engine.Status.State == SyncState.WaitingForFolder);
            Check("sync: and deletes nothing here", model.Library.Get<Presentation>(MacSongId) is not null);
            Save(settings, "sync-04-folder-offline");
            Directory.Move(away, macFolder);
            await Settle(engine);
            Check("sync: when the folder is back, sync carries on", engine.Status.State == SyncState.UpToDate);

            // A song from a newer Mac, with a kind this version doesn't know: never read, never written.
            var newer = WriteMacDocument(macFolder, MacKind.ForWindowsType(typeof(Presentation))!, MacSong("NEWER-MAC-SONG", "From a newer Mac"),
                // A kind of presentation no schema will ever have: whatever version these models are, a Mac can be one ahead.
                edit => edit.Put(AmPath.Root, "presentationKind", AmWrite.String("windowsTestFutureCase")));
            var newerBytes = File.ReadAllBytes(newer);
            await Settle(engine);
            Check("sync: a document this version can't read stops sync to ask", engine.Attention?.Kind == AttentionKind.Unreadable);
            Check("sync: it isn't brought in", model.Library.Get<Presentation>("NEWER-MAC-SONG") is null);
            Check("sync: and its file is left exactly as it was", File.ReadAllBytes(newer).AsSpan().SequenceEqual(newerBytes));
            await Delay(500);
            Save(settings, "sync-05-needs-attention");
            engine.LeaveOutUnreadable();
            await Settle(engine);
            Check("sync: left out, sync carries on", engine.Status.State == SyncState.UpToDate && engine.Attention is null);
            Check("sync: still untouched", File.ReadAllBytes(newer).AsSpan().SequenceEqual(newerBytes));
            await Delay(500);
            Save(settings, "sync-06-carried-on");
        }
        catch (Exception ex)
        {
            Check($"sync: no failure ({ex.GetType().Name}: {ex.Message})", false);
        }
        finally
        {
            settings?.Close();
            model.TurnOffLibrarySync();
            await Delay(1500);
            try { Directory.Delete(macFolder, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Waits for sync to look through everything and finish, media copies included (off the UI thread, which keeps drawing).</summary>
    private static async Task Settle(SyncEngine engine)
    {
        await Task.Run(() =>
        {
            engine.SyncNowAndWait(TimeSpan.FromMinutes(3));
            engine.WaitIdle(TimeSpan.FromMinutes(2));
        });
        // The library's Changed reaches the screen through the dispatcher.
        await Delay(600);
    }

    /// <summary>A small Mac library: one song, a picture and its media item, written as the Mac writes them.</summary>
    private (string PictureHash, string PictureId) MakeMacLibrary(string folder)
    {
        foreach (var kind in MacKind.All) Directory.CreateDirectory(Path.Combine(folder, kind.DirectoryName));
        Directory.CreateDirectory(Path.Combine(folder, "blobs"));
        var picture = StarterPicture();
        var hash = Convert.ToHexStringLower(SHA256.HashData(picture));
        File.WriteAllBytes(Path.Combine(folder, "blobs", hash + ".png"), picture);
        const string pictureId = "6E0C9B1A-SELFTEST-MAC-PICTURE";
        var media = new MediaItem { Id = pictureId, Name = "Mac picture", MediaKind = MediaKind.Image, Classification = MediaClassification.Background, FileHash = hash, FileName = "Mac picture.png" };
        WriteMacDocument(folder, MacKind.ForWindowsType(typeof(MediaItem))!, media);
        WriteMacDocument(folder, MacKind.ForWindowsType(typeof(Presentation))!, MacSong(MacSongId, "Great Is Thy Faithfulness (from the Mac)"));
        return (hash, pictureId);
    }

    private Presentation MacSong(string id, string name)
    {
        string[] lines =
        [
            "Great is Thy faithfulness, O God my Father\nThere is no shadow of turning with Thee",
            "Thou changest not, Thy compassions they fail not\nAs Thou hast been Thou forever wilt be",
            "Pardon for sin and a peace that endureth\nThine own dear presence to cheer and to guide",
        ];
        return new Presentation
        {
            Id = id, Name = name, PresentationKind = PresentationKind.Song,
            ThemeId = model.Library.All<Theme>().FirstOrDefault()?.Id ?? "",
            Sections = [new PresentationSection { Id = "verse", Name = "Verse 1" }],
            Slides = lines.Select((text, i) => new Slide
            {
                Id = $"{id}-slide-{i + 1}", Name = $"Slide {i + 1}", SectionId = "verse",
                Objects = [new SlideObject { Id = $"{id}-lyric-{i + 1}", ObjectKind = SlideObjectKind.Text, Name = "Lyrics", Text = text }],
            }).ToList(),
        };
    }

    /// <summary>
    /// Writes a document as the Mac's TypedDocument does (every field in one change named "create",
    /// under a random actor, not a Windows one), then <paramref name="also"/> as a second change.
    /// </summary>
    private static string WriteMacDocument(string folder, MacKind kind, object model, Action<AmEdit>? also = null)
    {
        using var document = AmDocument.Create();
        document.Apply(MacEncoder.Shared.NewDocument(MacDocuments.ToJson(model), kind.RecordName), "create", DateTimeOffset.UtcNow);
        if (also is not null)
        {
            var edit = new AmEdit();
            also(edit);
            document.Apply(edit, null, DateTimeOffset.UtcNow);
        }
        var path = Path.Combine(folder, kind.DirectoryName, ((ILibraryDocument)model).Id + ".automerge");
        File.WriteAllBytes(path, document.Save());
        return path;
    }

    /// <summary>The Mac edits its copy of the self-test song: open, change, save (merging the file first, as DocumentStore.save does).</summary>
    private static void MacEdits(string folder, Action<Presentation> change)
    {
        var kind = MacKind.ForWindowsType(typeof(Presentation))!;
        var path = Path.Combine(folder, kind.DirectoryName, MacSongId + ".automerge");
        using var document = AmDocument.Load(File.ReadAllBytes(path));
        var song = (Presentation)MacDocuments.ToModel(MacDecoder.Shared.Decode(document.Read(), kind.RecordName).Value!, kind);
        change(song);
        document.Actor = Guid.NewGuid().ToByteArray();
        document.Apply(DocumentDiff.Shared.Edit(document.Read(), MacDocuments.ToJson(song), kind.RecordName), null, DateTimeOffset.UtcNow);
        using (var disk = AmDocument.Load(File.ReadAllBytes(path))) document.Merge(disk);
        File.WriteAllBytes(path, document.Save());
    }

    /// <summary>A document in the Mac folder, read with the Mac's rules (null if the Mac couldn't read it).</summary>
    private static Presentation? MacReads(string folder, MacKind kind, string id)
    {
        var path = Path.Combine(folder, kind.DirectoryName, id + ".automerge");
        if (!File.Exists(path)) return null;
        using var document = AmDocument.Load(File.ReadAllBytes(path));
        return MacDecoder.Shared.Decode(document.Read(), kind.RecordName).Value is { } value ? (Presentation)MacDocuments.ToModel(value, kind) : null;
    }

    /// <summary>Settings › Library sync on its own, for its pictures (the full Settings window is a long scroll).</summary>
    private Window SettingsPage()
    {
        var page = new Window
        {
            Title = "Settings › Library sync", Width = 820, Height = 760, Owner = window,
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("BasePlane"),
            Content = new ScrollViewer { Content = new LibrarySyncSection { Margin = new Thickness(22, 4, 22, 22) } },
        };
        page.Show();
        return page;
    }

    private static T? Find<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match && match.Name == name) return match;
            if (Find<T>(child, name) is { } found) return found;
        }
        return null;
    }

    /// <summary>A small picture for the Mac's media: a 64×36 PNG.</summary>
    private static byte[] StarterPicture()
    {
        var pixels = new byte[64 * 36 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 180;
            pixels[i + 1] = 90;
            pixels[i + 2] = 40;
            pixels[i + 3] = 255;
        }
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(64, 36, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, 64 * 4);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
