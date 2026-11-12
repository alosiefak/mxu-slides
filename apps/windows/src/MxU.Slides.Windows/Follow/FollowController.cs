using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MxU.Slides.Core.Follow;
using MxU.Slides.Core.Library;
using MxU.Slides.LocalApi;
using MxU.Slides.Windows.Infrastructure;

namespace MxU.Slides.Windows.Follow;

/// <summary>What the follow controller needs from the app (MainViewModel), so it holds no view model itself.</summary>
/// <param name="Bridge">The same calls a remote control makes: a backup replays its booth's fires through them.</param>
/// <param name="Me">This computer's station id (ThisComputer.Id), as the Mac's machineID.</param>
/// <param name="MyName">The name the other computer shows for this one.</param>
/// <param name="InServiceWindow">A service is running here now: a backup dropping then is worth a banner.</param>
public sealed record FollowHost(
    IApiBridge Bridge, Func<string> Me, Func<string> MyName, LibraryStore Library, ApiTokenStore Tokens, Func<bool> InServiceWindow);

/// <summary>
/// Backing up another computer (MxU PR #12, the Mac's FollowController). One computer is the booth
/// and runs the service; another, paired to it with the four-digit code the booth shows, follows it:
/// every fire on the booth (a slide, media, an overlay, a clear, a timer, music…) runs on the backup
/// too, so if the booth stops answering (or freezes) the backup takes over where it was, and
/// MIDI, Companion and the Scheduler answer the backup from then on. Either computer can be a Mac.
/// <para>
/// Runs on the UI thread, as the Mac's is @MainActor: the link to the booth is a LocalApiClient
/// whose updates are read here with await (never blocking), and every request is async.
/// Pairing and roles are kept per computer (LocalRoot/follow.json); the key the booth gave is kept
/// encrypted for this Windows user (DPAPI), as the Mac keeps it in the Keychain.
/// </para>
/// </summary>
public sealed class FollowController
{
    /// <summary>The booth this computer follows (and may take over from).</summary>
    public sealed class Pairing
    {
        public string StationHexId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Host { get; set; } = "";
        public int Port { get; set; }
        public bool? Automatic { get; set; }
        public FollowRole? Role { get; set; }
        public DateTimeOffset? TookOverAt { get; set; }
        public bool? TookOverOnItsOwn { get; set; }
        public bool? TookOverFrozen { get; set; }
        public bool? Mirrors { get; set; }
    }

    /// <summary>On a booth: a backup took over, so this computer's triggers wait.</summary>
    public sealed record SteppedDown(string BackupName, FollowRole Role);

    private sealed class Saved
    {
        public Pairing? Pairing { get; set; }
        public SteppedDown? SteppedDown { get; set; }
        public FollowRole? BoothRole { get; set; }
        public List<string>? Copied { get; set; }
    }

    /// <summary>The name of the computer in charge while this one follows it, else null (read from any thread: the server's hold).</summary>
    public static string? FollowedName => held;

    public static bool IsBackingUp => held is not null;

    private static volatile string? held;

    /// <summary>This run of the app: a booth that comes back with the same one only dropped off the network.</summary>
    public static readonly string Launch = Guid.NewGuid().ToString("D").ToUpperInvariant();

    private const string AutomaticReason = "the booth stopped answering";

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly JsonSerializerOptions Json = ApiJson.Options;

    private readonly FollowHost host;
    private readonly string statePath;
    private readonly string keyFolder;
    private Saved saved;

    private LocalApiClient? client;
    private LocalApiClient.Endpoint? endpoint;
    private CancellationTokenSource? session;
    private BonjourBrowser? watch;
    private int rewatches;
    private const int MaxRewatches = 10;
    private string? boothLaunch;
    private int standbyRead;
    private bool copying;
    private bool standbyLoop;

    public FollowController(FollowHost host, string localRoot)
    {
        this.host = host;
        statePath = Path.Combine(localRoot, "follow.json");
        keyFolder = Path.Combine(localRoot, "follow-keys");
        saved = Load();
        Presence = new FollowPresence(names => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => BackupsHeard(names)));
    }

    /// <summary>Raised on the UI thread whenever anything here changes (the banner and Settings redraw).</summary>
    public event Action? Changed;

    /// <summary>On a booth: which backups are pinging (the server's ping hook feeds it).</summary>
    public FollowPresence Presence { get; }

    public Pairing? Paired => saved.Pairing;
    public SteppedDown? SteppedDownTo => saved.SteppedDown;
    public FollowLogic.State State { get; private set; } = FollowLogic.State.IsConnecting;
    public DateTimeOffset? LastFire { get; private set; }
    public string? Problem { get; private set; }
    public int CopiedItems { get; private set; }
    public string? CopyProblem { get; private set; }
    public bool BoothQuit { get; private set; }
    public string? CameBack { get; private set; }
    public FollowStandby? Standby { get; private set; }
    public IReadOnlyList<string> Backups { get; private set; } = [];
    public IReadOnlySet<string> ConnectedBackups { get; private set; } = new HashSet<string>();
    public string? DroppedBackup { get; private set; }

    /// <summary>The booth's pairing code (four digits; a new one after five wrong tries).</summary>
    public FollowPairing Code { get; } = new();

    public bool BackingUp => saved.Pairing is not null && !InCharge;

    public bool StandingBy => BackingUp && saved.Pairing?.Mirrors == false;

    public bool InCharge => saved.Pairing?.Role is { } role && role.InCharge != saved.Pairing.StationHexId;

    private string Me => host.Me();

    /// <summary>Starts following the booth this computer is paired to, if any (at launch).</summary>
    public void Start()
    {
        if (saved.SteppedDown is { } down) held = down.BackupName;
        RefreshBackups();
        if (saved.Pairing is not { } pairing) return;
        if (LoadKey(pairing.StationHexId) is { } key)
        {
            Begin(pairing, key);
        }
        else
        {
            State = FollowLogic.State.HasGivenUp;
            Problem = $"This computer lost its key for {pairing.Name}. Stop Backing Up, then pair again.";
        }
        Raise();
    }

    /// <summary>The app is closing: a booth tells its backups it quit, so none takes over for it.</summary>
    public void Quitting()
    {
        if (saved.Pairing is not null || ApiFireFeed.Shared.Sink is null) return;
        ApiFireFeed.Shared.Publish(new ApiFireEvent { Kind = ApiFireEvent.Kinds.Quit });
        // A moment for the frame to leave before the server stops (the Mac waits a quarter second).
        Thread.Sleep(250);
    }

    // MARK: Pairing

    /// <summary>Pairs with a booth by its code. Null when paired, else why not.</summary>
    public async Task<string?> PairAsync(string boothHost, int boothPort, string boothStationId, string boothName, string code, bool automatic, bool mirrors)
    {
        var request = new ApiFollowPairRequest(code.Trim(), Me, host.MyName());
        string key;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var response = await Http.PostAsJsonAsync(Url(boothHost, boothPort, "/v1/follow/pair"), request, Json, timeout.Token);
            var body = await response.Content.ReadAsByteArrayAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
                return Message(body) ?? "That computer didn't answer.";
            key = JsonSerializer.Deserialize<ApiFollowPairResponse>(body, Json)?.Token ?? throw new JsonException("No key");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            return $"Couldn't reach {boothName}: {ex.Message}";
        }
        // The booth's own id, from its ping when it wasn't found on the network.
        if (boothStationId.Length == 0) boothStationId = (await Get<ApiServerInfo>(new LocalApiClient.Endpoint(boothHost, boothPort, key), "/v1/ping"))?.StationHexId ?? $"{boothHost}:{boothPort}";
        var pairing = new Pairing
        {
            StationHexId = boothStationId, Name = boothName, Host = boothHost, Port = boothPort,
            Automatic = automatic, Role = FollowRole.Start(boothStationId), Mirrors = mirrors,
        };
        SaveKey(boothStationId, key);
        saved.Pairing = pairing;
        Store();
        Log.Info($"follow.paired: {pairing.Name} at {pairing.Host}:{pairing.Port}");
        Begin(pairing, key);
        Raise();
        return null;
    }

    public async Task UnpairAsync()
    {
        if (endpoint is { } at) await Post(at, "/v1/follow/unpair", null, TimeSpan.FromSeconds(3));
        StopClient();
        if (saved.Pairing is { } pairing) ClearKey(pairing.StationHexId);
        saved.Pairing = null;
        saved.Copied = null;
        Store();
        held = null;
        State = FollowLogic.State.IsConnecting;
        (Problem, CopyProblem, CameBack, Standby, LastFire, CopiedItems, BoothQuit) = (null, null, null, null, null, 0, false);
        Log.Info("follow.unpaired");
        Raise();
    }

    public void SetAutomatic(bool automatic)
    {
        if (saved.Pairing is not { } pairing || (pairing.Automatic ?? true) == automatic) return;
        pairing.Automatic = automatic;
        Store();
        Raise();
    }

    public async Task SetMirrorsAsync(bool mirrors)
    {
        if (saved.Pairing is not { } pairing || (pairing.Mirrors ?? true) == mirrors) return;
        pairing.Mirrors = mirrors;
        Store();
        Raise();
        if (BackingUp && endpoint is { } at) await CatchUp(at);
    }

    // MARK: Who is in charge

    /// <summary>The backup takes over (by hand, the API, or on its own when the booth stopped). Null when it did.</summary>
    public async Task<string?> TakeOverAsync(string reason = "by hand", bool frozen = false)
    {
        if (saved.Pairing is not { } pairing) return "This computer isn't backing up another.";
        if (InCharge) return null;
        var role = (pairing.Role ?? FollowRole.Start(pairing.StationHexId)).Handed(Me);
        pairing.Role = role;
        pairing.TookOverAt = DateTimeOffset.Now;
        // Only the booth going quiet counts as "on its own" (the Mac counts the API too, so a takeover
        // from Companion never told a booth that was still running: here it hears, as for Take Over).
        pairing.TookOverOnItsOwn = reason == AutomaticReason;
        pairing.TookOverFrozen = frozen;
        Store();
        held = null;
        Log.Info($"follow.tookOver: {reason}, term {role.Term}");
        Raise();
        // A booth that only stopped answering can't hear it; one that froze hears it when it wakes.
        if (!(pairing.TookOverOnItsOwn == true && !frozen)) await TellBooth();
        if (pairing.Mirrors == false) await PutUpBoothPicture();
        return null;
    }

    public async Task<string?> HandBackAsync()
    {
        if (saved.Pairing is not { Role: { } role } pairing || !InCharge) return "This computer isn't in charge.";
        var handed = role.Handed(pairing.StationHexId);
        if (await PostRole(handed) is not { } answer || answer.Term < handed.Term)
        {
            Problem = $"{pairing.Name} isn't answering, so it can't take back yet.";
            Raise();
            return Problem;
        }
        pairing.Role = handed;
        pairing.TookOverAt = null;
        Store();
        held = pairing.Name;
        Problem = null;
        Log.Info($"follow.handedBack: term {handed.Term}");
        Raise();
        if (endpoint is { } at) await CatchUp(at);
        return null;
    }

    /// <summary>On a booth: a backup tells it who is in charge; the answer is who the booth thinks is.</summary>
    public ApiFollowRoleCommand ReceiveRole(ApiFollowRoleCommand command)
    {
        var mine = BoothRole;
        if (mine.Newer(new FollowRole(command.Term, command.InCharge)) is not { } newer)
            return new ApiFollowRoleCommand(mine.Term, mine.InCharge, host.MyName());
        saved.BoothRole = newer;
        if (newer.InCharge == Me)
        {
            saved.SteppedDown = null;
            held = null;
        }
        else
        {
            saved.SteppedDown = new SteppedDown(command.Name, newer);
            held = command.Name;
        }
        Store();
        Log.Info($"follow.role: term {newer.Term}: {(newer.InCharge == Me ? "this computer" : command.Name)} in charge");
        Raise();
        return command;
    }

    /// <summary>On a booth that stepped down: it takes back (and tells its backups by a role fire).</summary>
    public string? TakeBack()
    {
        if (saved.SteppedDown is null) return "This computer is in charge already.";
        var role = BoothRole.Handed(Me);
        saved.BoothRole = role;
        saved.SteppedDown = null;
        Store();
        held = null;
        ApiFireFeed.Shared.Publish(new ApiFireEvent { Kind = ApiFireEvent.Kinds.Role, Term = role.Term, InCharge = Me });
        Log.Info($"follow.tookBack: term {role.Term}");
        Raise();
        return null;
    }

    private FollowRole BoothRole => saved.BoothRole ?? new FollowRole(0, Me);

    public ApiFollowStatus Status()
    {
        if (saved.SteppedDown is { } down) return new ApiFollowStatus("steppedDown", down.BackupName, Term: down.Role.Term, Launch: Launch);
        if (saved.Pairing is not { } pairing) return new ApiFollowStatus("none", Term: BoothRole.Term, Launch: Launch);
        var health = State switch
        {
            FollowLogic.State.Connecting => "connecting",
            FollowLogic.State.Live => "live",
            FollowLogic.State.Lost { Frozen: true } => "frozen",
            FollowLogic.State.Lost => "lost",
            _ => "gaveUp",
        };
        return new ApiFollowStatus(InCharge ? "inCharge" : "backingUp", pairing.Name, health, pairing.Role?.Term, Launch: Launch);
    }

    // MARK: Following

    private void Begin(Pairing pairing, string key)
    {
        StopClient();
        if (!InCharge) held = pairing.Name;
        State = FollowLogic.State.IsConnecting;
        Problem = null;
        var at = new LocalApiClient.Endpoint(pairing.Host, pairing.Port, key);
        endpoint = at;
        var link = new LocalApiClient(at, [ApiTopic.Fires]);
        client = link;
        var stop = new CancellationTokenSource();
        session = stop;
        link.Start();
        _ = Pump(link, at, stop.Token);
    }

    private async Task Pump(LocalApiClient link, LocalApiClient.Endpoint at, CancellationToken stop)
    {
        try
        {
            // Resumes on the UI thread after each update (the dispatcher's context), never blocking it.
            await foreach (var update in link.Updates.ReadAllAsync(stop))
            {
                if (client != link) return;
                await Handle(update, at);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("Following the other computer failed", ex);
        }
    }

    private void StopClient()
    {
        StopWatching();
        session?.Cancel();
        session = null;
        if (client is { } old) _ = old.StopAsync();
        client = null;
        endpoint = null;
    }

    private async Task Handle(LocalApiClient.Update update, LocalApiClient.Endpoint at)
    {
        switch (update)
        {
            case LocalApiClient.Update.HealthChanged { Health: var health }:
                var wasLive = State is FollowLogic.State.Live;
                State = health switch
                {
                    LocalApiClient.Health.Connecting => State is FollowLogic.State.Lost ? State : FollowLogic.State.IsConnecting,
                    LocalApiClient.Health.Live => FollowLogic.State.IsLive,
                    LocalApiClient.Health.Lost { Why: LocalApiClient.Loss.Frozen } => new FollowLogic.State.Lost(true),
                    LocalApiClient.Health.Lost => new FollowLogic.State.Lost(false),
                    _ => FollowLogic.State.HasGivenUp,
                };
                Log.Info($"follow.health: {health}");
                if (State is FollowLogic.State.Live)
                {
                    BoothQuit = false;
                    rewatches = 0;
                }
                else if (State is FollowLogic.State.GaveUp)
                {
                    WatchForBooth();
                }
                Raise();
                if (FollowLogic.TakesOverOnItsOwn(saved.Pairing?.Automatic ?? true, BackingUp, wasLive, State, BoothQuit))
                {
                    await TakeOverAsync(AutomaticReason, State is FollowLogic.State.Lost { Frozen: true });
                }
                else if (State is FollowLogic.State.Live && !wasLive)
                {
                    if (InCharge) await BoothReturned(at);
                    else await CatchUp(at);
                    _ = CopyFiles(at);
                }
                break;
            case LocalApiClient.Update.Event { Topic: ApiTopic.Fires, Data: var data }:
                if (ApiFireFeed.Decode(data) is not { } fire) return;
                if (fire.Kind == ApiFireEvent.Kinds.Role && fire.Term is { } term && fire.InCharge is { } inCharge && saved.Pairing is { } pairing
                    && (pairing.Role ?? FollowRole.Start(pairing.StationHexId)).Newer(new FollowRole(term, inCharge)) is { } newer)
                {
                    Adopt(newer);
                    if (BackingUp) await CatchUp(at);
                }
                else if (fire.Kind == ApiFireEvent.Kinds.Quit)
                {
                    BoothQuit = true;
                    Log.Info($"follow.boothQuit: {saved.Pairing?.Name}");
                    Raise();
                }
                else if (fire.Kind != ApiFireEvent.Kinds.Capture && BackingUp && FollowReplay.Replays(fire))
                {
                    if (StandingBy)
                    {
                        LastFire = DateTimeOffset.Now;
                        await ReadStandby(at);
                        Raise();
                    }
                    else
                    {
                        await Replay(fire);
                    }
                }
                break;
        }
    }

    private async Task Replay(ApiFireEvent fire)
    {
        if (saved.Pairing is not { } pairing) return;
        LastFire = DateTimeOffset.Now;
        CameBack = null;
        try
        {
            await ApiFireSource.With(new ApiFireSource(ApiFireSource.Kinds.Follow, pairing.Name), () => FollowReplay.Apply(fire, host.Bridge));
            Problem = null;
        }
        catch (Exception ex) when (ex is ApiError or InvalidOperationException or ArgumentException)
        {
            Problem = $"Couldn't run {fire.Kind.Raw()}: {ex.Message}";
            Log.Warn($"follow.replayFailed: {fire.Kind.Raw()} seq {fire.Seq}: {ex.Message}");
        }
        Raise();
    }

    private async Task BoothReturned(LocalApiClient.Endpoint at)
    {
        var status = await Get<ApiFollowStatus>(at, "/v1/follow");
        if (saved.Pairing is { TookOverAt: { } since } pairing && FollowLogic.HandsBackOnReturn(
                pairing.TookOverOnItsOwn ?? false, pairing.TookOverFrozen ?? false, since, DateTimeOffset.Now, boothLaunch, status?.Launch))
        {
            var line = FollowLogic.CameBackLine(pairing.Name, DateTimeOffset.Now - since);
            if (await HandBackAsync() is null)
            {
                CameBack = line;
                Log.Info($"follow.gaveWay: {line}");
                Raise();
                return;
            }
        }
        await TellBooth();
    }

    private async Task TellBooth()
    {
        if (saved.Pairing?.Role is not { } role) return;
        if (await PostRole(role) is { } answer && role.Newer(new FollowRole(answer.Term, answer.InCharge)) is { } newer) Adopt(newer);
    }

    private Task<ApiFollowRoleCommand?> PostRole(FollowRole role) =>
        endpoint is { } at
            ? Post<ApiFollowRoleCommand>(at, "/v1/follow/role", new ApiFollowRoleCommand(role.Term, role.InCharge, host.MyName()), TimeSpan.FromSeconds(3))
            : Task.FromResult<ApiFollowRoleCommand?>(null);

    private void Adopt(FollowRole role)
    {
        if (saved.Pairing is not { } pairing) return;
        pairing.Role = role;
        if (role.InCharge == pairing.StationHexId) pairing.TookOverAt = null;
        Store();
        held = role.InCharge == Me ? null : pairing.Name;
        Log.Info($"follow.role: term {role.Term}, in charge {(role.InCharge == Me ? "here" : pairing.Name)}");
        Raise();
    }

    /// <summary>Brings this computer in step with what the booth shows (on joining, after handing back, when the options change).</summary>
    private async Task CatchUp(LocalApiClient.Endpoint at)
    {
        if (await Get<ApiFollowStatus>(at, "/v1/follow") is { } status)
        {
            boothLaunch = status.Launch;
            if (status.Role == "none" && status.Term is { } term && saved.Pairing is { } pairing
                && (pairing.Role ?? FollowRole.Start(pairing.StationHexId)).Newer(new FollowRole(term, pairing.StationHexId)) is { } newer)
                Adopt(newer);
        }
        var bridge = host.Bridge;
        var source = new ApiFireSource(ApiFireSource.Kinds.Follow, saved.Pairing?.Name);
        if (StandingBy)
        {
            await ReadStandby(at);
            if (!standbyLoop) _ = StandbyLoop(at);
            if (FollowStandby.ShowsAnything(bridge.ShowStatus(), bridge.AudioStatus()))
            {
                await ApiFireSource.With(source, async () =>
                {
                    foreach (var fire in new[] { new ApiFireEvent { Kind = ApiFireEvent.Kinds.ClearAll }, new ApiFireEvent { Kind = ApiFireEvent.Kinds.StopAudio } })
                        await Quietly(() => FollowReplay.Apply(fire, bridge));
                });
            }
            Log.Info($"follow.standingBy: {Standby?.Show.LiveSlide?.SlideId ?? "no slide"}");
            Raise();
            return;
        }
        if (await Get<ApiShowStatus>(at, "/v1/status") is not { } show) return;
        var differences = FollowReplay.CatchUp(show, bridge.ShowStatus());
        await ApiFireSource.With(source, async () =>
        {
            foreach (var fire in differences) await Quietly(() => FollowReplay.Apply(fire, bridge));
            if (show.LiveSlide is { } live)
                await Quietly(() => bridge.FireSlideAsync(FollowReplay.Command(new ApiFireSlideCommand(
                    live.PresentationId, live.SlideId, ServiceItemId: live.ServiceItemId, Occurrence: live.Occurrence))));
        });
        Log.Info($"follow.caughtUp: {show.LiveSlide?.SlideId ?? "no slide"}, {differences.Count} layers");
    }

    private async Task StandbyLoop(LocalApiClient.Endpoint at)
    {
        standbyLoop = true;
        try
        {
            while (StandingBy && endpoint == at && State is FollowLogic.State.Live)
            {
                await Task.Delay(TimeSpan.FromSeconds(15));
                if (!StandingBy || endpoint != at || State is not FollowLogic.State.Live) break;
                await ReadStandby(at);
            }
        }
        finally
        {
            standbyLoop = false;
        }
    }

    private async Task ReadStandby(LocalApiClient.Endpoint at)
    {
        var read = ++standbyRead;
        if (await Get<ApiShowStatus>(at, "/v1/status") is not { } show) return;
        var transport = Get<List<ApiTransportRow>>(at, "/v1/transport");
        var audio = Get<ApiAudioStatus>(at, "/v1/audio");
        var rows = await transport;
        var sound = await audio;
        if (read != standbyRead) return;
        Standby = new FollowStandby(show, (IReadOnlyList<ApiTransportRow>?)rows ?? [], sound, DateTimeOffset.Now);
    }

    /// <summary>"Stay ready": a backup that took over puts up what the booth last showed, where it would be now.</summary>
    private async Task PutUpBoothPicture()
    {
        if (Standby is not { } standby) return;
        var bridge = host.Bridge;
        var fires = standby.Takeover(bridge.ShowStatus(), DateTimeOffset.Now);
        await ApiFireSource.With(new ApiFireSource(ApiFireSource.Kinds.Follow, saved.Pairing?.Name), async () =>
        {
            foreach (var fire in fires)
            {
                for (var attempt = 0; attempt < 10; attempt++)
                {
                    try
                    {
                        await FollowReplay.Apply(fire, bridge);
                        break;
                    }
                    catch (Exception ex) when (ex is ApiError or InvalidOperationException)
                    {
                        // A video or song that has only just started can't seek yet: a moment, then again.
                        if (fire.Kind is not (ApiFireEvent.Kinds.MediaTransport or ApiFireEvent.Kinds.AudioTransport) || attempt == 9) break;
                        await Task.Delay(100);
                    }
                }
            }
        });
        Log.Info($"follow.putUp: {standby.Show.LiveSlide?.SlideId ?? "no slide"}, {standby.Transport.Count} videos");
    }

    private static async Task Quietly(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex) when (ex is ApiError or InvalidOperationException or ArgumentException)
        {
            Log.Info($"follow: skipped a catch-up step: {ex.Message}");
        }
    }

    // MARK: Copying the booth's items

    /// <summary>Copies what changed on the booth now and every five minutes while live.</summary>
    private async Task CopyFiles(LocalApiClient.Endpoint at)
    {
        if (copying) return;
        copying = true;
        try
        {
            while (endpoint == at && State is FollowLogic.State.Live)
            {
                try
                {
                    CopiedItems += await CopyOnce(at);
                    CopyProblem = null;
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException or UnauthorizedAccessException)
                {
                    CopyProblem = $"Couldn't copy {saved.Pairing?.Name ?? "the other computer"}'s items: {ex.Message}";
                    Log.Warn($"follow.copyFailed: {ex.Message}");
                }
                Raise();
                await Task.Delay(TimeSpan.FromMinutes(5));
            }
        }
        finally
        {
            copying = false;
        }
    }

    /// <summary>One pass: missing media files first, then the items whose version changed, then copies the booth dropped.</summary>
    private async Task<int> CopyOnce(LocalApiClient.Endpoint at)
    {
        var library = host.Library;
        var list = await Get<FollowFiles.ItemList>(at, $"/v1/follow/files?{ApiRouteTable.ReadsJsonQuery}=1", TimeSpan.FromSeconds(30))
            ?? throw new HttpRequestException("The other computer didn't list its items.");
        // The versions held here: reading each file is disk work, off the UI thread.
        var heads = await Task.Run(() =>
        {
            var held = new Dictionary<string, IReadOnlyList<string>>();
            foreach (var item in list.Items)
                if (FollowCopy.HeldHeads(library, item.Kind, item.Id) is { } here) held[$"{item.Kind}/{item.Id}"] = here;
            return held;
        });
        var hashes = list.Items.Select(i => i.File).OfType<string>().ToList();
        var present = library.Blobs.Presence(hashes);
        var (documents, files) = FollowFiles.Wanted(list.Items, heads, present);
        var exts = list.Items.Where(i => i.File is not null).GroupBy(i => i.File!).ToDictionary(g => g.Key, g => g.First().Ext ?? "");

        foreach (var hash in files) await Download(at, hash, exts.GetValueOrDefault(hash, ""), library.Blobs);
        var landed = 0;
        var unreadable = 0;
        foreach (var item in documents)
        {
            // A Mac booth's saves are Automerge documents, which this PC can't read yet.
            if (!FollowCopy.IsJson(item.Heads) || !FollowCopy.Kinds.ContainsKey(item.Kind))
            {
                unreadable++;
                continue;
            }
            var document = await Get<FollowFiles.Document>(at, $"/v1/follow/files/{item.Kind}/{Uri.EscapeDataString(item.Id)}", TimeSpan.FromSeconds(30));
            if (document is not null && FollowCopy.Land(library, item.Kind, item.Id, document.Bytes)) landed++;
        }
        var copied = (saved.Copied ?? []).ToHashSet();
        var gone = FollowFiles.Gone(copied, list.Items);
        foreach (var key in gone) FollowCopy.Remove(library, key);
        var holding = list.Items.Where(i => FollowCopy.IsJson(i.Heads)).Select(i => $"{i.Kind}/{i.Id}").ToList();
        if (!holding.ToHashSet().SetEquals(copied))
        {
            saved.Copied = holding;
            Store();
        }
        if (landed > 0 || files.Count > 0 || gone.Count > 0)
            Log.Info($"follow.copied: {landed} items, {files.Count} files, {gone.Count} removed");
        if (unreadable > 0)
            throw new IOException($"{unreadable} items are saved in the Mac's format, which this PC can't copy yet. The show still follows.");
        return landed;
    }

    private static async Task Download(LocalApiClient.Endpoint at, string hash, string ext, BlobStore blobs)
    {
        if (!FollowFiles.IsHash(hash)) return;
        Directory.CreateDirectory(blobs.Folder);
        var temp = Path.Combine(blobs.Folder, $"{hash}.download");
        using (var request = Request(HttpMethod.Get, at, $"/v1/follow/blobs/{hash}"))
        using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
        {
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"The other computer didn't send a media file ({(int)response.StatusCode}).");
            await using var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            await response.Content.CopyToAsync(file);
        }
        // Hashing a long video is slow: off the UI thread.
        var actual = await Task.Run(() => LibraryStore.HashFile(temp));
        if (actual != hash)
        {
            File.Delete(temp);
            throw new IOException("A media file came across damaged.");
        }
        var named = Path.Combine(blobs.Folder, ext.Length == 0 ? $"{hash}.tmpcopy" : $"{hash}.tmpcopy.{ext}");
        File.Move(temp, named, overwrite: true);
        await Task.Run(() => blobs.Store(named, hash));
        File.Delete(named);
    }

    // MARK: The booth's view of its backups

    public void RefreshBackups()
    {
        var names = host.Tokens.All.Select(t => FollowPresence.BackupName(t.Name)).OfType<string>().Distinct().Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        if (!names.SequenceEqual(Backups)) Backups = names;
    }

    private void BackupsHeard(IReadOnlySet<string> names)
    {
        RefreshBackups();
        if (ConnectedBackups.Except(names).FirstOrDefault() is { } gone && Backups.Contains(gone) && host.InServiceWindow())
        {
            DroppedBackup = gone;
            Log.Info($"follow.backupDropped: {gone}");
        }
        if (DroppedBackup is { } dropped && names.Contains(dropped)) DroppedBackup = null;
        ConnectedBackups = names;
        Raise();
    }

    public void HideDroppedBackup()
    {
        DroppedBackup = null;
        Raise();
    }

    /// <summary>On a booth: forgets a backup (its key stops working; it pairs again with the code).</summary>
    public void Forget(string backup)
    {
        foreach (var token in host.Tokens.All.Where(t => FollowPresence.BackupName(t.Name) == backup).ToList()) host.Tokens.Revoke(token.Id);
        ConnectedBackups = ConnectedBackups.Where(n => n != backup).ToHashSet();
        if (DroppedBackup == backup) DroppedBackup = null;
        RefreshBackups();
        Log.Info($"follow.forgotBackup: {backup}");
        Raise();
    }

    // MARK: Finding the booth again

    public void TryAgain()
    {
        rewatches = 0;
        Reconnect(null);
    }

    private void WatchForBooth()
    {
        if (watch is not null || rewatches >= MaxRewatches || saved.Pairing is not { } pairing) return;
        var browser = new BonjourBrowser(Me);
        watch = browser;
        var booth = pairing.StationHexId;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        browser.Changed += peers =>
        {
            if (peers.FirstOrDefault(p => p.StationHexId == booth) is not { } peer) return;
            dispatcher?.BeginInvoke(() =>
            {
                if (watch != browser) return;
                rewatches++;
                Log.Info($"follow.boothSeen: {peer.Host}:{peer.Port}");
                Reconnect(peer);
            });
        };
        try
        {
            browser.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Warn($"Couldn't look for {pairing.Name} on the network: {ex.Message}");
            StopWatching();
        }
    }

    private void StopWatching()
    {
        watch?.Stop();
        watch = null;
    }

    private void Reconnect(BonjourBrowser.Peer? peer)
    {
        if (saved.Pairing is not { } pairing || LoadKey(pairing.StationHexId) is not { } key) return;
        if (peer is not null && (peer.Host != pairing.Host || peer.Port != pairing.Port))
        {
            pairing.Host = peer.Host;
            pairing.Port = peer.Port;
            Store();
        }
        Begin(pairing, key);
        Raise();
    }

    // MARK: Requests

    private static string Url(string host, int port, string path) =>
        new LocalApiClient.Endpoint(host, port, "").Http(path).ToString();

    private static HttpRequestMessage Request(HttpMethod method, LocalApiClient.Endpoint at, string path)
    {
        var request = new HttpRequestMessage(method, at.Http(path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", at.Token);
        return request;
    }

    private static async Task<T?> Get<T>(LocalApiClient.Endpoint at, string path, TimeSpan? timeout = null) where T : class
    {
        try
        {
            using var cancel = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
            using var request = Request(HttpMethod.Get, at, path);
            using var response = await Http.SendAsync(request, cancel.Token);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<T>(Json, cancel.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static async Task<T?> Post<T>(LocalApiClient.Endpoint at, string path, object? body, TimeSpan timeout) where T : class
    {
        try
        {
            using var cancel = new CancellationTokenSource(timeout);
            using var request = Request(HttpMethod.Post, at, path);
            if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: Json);
            using var response = await Http.SendAsync(request, cancel.Token);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<T>(Json, cancel.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static async Task Post(LocalApiClient.Endpoint at, string path, object? body, TimeSpan timeout)
    {
        try
        {
            using var cancel = new CancellationTokenSource(timeout);
            using var request = Request(HttpMethod.Post, at, path);
            if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: Json);
            using var response = await Http.SendAsync(request, cancel.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
        }
    }

    private static string? Message(byte[] body)
    {
        try
        {
            return JsonDocument.Parse(body).RootElement.TryGetProperty("message", out var message) ? message.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // MARK: Keeping it

    private Saved Load()
    {
        try
        {
            if (File.Exists(statePath)) return JsonSerializer.Deserialize<Saved>(File.ReadAllBytes(statePath), Json) ?? new Saved();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn($"Backup settings were unreadable: {ex.Message}");
        }
        return new Saved();
    }

    private void Store()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            LibraryStore.AtomicWrite(statePath, JsonSerializer.SerializeToUtf8Bytes(saved, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Couldn't save the backup settings: {ex.Message}");
        }
    }

    private string KeyPath(string station) =>
        Path.Combine(keyFolder, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(station)))[..16] + ".bin");

    private string? LoadKey(string station)
    {
        try
        {
            var path = KeyPath(station);
            return File.Exists(path) ? Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return null;
        }
    }

    private void SaveKey(string station, string key)
    {
        Directory.CreateDirectory(keyFolder);
        LibraryStore.AtomicWrite(KeyPath(station), ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser));
    }

    private void ClearKey(string station)
    {
        try
        {
            File.Delete(KeyPath(station));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void Raise() => Changed?.Invoke();

    /// <summary>
    /// For the self-test: pairs a second copy of the app (its own data folders) with a booth before
    /// it starts, as Make This the Backup would have, so it follows the booth as soon as it opens.
    /// </summary>
    public static void Prepare(string localRoot, Pairing pairing, string key)
    {
        var controller = new FollowController(localRoot);
        controller.saved.Pairing = pairing;
        controller.Store();
        controller.SaveKey(pairing.StationHexId, key);
    }

    private FollowController(string localRoot)
    {
        host = null!;
        statePath = Path.Combine(localRoot, "follow.json");
        keyFolder = Path.Combine(localRoot, "follow-keys");
        saved = new Saved();
        Presence = new FollowPresence(_ => { });
    }
}
