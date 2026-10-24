using MxU.Slides.Broadcast.Streaming;

namespace MxU.Slides.Broadcast.Rtmp;

/// <summary>
/// An RTMP server URL taken apart: rtmp://host[:port]/app[/instance][?query]. The "app" is
/// the whole path (and query) after the host; "tcUrl" is the URL as given, minus any user
/// name and password and a trailing slash; the stream key travels separately in the publish
/// command. Default ports are 1935 for rtmp and 443 for rtmps. Port of the URL handling in
/// HaishinKit's RTMPConnection.connect.
/// </summary>
internal sealed record RtmpUrl(bool Secure, string Host, int Port, string App, string TcUrl)
{
    public const int DefaultPort = 1935;
    public const int DefaultSecurePort = 443;

    public static RtmpUrl Parse(string text)
    {
        var trimmed = text.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            throw StreamSessionException.BadUrl(text);
        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme is not ("rtmp" or "rtmps")) throw StreamSessionException.BadUrl(text);
        var secure = scheme == "rtmps";
        // "rtmp" isn't a scheme .NET knows, so Port is -1 unless the URL names one.
        var explicitPort = uri.Port > 0;
        var port = explicitPort ? uri.Port : secure ? DefaultSecurePort : DefaultPort;
        var path = uri.AbsolutePath.Trim('/');
        var app = Uri.UnescapeDataString(path) + (uri.Query.Length > 1 ? uri.Query : "");
        var tcUrl = $"{scheme}://{uri.Host}{(explicitPort ? ":" + uri.Port : "")}/{path}{(uri.Query.Length > 1 ? uri.Query : "")}".TrimEnd('/');
        return new RtmpUrl(secure, uri.Host, port, app, tcUrl);
    }
}
