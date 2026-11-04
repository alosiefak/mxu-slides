namespace MxU.Slides.Windows.Infrastructure;

// Library sync with a Mac (Phase 10): which shared folder this PC syncs with. It's this PC's own
// setting (another PC may reach the same folder by a different path), so it lives here, not in
// the library.
public sealed partial class AppSettings
{
    /// <summary>The shared Mac library folder (OneDrive, Dropbox, iCloud for Windows or a network share), or null for no sync.</summary>
    public string? LibrarySyncFolder { get; set; }

    /// <summary>True when the operator paused sync (it stays paused after a restart).</summary>
    public bool LibrarySyncPaused { get; set; }
}
