using System.ComponentModel;
using System.Windows.Threading;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Core.Stage;
using MxU.Slides.LocalApi;
using MxU.Slides.LocalApi.Remote;
using MxU.Slides.Windows.Rendering;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// The browser remote's view of the app (Local API, /remote): the service with every row's slides,
/// slide thumbnails, the saved alerts and the stage message, plus pairing a phone from Settings.
/// Like the rest of the bridge, the server calls these on the UI thread. Windows only.
/// </summary>
public sealed partial class MainViewModel : IRemoteBridge
{
    // Recent thumbnails, by what they show and their size, so a phone scrolling back and forth costs nothing.
    private readonly Dictionary<string, byte[]> remoteThumbnails = new(StringComparer.Ordinal);
    private readonly Queue<string> remoteThumbnailOrder = new();
    private const int RemoteThumbnailsKept = 240;

    private bool remoteHooked;
    private RemotePairing? pairingHooked;

    /// <summary>The address a phone opens, e.g. http://192.168.1.20:6980/remote.</summary>
    public string RemoteAddress => $"{ApiAddress}/remote";

    /// <summary>The stage view's address.</summary>
    public string StageViewAddress => $"{ApiAddress}/remote/stage";

    public bool RemoteAvailable => api?.IsRunning == true;

    /// <summary>A phone paired (raised on the UI thread with the new key's record; the Settings section says so).</summary>
    public event Action<ApiToken>? RemotePaired;

    /// <summary>The code on offer, while there is one.</summary>
    public RemotePairingCode? RemotePairingCode => api?.Pairing.Current;

    /// <summary>Offers a 6-digit code a phone can trade for a key with this scope; null when the Local API is off.</summary>
    public RemotePairingCode? BeginRemotePairing(ApiScope scope)
    {
        if (api is not { IsRunning: true } server) return null;
        if (!ReferenceEquals(pairingHooked, server.Pairing))
        {
            pairingHooked = server.Pairing;
            // Redeemed on a server thread; the key list and the Settings section belong to the UI thread.
            server.Pairing.Paired += token => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => RemotePaired?.Invoke(token));
        }
        return server.Pairing.Begin(scope);
    }

    public void CancelRemotePairing() => api?.Pairing.Cancel();

    /// <summary>The first time a remote asks: tell remotes when the operator picks another service (that isn't a show change).</summary>
    private void EnsureRemoteHooks()
    {
        if (remoteHooked) return;
        remoteHooked = true;
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SelectedService)) PublishApi(LocalApi.ApiTopic.Show);
        };
    }

    // MARK: The service

    RemoteServiceDocument IRemoteBridge.RemoteService()
    {
        EnsureRemoteHooks();
        var themes = new Dictionary<string, string>(StringComparer.Ordinal);
        ApiServiceSnapshot? snapshot = null;
        var service = SelectedService;
        if (service is not null)
        {
            var items = new List<ApiServiceSnapshot.Item>();
            // Hidden rows aren't part of the run of show, so the remote doesn't offer them.
            foreach (var item in service.Items.Where(i => !i.IsHidden()))
            {
                ApiServiceSnapshot.PresentationRef? presentation = null;
                ApiServiceSnapshot.MediaRef? media = null;
                if (item.ItemKind == ServiceItemKind.Presentation && Library.Get<Presentation>(item.RefId) is { } p)
                    presentation = RemotePresentation(p, item.ArrangementId ?? p.DefaultArrangementId, themes);
                else if (item.ItemKind == ServiceItemKind.Media && Library.Get<MediaItem>(item.RefId) is { } m)
                    media = new ApiServiceSnapshot.MediaRef(m.Id, m.Name, m.DurationSeconds is { } d ? (int)Math.Round(d) : null, null);
                items.Add(new ApiServiceSnapshot.Item(item.Id, item.Name, ServiceItemKindJsonConverter.ToRaw(item.ItemKind), item.ColorHex, presentation, media));
            }
            snapshot = new ApiServiceSnapshot(new ApiServiceRef(service.Id, service.Name), items);
        }

        // What's live (or, with nothing live, open in the slide grid) when it isn't one of the service's rows.
        ApiServiceSnapshot.PresentationRef? other = null;
        var liveOutside = Live is { } live && (live.ItemId is null || service?.Items.Any(i => i.Id == live.ItemId) != true);
        if (liveOutside && Library.Get<Presentation>(Live!.PresentationId) is { } livePresentation)
            other = RemotePresentation(livePresentation, Live.ArrangementId, themes);
        else if (Live is null && CurrentPresentation is { } open && CurrentContextItemId is null)
            other = RemotePresentation(open, SelectedArrangement?.Id, themes);
        return new RemoteServiceDocument(snapshot, other);
    }

    private ApiServiceSnapshot.PresentationRef RemotePresentation(Presentation p, string? arrangementId, Dictionary<string, string> themes)
    {
        var canvas = SlideSceneBuilder.CanvasSizeFor(p);
        var size = new ApiServiceSnapshot.Size((int)canvas.Width, (int)canvas.Height);
        var slides = SlideSceneBuilder.ArrangedSlides(p, arrangementId).Select((slide, index) => new ApiServiceSnapshot.SlideRef(
            slide.Id, index, string.IsNullOrWhiteSpace(slide.Name) ? null : slide.Name, GroupNames.For(slide, p), GroupNames.ColorFor(slide, p),
            ConfidenceSceneBuilder.SlideText(slide, p).Body is { Length: > 0 } text ? text : null,
            ThumbnailChecksum(p, slide, themes), size)).ToList();
        var arrangement = p.Arrangements?.FirstOrDefault(a => a.Id == arrangementId);
        return new ApiServiceSnapshot.PresentationRef(p.Id, p.Name, arrangement?.Id, arrangement?.Name, slides);
    }

    /// <summary>
    /// A short fingerprint of everything a slide's picture depends on (the slide, the deck's
    /// background, its theme), so a phone fetches a thumbnail again only when it would look different.
    /// </summary>
    private string ThumbnailChecksum(Presentation p, Slide slide, Dictionary<string, string> themes)
    {
        var themeId = p.ThemeIdFor(slide) ?? "";
        if (!themes.TryGetValue(themeId, out var theme)) themes[themeId] = theme = WindowsExtras.Fingerprint(Library.Get<Theme>(themeId));
        return WindowsExtras.Fingerprint(new { slide, p.Background, p.BackgroundFill, p.CanvasWidth, p.CanvasHeight, theme });
    }

    async Task<byte[]?> IRemoteBridge.SlideThumbnailAsync(string presentationId, string slideId, int width)
    {
        if (Library.Get<Presentation>(presentationId) is not { } p || p.Slides.FirstOrDefault(s => s.Id == slideId) is not { } slide) return null;
        var key = $"{ThumbnailChecksum(p, slide, [])}@{width}";
        if (remoteThumbnails.TryGetValue(key, out var cached)) return cached;
        // The show comes first: draw only once the UI thread has nothing more urgent (input, frames).
        await Dispatcher.Yield(DispatcherPriority.Background);
        var scene = SlideSceneBuilder.Scene(slide, ThemeFor(slide, p), p, null, pager: TextLayout.Pager);
        var png = await ScenePictures.PngAsync(scene, width, Media);
        remoteThumbnails[key] = png;
        remoteThumbnailOrder.Enqueue(key);
        while (remoteThumbnailOrder.Count > RemoteThumbnailsKept) remoteThumbnails.Remove(remoteThumbnailOrder.Dequeue());
        return png;
    }

    // MARK: Messages

    IReadOnlyList<RemoteAlert> IRemoteBridge.RemoteAlerts()
    {
        var timerNames = Timers.Timers.Select(t => t.Name).ToList();
        return [.. Library.All<AlertPreset>()
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(a => new RemoteAlert(a.Id, a.Name, a.Message, AlertEditing.FillInTokens(a.Message, timerNames)))];
    }

    /// <summary>As the Stage panel's Send: steady, on the confidence monitors, under the stage message's own id.</summary>
    void IRemoteBridge.SetStageMessage(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            if (Show.LiveAlert?.Id == StageMessageId) ClearAlertsCommand.Execute(null);
            return;
        }
        FireAlert(StageMessageId, text.Trim(), AlertBehavior.Persist, AlertTarget.Confidence);
    }
}
