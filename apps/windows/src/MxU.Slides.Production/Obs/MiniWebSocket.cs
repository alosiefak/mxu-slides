using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using MxU.Slides.Production.Devices;

namespace MxU.Slides.Production.Obs;

/// <summary>
/// Just enough of a WebSocket client (RFC 6455) to talk to obs-websocket, on plain blocking
/// sockets so it runs on the device's own threads and never needs .NET's thread pool:
/// <list type="bullet">
/// <item>connect with a time limit, then the HTTP "Upgrade: websocket" handshake, checking the
/// server's Sec-WebSocket-Accept answer;</item>
/// <item>send text frames (a client must mask what it sends, with four random bytes);</item>
/// <item>receive frames: text (joining fragments), ping (answered with a pong), close.</item>
/// </list>
/// One thread receives; any thread may send (sends take a lock, so frames never interleave).
/// </summary>
internal sealed class MiniWebSocket : IDisposable
{
    // The fixed GUID RFC 6455 mixes into the handshake key.
    private const string HandshakeGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly Socket socket;
    private readonly NetworkStream stream;
    private readonly Lock sending = new();

    private MiniWebSocket(Socket socket)
    {
        this.socket = socket;
        stream = new NetworkStream(socket, ownsSocket: true);
    }

    /// <summary>
    /// How long a receive waits (zero: for ever). It starts as the connect time limit, for the
    /// sign-in; the reader thread then waits for ever, and keepalives notice a dead peer.
    /// </summary>
    public TimeSpan ReadTimeout
    {
        set => socket.ReceiveTimeout = (int)value.TotalMilliseconds;
    }

    /// <summary>The close code the server sent (4009 is obs-websocket's "wrong password"), once it closed.</summary>
    public int? CloseCode { get; private set; }

    /// <summary>
    /// Connects and upgrades to a WebSocket. Throws (an <see cref="IOException"/>, a
    /// <see cref="SocketException"/> or a <see cref="TimeoutException"/>) if it can't within <paramref name="timeout"/>.
    /// </summary>
    public static MiniWebSocket Connect(string host, int port, TimeSpan timeout)
    {
        var socket = Network.ConnectTcp(host, port, timeout);
        try
        {
            socket.ReceiveTimeout = (int)timeout.TotalMilliseconds;
            socket.SendTimeout = (int)timeout.TotalMilliseconds;
            // TCP keepalives notice a peer that vanished (a PC unplugged) within about 8 s, where a
            // blocked read would otherwise wait for hours. Windows 10 1709 and later take these.
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 5);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 1);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
            var made = new MiniWebSocket(socket);
            made.Handshake(host, port);
            return made;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>What the server must answer for a handshake key: base64(SHA-1(key + the RFC's GUID)).</summary>
    public static string AcceptFor(string key) =>
        Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + HandshakeGuid)));

    private void Handshake(string host, int port)
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var request = $"GET / HTTP/1.1\r\nHost: {host}:{port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
            + $"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n";
        stream.Write(Encoding.ASCII.GetBytes(request));

        // Read the reply's header, a byte at a time, up to the blank line (it's short, and nothing after it is read).
        var header = new StringBuilder();
        while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            var next = stream.ReadByte();
            if (next < 0) throw new IOException("The server closed the connection during the handshake");
            header.Append((char)next);
            if (header.Length > 8192) throw new IOException("The handshake reply is too long");
        }
        var lines = header.ToString().Split("\r\n");
        if (!lines[0].Contains(" 101 ", StringComparison.Ordinal))
            throw new IOException($"Not a WebSocket server ({lines[0]})");
        var accept = lines.FirstOrDefault(l => l.StartsWith("Sec-WebSocket-Accept:", StringComparison.OrdinalIgnoreCase))?[21..].Trim();
        if (accept != AcceptFor(key)) throw new IOException("The server's handshake answer is wrong");
    }

    /// <summary>Sends one text message.</summary>
    public void SendText(string text) => Send(0x1, Encoding.UTF8.GetBytes(text));

    private void Send(byte opcode, ReadOnlySpan<byte> payload)
    {
        // Header: FIN + opcode, then MASK + length (7 bits, or 126 + 2 bytes, or 127 + 8 bytes), then the mask.
        var header = new byte[14];
        var at = 0;
        header[at++] = (byte)(0x80 | opcode);
        if (payload.Length < 126)
        {
            header[at++] = (byte)(0x80 | payload.Length);
        }
        else if (payload.Length <= ushort.MaxValue)
        {
            header[at++] = 0x80 | 126;
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(at), (ushort)payload.Length);
            at += 2;
        }
        else
        {
            header[at++] = 0x80 | 127;
            BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(at), (ulong)payload.Length);
            at += 8;
        }
        var mask = RandomNumberGenerator.GetBytes(4);
        mask.CopyTo(header, at);
        at += 4;
        var masked = new byte[payload.Length];
        for (var i = 0; i < payload.Length; i++) masked[i] = (byte)(payload[i] ^ mask[i % 4]);
        lock (sending)
        {
            stream.Write(header, 0, at);
            stream.Write(masked);
        }
    }

    /// <summary>
    /// Waits for the next text message. Pings are answered on the way. Returns null when the
    /// server closes the connection (<see cref="CloseCode"/> then says why); throws when it drops.
    /// </summary>
    public string? ReceiveText()
    {
        var message = new MemoryStream();
        while (true)
        {
            var (fin, opcode, payload) = ReadFrame();
            switch (opcode)
            {
                case 0x8: // close
                    CloseCode = payload.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(payload) : 1005;
                    try { Send(0x8, payload.Length >= 2 ? payload.AsSpan(0, 2) : []); }
                    catch (IOException) { }
                    return null;
                case 0x9: // ping: answer with the same bytes
                    Send(0xA, payload);
                    continue;
                case 0xA: // pong
                    continue;
                case 0x1 or 0x2 or 0x0: // text, binary, continuation
                    message.Write(payload);
                    if (fin) return Encoding.UTF8.GetString(message.ToArray());
                    continue;
                default:
                    throw new IOException($"Unexpected WebSocket frame {opcode}");
            }
        }
    }

    private (bool Fin, int Opcode, byte[] Payload) ReadFrame()
    {
        var head = ReadExactly(2);
        var fin = (head[0] & 0x80) != 0;
        var opcode = head[0] & 0x0F;
        var masked = (head[1] & 0x80) != 0;
        long length = head[1] & 0x7F;
        if (length == 126) length = BinaryPrimitives.ReadUInt16BigEndian(ReadExactly(2));
        else if (length == 127) length = (long)BinaryPrimitives.ReadUInt64BigEndian(ReadExactly(8));
        if (length > 16 * 1024 * 1024) throw new IOException("A WebSocket message over 16 MB");
        var mask = masked ? ReadExactly(4) : null;
        var payload = ReadExactly((int)length);
        if (mask is not null)
            for (var i = 0; i < payload.Length; i++) payload[i] ^= mask[i % 4];
        return (fin, opcode, payload);
    }

    private byte[] ReadExactly(int count)
    {
        var buffer = new byte[count];
        stream.ReadExactly(buffer);
        return buffer;
    }

    public void Dispose()
    {
        try { socket.Shutdown(SocketShutdown.Both); }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
        stream.Dispose();
    }
}
