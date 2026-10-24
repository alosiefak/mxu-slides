namespace MxU.Slides.Core.Library;

/// <summary>
/// The library's media files, each named by the SHA-256 of its contents ("&lt;hash&gt;.mp4"), so
/// the same file imported twice is stored once. Port of the Mac's BlobStore.
/// <para>
/// Finding a file lists the folder once and remembers the listing until the folder changes
/// (its modified time moves on), so a hundred tiles asking "is my file here?" cost one
/// listing, not a hundred disk checks. A file the listing has is checked on disk before it's
/// trusted, in case someone deleted it. A file the listing hasn't got is only taken as missing
/// once the folder has been quiet for a moment: Windows stamps the folder from a clock that
/// ticks every few milliseconds, so a file landing in the same tick as the listing would leave
/// the stamp unchanged (see <see cref="QuietWindow"/>). Thread-safe.
/// </para>
/// </summary>
public sealed class BlobStore(string folder)
{
    /// <summary>How long after the folder last changed a listing still might have missed a file.</summary>
    public static readonly TimeSpan QuietWindow = TimeSpan.FromSeconds(2);

    /// <summary>The least time between two listings made only because the folder had just changed.</summary>
    private static readonly TimeSpan RelistGap = TimeSpan.FromMilliseconds(50);

    private readonly Lock gate = new();
    private Dictionary<string, string>? byStem;
    private readonly HashSet<string> absent = new(StringComparer.Ordinal);
    private DateTime? listedStamp;
    private DateTime listedAt;
    private int listings;

    public string Folder { get; } = folder;

    /// <summary>How many times the folder has been listed (the tests check the cache works).</summary>
    internal int DirectoryListings
    {
        get { lock (gate) return listings; }
    }

    /// <summary>The stored file for <paramref name="hash"/>, or null when it isn't on this PC.</summary>
    public string? Find(string? hash)
    {
        if (string.IsNullOrEmpty(hash)) return null;
        lock (gate)
        {
            ListIfStale();
            return Verified(hash, mayRelist: true);
        }
    }

    /// <summary>Which of <paramref name="hashes"/> have their file here; one listing answers the whole lot.</summary>
    public IReadOnlySet<string> Presence(IEnumerable<string> hashes)
    {
        lock (gate)
        {
            ListIfStale();
            var present = new HashSet<string>(StringComparer.Ordinal);
            var relisted = false;
            foreach (var hash in hashes)
            {
                if (string.IsNullOrEmpty(hash)) continue;
                // At most one extra listing for the whole pass, however many files are missing.
                if (Verified(hash, mayRelist: !relisted) is not null) present.Add(hash);
                else if (!Settled()) relisted = true;
            }
            return present;
        }
    }

    /// <summary>Copies a file in (unless it's already here); returns its hash.</summary>
    public string Store(string sourcePath) => Store(sourcePath, LibraryStore.HashFile(sourcePath));

    /// <summary>Copies a file whose hash is already known.</summary>
    public string Store(string sourcePath, string hash)
    {
        var destination = PathFor(hash, Path.GetExtension(sourcePath));
        if (!File.Exists(destination))
        {
            Directory.CreateDirectory(Folder);
            // Copied under a temporary name first, so a half-copied file is never taken for the real one.
            var temp = destination + ".tmp";
            File.Copy(sourcePath, temp, overwrite: true);
            File.Move(temp, destination, overwrite: true);
        }
        Note(hash, destination);
        return hash;
    }

    /// <summary>Moves a file in (a transcode's output, a finished recording); deletes it if an identical file is already here.</summary>
    public string Adopt(string sourcePath)
    {
        var hash = LibraryStore.HashFile(sourcePath);
        var destination = PathFor(hash, Path.GetExtension(sourcePath));
        Directory.CreateDirectory(Folder);
        if (!File.Exists(destination)) File.Move(sourcePath, destination);
        else File.Delete(sourcePath);
        Note(hash, destination);
        return hash;
    }

    /// <summary>Deletes a stored file; false if there was none.</summary>
    public bool Remove(string hash)
    {
        if (Find(hash) is not { } stored) return false;
        File.Delete(stored);
        NoteDirectoryChanged();
        return true;
    }

    /// <summary>The hash of every stored file, sorted.</summary>
    public IReadOnlyList<string> AllHashes() =>
        [.. Listing().Keys.Order(StringComparer.Ordinal)];

    /// <summary>Forgets the listing, so the next question lists the folder again.</summary>
    public void NoteDirectoryChanged()
    {
        lock (gate)
        {
            byStem = null;
            absent.Clear();
            listedStamp = null;
        }
    }

    private string PathFor(string hash, string extension) =>
        Path.Combine(Folder, extension.Length <= 1 ? hash : hash + extension.ToLowerInvariant());

    private void Note(string hash, string path)
    {
        lock (gate)
        {
            if (byStem is not null) byStem[hash] = path;
            absent.Remove(hash);
        }
    }

    private string? Verified(string hash, bool mayRelist)
    {
        var found = Lookup(hash);
        if (found is not null && !File.Exists(found))
        {
            // Deleted behind our back: the listing is out of date.
            List();
            found = Lookup(hash);
        }
        else if (found is null && mayRelist && !Settled() && DateTime.UtcNow - listedAt >= RelistGap)
        {
            List();
            found = Lookup(hash);
        }
        return found;
    }

    private string? Lookup(string hash)
    {
        if (absent.Contains(hash)) return null;
        string? found = null;
        if (byStem is { } listing && !listing.TryGetValue(hash, out found))
            // As on the Mac, a file whose name starts with the hash also counts.
            found = listing.FirstOrDefault(p => p.Key.StartsWith(hash, StringComparison.Ordinal)).Value;
        if (found is null) absent.Add(hash);
        return found;
    }

    /// <summary>True once the listing was made well after the folder last changed, so a miss can be believed.</summary>
    private bool Settled() => listedStamp is { } stamp && listedAt - stamp >= QuietWindow;

    private void ListIfStale()
    {
        if (byStem is null || Stamp() != listedStamp) List();
    }

    private void List()
    {
        listedStamp = Stamp();
        listedAt = DateTime.UtcNow;
        byStem = Listing();
        absent.Clear();
        listings++;
    }

    private Dictionary<string, string> Listing()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(Folder)) return map;
        try
        {
            foreach (var file in Directory.EnumerateFiles(Folder))
            {
                if (IsWorkFile(file)) continue;
                map.TryAdd(Path.GetFileNameWithoutExtension(file), file);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // An unreadable folder answers "not here"; the next change lists it again.
        }
        return map;
    }

    /// <summary>Files that live beside the blobs but aren't media: half-written copies and waveform caches.</summary>
    private static bool IsWorkFile(string file)
    {
        var extension = Path.GetExtension(file);
        return extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".partial", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".peaks", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(file).Contains(".partial.", StringComparison.OrdinalIgnoreCase);
    }

    private DateTime? Stamp()
    {
        try
        {
            return Directory.Exists(Folder) ? Directory.GetLastWriteTimeUtc(Folder) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
