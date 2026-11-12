using System.Text.Json;
using System.Windows.Threading;
using MxU.Slides.Core.Follow;
using MxU.Slides.Core.Model;
using MxU.Slides.LocalApi;
using MxU.Slides.Windows.Follow;
using MxU.Slides.Windows.Infrastructure;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// Backing up another computer (MxU PR #12), the app's side:
/// <list type="bullet">
/// <item><b>Telling.</b> Every fire here (a slide, a build step, media, an overlay, a clear, an
/// alert, a timer, a combo, an output preset, music, a video's transport) goes on the Local API's
/// fires topic, stamped with a count, its source (the operator, MIDI, the Scheduler…) and, for what
/// a slide or combo sets off, the fire it is an effect of. A backup replays only the roots.</item>
/// <item><b>Following.</b> <see cref="Follow"/> (Follow/FollowController.cs) keeps the link to the
/// booth and replays its fires through the same calls a remote makes.</item>
/// <item><b>Holding.</b> While this computer follows another, MIDI, the Scheduler and remotes
/// wait (reads still answer), and slide actions that drive gear the booth already drove (MIDI out,
/// the stream) are held. Taking over lets them all through.</item>
/// <item><b>Answering.</b> As a booth, the server answers a backup's pairing (the code in
/// Settings › Backup), its pings (with a heartbeat that stops if this app freezes), its role
/// messages and its copies of the library's items and media files.</item>
/// </list>
/// </summary>
public sealed partial class MainViewModel : IFollowApi
{
    private FollowController? followController;
    private DispatcherTimer? followBeatClock;
    private long followBeat;

    /// <summary>Backing up another computer, or being backed up (made on first use, started with the app).</summary>
    public FollowController Follow => followController ??= new FollowController(
        new FollowHost(this, () => Computer.Id, () => Computer.Name, Library, ApiTokens, () => Show.Live is not null || !Show.IsEmpty),
        AppPaths.LocalRoot);

    /// <summary>Once, at startup (from ApplyApiSettings): the heartbeat, the holds, the timers' fires, then following resumes.</summary>
    private void EnsureFollow()
    {
        if (followBeatClock is not null) return;
        // The beat moves only while this UI thread runs: a backup that sees it stand still knows this app froze.
        followBeatClock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        followBeatClock.Tick += (_, _) => Interlocked.Increment(ref followBeat);
        followBeatClock.Start();
        Actions.Holds = kind => FollowController.IsBackingUp && FollowLogic.Holds(kind);
        Timers.Acted += (id, action) => Fired(new ApiFireEvent
        {
            Kind = ApiFireEvent.Kinds.Timer, Id = id,
            Timer = action switch { "play" => ApiTimerAction.Play, "pause" => ApiTimerAction.Pause, _ => ApiTimerAction.Reset },
        });
        Follow.Start();
    }

    /// <summary>The Local API server, with what a backup needs from it and announced on the network.</summary>
    private LocalApiServer NewApiServer()
    {
        var server = new LocalApiServer
        {
            Advertise = true,
            Follow = new FollowHooks(
                Beat: () => (ulong)Interlocked.Read(ref followBeat),
                Hold: route => FollowController.FollowedName is { } name ? FollowHold.Error(route, name) : null,
                Pair: body =>
                {
                    var answer = Follow.Code.Answer(body, ApiTokens);
                    Follow.RefreshBackups();
                    return answer;
                },
                Blob: hash => Library.Blobs.Find(hash),
                Heard: name => Follow.Presence.Heard(name),
                Unpair: token =>
                {
                    ApiTokens.Revoke(token.Id);
                    Follow.RefreshBackups();
                }),
        };
        server.UpdateIdentity(Computer.Id, Computer.Name);
        // Each fire goes out as it happens; with the server off, the feed only counts.
        ApiFireFeed.Shared.Sink = fire =>
        {
            if (api is { IsRunning: true } running) running.Events.Publish(new ApiEvent(ApiTopic.Fires, ApiFireFeed.Encode(fire)));
        };
        return server;
    }

    /// <summary>The operator's triggers wait while this computer follows another (logged so it can be seen why).</summary>
    private static bool HeldWhileFollowing(string what)
    {
        if (!FollowController.IsBackingUp) return false;
        Log.Info($"follow.held: {what} (following {FollowController.FollowedName})");
        return true;
    }

    // MARK: Telling a backup

    private static int Fired(ApiFireEvent fire) => ApiFireFeed.Shared.Publish(fire);

    /// <summary>Tells the fire, and until disposed everything that follows from it is its effect.</summary>
    private static IDisposable FiredWithEffects(ApiFireEvent fire) => ApiFireFeed.Caused(Fired(fire));

    private static IDisposable FiredSlide(Presentation presentation, Slide slide, string? arrangementId, string? itemId, int occurrence, bool settled, bool runActions) =>
        FiredWithEffects(new ApiFireEvent
        {
            Kind = ApiFireEvent.Kinds.Slide,
            Slide = new ApiFireSlideCommand(presentation.Id, slide.Id, ArrangementId: arrangementId, ServiceItemId: itemId, Occurrence: occurrence),
            Settled = settled ? true : null,
            SlideActions = runActions ? null : false,
        });

    /// <summary>An enum as the wire (and the Mac) names it: "persist", "confidence"…</summary>
    private static string WireName<T>(T value) where T : struct, Enum => JsonSerializer.Serialize(value).Trim('"');

    // MARK: The follow routes (IFollowApi)

    ApiFollowStatus IFollowApi.FollowStatus() => Follow.Status();

    Task<string?> IFollowApi.FollowTakeOverAsync() => Follow.TakeOverAsync("API");

    Task<string?> IFollowApi.FollowHandBackAsync() => Follow.HandBackAsync();

    string? IFollowApi.FollowTakeBack() => Follow.TakeBack();

    /// <summary>
    /// The items this booth offers its backup. A Windows library is JSON, which a Mac backup can't
    /// read, so they're offered only to a backup that asks for JSON (a Windows one); a Mac backup is
    /// told there are none and simply follows the show.
    /// </summary>
    async Task<FollowFiles.ItemList> IFollowApi.FollowFilesAsync(bool readsJson)
    {
        if (!readsJson) return new FollowFiles.ItemList([]);
        // Reading and hashing every item's file is disk work: off the UI thread (the library is safe to read from any).
        var library = Library;
        var offers = await Task.Run(() => FollowCopy.Offered(library));
        return new FollowFiles.ItemList([.. offers.Select(o => new FollowFiles.Item(o.Kind, o.Id, o.Heads, o.File, o.Ext))]);
    }

    async Task<FollowFiles.Document> IFollowApi.FollowFileAsync(string kind, string id)
    {
        var (library, safe) = (Library, ApiIds.Validated(id));
        return await Task.Run(() => FollowCopy.Save(library, kind, safe)) is { } bytes
            ? new FollowFiles.Document(bytes)
            : throw ApiError.NotFound("Not a library item here.");
    }

    ApiFollowRoleCommand IFollowApi.FollowRole(ApiFollowRoleCommand command) => Follow.ReceiveRole(command);
}
