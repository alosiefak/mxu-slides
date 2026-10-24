using System.Runtime.CompilerServices;
using MxU.Slides.VideoIO.Common;
using MxU.Slides.VideoIO.Ndi;

namespace MxU.Slides.VideoIO.Tests.Ndi;

internal static class NdiTestRuntime
{
    /// <summary>The installed runtime, or a skip (CI has no NDI Tools).</summary>
    public static NdiLibrary Loaded()
    {
        if (!NdiLibrary.TryLoad(out var library, out var error)) Assert.Skip($"NDI runtime not installed: {error.Message}");
        return library;
    }
}

/// <summary>
/// Windows: a missing or broken runtime is reported, never thrown. Plays the part of the
/// Mac's NDIKitStubTests (the build without the NDI SDK).
/// </summary>
[Collection(NdiRuntimeCollection.Name)]
public sealed class NdiMissingRuntimeTests
{
    [Fact]
    public void AMissingRuntimeReportsTheInstallMessage()
    {
        var (library, error) = NdiLibrary.LoadFresh([@"C:\definitely\not\here\Processing.NDI.Lib.x64.dll"]);
        Assert.Null(library);
        var failure = Assert.IsType<NdiError>(error);
        Assert.Equal(NdiErrorKind.RuntimeNotFound, failure.Kind);
        Assert.Equal("NDI runtime not installed — install NDI Tools from ndi.video", failure.Message);
    }

    [Fact]
    public void AFileThatIsNotALibraryIsALoadFailure()
    {
        using var folder = new TestSupport.ScratchFolder();
        var junk = Path.Combine(folder.Path, NdiLibrary.RuntimeFileName);
        File.WriteAllText(junk, "not a DLL");
        var (library, error) = NdiLibrary.LoadFresh([junk]);
        Assert.Null(library);
        Assert.Equal(NdiErrorKind.LoadFailed, error!.Kind);
        Assert.Contains("would not load", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALibraryWithoutTheTableIsALoadFailure()
    {
        var kernel = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
        var (library, error) = NdiLibrary.LoadFresh([kernel]);
        Assert.Null(library);
        Assert.Equal(new NdiError(NdiErrorKind.LoadFailed, ["NDIlib_v5_load missing"]), error);
    }

    [Fact]
    public void EveryEntryPointReportsUnavailable()
    {
        Assert.Null(NdiFinder.Create(null));
        Assert.Null(NdiReceiver.Create(null));
        Assert.Null(NdiSender.Create(null, "MxU Stub"));
        using var input = new NdiInputSource(null, "MxU Stub");
        Assert.Equal(VideoInputState.OfflineBecause(NdiError.NotInstalledMessage), input.State);
        Assert.Null(input.LatestFrame());
    }

    [Fact]
    public void AnOutputWithoutTheRuntimeFailsWithTheMessageAndIgnoresFrames()
    {
        using var output = new NdiOutput("MxU Stub", 30_000, 1_000,
            () => (null, new NdiError(NdiErrorKind.RuntimeNotFound, [])));
        output.Start();
        Assert.Equal(new NdiOutputStatus(NdiOutputState.Failed, NdiError.NotInstalledMessage), output.Status);
        output.WriteVideo(TestSupport.Solid(4, 4, 1, 2, 3));
        output.WriteAudio(new Core.Media.AudioBuffer(48_000, 2, new float[64], 0));
        Assert.Equal(0, output.FramesSent);
        output.Stop();
        Assert.Equal(NdiOutputStatus.Idle, output.Status);
    }

    [Fact]
    public void ErrorsCompareByTheirDetails()
    {
        Assert.Equal(new NdiError(NdiErrorKind.TableMismatch, ["a", "b"]), new NdiError(NdiErrorKind.TableMismatch, ["a", "b"]));
        Assert.NotEqual(new NdiError(NdiErrorKind.TableMismatch, ["a"]), new NdiError(NdiErrorKind.TableMismatch, ["b"]));
    }
}

/// <summary>Port of NDIKitTests/NDITableLayoutTests.swift, plus the struct sizes the 64-bit runtime expects.</summary>
[Collection(NdiRuntimeCollection.Name)]
public sealed class NdiTableLayoutTests
{
    [Fact]
    public void EveryCalledSlotIsTheRuntimeExportOfThatName()
    {
        var library = NdiTestRuntime.Loaded();
        Assert.Empty(NdiLibrary.TableMismatches(library.Table, library.Export));
    }

    [Fact]
    public void TheSlottingThatCrashedLaunchIsRefusedBeforeAnyCall()
    {
        var library = NdiTestRuntime.Loaded();
        var table = library.Table with { FindCreateV2 = library.Table.Version, FindWaitForSources = library.Table.FindCreateV2 };
        string[] expected = ["NDIlib_find_create_v2", "NDIlib_find_wait_for_sources"];
        Assert.Equal(expected, NdiLibrary.TableMismatches(table, library.Export));
        var (activated, error) = NdiLibrary.Activate(table, library.Handle, library.LoadedFrom);
        Assert.Null(activated); // a mis-slotted table must never activate
        Assert.Equal(new NdiError(NdiErrorKind.TableMismatch, expected), error);
    }

    [Fact]
    public void AnEmptyTableNeverPassesForTheRuntime()
    {
        var mismatches = NdiLibrary.TableMismatches(new NdiTable(), _ => 0x1000);
        Assert.Equal(NdiTable.CalledSlots.Length, mismatches.Count);
        Assert.Equal(15, mismatches.Count); // the Mac's 13, plus version and audio sending
        Assert.Equal("NDIlib_initialize", mismatches[0]);
        Assert.Contains("NDIlib_find_create_v2", mismatches);
    }

    [Fact]
    public void StructsMatchTheSdkLayout()
    {
        Assert.Equal(24, Unsafe.SizeOf<NdiFindCreate>());
        Assert.Equal(16, Unsafe.SizeOf<NdiSource>());
        Assert.Equal(40, Unsafe.SizeOf<NdiRecvCreateV3>());
        Assert.Equal(24, Unsafe.SizeOf<NdiSendCreate>());
        Assert.Equal(72, Unsafe.SizeOf<NdiVideoFrameV2>());
        Assert.Equal(56, Unsafe.SizeOf<NdiAudioFrameV2>());
        Assert.Equal(0x41524742, NdiConstants.FourCCBgra); // 'BGRA' little-endian
    }

    [Fact]
    public void TheRuntimeReportsItsVersion()
    {
        var library = NdiTestRuntime.Loaded();
        Assert.Contains("NDI", library.Version, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Port of NDIKitTests/NDIReinitializeTests.swift.</summary>
[Collection(NdiRuntimeCollection.Name)]
public sealed class NdiReinitializeTests
{
    [Fact]
    public void SwapDeclinesWhileAnInstanceLivesThenLandsAfterRelease()
    {
        var library = NdiTestRuntime.Loaded();
        var finder = NdiFinder.Create(library);
        Assert.NotNull(finder);
        Assert.False(NdiLibrary.Reinitialize(TimeSpan.FromSeconds(0.3))); // a live finder must hold the swap off
        finder.Dispose();
        Assert.True(NdiLibrary.Reinitialize(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void RuntimeStillWorksAfterASwap()
    {
        var library = NdiTestRuntime.Loaded();
        Assert.True(NdiLibrary.Reinitialize(TimeSpan.FromSeconds(5)));
        using var finder = NdiFinder.Create(library);
        Assert.NotNull(finder);
        _ = finder.CurrentSourceNames(200);
        using var sender = NdiSender.Create(library, "MxU Swap Probe");
        Assert.NotNull(sender);
    }

    [Fact]
    public void GateBlocksNewInstancesOnlyDuringTheSwap()
    {
        var gate = new InstanceGate();
        gate.Enter();
        Assert.False(gate.TryExclusive(TimeSpan.FromSeconds(0.2), () => true, out _)); // exclusive must time out while a reader is active
        gate.Leave();
        Assert.True(gate.TryExclusive(TimeSpan.FromSeconds(1), () => 7, out var value));
        Assert.Equal(7, value);
    }
}

/// <summary>Windows: program audio is rearranged for NDI's planar layout.</summary>
public sealed class NdiAudioLayoutTests
{
    [Fact]
    public void InterleavedStereoBecomesOneChannelAfterTheOther()
    {
        float[] interleaved = [1, -1, 2, -2, 3, -3];
        var planar = new float[6];
        NdiSender.Deinterleave(interleaved, 2, planar);
        Assert.Equal([1f, 2, 3, -1, -2, -3], planar);
    }

    [Fact]
    public void ThreeChannelsKeepTheirOrder()
    {
        float[] interleaved = [1, 10, 100, 2, 20, 200];
        var planar = new float[6];
        NdiSender.Deinterleave(interleaved, 3, planar);
        Assert.Equal([1f, 2, 10, 20, 100, 200], planar);
    }
}
