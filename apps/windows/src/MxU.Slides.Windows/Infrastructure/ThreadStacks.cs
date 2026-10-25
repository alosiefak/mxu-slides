using System.Text;
using Microsoft.Diagnostics.Runtime;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>
/// The call stacks of this app's own threads, read from a snapshot of the process with
/// Microsoft's ClrMD library. .NET can't ask another thread where it is, so when something
/// hangs (the UI thread stops answering) this is how to see what it's stuck in. The Mac's
/// twin is ThreadStackCapture.swift. Taking the snapshot pauses the app for a moment, so it's
/// only for when something has already gone wrong.
/// </summary>
internal static class ThreadStacks
{
    /// <param name="firstThreadId">This managed thread's stack comes first (the UI thread, say).</param>
    /// <param name="maxFrames">Frames shown per thread.</param>
    public static string Describe(int? firstThreadId = null, int maxFrames = 30)
    {
        try
        {
            using var target = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
            using var runtime = target.ClrVersions[0].CreateRuntime();
            var text = new StringBuilder();
            var threads = runtime.Threads.Where(t => t.IsAlive)
                .OrderBy(t => t.ManagedThreadId == firstThreadId ? 0 : 1).ThenBy(t => t.ManagedThreadId);
            foreach (var thread in threads)
            {
                var frames = thread.EnumerateStackTrace().Take(maxFrames)
                    .Select(f => f.Method?.Signature ?? f.FrameName)
                    .Where(name => !string.IsNullOrEmpty(name))
                    .ToList();
                if (frames.Count == 0) continue; // a thread with no managed code on it
                text.AppendLine($"Thread {thread.ManagedThreadId}{(thread.ManagedThreadId == firstThreadId ? " (the UI thread)" : "")}:");
                foreach (var frame in frames) text.AppendLine("    " + frame);
            }
            return text.ToString();
        }
        catch (Exception ex)
        {
            return $"(couldn't read the threads' stacks: {ex.Message})";
        }
    }
}
