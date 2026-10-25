using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace MxU.Slides.Production.Simulators;

/// <summary>Catches every UDP packet sent to a port on 127.0.0.1 (a pretend Art-Net or sACN node), for the tests and the self-test.</summary>
public sealed class UdpCapture : IDisposable
{
    private readonly Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private volatile bool stopped;

    public UdpCapture()
    {
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        new Thread(Run) { IsBackground = true, Name = "UDP capture" }.Start();
    }

    public int Port => ((IPEndPoint)socket.LocalEndPoint!).Port;

    public ConcurrentQueue<(byte[] Packet, long At)> Packets { get; } = new();

    private void Run()
    {
        var buffer = new byte[2048];
        while (!stopped)
        {
            try
            {
                var length = socket.Receive(buffer);
                Packets.Enqueue((buffer[..length], Environment.TickCount64));
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { return; }
        }
    }

    /// <summary>Waits for a packet that matches (or the time is up).</summary>
    public byte[]? WaitFor(Func<byte[], bool> match, int milliseconds = 3000)
    {
        var until = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < until)
        {
            var found = Packets.FirstOrDefault(p => match(p.Packet));
            if (found.Packet is not null) return found.Packet;
            Thread.Sleep(5);
        }
        return null;
    }

    public void Dispose()
    {
        stopped = true;
        socket.Dispose();
    }
}
