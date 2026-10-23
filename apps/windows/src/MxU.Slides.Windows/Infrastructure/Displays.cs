using System.Runtime.InteropServices;
using static MxU.Slides.Windows.Infrastructure.NativeMethods;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>A connected display, in physical pixels.</summary>
public sealed record DisplayInfo(string DeviceName, string FriendlyName, int Left, int Top, int Width, int Height, bool IsPrimary, double Scale)
{
    public string Label => $"{FriendlyName} — {Width}×{Height}{(IsPrimary ? " (main)" : "")}";

    public override string ToString() => Label;
}

public static class Displays
{
    public static IReadOnlyList<DisplayInfo> All()
    {
        var names = FriendlyNames();
        var result = new List<DisplayInfo>();
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref RECT _, IntPtr _) =>
            {
                var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (GetMonitorInfo(monitor, ref info))
                {
                    var scale = GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 ? dpiX / 96.0 : 1.0;
                    var friendly = names.TryGetValue(info.szDevice, out var name) && !string.IsNullOrWhiteSpace(name)
                        ? name
                        : info.szDevice.Replace(@"\\.\", "");
                    result.Add(new DisplayInfo(info.szDevice, friendly, info.rcMonitor.Left, info.rcMonitor.Top,
                        info.rcMonitor.Width, info.rcMonitor.Height, (info.dwFlags & MONITORINFOF_PRIMARY) != 0, scale));
                }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Error("Could not enumerate displays", ex);
        }
        // Number duplicate names ("Generic PnP Monitor") so they can be told apart.
        var counts = result.GroupBy(d => d.FriendlyName).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var ordered = result.OrderByDescending(d => d.IsPrimary).ThenBy(d => d.Left).ThenBy(d => d.Top).ToList();
        var seen = new Dictionary<string, int>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var d = ordered[i];
            if (!counts.Contains(d.FriendlyName)) continue;
            seen[d.FriendlyName] = seen.GetValueOrDefault(d.FriendlyName) + 1;
            ordered[i] = d with { FriendlyName = $"{d.FriendlyName} {seen[d.FriendlyName]}" };
        }
        return ordered;
    }

    public static DisplayInfo? Find(string? deviceName) =>
        deviceName is null ? null : All().FirstOrDefault(d => d.DeviceName == deviceName);

    public static DisplayInfo? ForWindow(IntPtr hwnd)
    {
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        return GetMonitorInfo(monitor, ref info) ? All().FirstOrDefault(d => d.DeviceName == info.szDevice) : null;
    }

    private static Dictionary<string, string> FriendlyNames()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount) != 0) return names;
            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return names;
            foreach (var path in paths.Take((int)pathCount))
            {
                var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                        size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                        adapterId = path.sourceInfo.adapterId,
                        id = path.sourceInfo.id,
                    },
                };
                var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                        size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                        adapterId = path.targetInfo.adapterId,
                        id = path.targetInfo.id,
                    },
                };
                if (DisplayConfigGetDeviceInfo(ref source) == 0 && DisplayConfigGetDeviceInfo(ref target) == 0)
                    names[source.viewGdiDeviceName] = target.monitorFriendlyDeviceName;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Friendly display names unavailable: {ex.Message}");
        }
        return names;
    }
}

/// <summary>Stops the screensaver and sleep while outputs are showing.</summary>
public static class KeepAwake
{
    private static bool active;

    public static void Set(bool on)
    {
        if (on == active) return;
        active = on;
        SetThreadExecutionState(on
            ? ExecutionState.Continuous | ExecutionState.DisplayRequired | ExecutionState.SystemRequired
            : ExecutionState.Continuous);
        Log.Info(on ? "Keeping the computer awake while outputs are showing" : "Allowing sleep again");
    }
}
