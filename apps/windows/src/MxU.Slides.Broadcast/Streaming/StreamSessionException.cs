namespace MxU.Slides.Broadcast.Streaming;

public enum StreamFailureKind
{
    /// <summary>The server URL can't be used at all.</summary>
    BadUrl,

    /// <summary>The connection dropped or was refused: worth reconnecting.</summary>
    TransportClosed,

    /// <summary>Retrying can't help (credentials rejected, no encoder on this PC): the session fails.</summary>
    Fatal,
}

/// <summary>Why a stream attempt ended. Port of StreamSessionError.</summary>
public sealed class StreamSessionException(StreamFailureKind kind, string reason, Exception? inner = null)
    : Exception(reason, inner)
{
    public StreamFailureKind Kind { get; } = kind;

    public bool IsFatal => Kind == StreamFailureKind.Fatal;

    /// <summary>The words the status line shows after "Failed —".</summary>
    public string FailureDescription => Kind == StreamFailureKind.BadUrl ? $"bad URL: {Message}" : Message;

    public static StreamSessionException BadUrl(string url) => new(StreamFailureKind.BadUrl, url);

    public static StreamSessionException TransportClosed(string reason, Exception? inner = null) =>
        new(StreamFailureKind.TransportClosed, reason, inner);

    public static StreamSessionException Fatal(string reason, Exception? inner = null) => new(StreamFailureKind.Fatal, reason, inner);
}
