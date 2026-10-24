using System.Security.Cryptography;
using System.Text;
using MxU.Slides.Broadcast.Library;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Broadcast.Tests;

/// <summary>Port of StreamDestinationReadinessTests.swift.</summary>
public sealed class StreamDestinationReadinessTests
{
    private static StreamPresetDestination Destination(string name, StreamTransport transport, string url, string? key = null) =>
        new() { Id = Guid.NewGuid().ToString(), Name = name, Transport = transport, Url = url, StreamKey = key };

    [Fact]
    public void CustomNeedsUrlAndKeyExceptSrt()
    {
        Assert.Equal("Destination: no server URL", StreamDestinationReadiness.Problem(Destination("Destination", StreamTransport.Rtmp, "")));
        Assert.Equal("Resi: no stream key", StreamDestinationReadiness.Problem(Destination("Resi", StreamTransport.Rtmps, "rtmps://x/app")));
        Assert.True(StreamDestinationReadiness.IsReady(Destination("North", StreamTransport.Srt, "srt://h:9000")));
        Assert.True(StreamDestinationReadiness.IsReady(Destination("Resi", StreamTransport.Rtmps, "rtmps://x/app", "k")));
        Assert.Equal("Blank: no server URL", StreamDestinationReadiness.Problem(Destination("Blank", StreamTransport.Hls, "   ")));
    }

    [Fact]
    public void ProblemsKeepOrder()
    {
        Assert.Equal(["Resi: no stream key", "Decoder: no server URL"], StreamDestinationReadiness.Problems([
            Destination("Resi", StreamTransport.Rtmps, "rtmps://x/app"),
            Destination("North", StreamTransport.Srt, "srt://h:9000"),
            Destination("Decoder", StreamTransport.Rtmp, ""),
        ]));
    }

    [Fact]
    public void StartRefusalOnlyWhenNothingCouldStart()
    {
        var broken = Destination("Decoder", StreamTransport.Rtmp, "");
        Assert.Equal("Won't go live: Decoder: no server URL", StreamDestinationReadiness.StartRefusal(StreamPresetKind.Stream, [broken]));
        var custom = Destination("Resi", StreamTransport.Rtmps, "rtmps://x/app", "k");
        Assert.Null(StreamDestinationReadiness.StartRefusal(StreamPresetKind.Stream, [custom, broken]));
        Assert.Equal("Won't go live: no destinations selected", StreamDestinationReadiness.StartRefusal(StreamPresetKind.Stream, []));
        Assert.Null(StreamDestinationReadiness.StartRefusal(StreamPresetKind.RecordOnly, []));
    }
}

/// <summary>Port of StreamAudioSourceTests.swift.</summary>
public sealed class StreamAudioSourceTests
{
    private static StreamRecordPreset Preset() => new()
    {
        Id = "p", Name = "P", AudioInputUid = "legacy-uid", AudioIncludesProgram = true, AudioInputId = "in-1", AudioMixId = "mix-1",
    };

    [Fact]
    public void ProgramClearsEveryAudioField()
    {
        var value = Preset();
        StreamAudioSource.Program.ApplyTo(value, includingProgram: true);
        Assert.Null(value.AudioMixId);
        Assert.Null(value.AudioInputId);
        Assert.Null(value.AudioInputUid);
        Assert.Null(value.AudioIncludesProgram);
    }

    [Fact]
    public void MixReplacesInputAndCarriesAddProgram()
    {
        var value = Preset();
        StreamAudioSource.Mix("stream").ApplyTo(value, includingProgram: true);
        Assert.Equal("stream", value.AudioMixId);
        Assert.Null(value.AudioInputId);
        Assert.Null(value.AudioInputUid);
        Assert.True(value.AudioIncludesProgram);
    }

    [Fact]
    public void InputReplacesMixWithoutAddProgram()
    {
        var value = Preset();
        StreamAudioSource.Input("pulpit").ApplyTo(value, includingProgram: false);
        Assert.Null(value.AudioMixId);
        Assert.Equal("pulpit", value.AudioInputId);
        Assert.Null(value.AudioInputUid);
        Assert.Null(value.AudioIncludesProgram);
    }

    [Fact]
    public void StorageValueRoundTrips()
    {
        foreach (var source in new[] { StreamAudioSource.Program, StreamAudioSource.Mix("main"), StreamAudioSource.Input("a:b") })
            Assert.Equal(source, StreamAudioSource.FromStorageValue(source.StorageValue));
        Assert.Equal(StreamAudioSource.Program, StreamAudioSource.FromStorageValue("mix:"));
        Assert.Equal(StreamAudioSource.Program, StreamAudioSource.FromStorageValue("junk"));
    }

    [Fact]
    public void OfReadsWhatThePresetIsSetTo()
    {
        Assert.Equal(StreamAudioSource.Mix("mix-1"), StreamAudioSource.Of(Preset()));
        Assert.Equal(StreamAudioSource.Program, StreamAudioSource.Of(new StreamRecordPreset { Id = "q", Name = "Q" }));
    }
}

/// <summary>Port of StreamDestinationLibraryTests.swift, plus the Windows rule that keys never reach the JSON.</summary>
public sealed class StreamDestinationLibraryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "BroadcastLibrary-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static StreamPresetDestination CustomLegacy(string name = "Resi", string url = "rtmps://x/app", string? key = "k") =>
        new() { Id = Guid.NewGuid().ToString().ToUpperInvariant(), Name = name, Transport = StreamTransport.Rtmps, Url = url, StreamKey = key };

    private (LibraryStore Store, StreamKeyVault Vault) Library()
    {
        var store = new LibraryStore(Path.Combine(root, "Library"));
        store.Load();
        return (store, new StreamKeyVault(Path.Combine(root, "Keys")));
    }

    [Fact]
    public void DisplayNameFallsBackToTheUrl()
    {
        var custom = StreamDestinationLibrary.Hoisted(CustomLegacy(name: ""));
        Assert.Equal("rtmps://x/app", StreamDestinationLibrary.DisplayName(custom));
        custom.Name = "Resi";
        Assert.Equal("Resi", StreamDestinationLibrary.DisplayName(custom));
    }

    [Fact]
    public void HoistingKeepsCustomIds()
    {
        var custom = CustomLegacy();
        var hoisted = StreamDestinationLibrary.Hoisted(custom);
        Assert.Equal(custom.Id, hoisted.Id);
        Assert.Equal("rtmps://x/app", hoisted.Url);
        Assert.Equal("k", hoisted.StreamKey);
    }

    [Fact]
    public void PlanDropsUnfinishedDedupesCustomAndInfersKind()
    {
        var preset = new StreamRecordPreset
        {
            Id = "p", Name = "Sunday",
            Destinations = [CustomLegacy(name: "Resi"), CustomLegacy(name: "Resi again"), CustomLegacy(name: "Decoder", url: "", key: null)],
        };
        var plan = StreamDestinationLibrary.Plan(preset, new Dictionary<string, string>());
        Assert.Equal(StreamPresetKind.Stream, plan.Kind);
        Assert.Equal(["Sunday: removed a destination that could not stream — Decoder: no server URL"], plan.Dropped);
        Assert.Single(plan.DestinationIds);
        Assert.Single(plan.Records);
        Assert.Equal(plan.DestinationIds[0], plan.Records[0].Id);

        var reuse = StreamDestinationLibrary.Plan(
            new StreamRecordPreset { Id = "q", Name = "Q", Destinations = [CustomLegacy(name: "R")] },
            new Dictionary<string, string> { [StreamDestinationLibrary.CustomKey(StreamTransport.Rtmps, "rtmps://x/app", "k")] = "existing" });
        Assert.Empty(reuse.Records);
        Assert.Equal(["existing"], reuse.DestinationIds);

        var empty = StreamDestinationLibrary.Plan(new StreamRecordPreset { Id = "r", Name = "Rec" }, new Dictionary<string, string>());
        Assert.Equal(StreamPresetKind.RecordOnly, empty.Kind);
        var allDropped = StreamDestinationLibrary.Plan(
            new StreamRecordPreset { Id = "s", Name = "S", Destinations = [CustomLegacy(name: "Decoder", url: "", key: null)] }, new Dictionary<string, string>());
        Assert.Equal(StreamPresetKind.Stream, allDropped.Kind);
    }

    [Fact]
    public void SessionDestinationCarriesTheDisplayName()
    {
        var custom = StreamDestinationLibrary.SessionDestination(StreamDestinationLibrary.Hoisted(CustomLegacy()), "Resi");
        Assert.Equal("Resi", custom.Name);
        Assert.Null(custom.Privacy);
        Assert.Equal("rtmps://x/app", custom.Url);
    }

    [Fact]
    public void LibraryMigrationIsIdempotentWritesKindsAndKeepsKeysOutOfTheJson()
    {
        var (store, vault) = Library();
        var resi = CustomLegacy(key: "resi-secret");
        var north = CustomLegacy(name: "North", url: "rtmps://n/app", key: "north-secret");
        store.Save(new StreamRecordPreset { Id = "sunday", Name = "Sunday", Destinations = [resi, north, CustomLegacy(name: "Decoder", url: "", key: null)] });
        store.Save(new StreamRecordPreset { Id = "archive", Name = "Archive" });

        var first = StreamDestinationLibrary.MigrateStreamDestinations(store, vault);
        Assert.Equal(2, first.Presets);
        Assert.Single(first.Dropped);
        var sunday = store.Get<StreamRecordPreset>("sunday")!;
        Assert.Empty(sunday.Destinations);
        Assert.Equal(StreamPresetKind.Stream, sunday.PresetKind);
        Assert.Equal(2, sunday.DestinationIds!.Count);
        Assert.Equal(StreamPresetKind.RecordOnly, store.Get<StreamRecordPreset>("archive")!.PresetKind);
        Assert.Equal(2, store.Count<StreamDestination>());

        var second = StreamDestinationLibrary.MigrateStreamDestinations(store, vault);
        Assert.Equal(0, second.Presets);
        Assert.Empty(second.Dropped);
        Assert.Equal(2, store.Count<StreamDestination>());

        // The secrets are in the vault, not in any file the library wrote (or in memory there).
        foreach (var file in Directory.EnumerateFiles(store.Root, "*", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("resi-secret", text);
            Assert.DoesNotContain("north-secret", text);
        }
        Assert.All(store.All<StreamDestination>(), d => Assert.Null(d.StreamKey));
        Assert.Equal("resi-secret", vault.Get(resi.Id));
        Assert.Equal("resi-secret", StreamDestinationLibrary.Resolve(store, vault, resi.Id)!.StreamKey);

        var resolved = StreamDestinationLibrary.Resolve(store, vault, store.Get<StreamRecordPreset>("sunday")!);
        Assert.Equal(["resi-secret", "north-secret"], resolved.Select(d => d.StreamKey));
        Assert.Equal(["Resi", "North"], resolved.Select(d => d.Name));
        Assert.Empty(StreamDestinationLibrary.Resolve(store, vault, store.Get<StreamRecordPreset>("archive")!));
    }

    [Fact]
    public void SavingMovesTheKeyAndNullLeavesItAlone()
    {
        var (store, vault) = Library();
        var destination = new StreamDestination { Id = "D1", Name = "YouTube", Transport = StreamTransport.Rtmp, Url = "rtmp://a.rtmp.youtube.com/live2", StreamKey = "abcd-1234" };
        StreamDestinationLibrary.SaveDestination(store, vault, destination);
        Assert.DoesNotContain("abcd-1234", File.ReadAllText(store.DocumentPath(typeof(StreamDestination), "D1")));
        Assert.Equal("abcd-1234", destination.StreamKey);

        var renamed = StreamDestinationLibrary.Resolve(store, vault, "D1")!;
        renamed.StreamKey = null;
        renamed.Name = "YouTube (main)";
        StreamDestinationLibrary.SaveDestination(store, vault, renamed);
        Assert.Equal("abcd-1234", StreamDestinationLibrary.Resolve(store, vault, "D1")!.StreamKey);

        renamed.StreamKey = "";
        StreamDestinationLibrary.SaveDestination(store, vault, renamed);
        Assert.Null(StreamDestinationLibrary.Resolve(store, vault, "D1")!.StreamKey);

        StreamDestinationLibrary.DeleteDestination(store, vault, "D1");
        Assert.Null(store.Get<StreamDestination>("D1"));
    }

    [Fact]
    public void KeysWrittenByTheMacStillWorkAndCanBeScrubbed()
    {
        var (store, vault) = Library();
        store.Save(new StreamDestination { Id = "M1", Name = "From the Mac", Transport = StreamTransport.Rtmps, Url = "rtmps://x/app", StreamKey = "mac-key" });
        Assert.Equal("mac-key", StreamDestinationLibrary.Resolve(store, vault, "M1")!.StreamKey);
        Assert.Equal(1, StreamDestinationLibrary.ScrubStreamKeys(store, vault));
        Assert.DoesNotContain("mac-key", File.ReadAllText(store.DocumentPath(typeof(StreamDestination), "M1")));
        Assert.Equal("mac-key", StreamDestinationLibrary.Resolve(store, vault, "M1")!.StreamKey);
        Assert.Equal(0, StreamDestinationLibrary.ScrubStreamKeys(store, vault));
    }

    [Fact]
    public void ResolvedKindInfersOldPresets()
    {
        Assert.True(new StreamRecordPreset { Id = "a", Name = "A" }.IsRecordOnly());
        Assert.False(new StreamRecordPreset { Id = "b", Name = "B", DestinationIds = ["x"] }.IsRecordOnly());
        Assert.True(new StreamRecordPreset { Id = "c", Name = "C", DestinationIds = ["x"], PresetKind = StreamPresetKind.RecordOnly }.IsRecordOnly());
    }
}

public sealed class StreamKeyVaultTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "StreamKeyVault-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void RoundTripsAndNeverStoresPlainText()
    {
        var vault = new StreamKeyVault(folder);
        Assert.Null(vault.Get("D1"));
        vault.Set("D1", "live_1234_secret");
        Assert.Equal("live_1234_secret", vault.Get("D1"));
        Assert.True(vault.Contains("D1"));
        var bytes = File.ReadAllBytes(Directory.GetFiles(folder).Single());
        Assert.DoesNotContain("live_1234_secret", Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain("live_1234_secret", Encoding.Unicode.GetString(bytes));

        vault.Set("D1", "rotated");
        Assert.Equal("rotated", new StreamKeyVault(folder).Get("D1"));
        vault.Set("D1", null);
        Assert.Null(vault.Get("D1"));
        Assert.False(vault.Contains("D1"));
    }

    [Fact]
    public void OnlyThisAppsEntropyDecrypts()
    {
        var vault = new StreamKeyVault(folder);
        vault.Set("D2", "secret");
        var sealedKey = File.ReadAllBytes(Directory.GetFiles(folder).Single());
        Assert.ThrowsAny<CryptographicException>(() => ProtectedData.Unprotect(sealedKey, null, DataProtectionScope.CurrentUser));
    }

    [Fact]
    public void DamagedFilesReadAsNoKey()
    {
        var vault = new StreamKeyVault(folder);
        vault.Set("D3", "secret");
        File.WriteAllBytes(Directory.GetFiles(folder).Single(), [1, 2, 3]);
        Assert.Null(vault.Get("D3"));
    }

    [Fact]
    public void OddIdsMakeSafeFileNames()
    {
        var vault = new StreamKeyVault(folder);
        vault.Set("../../evil", "x");
        Assert.Equal("x", vault.Get("../../evil"));
        Assert.All(Directory.GetFiles(folder), f => Assert.Equal(folder, Path.GetDirectoryName(f)));
    }
}
