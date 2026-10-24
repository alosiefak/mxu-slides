using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MxU.Slides.Core.Scene;
using MxU.Slides.VideoIO.Capture;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.Rendering;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>A live input tile in the Media tab: a named camera, capture card or NDI source.</summary>
public sealed partial class InputTileViewModel(VideoInputInventory.Entry entry) : ObservableObject
{
    public VideoInputInventory.Entry Entry { get; } = entry;
    public string Name => Entry.Name;
    public string Kind => Entry.Kind?.ToString() ?? "Not set up";

    /// <summary>A one-item scene of the input, for the tile's moving thumbnail.</summary>
    public RenderScene Preview { get; } = Thumbnail(entry);

    [ObservableProperty]
    public partial bool IsLive { get; set; }

    private static RenderScene Thumbnail(VideoInputInventory.Entry entry)
    {
        var scene = new RenderScene(SceneSize.Hd);
        scene.AddItem(new RenderItem
        {
            Id = "input", Frame = new SceneRect(SceneSize.Hd),
            Content = new ItemContent.Media(entry.EngineId, SceneMediaScaleMode.Fit),
        }, LayerKind.VideoInput);
        return scene;
    }
}

/// <summary>
/// Live video inputs: cameras, capture cards, NDI sources and DeckLink inputs, each saved as a
/// named entry ("Pulpit camera") in <c>video-inputs.json</c>. Click one to put it on the Video
/// Input layer, under the slides. Port of the Mac's VideoInputInventory and live-input cues.
/// </summary>
public sealed partial class MainViewModel
{
    private VideoInputInventory? inputs;

    public VideoInputInventory Inputs
    {
        get
        {
            if (inputs is not null) return inputs;
            inputs = new VideoInputInventory(Path.Combine(AppPaths.DataRoot, "video-inputs.json"));
            inputs.EntriesChanged += () => System.Windows.Application.Current?.Dispatcher.BeginInvoke(RefreshInputs);
            // A slide can also name a device directly (an older Mac document): it shows through the input that uses that device.
            Media.LiveFrames = engineId => inputs.LatestFrame(NamedInputFor(engineId));
            Media.LiveInputName = engineId => inputs.EntryForEngineId(NamedInputFor(engineId))?.Name;
            Media.ScreenFrames = engineId => ScreenMirrors.LatestFrame(engineId);
            return inputs;
        }
    }

    public ObservableCollection<InputTileViewModel> InputTiles { get; } = [];

    /// <summary>Opens every assigned input so it's ready before it's needed.</summary>
    private void StartInputs()
    {
        try
        {
            Inputs.WarmUp();
            RefreshInputs();
        }
        catch (Exception ex)
        {
            Log.Error("Video inputs couldn't start", ex);
        }
    }

    public void RefreshInputs()
    {
        InputTiles.Clear();
        foreach (var entry in Inputs.Entries) InputTiles.Add(new InputTileViewModel(entry));
        MarkLiveInputs();
    }

    private void MarkLiveInputs()
    {
        foreach (var tile in InputTiles)
            tile.IsLive = Show.LiveMedia.Values.Any(cue => cue.MediaId == tile.Entry.EngineId);
    }

    /// <summary>Puts an input on the Video Input layer, or takes it off if it's already there.</summary>
    [RelayCommand]
    private void InputClicked(InputTileViewModel? tile)
    {
        if (tile is null) return;
        if (tile.IsLive) Clear(() => Show.Clear(LayerKind.VideoInput));
        else FireLiveInput(tile.Entry.Id, LayerKind.VideoInput);
        MarkLiveInputs();
    }

    /// <summary>The cue id for an inventory entry (a bare id gets the Mac's <c>input::item::</c> prefix).</summary>
    private static string LiveInputCueId(string liveInputId) =>
        MediaHub.IsLiveInput(liveInputId) ? liveInputId : VideoInputInventory.EngineIdPrefix + liveInputId;
}
