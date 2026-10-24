using MxU.Slides.Core.Media;
using MxU.Slides.Core.Outputs;
using MxU.Slides.VideoIO.Common;
using MxU.Slides.VideoIO.DeckLink;
using MxU.Slides.VideoIO.Ndi;
using MxU.Slides.Windows.Infrastructure;

namespace MxU.Slides.Windows.Outputs;

/// <summary>
/// Makes the sink for a screen carried by NDI or a DeckLink card (the OutputManager calls
/// this when a screen is assigned one). NDI comes from the installed NDI Tools runtime and
/// DeckLink from Blackmagic's Desktop Video driver; without them the output reports why and
/// nothing else changes.
/// </summary>
public static class VideoOutputs
{
    /// <summary>Points NDI at its config folder and sends the video I/O kit's diagnostics to the app log. Call once at start-up.</summary>
    public static void Initialize()
    {
        NdiRuntimeConfig.Adopt();
        VideoIOLog.Noted += (category, detail) => Log.Info($"{category}: {detail}");
    }

    public static IProgramSink? Create(string deviceId, OutputConfiguration config)
    {
        try
        {
            switch (OutputDevice.Kind(deviceId))
            {
                case OutputDeviceKind.Ndi:
                    var ndi = new NdiOutput(OutputDevice.Value(deviceId));
                    ndi.StatusChanged += status => Log.Info($"NDI \"{OutputDevice.Value(deviceId)}\": {status.State} {status.Detail}");
                    ndi.Start();
                    return ndi;
                case OutputDeviceKind.DeckLink:
                    if (!long.TryParse(OutputDevice.Value(deviceId), out var persistentId)) return null;
                    var card = DeckLinkRuntime.Devices().FirstOrDefault(d => d.PersistentId == persistentId);
                    if (card is null)
                    {
                        Log.Warn($"DeckLink card {persistentId} isn't connected (or Desktop Video isn't installed)");
                        return null;
                    }
                    var carried = config.CarriedSlices(deviceId);
                    var screen = carried.Count > 0 ? config.Screen(carried[0].ScreenId) : null;
                    var controller = new DeckLinkOutputController();
                    controller.StatusChanged += status => Log.Info($"DeckLink {card.Name}: {status.State} {status.Detail}");
                    controller.Start(new DeckLinkOutputRequest(deviceId, screen?.Name ?? card.Name, persistentId, card.Name,
                        DeckLinkKeying.Off, screen?.Width ?? 1920, screen?.Height ?? 1080, screen?.FramesPerSecond ?? 30));
                    return controller;
                default:
                    return null;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Couldn't start the output on {deviceId}", ex);
            return null;
        }
    }
}
