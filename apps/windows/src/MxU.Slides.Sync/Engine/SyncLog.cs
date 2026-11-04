namespace MxU.Slides.Sync.Engine;

/// <summary>Which way a log line went.</summary>
public enum SyncDirection
{
    /// <summary>Came in from the shared folder (the Mac).</summary>
    In,

    /// <summary>Went out to the shared folder.</summary>
    Out,

    /// <summary>Something worth knowing: a backup made, a choice sync made for you.</summary>
    Note,

    /// <summary>Something that needs a look.</summary>
    Problem,
}

public sealed record SyncLogEntry(DateTimeOffset At, SyncDirection Direction, string Text)
{
    public string Arrow => Direction switch
    {
        SyncDirection.In => "↓",
        SyncDirection.Out => "↑",
        SyncDirection.Problem => "!",
        _ => "·",
    };

    public override string ToString() => $"{At:yyyy-MM-dd HH:mm:ss} {Arrow} {Text}";
}

/// <summary>
/// What came in and went out: the newest lines kept for the Settings page, every line appended to
/// "Library sync/sync-log.txt" (cut back to its newer half when it passes half a megabyte).
/// Thread-safe.
/// </summary>
public sealed class SyncLog(string? file, Func<DateTimeOffset> now)
{
    private const int Kept = 300;
    private const long FileLimit = 512 * 1024;

    private readonly Lock gate = new();
    private readonly LinkedList<SyncLogEntry> entries = new();

    /// <summary>A line was added (raised on the thread that added it).</summary>
    public event Action<SyncLogEntry>? Added;

    /// <summary>The newest lines, oldest first.</summary>
    public IReadOnlyList<SyncLogEntry> Entries
    {
        get { lock (gate) return entries.ToList(); }
    }

    public SyncLogEntry Add(SyncDirection direction, string text)
    {
        var entry = new SyncLogEntry(now(), direction, text);
        lock (gate)
        {
            entries.AddLast(entry);
            while (entries.Count > Kept) entries.RemoveFirst();
            if (file is not null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    File.AppendAllText(file, entry + Environment.NewLine);
                    if (new FileInfo(file).Length > FileLimit)
                    {
                        var lines = File.ReadAllLines(file);
                        File.WriteAllLines(file, lines[(lines.Length / 2)..]);
                    }
                }
                catch (IOException)
                {
                    // The log is a convenience: a full disk mustn't stop sync.
                }
            }
        }
        Added?.Invoke(entry);
        return entry;
    }
}
