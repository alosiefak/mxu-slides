using MxU.Slides.Audio.Decoding;
using MxU.Slides.Audio.Timeline;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Audio.Library;

/// <summary>
/// Brings audio files into the library: copies each into the Blobs folder named by its
/// SHA-256 (so the same file is only stored once, exactly like <see cref="LibraryStore.ImportMedia"/>
/// does for pictures and video), reads its length, and records an <see cref="AudioItem"/>.
/// Also finds an item's file again. Port of the audio half of PresenterCore's MediaImporter.
/// </summary>
public sealed class AudioImporter(LibraryStore store)
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".aac", ".wav", ".wave", ".aif", ".aiff", ".aifc", ".flac", ".wma", ".alac", ".mp2", ".ac3",
    };

    /// <summary>The audio file types the importer takes (Windows decodes all of them).</summary>
    public static IEnumerable<string> SupportedExtensions => Extensions;

    public static bool IsAudioFile(string path) => Extensions.Contains(Path.GetExtension(path));

    /// <summary>The outcome for one file.</summary>
    public sealed record Result(string SourcePath, AudioItem? Item, string? SkippedReason)
    {
        public bool Imported => Item is not null;
    }

    /// <summary>
    /// Imports every audio file in <paramref name="paths"/> (folders are walked, in name
    /// order). Files that aren't audio or can't be read are reported as skipped.
    /// </summary>
    public IReadOnlyList<Result> ImportFiles(IEnumerable<string> paths, IReadOnlyDictionary<string, string>? names = null, string? folderId = null)
    {
        var results = new List<Result>();
        foreach (var path in Files(paths))
        {
            if (!IsAudioFile(path))
            {
                results.Add(new Result(path, null, "not an audio file"));
                continue;
            }
            try
            {
                var name = names is not null && names.TryGetValue(path, out var given) ? given : null;
                results.Add(new Result(path, Import(path, name, folderId), null));
            }
            catch (Exception error)
            {
                results.Add(new Result(path, null, error.Message));
            }
        }
        return results;
    }

    /// <summary>Files in <paramref name="paths"/>, folders expanded (hidden entries skipped), in name order.</summary>
    public static IEnumerable<string> Files(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (!Directory.Exists(path))
            {
                yield return path;
                continue;
            }
            var children = new DirectoryInfo(path).EnumerateFileSystemInfos()
                .Where(info => !info.Name.StartsWith('.') && !info.Attributes.HasFlag(FileAttributes.Hidden))
                .Select(info => info.FullName)
                .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal);
            foreach (var child in Files(children)) yield return child;
        }
    }

    /// <summary>
    /// Copies one audio file in and records it (the same content imported again returns
    /// the item already made for it). Null for a file type the importer doesn't take.
    /// </summary>
    /// <param name="name">The item's name (default: the file name without its extension).</param>
    public AudioItem? Import(string sourcePath, string? name = null, string? folderId = null)
    {
        if (!IsAudioFile(sourcePath)) return null;
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("The audio file is missing", sourcePath);
        var hash = LibraryStore.HashFile(sourcePath);
        var blob = Path.Combine(store.BlobDirectory, hash + Path.GetExtension(sourcePath).ToLowerInvariant());
        Directory.CreateDirectory(store.BlobDirectory);
        if (!File.Exists(blob))
        {
            var temp = blob + ".tmp";
            File.Copy(sourcePath, temp, overwrite: true);
            File.Move(temp, blob, overwrite: true);
        }
        var existing = store.All<AudioItem>().FirstOrDefault(a => a.FileHash == hash);
        if (existing is not null) return existing;
        var item = new AudioItem
        {
            Id = LibraryStore.NewId(),
            Name = name ?? Path.GetFileNameWithoutExtension(sourcePath),
            FileHash = hash,
            FileName = Path.GetFileName(sourcePath),
            Tags = [],
            Favorite = false,
            DurationSeconds = AudioFileDecoder.ProbeDuration(blob),
            FolderId = folderId,
        };
        store.Save(item);
        return item;
    }

    /// <summary>The library copy of an item's file, or null when it is missing.</summary>
    public string? FilePath(AudioItem item) => FilePath(store, item);

    public static string? FilePath(LibraryStore store, AudioItem item)
    {
        if (string.IsNullOrEmpty(item.FileHash)) return null;
        var withExtension = Path.Combine(store.BlobDirectory, item.FileHash + Path.GetExtension(item.FileName).ToLowerInvariant());
        if (File.Exists(withExtension)) return withExtension;
        if (!Directory.Exists(store.BlobDirectory)) return null;
        return Directory.EnumerateFiles(store.BlobDirectory, item.FileHash + ".*")
            .FirstOrDefault(f => !f.EndsWith(".tmp", StringComparison.Ordinal) && !f.EndsWith(".peaks", StringComparison.Ordinal));
    }
}

/// <summary>
/// Waveforms for timeline lanes, worked out once per file (by its hash) and kept as small
/// ".peaks" files in a cache folder. Thread-safe. Port of the app's WaveformStore.
/// </summary>
public sealed class WaveformCache(string folder)
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, Waveform> loaded = new(StringComparer.Ordinal);

    public string Folder { get; } = folder;

    /// <summary>The waveform for a file (reading the cache, or decoding it the first time); null if it can't be read.</summary>
    public Waveform? Get(string hash, string audioPath)
    {
        lock (gate)
            if (loaded.TryGetValue(hash, out var known)) return known;
        var wave = Read(hash, audioPath);
        if (wave is not null)
            lock (gate) loaded[hash] = wave;
        return wave;
    }

    private Waveform? Read(string hash, string audioPath)
    {
        var cached = Path.Combine(Folder, hash.Replace('/', '_') + ".peaks");
        try
        {
            if (File.Exists(cached) && Waveform.Decode(File.ReadAllBytes(cached)) is { } stored) return stored;
        }
        catch
        {
            // A damaged cache file is rebuilt below.
        }
        AudioFileDecoder decoder;
        try
        {
            using var probe = AudioFileDecoder.Open(audioPath, 48_000);
            decoder = AudioFileDecoder.Open(audioPath, probe.SourceRate);
        }
        catch
        {
            return null;
        }
        using (decoder)
        {
            decoder.Seek(0);
            var bucketer = new WaveformBucketer(decoder.SourceRate);
            var left = new float[65_536];
            var right = new float[65_536];
            int got;
            var channels = decoder.SourceChannels == 1 ? new[] { left } : new[] { left, right };
            while ((got = decoder.Read(left, right, 0, left.Length)) > 0) bucketer.Add(channels, got);
            var wave = bucketer.Finished();
            try
            {
                Directory.CreateDirectory(Folder);
                LibraryStore.AtomicWrite(cached, wave.Encoded());
            }
            catch
            {
                // The waveform still draws; it just isn't cached.
            }
            return wave;
        }
    }
}
