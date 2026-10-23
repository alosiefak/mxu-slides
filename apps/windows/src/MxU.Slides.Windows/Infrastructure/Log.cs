using System.Collections.Concurrent;
using System.Text;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>
/// A small, thread-safe file log (one file per day, two weeks kept). Logging
/// must never take the show down, so every failure here is swallowed.
/// </summary>
public static class Log
{
    private static readonly BlockingCollection<string> Queue = new(new ConcurrentQueue<string>(), 10_000);
    private static readonly Thread Writer;

    static Log()
    {
        Writer = new Thread(Drain) { IsBackground = true, Name = "MxU log writer" };
        Writer.Start();
    }

    public static string CurrentFile => Path.Combine(AppPaths.LogDirectory, $"mxu-slides-{DateTime.Now:yyyy-MM-dd}.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}\n{exception}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] [{Environment.CurrentManagedThreadId}] {message}";
        System.Diagnostics.Debug.WriteLine(line);
        Queue.TryAdd(line);
    }

    public static void Flush()
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (Queue.Count > 0 && DateTime.UtcNow < deadline) Thread.Sleep(10);
    }

    private static void Drain()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDirectory);
            Prune();
        }
        catch
        {
        }
        foreach (var line in Queue.GetConsumingEnumerable())
        {
            try
            {
                File.AppendAllText(CurrentFile, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
            }
        }
    }

    private static void Prune()
    {
        foreach (var file in Directory.EnumerateFiles(AppPaths.LogDirectory, "mxu-slides-*.log"))
        {
            if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-14)) File.Delete(file);
        }
    }
}
