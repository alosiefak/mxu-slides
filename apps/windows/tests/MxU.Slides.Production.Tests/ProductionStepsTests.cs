using System.Diagnostics;
using MxU.Slides.Core.Library;
using MxU.Slides.Production.Actions;
using MxU.Slides.Production.Devices;
using MxU.Slides.Production.Lighting;
using MxU.Slides.Production.Simulators;
using MxU.Slides.Production.Tests.Fakes;

namespace MxU.Slides.Production.Tests;

// Production steps are kept in the library's Windows extras, never in a shared document,
// and run when their owner runs, each after its own delay.
public sealed class ProductionStepsTests : IDisposable
{
    private readonly Wait.TempFolder folder = new();

    public void Dispose() => folder.Dispose();

    private static ProductionAction Step(ProductionKind kind, double? delay = null, Action<ProductionAction>? fill = null)
    {
        var step = new ProductionAction { Kind = kind, DelaySeconds = delay };
        fill?.Invoke(step);
        return step;
    }

    [Fact]
    public void StepsAreKeptInTheExtrasFolderWhereTheMacNeverLooks()
    {
        var extras = new WindowsExtras(folder.Path);
        var steps = new ProductionSteps(extras);
        steps.Save("slide-1", [Step(ProductionKind.AtemProgram, fill: s => s.Source = 2), Step(ProductionKind.LightsLook, 1.5, s => s.LookId = "sermon")]);

        var file = Path.Combine(folder.Path, "Windows extras", "production", "slide-1.json");
        Assert.True(File.Exists(file));
        var json = File.ReadAllText(file);
        Assert.Contains("\"kind\": \"atemProgram\"", json);
        Assert.Contains("\"delaySeconds\": 1.5", json);
        Assert.DoesNotContain("\"mixEffect\"", json);   // unset fields aren't written

        // A fresh store (a restart) reads them back.
        var again = new ProductionSteps(new WindowsExtras(folder.Path));
        again.Preload();
        var read = again.For("slide-1");
        Assert.Equal([ProductionKind.AtemProgram, ProductionKind.LightsLook], read.Select(s => s.Kind));
        Assert.Equal(2, read[0].Source);
        Assert.Equal(["slide-1"], again.Owners());

        steps.Save("slide-1", []);
        Assert.False(File.Exists(file));
        Assert.Empty(steps.For("slide-1"));
    }

    [Fact]
    public void AKindFromANewerVersionIsSkippedNotTheWholeList()
    {
        var path = Path.Combine(folder.Path, "Windows extras", "production");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "combo-1.json"), """
            { "Value": [ { "id": "a", "kind": "teleportCamera" }, { "id": "b", "kind": "lightsBlackout", "on": true } ], "Basis": null }
            """);
        var steps = new ProductionSteps(new WindowsExtras(folder.Path)).For("combo-1");
        Assert.Equal(2, steps.Count);
        Assert.Null(steps[0].Kind);
        Assert.Equal(ProductionKind.LightsBlackout, steps[1].Kind);
        Assert.StartsWith("Unknown step", ProductionEditing.Label(steps[0], ProductionContext.Empty));
    }

    [Fact]
    public void TheRunnerHonoursEachStepsDelayAndKeepsListOrder()
    {
        var clock = new ManualClock();
        var steps = new ProductionSteps(new WindowsExtras(folder.Path));
        var ran = new List<string>();
        var runner = new ProductionRunner(steps, s => ran.Add(s.Id), clock);
        steps.Save("cue-1",
        [
            new ProductionAction { Id = "now", Kind = ProductionKind.AtemCut },
            new ProductionAction { Id = "two-a", Kind = ProductionKind.ObsStreaming, DelaySeconds = 2 },
            new ProductionAction { Id = "two-b", Kind = ProductionKind.LightsOff, DelaySeconds = 2 },
            new ProductionAction { Id = "one", Kind = ProductionKind.VmixCut, DelaySeconds = 1 },
        ]);
        runner.OwnerRan("cue-1");
        Assert.Equal(["now"], ran);
        Assert.Equal(2, runner.Pending);
        clock.Advance(1);
        Assert.Equal(["now", "one"], ran);
        clock.Advance(1);
        Assert.Equal(["now", "one", "two-a", "two-b"], ran);
        Assert.Equal(0, runner.Pending);

        runner.OwnerRan("someone-without-steps");
        Assert.Equal(4, ran.Count);
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public void ClearAllDropsTheStepsStillWaiting()
    {
        var clock = new ManualClock();
        var steps = new ProductionSteps(new WindowsExtras(folder.Path));
        var ran = new List<string>();
        var runner = new ProductionRunner(steps, s => ran.Add(s.Id), clock);
        steps.Save("slide", [new ProductionAction { Id = "later", Kind = ProductionKind.AtemCut, DelaySeconds = 3 }]);
        runner.OwnerRan("slide");
        runner.CancelPending();
        clock.Advance(5);
        Assert.Empty(ran);
        Assert.Equal(0, runner.Pending);
    }

    [Fact]
    public void AStepThatThrowsDoesNotStopTheOthers()
    {
        var steps = new ProductionSteps(new WindowsExtras(folder.Path));
        var ran = new List<string>();
        var runner = new ProductionRunner(steps, s =>
        {
            if (s.Id == "bad") throw new InvalidOperationException("boom");
            ran.Add(s.Id);
        }, new ManualClock());
        runner.Run([new ProductionAction { Id = "bad", Kind = ProductionKind.AtemCut }, new ProductionAction { Id = "good", Kind = ProductionKind.AtemCut }]);
        Assert.Equal(["good"], ran);
    }

    // MARK: The hub

    private ProductionHub Hub(ManualClock? clock = null) =>
        new(new WindowsExtras(folder.Path), Path.Combine(folder.Path, "production.json"), new DeviceSecrets(Path.Combine(folder.Path, "secrets")), clock);

    [Fact]
    public async Task AStepForADeviceThisPCHasntGotIsSkippedAtOnce()
    {
        using var hub = Hub();
        hub.Start();
        var watch = Stopwatch.StartNew();
        Assert.False(await hub.PerformAsync(Step(ProductionKind.ObsProgramScene, fill: s => s.Scene = "Worship")));
        Assert.False(await hub.PerformAsync(Step(ProductionKind.AtemCut)));
        Assert.True(watch.ElapsedMilliseconds < 100);
    }

    [Fact]
    public async Task AMissingDeviceNeverHoldsTheShowUp()
    {
        using var hub = Hub();
        hub.Start();
        var atem = DeviceSettings.New(DeviceKind.Atem);
        atem.Host = "127.0.0.1";
        atem.Port = Wait.UnusedPort();
        var obs = Wait.Device(DeviceKind.Obs, Wait.UnusedPort(System.Net.Sockets.SocketType.Stream));
        hub.SaveDevice(atem);
        hub.SaveDevice(obs);
        hub.Steps.Save("slide", [.. Enumerable.Range(0, 20).Select(_ => Step(ProductionKind.AtemCut)), Step(ProductionKind.ObsStreaming)]);

        // Firing the slide hands the steps over and returns straight away…
        var watch = Stopwatch.StartNew();
        hub.Runner.OwnerRan("slide");
        Assert.True(watch.ElapsedMilliseconds < 50, $"took {watch.ElapsedMilliseconds} ms");
        // …and each device gives up on its own quickly.
        watch.Restart();
        Assert.False(await hub.PerformAsync(Step(ProductionKind.AtemCut)));
        Assert.False(await hub.PerformAsync(Step(ProductionKind.ObsStreaming)));
        Assert.True(watch.ElapsedMilliseconds < 2000, $"took {watch.ElapsedMilliseconds} ms");
        await Wait.Until(() => hub.Devices.All(d => d.Status.State == DeviceState.Problem), 5000, "both lights to go red");
    }

    [Fact]
    public async Task AStepReachesTheSwitcherWhenItsSlideFires()
    {
        using var simulator = new AtemSimulator();
        using var hub = Hub();
        hub.Start();
        hub.SaveDevice(Wait.Device(DeviceKind.Atem, simulator.Port));
        await Wait.Until(() => hub.Devices.Single().Status.IsOnline, what: "the ATEM to connect");
        hub.Steps.Save("slide", [Step(ProductionKind.AtemProgram, fill: s => s.Source = 3)]);
        hub.Runner.OwnerRan("slide");
        var program = simulator.WaitFor("CPgI");
        Assert.NotNull(program);
        Assert.Equal([0, 0, 0, 3], program.Value.Data);
        // The editors can name the inputs the switcher reported.
        Assert.Equal("Camera 3", ProductionEditing.SourceName(hub.Context(), null, 3));
    }

    [Fact]
    public async Task LightingStepsDriveTheLookAndTheLibraryKeepsTheLooks()
    {
        using var hub = Hub();
        hub.Start();
        hub.SaveLighting(new LightingShow { Looks = [new LightingLook { Id = "sermon", Name = "Sermon", FadeSeconds = 0, Levels = [new(1, 1, 180)] }] });
        Assert.Equal("Sermon", Assert.Single(hub.Lighting.Show.Looks).Name);
        Assert.True(await hub.PerformAsync(Step(ProductionKind.LightsLook, fill: s => s.LookId = "sermon")));
        Assert.Equal("sermon", hub.Lighting.MainLookId);
        Assert.True(await hub.PerformAsync(Step(ProductionKind.LightsMaster, fill: s => s.Level = 50)));
        Assert.Equal(90, hub.Lighting.Frame()[1][0]);
        Assert.True(await hub.PerformAsync(Step(ProductionKind.LightsBlackout, fill: s => s.On = true)));
        Assert.True(hub.Lighting.Blackout);
        Assert.False(await hub.PerformAsync(Step(ProductionKind.LightsLook, fill: s => s.LookId = "gone")));
        Assert.True(File.Exists(Path.Combine(folder.Path, "Windows extras", "lighting", "show.json")));
    }

    [Fact]
    public void DevicesAreThisPCsAndPasswordsAreKeptApart()
    {
        using (var hub = Hub())
        {
            var obs = DeviceSettings.New(DeviceKind.Obs);
            hub.SaveDevice(obs, "hunter2");
            Assert.Equal("hunter2", hub.Secrets.Get(obs.Id));
        }
        var json = File.ReadAllText(Path.Combine(folder.Path, "production.json"));
        Assert.Contains("\"kind\": \"obs\"", json);
        Assert.DoesNotContain("hunter2", json);
        using var reopened = Hub();
        var device = Assert.Single(reopened.Setups);
        Assert.Equal((DeviceKind.Obs, "127.0.0.1", 4455), (device.Kind, device.Host, device.EffectivePort));
        reopened.RemoveDevice(device.Id);
        Assert.Empty(reopened.Setups);
        Assert.Null(reopened.Secrets.Get(device.Id));
    }
}
