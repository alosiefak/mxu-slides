using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using MxU.Slides.Core.Threading;

namespace MxU.Slides.Core.Scripture;

/// <summary>
/// The translations the app carries (KJV, WEB, ASV), and loading them. The texts are built into
/// MxU.Slides.Core.dll as embedded resources (see the .csproj), so they travel with the app and
/// its tests with nothing to install.
/// <para>
/// <b>Lazily, and off the UI thread.</b> Nothing is read until a translation is first asked for.
/// <see cref="LoadAsync"/> and <see cref="SearchAsync"/> do the work on the catalog's own thread
/// (a <see cref="WorkQueue"/> called "Bible", below normal priority), never the UI thread and never
/// .NET's shared thread pool, so a search can't hold up the show. Their tasks finish on that
/// thread and an <c>await</c> on the UI thread carries on there. Each translation is read once:
/// about a tenth of a second and ~10 MB; its search index, built on the first search, about as much again.
/// </para>
/// Windows only.
/// </summary>
public sealed class BibleCatalog
{
    private static readonly Lazy<BibleCatalog> builtIn = new(() => FromAssembly(typeof(BibleCatalog).Assembly));

    /// <summary>The translations built into the app.</summary>
    public static BibleCatalog BuiltIn => builtIn.Value;

    private readonly Func<BibleTranslation, Stream> open;
    private readonly Lock gate = new();
    private readonly Dictionary<string, Lazy<Bible>> bibles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Lazy<BibleSearch>> indexes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lazy<WorkQueue> worker = new(() => new WorkQueue("Bible", ThreadPriority.BelowNormal));

    /// <param name="open">Opens a translation's data file (gzip'd).</param>
    public BibleCatalog(IReadOnlyList<BibleTranslation> translations, Func<BibleTranslation, Stream> open)
    {
        Translations = translations;
        this.open = open;
    }

    public IReadOnlyList<BibleTranslation> Translations { get; }

    /// <summary>The first listed (the KJV).</summary>
    public BibleTranslation Default => Translations[0];

    /// <summary>A translation by its id ("kjv") or abbreviation ("KJV"), or null.</summary>
    public BibleTranslation? Find(string? idOrAbbreviation) =>
        idOrAbbreviation is null ? null
            : Translations.FirstOrDefault(t => string.Equals(t.Id, idOrAbbreviation, StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.Abbreviation, idOrAbbreviation, StringComparison.OrdinalIgnoreCase));

    /// <summary>The translation if it's already in memory, else null (never loads).</summary>
    public Bible? Loaded(string id)
    {
        lock (gate) return bibles.TryGetValue(id, out var lazy) && lazy.IsValueCreated ? lazy.Value : null;
    }

    /// <summary>Every translation already in memory.</summary>
    public IReadOnlyList<Bible> AllLoaded() => [.. Translations.Select(t => Loaded(t.Id)).OfType<Bible>()];

    /// <summary>
    /// Reads a translation now, on the calling thread (once; later calls return the same one).
    /// The app calls <see cref="LoadAsync"/>; tests and the Bible thread call this.
    /// </summary>
    public Bible Load(string id) => Entry(bibles, id, t => () =>
    {
        using var stream = open(t);
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return Bible.Read(t, reader);
    }).Value;

    /// <summary>The translation's search index, built the first time (on the calling thread).</summary>
    public BibleSearch Index(string id) => Entry(indexes, id, t => () => BibleSearch.Build(Load(t.Id))).Value;

    /// <summary>Reads a translation on the Bible thread.</summary>
    public Task<Bible> LoadAsync(string id) => Loaded(id) is { } ready ? Task.FromResult(ready) : OnWorker(() => Load(id));

    /// <summary>Searches a translation on the Bible thread (loading it, and building its index, the first time).</summary>
    public Task<IReadOnlyList<SearchHit>> SearchAsync(string id, string query, int limit = 50, CancellationToken cancel = default) =>
        OnWorker(() =>
        {
            cancel.ThrowIfCancellationRequested();
            return Index(id).Search(query, limit, cancel);
        }, cancel);

    /// <summary>Loads a translation and builds its index in the background, so the first search is instant.</summary>
    public void Warm(string id) => worker.Value.Async(() => Index(id));

    private Task<T> OnWorker<T>(Func<T> work, CancellationToken cancel = default)
    {
        // Continuations run asynchronously: an awaiting UI method resumes on the UI thread, not on this one.
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        worker.Value.Async(() =>
        {
            if (cancel.IsCancellationRequested)
            {
                done.TrySetCanceled(cancel);
                return;
            }
            try { done.TrySetResult(work()); }
            catch (OperationCanceledException) { done.TrySetCanceled(cancel); }
            catch (Exception error) { done.TrySetException(error); }
        });
        return done.Task;
    }

    // One Lazy per translation, so two threads asking at once read it once.
    private Lazy<T> Entry<T>(Dictionary<string, Lazy<T>> table, string id, Func<BibleTranslation, Func<T>> make)
    {
        var translation = Find(id) ?? throw new ArgumentException($"No translation “{id}”.", nameof(id));
        lock (gate)
        {
            if (!table.TryGetValue(translation.Id, out var entry))
                table[translation.Id] = entry = new Lazy<T>(make(translation), LazyThreadSafetyMode.ExecutionAndPublication);
            return entry;
        }
    }

    // MARK: Built in

    private const string ResourcePrefix = "Bibles/";

    /// <summary>The catalog built into an assembly: <c>Bibles/bibles.json</c> and one <c>Bibles/&lt;id&gt;.tsv.gz</c> each.</summary>
    public static BibleCatalog FromAssembly(Assembly assembly)
    {
        using var manifest = assembly.GetManifestResourceStream(ResourcePrefix + "bibles.json")
            ?? throw new InvalidOperationException("The Bible manifest isn't built into the app.");
        var translations = JsonSerializer.Deserialize<List<BibleTranslation>>(manifest) ?? [];
        return new BibleCatalog(translations, t => assembly.GetManifestResourceStream(ResourcePrefix + t.File)
            ?? throw new FileNotFoundException($"The {t.Abbreviation} text isn't built into the app.", t.File));
    }
}
