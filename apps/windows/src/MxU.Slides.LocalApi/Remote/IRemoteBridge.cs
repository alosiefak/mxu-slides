namespace MxU.Slides.LocalApi.Remote;

/// <summary>
/// What the browser remote asks of the app, beyond the Mac's <see cref="IApiBridge"/>. The app's
/// bridge implements both; the server sees this one (<c>bridge is IRemoteBridge</c>) and then
/// serves /remote. Like <see cref="IApiBridge"/>, it's called on the app's UI thread.
/// Windows only: the Mac has no browser remote.
/// </summary>
public interface IRemoteBridge
{
    /// <summary>The service this computer runs, every row with its slides, and the presentation live (or open) outside it.</summary>
    RemoteServiceDocument RemoteService();

    /// <summary>A slide drawn as a PNG about <paramref name="width"/> pixels wide; null when there's no such slide.</summary>
    Task<byte[]?> SlideThumbnailAsync(string presentationId, string slideId, int width);

    /// <summary>The saved alerts, with the blanks each one asks for.</summary>
    IReadOnlyList<RemoteAlert> RemoteAlerts();

    /// <summary>The stage message: steady, on the confidence monitors, as the Stage panel sends it. Empty takes it down.</summary>
    void SetStageMessage(string? text);
}

/// <summary>
/// The remote's picture of the service. <see cref="Service"/> uses the Mac's own service snapshot
/// shape (APIServiceSnapshot), so a Mac remote could read it too. <see cref="Other"/> is the
/// presentation live (or open) in the app when it isn't one of the service's rows.
/// </summary>
public sealed record RemoteServiceDocument(ApiServiceSnapshot? Service, ApiServiceSnapshot.PresentationRef? Other);

/// <summary>A saved alert: <see cref="Blanks"/> are the {words} to fill in before sending it.</summary>
public sealed record RemoteAlert(string Id, string Name, string Message, IReadOnlyList<string> Blanks);

/// <summary>Who a key is: the name it was given and what it may do (view or control).</summary>
public sealed record RemoteSession(string Name, string Scope);

/// <summary>A phone pairing: the code shown in Settings and the name the phone gave itself.</summary>
public sealed record RemotePairRequest(string? Code = null, string? Name = null);

/// <summary>The key a pairing made. The phone keeps it (in the browser's local storage); this computer keeps only its hash.</summary>
public sealed record RemotePairResponse(string Key, string Scope, string Name);

public sealed record RemoteStageMessage(string? Text = null);
