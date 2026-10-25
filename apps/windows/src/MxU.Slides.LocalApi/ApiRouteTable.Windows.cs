using System.Text.Json.Nodes;

namespace MxU.Slides.LocalApi;

/// <summary>
/// What a bridge offers for production control (Windows only, Phase 6): cameras, OBS, vMix and
/// lights. A bridge that doesn't implement it answers these routes "Production unavailable."
/// </summary>
public interface IProductionApi
{
    /// <summary>This PC's production devices with their status lights, and the lights' state.</summary>
    JsonObject ProductionStatus();

    /// <summary>Runs the production steps kept for an owner (a slide, combo, schedule trigger or timeline cue id). False when it has none.</summary>
    bool FireProductionSteps(string ownerId);

    /// <summary>Runs one production step, given in its stored JSON shape ({"kind":"atemCut"}). True when the device did it.</summary>
    Task<bool> FireProductionStep(JsonObject step);
}

/// <summary>
/// The Windows-only routes, under /v1/windows/ so they can never collide with a route the Mac
/// adds later. The Mac has no production control, so these have no twin.
/// </summary>
public static partial class ApiRouteTable
{
    private static void AddWindowsRoutes(List<ApiRoute> routes, IApiBridge bridge)
    {
        static JsonNode Object() => new JsonObject { ["type"] = "object" };
        IProductionApi Production() => bridge as IProductionApi ?? throw ApiError.Unavailable("Production");

        routes.Add(new ApiRoute("GET", "/v1/windows/production", ApiScope.View,
            "getProduction", "Windows only: production devices (ATEM, OBS, vMix, DMX) with their status, and the lights.", "Production (Windows)",
            null, Object(), _ => Task.FromResult(ApiHandlerResponse.Json(Production().ProductionStatus()))));

        routes.Add(new ApiRoute("POST", "/v1/windows/production/owners/{id}/fire", ApiScope.Control,
            "fireProductionSteps", "Windows only: run the production steps of a slide, combo, schedule trigger or timeline cue.", "Production (Windows)",
            null, ApiJson.Ref("OK"), context =>
            {
                if (!Production().FireProductionSteps(context.PathParameter("id")))
                    throw ApiError.NotFound("No production steps for that id.");
                return Task.FromResult(ApiHandlerResponse.Ok);
            }));

        routes.Add(new ApiRoute("POST", "/v1/windows/production/steps/fire", ApiScope.Control,
            "fireProductionStep", "Windows only: run one production step, e.g. {\"kind\":\"atemProgram\",\"source\":2}.", "Production (Windows)",
            Object(), Object(), async context =>
            {
                var production = Production();
                var step = context.Decode<JsonObject>();
                var done = await production.FireProductionStep(step);
                return ApiHandlerResponse.Json(new JsonObject { ["done"] = done });
            }));
    }
}
