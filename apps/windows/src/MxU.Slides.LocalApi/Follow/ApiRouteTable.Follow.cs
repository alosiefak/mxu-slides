using System.Text.Json.Nodes;

namespace MxU.Slides.LocalApi;

/// <summary>
/// What a bridge offers for backing up another computer (MxU PR #12): its follow status, taking
/// over and handing back, its library items for a backup to copy, and who is in charge. The
/// Mac's LocalAPIController adds the same routes after its route table. A bridge that doesn't
/// implement it answers them "Follow unavailable."
/// </summary>
public interface IFollowApi
{
    ApiFollowStatus FollowStatus();

    /// <summary>A backup takes over; null when it did, else why not.</summary>
    Task<string?> FollowTakeOverAsync();

    /// <summary>A backup in charge hands back to its booth.</summary>
    Task<string?> FollowHandBackAsync();

    /// <summary>A booth that stepped down takes back.</summary>
    string? FollowTakeBack();

    /// <summary>This computer's library items, for its backup (Windows offers them only to a Windows backup: see the app).</summary>
    FollowFiles.ItemList FollowFiles(bool readsJson);

    FollowFiles.Document FollowFile(string kind, string id);

    /// <summary>A backup tells its booth who is in charge; the answer is who the booth thinks is.</summary>
    ApiFollowRoleCommand FollowRole(ApiFollowRoleCommand command);
}

public static partial class ApiRouteTable
{
    /// <summary>
    /// A Windows backup asks for /v1/follow/files?json=1 so a Windows booth offers its items (JSON
    /// saves); a Mac booth ignores it and offers Automerge saves, which a Windows backup skips.
    /// </summary>
    public const string ReadsJsonQuery = "json";

    private static void AddFollowRoutes(List<ApiRoute> routes, IApiBridge bridge)
    {
        static JsonNode Object() => new JsonObject { ["type"] = "object" };
        IFollowApi Follow() => bridge as IFollowApi ?? throw ApiError.Unavailable("Follow");
        void Act(string path, string id, string summary, Func<Task<string?>> run) =>
            routes.Add(new ApiRoute("POST", path, ApiScope.Control, id, summary, "Follow", null, ApiJson.Ref("OK"), async _ =>
            {
                if (await run() is { } refused) throw new ApiError(409, "follow", refused);
                return ApiHandlerResponse.Ok;
            }));

        routes.Add(new ApiRoute("GET", "/v1/follow", ApiScope.View, "followStatus", "Follow mode: backing up, in charge or stepped down.", "Follow",
            null, Object(), _ => Task.FromResult(ApiHandlerResponse.Json(Follow().FollowStatus()))));
        Act("/v1/follow/takeover", "followTakeOver", "A backup takes over.", () => Follow().FollowTakeOverAsync());
        Act("/v1/follow/handback", "followHandBack", "A backup in charge hands back.", () => Follow().FollowHandBackAsync());
        Act("/v1/follow/takeback", "followTakeBack", "A booth that stepped down takes back.", () => Task.FromResult(Follow().FollowTakeBack()));
        routes.Add(new ApiRoute("GET", "/v1/follow/files", ApiScope.Control, "followFiles", "This computer's library items, for its backup.", "Follow",
            null, Object(), context => Task.FromResult(ApiHandlerResponse.Json(Follow().FollowFiles(context.Query.GetValueOrDefault(ReadsJsonQuery) == "1")))));
        routes.Add(new ApiRoute("GET", "/v1/follow/files/{kind}/{id}", ApiScope.Control, "followFile", "One library item's whole save.", "Follow",
            null, Object(), context => Task.FromResult(ApiHandlerResponse.Json(Follow().FollowFile(context.PathParameter("kind"), context.PathParameter("id"))))));
        routes.Add(new ApiRoute("POST", "/v1/follow/role", ApiScope.Control, "followRole", "A backup tells its booth who is in charge.", "Follow",
            null, Object(), context => Task.FromResult(ApiHandlerResponse.Json(Follow().FollowRole(context.Decode<ApiFollowRoleCommand>())))));
    }
}
