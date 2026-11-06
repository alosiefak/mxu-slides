using System.Text.Json;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.MotionTemplates;

/// <summary>
/// Where motion templates live in the library, and how each use keeps something a Mac can show.
/// <para>
/// <b>Nothing here is in the shared format.</b> The Mac has no Lottie, and its reader refuses a
/// whole document that names a kind it doesn't know (HOW-IT-WORKS section 41). So:
/// <list type="bullet">
/// <item>The <b>catalogue</b> (one entry per imported template) and the <b>animations</b> (by hash)
/// are Windows extras: "Windows extras/motion templates/" and ".../motion template animations/".</item>
/// <item>A <b>use</b> is an ordinary shared document plus a Windows extra beside it. A lower third
/// (<see cref="MotionUse.Overlay"/>) is an <see cref="Overlay"/> holding one picture, a PNG with
/// alpha of the design at its settled frame: a Mac shows a still lower third that fades in and
/// out, and Windows plays the animation in its place. A full-screen template
/// (<see cref="MotionUse.Background"/>) is a <see cref="MediaItem"/> video rendered from it: a
/// Mac plays the video, and Windows plays the animation live (sharp at any size, with its fields).</item>
/// <item>The use's extra (<see cref="MotionTemplateUse"/>) is fingerprinted by its document: if a
/// Mac changes the overlay (moves it, swaps its picture) or replaces the video, the fingerprint
/// no longer matches and Windows shows the document as the Mac left it. The Mac's edit wins.</item>
/// </list>
/// </para>
/// Thread-safe. Windows only: the Mac has no twin.
/// </summary>
public sealed class MotionTemplateStore
{
    public const string CatalogueKind = "motion templates";
    public const string LottieKind = "motion template animations";
    public const string UseKind = "motion template uses";

    /// <summary>The overlay pack uses of templates go in, and the media folder for their stills and videos.</summary>
    public const string Pack = "Motion Templates";

    private readonly LibraryStore library;
    private readonly Lock gate = new();
    // Animations never change once stored (they're kept by hash), so each is read once.
    private readonly Dictionary<string, LottieDocument> animations = new(StringComparer.Ordinal);

    public MotionTemplateStore(LibraryStore library) => this.library = library;

    public LibraryStore Library => library;

    // MARK: The catalogue

    /// <summary>Every template, starters first, then by name.</summary>
    public IReadOnlyList<MotionTemplateInfo> All() =>
        [.. library.Extras.Owners(CatalogueKind).Select(Get).OfType<MotionTemplateInfo>()
            .OrderByDescending(t => t.Starter).ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)];

    public MotionTemplateInfo? Get(string? id) => id is null ? null : library.Extras.Get<MotionTemplateInfo>(CatalogueKind, id);

    /// <summary>
    /// Adds an animation to the catalogue. Importing the same animation again gives back the entry
    /// it already has (by hash), so a template isn't listed twice. <paramref name="id"/> fixes the id
    /// (the starters have their own); otherwise a new one is made.
    /// </summary>
    public MotionTemplateInfo Add(LottieDocument document, string name, string? sourceFileName = null, bool starter = false,
        string? id = null, string? description = null, MotionUse? suggestedUse = null)
    {
        var hash = StoreLottie(document);
        if (id is null && All().FirstOrDefault(t => t.LottieHash == hash && !t.Starter) is { } existing) return existing;
        var info = new MotionTemplateInfo
        {
            Id = id ?? LibraryStore.NewId(),
            Name = string.IsNullOrWhiteSpace(name) ? "Motion template" : name.Trim(),
            LottieHash = hash,
            SourceFileName = sourceFileName,
            ImportedAt = DateTimeOffset.Now.ToUnixTimeMilliseconds() / 1000.0,
            Starter = starter,
            SuggestedUse = suggestedUse ?? SuggestUse(document),
            Width = document.Width,
            Height = document.Height,
            FrameRate = document.FrameRate,
            Seconds = document.Seconds,
            Description = description,
        };
        library.Extras.Save(CatalogueKind, info.Id, info);
        return info;
    }

    public void Rename(string id, string name)
    {
        if (Get(id) is { } info && name.Trim() is { Length: > 0 } trimmed) library.Extras.Save(CatalogueKind, id, info with { Name = trimmed });
    }

    /// <summary>Takes a template out of the catalogue. Its uses keep working: each carries its own animation.</summary>
    public void Remove(string id) => library.Extras.Delete(CatalogueKind, id);

    // MARK: Animations, by hash

    /// <summary>Keeps an animation (if it isn't kept already) and returns its hash.</summary>
    public string StoreLottie(LottieDocument document)
    {
        var bytes = document.ToUtf8();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        lock (gate)
        {
            if (!animations.ContainsKey(hash))
            {
                if (library.Extras.Get<StoredAnimation>(LottieKind, hash) is null)
                    library.Extras.Save(LottieKind, hash, new StoredAnimation(JsonSerializer.Deserialize<JsonElement>(bytes)));
                animations[hash] = LottieDocument.Parse(bytes);
            }
        }
        return hash;
    }

    /// <summary>The animation with this hash, or null when it isn't in the library (or can't be read).</summary>
    public LottieDocument? Lottie(string? hash)
    {
        if (string.IsNullOrEmpty(hash)) return null;
        lock (gate)
        {
            if (animations.TryGetValue(hash, out var known)) return known;
        }
        // Read outside the lock: a big animation takes a moment, and other templates needn't wait.
        if (library.Extras.Get<StoredAnimation>(LottieKind, hash) is not { } stored) return null;
        try
        {
            var document = LottieDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(stored.Animation));
            lock (gate) animations[hash] = document;
            return document;
        }
        catch (LottieFormatException)
        {
            return null;
        }
    }

    /// <summary>An animation as kept in the extras: the Lottie JSON itself, inside the extra's envelope.</summary>
    private sealed record StoredAnimation(JsonElement Animation);

    // MARK: Uses

    /// <summary>The template an overlay plays on Windows, or null: it isn't one, or a Mac has changed it since.</summary>
    public MotionTemplateUse? UseOf(Overlay overlay) =>
        library.Extras.Get<MotionTemplateUse>(UseKind, overlay.Id, basis: Basis(overlay)) is { Use: MotionUse.Overlay } use ? use : null;

    /// <summary>The template a video plays on Windows, or null: it isn't one, or its file has been replaced since (on a Mac, say).</summary>
    public MotionTemplateUse? UseOf(MediaItem item) =>
        library.Extras.Get<MotionTemplateUse>(UseKind, item.Id, basis: item.FileHash) is { Use: MotionUse.Background } use ? use : null;

    /// <summary>Remembers what an overlay plays, fingerprinted by the overlay as it is now (save the overlay and its still first).</summary>
    public void SaveUse(Overlay overlay, MotionTemplateUse use) =>
        library.Extras.Save(UseKind, overlay.Id, use with { Use = MotionUse.Overlay }, Basis(overlay));

    /// <summary>Remembers what a video plays, fingerprinted by its file (save the item first).</summary>
    public void SaveUse(MediaItem item, MotionTemplateUse use) =>
        library.Extras.Save(UseKind, item.Id, use with { Use = MotionUse.Background }, item.FileHash);

    public void ForgetUse(string ownerId) => library.Extras.Delete(UseKind, ownerId);

    /// <summary>
    /// The part of an overlay its use is fingerprinted by: its objects, its build order, and the
    /// files of the pictures it shows. A Mac moving the still, retiming it or swapping its picture
    /// changes it.
    /// </summary>
    public string Basis(Overlay overlay)
    {
        var files = overlay.Objects.Where(o => o.MediaId is not null)
            .Select(o => library.Get<MediaItem>(o.MediaId)?.FileHash ?? "").ToList();
        return WindowsExtras.Fingerprint(new OverlayBasis(overlay.Objects, overlay.AnimationOrder, files));
    }

    private sealed record OverlayBasis(List<SlideObject> Objects, List<string>? Order, List<string> Files);

    /// <summary>What a use's still or video is made from: when any of it changes, the fallback is made again.</summary>
    public static string FallbackStamp(MotionTemplateUse use) =>
        WindowsExtras.Fingerprint(new FallbackBasis(use.LottieHash, use.Values.OrderBy(v => v.Key, StringComparer.Ordinal).ToList(),
            use.Playback, use.Use, use.BackgroundColor));

    private sealed record FallbackBasis(string Hash, List<KeyValuePair<string, string>> Values, MotionPlayback Playback, MotionUse Use, string Background);

    /// <summary>A use's fallback no longer shows its values (they were changed since it was made).</summary>
    public static bool FallbackIsStale(MotionTemplateUse use) => use.FallbackStamp != FallbackStamp(use);

    // MARK: The shared documents

    /// <summary>
    /// The overlay a Mac reads for a lower-third use: one picture (<paramref name="stillMediaId"/>,
    /// the PNG of the settled design) where the animation plays, fading in over the template's In
    /// (at least a quarter, at most a second) and out over its Out, so even the still arrives and
    /// leaves gently. Filed in the "Motion Templates" pack.
    /// </summary>
    public static Overlay FallbackOverlay(string name, string stillMediaId, MotionTimeline timeline, SceneRect frame, string category = "Lower Thirds")
    {
        static double Gentle(double seconds) => Math.Round(Math.Clamp(seconds, 0.25, 1.0), 2);
        return new Overlay
        {
            Id = LibraryStore.NewId(),
            Name = string.IsNullOrWhiteSpace(name) ? "Motion template" : name.Trim(),
            Folder = OverlayLibrary.FolderFor(Pack, category),
            Objects =
            [
                new SlideObject
                {
                    Id = LibraryStore.NewId(),
                    ObjectKind = SlideObjectKind.Media,
                    Name = "Motion template",
                    Text = "",
                    X = Math.Round(frame.X, 2),
                    Y = Math.Round(frame.Y, 2),
                    Width = Math.Round(frame.Width, 2),
                    Height = Math.Round(frame.Height, 2),
                    MediaId = stillMediaId,
                    MediaScaleMode = MediaScaleMode.Fit,
                    AnimationSteps =
                    [
                        new AnimationStep { Id = LibraryStore.NewId(), Kind = AnimationKind.In, Animation = StepAnimation.Fade,
                            Trigger = AnimationTrigger.WithPrevious, DurationSeconds = Gentle(timeline.InSeconds) },
                        new AnimationStep { Id = LibraryStore.NewId(), Kind = AnimationKind.Out, Animation = StepAnimation.Fade,
                            Trigger = AnimationTrigger.OnDismiss, DurationSeconds = Gentle(timeline.TakeDownSeconds) },
                    ],
                },
            ],
        };
    }

    /// <summary>The prefix of the picture ids the live animations are drawn under (Rendering/MediaHub.Motion.cs).</summary>
    public const string PicturePrefix = "motion::";

    /// <summary>
    /// The copy of a use's overlay that Windows puts on the program. It's never saved. Its picture
    /// shows the live animation (<paramref name="pictureId"/>, a <see cref="PicturePrefix"/> id)
    /// instead of the still, and its steps are swapped: no In (the animation brings itself in),
    /// and an Out that keeps it fully up while the animation's own Out plays, then lets it go.
    /// The show takes an overlay down once its Outs have played (ShowState.SweepFinishedExits),
    /// so this is what lets the template's Out finish on the screen. A template without an Out
    /// fades instead (<see cref="MotionTimeline.FadeSeconds"/>). Anything else on the overlay (a
    /// text box added in the overlay editor) is left as it is.
    /// </summary>
    public static Overlay LiveCopy(Overlay shared, MotionTemplateUse use, string pictureId, MotionTimeline timeline)
    {
        var copy = MxuJson.Clone(shared);
        foreach (var o in copy.Objects.Where(o => o.ObjectKind == SlideObjectKind.Media && o.MediaId == use.FallbackMediaId))
        {
            o.MediaId = pictureId;
            o.AnimationSteps = [HoldThenGo(timeline)];
            o.Builds = null;
        }
        return copy;
    }

    /// <summary>An Out step that holds for the template's Out and then cuts (or, with no Out, a short fade).</summary>
    internal static AnimationStep HoldThenGo(MotionTimeline timeline)
    {
        const double cut = 0.05;
        if (!timeline.Segments.HasOut)
            return new AnimationStep { Id = "motion-out", Kind = AnimationKind.Out, Animation = StepAnimation.Fade, Trigger = AnimationTrigger.OnDismiss,
                DurationSeconds = MotionTimeline.FadeSeconds };
        var hold = Math.Max(0, timeline.TakeDownSeconds - cut);
        return new AnimationStep { Id = "motion-out", Kind = AnimationKind.Out, Animation = StepAnimation.Fade, Trigger = AnimationTrigger.OnDismiss,
            DelaySeconds = Math.Round(hold, 3), DurationSeconds = cut };
    }

    /// <summary>
    /// Where an animation of <paramref name="width"/> × <paramref name="height"/> sits on the slide:
    /// as big as fits, centred (a 16:9 lower third fills a 16:9 slide exactly).
    /// </summary>
    public static SceneRect Fit(double width, double height, SceneSize canvas)
    {
        if (width <= 0 || height <= 0) return new SceneRect(canvas);
        var scale = Math.Min(canvas.Width / width, canvas.Height / height);
        var w = width * scale;
        var h = height * scale;
        return new SceneRect((canvas.Width - w) / 2, (canvas.Height - h) / 2, w, h);
    }

    /// <summary>
    /// A guess from the file alone at what a template is for: a solid layer or a shape filling the
    /// frame, or a name saying so, is a background. (The app also looks at the drawn picture: a
    /// design that covers almost all of it is a background.)
    /// </summary>
    public static MotionUse SuggestUse(LottieDocument document)
    {
        var name = document.Name.ToLowerInvariant();
        if (name.Contains("background") || name.Contains("backdrop") || name.Contains("full screen") || name.Contains("fullscreen")) return MotionUse.Background;
        foreach (var (_, layer) in document.LayerNodes().Where(l => !l.Path.StartsWith("A:", StringComparison.Ordinal)))
            if (LottieDocument.Number(layer["ty"]) == LottieLayer.Solid
                && LottieDocument.Number(layer["sw"]) >= document.Width * 0.95 && LottieDocument.Number(layer["sh"]) >= document.Height * 0.95)
                return MotionUse.Background;
        return MotionUse.Overlay;
    }
}
