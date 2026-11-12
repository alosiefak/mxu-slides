using System.Text.Json;
using System.Text.Json.Serialization;

namespace MxU.Slides.LocalApi;

/// <summary>
/// A backup replaying its booth's fires (MxU PR #12, FollowMode.swift): which fires replay (only
/// the roots: their effects follow from them here), the catch-up that brings a backup that joins
/// late in step with what the booth shows, and each fire as the bridge call it runs.
/// </summary>
public static class FollowReplay
{
    public static bool Replays(ApiFireEvent fire) =>
        fire.Cause is null && fire.Kind is not (ApiFireEvent.Kinds.Role or ApiFireEvent.Kinds.Capture or ApiFireEvent.Kinds.Quit);

    /// <summary>A library fire (its "service item" is the presentation itself) replays by slide, not as a service item.</summary>
    public static ApiFireSlideCommand Command(ApiFireSlideCommand slide) =>
        slide.ServiceItemId is not null && slide.ServiceItemId == slide.PresentationId ? slide with { ServiceItemId = null } : slide;

    /// <summary>The fires that make this computer show what the booth shows: clears for what it shouldn't, then what's missing.</summary>
    public static List<ApiFireEvent> CatchUp(ApiShowStatus booth, ApiShowStatus here)
    {
        var events = new List<ApiFireEvent>();
        if (booth.LiveSlide is null && here.LiveSlide is not null)
            events.Add(new ApiFireEvent { Kind = ApiFireEvent.Kinds.Clear, Clear = new ApiClearCommand(Function: "slides") });
        foreach (var layer in here.MediaLayers.Keys.Order(StringComparer.Ordinal))
        {
            if (!booth.MediaLayers.ContainsKey(layer))
                events.Add(new ApiFireEvent { Kind = ApiFireEvent.Kinds.Clear, Clear = new ApiClearCommand(Layer: layer) });
        }
        var boothOverlays = booth.Overlays.Select(o => o.Id).ToHashSet();
        var hereOverlays = here.Overlays.Select(o => o.Id).ToHashSet();
        foreach (var id in hereOverlays.Except(boothOverlays).Order(StringComparer.Ordinal))
            events.Add(new ApiFireEvent { Kind = ApiFireEvent.Kinds.DismissOverlay, Id = id });
        foreach (var (layer, content) in booth.MediaLayers.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (here.MediaLayers.GetValueOrDefault(layer)?.MediaId != content.MediaId)
                events.Add(new ApiFireEvent { Kind = ApiFireEvent.Kinds.Media, Id = content.MediaId });
        }
        foreach (var overlay in booth.Overlays.Where(o => !hereOverlays.Contains(o.Id)))
            events.Add(new ApiFireEvent { Kind = ApiFireEvent.Kinds.Overlay, Id = overlay.Id });
        return events;
    }

    /// <summary>Runs a fire on this computer, through the same bridge a remote control uses.</summary>
    public static async Task Apply(ApiFireEvent fire, IApiBridge bridge)
    {
        switch (fire.Kind)
        {
            case ApiFireEvent.Kinds.Slide:
                if (fire.Slide is { } slide) await bridge.FireSlideAsync(Command(slide));
                break;
            case ApiFireEvent.Kinds.Step:
                await bridge.AdvanceAsync(fire.Steps ?? 1, fire.Settled ?? false);
                break;
            case ApiFireEvent.Kinds.Clear:
                if (fire.Clear is { } clear) bridge.Clear(clear);
                break;
            case ApiFireEvent.Kinds.ClearAll: bridge.ClearAll(); break;
            case ApiFireEvent.Kinds.Media:
                if (fire.Id is { } media) bridge.FireMedia(media);
                break;
            case ApiFireEvent.Kinds.Overlay:
                if (fire.Id is { } overlay) bridge.FireOverlay(overlay);
                break;
            case ApiFireEvent.Kinds.DismissOverlay: bridge.DismissOverlay(fire.Id ?? ""); break;
            case ApiFireEvent.Kinds.Alert:
                if (fire.Alert is { } alert) bridge.FireAlert(alert);
                break;
            case ApiFireEvent.Kinds.DismissAlert: bridge.DismissAlert(); break;
            case ApiFireEvent.Kinds.Timer:
                if (fire.Id is { } timer && fire.Timer is { } action) bridge.TimerAction(timer, action);
                break;
            case ApiFireEvent.Kinds.Combo:
                if (fire.Id is { } combo) bridge.FireActionCombo(combo);
                break;
            case ApiFireEvent.Kinds.OutputPreset: bridge.ActivateOutputPreset(fire.Id); break;
            case ApiFireEvent.Kinds.AudioItem:
                if (fire.Id is { } item) bridge.FireAudioItem(item);
                break;
            case ApiFireEvent.Kinds.AudioPlaylist:
                if (fire.Id is { } playlist) bridge.FireAudioPlaylist(playlist, fire.EntryId);
                break;
            case ApiFireEvent.Kinds.StopAudio: bridge.StopAudio(fire.AudioStop ?? new ApiAudioStopCommand()); break;
            case ApiFireEvent.Kinds.AudioTransport:
                if (fire.AudioTransport is { } transport) bridge.AudioTransport(transport);
                break;
            case ApiFireEvent.Kinds.MediaTransport:
                if (fire.Id is { } id && fire.MediaTransport is { } command) bridge.MediaTransport(id, command);
                break;
        }
    }
}

/// <summary>
/// What a "Stay ready" backup last read from its booth (the screens, the videos and the music),
/// to put up where it would be now when it takes over. Port of FollowStandby.
/// </summary>
public sealed record FollowStandby(ApiShowStatus Show, IReadOnlyList<ApiTransportRow> Transport, ApiAudioStatus? Audio, DateTimeOffset At)
{
    public static bool ShowsAnything(ApiShowStatus show, ApiAudioStatus audio) =>
        show.LiveSlide is not null || show.MediaLayers.Count > 0 || show.Overlays.Count > 0 || audio.Buses.Any(b => b.Playing);

    /// <summary>Where a playing video or song is now, from where it was then (round a loop, never past its end).</summary>
    public static double Position(double position, double? duration, bool playing, bool looping, DateTimeOffset at, DateTimeOffset now)
    {
        if (!playing) return position;
        var moved = position + Math.Max(0, (now - at).TotalSeconds);
        if (duration is not { } length || length <= 0) return moved;
        return looping ? moved % length : Math.Min(moved, length);
    }

    public List<ApiFireEvent> Takeover(ApiShowStatus here, DateTimeOffset now)
    {
        var events = FollowReplay.CatchUp(Show, here);
        if (Show.LiveSlide is { } live)
            events.Add(new ApiFireEvent
            {
                Kind = ApiFireEvent.Kinds.Slide,
                Slide = new ApiFireSlideCommand(live.PresentationId, live.SlideId, ServiceItemId: live.ServiceItemId, Occurrence: live.Occurrence),
            });
        foreach (var row in Transport)
        {
            var position = Position(row.Position, row.Duration, row.Playing, row.Looping, At, now);
            events.Add(new ApiFireEvent { Kind = ApiFireEvent.Kinds.MediaTransport, Id = row.MediaId, MediaTransport = new ApiMediaTransportCommand("seek", position) });
            if (!row.Playing) events.Add(new ApiFireEvent { Kind = ApiFireEvent.Kinds.MediaTransport, Id = row.MediaId, MediaTransport = new ApiMediaTransportCommand("pause") });
        }
        foreach (var bus in (Audio?.Buses ?? []).Where(b => b.Playing))
        {
            if (bus.PlaylistId is { } playlist)
            {
                // A booth that doesn't say which song is on can't be followed to it.
                if (bus.EntryId is not { } entry) continue;
                events.Add(new ApiFireEvent { Kind = ApiFireEvent.Kinds.AudioPlaylist, Id = playlist, EntryId = entry });
            }
            else if (bus.AudioItemId is { } item)
            {
                events.Add(new ApiFireEvent { Kind = ApiFireEvent.Kinds.AudioItem, Id = item });
            }
            else
            {
                continue;
            }
            if (bus.Position is { } at)
                events.Add(new ApiFireEvent { Kind = ApiFireEvent.Kinds.AudioTransport, AudioTransport = new ApiAudioTransportCommand("seek", Position(at, bus.Duration, true, false, At, now)) });
        }
        return events;
    }
}

/// <summary>Pairing a backup: "code, stationHexId, name" in, an Operate key named "Backup: …" out.</summary>
public sealed record ApiFollowPairRequest([property: JsonRequired] string Code, [property: JsonRequired] string StationHexId, [property: JsonRequired] string Name);

public sealed record ApiFollowPairResponse([property: JsonRequired] string Token);

/// <summary>
/// The booth's pairing code: four digits, retired after five wrong tries until a new one is made.
/// A right code gets an Operate (Control) key named for the backup, replacing any it had. Port of FollowPairing.
/// </summary>
public sealed class FollowPairing(string? code = null)
{
    public const int MaxAttempts = 5;

    private readonly Lock gate = new();
    private string code = code ?? NewCode();
    private int misses;

    public static string NewCode() => Random.Shared.Next(0, 10000).ToString("D4", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The code to show, or null once it's been guessed at too often.</summary>
    public string? Current
    {
        get
        {
            lock (gate) return misses < MaxAttempts ? code : null;
        }
    }

    public string Refresh()
    {
        lock (gate)
        {
            code = NewCode();
            misses = 0;
            return code;
        }
    }

    public ApiHandlerResponse Answer(byte[] body, ApiTokenStore tokens)
    {
        ApiFollowPairRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<ApiFollowPairRequest>(body, ApiJson.Options);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException)
        {
            request = null;
        }
        if (request is null) return ApiHandlerResponse.Json(ApiError.BadRequest("Send code, stationHexId and name.").Body, 400);
        bool matched;
        lock (gate)
        {
            matched = misses < MaxAttempts && request.Code == code;
            if (!matched) misses++;
        }
        if (!matched)
            return ApiHandlerResponse.Json(new ApiError(403, "wrong_code", "That code doesn't match. Check the code this computer shows.").Body, 403);
        var name = FollowPresence.Prefix + request.Name;
        foreach (var old in tokens.All.Where(t => t.Name == name).ToList()) tokens.Revoke(old.Id);
        var (_, secret) = tokens.Create(name, ApiScope.Control);
        return ApiHandlerResponse.Json(new ApiFollowPairResponse(secret));
    }
}

/// <summary>
/// The booth's view of its backups: a backup pings every second, and one quiet for
/// <c>lostAfter</c> has dropped. Keys that aren't a backup's never count. Port of FollowPresence.
/// </summary>
public sealed class FollowPresence(TimeSpan lostAfter, Action<IReadOnlySet<string>> changed)
{
    public const string Prefix = "Backup: ";

    public FollowPresence(Action<IReadOnlySet<string>> changed) : this(TimeSpan.FromSeconds(4), changed)
    {
    }

    public static string? BackupName(string tokenName) => tokenName.StartsWith(Prefix, StringComparison.Ordinal) ? tokenName[Prefix.Length..] : null;

    private readonly Lock gate = new();
    private readonly Dictionary<string, Guid> heard = [];

    public void Heard(string tokenName)
    {
        if (BackupName(tokenName) is not { } name) return;
        var mark = Guid.NewGuid();
        bool isNew;
        HashSet<string> names;
        lock (gate)
        {
            isNew = !heard.ContainsKey(name);
            heard[name] = mark;
            names = [.. heard.Keys];
        }
        if (isNew) changed(names);
        // Not show work: a timer on the shared pool is fine to notice a backup gone quiet.
        _ = Task.Delay(lostAfter).ContinueWith(_ => Expire(name, mark), TaskScheduler.Default);
    }

    private void Expire(string name, Guid mark)
    {
        HashSet<string> names;
        lock (gate)
        {
            if (!heard.TryGetValue(name, out var last) || last != mark) return;
            heard.Remove(name);
            names = [.. heard.Keys];
        }
        changed(names);
    }
}

/// <summary>While this computer backs up another, everything but reading (and the follow routes) answers "backing up".</summary>
public static class FollowHold
{
    public const string Prefix = "/v1/follow";

    public static ApiError? Error(ApiRoute route, string following) =>
        route.Scope == ApiScope.View || route.Path.StartsWith(Prefix, StringComparison.Ordinal)
            ? null
            : new ApiError(409, "backing_up", $"{following} is in charge. This computer answers again once it takes over.");
}

/// <summary>Who is in charge, by term: the newer term wins (a backup that took over, a booth that took back).</summary>
public sealed record ApiFollowRoleCommand([property: JsonRequired] int Term, [property: JsonRequired] string InCharge, [property: JsonRequired] string Name);

/// <param name="Role">none, backingUp, inCharge or steppedDown.</param>
/// <param name="Health">connecting, live, lost, frozen or gaveUp, for a backup.</param>
/// <param name="Launch">This run of the app: a booth that comes back with the same one only dropped off the network.</param>
public sealed record ApiFollowStatus(
    [property: JsonRequired] string Role, string? Name = null, string? Health = null, int? Term = null, string? StreamPresetId = null, string? Launch = null);

/// <summary>
/// A booth's items for its backup to hold (GET /v1/follow/files): each by kind and id with its
/// version ("heads"), and the media file it needs by hash. Port of FollowFiles.
/// </summary>
public static class FollowFiles
{
    /// <param name="Heads">The item's version. A Mac's are Automerge change hashes; Windows' are "json-" and its file's hash.</param>
    /// <param name="Area">"team" for an item the church shares.</param>
    public sealed record Item(
        [property: JsonRequired] string Kind, [property: JsonRequired] string Id, [property: JsonRequired] IReadOnlyList<string> Heads,
        string? File = null, string? Ext = null, string? Area = null)
    {
        public bool Equals(Item? other) =>
            other is not null && Kind == other.Kind && Id == other.Id && Heads.SequenceEqual(other.Heads) && File == other.File && Ext == other.Ext && Area == other.Area;

        public override int GetHashCode() => HashCode.Combine(Kind, Id);
    }

    public sealed record ItemList([property: JsonRequired] IReadOnlyList<Item> Items);

    /// <summary>One item's whole save: a Mac's Automerge document, or a Windows library file's JSON.</summary>
    public sealed record Document([property: JsonRequired] byte[] Bytes);

    /// <summary>The items whose version differs from the copy here, and the media files this computer lacks.</summary>
    public static (List<Item> Documents, List<string> Files) Wanted(IReadOnlyList<Item> items, IReadOnlyDictionary<string, IReadOnlyList<string>> heads, IReadOnlySet<string> filesHere)
    {
        var documents = items.Where(i => !heads.TryGetValue($"{i.Kind}/{i.Id}", out var held) || !held.SequenceEqual(i.Heads)).ToList();
        var files = items.Select(i => i.File).OfType<string>().ToHashSet().Except(filesHere).Order(StringComparer.Ordinal).ToList();
        return (documents, files);
    }

    /// <summary>Copies this backup made that the booth no longer holds.</summary>
    public static List<string> Gone(IReadOnlySet<string> copied, IReadOnlyList<Item> booth) =>
        copied.Except(booth.Select(i => $"{i.Kind}/{i.Id}")).Order(StringComparer.Ordinal).ToList();

    /// <summary>A media file's name: a lower-case SHA-256, never a path.</summary>
    public static bool IsHash(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>
/// What a backup and its booth need from the server (MxU PR #12): the app's heartbeat for pongs,
/// the hold while backing up, the pairing door, the media files, and who is pinging.
/// </summary>
/// <param name="Beat">A count the app's UI thread moves on; a pong carries it, so a backup can tell a frozen app.</param>
/// <param name="Hold">An error for a route this computer won't run now (it's backing up), else null.</param>
/// <param name="Pair">Answers POST /v1/follow/pair (FollowPairing.Answer).</param>
/// <param name="Blob">The file of a media hash, for GET /v1/follow/blobs/{hash}.</param>
/// <param name="Heard">Called with a key's name each time it pings (FollowPresence).</param>
/// <param name="Unpair">Called for POST /v1/follow/unpair with the key that called.</param>
public sealed record FollowHooks(
    Func<ulong>? Beat = null, Func<ApiRoute, ApiError?>? Hold = null, Func<byte[], ApiHandlerResponse>? Pair = null,
    Func<string, string?>? Blob = null, Action<string>? Heard = null, Action<ApiToken>? Unpair = null);
