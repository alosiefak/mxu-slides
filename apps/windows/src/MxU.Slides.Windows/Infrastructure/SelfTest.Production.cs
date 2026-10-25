using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using MxU.Slides.Core.Model;
using MxU.Slides.LocalApi;
using MxU.Slides.Production.Actions;
using MxU.Slides.Production.Devices;
using MxU.Slides.Production.Lighting;
using MxU.Slides.Production.Simulators;
using MxU.Slides.Windows.Views;
using MxU.Slides.Windows.Views.Production;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>
/// The Phase 6 production checks, against pretend devices on 127.0.0.1 (an ATEM, OBS with a
/// password, vMix, and an Art-Net capture), never real ones: add the devices through the
/// device dialog and see their lights go green; build a lighting look and a cue in the
/// lighting window; give a slide production steps in its Slide Actions window and fire it
/// (the camera cut, the OBS scene, the vMix cut half a second later, the lights); run a combo
/// that has only production steps; and check a missing device costs the show nothing.
/// Pictures: 90-production-settings, 91-production-device, 92-production-lighting,
/// 93-production-add-menu, 94-production-slide-actions.
/// </summary>
public sealed partial class SelfTest
{
    private async Task ProductionCheck()
    {
        using var atem = new AtemSimulator("Pulpit", "Wide", "Choir");
        using var obs = new ObsSimulator("selftest");
        using var vmix = new VmixSimulator();
        using var artNet = new UdpCapture();
        var hub = model.Production;
        var before = hub.Setups.Select(d => d.Id).ToHashSet();
        string? deckId = null;
        string? comboId = null;
        try
        {
            await ProductionDevicesCheck(atem, obs, vmix, artNet);
            var look = await LightingWindowCheck();
            deckId = await ProductionSlideCheck(atem, obs, vmix, artNet, look);
            comboId = await ProductionComboCheck(atem);
            await MissingDeviceCheck();
            await ProductionApiCheck(atem);
        }
        finally
        {
            model.ClearAllCommand.Execute(null);
            foreach (var device in hub.Setups.Where(d => !before.Contains(d.Id))) hub.RemoveDevice(device.Id);
            hub.Lighting.SetBlackout(false);
            hub.Lighting.SetGrandMaster(100);
            hub.Lighting.SetProgrammer(null);
            hub.SaveLighting(new LightingShow());
            if (deckId is not null)
            {
                foreach (var slide in model.Library.Get<Presentation>(deckId)?.Slides ?? []) hub.Steps.Save(slide.Id, []);
                model.Library.Delete<Presentation>(deckId);
            }
            if (comboId is not null)
            {
                hub.Steps.Save(comboId, []);
                model.Automation.DeleteActionCombo(comboId);
            }
        }
    }

    /// <summary>The device dialog adds each device; the lights go green (and an Art-Net node waits, grey, until the first lighting step).</summary>
    private async Task ProductionDevicesCheck(AtemSimulator atem, ObsSimulator obs, VmixSimulator vmix, UdpCapture artNet)
    {
        void Add(DeviceKind kind, int port, string? password = null, string? name = null)
        {
            var dialog = ProductionDeviceDialog.Make(window, model, DeviceSettings.New(kind), isNew: true);
            dialog.Fill("127.0.0.1", port, password, name);
            dialog.Save();
            dialog.Close();
        }
        Add(DeviceKind.Atem, atem.Port, name: "Self-test ATEM");
        Add(DeviceKind.Obs, obs.Port, "selftest", "Self-test OBS");
        Add(DeviceKind.Vmix, vmix.Port, name: "Self-test vMix");
        Add(DeviceKind.ArtNet, artNet.Port, name: "Self-test Art-Net");
        var hub = model.Production;
        // Usually well under a second; generous, because the PC may be busy with other work.
        var online = await Until(() => hub.Devices.Count(d => d.Status.IsOnline), n => n >= 3, 20000);
        Check("an ATEM, OBS (with its password) and vMix connect: their lights go green", online >= 3);
        Check("the OBS password is kept encrypted, not in production.json",
            hub.Setups.FirstOrDefault(d => d.Kind == DeviceKind.Obs) is { } obsDevice && hub.Secrets.Get(obsDevice.Id) == "selftest"
            && !File.ReadAllText(Path.Combine(AppPaths.DataRoot, "production.json")).Contains("selftest\"", StringComparison.Ordinal));
        Check("the switcher's input names are read", hub.Devices.FirstOrDefault(d => d.Kind == DeviceKind.Atem)?.Catalog.Inputs.Select(i => i.Name).SequenceEqual(["Pulpit", "Wide", "Choir"]) == true);
        Check("an Art-Net node sends nothing before the first lighting step", artNet.Packets.IsEmpty
            && hub.Devices.FirstOrDefault(d => d.Kind == DeviceKind.ArtNet)?.Status.State == DeviceState.Idle);

        if (model.Outputs is null) return;
        var settings = new SettingsWindow(model, model.Outputs) { Owner = window };
        settings.Show();
        try
        {
            await Delay(500);
            settings.ProductionPanel.BringIntoView();
            // Each row's light repaints by itself as its device connects: wait for them, then for the layout.
            await Until(() => settings.ProductionPanel.Shown.Count(s => s.Status.IsOnline), n => n >= 3, 10000);
            settings.UpdateLayout();
            await Delay(300);
            Check("Settings › Production lists each device with its light",
                settings.ProductionPanel.Shown.Count(s => s.Name.StartsWith("Self-test", StringComparison.Ordinal)) == 4
                && settings.ProductionPanel.Shown.Count(s => s.Status.IsOnline) >= 3);
            Save(settings, "90-production-settings");
        }
        finally
        {
            settings.Close();
        }
        var editing = ProductionDeviceDialog.Make(window, model, hub.Setups.First(d => d.Kind == DeviceKind.Obs), isNew: false);
        editing.Show();
        await Delay(400);
        Save(editing, "91-production-device");
        editing.Close();
    }

    /// <summary>The lighting window makes a look with two channels and a cue for it; the look is kept in the library's extras.</summary>
    private async Task<LightingLook?> LightingWindowCheck()
    {
        var lighting = LightingWindow.Open(window, model);
        try
        {
            await Delay(500);
            var look = lighting.NewLook("Self-test sermon");
            lighting.SetChannel(1, 255);
            lighting.SetChannel(2, 128);
            lighting.AddCue();
            lighting.SaveNow();
            await Delay(400);
            Save(lighting, "92-production-lighting");
            var saved = model.Production.Lighting.Show;
            Check("the lighting window saves a look and a cue to the library",
                saved.Look(look.Id) is { Levels.Count: 2 } && saved.Cues.Any(c => c.LookId == look.Id)
                && File.Exists(Path.Combine(model.Library.Extras.Root, LightingShow.ExtrasKind, LightingShow.ExtrasOwner + ".json")));
            return saved.Look(look.Id);
        }
        finally
        {
            lighting.Close();
            await Delay(200);
            Check("closing the lighting window lets the rig go back to the looks", !model.Production.Lighting.IsActive || model.Production.Lighting.Frame().Values.All(u => u.All(v => v == 0)));
        }
    }

    /// <summary>A slide's Production steps, made in its Slide Actions window, run when it fires (each after its delay).</summary>
    private async Task<string?> ProductionSlideCheck(AtemSimulator atem, ObsSimulator obs, VmixSimulator vmix, UdpCapture artNet, LightingLook? look)
    {
        var slide = new Slide
        {
            Id = "selftest-production-slide", Name = "Sermon",
            Objects = [new SlideObject { Id = "t", ObjectKind = SlideObjectKind.Text, Name = "Text", Text = "The sermon" }],
        };
        var deck = new Presentation { Id = "selftest-production-deck", Name = "Production check", Slides = [slide] };
        model.Library.Save(deck);
        model.Open(deck, null, null);
        await Delay(300);
        var editorWindow = SlideActionsWindow.ForSlide(window, model, slide, deck);
        var section = ProductionStepsSection.Of(editorWindow?.Editor);
        Check("a slide's actions window has a Production (Windows) section", section is not null);
        if (editorWindow is null || section is null) return deck.Id;
        try
        {
            editorWindow.Show();
            await Delay(500);
            var context = model.Production.Context();
            var program = ProductionEditing.New(ProductionKind.AtemProgram, context);
            program.Source = 2;
            section.Append(program);
            var scene = ProductionEditing.New(ProductionKind.ObsProgramScene, context);
            scene.Scene = "Worship";
            section.Append(scene);
            var cut = ProductionEditing.New(ProductionKind.VmixCut, context);
            ProductionEditing.SetDelay(cut, 0.5);
            section.Append(cut);
            if (look is not null)
            {
                var lights = ProductionEditing.New(ProductionKind.LightsLook, context, look.Id);
                lights.FadeSeconds = 0;
                section.Append(lights);
            }
            await Delay(300);
            Check("the steps read as sentences, with the switcher's names",
                ProductionEditing.Label(program, model.Production.Context()) == "ATEM: Program Wide");
            var menu = section.ShowAddMenu();
            await Delay(300);
            Check("the Add menu groups the steps by device", menu.Items.OfType<MenuItem>().Select(i => i.Header as string).SequenceEqual(["ATEM", "OBS", "vMix", "Lights"]));
            if (menu.Items.OfType<MenuItem>().FirstOrDefault() is { } first)
            {
                first.IsSubmenuOpen = true;
                await Delay(300);
            }
            SaveElement(menu, "93-production-add-menu");
            menu.IsOpen = false;
            // Tall enough to show the section's top and its first cards.
            editorWindow.Height = Math.Min(1000, System.Windows.SystemParameters.WorkArea.Height - 24);
            await Delay(200);
            section.BringIntoView(new System.Windows.Rect(0, 0, Math.Max(1, section.ActualWidth), 60));
            await Delay(300);
            Save(editorWindow, "94-production-slide-actions");
        }
        finally
        {
            editorWindow.Close();
        }

        var stored = model.Production.Steps.For(slide.Id);
        var deckFile = Directory.EnumerateFiles(model.Library.Root, "*.json", SearchOption.AllDirectories)
            .FirstOrDefault(f => !f.Contains(Core.Library.WindowsExtras.FolderName, StringComparison.Ordinal) && File.ReadAllText(f).Contains(deck.Id, StringComparison.Ordinal));
        Check("the production steps are kept in Windows extras, and the shared slide document never names them",
            stored.Count == (look is null ? 3 : 4) && deckFile is not null && !File.ReadAllText(deckFile).Contains("atemProgram", StringComparison.Ordinal)
            && model.Library.Get<Presentation>(deck.Id)?.Slides[0].Actions is null);

        // The devices are connected before the slide fires (a busy PC can make OBS slow to sign in).
        await Until(() => model.Production.Devices.Count(d => d.Status.IsOnline), n => n >= 3, 15000);
        while (atem.Commands.TryDequeue(out _)) { }
        while (obs.Requests.TryDequeue(out _)) { }
        while (vmix.Functions.TryDequeue(out _)) { }
        var watch = Stopwatch.StartNew();
        model.GoLiveCommand.Execute(model.Slides.FirstOrDefault(s => s.Slide.Id == slide.Id));
        var fired = watch.ElapsedMilliseconds;
        await Delay(250);
        var cutEarly = !vmix.Functions.IsEmpty;
        var cameraCut = await Until(() => atem.Commands.FirstOrDefault(c => c.Name == "CPgI").Data, d => d is not null, 2000);
        var obsScene = await Until(() => obs.Requests.FirstOrDefault(r => r.Type == "SetCurrentProgramScene").Data, d => d is not null, 2000);
        var vmixCut = await Until(() => vmix.Functions.FirstOrDefault(), f => f is not null, 2000);
        Check($"firing the slide switches the ATEM to Wide (source 2) and OBS to Worship ({fired} ms on the UI thread)",
            cameraCut is [0, 0, 0, 2] && obsScene?["sceneName"]?.GetValue<string>() == "Worship");
        Check("the vMix cut waits its half second", !cutEarly && vmixCut == "/api/?Function=Cut");
        if (look is not null)
        {
            var packet = artNet.WaitFor(p => p.Length == 18 + 512 && p[18] == 255 && p[19] == 128, 2000);
            Check("the lights go to the slide's look (Art-Net: channel 1 at 255, 2 at 128)", packet is not null);
        }
        model.ClearAllCommand.Execute(null);
        await Delay(200);
        return deck.Id;
    }

    /// <summary>A combo with only production steps (a cut) runs them from the Combos tab, MIDI or the Local API.</summary>
    private async Task<string?> ProductionComboCheck(AtemSimulator atem)
    {
        var comboId = model.Automation.CreateActionCombo("Self-test camera cut");
        if (comboId is null) return null;
        model.SaveProductionSteps(comboId, [ProductionEditing.New(ProductionKind.AtemCut, model.Production.Context())]);
        while (atem.Commands.TryDequeue(out _)) { }
        model.RunActionCombo(comboId);
        var cut = await Until(() => atem.Commands.Any(c => c.Name == "DCut"), done => done, 2000);
        Check("a combo with only production steps runs them (the ATEM cuts)", cut);
        return comboId;
    }

    /// <summary>A step for a switcher that isn't there costs the show nothing: the slide goes live at once and the light goes red.</summary>
    private async Task MissingDeviceCheck()
    {
        int port;
        using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            port = ((IPEndPoint)probe.LocalEndPoint!).Port;
        }
        var missing = DeviceSettings.New(DeviceKind.Atem);
        missing.Name = "Self-test missing ATEM";
        missing.Host = "127.0.0.1";
        missing.Port = port;
        model.Production.SaveDevice(missing);
        var step = ProductionEditing.New(ProductionKind.AtemCut, model.Production.Context());
        step.DeviceId = missing.Id;
        // Handing steps over (what firing a slide does) only queues them: the UI thread carries straight on.
        var handOver = Stopwatch.StartNew();
        model.Production.Runner.Run([step.Copy(), step.Copy(), step.Copy()]);
        var handed = handOver.ElapsedMilliseconds;
        var (done, took) = await TimedOnItsOwnThread(() => model.Production.PerformAsync(step));
        var red = await Until(() => model.Production.StatusOf(missing.Id).State, s => s == DeviceState.Problem, 10000);
        Check($"a missing switcher costs the show nothing (handed over in {handed} ms, skipped in {took} ms) and its light goes red",
            handed < 200 && !done && took is >= 0 and < 1500 && red == DeviceState.Problem);
        model.Production.RemoveDevice(missing.Id);
    }

    /// <summary>
    /// Hands a step over and times its answer on a thread of its own, neither the UI's (a busy
    /// screen would count against the device) nor the thread pool's (a busy PC would). The answer
    /// comes from the device's own thread. Took is -1 when there was no answer within 5 s.
    /// </summary>
    private static Task<(bool Done, long Took)> TimedOnItsOwnThread(Func<Task<bool>> handOver)
    {
        var result = new TaskCompletionSource<(bool, long)>(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            try
            {
                var watch = Stopwatch.StartNew();
                var task = handOver();
                var answered = task.Wait(TimeSpan.FromSeconds(5));
                result.TrySetResult(answered ? (task.Result, watch.ElapsedMilliseconds) : (false, -1));
            }
            catch (Exception)
            {
                result.TrySetResult((false, -1));
            }
        }) { IsBackground = true, Name = "Self-test timing" }.Start();
        return result.Task;
    }

    /// <summary>The Local API's Windows-only routes report the devices and fire a step.</summary>
    private async Task ProductionApiCheck(AtemSimulator atem)
    {
        var api = (IProductionApi)model;
        var status = api.ProductionStatus();
        Check("GET /v1/windows/production lists the devices with their lights",
            status["devices"] is JsonArray devices && devices.OfType<JsonObject>().Any(d => d["kind"]?.GetValue<string>() == "atem" && d["state"]?.GetValue<string>() == "online"));
        while (atem.Commands.TryDequeue(out _)) { }
        var fired = await api.FireProductionStep(new JsonObject { ["kind"] = "atemMacro", ["number"] = 4 });
        var macro = await Until(() => atem.Commands.FirstOrDefault(c => c.Name == "MAct").Data, d => d is not null, 2000);
        Check("POST /v1/windows/production/steps/fire runs ATEM macro 4", fired && macro is [0, 3, 0, 0]);
    }
}
