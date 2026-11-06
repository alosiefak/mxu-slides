using System.IO.Compression;
using System.Text.Json.Nodes;

namespace MxU.Slides.Core.MotionTemplates;

/// <summary>
/// Reading Lottie files from disk into one self-contained animation: the pictures it draws are
/// put inside it (as <c>data:</c> URIs), so the library keeps a single piece of JSON per
/// template and nothing can go missing later.
/// <list type="bullet">
/// <item><b>.json</b>: what Bodymovin exports. Pictures are usually inside already; if they sit in
/// an "images" folder beside the file, they're read from there.</item>
/// <item><b>.lottie</b>: LottieFiles' package, a zip holding a <c>manifest.json</c>, the animation
/// (<c>animations/</c> in version 1, <c>a/</c> in version 2) and its pictures (<c>images/</c> or
/// <c>i/</c>). Only those entries are read, straight from the zip and with size limits, so a
/// hostile file can't fill the disk or reach outside its folder.</item>
/// </list>
/// Windows only: the Mac has no Lottie support.
/// </summary>
public static class LottieFiles
{
    public static readonly IReadOnlyList<string> Extensions = [".json", ".lottie"];

    /// <summary>Most entries a .lottie may hold, and most bytes it may unpack to.</summary>
    public const int MaxEntries = 2000;

    public const long MaxUnpacked = 256L << 20;

    public static bool IsLottieFile(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>
    /// Reads <paramref name="path"/> (a .json or .lottie) into one self-contained animation, and a
    /// name for it: the composition's own, unless that's After Effects' default ("Comp 1"), when
    /// the file's name reads better.
    /// </summary>
    /// <exception cref="LottieFormatException">It isn't a usable Lottie animation (the message says why).</exception>
    /// <exception cref="IOException">It can't be read.</exception>
    public static (LottieDocument Document, string Name) Read(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        LottieDocument document;
        if (Path.GetExtension(path).Equals(".lottie", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = File.OpenRead(path);
            document = ReadPackage(stream);
        }
        else
        {
            document = LottieDocument.Parse(File.ReadAllBytes(path));
            var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
            document = Embedded(document, relative => SafeFile(folder, relative));
        }
        return (document, NameFor(document, fileName));
    }

    /// <summary>A name for a template: its composition's name, unless it's a default like "Comp 1" or empty.</summary>
    public static string NameFor(LottieDocument document, string fileName)
    {
        var name = document.Name.Trim();
        var isDefault = name.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(name, @"^(Comp|Composition|Main|Untitled)\s*\d*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return isDefault ? fileName : name;
    }

    /// <summary>Reads a .lottie package (a zip) into its first (or active) animation, with its pictures inside.</summary>
    public static LottieDocument ReadPackage(Stream zip)
    {
        ZipArchive archive;
        try { archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException e) { throw new LottieFormatException("It isn't a .lottie package (it isn't a zip file).", e); }
        using (archive)
        {
            if (archive.Entries.Count > MaxEntries) throw new LottieFormatException($"The package holds more than {MaxEntries} files.");
            var entries = archive.Entries.Where(e => !e.FullName.EndsWith('/'))
                .GroupBy(e => e.FullName.Replace('\\', '/').TrimStart('/'), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            long budget = MaxUnpacked;

            byte[]? Load(string name)
            {
                if (!entries.TryGetValue(name.Replace('\\', '/').TrimStart('/'), out var entry)) return null;
                if (entry.Length > budget) throw new LottieFormatException("The package unpacks to more than it should; it may be damaged.");
                using var source = entry.Open();
                using var copy = new MemoryStream((int)Math.Min(entry.Length, int.MaxValue));
                // Read no more than the entry claims (plus one byte to catch a lie), whatever its header says.
                var buffer = new byte[81_920];
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    budget -= read;
                    if (budget < 0 || copy.Length + read > entry.Length + 1) throw new LottieFormatException("The package unpacks to more than it should; it may be damaged.");
                    copy.Write(buffer, 0, read);
                }
                return copy.ToArray();
            }

            var animation = PickAnimation(Load("manifest.json"), entries.Keys);
            var json = animation is null ? null : Load(animation);
            if (json is null) throw new LottieFormatException("The package has no animation in it.");
            var document = LottieDocument.Parse(json);
            return Embedded(document, relative =>
                Load(relative) ?? Load("images/" + Path.GetFileName(relative)) ?? Load("i/" + Path.GetFileName(relative)));
        }
    }

    /// <summary>The animation entry a package's manifest names first (or its active one), else the first animation file in it.</summary>
    private static string? PickAnimation(byte[]? manifest, IEnumerable<string> names)
    {
        var animations = names.Where(n => (n.StartsWith("animations/", StringComparison.OrdinalIgnoreCase) || n.StartsWith("a/", StringComparison.OrdinalIgnoreCase))
                                          && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToList();
        if (manifest is not null)
        {
            try
            {
                if (JsonNode.Parse(manifest) is JsonObject root)
                {
                    var wanted = LottieDocument.Text(root["activeAnimationId"])
                                 ?? LottieDocument.Text(root["initial"]?["animation"])
                                 ?? (root["animations"] as JsonArray)?.OfType<JsonObject>().Select(a => LottieDocument.Text(a["id"])).FirstOrDefault(id => id is not null);
                    if (wanted is not null && animations.FirstOrDefault(a => Path.GetFileNameWithoutExtension(a).Equals(wanted, StringComparison.OrdinalIgnoreCase)) is { } named)
                        return named;
                }
            }
            catch (System.Text.Json.JsonException) { }
        }
        return animations.FirstOrDefault();
    }

    /// <summary>
    /// A copy with every picture that's kept outside the animation put inside it. <paramref name="load"/>
    /// reads a picture by its relative path ("images/img_0.png"), or gives null when it isn't there;
    /// a missing picture is left as it was (the animation draws without it).
    /// </summary>
    public static LottieDocument Embedded(LottieDocument document, Func<string, byte[]?> load)
    {
        var copy = document.Clone();
        var changed = false;
        foreach (var asset in copy.ImageAssets())
        {
            var p = LottieDocument.Text(asset["p"]) ?? "";
            if (p.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || p.Length == 0) continue;
            var relative = ((LottieDocument.Text(asset["u"]) ?? "") + p).Replace('\\', '/').TrimStart('/');
            if (!IsSafeRelative(relative) || load(relative) is not { } bytes) continue;
            asset["u"] = "";
            asset["p"] = DataUri(bytes, relative);
            asset["e"] = 1;
            changed = true;
        }
        return changed ? LottieDocument.Parse(copy.ToUtf8()) : document;
    }

    /// <summary>A <c>data:</c> URI for a picture or font, typed by its file name.</summary>
    public static string DataUri(byte[] bytes, string fileName)
    {
        var type = Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".ttf" => "font/ttf",
            ".otf" => "font/otf",
            _ => "image/png",
        };
        return $"data:{type};base64,{Convert.ToBase64String(bytes)}";
    }

    private static bool IsSafeRelative(string path) =>
        path.Length > 0 && !Path.IsPathRooted(path) && !path.Contains(':') && path.Split('/').All(part => part is not ".." and not "");

    /// <summary>A file inside <paramref name="folder"/> (never outside it), or null.</summary>
    private static byte[]? SafeFile(string folder, string relative)
    {
        if (!IsSafeRelative(relative)) return null;
        var full = Path.GetFullPath(Path.Combine(folder, relative));
        if (!full.StartsWith(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
        try { return File.Exists(full) && new FileInfo(full).Length <= LottieDocument.MaxBytes ? File.ReadAllBytes(full) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
