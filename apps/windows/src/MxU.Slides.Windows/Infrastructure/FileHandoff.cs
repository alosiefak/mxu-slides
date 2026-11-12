using System.IO.Pipes;
using System.Runtime.InteropServices;
using MxU.Slides.Core.Library;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>
/// Double-clicking a .mxuslides in Explorer starts a new copy of the app with the file's path. Only
/// one copy may run per data folder (App.xaml.cs's lock), so the new one hands its files to the
/// running one through a named pipe and leaves; the running one imports them and comes to the
/// front. A pipe is Windows' local, in-memory channel between programs: nothing goes over the
/// network, and only this Windows user's programs can open it. The message's rules are Core's
/// (<see cref="OpenRequests"/>). The Mac gets the same from macOS (<c>application(_:open:)</c>).
/// </summary>
internal sealed class FileHandoff : IDisposable
{
    private readonly string pipeName;
    private readonly Action<IReadOnlyList<string>> received;
    private readonly CancellationTokenSource stop = new();
    private readonly Thread thread;

    private FileHandoff(string instanceName, Action<IReadOnlyList<string>> received)
    {
        pipeName = OpenRequests.PipeName(instanceName);
        this.received = received;
        // Its own thread, waiting on the pipe: never the shared thread pool (HOW-IT-WORKS section 26).
        thread = new Thread(Listen) { IsBackground = true, Name = "MxU file hand-off" };
        thread.Start();
    }

    /// <summary>Starts listening for files from later copies. <paramref name="received"/> is called on the listening thread.</summary>
    public static FileHandoff Listen(string instanceName, Action<IReadOnlyList<string>> received) => new(instanceName, received);

    /// <summary>
    /// From a second copy: sends <paramref name="files"/> to the running one (none at all just brings
    /// it to the front). True when it took them (this copy should then quit quietly); false when the
    /// running copy didn't answer within a few seconds.
    /// </summary>
    public static bool SendToRunningCopy(string instanceName, IReadOnlyList<string> files)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", OpenRequests.PipeName(instanceName), PipeDirection.InOut, PipeOptions.None);
            pipe.Connect(TimeSpan.FromSeconds(5));
            // Windows only lets the program the user is using bring a window to the front; this lets the running copy do it.
            AllowSetForegroundWindow(AsfwAny);
            var message = OpenRequests.Encode(files);
            pipe.Write(message);
            pipe.Flush();
            return pipe.ReadByte() == 1;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            Log.Warn($"Couldn't hand {files.Count} file(s) to the running copy: {ex.Message}");
            return false;
        }
    }

    private void Listen()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                pipe.WaitForConnectionAsync(stop.Token).GetAwaiter().GetResult();
                var files = OpenRequests.Decode(pipe);
                pipe.WriteByte(files is null ? (byte)0 : (byte)1);
                pipe.Flush();
                if (files is not null)
                {
                    if (files.Count > 0) Log.Info($"Opened from Explorer: {string.Join(", ", files.Select(Path.GetFileName))}");
                    received(files);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // A sender that went away half way: listen again.
                Log.Warn($"File hand-off: {ex.Message}");
                Thread.Sleep(200);
            }
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        thread.Join(TimeSpan.FromSeconds(1));
        stop.Dispose();
    }

    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
