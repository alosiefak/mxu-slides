using System.Buffers.Binary;
using System.Security.Cryptography;

namespace MxU.Slides.Broadcast.Rtmp;

/// <summary>
/// The RTMP "simple" handshake that opens every connection. The client sends C0 (the protocol
/// version, 3) and C1 (1536 bytes: a time, four zero bytes, random filler); the server answers
/// S0, S1 (its own 1536 bytes) and S2 (an echo of C1); the client finishes with C2, an echo of
/// S1. After that both sides speak in chunks. Port of HaishinKit's RTMPHandshake (which, like
/// this, doesn't do the Flash "digest" handshake; publishing servers don't require it).
/// </summary>
internal static class RtmpHandshake
{
    public const int SignatureSize = 1536;
    public const byte ProtocolVersion = 3;

    public static async Task ClientAsync(Stream stream, CancellationToken cancellation)
    {
        var started = Environment.TickCount64;
        var c0c1 = new byte[1 + SignatureSize];
        c0c1[0] = ProtocolVersion;
        BinaryPrimitives.WriteInt32BigEndian(c0c1.AsSpan(1), 0);
        RandomNumberGenerator.Fill(c0c1.AsSpan(9));
        await stream.WriteAsync(c0c1, cancellation);
        await stream.FlushAsync(cancellation);

        var s0s1 = new byte[1 + SignatureSize];
        await stream.ReadExactlyAsync(s0s1, cancellation);
        if (s0s1[0] != ProtocolVersion)
            throw new InvalidDataException($"server speaks RTMP version {s0s1[0]}, not 3");

        // C2: the server's time, how long we took, then its random bytes echoed back.
        var c2 = new byte[SignatureSize];
        s0s1.AsSpan(1, 4).CopyTo(c2);
        BinaryPrimitives.WriteInt32BigEndian(c2.AsSpan(4), (int)(Environment.TickCount64 - started));
        s0s1.AsSpan(9).CopyTo(c2.AsSpan(8));
        await stream.WriteAsync(c2, cancellation);
        await stream.FlushAsync(cancellation);

        var s2 = new byte[SignatureSize];
        await stream.ReadExactlyAsync(s2, cancellation);
    }

    /// <summary>The server half, for the in-process test server. Returns C1 so a test can check S2 echoed it.</summary>
    public static async Task<byte[]> ServerAsync(Stream stream, CancellationToken cancellation)
    {
        var c0c1 = new byte[1 + SignatureSize];
        await stream.ReadExactlyAsync(c0c1, cancellation);
        if (c0c1[0] != ProtocolVersion) throw new InvalidDataException($"client asked for RTMP version {c0c1[0]}");
        var s0s1s2 = new byte[1 + SignatureSize * 2];
        s0s1s2[0] = ProtocolVersion;
        RandomNumberGenerator.Fill(s0s1s2.AsSpan(9, SignatureSize - 8));
        c0c1.AsSpan(1).CopyTo(s0s1s2.AsSpan(1 + SignatureSize));
        await stream.WriteAsync(s0s1s2, cancellation);
        await stream.FlushAsync(cancellation);
        var c2 = new byte[SignatureSize];
        await stream.ReadExactlyAsync(c2, cancellation);
        if (!c2.AsSpan(8).SequenceEqual(s0s1s2.AsSpan(9, SignatureSize - 8)))
            throw new InvalidDataException("C2 didn't echo S1");
        return c0c1[1..];
    }
}
