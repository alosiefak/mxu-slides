using System.Text.Json.Nodes;
using System.Windows;
using MxU.Slides.Core.Json;
using MxU.Slides.LocalApi;
using MxU.Slides.Production;
using MxU.Slides.Production.Actions;
using MxU.Slides.Production.Devices;
using MxU.Slides.Windows.Infrastructure;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// Production control (Windows only, Phase 6): cameras through an ATEM, OBS, vMix and DMX
/// lights. The <see cref="ProductionHub"/> (in MxU.Slides.Production) does the work on its own
/// threads; this starts it, gives its runner to the action router so production steps run
/// when their slide, combo, trigger or timeline cue runs, and answers the Local API's
/// /v1/windows/production routes. Devices belong to this PC (production.json beside
/// settings.json; passwords encrypted in the local folder); the steps and the lighting looks
/// belong to the library (its Windows extras, where the Mac never looks).
/// </summary>
public sealed partial class MainViewModel : IProductionApi
{
    private ProductionHub? production;

    /// <summary>This PC's production devices, the lights and the production steps.</summary>
    public ProductionHub Production => production ??= CreateProduction();

    private ProductionHub CreateProduction()
    {
        var hub = new ProductionHub(
            Library.Extras,
            Path.Combine(AppPaths.DataRoot, "production.json"),
            new DeviceSecrets(Path.Combine(AppPaths.LocalRoot, "ProductionSecrets")));
        hub.Noted += line => Log.Info($"Production: {line}");
        hub.Runner.Noted += (crumb, detail) => Log.Info($"Production: {crumb} {detail}");
        return hub;
    }

    /// <summary>
    /// Connects the devices in the background and hooks production steps into show automation.
    /// Returns at once: a device that's slow to answer never holds up the start.
    /// </summary>
    private void StartProduction()
    {
        try
        {
            Production.Start();
            Actions.ExtraSteps = Production.Runner;
            // Let receivers know this source is going (sACN), and stop the device threads.
            if (Application.Current is { } app) app.Exit += (_, _) => production?.Dispose();
        }
        catch (Exception ex)
        {
            // Production is extra: the show must start without it.
            Log.Error("Production control didn't start", ex);
        }
    }

    /// <summary>The steps of one owner, for the editors (copies, safe to change).</summary>
    public List<ProductionAction> ProductionSteps(string ownerId) => [.. Production.Steps.For(ownerId).Select(s => s.Copy())];

    /// <summary>Saves an owner's production steps (none removes them).</summary>
    public void SaveProductionSteps(string ownerId, IReadOnlyList<ProductionAction> steps)
    {
        try { Production.Steps.Save(ownerId, steps); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Couldn't save production steps", ex);
            Problem = $"Couldn't save the production steps: {ex.Message}";
        }
    }

    // MARK: Local API (Windows only)

    JsonObject IProductionApi.ProductionStatus()
    {
        var lights = Production.Lighting;
        return new JsonObject
        {
            ["devices"] = new JsonArray([.. Production.Setups.Select(d =>
            {
                var status = Production.StatusOf(d.Id);
                return (JsonNode)new JsonObject
                {
                    ["id"] = d.Id, ["name"] = d.Name, ["kind"] = JsonName(d.Kind),
                    ["enabled"] = d.Enabled, ["state"] = status.State.ToString().ToLowerInvariant(), ["detail"] = status.Detail,
                };
            })]),
            ["lights"] = new JsonObject
            {
                ["mainLookId"] = lights.MainLookId,
                ["looksOnTop"] = new JsonArray([.. lights.LooksOnTop.Select(id => (JsonNode)id)]),
                ["cueId"] = lights.CurrentCue?.Id,
                ["blackout"] = lights.Blackout,
                ["grandMaster"] = Math.Round(lights.GrandMaster * 100, 1),
            },
        };
    }

    private static string JsonName(DeviceKind kind)
    {
        var name = kind.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    bool IProductionApi.FireProductionSteps(string ownerId)
    {
        if (Production.Steps.For(ownerId).Count == 0) return false;
        Production.Runner.OwnerRan(ownerId);
        return true;
    }

    Task<bool> IProductionApi.FireProductionStep(JsonObject step)
    {
        ProductionAction parsed;
        try { parsed = MxuJson.Deserialize<ProductionAction>(step.ToJsonString()); }
        catch (System.Text.Json.JsonException ex) { throw ApiError.BadRequest($"Not a production step: {ex.Message}"); }
        if (parsed.Kind is null) throw ApiError.BadRequest($"Unknown production step kind \"{parsed.KindName}\".");
        return Production.PerformAsync(parsed);
    }
}
