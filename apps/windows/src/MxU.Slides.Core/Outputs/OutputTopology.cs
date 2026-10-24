using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Outputs;

/// <summary>
/// A connected display as the output code sees it. <see cref="Id"/> is the
/// device id outputs are assigned by (on Windows, <see cref="OutputDevice.Display"/>
/// of the GDI device name; the Mac uses the display's UUID). Port of
/// OutputEngine/DisplaySnapshot.swift without the AppKit lookups: the WPF app
/// builds these from its own display list.
/// </summary>
public sealed record DisplaySnapshot(string Id, string Name, SceneRect Frame, int MaximumFramesPerSecond = 60, bool IsMain = false);

/// <summary>
/// Decides which output windows to open, close or move when displays come and
/// go, so a survivor is never torn down because its neighbour was unplugged.
/// Pure: the caller applies the changes. Port of OutputEngine/OutputTopology.swift.
/// </summary>
public static class OutputTopology
{
    public abstract record Change
    {
        private Change() { }

        /// <summary>The display is assigned and connected but has no window yet.</summary>
        public sealed record Open(string DisplayId) : Change;

        /// <summary>The window's display was unassigned or disconnected.</summary>
        public sealed record Close(string DisplayId) : Change;

        /// <summary>The display moved or changed size (an arrangement change): move the window, don't rebuild it.</summary>
        public sealed record Reframe(string DisplayId, SceneRect Frame) : Change;
    }

    /// <summary>
    /// The changes that take the open windows to "one window on every assigned,
    /// connected display". Closes come first (in id order), then the opens and
    /// reframes (in id order). An assigned display that is not connected is simply
    /// waited for: it gets its window back when it is plugged in again.
    /// </summary>
    public static IReadOnlyList<Change> Reconcile(
        IReadOnlySet<string> assigned,
        IReadOnlyDictionary<string, SceneRect> openWindows,
        IReadOnlyList<DisplaySnapshot> connected)
    {
        var connectedById = new Dictionary<string, DisplaySnapshot>(StringComparer.Ordinal);
        foreach (var display in connected) connectedById.TryAdd(display.Id, display);

        var changes = new List<Change>();
        foreach (var id in openWindows.Keys.Order(StringComparer.Ordinal))
            if (!assigned.Contains(id) || !connectedById.ContainsKey(id))
                changes.Add(new Change.Close(id));
        foreach (var id in assigned.Order(StringComparer.Ordinal))
        {
            if (!connectedById.TryGetValue(id, out var display)) continue;
            if (openWindows.TryGetValue(id, out var windowFrame))
            {
                if (windowFrame != display.Frame) changes.Add(new Change.Reframe(id, display.Frame));
            }
            else
            {
                changes.Add(new Change.Open(id));
            }
        }
        return changes;
    }
}
