using MxU.Slides.Production.Actions;
using MxU.Slides.Production.Devices;
using MxU.Slides.Production.Lighting;

namespace MxU.Slides.Production.Tests;

// What the "Production (Windows)" editor shows and offers.
public class ProductionEditingTests
{
    private static readonly LightingShow Lights = new()
    {
        Looks = [new LightingLook { Id = "sermon", Name = "Sermon", FadeSeconds = 3 }, new LightingLook { Id = "walk", Name = "Walk-in" }],
        Cues = [new LightingCue { Id = "q1", LookId = "walk" }, new LightingCue { Id = "q2", Name = "Message", LookId = "sermon" }],
    };

    private static readonly ProductionContext Context = new(
        [new DeviceInfo("atem", "Main ATEM", DeviceKind.Atem), new DeviceInfo("obs", "Stream PC", DeviceKind.Obs)],
        (_, kind) => kind switch
        {
            DeviceKind.Atem => DeviceCatalog.Empty with { Inputs = [new(1, "Pulpit"), new(2, "Wide")], Macros = [new(4, "Walk-in")] },
            DeviceKind.Obs => DeviceCatalog.Empty with { Scenes = ["Worship", "Sermon"] },
            _ => DeviceCatalog.Empty,
        },
        Lights);

    private static string Label(ProductionKind kind, Action<ProductionAction> fill)
    {
        var step = new ProductionAction { Kind = kind };
        fill(step);
        return ProductionEditing.Label(step, Context);
    }

    [Fact]
    public void EachStepReadsAsASentence()
    {
        Assert.Equal("ATEM: Program Wide", Label(ProductionKind.AtemProgram, s => s.Source = 2));
        Assert.Equal("ATEM: Preview source 3010 (M/E 2)", Label(ProductionKind.AtemPreview, s => { s.Source = 3010; s.MixEffect = 2; }));
        Assert.Equal("ATEM: Run macro 4 (Walk-in)", Label(ProductionKind.AtemMacro, s => s.Number = 4));
        Assert.Equal("ATEM: DSK 1 off", Label(ProductionKind.AtemDownstreamKey, s => s.On = false));
        Assert.Equal("OBS: Scene Worship (Fade)", Label(ProductionKind.ObsProgramScene, s => { s.Scene = "Worship"; s.Transition = "Fade"; }));
        Assert.Equal("OBS: Stop streaming", Label(ProductionKind.ObsStreaming, s => s.On = false));
        Assert.Equal("vMix: Cut", Label(ProductionKind.VmixCut, _ => { }));
        Assert.Equal("vMix: Overlay 2 in (Lower third)", Label(ProductionKind.VmixOverlay, s => { s.Number = 2; s.Input = "Lower third"; }));
        Assert.Equal("Lights: Sermon, 3 s fade", Label(ProductionKind.LightsLook, s => { s.LookId = "sermon"; s.FadeSeconds = 3; }));
        Assert.Equal("Lights: Walk-in, on top", Label(ProductionKind.LightsLook, s => { s.LookId = "walk"; s.OnTop = true; }));
        Assert.Equal("Lights: GO (next cue)", Label(ProductionKind.LightsCue, _ => { }));
        Assert.Equal("Lights: Cue 2 · Message", Label(ProductionKind.LightsCue, s => s.CueId = "q2"));
        Assert.Equal("Lights: Blackout on/off", Label(ProductionKind.LightsBlackout, _ => { }));
        Assert.Equal("ATEM · PROGRAM INPUT", ProductionEditing.Header(new ProductionAction { Kind = ProductionKind.AtemProgram }));
    }

    [Fact]
    public void AStepShowsItsDeviceThenItsSettings()
    {
        var fields = ProductionEditing.Fields(new ProductionAction { Kind = ProductionKind.AtemProgram });
        Assert.Equal(["Device", "Source", "M/E"], fields.Select(f => f.Label));
        var source = Assert.IsType<SuggestField>(fields[1]);
        var step = new ProductionAction { Kind = ProductionKind.AtemProgram };
        source.Set(step, "2 · Wide");
        Assert.Equal(2, step.Source);
        Assert.Equal(["1 · Pulpit", "2 · Wide"], source.Suggestions(Context, step).Select(c => c.Name));
        Assert.Equal(["Look", "Fade", "How"], ProductionEditing.Fields(new ProductionAction { Kind = ProductionKind.LightsLook }).Select(f => f.Label));
        Assert.Empty(ProductionEditing.Fields(new ProductionAction { KindName = "fromTheFuture" }));
    }

    [Fact]
    public void NewStepsStartFromWhatTheDevicesOffer()
    {
        Assert.Equal(1, ProductionEditing.New(ProductionKind.AtemProgram, Context).Source);
        Assert.Equal("Worship", ProductionEditing.New(ProductionKind.ObsProgramScene, Context).Scene);
        Assert.Equal("sermon", ProductionEditing.New(ProductionKind.LightsLook, Context).LookId);
        Assert.Equal("walk", ProductionEditing.New(ProductionKind.LightsLook, Context, "walk").LookId);
        Assert.Null(ProductionEditing.New(ProductionKind.AtemCut, Context).DeviceId);   // one ATEM: no need to name it
    }

    [Fact]
    public void TheAddMenuGroupsTheStepsByDevice()
    {
        var menu = ProductionEditing.AddMenu(Context);
        Assert.Equal(["ATEM", "OBS", "vMix", "Lights"], menu.Select(g => g.Title));
        var looks = menu[3].Items[0].Children!;
        Assert.Equal(["Sermon", "Walk-in"], looks.Select(l => l.Title));
        Assert.Equal("walk", looks[1].Make!().LookId);
        Assert.Null(ProductionEditing.AddMenu(ProductionContext.Empty)[3].Items[0].Children![0].Make);
    }

    [Fact]
    public void TypedNumbersAndDelaysAreRead()
    {
        Assert.Equal("2", ProductionEditing.Leading("2 · Camera 2"));
        Assert.Equal("Camera 2", ProductionEditing.Leading(" Camera 2 "));
        Assert.Equal(1.5, ProductionEditing.ParseSeconds("1.5 s"));
        Assert.Equal(1.5, ProductionEditing.ParseSeconds("1,5"));
        Assert.Null(ProductionEditing.ParseSeconds("soon"));
        Assert.Equal(3600, ProductionEditing.ParseSeconds("99999"));
        var step = new ProductionAction();
        ProductionEditing.SetDelay(step, 0);
        Assert.Null(step.DelaySeconds);
        ProductionEditing.SetDelay(step, 2.04);
        Assert.Equal(2, step.DelaySeconds);
    }

    [Fact]
    public void KindsAreStoredCamelCase()
    {
        var step = new ProductionAction { Kind = ProductionKind.AtemFadeToBlack };
        Assert.Equal("atemFadeToBlack", step.KindName);
        Assert.Equal(DeviceKind.Atem, ProductionAction.DeviceFor(ProductionKind.AtemDownstreamKey));
        Assert.Equal(DeviceKind.Obs, ProductionAction.DeviceFor(ProductionKind.ObsSourceVisible));
        Assert.Equal(DeviceKind.Vmix, ProductionAction.DeviceFor(ProductionKind.VmixActive));
        Assert.Null(ProductionAction.DeviceFor(ProductionKind.LightsLook));
    }
}
