using System.Text;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;
using MxU.Slides.ProImport.Rtf;
using RV = Pro.SerializationInterop.RVProtoData;

namespace MxU.Slides.ProImport;

public sealed class ProMappedDocument
{
    public required Presentation Presentation { get; init; }
    public required List<ProMediaWant> MediaWants { get; init; }
    public required List<string> Warnings { get; init; }
    public Dictionary<string, string> GroupHotKeys { get; init; } = [];
}

/// <summary>A ProPresenter 7 presentation → an MxU presentation, with media left as placeholders to resolve.</summary>
public static class ProDocumentMapper
{
    public const string PlaceholderPrefix = "pro-media://";

    private static readonly IReadOnlyDictionary<string, string> NoTimers = new Dictionary<string, string>();

    public static ProMappedDocument Map(
        RV.Presentation doc, string fallbackName,
        IReadOnlyDictionary<string, string>? timerIdsByProUuid = null,
        IReadOnlyDictionary<string, string>? timerIdsByName = null)
    {
        timerIdsByProUuid ??= NoTimers;
        timerIdsByName ??= NoTimers;
        var state = new MapState();
        var name = doc.Name.Length == 0 ? fallbackName : doc.Name;

        var arrangementGroupIds = doc.Arrangements.SelectMany(a => a.GroupIdentifiers.Select(u => u.Lower())).ToHashSet();
        var sections = new List<PresentationSection>();
        var groupOfCue = new Dictionary<string, string>();
        var groupHotKeys = new Dictionary<string, string>();
        foreach (var cueGroup in doc.CueGroups)
        {
            var group = cueGroup.Group ?? new RV.Group();
            var groupId = group.Uuid.Lower();
            if (group.Name.Length == 0 && !arrangementGroupIds.Contains(groupId)) continue;
            sections.Add(new PresentationSection
            {
                Id = groupId, Name = group.Name, ColorHex = group.Color is { } color ? HexColor(color) : null,
            });
            if (group.HotKey is { } hotKey && group.Name.Length > 0 && HotKeyLetter(hotKey.Code) is { } letter)
                groupHotKeys.TryAdd(NormalizedGroupName(group.Name), letter);
            foreach (var cueId in cueGroup.CueIdentifiers) groupOfCue[cueId.Lower()] = groupId;
        }

        var docTransition = doc.Transition is { } rvTransition ? Transition(rvTransition, state) : null;

        var cueById = new Dictionary<string, RV.Cue>();
        foreach (var cue in doc.Cues) cueById.TryAdd(cue.Uuid.Lower(), cue);
        var orderedCueIds = new List<string>();
        var seenCueIds = new HashSet<string>();
        foreach (var cueGroup in doc.CueGroups)
            foreach (var id in cueGroup.CueIdentifiers)
                if (seenCueIds.Add(id.Lower())) orderedCueIds.Add(id.Lower());
        foreach (var cue in doc.Cues)
            if (seenCueIds.Add(cue.Uuid.Lower())) orderedCueIds.Add(cue.Uuid.Lower());

        var slides = new List<Slide>();
        (int Width, int Height)? canvas = null;
        foreach (var cueId in orderedCueIds)
        {
            if (!cueById.TryGetValue(cueId, out var cue)) continue;
            if (MapCue(cue, groupOfCue, timerIdsByProUuid, timerIdsByName, docTransition, ref canvas, state) is { } slide)
                slides.Add(slide);
        }
        if (canvas is { } deck)
        {
            foreach (var (size, count) in state.OffCanvasSlides.OrderBy(p => p.Key, StringComparer.Ordinal))
                state.Warn($"{count} slide(s) are {size}, not the deck's {deck.Width}×{deck.Height} — they keep Pro's coordinates");
        }
        if (slides.Count == 0) slides = [new Slide { Id = SwiftCompat.NewId(), Name = "", Objects = [] }];

        sections = EnforceContiguity(slides, sections, state);
        var referenced = slides.Select(s => s.SectionId).OfType<string>().ToHashSet();
        sections.RemoveAll(s => !referenced.Contains(s.Id));

        var arrangements = new List<Arrangement>();
        foreach (var arrangement in doc.Arrangements)
        {
            var ids = arrangement.GroupIdentifiers.Select(u => u.Lower()).ToList();
            var known = ids.Where(id => sections.Any(s => s.Id == id)).ToList();
            if (known.Count < ids.Count)
                state.Warn($"arrangement \"{arrangement.Name}\" referenced {ids.Count - known.Count} missing group(s)");
            if (known.Count == 0) continue;
            arrangements.Add(new Arrangement { Id = arrangement.Uuid.Lower(), Name = arrangement.Name, SectionIds = known });
        }
        var selectedArrangement = doc.SelectedArrangement.Lower();
        var defaultArrangementId = arrangements.FirstOrDefault(a => a.Id == selectedArrangement)?.Id;

        var music = doc.Music;
        var originalKey = KeyName(music?.Original)
            ?? NormalizedKeyName(music?.OriginalMusicKey ?? "")
            ?? NormalizedKeyName(doc.MusicKey);
        var userKey = KeyName(music?.User) ?? NormalizedKeyName(music?.UserMusicKey ?? "");
        var musicKey = originalKey ?? userKey;
        var displayKey = userKey == musicKey ? null : userKey;

        CCLIInfo? ccli = null;
        if (doc.Ccli is { } c)
        {
            var info = new CCLIInfo();
            if (c.SongNumber > 0) info.SongNumber = (int)c.SongNumber;
            if (c.SongTitle.Length > 0) info.SongTitle = c.SongTitle;
            if (c.Author.Length > 0) info.Author = c.Author;
            if (c.Publisher.Length > 0) info.Publisher = c.Publisher;
            if (c.CopyrightYear > 0) info.CopyrightYear = (int)c.CopyrightYear;
            if (!MxuJson.StructurallyEqual(info, new CCLIInfo())) ccli = info;
        }

        var wide = canvas is (1920, 1080);
        var presentation = new Presentation
        {
            Id = doc.Uuid.Raw().Length == 0 ? SwiftCompat.NewId() : doc.Uuid.Lower(),
            Name = name,
            PresentationKind = PresentationKind.Deck,
            ThemeId = "",
            Slides = slides,
            CanvasWidth = canvas is null || wide ? null : canvas.Value.Width,
            CanvasHeight = canvas is null || wide ? null : canvas.Value.Height,
            Sections = sections.Count == 0 ? null : sections,
            Arrangements = arrangements.Count == 0 ? null : arrangements,
            DefaultArrangementId = defaultArrangementId,
            Ccli = ccli,
            MusicKey = musicKey,
            DisplayKey = displayKey,
            Origin = new PresentationOrigin { Source = PresentationOriginSource.ProPresenter },
        };

        if (doc.SlideShowCase == RV.Presentation.SlideShowOneofCase.SlideShowDuration && doc.SlideShowDuration > 0)
            presentation.AutoAdvance = new AutoAdvance { DelaySeconds = doc.SlideShowDuration, LoopToStart = true };

        if (doc.Background is { IsEnabled: true } background)
        {
            switch (background.FillCase)
            {
                case RV.Background.FillOneofCase.Color:
                    presentation.BackgroundFill = new ObjectFill { FillKind = FillKind.Solid, ColorHex = HexColor(background.Color) };
                    break;
                case RV.Background.FillOneofCase.Gradient:
                    presentation.BackgroundFill = new ObjectFill
                    {
                        FillKind = FillKind.LinearGradient,
                        GradientAngleDegrees = background.Gradient.Angle,
                        GradientStops = background.Gradient.Stops.Select(s => new GradientStop { ColorHex = HexColor(s.Color), Position = s.Position }).ToList(),
                    };
                    break;
            }
        }
        if (doc.Timeline is { } timeline)
            presentation.Timelines = Timelines(timeline, presentation, timerIdsByProUuid, timerIdsByName, state);
        return new ProMappedDocument
        {
            Presentation = presentation, MediaWants = state.Wants, Warnings = state.Warnings, GroupHotKeys = groupHotKeys,
        };
    }

    /// <summary>GroupPalette.normalizedName: letters and digits only, lowercased.</summary>
    public static string NormalizedGroupName(string name)
    {
        var builder = new StringBuilder();
        foreach (var rune in name.EnumerateRunes())
            if (Rune.IsLetterOrDigit(rune) || Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.NonSpacingMark
                    or System.Globalization.UnicodeCategory.SpacingCombiningMark or System.Globalization.UnicodeCategory.EnclosingMark
                    or System.Globalization.UnicodeCategory.LetterNumber or System.Globalization.UnicodeCategory.OtherNumber)
                builder.Append(rune.ToString());
        return builder.ToString().ToLowerInvariant();
    }

    internal static List<PresentationTimeline>? Timelines(
        RV.Presentation.Types.Timeline source, Presentation presentation,
        IReadOnlyDictionary<string, string> timerIdsByProUuid, IReadOnlyDictionary<string, string> timerIdsByName, MapState state)
    {
        var fires = new List<(double At, string SlideId)>();
        var others = new List<TimelineCue>();
        var cues = source.CuesV2.Count == 0 ? source.Cues : source.CuesV2;
        for (var index = 0; index < cues.Count; index++)
        {
            var cue = cues[index];
            if (!double.IsFinite(cue.TriggerTime) || cue.TriggerTime < 0) continue;
            switch (cue.TriggerInfoCase)
            {
                case RV.Presentation.Types.Timeline.Types.Cue.TriggerInfoOneofCase.CueId:
                    fires.Add((cue.TriggerTime, cue.CueId.Lower()));
                    break;
                case RV.Presentation.Types.Timeline.Types.Cue.TriggerInfoOneofCase.Action:
                    var steps = ProWorkspaceMapper.MapActionSteps(
                        cue.Action, $"timeline cue \"{cue.Name}\"", timerIdsByProUuid, timerIdsByName, state) ?? [];
                    if (steps.Count == 0)
                        state.Warn($"timeline: action cue \"{(cue.Name.Length == 0 ? DescribeActionKind(cue.Action) : cue.Name)}\" has no MxU action and was left out");
                    else
                        others.Add(new TimelineCue
                        {
                            Id = $"pro-action-{index}", Lane = PresentationTimelines.ActionsLane,
                            At = PresentationTimelines.SnappedToFrame(cue.TriggerTime), Actions = steps,
                        });
                    break;
            }
        }

        if (source.AudioAction is { ActionTypeDataCase: RV.Action.ActionTypeDataOneofCase.Media } audioAction
            && audioAction.Media.Element is { TypePropertiesCase: RV.Media.TypePropertiesOneofCase.Audio } audio)
        {
            others.Add(new TimelineCue { Id = "pro-audio", Lane = PresentationTimelines.AudioLane, At = 0, MediaId = state.Want(audio) });
        }
        if (fires.Count == 0 && others.Count == 0 && !source.TimecodeEnable) return null;
        if (source.Loop) state.Warn("timeline: it loops in ProPresenter; MxU plays it once");
        var orders = new List<Arrangement?> { null };
        orders.AddRange(presentation.Arrangements ?? []);
        return orders.Select(arrangement =>
        {
            var placed = PresentationTimelines.SlideCues(fires, PlayedBlocks(arrangement, presentation), "pro-slide-");
            if (placed.Missing > 0)
                state.Warn(arrangement is not null
                    ? $"timeline: {placed.Missing} cue(s) name slides the arrangement \"{arrangement.Name}\" doesn't play; its timeline leaves them out"
                    : $"timeline: {placed.Missing} cue(s) name slides that aren't in the presentation");
            var offset = double.IsFinite(source.TimecodeOffset) ? source.TimecodeOffset : 0;
            return new PresentationTimeline
            {
                Id = "pro-timeline-" + (arrangement?.Id ?? "base"),
                ArrangementId = arrangement?.Id,
                TimecodeEnabled = source.TimecodeEnable,
                TimecodeStart = (int)SwiftCompat.Rounded(Math.Max(offset, 0) * PresentationTimelines.FramesPerSecond),
                Length = double.IsFinite(source.Duration) && source.Duration > 0 ? source.Duration : null,
                Cues = placed.Cues.Concat(others).OrderBy(c => c.At).ToList(),
            };
        }).ToList();
    }

    internal static List<TimelinePlayedBlock> PlayedBlocks(Arrangement? arrangement, Presentation presentation)
    {
        if (arrangement is not null)
            return arrangement.SectionIds
                .Select(id => new TimelinePlayedBlock(id, presentation.Slides.Where(s => s.SectionId == id).Select(s => s.Id).ToList()))
                .ToList();
        var blocks = new List<TimelinePlayedBlock>();
        foreach (var slide in presentation.Slides)
        {
            if (blocks.Count > 0 && blocks[^1].SectionId == slide.SectionId) blocks[^1].SlideIds.Add(slide.Id);
            else blocks.Add(new TimelinePlayedBlock(slide.SectionId, [slide.Id]));
        }
        return blocks;
    }

    /// <summary>Swaps media placeholders for library ids, dropping whatever lost its media. Returns a copy.</summary>
    public static Presentation ReplacingMediaIds(Presentation presentation, IReadOnlyDictionary<string, string> map)
    {
        var result = MxuJson.Clone(presentation);

        string? RemapId(string? id) => id is null || !id.StartsWith(PlaceholderPrefix, StringComparison.Ordinal) ? id : map.GetValueOrDefault(id);
        CueMedia? RemapCue(CueMedia? media)
        {
            if (media is null || RemapId(media.MediaId) is not { } mapped) return null;
            media.MediaId = mapped;
            return media;
        }
        ObjectFill? RemapFill(ObjectFill? fill)
        {
            if (fill is null) return null;
            if (fill.MediaId is not null && RemapId(fill.MediaId) is null) return null;
            fill.MediaId = RemapId(fill.MediaId);
            return fill;
        }
        List<SlideAction>? RemapActions(List<SlideAction>? actions)
        {
            if (actions is null) return null;
            var mapped = new List<SlideAction>();
            foreach (var action in actions)
            {
                if (action.AudioItemId is { } audio && audio.StartsWith(PlaceholderPrefix, StringComparison.Ordinal))
                {
                    if (!map.TryGetValue(audio, out var resolved)) continue;
                    action.AudioItemId = resolved;
                }
                if (action.MediaId is { } media && media.StartsWith(PlaceholderPrefix, StringComparison.Ordinal))
                {
                    if (!map.TryGetValue(media, out var resolved)) continue;
                    action.MediaId = resolved;
                }
                mapped.Add(action);
            }
            return mapped.Count == 0 ? null : mapped;
        }

        if (result.Timelines is { } timelines)
        {
            foreach (var timeline in timelines)
            {
                var kept = new List<TimelineCue>();
                foreach (var cue in timeline.Cues)
                {
                    if (cue.MediaId is { } id && id.StartsWith(PlaceholderPrefix, StringComparison.Ordinal))
                    {
                        if (!map.TryGetValue(id, out var mapped)) continue;
                        cue.MediaId = mapped;
                    }
                    if (cue.Actions is not null)
                    {
                        cue.Actions = RemapActions(cue.Actions);
                        if (cue.Actions is null) continue;
                    }
                    kept.Add(cue);
                }
                timeline.Cues = kept;
            }
        }

        result.Background = RemapCue(result.Background);
        foreach (var section in result.Sections ?? []) section.Background = RemapCue(section.Background);
        foreach (var slide in result.Slides)
        {
            slide.Background = RemapCue(slide.Background);
            slide.Actions = RemapActions(slide.Actions);
            var objects = new List<SlideObject>();
            foreach (var obj in slide.Objects)
            {
                var fillLostItsMedia = obj.Fill?.FillKind == FillKind.Media
                    && obj.Fill.MediaId?.StartsWith(PlaceholderPrefix, StringComparison.Ordinal) == true
                    && RemapId(obj.Fill.MediaId) is null;
                obj.Fill = RemapFill(obj.Fill);
                if (obj.TextStyle is { } style) style.Fill = RemapFill(style.Fill);
                if (fillLostItsMedia && obj.Stroke is null && obj.Text.Trim().Length == 0) continue;
                objects.Add(obj);
            }
            slide.Objects = objects;
        }
        return result;
    }

    internal sealed class MapState
    {
        public List<ProMediaWant> Wants { get; } = [];
        public List<string> Warnings { get; } = [];
        private readonly Dictionary<string, string> wantByUuid = [];
        public ProInputPlanBook InputBook { get; set; } = new();
        public Dictionary<string, int> OffCanvasSlides { get; } = [];

        public void Warn(string message)
        {
            if (!Warnings.Contains(message)) Warnings.Add(message);
        }

        public string Want(RV.Media? media, MediaClassification? classification = null)
        {
            media ??= new RV.Media();
            var uuid = media.Uuid.Lower();
            if (wantByUuid.TryGetValue(uuid, out var existing))
            {
                if (classification is not null && Wants.FirstOrDefault(w => w.PlaceholderId == existing) is { Classification: null } earlier)
                    earlier.Classification = classification;
                return existing;
            }
            var placeholder = PlaceholderPrefix + (uuid.Length == 0 ? SwiftCompat.NewId() : uuid);
            var url = media.Url;
            string? absolute = null;
            if (url is { StorageCase: RV.URL.StorageOneofCase.AbsoluteString, AbsoluteString.Length: > 0 })
                absolute = SwiftCompat.UrlPath(url.AbsoluteString);
            string? relative = null;
            if (url is { RelativeFilePathCase: RV.URL.RelativeFilePathOneofCase.Local } && url.Local.Path.Length > 0)
                relative = url.Local.Path;
            else if (url is { StorageCase: RV.URL.StorageOneofCase.RelativePath, RelativePath.Length: > 0 })
                relative = url.RelativePath;
            var want = new ProMediaWant(placeholder, absolute, relative) { Classification = classification };

            if (media is { TypePropertiesCase: RV.Media.TypePropertiesOneofCase.Video, Video.Transport: { } transport })
            {
                if (transport.InPoint > 0) want.InPoint = transport.InPoint;
                var outPoint = transport.OutPoint > 0 ? transport.OutPoint : transport.EndPoint;
                if (outPoint > 0) want.OutPoint = outPoint;
                if (transport.PlayRate > 0 && transport.PlayRate != 1) want.PlayRate = transport.PlayRate;
                var fade = Math.Max(transport.ShouldFadeIn ? transport.FadeInDuration : 0, transport.ShouldFadeOut ? transport.FadeOutDuration : 0);
                if (fade > 0) want.FadeSeconds = fade;
            }
            if (MediaLoops(media)) want.Loops = true;
            Wants.Add(want);
            if (uuid.Length > 0) wantByUuid[uuid] = placeholder;
            return placeholder;
        }
    }

    private static Slide? MapCue(
        RV.Cue cue, Dictionary<string, string> groupOfCue,
        IReadOnlyDictionary<string, string> timerIdsByProUuid, IReadOnlyDictionary<string, string> timerIdsByName,
        Transition? docTransition, ref (int Width, int Height)? canvas, MapState state)
    {
        RV.Action.Types.SlideType? slideAction = null;
        CueMedia? cueMedia = null;
        Transition? cueActionTransition = null;
        var slideActions = new List<SlideAction>();
        var flattened = new List<string>();

        foreach (var action in cue.Actions)
        {
            switch (action.ActionTypeDataCase)
            {
                case RV.Action.ActionTypeDataOneofCase.Slide:
                    slideAction = action.Slide;
                    break;
                case RV.Action.ActionTypeDataOneofCase.Media:
                {
                    var media = action.Media;
                    var element = media.Element ?? new RV.Media();
                    if (element.TypePropertiesCase == RV.Media.TypePropertiesOneofCase.Audio)
                    {
                        var fire = new SlideAction { Id = SwiftCompat.NewId(), Kind = SlideActionKind.FireAudio, AudioItemId = state.Want(element) };
                        if (action.DelayTime > 0) fire.DelaySeconds = action.DelayTime;
                        if (element.Audio.Transport is { } transport
                            && transport.PlaybackBehavior is RV.Media.Types.TransportProperties.Types.PlaybackBehavior.Loop
                                or RV.Media.Types.TransportProperties.Types.PlaybackBehavior.LoopForCount
                                or RV.Media.Types.TransportProperties.Types.PlaybackBehavior.LoopForTime)
                            fire.AudioRepeat = true;
                        slideActions.Add(fire);
                        continue;
                    }
                    var loops = MediaLoops(element);
                    var isVideo = element.TypePropertiesCase == RV.Media.TypePropertiesOneofCase.Video;
                    var background = media.LayerType == RV.Action.Types.LayerType.Background;
                    var placeholder = state.Want(element, background ? MediaClassification.Background : MediaClassification.Foreground);
                    if (!isVideo)
                    {
                        var fire = new SlideAction { Id = SwiftCompat.NewId(), Kind = SlideActionKind.FireMedia, MediaId = placeholder };
                        if (action.DelayTime > 0) fire.DelaySeconds = action.DelayTime;
                        slideActions.Add(fire);
                        continue;
                    }
                    cueMedia = new CueMedia
                    {
                        MediaId = placeholder, Layer = background ? CueMediaLayer.LoopingVideos : CueMediaLayer.Videos, Loops = loops,
                    };
                    break;
                }
                case RV.Action.ActionTypeDataOneofCase.Transition when action.Transition.Transition is not null:
                    cueActionTransition = Transition(action.Transition.Transition, state);
                    break;
                case RV.Action.ActionTypeDataOneofCase.None:
                    continue;
                default:
                    if (ProWorkspaceMapper.MapActionSteps(action, $"cue \"{cue.Name}\"", timerIdsByProUuid, timerIdsByName, state) is { } steps)
                    {
                        slideActions.AddRange(steps);
                    }
                    else
                    {
                        var label = (action.Label?.Text ?? "").Length == 0 ? action.Name : action.Label!.Text;
                        flattened.Add(label.Length == 0 ? DescribeActionKind(action) : label);
                    }
                    break;
            }
        }

        bool? videoEndAnchored = cueMedia?.Layer == CueMediaLayer.Videos ? true : null;
        AutoAdvance? autoAdvance = null;
        switch (cue.CompletionTargetType, cue.CompletionActionType)
        {
            case (RV.Cue.Types.CompletionTargetType.None, _):
                break;
            case (RV.Cue.Types.CompletionTargetType.Next, RV.Cue.Types.CompletionActionType.AfterTime):
                autoAdvance = new AutoAdvance { DelaySeconds = Math.Max(cue.CompletionTime, 0), AfterPlayback = videoEndAnchored };
                break;
            case (RV.Cue.Types.CompletionTargetType.First, RV.Cue.Types.CompletionActionType.AfterTime):
                autoAdvance = new AutoAdvance { DelaySeconds = Math.Max(cue.CompletionTime, 0), LoopToStart = true, AfterPlayback = videoEndAnchored };
                break;
            case (RV.Cue.Types.CompletionTargetType.Next, RV.Cue.Types.CompletionActionType.AfterAction):
                autoAdvance = new AutoAdvance { DelaySeconds = 0, AfterPlayback = true };
                break;
            case (RV.Cue.Types.CompletionTargetType.First, RV.Cue.Types.CompletionActionType.AfterAction):
                autoAdvance = new AutoAdvance { DelaySeconds = 0, LoopToStart = true, AfterPlayback = true };
                break;
            default:
                flattened.Add("go to next timer");
                break;
        }

        var cueId = cue.Uuid.Lower();
        if (slideAction is null)
        {
            if (cueMedia is not null || slideActions.Count > 0)
            {
                return new Slide
                {
                    Id = cueId, Name = cue.Name, Objects = [], Background = cueMedia, SectionId = groupOfCue.GetValueOrDefault(cueId),
                    Actions = slideActions.Count == 0 ? null : slideActions, AutoAdvance = autoAdvance,
                    Transition = cueActionTransition ?? docTransition,
                };
            }
            state.Warn($"cue \"{cue.Name}\" had no slide content and was skipped");
            return null;
        }

        var presentationSlide = slideAction.Presentation ?? new RV.PresentationSlide();
        var slideTransition = cueActionTransition
            ?? (presentationSlide.Transition is { } own ? Transition(own, state) : docTransition);

        var baseSlide = presentationSlide.BaseSlide ?? new RV.Slide();
        if (baseSlide.Size is { Width: > 0 } baseSize)
        {
            var size = ((int)baseSize.Width, (int)baseSize.Height);
            if (canvas is { } current)
            {
                if (current != size)
                {
                    var key = $"{size.Item1}×{size.Item2}";
                    state.OffCanvasSlides[key] = state.OffCanvasSlides.GetValueOrDefault(key) + 1;
                }
            }
            else
            {
                canvas = size;
            }
        }
        double canvasWidth = canvas?.Width ?? 1920, canvasHeight = canvas?.Height ?? 1080;

        var objects = new List<SlideObject>();
        if (baseSlide.DrawsBackgroundColor && baseSlide.BackgroundColor is { } backgroundColor)
        {
            objects.Add(new SlideObject
            {
                Id = SwiftCompat.NewId(), ObjectKind = SlideObjectKind.Shape, Name = "Background Color", Text = "",
                X = 0, Y = 0, Width = canvasWidth, Height = canvasHeight,
                Fill = new ObjectFill { FillKind = FillKind.Solid, ColorHex = HexColor(backgroundColor) },
            });
        }

        var stepWarnings = new List<string>();
        foreach (var wrapped in baseSlide.Elements.Reverse())
        {
            var element = wrapped.Element_ ?? new RV.Graphics.Types.Element();
            var mapped = MapElement(element, state);
            if (ProWorkspaceMapper.TextLink(wrapped, timerIdsByProUuid, timerIdsByName, state) is { } link)
            {
                if (link.Source == TextSourceKind.VideoCountdown) link.ShowsVideoName = false;
                mapped ??= ProWorkspaceMapper.EmptyTextPlaceholder(element, state);
                if (mapped is not null)
                {
                    mapped.ObjectKind = SlideObjectKind.Text;
                    mapped.TextLink = link;
                }
            }
            if (mapped is not { } obj) continue;
            if (element.Hidden) obj.Hidden = true;

            var steps = ProAnimationMapper.Steps(wrapped, obj.Id, obj.Text, stepWarnings);
            if (steps.Count > 0) obj.AnimationSteps = steps;
            if (ProWorkspaceMapper.Visibility(wrapped, timerIdsByProUuid, timerIdsByName, state) is { } visibility)
            {
                obj.VisibilityMatch = visibility.Match;
                obj.VisibilityConditions = visibility.Conditions;
            }
            if (obj.Hidden != true) WarnOnNearFullBleed(obj, canvasWidth, canvasHeight, state);
            objects.Add(obj);
        }

        var notes = new List<string>();
        if (presentationSlide.Notes is { } slideNotes && AttributedString(slideNotes.RtfData)?.Text is { Length: > 0 } noteText)
            notes.Add(noteText);
        if (flattened.Count > 0) notes.Add($"Flattened ProPresenter actions: {string.Join(", ", flattened)}");
        foreach (var warning in stepWarnings)
        {
            state.Warn(warning);
            notes.Add($"ProPresenter {warning}");
        }

        AnimationImportSupport.RigidifyMoves(objects, canvasWidth, canvasHeight);
        return new Slide
        {
            Id = cueId,
            Name = cue.Name,
            Objects = objects,
            Background = cueMedia,
            SectionId = groupOfCue.GetValueOrDefault(cueId),
            Actions = slideActions.Count == 0 ? null : slideActions,
            Notes = notes.Count == 0 ? null : string.Join("\n", notes),
            AutoAdvance = autoAdvance,
            Transition = slideTransition,
            AnimationOrder = ProAnimationMapper.AnimationOrder(baseSlide.ElementBuildOrder, objects),
        };
    }

    internal static Transition? Transition(RV.Transition rv, MapState state)
    {
        if (rv.Effect is null && !(rv.Duration > 0)) return null;
        var name = (rv.Effect?.Name ?? "").TrimmingWhitespaces();
        var lowered = name.ToLowerInvariant();
        double? duration = rv.Duration > 0 ? rv.Duration : null;
        if (lowered.Length == 0 || lowered == "none" || lowered == "cut") return new Transition { TransitionKind = TransitionKind.Cut };
        if (lowered.Contains("blur")) return new Transition { TransitionKind = TransitionKind.BlurDissolve, DurationSeconds = duration };
        if (lowered.Contains("dissolve")) return new Transition { TransitionKind = TransitionKind.Dissolve, DurationSeconds = duration };
        if (lowered.Contains("fade")) return new Transition { TransitionKind = TransitionKind.FadeBlack, DurationSeconds = duration };
        state.Warn($"transition \"{name}\" has no MxU equivalent — imported as a dissolve");
        return new Transition { TransitionKind = TransitionKind.Dissolve, DurationSeconds = duration };
    }

    internal static string DescribeActionKind(RV.Action? action) => action?.ActionTypeDataCase switch
    {
        RV.Action.ActionTypeDataOneofCase.Transition => "transition",
        RV.Action.ActionTypeDataOneofCase.Effects => "effects",
        RV.Action.ActionTypeDataOneofCase.Timer => "timer",
        RV.Action.ActionTypeDataOneofCase.Clear or RV.Action.ActionTypeDataOneofCase.ClearGroup => "clear",
        RV.Action.ActionTypeDataOneofCase.Stage => "stage layout",
        RV.Action.ActionTypeDataOneofCase.Prop => "prop",
        RV.Action.ActionTypeDataOneofCase.Mask => "mask",
        RV.Action.ActionTypeDataOneofCase.Message => "message",
        RV.Action.ActionTypeDataOneofCase.Communication => "device control",
        RV.Action.ActionTypeDataOneofCase.AudienceLook => "look",
        RV.Action.ActionTypeDataOneofCase.Macro => "macro",
        RV.Action.ActionTypeDataOneofCase.AudioInput => "audio input",
        RV.Action.ActionTypeDataOneofCase.TransportControl => "transport",
        _ => "action",
    };

    private static List<PresentationSection> EnforceContiguity(List<Slide> slides, List<PresentationSection> mapped, MapState state)
    {
        var result = mapped.ToList();
        var completedRuns = new HashSet<string>();
        string? runOriginal = null;
        string? runAssigned = null;
        foreach (var slide in slides)
        {
            if (slide.SectionId is not { } original)
            {
                if (runOriginal is not null) completedRuns.Add(runOriginal);
                runOriginal = null;
                runAssigned = null;
                continue;
            }
            if (original != runOriginal)
            {
                if (runOriginal is not null) completedRuns.Add(runOriginal);
                runOriginal = original;
                if (completedRuns.Contains(original) && result.FirstOrDefault(s => s.Id == original) is { } source)
                {
                    var clone = new PresentationSection { Id = SwiftCompat.NewId(), Name = source.Name, ColorHex = source.ColorHex };
                    result.Add(clone);
                    runAssigned = clone.Id;
                    state.Warn($"group \"{source.Name}\" was split — its slides were not contiguous");
                }
                else
                {
                    runAssigned = original;
                }
            }
            slide.SectionId = runAssigned;
        }
        return result;
    }

    internal static SlideObject? MapElement(RV.Graphics.Types.Element element, MapState state)
    {
        (double? X, double? Y, double? W, double? H) frame = element.Bounds is { } bounds
            ? (bounds.Origin?.X ?? 0, bounds.Origin?.Y ?? 0, bounds.Size?.Width ?? 0, bounds.Size?.Height ?? 0)
            : (null, null, null, null);
        var rotation = -element.Rotation % 360;
        var opacity = NormalizedOpacity(element.Opacity);
        var attributed = element.Text is { } textBox ? AttributedString(textBox.RtfData) : null;
        var text = attributed?.Text ?? "";
        if (SwiftCompat.AllInvisible(text)) text = "";

        if (text.Length > 0 && (!HasEnabledInkFill(element) || LineFillEnabled(element)))
            return TextObject(element, text, attributed, frame, rotation, opacity, state);

        var shape = ShapeOf(element.Path ?? new RV.Graphics.Types.Path());
        var elementIsMedia = element.Fill?.FillTypeCase == RV.Graphics.Types.Fill.FillTypeOneofCase.Media;
        var obj = new SlideObject
        {
            Id = ElementId(element),
            ObjectKind = SlideObjectKind.Shape,
            Name = ElementName(element, text.Length > 0 ? "Text" : elementIsMedia ? "Media" : "Shape"),
            Text = text,
            X = frame.X, Y = frame.Y, Width = frame.W, Height = frame.H,
            RotationDegrees = rotation == 0 ? null : rotation,
            Opacity = opacity == 1 ? null : opacity,
            TextStyle = text.Length == 0 ? null : TextStyleFor(element, attributed, state),
            ShapeKind = shape.Kind == ShapeKind.Rectangle && shape.CornerRadius is null ? null : shape.Kind,
            CornerRadius = shape.CornerRadius,
            PathData = shape.PathData,
            Fill = LineFillEnabled(element) ? null : ObjectFillFor(element, state),
        };
        if (element.Stroke is { Enable: true } stroke)
            obj.Stroke = new ObjectStroke { ColorHex = HexColor(stroke.Color), Width = stroke.Width };
        if (element.Shadow is { Enable: true } shadow)
            obj.Shadow = ObjectShadow(shadow);

        if (text.Length > 0 && attributed is not null)
        {
            var runs = StyleRuns(attributed, element.Text?.RtfData.ToByteArray());
            if (runs.Count > 0) obj.StyleRuns = runs;
        }

        if (elementIsMedia)
        {
            var media = element.Fill!.Media;
            var (effects, alphaFactor) = MapMediaEffects(media, $"\"{obj.Name}\"", state);
            if (effects is not null) obj.Effects = effects;
            if (alphaFactor != 1) obj.Opacity = (obj.Opacity ?? 1) * alphaFactor;
            var (horizontal, vertical) = MediaFlips(media);
            if (horizontal || vertical)
            {
                if (shape.Kind == ShapeKind.Rectangle && shape.CornerRadius is null && text.Length == 0)
                {
                    obj.FlipHorizontal = horizontal ? true : null;
                    obj.FlipVertical = vertical ? true : null;
                }
                else
                {
                    state.Warn($"\"{obj.Name}\": its media fill was flipped in ProPresenter — fills have no flip yet, the media imported unflipped");
                }
            }
        }

        var fillIsInk = obj.Fill is not null && obj.Fill.FillKind != FillKind.None;
        if (!fillIsInk && obj.Stroke is null && text.Length == 0) return null;
        return obj;
    }

    private static void WarnOnNearFullBleed(SlideObject obj, double canvasWidth, double canvasHeight, MapState state)
    {
        if (obj is not { X: { } x, Y: { } y, Width: { } width, Height: { } height }) return;
        double minX = x, minY = y, maxX = x + width, maxY = y + height;
        if (obj.RotationDegrees is { } degrees && degrees % 360 != 0)
        {
            var centerX = x + width / 2;
            var centerY = y + height / 2;
            var radians = degrees * Math.PI / 180;
            var halfW = Math.Abs(width / 2 * Math.Cos(radians)) + Math.Abs(height / 2 * Math.Sin(radians));
            var halfH = Math.Abs(width / 2 * Math.Sin(radians)) + Math.Abs(height / 2 * Math.Cos(radians));
            minX = centerX - halfW;
            minY = centerY - halfH;
            maxX = centerX + halfW;
            maxY = centerY + halfH;
        }
        var worst = new[] { Math.Max(0, minX), Math.Max(0, minY), Math.Max(0, canvasWidth - maxX), Math.Max(0, canvasHeight - maxY) }.Max();
        if (worst > 0.5 || worst < 0.01) return;
        state.Warn($"\"{obj.Name}\" misses the canvas edge by {SwiftCompat.Format(worst, 2)} — a hairline of whatever is behind it may show");
    }

    private static SlideObject TextObject(
        RV.Graphics.Types.Element element, string text, AttributedText? attributed,
        (double? X, double? Y, double? W, double? H) frame, double rotation, double opacity, MapState state)
    {
        var style = TextStyleFor(element, attributed, state);
        if (element.Text?.ChordPro is { Enabled: true } chordPro)
        {
            var chordStyle = style ?? new TextStyle();
            chordStyle.ShowChords = true;
            chordStyle.ChordNotation = ChordNotationOf(chordPro.Notation);
            if (chordPro.Color is { } chordColor) chordStyle.ChordColorHex = HexColor(chordColor);
            style = chordStyle;
        }
        var obj = new SlideObject
        {
            Id = ElementId(element), ObjectKind = SlideObjectKind.Text, Name = ElementName(element, "Text"), Text = text,
            X = frame.X, Y = frame.Y, Width = frame.W, Height = frame.H,
            RotationDegrees = rotation == 0 ? null : rotation,
            Opacity = opacity == 1 ? null : opacity,
            TextStyle = style,
        };
        var chords = ChordPlacements(element, text);
        if (chords.Count > 0) obj.Chords = chords;
        if (attributed is not null)
        {
            var runs = StyleRuns(attributed, element.Text?.RtfData.ToByteArray());
            if (runs.Count > 0) obj.StyleRuns = runs;
        }
        return obj;
    }

    /// <summary>NSString line ranges: each line with its terminator (\n, \r, \r\n, U+0085, U+2028, U+2029).</summary>
    private static List<(int Start, int Length)> LineRanges(string text)
    {
        var lines = new List<(int, int)>();
        var start = 0;
        var index = 0;
        while (index < text.Length)
        {
            var c = text[index];
            if (c is '\n' or '\u0085' or '\u2028' or '\u2029' or '\r')
            {
                var end = index + (c == '\r' && index + 1 < text.Length && text[index + 1] == '\n' ? 2 : 1);
                lines.Add((start, end - start));
                start = index = end;
                continue;
            }
            index++;
        }
        if (start < text.Length) lines.Add((start, text.Length - start));
        return lines;
    }

    internal static List<TextStyleRun> StyleRuns(AttributedText attributed, byte[]? rtfData = null)
    {
        if (attributed.Length == 0) return [];
        var text = attributed.Text;
        var lineRanges = LineRanges(text);

        (int Start, int Length) Content((int Start, int Length) line) =>
            line.Length > 0 && text[line.Start + line.Length - 1] == '\n' ? (line.Start, line.Length - 1) : line;

        (int Column, int Length)? Span(int overlapStart, int overlapLength, (int Start, int Length) content, string line)
        {
            var column = SwiftCompat.Characters(line, overlapStart - content.Start);
            var end = SwiftCompat.Characters(line, overlapStart + overlapLength - content.Start);
            return end > column ? (column, end - column) : null;
        }

        var runs = new List<TextStyleRun>();
        void Collect(Func<RtfAttributes, int> select, Action<TextStyleRun> assign)
        {
            foreach (var (start, length, value) in attributed.Ranges(select))
            {
                if (value == 0) continue;
                for (var lineIndex = 0; lineIndex < lineRanges.Count; lineIndex++)
                {
                    var content = Content(lineRanges[lineIndex]);
                    var overlapStart = Math.Max(content.Start, start);
                    var overlapLength = Math.Min(content.Start + content.Length, start + length) - overlapStart;
                    if (overlapLength <= 0) continue;
                    var line = text.Substring(content.Start, content.Length);
                    if (Span(overlapStart, overlapLength, content, line) is not { } span) continue;
                    var run = new TextStyleRun { Line = lineIndex, Column = span.Column, Length = span.Length };
                    assign(run);
                    runs.Add(run);
                }
            }
        }
        Collect(a => a.Underline, r => r.Underline = true);
        Collect(a => a.Strikethrough, r => r.Strikethrough = true);

        var rtfText = rtfData is null ? null : Encoding.UTF8.GetString(rtfData);
        for (var lineIndex = 0; lineIndex < lineRanges.Count; lineIndex++)
        {
            var content = Content(lineRanges[lineIndex]);
            if (content.Length == 0) continue;
            var line = text.Substring(content.Start, content.Length);
            var leading = attributed.AttributesAt(content.Start);
            var leadingColor = leading.Foreground?.Hex;
            foreach (var piece in attributed.RunsIn(content.Start, content.Length))
            {
                if (SwiftCompat.AllInvisible(text.Substring(piece.Start, piece.Length))) continue;
                if (Span(piece.Start, piece.Length, content, line) is not { } span) continue;
                var attrs = piece.Attributes;
                var run = new TextStyleRun { Line = lineIndex, Column = span.Column, Length = span.Length };
                if (attrs.FontSize != leading.FontSize) run.FontSize = attrs.FontSize;
                if (attrs.FontName != leading.FontName && rtfText?.Contains(attrs.FontName + ";", StringComparison.Ordinal) == true)
                    run.FontName = attrs.FontName;
                if (attrs.Foreground?.Hex is { } color && color != leadingColor) run.ColorHex = color;
                if (attrs.Kern != leading.Kern) run.Tracking = attrs.Kern;
                if (attrs.Background is { } highlight) run.HighlightColorHex = highlight.Hex;
                if (!MxuJson.StructurallyEqual(run, new TextStyleRun { Line = lineIndex, Column = span.Column, Length = span.Length }))
                    runs.Add(run);
            }
        }
        return runs.OrderBy(r => r.Line).ThenBy(r => r.Column).ToList();
    }

    internal static List<ChordPlacement> ChordPlacements(RV.Graphics.Types.Element element, string plainText)
    {
        if (element.Text is not { } textBox) return [];
        var chords = (textBox.Attributes?.CustomAttributes ?? [])
            .Where(a => a.AttributeCase == RV.Graphics.Types.Text.Types.Attributes.Types.CustomAttribute.AttributeOneofCase.Chord
                && a.Chord.TrimmingWhitespaces().Length > 0)
            .Select(a => (Offset: a.Range?.Start ?? 0, Symbol: a.Chord.TrimmingWhitespaces()))
            .ToList();
        if (chords.Count == 0) return [];

        var lines = plainText.Split('\n');
        var lineStarts = new List<int>();
        var running = 0;
        foreach (var line in lines)
        {
            lineStarts.Add(running);
            running += line.Length + 1;
        }
        return chords.Select(chord =>
            {
                var lineIndex = lineStarts.FindLastIndex(start => start <= chord.Offset);
                if (lineIndex < 0) lineIndex = 0;
                var line = lines[lineIndex];
                var within = Math.Clamp(chord.Offset - lineStarts[lineIndex], 0, line.Length);
                return new ChordPlacement { Line = lineIndex, Column = SwiftCompat.Characters(line, within), Symbol = chord.Symbol };
            })
            .OrderBy(p => p.Line).ThenBy(p => p.Column)
            .ToList();
    }

    internal static ChordNotation? ChordNotationOf(RV.Graphics.Types.Text.Types.ChordPro.Types.Notation notation) => notation switch
    {
        RV.Graphics.Types.Text.Types.ChordPro.Types.Notation.Numbers => ChordNotation.Numbers,
        RV.Graphics.Types.Text.Types.ChordPro.Types.Notation.Numerals => ChordNotation.Numerals,
        RV.Graphics.Types.Text.Types.ChordPro.Types.Notation.DoReMi => ChordNotation.DoReMi,
        _ => null,
    };

    private static readonly Dictionary<RV.MusicKeyScale.Types.MusicKey, string> KeyNames = new()
    {
        [RV.MusicKeyScale.Types.MusicKey.AFlat] = "Ab", [RV.MusicKeyScale.Types.MusicKey.A] = "A", [RV.MusicKeyScale.Types.MusicKey.ASharp] = "A#",
        [RV.MusicKeyScale.Types.MusicKey.BFlat] = "Bb", [RV.MusicKeyScale.Types.MusicKey.B] = "B", [RV.MusicKeyScale.Types.MusicKey.BSharp] = "B#",
        [RV.MusicKeyScale.Types.MusicKey.CFlat] = "Cb", [RV.MusicKeyScale.Types.MusicKey.C] = "C", [RV.MusicKeyScale.Types.MusicKey.CSharp] = "C#",
        [RV.MusicKeyScale.Types.MusicKey.DFlat] = "Db", [RV.MusicKeyScale.Types.MusicKey.D] = "D", [RV.MusicKeyScale.Types.MusicKey.DSharp] = "D#",
        [RV.MusicKeyScale.Types.MusicKey.EFlat] = "Eb", [RV.MusicKeyScale.Types.MusicKey.E] = "E", [RV.MusicKeyScale.Types.MusicKey.ESharp] = "E#",
        [RV.MusicKeyScale.Types.MusicKey.FFlat] = "Fb", [RV.MusicKeyScale.Types.MusicKey.F] = "F", [RV.MusicKeyScale.Types.MusicKey.FSharp] = "F#",
        [RV.MusicKeyScale.Types.MusicKey.GFlat] = "Gb", [RV.MusicKeyScale.Types.MusicKey.G] = "G", [RV.MusicKeyScale.Types.MusicKey.GSharp] = "G#",
    };

    internal static string? KeyName(RV.MusicKeyScale? scale)
    {
        if (scale is null || !KeyNames.TryGetValue(scale.MusicKey, out var name)) return null;
        return scale.MusicScale == RV.MusicKeyScale.Types.MusicScale.Minor ? name + "m" : name;
    }

    internal static string? NormalizedKeyName(string text)
    {
        var trimmed = text.TrimmingWhitespaces();
        return trimmed.Length > 0 && ChordMath.ParseKey(trimmed) is not null ? trimmed : null;
    }

    private static bool HasEnabledInkFill(RV.Graphics.Types.Element element)
    {
        if (element.Fill is not { Enable: true } fill) return false;
        return fill.FillTypeCase switch
        {
            RV.Graphics.Types.Fill.FillTypeOneofCase.Color => fill.Color.Alpha > 0,
            RV.Graphics.Types.Fill.FillTypeOneofCase.Gradient => true,
            _ => false,
        };
    }

    private static bool LineFillEnabled(RV.Graphics.Types.Element element) =>
        element.MaskCase == RV.Graphics.Types.Element.MaskOneofCase.TextLineMask && element.TextLineMask.Enabled;

    internal static TextStyle? TextStyleFor(RV.Graphics.Types.Element element, AttributedText? attributed, MapState state)
    {
        var attrs = element.Text?.Attributes ?? new RV.Graphics.Types.Text.Types.Attributes();
        var style = new TextStyle();

        if (attrs.Font is { Name.Length: > 0 } font) style.FontName = font.Name;
        if (attrs.Font is { Size: > 0 }) style.FontSize = attrs.Font.Size;
        if (attrs.FillCase == RV.Graphics.Types.Text.Types.Attributes.FillOneofCase.TextSolidFill) style.ColorHex = HexColor(attrs.TextSolidFill);
        if (attrs.Kerning != 0) style.Tracking = attrs.Kerning;
        if (attrs.ParagraphStyle is { } paragraph)
        {
            switch (paragraph.Alignment)
            {
                case RV.Graphics.Types.Text.Types.Attributes.Types.Alignment.Left or RV.Graphics.Types.Text.Types.Attributes.Types.Alignment.Natural:
                    style.HorizontalAlignment = TextHorizontalAlignment.Left;
                    break;
                case RV.Graphics.Types.Text.Types.Attributes.Types.Alignment.Right:
                    style.HorizontalAlignment = TextHorizontalAlignment.Right;
                    break;
                case RV.Graphics.Types.Text.Types.Attributes.Types.Alignment.Center:
                    style.HorizontalAlignment = TextHorizontalAlignment.Center;
                    break;
            }
            var multiple = paragraph.LineHeightMultiple;
            if (multiple > 0 && multiple != 1) style.LineHeightMultiple = multiple;
            var firstLineDelta = paragraph.FirstLineHeadIndent - paragraph.HeadIndent;
            if (firstLineDelta != 0) style.FirstLineIndent = firstLineDelta;
            if (paragraph.HeadIndent != 0) style.LeftIndent = paragraph.HeadIndent;
            if (paragraph.TailIndent < 0) style.RightIndent = -paragraph.TailIndent;
            else if (paragraph.TailIndent > 0)
                state.Warn($"absolute right-edge indent ({SwiftCompat.Describe(paragraph.TailIndent)}) is not supported — the line width follows the box");
            if (paragraph.ParagraphSpacing > 0) style.ParagraphSpacing = paragraph.ParagraphSpacing;
            if (paragraph.ParagraphSpacingBefore > 0)
                state.Warn($"space-before-paragraph ({SwiftCompat.Describe(paragraph.ParagraphSpacingBefore)}) is not supported — only space after carries");
        }

        if (element.Text?.Margins is { } margins)
        {
            if (margins.Top != 0) style.InsetTop = margins.Top;
            if (margins.Left != 0) style.InsetLeft = margins.Left;
            if (margins.Bottom != 0) style.InsetBottom = margins.Bottom;
            if (margins.Right != 0) style.InsetRight = margins.Right;
        }

        style.VerticalAlignment = element.Text?.VerticalAlignment switch
        {
            RV.Graphics.Types.Text.Types.VerticalAlignment.Middle => TextVerticalAlignment.Middle,
            null or RV.Graphics.Types.Text.Types.VerticalAlignment.Top => TextVerticalAlignment.Top,
            _ => TextVerticalAlignment.Bottom,
        };
        if (attrs.Capitalization == RV.Graphics.Types.Text.Types.Attributes.Types.Capitalization.AllCaps) style.TextTransform = TextTransform.Uppercase;

        if (attrs.UnderlineStyle is { } underline && underline.Style != RV.Graphics.Types.Text.Types.Attributes.Types.Underline.Types.Style.None)
            style.Underline = true;
        if (attrs.StrikethroughStyle is { } strike && strike.Style != RV.Graphics.Types.Text.Types.Attributes.Types.Underline.Types.Style.None)
            style.Strikethrough = true;
        if (element.Text?.ScaleBehavior is RV.Graphics.Types.Text.Types.ScaleBehavior.ScaleFontDown or RV.Graphics.Types.Text.Types.ScaleBehavior.ScaleFontUpDown)
            style.AutoShrink = true;
        if (attrs.StrokeWidth > 0) style.Outline = new ObjectStroke { ColorHex = HexColor(attrs.StrokeColor), Width = attrs.StrokeWidth };
        if (element.Shadow is { Enable: true } shadow) style.Shadow = ObjectShadow(shadow);
        if (attrs.FillCase == RV.Graphics.Types.Text.Types.Attributes.FillOneofCase.MediaFill)
            style.Fill = new ObjectFill { FillKind = FillKind.Media, MediaId = state.Want(attrs.MediaFill.Media) };

        if (LineFillEnabled(element) && ObjectFillFor(element, state) is { FillKind: FillKind.Solid or FillKind.LinearGradient } lineFill)
        {
            var mask = element.TextLineMask;
            style.LineFill = new TextLineFill
            {
                Fill = lineFill,
                WidthMode = mask.MaskStyle switch
                {
                    RV.Graphics.Types.Text.Types.LineFillMask.Types.LineMaskStyle.LineWidth => TextLineFillWidthMode.LineWidth,
                    RV.Graphics.Types.Text.Types.LineFillMask.Types.LineMaskStyle.MaxLineWidth => TextLineFillWidthMode.MaxLineWidth,
                    _ => null,
                },
                VerticalPadding = mask.HeightOffset == 0 ? null : mask.HeightOffset,
                HorizontalPadding = mask.WidthOffset == 0 ? null : mask.WidthOffset,
                VerticalOffset = mask.VerticalOffset == 0 ? null : mask.VerticalOffset,
                HorizontalOffset = mask.HorizontalOffset == 0 ? null : mask.HorizontalOffset,
            };
        }
        if (attributed is not null)
        {
            style.LineStyles = LineOverrides(
                attributed, style.FontSize, style.ColorHex, style.FontName,
                style.FirstLineIndent, style.LeftIndent, style.RightIndent, element.Text?.RtfData.ToByteArray());
        }
        return MxuJson.StructurallyEqual(style, new TextStyle()) ? null : style;
    }

    private static List<LineStyleOverride>? LineOverrides(
        AttributedText attributed, double? baseSize, string? baseColorHex, string? baseFontName,
        double? baseFirstLineIndent, double? baseLeftIndent, double? baseRightIndent, byte[]? rtfData)
    {
        var overrides = new List<LineStyleOverride>();
        var lines = attributed.Text.Split('\n');
        var rtfText = rtfData is null ? null : Encoding.UTF8.GetString(rtfData);

        var lineFonts = new Dictionary<int, string>();
        var scan = 0;
        for (var index = 0; index < lines.Length; index++)
        {
            if (lines[index].Length > 0 && scan < attributed.Length) lineFonts[index] = attributed.AttributesAt(scan).FontName;
            scan += lines[index].Length + 1;
        }
        string? referenceFont = null;
        var distinct = lineFonts.Values.ToHashSet();
        if (distinct.Count > 1)
        {
            referenceFont = baseFontName is not null && distinct.Contains(baseFontName)
                ? baseFontName
                : lineFonts.Values.GroupBy(n => n).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).First().Key;
        }

        var location = 0;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var lineStart = location;
            location += line.Length + 1;
            if (line.Length == 0 || lineStart >= attributed.Length) continue;
            var attrs = attributed.AttributesAt(lineStart);
            var lineOverride = new LineStyleOverride { LineIndex = index };
            if (baseSize is { } size && attrs.FontSize != size) lineOverride.FontSize = attrs.FontSize;
            if (referenceFont is not null && lineFonts.TryGetValue(index, out var name) && name != referenceFont
                && rtfText?.Contains(name + ";", StringComparison.Ordinal) == true)
                lineOverride.FontName = name;
            if (attrs.Foreground?.Hex is { } hex && baseColorHex is not null && hex != baseColorHex) lineOverride.ColorHex = hex;
            var paragraph = attrs.Paragraph;
            var first = paragraph.FirstLineHeadIndent - paragraph.HeadIndent;
            if (first != (baseFirstLineIndent ?? 0)) lineOverride.FirstLineIndent = first;
            if (paragraph.HeadIndent != (baseLeftIndent ?? 0)) lineOverride.LeftIndent = paragraph.HeadIndent;
            if (paragraph.TailIndent <= 0)
            {
                var right = -paragraph.TailIndent;
                if (right != (baseRightIndent ?? 0)) lineOverride.RightIndent = right;
            }
            if (!MxuJson.StructurallyEqual(lineOverride, new LineStyleOverride { LineIndex = index })) overrides.Add(lineOverride);
        }
        return overrides.Count == 0 ? null : overrides;
    }

    private sealed record MappedShape(ShapeKind Kind, double? CornerRadius = null, string? PathData = null);

    private static MappedShape ShapeOf(RV.Graphics.Types.Path path)
    {
        switch (path.Shape?.Type ?? RV.Graphics.Types.Path.Types.Shape.Types.Type.Unknown)
        {
            case RV.Graphics.Types.Path.Types.Shape.Types.Type.Rectangle or RV.Graphics.Types.Path.Types.Shape.Types.Type.Unknown:
                if (path.Shape is { AdditionalDataCase: RV.Graphics.Types.Path.Types.Shape.AdditionalDataOneofCase.RoundedRectangle } rounded
                    && rounded.RoundedRectangle.Roundness > 0)
                    return new MappedShape(ShapeKind.RoundedRectangle, rounded.RoundedRectangle.Roundness);
                if (path.Points.Count == 0 || IsUnitRectangle(path)) return new MappedShape(ShapeKind.Rectangle);
                return new MappedShape(ShapeKind.Path, PathData: SvgPath(path));
            case RV.Graphics.Types.Path.Types.Shape.Types.Type.Ellipse:
                return new MappedShape(ShapeKind.Ellipse);
            default:
                return new MappedShape(ShapeKind.Path, PathData: SvgPath(path));
        }
    }

    private static bool IsUnitRectangle(RV.Graphics.Types.Path path)
    {
        if (!path.Closed || path.Points.Count != 4) return false;
        var corners = new HashSet<(double, double)> { (0, 0), (1, 0), (1, 1), (0, 1) };
        foreach (var point in path.Points)
        {
            var at = point.Point ?? new RV.Graphics.Types.Point();
            if (point.Curved
                || !corners.Contains((SwiftCompat.Rounded(at.X), SwiftCompat.Rounded(at.Y)))
                || !(point.Q0 is null || point.Q0.Equals(at))
                || !(point.Q1 is null || point.Q1.Equals(at)))
                return false;
        }
        return true;
    }

    private static string? SvgPath(RV.Graphics.Types.Path path)
    {
        var points = path.Points;
        if (points.Count < 2) return null;
        static string Fmt(double value) => SwiftCompat.Format(Math.Clamp(value, -4, 4), 4);
        static string Coord(RV.Graphics.Types.Point? point) => $"{Fmt(point?.X ?? 0)} {Fmt(point?.Y ?? 0)}";
        static RV.Graphics.Types.Point? Out(RV.Graphics.Types.Path.Types.BezierPoint p) => p.Q1 ?? p.Point;
        static RV.Graphics.Types.Point? In(RV.Graphics.Types.Path.Types.BezierPoint p) => p.Q0 ?? p.Point;

        var commands = new List<string> { $"M {Coord(points[0].Point)}" };
        for (var index = 1; index < points.Count; index++)
            commands.Add($"C {Coord(Out(points[index - 1]))} {Coord(In(points[index]))} {Coord(points[index].Point)}");
        if (path.Closed)
        {
            commands.Add($"C {Coord(Out(points[^1]))} {Coord(In(points[0]))} {Coord(points[0].Point)}");
            commands.Add("Z");
        }
        return string.Join(" ", commands);
    }

    private static ObjectFill? ObjectFillFor(RV.Graphics.Types.Element element, MapState state)
    {
        if (element.Fill is not { } fill) return null;
        switch (fill.FillTypeCase)
        {
            case RV.Graphics.Types.Fill.FillTypeOneofCase.Color when fill.Enable:
                return fill.Color.Alpha > 0
                    ? new ObjectFill { FillKind = FillKind.Solid, ColorHex = HexColor(fill.Color) }
                    : new ObjectFill { FillKind = FillKind.None };
            case RV.Graphics.Types.Fill.FillTypeOneofCase.Gradient when fill.Enable:
                return new ObjectFill
                {
                    FillKind = FillKind.LinearGradient,
                    GradientAngleDegrees = fill.Gradient.Angle,
                    GradientStops = fill.Gradient.Stops.Select(s => new GradientStop { ColorHex = HexColor(s.Color), Position = s.Position }).ToList(),
                };
            case RV.Graphics.Types.Fill.FillTypeOneofCase.Media:
            {
                var media = fill.Media;
                if (media.TypePropertiesCase == RV.Media.TypePropertiesOneofCase.LiveVideo)
                {
                    var live = media.LiveVideo;
                    var device = live.LiveVideo?.VideoDevice ?? new RV.Media.Types.VideoDevice();
                    var scale = live.Drawing is { } liveDrawing ? ScaleMode(liveDrawing.ScaleBehavior) : null;
                    if (state.InputBook.IsEnabled)
                        return new ObjectFill { FillKind = FillKind.Media, MediaScaleMode = scale, LiveInputId = state.InputBook.Id(device) };
                    var mapped = ProInputPlanBook.Device(device);
                    return new ObjectFill
                    {
                        FillKind = FillKind.Media, MediaScaleMode = scale, CaptureSourceKind = mapped.Kind, CaptureSourceId = mapped.SourceId,
                    };
                }
                (double Width, double Height)? elementSize = element.Bounds is { } bounds
                    ? (bounds.Size?.Width ?? 0, bounds.Size?.Height ?? 0) : null;
                var custom = CustomSourceRect(media, elementSize);
                var mediaId = state.Want(media);
                return new ObjectFill
                {
                    FillKind = FillKind.Media,
                    MediaId = mediaId,
                    MediaScaleMode = ScaleMode(ScaleBehaviorOf(media)) ?? (custom is not null ? MediaScaleMode.Stretch : null),
                    MediaSourceRect = MediaSourceRectOf(media) ?? custom,
                    Loops = MediaLoops(media) ? true : null,
                };
            }
            default:
                return new ObjectFill { FillKind = FillKind.None };
        }
    }

    internal static RV.Media.Types.ScaleBehavior ScaleBehaviorOf(RV.Media media) => media.TypePropertiesCase switch
    {
        RV.Media.TypePropertiesOneofCase.Image => media.Image.Drawing?.ScaleBehavior ?? RV.Media.Types.ScaleBehavior.Fit,
        RV.Media.TypePropertiesOneofCase.Video => media.Video.Drawing?.ScaleBehavior ?? RV.Media.Types.ScaleBehavior.Fit,
        _ => RV.Media.Types.ScaleBehavior.Fit,
    };

    internal static MediaScaleMode? ScaleMode(RV.Media.Types.ScaleBehavior behavior) => behavior switch
    {
        RV.Media.Types.ScaleBehavior.Fit => MediaScaleMode.Fit,
        RV.Media.Types.ScaleBehavior.Stretch => MediaScaleMode.Stretch,
        _ => null,
    };

    internal static RV.Media.Types.DrawingProperties? Drawing(RV.Media media) => media.TypePropertiesCase switch
    {
        RV.Media.TypePropertiesOneofCase.Image => media.Image.Drawing,
        RV.Media.TypePropertiesOneofCase.Video => media.Video.Drawing,
        _ => null,
    };

    internal static (bool Horizontal, bool Vertical) MediaFlips(RV.Media media) =>
        Drawing(media) is { } drawing ? (drawing.FlippedHorizontally, drawing.FlippedVertically) : (false, false);

    internal static MediaSourceRect? CustomSourceRect(RV.Media media, (double Width, double Height)? elementSize)
    {
        if (Drawing(media) is not { ScaleBehavior: RV.Media.Types.ScaleBehavior.Custom, CustomImageBounds: { } bounds } || elementSize is not { } size)
            return null;
        double originX = bounds.Origin?.X ?? 0, originY = bounds.Origin?.Y ?? 0;
        double boundsWidth = bounds.Size?.Width ?? 0, boundsHeight = bounds.Size?.Height ?? 0;
        if (boundsWidth <= 0 || boundsHeight <= 0 || size.Width <= 0 || size.Height <= 0) return null;
        var x = Math.Max(0, (0 - originX) / boundsWidth);
        var y = Math.Max(0, (0 - originY) / boundsHeight);
        var maxX = Math.Min(1, (size.Width - originX) / boundsWidth);
        var maxY = Math.Min(1, (size.Height - originY) / boundsHeight);
        var width = maxX - x;
        var height = maxY - y;
        if (width <= 0 || height <= 0 || !double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height))
            return null;
        if (x == 0 && y == 0 && width == 1 && height == 1) return null;
        return new MediaSourceRect { X = x, Y = y, Width = width, Height = height };
    }

    internal static (List<Effect>? Effects, double AlphaFactor) MapMediaEffects(RV.Media media, string context, MapState state)
    {
        if (Drawing(media) is not { Effects.Count: > 0 } drawing) return (null, 1);
        var chain = new List<Effect>();
        var alpha = 1.0;
        foreach (var effect in drawing.Effects.Where(e => e.Enabled))
        {
            double? Variable(string name)
            {
                foreach (var candidate in effect.Variables.Where(v => v.Name == name))
                {
                    return candidate.TypeCase switch
                    {
                        RV.Effect.Types.EffectVariable.TypeOneofCase.Int => candidate.Int.Value,
                        RV.Effect.Types.EffectVariable.TypeOneofCase.Float => candidate.Float.Value,
                        RV.Effect.Types.EffectVariable.TypeOneofCase.Double => candidate.Double.Value,
                        _ => null,
                    };
                }
                return null;
            }
            switch (effect.Name.ToLowerInvariant())
            {
                case "adjust color":
                    var adjust = new Effect
                    {
                        EffectKind = EffectKind.ColorAdjust,
                        Brightness = Variable("brightness") ?? 0,
                        Contrast = (Variable("contrast") ?? 1) - 1,
                        Saturation = Variable("saturation") ?? 1,
                        Hue = Variable("hue") ?? 0,
                    };
                    var neutral = adjust.Brightness == 0 && adjust.Contrast == 0 && adjust.Saturation == 1 && adjust.Hue == 0;
                    if (!neutral) chain.Add(adjust);
                    break;
                case "adjust alpha":
                    alpha *= Math.Clamp(Variable("amount") ?? 1, 0, 1);
                    break;
                case "blur":
                    var blur = new Effect { EffectKind = EffectKind.Blur, Radius = Variable("amount") ?? Variable("radius") ?? 0 };
                    if ((blur.Radius ?? 0) > 0) chain.Add(blur);
                    break;
                default:
                    state.Warn($"{context}: effect \"{effect.Name}\" has no MxU equivalent and was dropped");
                    break;
            }
        }
        return (chain.Count == 0 ? null : chain, alpha);
    }

    internal static MediaSourceRect? MediaSourceRectOf(RV.Media media)
    {
        if (Drawing(media) is not { CropEnable: true, NaturalSize: { } size } drawing) return null;
        if (size.Width <= 0 || size.Height <= 0) return null;
        var insets = drawing.CropInsets ?? new RV.Graphics.Types.EdgeInsets();
        var x = insets.Left / size.Width;
        var y = insets.Top / size.Height;
        var width = (size.Width - insets.Left - insets.Right) / size.Width;
        var height = (size.Height - insets.Top - insets.Bottom) / size.Height;
        if (width <= 0 || height <= 0 || !double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height))
            return null;
        if (x == 0 && y == 0 && width == 1 && height == 1) return null;
        return new MediaSourceRect { X = x, Y = y, Width = width, Height = height };
    }

    internal static bool MediaLoops(RV.Media media) =>
        media is { TypePropertiesCase: RV.Media.TypePropertiesOneofCase.Video, Video.Transport: { } transport }
        && transport.PlaybackBehavior is RV.Media.Types.TransportProperties.Types.PlaybackBehavior.Loop
            or RV.Media.Types.TransportProperties.Types.PlaybackBehavior.LoopForCount
            or RV.Media.Types.TransportProperties.Types.PlaybackBehavior.LoopForTime;

    internal static double NormalizedOpacity(double raw) => Math.Clamp(raw > 1 ? raw / 100 : raw, 0, 1);

    private static ObjectShadow ObjectShadow(RV.Graphics.Types.Shadow shadow)
    {
        var radians = shadow.Angle * Math.PI / 180;
        var color = shadow.Color?.Clone() ?? new RV.Color();
        color.Alpha = (float)(color.Alpha * (shadow.Opacity == 0 ? 1 : NormalizedOpacity(shadow.Opacity)));
        return new ObjectShadow
        {
            ColorHex = HexColor(color),
            BlurRadius = shadow.Radius,
            OffsetX = SwiftCompat.Rounded(Math.Cos(radians) * shadow.Offset * 100) / 100,
            OffsetY = SwiftCompat.Rounded(-Math.Sin(radians) * shadow.Offset * 100) / 100,
        };
    }

    internal static string? HotKeyLetter(RV.KeyCode code)
    {
        var raw = (int)code;
        return raw is >= 1 and <= 26 ? ((char)(96 + raw)).ToString() : null;
    }

    public static string HexColor(RV.Color? color)
    {
        static int Channel(float value) => (int)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f, MidpointRounding.AwayFromZero);
        color ??= new RV.Color();
        return $"#{Channel(color.Red):X2}{Channel(color.Green):X2}{Channel(color.Blue):X2}{Channel(color.Alpha):X2}";
    }

    private static string ElementId(RV.Graphics.Types.Element element) =>
        element.Uuid.Raw().Length == 0 ? SwiftCompat.NewId() : element.Uuid.Lower();

    private static string ElementName(RV.Graphics.Types.Element element, string fallback) =>
        element.Name.Length == 0 ? fallback : element.Name;

    internal static AttributedText? AttributedString(Google.Protobuf.ByteString? data) =>
        data is null || data.IsEmpty ? null : AttributedText.FromRtf(data.Span);
}
