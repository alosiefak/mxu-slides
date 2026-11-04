using System.Runtime.InteropServices;
using System.Windows.Media;
using Microsoft.Win32;
using MxU.Slides.Audio;
using MxU.Slides.Core.Diagnostics;
using MxU.Slides.Core.Library;
using MxU.Slides.VideoIO.Capture;
using MxU.Slides.VideoIO.DeckLink;
using MxU.Slides.VideoIO.Ndi;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>
/// What this PC is, for Diagnostics and a problem report: Windows and the app, the processor and
/// memory, the displays, the graphics card, sound devices, cameras and capture, NDI and DeckLink.
/// The Mac's twin is FeedbackController.systemInfo (os_version, mac_model, chip, memory, displays).
/// Asking Windows about devices can take a second or two (a sleeping USB interface), so
/// <see cref="GatherAsync"/> asks on a thread of its own; the graphics questions WPF answers
/// itself are asked on the UI thread first.
/// </summary>
internal static class SystemInfo
{
    public sealed record Line(string Area, string Name, string Value);

    public sealed record Report(IReadOnlyList<Line> Lines, HealthFacts Facts);

    public static async Task<Report> GatherAsync(ViewModels.MainViewModel model)
    {
        // WPF's graphics answers belong to the UI thread.
        var tier = RenderCapability.Tier >> 16;
        var shaders = RenderCapability.IsPixelShaderVersionSupported(3, 0);
        var displays = Displays.All();
        var library = model.Library;
        var counts = (Presentations: library.Count<Core.Model.Presentation>(), Services: library.Count<Core.Model.Service>(),
            Media: library.Count<Core.Model.MediaItem>(), Themes: library.Count<Core.Model.Theme>());
        var screens = model.Settings.Screens ?? [];
        // A screen (or one of its slices) set to show on a display that isn't plugged in now.
        bool Missing(string? device) => device is { Length: > 0 } id && Core.Outputs.OutputDevice.Kind(id) == Core.Outputs.OutputDeviceKind.Display
            && Displays.Find(Core.Outputs.OutputDevice.Value(id)) is null;
        var missingScreens = screens.Where(s => Missing(s.Device) || (s.Slices ?? []).Any(slice => Missing(slice.Device))).Select(s => s.Name).ToList();
        var autoBackups = model.Settings.AutoBackup != BackupFrequency.Off;
        var backupFolder = model.BackupFolder;
        var preventsSleep = model.Settings.PreventSleep;
        var loadProblems = library.LoadProblems.Count;
        var missingMedia = model.MediaItems.Count(m => m.IsFileMissing);
        var transcode = library.All<Core.Model.MediaItem>().Count(m => m.FileStatus is Core.Model.MediaFileStatus.NeedsTranscode or Core.Model.MediaFileStatus.TranscodeFailed);
        var cameras = await CameraList();

        return await Task.Factory.StartNew(() =>
        {
            var lines = new List<Line>();
            void Add(string area, string name, string value) => lines.Add(new Line(area, name, value));

            Add("App", "Version", typeof(SystemInfo).Assembly.GetName().Version?.ToString(3) ?? "?");
            Add("App", "Library", $"{counts.Presentations} presentations, {counts.Services} services, {counts.Media} media items, {counts.Themes} themes");
            Add("Windows", "Version", RuntimeInformation.OSDescription);
            Add("Windows", ".NET", RuntimeInformation.FrameworkDescription);
            Add("Processor", "Model", ProcessorName());
            Add("Processor", "Cores", Environment.ProcessorCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Add("Memory", "Installed", $"{TotalMemoryGb():0.#} GB");
            long? free = null;
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(library.Root))!);
                free = drive.AvailableFreeSpace;
                Add("Disk", "Free on the library's drive", $"{free / (1024.0 * 1024 * 1024):0.#} GB ({drive.Name})");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
            foreach (var display in displays) Add("Displays", display.IsPrimary ? "Main" : "Other", display.Label);
            Add("Graphics", "WPF drawing", tier switch { 2 => "the graphics card (tier 2)", 1 => "partly the graphics card (tier 1)", _ => "the processor only (tier 0)" });
            Add("Graphics", "Effect shaders (pixel shader 3.0)", shaders ? "yes" : "no");
            foreach (var adapter in GraphicsAdapters()) Add("Graphics", "Card", adapter);
            var outputs = AudioDeviceList.OutputDevices();
            foreach (var output in outputs) Add("Sound", "Output", $"{output.Name} ({output.ChannelCount} channels)");
            if (outputs.Count == 0) Add("Sound", "Output", "none");
            if (AudioDeviceList.DefaultOutputDevice() is { } main) Add("Sound", "Default output", main.Name);
            foreach (var input in AudioDeviceList.InputDevices()) Add("Sound", "Input", input.Name);
            foreach (var camera in cameras) Add("Capture", "Camera or capture card", camera.Name);
            if (cameras.Count == 0) Add("Capture", "Camera or capture card", "none found");
            var ndi = NdiLibrary.TryLoad(out _, out var ndiError);
            Add("NDI", "Runtime", ndi ? "installed" : ndiError?.Message ?? NdiError.NotInstalledMessage);
            var deckLink = DeckLinkRuntime.IsAvailable ? DeckLinkRuntime.Devices() : [];
            Add("DeckLink", "Cards", deckLink.Count == 0 ? "none" : string.Join(", ", deckLink.Select(d => d.Name)));

            var facts = new HealthFacts
            {
                FreeDiskBytes = free,
                LibraryLoadProblems = loadProblems,
                MissingMediaFiles = missingMedia,
                MediaNeedingTranscode = transcode,
                Displays = displays.Count,
                ScreensWithoutDisplay = missingScreens,
                RenderTier = tier,
                PixelShader3 = shaders,
                SoundOutputs = outputs.Count,
                NdiInstalled = ndi,
                DeckLinkCards = deckLink.Count,
                AutomaticBackups = autoBackups,
                LastBackup = BackupFiles.List(backupFolder).Where(e => e.Kind is BackupFiles.Kind.Automatic or BackupFiles.Kind.Saved).Select(e => (DateTimeOffset?)e.Created).FirstOrDefault(),
                ErrorsToday = LogLinesToday("[ERROR]"),
                HangsThisWeek = DiagnosticsBundle.Candidates(AppPaths.LogDirectory).Count(c => c.Name.StartsWith("hang-", StringComparison.Ordinal) && c.Modified > DateTimeOffset.Now.AddDays(-7)),
                OnBattery = OnBattery(),
                PreventsSleep = preventsSleep,
            };
            return new Report(lines, facts);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private static async Task<IReadOnlyList<CameraDevice>> CameraList()
    {
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return await CameraDevices.ListAsync(limit.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string ProcessorName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "unknown";
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return "unknown";
        }
    }

    private static double TotalMemoryGb()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) ? status.TotalPhysical / (1024.0 * 1024 * 1024) : GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024 * 1024);
    }

    /// <summary>The graphics cards, by name and their own memory, through DXGI (the part of Windows that lists them).</summary>
    private static IEnumerable<string> GraphicsAdapters()
    {
        var names = new List<string>();
        try
        {
            using var factory = Vortice.DXGI.DXGI.CreateDXGIFactory1<Vortice.DXGI.IDXGIFactory1>();
            for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
            {
                using (adapter)
                {
                    var description = adapter.Description1;
                    if ((description.Flags & Vortice.DXGI.AdapterFlags.Software) != 0) continue;
                    names.Add($"{description.Description.Trim()} ({description.DedicatedVideoMemory / (1024 * 1024)} MB of its own memory)");
                }
            }
        }
        catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException or SharpGen.Runtime.SharpGenException)
        {
            names.Add("couldn't be read: " + ex.Message);
        }
        return names;
    }

    private static int LogLinesToday(string marker)
    {
        try
        {
            return File.Exists(Log.CurrentFile) ? File.ReadLines(Log.CurrentFile).Count(line => line.Contains(marker, StringComparison.Ordinal)) : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static bool? OnBattery()
    {
        var status = new PowerStatus();
        if (!GetSystemPowerStatus(ref status)) return null;
        return status.AcLineStatus switch { 0 => true, 1 => false, _ => null };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(ref PowerStatus status);
}
