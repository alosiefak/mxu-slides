namespace MxU.Slides.Broadcast.Streaming;

public enum StreamStateKind { Idle, Connecting, Publishing, Reconnecting, Failed, Stopped }

/// <summary>
/// Where a stream session is. <see cref="Attempt"/> counts reconnect tries (1 is the first
/// retry after a drop); <see cref="Reason"/> says why it failed. Values compare by content, so
/// <c>state == StreamSessionState.Reconnecting(2)</c> works. Port of StreamSession.State.
/// </summary>
public readonly record struct StreamSessionState(StreamStateKind Kind, int Attempt = 0, string? Reason = null)
{
    public static StreamSessionState Idle => new(StreamStateKind.Idle);

    public static StreamSessionState Connecting => new(StreamStateKind.Connecting);

    public static StreamSessionState Publishing => new(StreamStateKind.Publishing);

    public static StreamSessionState Stopped => new(StreamStateKind.Stopped);

    public static StreamSessionState Reconnecting(int attempt) => new(StreamStateKind.Reconnecting, attempt);

    public static StreamSessionState Failed(string reason) => new(StreamStateKind.Failed, 0, reason);

    public override string ToString() => Kind switch
    {
        StreamStateKind.Reconnecting => $"reconnecting(attempt: {Attempt})",
        StreamStateKind.Failed => $"failed({Reason})",
        _ => Kind.ToString().ToLowerInvariant(),
    };
}

/// <summary>A snapshot of a session for status lines: its state and how many frames went out or were dropped.</summary>
public readonly record struct StreamSessionStatus(StreamSessionState State, int FramesSubmitted, int FramesDropped);
