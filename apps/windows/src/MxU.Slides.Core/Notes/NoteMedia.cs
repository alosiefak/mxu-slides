using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Notes;

/// <summary>
/// The pictures and videos inside a note: which blocks they are, the key each is known by, which
/// still need bringing into the library, and how picture slides made before the import get their
/// media once it lands. Port of NoteMedia.swift.
/// </summary>
public static class NoteMedia
{
    /// <summary>Every picture and video in the note, inside quotes and lists too.</summary>
    public static List<NoteBlock> Blocks(NoteBody body) => body.Blocks.SelectMany(MediaBlocks).ToList();

    private static IEnumerable<NoteBlock> MediaBlocks(NoteBlock block) => block.Type switch
    {
        NoteBlockType.Image => block.Url is { } url && IsPicture(url) ? [block] : [],
        NoteBlockType.Attachment => IsMedia(block) ? [block] : [],
        NoteBlockType.Quote => (block.Blocks ?? []).SelectMany(MediaBlocks),
        NoteBlockType.List or NoteBlockType.TaskList => (block.Items ?? []).SelectMany(i => (i.Blocks ?? []).SelectMany(MediaBlocks)),
        _ => [],
    };

    private static readonly HashSet<string> FetchedSchemes = ["https", "http", "file", "data"];

    // A URL's scheme, as Foundation's URL(string:) reads it. A drive letter ("C:\…") is a path, not a scheme.
    private static readonly Regex SchemePattern = new(@"^([A-Za-z][A-Za-z0-9+.\-]+):", RegexOptions.CultureInvariant);

    private static string? Scheme(string url) => SchemePattern.Match(url) is { Success: true } m ? m.Groups[1].Value.ToLowerInvariant() : null;

    /// <summary>A picture this computer could fetch: not a mail app's icon, and not "cid:", which only Mail reads.</summary>
    private static bool IsPicture(string url)
    {
        if (url.Length == 0 || IsMailChrome(url)) return false;
        return Scheme(url) is not { } scheme || FetchedSchemes.Contains(scheme);
    }

    private static bool IsMedia(NoteBlock block)
    {
        if (string.IsNullOrEmpty(block.HexId) && string.IsNullOrEmpty(block.Url)) return false;
        return IsVideo(block) || MediaFileTypes.IsImage(block.Filename ?? "");
    }

    /// <summary>A video attachment: marked as one, or named like one (".mov", ".mp4"…).</summary>
    public static bool IsVideo(NoteBlock block) =>
        block.Type == NoteBlockType.Attachment && (block.AttachmentKind == "video" || MediaFileTypes.IsVideo(block.Filename ?? ""));

    /// <summary>What a picture or video is known by: an image's URL, or an attachment's "hex:" id (else its URL).</summary>
    public static string? Key(NoteBlock block) => block.Type switch
    {
        NoteBlockType.Image => block.Url,
        NoteBlockType.Attachment => string.IsNullOrEmpty(block.HexId) ? block.Url : "hex:" + block.HexId,
        _ => null,
    };

    /// <summary>Fetched by its URL (a picture, or an attachment with no file id).</summary>
    public static bool FetchesByUrl(NoteBlock block) => block.Type == NoteBlockType.Image || string.IsNullOrEmpty(block.HexId);

    public static string FileName(NoteBlock block)
    {
        if (block.Type == NoteBlockType.Attachment) return string.IsNullOrEmpty(block.Filename) ? "attachment" : block.Filename;
        return LastPathComponent(block.Url) is { Length: > 0 } name && name != "/" ? name : "picture";
    }

    private static string? LastPathComponent(string? url)
    {
        if (url is null) return null;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && Scheme(url) is not null)
            return Uri.UnescapeDataString(uri.AbsolutePath.TrimEnd('/').Split('/')[^1]);
        return url.Split('?')[0].TrimEnd('/', '\\').Split('/', '\\')[^1];
    }

    /// <summary>Where a picture is downloaded to: a numbered folder each, so two "image.png" never collide.</summary>
    public static string DownloadPath(NoteBlock block, int index, string folder) =>
        Path.Combine(folder, index.ToString(System.Globalization.CultureInfo.InvariantCulture), FileName(block));

    /// <summary>The note's media the library doesn't hold yet: never brought in, or brought in and since deleted.</summary>
    public static List<NoteBlock> Pending(NoteBody body, IReadOnlyList<NoteMediaImport> remembered, Func<string, bool> libraryHas)
    {
        var known = new Dictionary<string, string>();
        foreach (var entry in remembered) known[entry.Key] = entry.MediaId;
        return Blocks(body).Where(block =>
            Key(block) is { } key && (!known.TryGetValue(key, out var id) || !libraryHas(id))).ToList();
    }

    /// <summary>The pending media this computer can fetch now.</summary>
    public static List<NoteBlock> Importable(NoteBody body, IReadOnlyList<NoteMediaImport> remembered, Func<string, bool> libraryHas) =>
        Pending(body, remembered, libraryHas).Where(b => FetchesByUrl(b) && Resolve(b.Url ?? "") is not null).ToList();

    /// <summary>The Mac's library folder that new media lands in, until someone files it.</summary>
    public const string NeedsSorted = "Needs Sorted";

    /// <summary>"Also adds 1 picture and 1 video to Media › Needs Sorted." under Make Slides; null when nothing comes in.</summary>
    public static string? FooterLine(IReadOnlyList<NoteBlock> blocks)
    {
        var videos = blocks.Count(IsVideo);
        var parts = new[] { (blocks.Count - videos, "picture"), (videos, "video") }
            .Where(p => p.Item1 > 0).Select(p => $"{p.Item1} {p.Item2}{(p.Item1 == 1 ? "" : "s")}").ToList();
        if (parts.Count == 0) return null;
        // No-break spaces keep "Media › Needs Sorted" on one line.
        var folder = $"Media \u203A {NeedsSorted}".Replace(' ', '\u00A0');
        return $"Also adds {string.Join(" and ", parts)} to {folder}.";
    }

    /// <summary>The note's memory of which library item each picture became, in note order, with new landings.</summary>
    public static List<NoteMediaImport> Remembering(IReadOnlyList<NoteMediaImport> remembered, IReadOnlyList<NoteMediaImport> landed, NoteBody body)
    {
        var byKey = new Dictionary<string, NoteMediaImport>();
        foreach (var entry in remembered) byKey[entry.Key] = entry;
        foreach (var entry in landed) byKey[entry.Key] = entry;
        var seen = new HashSet<string>();
        return Blocks(body).Select(Key).OfType<string>().Where(seen.Add)
            .Select(k => byKey.GetValueOrDefault(k)).OfType<NoteMediaImport>().ToList();
    }

    /// <summary>A URL this computer can read: web, file or data. Null for anything else ("cid:").</summary>
    public static Uri? Resolve(string url) =>
        Scheme(url) is { } scheme && FetchedSchemes.Contains(scheme) && Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null;

    /// <summary>Gmail's and Google Drive's own little icons, which come along when a note is pasted from mail.</summary>
    public static bool IsMailChrome(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host.Length == 0) return false;
        var host = uri.Host.ToLowerInvariant();
        return host == "gstatic.com" || host.EndsWith(".gstatic.com", StringComparison.Ordinal) || host == "mail.google.com"
            || (host.StartsWith("drive-thirdparty.", StringComparison.Ordinal) && host.EndsWith(".googleusercontent.com", StringComparison.Ordinal));
    }

    /// <summary>A picture's size in a column: scaled down to the column's width, never up.</summary>
    public static (double Width, double Height) Fitted((double Width, double Height) size, double width)
    {
        if (size.Width <= width || size.Width <= 0 || width <= 0) return size;
        return (width, Math.Round(size.Height * width / size.Width, MidpointRounding.AwayFromZero));
    }

    private const string PendingPrefix = "note-media:";

    /// <summary>The stand-in media id a slide holds until the picture it shows is in the library.</summary>
    public static string PendingId(string key) => PendingPrefix + key;

    public static string? KeyOfPendingId(string id) => id.StartsWith(PendingPrefix, StringComparison.Ordinal) ? id[PendingPrefix.Length..] : null;

    /// <summary>
    /// The slides with their stand-in ids swapped for the library items the media became. A picture
    /// that never came in leaves the slide with a note saying so. Null when no slide was waiting.
    /// </summary>
    public static List<Slide>? Filling(IReadOnlyList<Slide> slides, IReadOnlyList<NoteMediaImport> remembered)
    {
        var ids = new Dictionary<string, string>();
        foreach (var entry in remembered) ids[entry.Key] = entry.MediaId;
        var changed = false;
        static string Missing(string key) =>
            $"{SlideMediaFacts.NeedsMediaNote} {(key.StartsWith("hex:", StringComparison.Ordinal) ? "the note's video" : key)} did not come in";
        var filled = slides.Select(original =>
        {
            var slide = Json.MxuJson.Clone(original);
            if (slide.Background is { } background && KeyOfPendingId(background.MediaId) is { } key)
            {
                changed = true;
                if (ids.TryGetValue(key, out var id))
                {
                    background.MediaId = id;
                }
                else
                {
                    slide.Background = null;
                    slide.AutoAdvance = null;
                    slide.Notes = Missing(key);
                }
            }
            slide.Objects = slide.Objects.Where(o =>
            {
                if (o.Fill?.MediaId is not { } mediaId || KeyOfPendingId(mediaId) is not { } objectKey) return true;
                changed = true;
                if (!ids.TryGetValue(objectKey, out var id))
                {
                    slide.Notes = Missing(objectKey);
                    return false;
                }
                o.Fill.MediaId = id;
                return true;
            }).ToList();
            return slide;
        }).ToList();
        return changed ? filled : null;
    }

    /// <summary>
    /// Saves a picture read out of a note file into <paramref name="folder"/>, once per content (by
    /// hash), and returns its file URL; null when it can't be written.
    /// </summary>
    public static Func<byte[], string, string?> Saver(string folder) => (data, name) =>
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(data))[..16];
        var clean = string.Concat(name.Select(c => c is '/' or ':' or '\\' || Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));
        var file = Path.Combine(folder, hash, clean.Length == 0 ? "picture" : clean);
        try
        {
            if (!File.Exists(file))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                Library.LibraryStore.AtomicWrite(file, data);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return new Uri(file).AbsoluteUri;
    };

    /// <summary>The note's pictures kept as files on this computer.</summary>
    public static List<NoteBlock> LocalFiles(NoteBody body) =>
        Blocks(body).Where(b => FetchesByUrl(b) && b.Url is { } url && Resolve(url) is { IsFile: true }).ToList();

    /// <summary>Where a video's poster frame is taken: a second in, or near the start of a short clip.</summary>
    public static double PosterTime(double duration) => duration >= 2 ? 1 : 0.1;
}

/// <summary>Picture and video file names, as the Mac's UTType conformance reads them.</summary>
public static class MediaFileTypes
{
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpg", "jpeg", "jpe", "gif", "heic", "heif", "tif", "tiff", "bmp", "webp", "svg", "ico", "icns", "jp2", "avif",
        "psd", "tga", "dng", "cr2", "nef", "arw",
    };

    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "m4v", "mov", "qt", "avi", "wmv", "webm", "mkv", "mpg", "mpeg", "mpe", "3gp", "3g2", "mts", "m2ts", "flv", "ogv", "vob",
    };

    private static string Extension(string name) => name.Contains('.') ? name.Split('.')[^1] : "";

    public static bool IsImage(string fileName) => Images.Contains(Extension(fileName));

    public static bool IsVideo(string fileName) => Videos.Contains(Extension(fileName));

    /// <summary>True for a video, false for a picture, null for anything else (NoteFileReader.Media.isVideo).</summary>
    public static bool? IsVideoFile(string fileName) => IsImage(fileName) ? false : IsVideo(fileName) ? true : null;
}
