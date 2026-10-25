using System.Security.Cryptography;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Threading;

namespace MxU.Slides.Sync.Engine;

/// <summary>
/// Copies media files both ways between the Windows library's Blobs folder and the shared
/// folder's blobs folder. Both apps name a media file by the SHA-256 of its contents
/// ("&lt;hash&gt;.mp4"), so a file is never changed, only added: a file one side lacks is copied
/// across, and that's all. A file arriving from the shared folder is checked against its name
/// first (a cloud client may still be downloading it); a mismatch waits for the next look. Big
/// copies run on their own queue, so documents keep syncing meanwhile, and a copy lands under a
/// temporary name before it's renamed, so a half-copied file is never taken for a whole one.
/// </summary>
public sealed class BlobMirror(WindowsLibrary windows, SharedFolder shared, SyncLog log) : IDisposable
{
    private readonly WorkQueue queue = new("Library sync media");
    private readonly CancellationTokenSource stopping = new();
    private readonly Dictionary<string, FileStamp> notYet = new(StringComparer.OrdinalIgnoreCase);
    private int requested;
    private volatile bool running;

    /// <summary>True when no copying is going on or waiting.</summary>
    public bool IsIdle => Volatile.Read(ref requested) == 0 && !running && queue.Pending == 0;

    /// <summary>Asks for a look (one at a time; asking again while one waits does nothing).</summary>
    public void Request()
    {
        if (Interlocked.Exchange(ref requested, 1) == 1) return;
        queue.Async(() =>
        {
            Volatile.Write(ref requested, 0);
            running = true;
            try { Mirror(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                log.Add(SyncDirection.Note, $"Media copying stopped for now ({error.Message}); it carries on at the next look.");
            }
            catch (OperationCanceledException)
            {
            }
            finally { running = false; }
        });
    }

    private void Mirror()
    {
        if (!shared.IsReachable()) return;
        var here = SharedFolder.ListBlobs(windows.BlobDirectory);
        var there = SharedFolder.ListBlobs(shared.BlobDirectory);
        var sent = 0;
        foreach (var (name, file) in here)
        {
            if (there.ContainsKey(name)) continue;
            stopping.Token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(shared.BlobDirectory);
            Copy(file.FullName, Path.Combine(shared.BlobDirectory, name));
            sent++;
        }
        var arrived = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, file) in there)
        {
            if (here.ContainsKey(name)) continue;
            stopping.Token.ThrowIfCancellationRequested();
            var stamp = FileStamp.Of(file);
            if (notYet.TryGetValue(name, out var last) && last == stamp) continue;
            var hash = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
            if (Hash(file.FullName) != hash)
            {
                notYet[name] = stamp;
                continue;
            }
            notYet.Remove(name);
            windows.Store.Blobs.Store(file.FullName, hash);
            arrived.Add(hash);
        }
        if (sent > 0) log.Add(SyncDirection.Out, $"Copied {SyncEngine.Count(sent, "media file")} to the shared folder");
        if (arrived.Count == 0) return;
        log.Add(SyncDirection.In, $"Copied {SyncEngine.Count(arrived.Count, "media file")} from the shared folder");
        // The tiles that said FILE MISSING can show their picture now.
        foreach (var item in windows.Store.All<MediaItem>().Where(m => arrived.Contains(m.FileHash)))
            windows.Store.NotifyChanged(typeof(MediaItem), item.Id);
        foreach (var item in windows.Store.All<AudioItem>().Where(a => arrived.Contains(a.FileHash)))
            windows.Store.NotifyChanged(typeof(AudioItem), item.Id);
    }

    private string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private void Copy(string from, string to)
    {
        var temp = $"{to}.{Guid.NewGuid():N}.mxu-sync";
        try
        {
            using (var source = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            using (var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                source.CopyTo(target, 1 << 20);
                target.Flush(flushToDisk: true);
            }
            File.Move(temp, to, overwrite: false);
        }
        catch (IOException) when (File.Exists(to))
        {
            // Someone else copied it first: same name, same contents.
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public void Dispose()
    {
        stopping.Cancel();
        queue.Dispose();
    }
}
