using System.Text;
using System.Text.Json.Nodes;
using MxU.Slides.LocalApi;

namespace MxU.Slides.LocalApi.Tests;

// Windows only: production control's routes, under /v1/windows/ (the Mac has no twin).
public sealed class WindowsRoutesTests
{
    private sealed class ProductionBridge : CoreOnlyBridge, IProductionApi
    {
        public List<string> Owners { get; } = [];
        public List<string> Steps { get; } = [];

        public JsonObject ProductionStatus() => new() { ["devices"] = new JsonArray() };

        public bool FireProductionSteps(string ownerId)
        {
            Owners.Add(ownerId);
            return ownerId == "slide-1";
        }

        public Task<bool> FireProductionStep(JsonObject step)
        {
            Steps.Add(step["kind"]!.GetValue<string>());
            return Task.FromResult(true);
        }
    }

    private static Task<ApiHandlerResponse> Call(IApiBridge bridge, string method, string path, string body = "")
    {
        var router = new ApiRouter(ApiRouteTable.Build(bridge));
        var match = router.Match(method, path) ?? throw new InvalidOperationException($"No route for {method} {path}");
        return match.Route.Handler(new ApiRequestContext
        {
            PathParameters = match.Parameters, Body = Encoding.UTF8.GetBytes(body), Scope = ApiScope.Control,
        });
    }

    [Fact]
    public async Task ABridgeWithoutProductionAnswersUnavailable()
    {
        var error = await Assert.ThrowsAsync<ApiError>(() => Call(new CoreOnlyBridge(), "GET", "/v1/windows/production"));
        Assert.Equal((404, "Production unavailable."), (error.Status, error.Message));
    }

    [Fact]
    public async Task AnOwnersStepsAndASingleStepFire()
    {
        var bridge = new ProductionBridge();
        await Call(bridge, "POST", "/v1/windows/production/owners/slide-1/fire");
        var missing = await Assert.ThrowsAsync<ApiError>(() => Call(bridge, "POST", "/v1/windows/production/owners/nobody/fire"));
        Assert.Equal(404, missing.Status);
        var response = await Call(bridge, "POST", "/v1/windows/production/steps/fire", """{"kind":"atemCut"}""");
        Assert.Equal("""{"done":true}""", Encoding.UTF8.GetString(response.Body));
        Assert.Equal(["slide-1", "nobody"], bridge.Owners);
        Assert.Equal(["atemCut"], bridge.Steps);
        var status = await Call(bridge, "GET", "/v1/windows/production");
        Assert.Contains("devices", Encoding.UTF8.GetString(status.Body));
    }
}
