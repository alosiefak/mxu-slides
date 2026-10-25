using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using MxU.Slides.Production.Devices;

namespace MxU.Slides.Production.Vmix;

/// <summary>
/// One HTTP/1.1 GET on a plain blocking socket, with a hard time limit. vMix's web API is
/// plain HTTP on the local network, and this way the request runs entirely on the device's
/// own thread (no thread pool), and gives up after <c>timeout</c> however the far end behaves.
/// </summary>
internal static class MiniHttp
{
    public sealed record Response(int Status, string Body);

    /// <summary>
    /// GETs <paramref name="pathAndQuery"/> ("/api/?Function=Cut"). With a user name or password
    /// it signs in with HTTP Basic, as vMix's web controller asks. Throws <see cref="IOException"/>,
    /// <see cref="SocketException"/> or <see cref="TimeoutException"/> when there's no good answer in time.
    /// </summary>
    public static Response Get(string host, int port, string pathAndQuery, string? user, string? password, TimeSpan timeout)
    {
        var started = Stopwatch.GetTimestamp();
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        var connecting = socket.BeginConnect(Network.ResolveIPv4(host), port, null, null);
        if (!connecting.AsyncWaitHandle.WaitOne(timeout)) throw new TimeoutException($"No answer from {host}:{port}");
        socket.EndConnect(connecting);

        var request = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"GET {pathAndQuery} HTTP/1.1\r\n")
            .Append(CultureInfo.InvariantCulture, $"Host: {host}:{port}\r\n")
            .Append("Connection: close\r\n");
        if (!string.IsNullOrEmpty(user) || !string.IsNullOrEmpty(password))
            request.Append("Authorization: Basic ").Append(Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"))).Append("\r\n");
        request.Append("\r\n");
        socket.SendTimeout = (int)timeout.TotalMilliseconds;
        socket.Send(Encoding.ASCII.GetBytes(request.ToString()));

        // "Connection: close" means the reply ends when the server closes: read until then (or the limit).
        var reply = new MemoryStream();
        var buffer = new byte[16384];
        while (true)
        {
            var left = timeout - Stopwatch.GetElapsedTime(started);
            if (left <= TimeSpan.Zero) throw new TimeoutException($"{host}:{port} was too slow to answer");
            socket.ReceiveTimeout = Math.Max(1, (int)left.TotalMilliseconds);
            int read;
            try { read = socket.Receive(buffer); }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut) { throw new TimeoutException($"{host}:{port} was too slow to answer"); }
            if (read == 0) break;
            reply.Write(buffer, 0, read);
            if (reply.Length > 8 * 1024 * 1024) throw new IOException("The reply is too big");
            if (Complete(reply)) break;
        }
        return Parse(reply.ToArray());
    }

    /// <summary>Whether the whole reply is in: its headers and as many bytes as Content-Length says (some servers don't close).</summary>
    private static bool Complete(MemoryStream reply)
    {
        var text = Encoding.Latin1.GetString(reply.GetBuffer(), 0, (int)reply.Length);
        var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (end < 0) return false;
        var length = ContentLength(text[..end]);
        return length is { } expected && reply.Length - (end + 4) >= expected;
    }

    private static int? ContentLength(string headers) =>
        headers.Split("\r\n").FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) is { } line
            && int.TryParse(line[15..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var length)
            ? length
            : null;

    internal static Response Parse(byte[] reply)
    {
        var text = Encoding.Latin1.GetString(reply);
        var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (end < 0) throw new IOException("Not an HTTP reply");
        var headers = text[..end];
        var statusLine = headers.Split("\r\n")[0].Split(' ');
        if (statusLine.Length < 2 || !int.TryParse(statusLine[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var status))
            throw new IOException("Not an HTTP reply");
        var body = reply.AsSpan(end + 4);
        if (headers.Contains("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase)) body = Unchunk(body.ToArray());
        if (ContentLength(headers) is { } length && length < body.Length) body = body[..length];
        return new Response(status, Encoding.UTF8.GetString(body));
    }

    private static byte[] Unchunk(byte[] body)
    {
        var output = new MemoryStream();
        var at = 0;
        while (at < body.Length)
        {
            var lineEnd = Array.IndexOf(body, (byte)'\n', at);
            if (lineEnd < 0) break;
            var sizeText = Encoding.ASCII.GetString(body, at, lineEnd - at).Trim().Split(';')[0];
            if (!int.TryParse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size) || size == 0) break;
            at = lineEnd + 1;
            if (at + size > body.Length) size = body.Length - at;
            output.Write(body, at, size);
            at += size + 2;
        }
        return output.ToArray();
    }
}
