using System.Windows;
using MxU.Slides.Core.Model;
using MxU.Slides.Sync.Engine;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.Rendering;

namespace MxU.Slides.Windows.ViewModels;

// Library sync with a Mac (Phase 10): the engine (MxU.Slides.Sync, Engine/SyncEngine.cs) and what
// the Settings page (Views/LibrarySyncSection.cs) asks of it. Sync works on its own thread; this
// passes its news to the UI thread, and reopens a song that's on screen when the Mac changed it.
public sealed partial class MainViewModel
{
    /// <summary>The running sync, or null when this PC doesn't sync.</summary>
    public SyncEngine? LibrarySync { get; private set; }

    /// <summary>Sync's status, attention or log changed (raised on the UI thread).</summary>
    public event Action? LibrarySyncChanged;

    /// <summary>A song the Mac changed while it was being edited here: reopened once editing ends.</summary>
    private string? reopenAfterEditing;

    private void StartLibrarySync()
    {
        if (Settings.LibrarySyncFolder is not { Length: > 0 } folder) return;
        try
        {
            var engine = new SyncEngine(Library, folder, new SyncOptions { AppName = $"MxU Slides for Windows {typeof(MainViewModel).Assembly.GetName().Version}" });
            engine.StatusChanged += RaiseLibrarySyncChanged;
            engine.Log.Added += _ => RaiseLibrarySyncChanged();
            engine.Received += OnSyncReceived;
            engine.Start(startPaused: Settings.LibrarySyncPaused);
            LibrarySync = engine;
            if (Application.Current is { } app) app.Exit += (_, _) => engine.Dispose();
        }
        catch (Exception ex)
        {
            // Sync is extra: the show must start without it.
            Log.Error("Library sync didn't start", ex);
            Problem = $"Library sync didn't start: {ex.Message}";
        }
        RaiseLibrarySyncChanged();
    }

    /// <summary>Starts syncing with <paramref name="folder"/> (stopping any other first). Returns why it can't, or null.</summary>
    public string? ChooseLibrarySyncFolder(string folder)
    {
        if (SyncEngine.CheckFolder(folder, Library.Root) is { } problem) return problem;
        StopLibrarySync();
        Settings.LibrarySyncFolder = Path.GetFullPath(folder);
        Settings.LibrarySyncPaused = false;
        Settings.Save();
        StartLibrarySync();
        return null;
    }

    /// <summary>Stops syncing. Nothing is deleted on either side; choosing the folder again carries on where it left off.</summary>
    public void TurnOffLibrarySync()
    {
        StopLibrarySync();
        Settings.LibrarySyncFolder = null;
        Settings.Save();
        RaiseLibrarySyncChanged();
    }

    public void PauseLibrarySync(bool pause)
    {
        if (LibrarySync is not { } engine) return;
        if (pause) engine.Pause();
        else engine.Resume();
        Settings.LibrarySyncPaused = pause;
        Settings.Save();
        RaiseLibrarySyncChanged();
    }

    private void StopLibrarySync()
    {
        if (LibrarySync is not { } engine) return;
        LibrarySync = null;
        engine.StatusChanged -= RaiseLibrarySyncChanged;
        engine.Received -= OnSyncReceived;
        // Disposing waits for sync's current document; off the UI thread, so the screen doesn't stall.
        _ = Task.Run(engine.Dispose);
    }

    private int librarySyncChangePending;

    /// <summary>
    /// Tells the Settings page, on the UI thread. A first sync can log hundreds of lines in a few
    /// seconds; however many arrive, the page redraws once for them, when the UI is free.
    /// </summary>
    private void RaiseLibrarySyncChanged()
    {
        if (Application.Current?.Dispatcher is not { } dispatcher)
        {
            LibrarySyncChanged?.Invoke();
            return;
        }
        if (Interlocked.Exchange(ref librarySyncChangePending, 1) == 1) return;
        dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            Volatile.Write(ref librarySyncChangePending, 0);
            LibrarySyncChanged?.Invoke();
        });
    }

    /// <summary>The Mac changed a document and sync saved it here: refresh the open song and the live screen.</summary>
    private void OnSyncReceived(Type type, string id)
    {
        if (type != typeof(Presentation)) return;
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => OnSyncReceived(type, id));
            return;
        }
        if (Library.Get<Presentation>(id) is not { } fresh) return;
        if (CurrentPresentation?.Id == id)
        {
            if (IsEditing) reopenAfterEditing = id;
            else Open(fresh, CurrentContextItemId, SelectedArrangement?.Id);
        }
        Show.PresentationEdited(fresh);
        if (Show.Live?.Presentation?.Id == id) RenderOutputs(SceneTransition.Cut, refreshOnly: true);
        AutoAdvance.PresentationEdited(id);
    }

    /// <summary>Editing just ended: show what the Mac changed meanwhile (sync has merged it with the edit).</summary>
    private void ReopenAfterEditing()
    {
        if (IsEditing || reopenAfterEditing is not { } id) return;
        reopenAfterEditing = null;
        if (CurrentPresentation?.Id == id && Library.Get<Presentation>(id) is { } fresh)
            Open(fresh, CurrentContextItemId, SelectedArrangement?.Id);
    }

    /// <summary>Wires the reopening to the end of editing (called once, from the constructor).</summary>
    private void InitializeLibrarySync()
    {
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsEditing)) ReopenAfterEditing();
        };
        StartLibrarySync();
    }
}
