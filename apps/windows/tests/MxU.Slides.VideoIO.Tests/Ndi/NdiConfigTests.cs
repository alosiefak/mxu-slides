using System.Text;
using MxU.Slides.VideoIO.Ndi;

namespace MxU.Slides.VideoIO.Tests.Ndi;

/// <summary>Port of NDIKitTests/NDIFingerprintTests.swift.</summary>
public sealed class NdiFingerprintTests
{
    [Fact]
    public void FingerprintIsOrderInsensitive()
    {
        var wifi = new NdiAdapter("{A1}", "Wi-Fi", "192.168.1.4");
        var wire = new NdiAdapter("{B2}", "Ethernet", "10.1.20.7");
        Assert.Equal(NdiAdapterList.Fingerprint([wifi, wire]), NdiAdapterList.Fingerprint([wire, wifi]));
    }

    [Fact]
    public void ReaddressChangesTheFingerprint()
    {
        var before = new NdiAdapter("{A1}", "Wi-Fi", "192.168.1.4");
        var after = new NdiAdapter("{A1}", "Wi-Fi", "192.168.2.4");
        Assert.NotEqual(NdiAdapterList.Fingerprint([before]), NdiAdapterList.Fingerprint([after]));
    }
}

/// <summary>Port of NDIKitTests/NDIRuntimeConfigTests.swift.</summary>
[Collection(NdiRuntimeCollection.Name)]
public sealed class NdiRuntimeConfigTests
{
    [Fact]
    public void PinnedConfigListsTheAdapter() =>
        Assert.Equal("""{"ndi":{"adapters":{"allowed":["10.1.20.7"]}}}""", Encoding.UTF8.GetString(NdiRuntimeConfig.ConfigJson(["10.1.20.7"])));

    [Fact]
    public void AutomaticConfigRestrictsNothing() =>
        Assert.Equal("""{"ndi":{}}""", Encoding.UTF8.GetString(NdiRuntimeConfig.ConfigJson([])));

    [Fact]
    public void ActivateWritesTheFileAndPointsTheProcessAtIt()
    {
        using var folder = new TestSupport.ScratchFolder();
        var previous = Environment.GetEnvironmentVariable(NdiRuntimeConfig.EnvironmentKey);
        try
        {
            NdiRuntimeConfig.Activate(["192.168.4.9"], folder.Path);
            Assert.Equal(folder.Path, Environment.GetEnvironmentVariable(NdiRuntimeConfig.EnvironmentKey));
            var written = File.ReadAllBytes(Path.Combine(folder.Path, NdiRuntimeConfig.FileName));
            Assert.Equal(NdiRuntimeConfig.ConfigJson(["192.168.4.9"]), written);
        }
        finally
        {
            NdiRuntimeConfig.SetVariable(NdiRuntimeConfig.EnvironmentKey, previous);
        }
    }

    [Fact]
    public void AdapterListIsLoopbackFreeAndAddressed()
    {
        foreach (var adapter in NdiAdapterList.Current())
        {
            Assert.False(adapter.Ipv4.StartsWith("127.", StringComparison.Ordinal));
            Assert.NotEmpty(adapter.Ipv4);
            Assert.NotEmpty(adapter.DisplayName);
            Assert.NotEmpty(adapter.Id);
        }
    }

    [Fact]
    public void TheSharedFolderFollowsTheLocalDataOverride()
    {
        var previous = Environment.GetEnvironmentVariable("MXU_SLIDES_LOCAL");
        try
        {
            Environment.SetEnvironmentVariable("MXU_SLIDES_LOCAL", @"C:\scratch\local");
            Assert.Equal(@"C:\scratch\local\NDI", NdiRuntimeConfig.SharedDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MXU_SLIDES_LOCAL", previous);
        }
    }
}

/// <summary>Windows version of NDIKitTests/NDISearchPathTests.swift: where the installed runtime is looked for.</summary>
public sealed class NdiSearchPathTests
{
    [Fact]
    public void TheRuntimeFolderNdiToolsAdvertisesComesFirstThenV5ThenTheDefaultInstall()
    {
        var environment = new Dictionary<string, string?>
        {
            ["NDI_RUNTIME_DIR_V6"] = @"D:\NDI6\Runtime",
            ["NDI_RUNTIME_DIR_V5"] = @"D:\NDI5\Runtime",
        };
        var paths = NdiLibrary.CandidatePaths(name => environment.GetValueOrDefault(name), @"C:\Program Files");
        Assert.Equal(@"D:\NDI6\Runtime\Processing.NDI.Lib.x64.dll", paths[0]);
        Assert.Equal(@"D:\NDI5\Runtime\Processing.NDI.Lib.x64.dll", paths[1]);
        Assert.Equal(@"C:\Program Files\NDI\NDI 6 Tools\Runtime\Processing.NDI.Lib.x64.dll", paths[2]);
        Assert.Contains(@"C:\Program Files\NDI\NDI 5 Runtime\v5\Processing.NDI.Lib.x64.dll", paths);
    }

    [Fact]
    public void WithoutTheVariablesOnlyTheInstallFoldersAreTried()
    {
        var paths = NdiLibrary.CandidatePaths(_ => null, @"C:\Program Files");
        Assert.All(paths, path => Assert.StartsWith(@"C:\Program Files\NDI\", path));
    }

    [Fact]
    public void BothVariablesNamingOneFolderTryItOnce()
    {
        var paths = NdiLibrary.CandidatePaths(_ => @"C:\Program Files\NDI\NDI 6 Tools\Runtime", @"C:\Program Files");
        Assert.Single(paths, path => path.Contains("NDI 6 Tools", StringComparison.Ordinal));
    }
}
