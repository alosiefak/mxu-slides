using System.Diagnostics;
using MxU.Slides.Production.Atem;
using MxU.Slides.Production.Devices;
using MxU.Slides.Production.Simulators;
using MxU.Slides.Production.Tests.Fakes;

namespace MxU.Slides.Production.Tests;

// The ATEM protocol as atem-connection and libqatem document it: the bytes first, then a
// whole session against a pretend switcher on 127.0.0.1.
public class AtemPacketTests
{
    private static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", ""));

    [Fact]
    public void TheHelloIsTheTwentyBytesAtemConnectionSends() =>
        Assert.Equal(Hex("1014 53AB 0000 0000 003A 0000 0100 0000 0000 0000"), AtemPacket.Hello());

    [Fact]
    public void AnAckCarriesTheSessionAndThePacketItAcknowledges() =>
        Assert.Equal(Hex("800C 8001 0005 0000 0000 0000"), AtemPacket.Ack(0x8001, 5));

    [Fact]
    public void AReliablePacketHasItsIdAtTheEndOfTheHeaderAndItsCommandsAfter()
    {
        var packet = AtemPacket.Reliable(0x8001, 1, AtemCommands.Cut(0));
        Assert.Equal(Hex("0818 8001 0000 0000 0000 0001 000C 0000 44437574 00000000"), packet);
        var header = AtemHeader.Read(packet);
        Assert.Equal((AtemFlags.AckRequest, 24, (ushort)0x8001, (ushort)1), (header.Flags, header.Length, header.Session, header.PacketId));
        // A resend is the same packet with the Retransmit flag (0x04) added.
        Assert.Equal(AtemFlags.AckRequest | AtemFlags.Retransmit, AtemHeader.Read(AtemPacket.AsRetransmit(packet)).Flags);
    }

    [Fact]
    public void EachCommandIsItsLengthTwoZerosItsNameAndItsData()
    {
        Assert.Equal(Hex("000C0000 43506749 0000 0002"), AtemCommands.ProgramInput(0, 2));        // CPgI: M/E 1, Camera 2
        Assert.Equal(Hex("000C0000 43507649 0100 0BC2"), AtemCommands.PreviewInput(1, 3010));     // CPvI: M/E 2, Media Player 1
        Assert.Equal(Hex("000C0000 44417574 00000000"), AtemCommands.Auto(0));                    // DAut
        Assert.Equal(Hex("000C0000 46746241 00020000"), AtemCommands.FadeToBlack(0));             // FtbA
        Assert.Equal(Hex("000C0000 4D416374 0003 0000"), AtemCommands.RunMacro(3));               // MAct: macro 4, run
        Assert.Equal(Hex("000C0000 43417553 01 00 271A"), AtemCommands.AuxSource(0, 10010));      // CAuS: aux 1, ME 1 program
        Assert.Equal(Hex("000C0000 434B4F6E 00 01 01 00"), AtemCommands.UpstreamKeyOnAir(0, 1, true)); // CKOn: key 2 on
        Assert.Equal(Hex("000C0000 4344734C 01 00 0000"), AtemCommands.DownstreamKeyOnAir(1, false));  // CDsL: DSK 2 off
    }

    [Fact]
    public void ABodySplitsBackIntoItsCommands()
    {
        var body = AtemCommands.Cut(0).Concat(AtemCommands.ProgramInput(0, 4)).ToArray();
        var commands = AtemPacket.Commands(body).ToList();
        Assert.Equal(["DCut", "CPgI"], commands.Select(c => c.Name));
        Assert.Equal([0, 0, 0, 4], commands[1].Data);
    }

    [Fact]
    public void PacketIdsWrapAt32768AndAcksCoverEverythingBefore()
    {
        Assert.Equal(0, AtemPacket.Next(32767));
        Assert.True(AtemPacket.Covers(5, 3));
        Assert.True(AtemPacket.Covers(5, 5));
        Assert.False(AtemPacket.Covers(3, 5));
        Assert.True(AtemPacket.Covers(2, 32766));   // across the wrap
        Assert.False(AtemPacket.Covers(32766, 2));
    }

    [Fact]
    public void TheStateReadsNamesMacrosAndWhatIsOnProgram()
    {
        var state = new AtemState();
        var input = new byte[36];
        input[1] = 2;
        "Pulpit"u8.CopyTo(input.AsSpan(2));
        "PULP"u8.CopyTo(input.AsSpan(22));
        state.Apply("InPr", input);
        state.Apply("_pin", "ATEM Mini Pro\0\0\0"u8);
        state.Apply("MPrp", [0, 3, 1, 0, 0, 4, 0, 0, (byte)'W', (byte)'a', (byte)'l', (byte)'k']);
        state.Apply("PrgI", [0, 0, 0, 2]);
        state.Apply("MPrp", [0, 5, 1, 0, 0, 0, 0, 0]);   // recorded, but with no name
        state.Apply("XYZW", [1, 2, 3]);
        Assert.Equal(new AtemInput(2, "Pulpit", "PULP"), Assert.Single(state.Inputs));
        Assert.Equal("ATEM Mini Pro", state.ProductName);
        Assert.Equal("Walk", state.Macros[3]);
        Assert.Equal("Macro 6", state.Macros[5]);
        Assert.Equal(2, state.Program(0));
        Assert.False(state.InitComplete);
        state.Apply("InCm", [1, 0, 0, 0]);
        Assert.True(state.InitComplete);
    }
}

public class AtemSwitcherTests
{
    private static AtemSwitcher Switcher(int port) => new(Wait.Device(DeviceKind.Atem, port));

    [Fact]
    public async Task ItShakesHandsReadsTheInputsAndItsCommandsArrive()
    {
        using var simulator = new AtemSimulator("Pulpit", "Wide", "Choir");
        using var atem = Switcher(simulator.Port);
        atem.Start();
        await Wait.Until(() => atem.Status.IsOnline, what: "the ATEM to connect");
        Assert.Equal("ATEM Test Switcher", atem.State.ProductName);
        Assert.Equal(["Pulpit", "Wide", "Choir"], atem.State.Inputs.Select(i => i.LongName));
        Assert.Equal("Walk-in", atem.State.Macros[3]);
        Assert.Equal((1, 2), (atem.State.Program(0), atem.State.Preview(0)));
        Assert.Equal(["Pulpit", "Wide", "Choir"], atem.Catalog.Inputs.Select(i => i.Name));
        Assert.Equal(new CatalogEntry(4, "Walk-in"), Assert.Single(atem.Catalog.Macros));

        Assert.True(await atem.Program(1, 2));
        Assert.True(await atem.Cut(1));
        Assert.True(await atem.RunMacro(4));
        Assert.True(await atem.DownstreamKey(1, true));
        Assert.Equal(["CPgI", "DCut", "MAct", "CDsL"], simulator.Commands.Select(c => c.Name));
        Assert.Equal([0, 0, 0, 2], simulator.Commands.First().Data);
        Assert.Equal([0, 3, 0, 0], simulator.Commands.First(c => c.Name == "MAct").Data);
        // Everything the switcher numbered (its state and its pings) was acked, in the session it gave.
        await Wait.Until(() => simulator.Unacknowledged == 0, what: "every packet to be acked");
        Assert.Equal(0, simulator.WrongSession);
    }

    [Fact]
    public async Task ALostStatePacketIsNeverAckedPastAndArrivesWhenSentAgain()
    {
        // Packet 2 of the state (the first two input names) goes missing: 3, 4 and 5 arrive first.
        using var simulator = new AtemSimulator("Pulpit", "Wide", "Choir") { LoseStatePacket = 2 };
        using var atem = Switcher(simulator.Port);
        atem.Start();
        await Wait.Until(() => atem.Status.IsOnline, what: "the ATEM to connect once packet 2 is sent again");
        // Acking 3 would have told the switcher 2 had come too, and it would never be sent again.
        Assert.False(simulator.AckedPastAGap);
        Assert.True(simulator.Resends >= 1);
        Assert.Equal(["Pulpit", "Wide", "Choir"], atem.State.Inputs.Select(i => i.LongName));
        Assert.Equal(1, simulator.Hellos);
    }

    [Fact]
    public async Task AResendOfAPacketItAlreadyHadIsAckedAgain()
    {
        // Every ack of the five state packets is lost, the switcher doesn't ping, and the client's
        // own keepalive is off: only acking the switcher's resends can tell it they got through.
        using var simulator = new AtemSimulator { LoseClientAcks = 5, Pinging = false };
        using var atem = Switcher(simulator.Port);
        atem.KeepaliveEvery = TimeSpan.FromHours(1);
        atem.Start();
        await Wait.Until(() => atem.Status.IsOnline);
        await Wait.Until(() => simulator.Unacknowledged == 0, what: "the switcher to have every ack");
        Assert.True(simulator.Resends >= 1);
        Assert.Equal(0, simulator.SessionsDropped);
    }

    [Fact]
    public async Task ARetransmitRequestGetsACommandSentAgainAtOnce()
    {
        using var simulator = new AtemSimulator();
        using var atem = Switcher(simulator.Port);
        atem.Start();
        await Wait.Until(() => atem.Status.IsOnline);
        // Its own resend timer is far off: only answering the switcher's "send it again" can deliver the cut.
        atem.ResendAfter = TimeSpan.FromSeconds(30);
        simulator.AskAgainForNextCommand = true;
        var watch = Stopwatch.StartNew();
        Assert.True(await atem.Cut(1));
        Assert.True(watch.ElapsedMilliseconds < 3000, $"took {watch.ElapsedMilliseconds} ms");
        Assert.Equal((1, 1), (simulator.RetransmitRequests, simulator.Retransmits));
        Assert.Equal("DCut", Assert.Single(simulator.Commands).Name);
    }

    [Fact]
    public async Task ALostCommandPacketIsSentAgainUntilItIsAcked()
    {
        using var simulator = new AtemSimulator();
        using var atem = Switcher(simulator.Port);
        atem.Start();
        await Wait.Until(() => atem.Status.IsOnline);
        simulator.DropCommandPackets = 2;
        Assert.True(await atem.Auto(1));
        Assert.True(simulator.Retransmits >= 2);
        Assert.Equal("DAut", Assert.Single(simulator.Commands).Name);
    }

    [Fact]
    public async Task ItAcksThePingsSoTheSessionStaysUp()
    {
        // A switcher gives up on a client that doesn't ack (and goes quiet, which the client would notice).
        using var simulator = new AtemSimulator { DropClientAfterMilliseconds = 1500 };
        using var atem = Switcher(simulator.Port);
        atem.SilenceTimeout = TimeSpan.FromSeconds(1.5);
        atem.Start();
        await Wait.Until(() => atem.Status.IsOnline);
        await Task.Delay(3000, TestContext.Current.CancellationToken);
        Assert.True(atem.Status.IsOnline);
        Assert.Equal((1, 0), (simulator.Hellos, simulator.SessionsDropped));
        // The state was packets 1-5; the pings after it (about two a second) were acked too.
        Assert.True(simulator.LastClientAck > 6, $"last ack {simulator.LastClientAck}");
    }

    [Fact]
    public async Task WhenTheSwitcherGoesQuietItReconnectsByItself()
    {
        using var simulator = new AtemSimulator();
        using var atem = Switcher(simulator.Port);
        atem.SilenceTimeout = TimeSpan.FromMilliseconds(600);
        atem.HelloRetry = TimeSpan.FromMilliseconds(200);
        atem.Start();
        await Wait.Until(() => atem.Status.IsOnline);
        simulator.Silent = true;
        await Wait.Until(() => atem.Status.State == DeviceState.Problem, what: "the light to go red");
        Assert.False(await atem.Cut(1));
        simulator.Silent = false;
        await Wait.Until(() => atem.Status.IsOnline, what: "the ATEM to come back");
        Assert.True(simulator.Hellos >= 2);
        Assert.True(await atem.Cut(1));
    }

    [Fact]
    public async Task ASwitcherThatVanishesHoldsUpOneCommandAtMostThenTheRestAreSkipped()
    {
        using var simulator = new AtemSimulator();
        using var atem = Switcher(simulator.Port);
        atem.Start();
        await Wait.Until(() => atem.Status.IsOnline);
        simulator.Silent = true;   // unplugged in the middle of the service
        var watch = Stopwatch.StartNew();
        // Five cuts queued at once: the first waits out its resends (about a second), the rest go nowhere at once.
        var cuts = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => atem.Cut(1)));
        Assert.All(cuts, Assert.False);
        Assert.True(watch.ElapsedMilliseconds < 2500, $"took {watch.ElapsedMilliseconds} ms");
        Assert.Equal(DeviceState.Problem, atem.Status.State);
    }

    [Fact]
    public async Task AMissingSwitcherTurnsRedQuicklyAndNeverHoldsACommandUp()
    {
        using var atem = Switcher(Wait.UnusedPort());
        atem.HelloRetry = TimeSpan.FromMilliseconds(150);
        atem.Start();
        await Wait.Until(() => atem.Status.State == DeviceState.Problem, 3000, "the light to go red");
        Assert.Contains("No answer", atem.Status.Detail);
        var watch = Stopwatch.StartNew();
        Assert.False(await atem.Program(1, 2));
        Assert.True(watch.ElapsedMilliseconds < 500, $"took {watch.ElapsedMilliseconds} ms");
    }
}
