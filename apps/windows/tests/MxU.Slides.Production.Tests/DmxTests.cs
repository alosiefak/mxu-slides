using System.Buffers.Binary;
using System.Net;
using MxU.Slides.Production.Devices;
using MxU.Slides.Production.Dmx;
using MxU.Slides.Production.Lighting;
using MxU.Slides.Production.Simulators;
using MxU.Slides.Production.Tests.Fakes;

namespace MxU.Slides.Production.Tests;

// Art-Net 4 (ArtDmx) and ANSI E1.31-2018 (sACN) data packets, byte for byte.
public class DmxPacketTests
{
    private static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", ""));

    [Fact]
    public void AnArtDmxPacketIsLaidOutAsTheSpecSays()
    {
        var packet = ArtNetPacket.Dmx(0x1234, 7, [255, 128, 0]);
        // "Art-Net\0", OpDmx 0x5000 low byte first, version 14, sequence, physical,
        // SubUni (low byte of the port-address), Net (high 7 bits), length 4 (padded to even), data.
        Assert.Equal(Hex("4172742D4E657400 0050 000E 07 00 34 12 0004 FF800000"), packet);
    }

    [Fact]
    public void ArtNetLengthsAreEvenAndAtLeastTwo()
    {
        Assert.Equal(20, ArtNetPacket.Dmx(0, 1, []).Length);
        Assert.Equal(18 + 512, ArtNetPacket.Dmx(0, 1, new byte[512]).Length);
        Assert.Equal(18 + 512, ArtNetPacket.Dmx(0, 1, new byte[600]).Length);
    }

    [Fact]
    public void ShowUniverseOneIsArtNetZeroAndSequencesSkipZero()
    {
        Assert.Equal(0, ArtNetPacket.PortAddress(1));
        Assert.Equal(16, ArtNetPacket.PortAddress(17));
        Assert.Equal("0:1:0", DmxOutput.ArtNetName(16));
        Assert.Equal(1, ArtNetPacket.NextSequence(255));
        Assert.Equal(1, ArtNetPacket.NextSequence(0));
        Assert.Equal(6, ArtNetPacket.NextSequence(5));
    }

    [Fact]
    public void AnSacnDataPacketIsLaidOutAsE131Says()
    {
        var cid = Guid.Parse("01020304-0506-0708-090a-0b0c0d0e0f10");
        var levels = new byte[512];
        levels[0] = 255;
        levels[511] = 9;
        var packet = SacnPacket.Data(cid, "MxU Slides (TEST)", 100, 300, 42, levels);
        Assert.Equal(638, packet.Length);
        // Root layer
        Assert.Equal(Hex("0010 0000 4153432D45312E3137000000 726E 00000004 0102030405060708090A0B0C0D0E0F10"), packet[..38]);
        // Framing layer: flags+length (600), vector 2, the name (64 bytes, zero padded), priority, sync, sequence, options, universe
        Assert.Equal(Hex("7258 00000002"), packet[38..44]);
        Assert.Equal("MxU Slides (TEST)"u8.ToArray(), packet[44..61]);
        Assert.All(packet[61..108], b => Assert.Equal(0, b));   // the rest of the 64-byte name is zeros
        Assert.Equal(Hex("64 0000 2A 00 012C"), packet[108..115]);
        // DMP layer: flags+length (523), vector 2, type 0xA1, first address 0, increment 1, 513 values, start code 0
        Assert.Equal(Hex("720B 02 A1 0000 0001 0201 00"), packet[115..126]);
        Assert.Equal(255, packet[126]);
        Assert.Equal(9, packet[637]);
    }

    [Fact]
    public void AStoppingSourceSaysSoInItsOptions() =>
        Assert.Equal(0x40, SacnPacket.Data(Guid.NewGuid(), "x", 100, 1, 0, new byte[512], SacnPacket.StreamTerminated)[112]);

    [Fact]
    public void EachSacnUniverseHasItsOwnMulticastGroup()
    {
        Assert.Equal(IPAddress.Parse("239.255.0.1"), SacnPacket.MulticastGroup(1));
        Assert.Equal(IPAddress.Parse("239.255.1.44"), SacnPacket.MulticastGroup(300));
        Assert.Equal(1, SacnPacket.Universe(0));
        Assert.Equal(63999, SacnPacket.Universe(70000));
    }
}

// The DMX engine and a node, against a capture on 127.0.0.1.
public class DmxOutputTests
{
    private static LightingShow Show() => new()
    {
        Looks =
        [
            new LightingLook { Id = "warm", Name = "Warm", FadeSeconds = 0, Levels = [new(1, 1, 255), new(1, 2, 128), new(2, 5, 77)] },
        ],
    };

    private static DmxOutput Node(DeviceKind kind, int port, params int[] universes)
    {
        var settings = Wait.Device(kind, port);
        settings.Delivery = DmxDelivery.Unicast;
        settings.Universes = [.. universes];
        var node = new DmxOutput(settings, Guid.Parse("11111111-2222-3333-4444-555555555555"), "MxU test");
        node.Start();
        return node;
    }

    [Fact]
    public async Task NothingIsSentUntilALightingStepRunsThenAboutFortyFramesASecond()
    {
        using var capture = new UdpCapture();
        var lighting = new LightingController();
        lighting.Load(Show());
        using var engine = new DmxEngine(lighting);
        using var node = Node(DeviceKind.ArtNet, capture.Port, 1);
        engine.SetOutputs([node]);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Empty(capture.Packets);
        Assert.Equal(DeviceState.Idle, node.Status.State);

        Assert.True(lighting.FireLook("warm"));
        var first = capture.WaitFor(p => p.Length == 18 + 512 && p[18] == 255);
        Assert.NotNull(first);
        Assert.Equal(128, first[19]);
        // Universe 1 goes out as Art-Net 0:0:0 (SubUni and Net both 0).
        Assert.Equal((0, 0), (first[14], first[15]));
        await Wait.Until(() => node.Status.IsOnline);
        Assert.Contains("Art-Net 0:0:0", node.Status.Detail);

        var before = capture.Packets.Count;
        await Task.Delay(1000, TestContext.Current.CancellationToken);
        var perSecond = capture.Packets.Count - before;
        // About 40 a second; generous limits, as the PC may be busy with other tests.
        Assert.InRange(perSecond, 15, 60);
        // Each packet's sequence is one more than the last (1-255, skipping 0).
        var sequences = capture.Packets.Select(p => p.Packet[12]).Take(30).ToList();
        for (var i = 1; i < sequences.Count; i++) Assert.Equal(ArtNetPacket.NextSequence(sequences[i - 1]), sequences[i]);
    }

    [Fact]
    public async Task AnSacnNodeGetsOnlyItsOwnUniverses()
    {
        using var capture = new UdpCapture();
        var lighting = new LightingController();
        lighting.Load(Show());
        using var engine = new DmxEngine(lighting);
        using var node = Node(DeviceKind.Sacn, capture.Port, 2);
        engine.SetOutputs([node]);
        lighting.FireLook("warm");
        var packet = capture.WaitFor(p => p.Length == SacnPacket.Length);
        Assert.NotNull(packet);
        Assert.Equal(2, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(113)));
        Assert.Equal(100, packet[108]);
        Assert.Equal(77, packet[126 + 4]);   // channel 5 of universe 2
        Assert.Equal(Guid.Parse("11111111-2222-3333-4444-555555555555").ToByteArray(bigEndian: true), packet[22..38]);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.All(capture.Packets, p => Assert.Equal(2, BinaryPrimitives.ReadUInt16BigEndian(p.Packet.AsSpan(113))));
    }

    [Fact]
    public void StoppingAnSacnSourceSendsThreeTerminatedPackets()
    {
        using var capture = new UdpCapture();
        using var node = Node(DeviceKind.Sacn, capture.Port, 1);
        node.Terminate([1]);
        Assert.NotNull(capture.WaitFor(_ => capture.Packets.Count >= 3));
        Assert.All(capture.Packets, p => Assert.Equal(SacnPacket.StreamTerminated, p.Packet[112]));
    }
}
