using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;
using RV = Pro.SerializationInterop.RVProtoData;
using MapState = MxU.Slides.ProImport.ProDocumentMapper.MapState;

namespace MxU.Slides.ProImport;

public sealed record ProMappedTheme(Theme Theme, List<ProMediaWant> MediaWants, List<string> Warnings);

public sealed record ProMappedOverlay(Overlay Overlay, List<ProMediaWant> MediaWants, List<string> Warnings);

public sealed record ProMappedAlert(AlertPreset Preset, string? TemplateThemeName, string? TemplateSlideName, List<string> Warnings);

public sealed record ProMappedLayout(ConfidenceLayout Layout, List<ProMediaWant> MediaWants, List<string> Warnings);

public sealed record ProMappedCombo(ActionCombo Combo, List<ProMediaWant> MediaWants, List<string> Warnings);

public sealed record ProMappedTrigger(ScheduleTrigger Trigger, List<string> Warnings);

/// <summary>
/// Video inputs met during an import, deduplicated by device and name: the workspace's own inputs,
/// the app's existing ones (seeded), and any a slide or macro names that neither list has.
/// </summary>
public sealed class ProInputPlanBook
{
    private readonly List<ProInputPlan> plans = [];
    private readonly Dictionary<string, string> idsByKey = [];
    private readonly List<string> workspaceIds = [];

    public IReadOnlyList<ProInputPlan> Plans => plans;

    public bool IsEnabled { get; private set; }

    public void Seed(IEnumerable<ProInputSeed> existing)
    {
        IsEnabled = true;
        foreach (var input in existing)
            foreach (var key in Keys(input.Name, input.SourceId))
                idsByKey.TryAdd(key, input.Id);
    }

    public void RegisterWorkspaceInput(string uuid, string name, RV.Media.Types.VideoDevice? device)
    {
        IsEnabled = true;
        workspaceIds.Add(Id(device, uuid.Length == 0 ? null : uuid.ToLowerInvariant(), name));
    }

    public string Id(RV.Media.Types.VideoDevice? device, string? preferredId = null, string fallbackName = "")
    {
        IsEnabled = true;
        device ??= new RV.Media.Types.VideoDevice();
        var mapped = Device(device);
        var name = fallbackName.Length == 0 ? device.Name : fallbackName;
        if (name.Length == 0) name = "Video Input";
        var keys = Keys(name, mapped.SourceId);
        foreach (var key in keys)
            if (idsByKey.TryGetValue(key, out var existing)) return existing;
        var id = preferredId ?? SwiftCompat.NewId();
        plans.Add(new ProInputPlan(id, name, mapped.Kind, mapped.SourceId));
        foreach (var key in keys) idsByKey[key] = id;
        return id;
    }

    public string? WorkspaceInputId(int index) => index >= 0 && index < workspaceIds.Count ? workspaceIds[index] : null;

    public static (CaptureSourceKind Kind, string? SourceId) Device(RV.Media.Types.VideoDevice device)
    {
        var kind = device.Type == RV.Media.Types.VideoDevice.Types.Type.Ndi ? CaptureSourceKind.Ndi : CaptureSourceKind.Camera;
        var sourceId = kind == CaptureSourceKind.Ndi ? (device.Name.Length == 0 ? device.UniqueId : device.Name) : device.UniqueId;
        return (kind, sourceId.Length == 0 ? null : sourceId);
    }

    private static List<string> Keys(string name, string? sourceId)
    {
        var keys = new List<string>();
        if (!string.IsNullOrEmpty(sourceId)) keys.Add("device:" + sourceId.ToLowerInvariant());
        if (name.Length > 0) keys.Add("name:" + name.ToLowerInvariant());
        return keys;
    }
}

/// <summary>ProPresenter workspace documents (themes, props, messages, stage, macros, calendar, screens, looks, timers) → MxU.</summary>
public static partial class ProWorkspaceMapper
{
    private const string ImportFolder = "ProPresenter Import";
    private const string AutomaticTimerPhrase = "the first running timer";
    private static readonly IReadOnlyDictionary<string, string> NoTimers = new Dictionary<string, string>();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlphanumerics();

    public static string ThemeId(string name) => "pro7-theme-" + NonAlphanumerics().Replace(name.ToLowerInvariant(), "-").Trim('-');

    public static ProMappedTheme MapTheme(RV.Template.Types.Document doc, string name)
    {
        var state = new MapState();
        var slides = new List<Slide>();
        foreach (var templateSlide in doc.Slides)
        {
            var baseSlide = templateSlide.BaseSlide ?? new RV.Slide();
            var objects = new List<SlideObject>();
            if (baseSlide.DrawsBackgroundColor && baseSlide.BackgroundColor is { } backgroundColor)
            {
                objects.Add(new SlideObject
                {
                    Id = SwiftCompat.NewId(), ObjectKind = SlideObjectKind.Shape, Name = "Background Color", Text = "",
                    X = 0, Y = 0, Width = 1920, Height = 1080,
                    Fill = new ObjectFill { FillKind = FillKind.Solid, ColorHex = ProDocumentMapper.HexColor(backgroundColor) },
                });
            }
            foreach (var action in templateSlide.Actions)
            {
                if (action.ActionTypeDataCase != RV.Action.ActionTypeDataOneofCase.Media) continue;
                var element = action.Media.Element ?? new RV.Media();
                if (element.TypePropertiesCase == RV.Media.TypePropertiesOneofCase.Audio) continue;
                var size = baseSlide.Size;
                objects.Add(new SlideObject
                {
                    Id = SwiftCompat.NewId(), ObjectKind = SlideObjectKind.Shape, Name = "Background", Text = "",
                    X = 0, Y = 0, Width = size?.Width ?? 1920, Height = size?.Height ?? 1080,
                    Fill = new ObjectFill
                    {
                        FillKind = FillKind.Media, MediaId = state.Want(element, MediaClassification.Background), MediaScaleMode = MediaScaleMode.Fill,
                    },
                });
            }
            foreach (var wrapped in baseSlide.Elements.Reverse())
            {
                var element = wrapped.Element_ ?? new RV.Graphics.Types.Element();
                if (ProDocumentMapper.MapElement(element, state) is { } obj)
                {
                    if (element.Hidden) obj.Hidden = true;
                    objects.Add(obj);
                }
                else if (EmptyTextPlaceholder(element, state) is { } placeholder)
                {
                    if (element.Hidden) placeholder.Hidden = true;
                    objects.Add(placeholder);
                }
            }
            slides.Add(new Slide
            {
                Id = baseSlide.Uuid.Raw().Length == 0 ? SwiftCompat.NewId() : baseSlide.Uuid.Lower(),
                Name = templateSlide.Name.Length == 0 ? "Theme Slide" : templateSlide.Name,
                Objects = objects,
            });
        }
        var firstStyle = slides.SelectMany(s => s.Objects).Select(o => o.TextStyle).FirstOrDefault(s => s is not null);
        var theme = new Theme
        {
            Id = ThemeId(name),
            Name = name,
            FontFamily = firstStyle?.FontName ?? "Helvetica Neue",
            FontSize = firstStyle?.FontSize ?? 96,
            TextColorHex = firstStyle?.ColorHex ?? "#FFFFFFFF",
            BackgroundColorHex = "#000000FF",
            Slides = slides.Count == 0 ? null : slides,
        };
        return new ProMappedTheme(theme, state.Wants, state.Warnings);
    }

    internal static SlideObject? EmptyTextPlaceholder(RV.Graphics.Types.Element element, MapState state)
    {
        if (element.Text is null) return null;
        var obj = new SlideObject
        {
            Id = element.Uuid.Raw().Length == 0 ? SwiftCompat.NewId() : element.Uuid.Lower(),
            ObjectKind = SlideObjectKind.Text,
            Name = element.Name.Length == 0 ? "Text Placeholder" : element.Name,
            Text = "",
        };
        if (element.Bounds is { } bounds)
        {
            obj.X = bounds.Origin?.X ?? 0;
            obj.Y = bounds.Origin?.Y ?? 0;
            obj.Width = bounds.Size?.Width ?? 0;
            obj.Height = bounds.Size?.Height ?? 0;
        }
        obj.TextStyle = ProDocumentMapper.TextStyleFor(element, null, state);
        return obj;
    }

    public static List<ProMappedOverlay> MapProps(RV.PropDocument doc)
    {
        var overlays = new List<ProMappedOverlay>();
        foreach (var cue in doc.Cues)
        {
            var state = new MapState();
            var propAction = cue.Actions.FirstOrDefault(a =>
                a is { ActionTypeDataCase: RV.Action.ActionTypeDataOneofCase.Slide, Slide.SlideCase: RV.Action.Types.SlideType.SlideOneofCase.Prop });
            if (propAction is null) continue;
            var baseSlide = propAction.Slide.Prop.BaseSlide ?? new RV.Slide();
            var objects = new List<SlideObject>();
            foreach (var wrapped in baseSlide.Elements.Reverse())
            {
                var element = wrapped.Element_ ?? new RV.Graphics.Types.Element();
                if (ProDocumentMapper.MapElement(element, state) is not { } obj) continue;
                if (element.Hidden) obj.Hidden = true;
                objects.Add(obj);
            }
            var overlay = new Overlay
            {
                Id = cue.Uuid.Raw().Length == 0 ? SwiftCompat.NewId() : cue.Uuid.Lower(),
                Name = cue.Name.Length == 0 ? "Prop" : cue.Name,
                Objects = objects,
                Folder = ImportFolder,
            };
            overlays.Add(new ProMappedOverlay(overlay, state.Wants, state.Warnings));
        }
        return overlays;
    }

    public static List<ProMappedAlert> MapMessages(RV.MessageDocument doc) => doc.Messages.Select(message =>
    {
        var warnings = new List<string>();
        var text = message.MessageText;
        foreach (var token in message.Tokens)
        {
            string name;
            switch (token.TokenTypeCase)
            {
                case RV.Message.Types.Token.TokenTypeOneofCase.Text: name = token.Text.Name; break;
                case RV.Message.Types.Token.TokenTypeOneofCase.Timer: name = token.Timer.Name; break;
                case RV.Message.Types.Token.TokenTypeOneofCase.Clock:
                    name = "clock";
                    warnings.Add($"message \"{message.Title}\": clock token becomes a fill-at-fire slot");
                    break;
                default: continue;
            }
            var uuid = token.Uuid.Raw();
            if (uuid.Length > 0) text = text.Replace("${" + uuid + "}", "{" + name + "}", StringComparison.OrdinalIgnoreCase);
            if (!text.Contains("{" + name + "}", StringComparison.CurrentCultureIgnoreCase))
                text += text.Length == 0 ? "{" + name + "}" : " {" + name + "}";
        }
        if (message.TimeToRemove > 0)
            warnings.Add($"message \"{message.Title}\": auto-clear after {(long)message.TimeToRemove}s is not supported — the alert clears manually");

        var templateName = message.Template?.Name ?? "";
        var hasTemplate = templateName.Length > 0;
        var preset = new AlertPreset
        {
            Id = message.Uuid.Raw().Length == 0 ? SwiftCompat.NewId() : message.Uuid.Lower(),
            Name = message.Title.Length == 0 ? "Message" : message.Title,
            Message = text,
            Behavior = AlertBehavior.Persist,
            Target = hasTemplate ? AlertTarget.Both : null,
        };
        var slideName = message.Template?.SlideName ?? "";
        return new ProMappedAlert(preset, hasTemplate ? templateName : null, slideName.Length == 0 ? null : slideName, warnings);
    }).ToList();

    public static List<ProMappedTrigger> MapCalendar(
        RV.Calendar doc,
        IReadOnlyDictionary<string, string>? timerIdsByProUuid = null,
        IReadOnlyDictionary<string, string>? timerIdsByName = null,
        TimeZoneInfo? timeZone = null)
    {
        timerIdsByProUuid ??= NoTimers;
        timerIdsByName ??= NoTimers;
        timeZone ??= TimeZoneInfo.Local;
        DateTime Local(RV.Timestamp? stamp) =>
            TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(stamp?.Seconds ?? 0), timeZone).DateTime;
        static string Day(DateTime date) => $"{SwiftCompat.Pad(date.Year, 4)}-{SwiftCompat.Pad(date.Month, 2)}-{SwiftCompat.Pad(date.Day, 2)}";

        return doc.Events.Select(calendarEvent =>
        {
            var state = new MapState();
            var name = calendarEvent.Name.Length == 0 ? "Calendar Event" : calendarEvent.Name;
            var context = $"calendar event \"{name}\"";
            var date = Local(calendarEvent.Date);
            var wall = $"{SwiftCompat.Pad(date.Hour, 2)}:{SwiftCompat.Pad(date.Minute, 2)}";
            if (date.Second > 0) wall += ":" + SwiftCompat.Pad(date.Second, 2);

            var condition = calendarEvent.RecurrenceDays.Count == 0
                ? new ScheduleCondition { Id = SwiftCompat.NewId(), Kind = ScheduleConditionKind.OneTime, Date = $"{Day(date)}T{wall}" }
                : new ScheduleCondition
                {
                    Id = SwiftCompat.NewId(), Kind = ScheduleConditionKind.Weekly,
                    Days = calendarEvent.RecurrenceDays.Select(d => (int)d).Where(d => d is >= 1 and <= 7).ToList(),
                    TimeOfDay = wall,
                };

            string? enabledUntil = null;
            if (calendarEvent.RecurrenceLimitDate is { Seconds: > 0 } limit)
                enabledUntil = $"{Day(Local(limit))}T23:59:59";
            if (calendarEvent.RecurrenceExcludedDates.Count > 0 && condition.Kind == ScheduleConditionKind.Weekly)
                condition.ExcludedDates = calendarEvent.RecurrenceExcludedDates.Select(stamp => Day(Local(stamp))).ToList();

            var actions = new List<SlideAction>();
            foreach (var proAction in calendarEvent.Actions)
            {
                if (MapActionSteps(proAction, context, timerIdsByProUuid, timerIdsByName, state) is { } steps) actions.AddRange(steps);
                else state.Warn($"{context}: {ProDocumentMapper.DescribeActionKind(proAction)} steps have no Scheduler equivalent yet");
            }
            if (actions.Count == 0 && calendarEvent.Action is { } eventAction)
            {
                switch (eventAction.ActionTypeCase)
                {
                    case RV.Calendar.Types.Event.Types.Action.ActionTypeOneofCase.Macro:
                        actions.Add(new SlideAction
                        {
                            Id = SwiftCompat.NewId(), Kind = SlideActionKind.FireCombo,
                            ComboId = eventAction.Macro.Identification?.ParameterUuid.Lower() ?? "",
                        });
                        break;
                    case RV.Calendar.Types.Event.Types.Action.ActionTypeOneofCase.Playlist:
                        state.Warn($"{context}: a fire-playlist calendar action has no Scheduler read yet");
                        break;
                }
            }
            var trigger = new ScheduleTrigger
            {
                Id = calendarEvent.Uuid.Raw().Length == 0 ? SwiftCompat.NewId() : calendarEvent.Uuid.Lower(),
                Name = name,
                Conditions = [condition],
                Actions = actions,
                EnabledUntil = enabledUntil,
            };
            return new ProMappedTrigger(trigger, state.Warnings);
        }).ToList();
    }

    public static List<ProMappedLayout> MapStageLayouts(
        RV.Stage.Types.Document doc,
        IReadOnlyDictionary<string, string>? timerIdsByProUuid = null,
        IReadOnlyDictionary<string, string>? timerIdsByName = null,
        ProInputPlanBook? inputs = null)
    {
        timerIdsByProUuid ??= NoTimers;
        timerIdsByName ??= NoTimers;
        inputs ??= new ProInputPlanBook();
        return doc.Layouts.Select(layout =>
        {
            var state = new MapState { InputBook = inputs };
            var baseSlide = layout.Slide ?? new RV.Slide();
            var objects = new List<SlideObject>();
            foreach (var wrapped in baseSlide.Elements.Reverse())
            {
                var element = wrapped.Element_ ?? new RV.Graphics.Types.Element();
                var obj = ProDocumentMapper.MapElement(element, state);
                if (TextLink(wrapped, timerIdsByProUuid, timerIdsByName, state) is { } link)
                {
                    obj ??= EmptyTextPlaceholder(element, state);
                    if (obj is not null)
                    {
                        obj.ObjectKind = SlideObjectKind.Text;
                        obj.TextLink = link;
                    }
                }
                if (Visibility(wrapped, timerIdsByProUuid, timerIdsByName, state) is { } visibility)
                {
                    obj ??= EmptyTextPlaceholder(element, state);
                    if (obj is not null)
                    {
                        obj.VisibilityMatch = visibility.Match;
                        obj.VisibilityConditions = visibility.Conditions;
                    }
                }
                if (obj is null) continue;
                if (element.Hidden) obj.Hidden = true;
                objects.Add(obj);
            }
            (int Width, int Height)? canvas = baseSlide.Size is { Width: > 0 } size ? ((int)size.Width, (int)size.Height) : null;
            var wide = canvas is (1920, 1080);
            var confidenceLayout = new ConfidenceLayout
            {
                Id = layout.Uuid.Raw().Length == 0 ? SwiftCompat.NewId() : layout.Uuid.Lower(),
                Name = layout.Name.Length == 0 ? "Stage Layout" : layout.Name,
                Objects = objects,
                Folder = ImportFolder,
                CanvasWidth = canvas is null || wide ? null : canvas.Value.Width,
                CanvasHeight = canvas is null || wide ? null : canvas.Value.Height,
            };
            return new ProMappedLayout(confidenceLayout, state.Wants, state.Warnings);
        }).ToList();
    }

    internal static TextLink? TextLink(
        RV.Slide.Types.Element element, IReadOnlyDictionary<string, string> timerIdsByProUuid,
        IReadOnlyDictionary<string, string> timerIdsByName, MapState state)
    {
        foreach (var dataLink in element.DataLinks)
        {
            switch (dataLink.PropertyTypeCase)
            {
                case RV.Slide.Types.Element.Types.DataLink.PropertyTypeOneofCase.SlideText:
                    return new TextLink
                    {
                        Source = dataLink.SlideText.SourceSlide == RV.Slide.Types.Element.Types.DataLink.Types.SlideSourceType.NextSlide
                            ? TextSourceKind.NextSlide : TextSourceKind.CurrentSlide,
                    };
                case RV.Slide.Types.Element.Types.DataLink.PropertyTypeOneofCase.TimerText:
                {
                    var timer = dataLink.TimerText;
                    var link = new TextLink { Source = TextSourceKind.Timer };
                    if (timerIdsByProUuid.TryGetValue(timer.TimerUuid.Lower(), out var mapped)
                        || timerIdsByName.TryGetValue(timer.TimerName.ToLowerInvariant(), out mapped))
                        link.TimerId = mapped;
                    else if (timer.TimerName.Length > 0)
                        state.Warn($"timer \"{timer.TimerName}\" was not imported — the linked box follows {AutomaticTimerPhrase}");
                    return link;
                }
                case RV.Slide.Types.Element.Types.DataLink.PropertyTypeOneofCase.ClockText:
                    return new TextLink
                    {
                        Source = TextSourceKind.Clock,
                        ClockFormat = dataLink.ClockText.Format?.MilitaryTimeEnabled == true ? "H:mm" : "h:mm a",
                    };
                case RV.Slide.Types.Element.Types.DataLink.PropertyTypeOneofCase.VideoCountdown:
                    return new TextLink { Source = TextSourceKind.VideoCountdown };
                case RV.Slide.Types.Element.Types.DataLink.PropertyTypeOneofCase.StageMessage:
                    return new TextLink { Source = TextSourceKind.StageMessage };
                case RV.Slide.Types.Element.Types.DataLink.PropertyTypeOneofCase.SlideCount:
                    return new TextLink { Source = TextSourceKind.SlidePosition };
                case RV.Slide.Types.Element.Types.DataLink.PropertyTypeOneofCase.GroupName:
                    return new TextLink { Source = TextSourceKind.CurrentGroup };
                case RV.Slide.Types.Element.Types.DataLink.PropertyTypeOneofCase.PlaylistItem:
                    switch (dataLink.PlaylistItem.PlaylistItemSource)
                    {
                        case RV.Slide.Types.Element.Types.DataLink.Types.PlaylistItem.Types.PlaylistItemSourceType.Current:
                            return new TextLink { Source = TextSourceKind.CurrentServiceItem };
                        case RV.Slide.Types.Element.Types.DataLink.Types.PlaylistItem.Types.PlaylistItemSourceType.Next:
                            return new TextLink { Source = TextSourceKind.NextServiceItem };
                        default:
                            state.Warn($"a playlist-item link ({SwiftCompat.CaseName(dataLink.PlaylistItem.PlaylistItemSource)}) has no MxU source yet and imported as static text");
                            return null;
                    }
                case RV.Slide.Types.Element.Types.DataLink.PropertyTypeOneofCase.Presentation:
                    return new TextLink { Source = TextSourceKind.CurrentPresentation };
                case RV.Slide.Types.Element.Types.DataLink.PropertyTypeOneofCase.PresentationNotes:
                    state.Warn("a \"notes\" link has no MxU source yet and imported as static text");
                    return null;
                case RV.Slide.Types.Element.Types.DataLink.PropertyTypeOneofCase.VisibilityLink
                    or RV.Slide.Types.Element.Types.DataLink.PropertyTypeOneofCase.None:
                    continue;
                default:
                    state.Warn($"a data link ({SwiftCompat.CaseName(dataLink.PropertyTypeCase)}) has no MxU source yet and imported as static text");
                    return null;
            }
        }
        return null;
    }

    internal static (VisibilityMatch? Match, List<VisibilityCondition> Conditions)? Visibility(
        RV.Slide.Types.Element element, IReadOnlyDictionary<string, string> timerIdsByProUuid,
        IReadOnlyDictionary<string, string> timerIdsByName, MapState state)
    {
        foreach (var dataLink in element.DataLinks)
        {
            if (dataLink.PropertyTypeCase != RV.Slide.Types.Element.Types.DataLink.PropertyTypeOneofCase.VisibilityLink) continue;
            var link = dataLink.VisibilityLink;
            if (link.Conditions.Count == 0) return null;
            VisibilityMatch? match = link.VisibilityCriterion switch
            {
                RV.Slide.Types.Element.Types.DataLink.Types.VisibilityLink.Types.VisibilityCriterion.Any => VisibilityMatch.Any,
                RV.Slide.Types.Element.Types.DataLink.Types.VisibilityLink.Types.VisibilityCriterion.None => VisibilityMatch.None,
                _ => null,
            };
            static VisibilityConditionState? TimeState(int raw) => raw switch
            {
                0 => VisibilityConditionState.HasTimeRemaining,
                1 => VisibilityConditionState.HasExpired,
                2 => VisibilityConditionState.IsRunning,
                3 => VisibilityConditionState.IsNotRunning,
                _ => null,
            };

            var conditions = new List<VisibilityCondition>();
            foreach (var condition in link.Conditions)
            {
                switch (condition.ConditionTypeCase)
                {
                    case RV.Slide.Types.Element.Types.DataLink.Types.VisibilityLink.Types.Condition.ConditionTypeOneofCase.TimerVisibility:
                    {
                        var timer = condition.TimerVisibility;
                        if (TimeState((int)timer.VisibilityCriterion) is not { } timerState)
                        {
                            state.Warn("a visibility condition uses an unsupported timer state and was dropped");
                            continue;
                        }
                        var mapped = new VisibilityCondition { ConditionKind = VisibilityConditionKind.Timer, State = timerState };
                        if (timerIdsByProUuid.TryGetValue(timer.TimerUuid.Lower(), out var id)
                            || timerIdsByName.TryGetValue(timer.TimerName.ToLowerInvariant(), out id))
                            mapped.TimerId = id;
                        else if (timer.TimerName.Length > 0)
                            state.Warn($"a visibility condition watches timer \"{timer.TimerName}\", which wasn't imported — it follows {AutomaticTimerPhrase}");
                        conditions.Add(mapped);
                        break;
                    }
                    case RV.Slide.Types.Element.Types.DataLink.Types.VisibilityLink.Types.Condition.ConditionTypeOneofCase.VideoCountdownVisibility:
                        if (TimeState((int)condition.VideoCountdownVisibility.VisibilityCriterion) is not { } videoState)
                        {
                            state.Warn("a video-countdown visibility condition uses a looping state, which has no MxU read — dropped");
                            continue;
                        }
                        conditions.Add(new VisibilityCondition { ConditionKind = VisibilityConditionKind.VideoCountdown, State = videoState });
                        break;
                    case RV.Slide.Types.Element.Types.DataLink.Types.VisibilityLink.Types.Condition.ConditionTypeOneofCase.AudioCountdownVisibility:
                        if (TimeState((int)condition.AudioCountdownVisibility.VisibilityCriterion) is not { } audioState)
                        {
                            state.Warn("an audio visibility condition uses a looping state, which has no MxU read — dropped");
                            continue;
                        }
                        conditions.Add(new VisibilityCondition { ConditionKind = VisibilityConditionKind.AudioPlayback, State = audioState });
                        break;
                    case RV.Slide.Types.Element.Types.DataLink.Types.VisibilityLink.Types.Condition.ConditionTypeOneofCase.ElementVisibility:
                    {
                        var other = condition.ElementVisibility;
                        var mapped = new VisibilityCondition
                        {
                            ConditionKind = VisibilityConditionKind.ObjectText,
                            State = other.VisibilityCriterion == RV.Slide.Types.Element.Types.DataLink.Types.VisibilityLink.Types.Condition.Types.ElementVisibility.Types.ElementVisibilityCriterion.HasNoText
                                ? VisibilityConditionState.HasNoText : VisibilityConditionState.HasText,
                        };
                        var uuid = other.OtherElementUuid.Lower();
                        if (uuid.Length > 0) mapped.ObjectId = uuid;
                        conditions.Add(mapped);
                        break;
                    }
                    case RV.Slide.Types.Element.Types.DataLink.Types.VisibilityLink.Types.Condition.ConditionTypeOneofCase.CaptureSessionVisibility:
                        conditions.Add(new VisibilityCondition
                        {
                            ConditionKind = VisibilityConditionKind.Capture,
                            State = condition.CaptureSessionVisibility.VisibilityCriterion == RV.Slide.Types.Element.Types.DataLink.Types.VisibilityLink.Types.Condition.Types.CaptureSessionVisibility.Types.CaptureSessionVisibilityCriterion.Inactive
                                ? VisibilityConditionState.IsInactive : VisibilityConditionState.IsActive,
                        });
                        break;
                    case RV.Slide.Types.Element.Types.DataLink.Types.VisibilityLink.Types.Condition.ConditionTypeOneofCase.VideoInputVisibility:
                    {
                        var input = condition.VideoInputVisibility;
                        var mapped = new VisibilityCondition
                        {
                            ConditionKind = VisibilityConditionKind.LiveInput,
                            State = input.VisibilityCriterion == RV.Slide.Types.Element.Types.DataLink.Types.VisibilityLink.Types.Condition.Types.VideoInputVisibility.Types.VideoInputVisibilityCriterion.Inactive
                                ? VisibilityConditionState.IsInactive : VisibilityConditionState.IsActive,
                        };
                        if (state.InputBook.WorkspaceInputId(input.VideoInputIndex) is { } id) mapped.LiveInputId = id;
                        else state.Warn("a video-input visibility condition imported — pick the MxU input on the object");
                        conditions.Add(mapped);
                        break;
                    }
                }
            }
            return conditions.Count == 0 ? null : (match, conditions);
        }
        return null;
    }

    public static List<ProMappedCombo> MapMacros(
        RV.MacrosDocument doc,
        IReadOnlyDictionary<string, string>? timerIdsByProUuid = null,
        IReadOnlyDictionary<string, string>? timerIdsByName = null,
        ProInputPlanBook? inputs = null)
    {
        timerIdsByProUuid ??= NoTimers;
        timerIdsByName ??= NoTimers;
        inputs ??= new ProInputPlanBook();
        return doc.Macros.Concat(doc.MacroCollections.SelectMany(c => c.Macros))
            .Where(macro => macro.Actions.Count > 0)
            .Select(macro => MapMacro(macro, timerIdsByProUuid, timerIdsByName, inputs))
            .ToList();
    }

    private static ProMappedCombo MapMacro(
        RV.MacrosDocument.Types.Macro macro, IReadOnlyDictionary<string, string> timerIdsByProUuid,
        IReadOnlyDictionary<string, string> timerIdsByName, ProInputPlanBook inputs)
    {
        var state = new MapState { InputBook = inputs };
        var actions = new List<SlideAction>();
        foreach (var proAction in macro.Actions)
        {
            if (MapActionSteps(proAction, $"macro \"{macro.Name}\"", timerIdsByProUuid, timerIdsByName, state) is { } steps)
                actions.AddRange(steps);
            else if (proAction.ActionTypeDataCase == RV.Action.ActionTypeDataOneofCase.Slide)
                state.Warn($"macro \"{macro.Name}\": an inline slide step has no combo equivalent");
            else
                state.Warn($"macro \"{macro.Name}\": {ProDocumentMapper.DescribeActionKind(proAction)} steps have no combo equivalent yet");
        }
        var combo = new ActionCombo
        {
            Id = macro.Uuid.Raw().Length == 0 ? SwiftCompat.NewId() : macro.Uuid.Lower(),
            Name = macro.Name.Length == 0 ? "Macro" : macro.Name,
            Actions = actions,
            ColorHex = macro.Color is { } color ? ProDocumentMapper.HexColor(color) : null,
        };
        return new ProMappedCombo(combo, state.Wants, state.Warnings);
    }

    internal static List<SlideAction>? MapActionSteps(
        RV.Action? proAction, string context,
        IReadOnlyDictionary<string, string> timerIdsByProUuid, IReadOnlyDictionary<string, string> timerIdsByName,
        MapState state)
    {
        proAction ??= new RV.Action();
        var actions = new List<SlideAction>();
        SlideAction Step(SlideActionKind kind) => new() { Id = SwiftCompat.NewId(), Kind = kind };
        void ClearLayer(string layer)
        {
            var step = Step(SlideActionKind.ClearLayer);
            step.Layer = layer;
            actions.Add(step);
        }

        switch (proAction.ActionTypeDataCase)
        {
            case RV.Action.ActionTypeDataOneofCase.Media:
            {
                var element = proAction.Media.Element ?? new RV.Media();
                switch (element.TypePropertiesCase)
                {
                    case RV.Media.TypePropertiesOneofCase.Image or RV.Media.TypePropertiesOneofCase.Video:
                        var fire = Step(SlideActionKind.FireMedia);
                        fire.MediaId = state.Want(element);
                        actions.Add(fire);
                        break;
                    case RV.Media.TypePropertiesOneofCase.LiveVideo:
                    {
                        var live = Step(SlideActionKind.FireLiveInput);
                        var device = element.LiveVideo.LiveVideo?.VideoDevice ?? new RV.Media.Types.VideoDevice();
                        var mapped = ProInputPlanBook.Device(device);
                        if (state.InputBook.IsEnabled)
                        {
                            live.LiveInputId = state.InputBook.Id(device);
                            if (mapped.SourceId is null)
                                state.Warn($"{context}: its video-input step named no device — assign one to the imported input in Settings › Audio/Video Inputs");
                        }
                        else
                        {
                            live.InputSourceKind = mapped.Kind;
                            live.InputSourceId = mapped.SourceId;
                            if (live.InputSourceId is null)
                                state.Warn($"{context}: its video-input step named no device — pick the source on the imported combo");
                        }
                        actions.Add(live);
                        break;
                    }
                    default:
                        state.Warn($"{context}: an audio/web media step has no combo equivalent yet");
                        break;
                }
                break;
            }
            case RV.Action.ActionTypeDataOneofCase.Clear:
                switch (proAction.Clear.TargetLayer)
                {
                    case RV.Action.Types.ClearType.Types.ClearTargetLayer.All: actions.Add(Step(SlideActionKind.ClearAll)); break;
                    case RV.Action.Types.ClearType.Types.ClearTargetLayer.Slide: ClearLayer("slide"); break;
                    case RV.Action.Types.ClearType.Types.ClearTargetLayer.Background:
                        foreach (var layer in new[] { "loopingVideos", "stillGraphics", "videos" }) ClearLayer(layer);
                        break;
                    case RV.Action.Types.ClearType.Types.ClearTargetLayer.Prop: ClearLayer("overlays"); break;
                    case RV.Action.Types.ClearType.Types.ClearTargetLayer.Messages: ClearLayer("alerts"); break;
                    case RV.Action.Types.ClearType.Types.ClearTargetLayer.LiveVideo: ClearLayer("videoInput"); break;
                    case RV.Action.Types.ClearType.Types.ClearTargetLayer.Audio: actions.Add(Step(SlideActionKind.ClearAudio)); break;
                    default:
                        state.Warn($"{context}: clear {SwiftCompat.CaseName(proAction.Clear.TargetLayer)} has no MxU layer equivalent");
                        break;
                }
                break;
            case RV.Action.ActionTypeDataOneofCase.Timer:
            {
                var timer = proAction.Timer;
                var identification = timer.TimerIdentification ?? new RV.CollectionElementType();
                var name = identification.ParameterName;
                string? timerId = timerIdsByProUuid.TryGetValue(identification.ParameterUuid.Lower(), out var byUuid) ? byUuid
                    : timerIdsByName.TryGetValue(name.ToLowerInvariant(), out var byName) ? byName : null;
                if (timerId is null)
                    state.Warn($"{context}: its timer step{(name.Length == 0 ? "" : $" (\"{name}\")")} didn't match an imported timer — pick one on the action");
                void TimerStep(SlideActionKind kind)
                {
                    var step = Step(kind);
                    step.TimerId = timerId;
                    actions.Add(step);
                }
                if (timer.TimerConfiguration is { } configuration && TimerPlanMode(configuration) is { } mode)
                {
                    var configure = Step(SlideActionKind.TimerConfigure);
                    configure.TimerId = timerId;
                    switch (mode)
                    {
                        case ProTimerMode.Countdown countdown:
                            configure.TimerMode = TimerMode.Countdown;
                            configure.TimerDurationSeconds = countdown.Seconds;
                            break;
                        case ProTimerMode.CountdownToTime toTime:
                            configure.TimerMode = TimerMode.CountdownToTime;
                            configure.TimerHour = toTime.Hour;
                            configure.TimerMinute = toTime.Minute;
                            break;
                        case ProTimerMode.CountUp countUp:
                            configure.TimerMode = TimerMode.CountUp;
                            configure.TimerDurationSeconds = countUp.LimitSeconds;
                            break;
                    }
                    actions.Add(configure);
                }
                switch (timer.ActionType)
                {
                    case RV.Action.Types.TimerType.Types.TimerAction.ActionStart: TimerStep(SlideActionKind.TimerStart); break;
                    case RV.Action.Types.TimerType.Types.TimerAction.ActionStop: TimerStep(SlideActionKind.TimerPause); break;
                    case RV.Action.Types.TimerType.Types.TimerAction.ActionReset: TimerStep(SlideActionKind.TimerReset); break;
                    case RV.Action.Types.TimerType.Types.TimerAction.ActionResetAndStart:
                        TimerStep(SlideActionKind.TimerReset);
                        TimerStep(SlideActionKind.TimerStart);
                        break;
                    case RV.Action.Types.TimerType.Types.TimerAction.ActionStopAndReset:
                        TimerStep(SlideActionKind.TimerPause);
                        TimerStep(SlideActionKind.TimerReset);
                        break;
                    default:
                        state.Warn($"{context}: unsupported timer action");
                        break;
                }
                break;
            }
            case RV.Action.ActionTypeDataOneofCase.Stage:
                foreach (var assignment in proAction.Stage.StageScreenAssignments)
                {
                    var step = Step(SlideActionKind.SetConfidenceLayout);
                    step.ConfidenceLayoutId = assignment.Layout?.ParameterUuid.Lower() ?? "";
                    actions.Add(step);
                }
                if (proAction.Stage.StageScreenAssignments.Count > 1)
                    state.Warn($"{context}: Pro7 set different stage layouts per screen; MxU applies each to ALL confidence screens — keep the last step or re-scope after import");
                break;
            case RV.Action.ActionTypeDataOneofCase.Message:
            {
                var step = Step(SlideActionKind.FireAlert);
                step.AlertId = proAction.Message.MessageIdentificaton?.ParameterUuid.Lower() ?? "";
                actions.Add(step);
                break;
            }
            case RV.Action.ActionTypeDataOneofCase.Macro:
            {
                var step = Step(SlideActionKind.FireCombo);
                step.ComboId = proAction.Macro.Identification?.ParameterUuid.Lower() ?? "";
                actions.Add(step);
                break;
            }
            case RV.Action.ActionTypeDataOneofCase.PresentationDocument:
            {
                var step = Step(SlideActionKind.FirePresentation);
                step.PresentationId = proAction.PresentationDocument.Identification?.ParameterUuid.Lower() ?? "";
                actions.Add(step);
                break;
            }
            case RV.Action.ActionTypeDataOneofCase.Prop:
            {
                var overlayId = proAction.Prop.Identification?.ParameterUuid.Lower() ?? "";
                if (proAction.Prop.TriggerTypeCase == RV.Action.Types.PropType.TriggerTypeOneofCase.Clear)
                {
                    var step = Step(SlideActionKind.DismissOverlay);
                    step.OverlayId = overlayId.Length == 0 ? null : overlayId;
                    actions.Add(step);
                }
                else
                {
                    var step = Step(SlideActionKind.FireOverlay);
                    step.OverlayId = overlayId;
                    actions.Add(step);
                }
                break;
            }
            case RV.Action.ActionTypeDataOneofCase.PlaylistItem:
            {
                var playlist = proAction.PlaylistItem;
                var step = Step(SlideActionKind.FireAudioPlaylist);
                step.PlaylistId = playlist.PlaylistUuid.Lower();
                if (playlist.ItemUuid.Raw().Length > 0) step.AudioEntryId = playlist.ItemUuid.Lower();
                actions.Add(step);
                break;
            }
            case RV.Action.ActionTypeDataOneofCase.AudienceLook:
            {
                var step = Step(SlideActionKind.SwitchOutputPreset);
                step.PresetId = proAction.AudienceLook.Identification?.ParameterUuid.Lower() ?? "";
                actions.Add(step);
                break;
            }
            case RV.Action.ActionTypeDataOneofCase.Communication:
            {
                if (proAction.Communication.CommandTypeDataCase != RV.Action.Types.CommunicationType.CommandTypeDataOneofCase.MidiCommand)
                    return null;
                var midi = proAction.Communication.MidiCommand;
                var step = Step(SlideActionKind.MidiOut);
                step.MidiKind = MIDIMessageKind.NoteOn;
                step.MidiChannel = Math.Min((int)midi.Channel + 1, 16);
                step.MidiNumber = (int)midi.Note;
                step.MidiValue = midi.State == RV.Action.Types.CommunicationType.Types.MIDICommand.Types.State.Off ? 0 : (int)midi.Intensity;
                actions.Add(step);
                break;
            }
            case RV.Action.ActionTypeDataOneofCase.None:
                return [];
            default:
                return null;
        }

        if (proAction.DelayTime > 0)
            foreach (var action in actions) action.DelaySeconds = proAction.DelayTime;
        return actions;
    }

    /// <summary>Swaps fire-media placeholders for library ids, dropping steps whose media wasn't found. Returns a copy.</summary>
    public static ActionCombo ReplacingMediaIds(ActionCombo combo, IReadOnlyDictionary<string, string> map)
    {
        var result = MxuJson.Clone(combo);
        result.Actions = result.Actions.Where(step =>
        {
            if (step.MediaId is not { } mediaId || !mediaId.StartsWith(ProDocumentMapper.PlaceholderPrefix, StringComparison.Ordinal)) return true;
            if (!map.TryGetValue(mediaId, out var mapped)) return false;
            step.MediaId = mapped;
            return true;
        }).ToList();
        return result;
    }

    public static (ProScreenPlan Plan, List<string> Warnings) MapScreens(RV.ProPresenterWorkspace workspace)
    {
        var warnings = new List<string>();
        var screens = workspace.ProScreens.Select(screen =>
        {
            var width = 1920;
            var height = 1080;
            ProScreenPlan.OutputCorrection? correction = null;
            List<ProScreenPlan.Slice> slices = [];
            var name = screen.Name.Length == 0 ? "Screen" : screen.Name;
            switch (screen.ArrangementCase)
            {
                case RV.ProPresenterScreen.ArrangementOneofCase.ArrangementSingle:
                    if (screen.ArrangementSingle.Screens.FirstOrDefault() is { } output)
                    {
                        if (output.Bounds is { Size.Width: > 0 } bounds)
                        {
                            width = (int)bounds.Size.Width;
                            height = (int)bounds.Size.Height;
                        }
                        correction = OutputCorrection(output, name, warnings);
                    }
                    break;
                case RV.ProPresenterScreen.ArrangementOneofCase.ArrangementCombined:
                    if (SpanCanvasSize(screen.ArrangementCombined.Screens) is { } combinedSize) (width, height) = combinedSize;
                    slices = SpanSlices(screen.ArrangementCombined.Screens, [], width, height, name, warnings);
                    break;
                case RV.ProPresenterScreen.ArrangementOneofCase.ArrangementEdgeBlend:
                    if (SpanCanvasSize(screen.ArrangementEdgeBlend.Screens) is { } blendSize) (width, height) = blendSize;
                    slices = SpanSlices(screen.ArrangementEdgeBlend.Screens, screen.ArrangementEdgeBlend.EdgeBlends, width, height, name, warnings);
                    break;
            }
            return new ProScreenPlan.Screen
            {
                ProUuid = screen.Uuid.Lower(), Name = name,
                IsConfidence = screen.ScreenType == RV.ProPresenterScreen.Types.ScreenType.Stage,
                Width = width, Height = height, Correction = correction, Slices = slices,
            };
        }).ToList();
        var assignments = workspace.StageLayoutMappings
            .Select(m => new ProScreenPlan.StageAssignment(m.Screen?.ParameterUuid.Lower() ?? "", m.Layout?.ParameterUuid.Lower() ?? ""))
            .ToList();
        var masks = MapMasks(workspace, warnings);
        return (new ProScreenPlan { Screens = screens, StageAssignments = assignments, Masks = masks }, warnings);
    }

    private static List<ProScreenPlan.MaskPlan> MapMasks(RV.ProPresenterWorkspace workspace, List<string> warnings)
    {
        var plans = new List<ProScreenPlan.MaskPlan>();
        foreach (var mask in workspace.Masks)
        {
            var slide = mask.BaseSlide ?? new RV.Slide();
            var name = mask.Name.Length == 0 ? "Mask" : mask.Name;
            var canvasWidth = slide.Size is { Width: > 0 } ? slide.Size.Width : 1920;
            var canvasHeight = slide.Size is { Height: > 0 } ? slide.Size.Height : 1080;
            var state = new MapState();
            var shapes = new List<ProScreenPlan.MaskShape>();
            foreach (var wrapped in slide.Elements)
            {
                if (ProDocumentMapper.MapElement(wrapped.Element_ ?? new RV.Graphics.Types.Element(), state) is not { } obj) continue;
                if (obj.ObjectKind != SlideObjectKind.Shape)
                {
                    warnings.Add($"mask \"{name}\": a {SlideObjectKindJsonConverter.ToRaw(obj.ObjectKind)} element has no mask reading and was dropped");
                    continue;
                }
                shapes.Add(new ProScreenPlan.MaskShape(
                    (obj.X ?? 0) / canvasWidth, (obj.Y ?? 0) / canvasHeight,
                    (obj.Width ?? 0) / canvasWidth, (obj.Height ?? 0) / canvasHeight,
                    ShapeKindJsonConverter.ToRaw(obj.ShapeKind ?? ShapeKind.Rectangle), obj.CornerRadius ?? 0, obj.PathData));
            }
            if (shapes.Count == 0)
            {
                warnings.Add($"mask \"{name}\": no readable shapes — dropped");
                continue;
            }
            plans.Add(new ProScreenPlan.MaskPlan(slide.Uuid.Lower(), name, shapes));
        }
        return plans;
    }

    private static List<ProScreenPlan.Slice> SpanSlices(
        IList<RV.Screen> outputs, IList<RV.EdgeBlend> edgeBlends, int canvasWidth, int canvasHeight,
        string screenName, List<string> warnings)
    {
        var union = SpanCanvasSize(outputs);
        var rects = outputs.Where(o => o.Bounds is { Size.Width: > 0 }).ToList();
        var originX = rects.Count == 0 ? 0 : rects.Min(o => o.Bounds.Origin?.X ?? 0);
        var originY = rects.Count == 0 ? 0 : rects.Min(o => o.Bounds.Origin?.Y ?? 0);

        var edges = new Dictionary<string, List<(RV.EdgeBlend.Types.Screen.Types.Edge Edge, RV.EdgeBlend.Types.Screen Entry)>>();
        foreach (var blend in edgeBlends)
        {
            var entries = new[] { blend.FirstScreen, blend.SecondScreen, blend.LeftScreen, blend.RightScreen, blend.TopScreen, blend.BottomScreen };
            foreach (var entry in entries)
            {
                if (entry is null || entry.Edge == RV.EdgeBlend.Types.Screen.Types.Edge.Unknown) continue;
                var key = entry.Uuid.Lower();
                if (!edges.TryGetValue(key, out var list)) edges[key] = list = [];
                list.Add((entry.Edge, entry));
            }
        }
        return outputs.Select((output, index) =>
        {
            (double X, double Y, double W, double H) rect;
            if (output.SubscreenUnitRect is { Size.Width: > 0 } unit)
                rect = (unit.Origin?.X ?? 0, unit.Origin?.Y ?? 0, unit.Size.Width, unit.Size.Height);
            else if (output.Bounds is { Size.Width: > 0 } bounds && union is { } size)
                rect = (((bounds.Origin?.X ?? 0) - originX) / size.Width, ((bounds.Origin?.Y ?? 0) - originY) / size.Height,
                    bounds.Size.Width / size.Width, bounds.Size.Height / size.Height);
            else
                rect = (0, 0, 1, 1);
            var slice = new ProScreenPlan.Slice
            {
                ProUuid = output.Uuid.Lower(),
                Name = output.Name.Length == 0 ? $"Output {index + 1}" : output.Name,
                X = rect.X, Y = rect.Y, Width = rect.W, Height = rect.H,
                Correction = OutputCorrection(output, screenName, warnings),
            };
            var sliceWidthPixels = Math.Max(rect.W * canvasWidth, 1);
            var sliceHeightPixels = Math.Max(rect.H * canvasHeight, 1);
            foreach (var (edge, entry) in edges.GetValueOrDefault(slice.ProUuid) ?? [])
            {
                var axisPixels = edge is RV.EdgeBlend.Types.Screen.Types.Edge.Left or RV.EdgeBlend.Types.Screen.Types.Edge.Right
                    ? sliceWidthPixels : sliceHeightPixels;
                var width = entry.Radius > 1 ? entry.Radius / axisPixels : entry.Radius;
                if (!(width > 0)) continue;
                double modeExponent = entry.Mode switch
                {
                    RV.EdgeBlend.Types.Mode.Linear => 1,
                    RV.EdgeBlend.Types.Mode.Quadratic => 2,
                    RV.EdgeBlend.Types.Mode.Cubic => 3,
                    _ => 2.2,
                };
                var curve = entry.Gamma > 0 ? entry.Gamma : modeExponent;
                double? intensity = entry.Intensity is > 0 and < 1 ? entry.Intensity : null;
                var compensation = output.BlendCompensation?.BlackLevel ?? 0;
                var lift = Math.Max(Math.Clamp(entry.BlackLevel, 0, 1), Math.Clamp(compensation, 0, 1));
                var plan = new ProScreenPlan.EdgeBlendPlan(Math.Clamp(width, 0, 1), curve, intensity, lift > 0 ? lift : null);
                switch (edge)
                {
                    case RV.EdgeBlend.Types.Screen.Types.Edge.Left: slice.BlendLeft = plan; break;
                    case RV.EdgeBlend.Types.Screen.Types.Edge.Right: slice.BlendRight = plan; break;
                    case RV.EdgeBlend.Types.Screen.Types.Edge.Top: slice.BlendTop = plan; break;
                    case RV.EdgeBlend.Types.Screen.Types.Edge.Bottom: slice.BlendBottom = plan; break;
                }
            }
            return slice;
        }).ToList();
    }

    private static (int Width, int Height)? SpanCanvasSize(IList<RV.Screen> outputs)
    {
        var rects = outputs.Where(o => o.Bounds is { Size.Width: > 0 }).Select(o => o.Bounds).ToList();
        if (rects.Count == 0) return null;
        var minX = rects.Min(r => r.Origin?.X ?? 0);
        var minY = rects.Min(r => r.Origin?.Y ?? 0);
        var maxX = rects.Max(r => (r.Origin?.X ?? 0) + r.Size.Width);
        var maxY = rects.Max(r => (r.Origin?.Y ?? 0) + r.Size.Height);
        return ((int)(maxX - minX), (int)(maxY - minY));
    }

    private static ProScreenPlan.OutputCorrection? OutputCorrection(RV.Screen output, string screenName, List<string> warnings)
    {
        var correction = new ProScreenPlan.OutputCorrection();
        if (output.CornerPinningEnabled && output.CornerValues is { } corners)
        {
            static ProScreenPlan.OutputCorrection.Offset? Offset(RV.Graphics.Types.Point? point) =>
                point is null || (point.X == 0 && point.Y == 0) ? null : new(point.X, point.Y);
            correction.TopLeft = Offset(corners.TopLeft);
            correction.TopRight = Offset(corners.TopRight);
            correction.BottomLeft = Offset(corners.BottomLeft);
            correction.BottomRight = Offset(corners.BottomRight);
        }
        if (output.ColorEnabled && output.ColorAdjustment is { } adjustment)
        {
            correction.Brightness = adjustment.Brightness;
            correction.Contrast = adjustment.Contrast;
            correction.Gamma = adjustment.Gamma;
            correction.BlackLevel = adjustment.BlackLevel;
            correction.RedLevel = adjustment.RedLevel;
            correction.GreenLevel = adjustment.GreenLevel;
            correction.BlueLevel = adjustment.BlueLevel;
        }
        correction.Gamma += output.Gamma;
        correction.BlackLevel += output.BlackLevel;
        correction.RotationDegrees = output.Rotation;
        return correction.IsNeutral ? null : correction;
    }

    public static (List<ProLookPlan> Looks, string? LiveLookUuid, List<string> Warnings) MapLooks(RV.ProPresenterWorkspace workspace)
    {
        var warnings = new List<string>();
        var looks = workspace.AudienceLooks.Select(look => new ProLookPlan(
            look.Uuid.Lower(),
            look.Name.Length == 0 ? "Look" : look.Name,
            look.ScreenLooks.Select(screenLook =>
            {
                var layers = new List<string>();
                if (screenLook.LiveVideoEnabled) layers.Add("videoInput");
                if (screenLook.PresentationBackgroundEnabled) layers.Add("loopingVideos");
                if (screenLook.PresentationForegroundEnabled) layers.AddRange(["slide", "videos", "stillGraphics"]);
                if (screenLook.PropsLayerEnabled || screenLook.PropsEnabled) layers.Add("overlays");
                if (screenLook.MessagesLayerEnabled) layers.Add("alerts");
                if (screenLook.AnnouncementsEnabled)
                    warnings.Add($"look \"{look.Name}\": the announcements layer has no MxU room layer and was dropped");
                return new ProLookPlan.ScreenLook
                {
                    ProScreenUuid = screenLook.ProScreenUuid.Lower(),
                    EnabledLayers = layers,
                    SlideThemeName = screenLook.TemplateDocumentFilePath is { } path ? TemplateThemeName(path) : null,
                    MaskProUuid = screenLook.MaskUuid is { } mask && mask.String.Length > 0 ? mask.Lower() : null,
                };
            }).ToList())).ToList();
        string? liveUuid = null;
        if (workspace.LiveAudienceLook is { } live)
        {
            var original = live.OriginalLookUuid.Lower();
            liveUuid = original.Length == 0 ? live.Uuid.Lower() : original;
        }
        return (looks, liveUuid, warnings);
    }

    internal static string? TemplateThemeName(RV.URL url)
    {
        string? path = null;
        if (url is { StorageCase: RV.URL.StorageOneofCase.AbsoluteString, AbsoluteString.Length: > 0 })
            path = SwiftCompat.UrlPath(url.AbsoluteString) ?? url.AbsoluteString;
        if (path is null && url is { RelativeFilePathCase: RV.URL.RelativeFilePathOneofCase.Local } && url.Local.Path.Length > 0)
            path = url.Local.Path;
        if (path is null && url is { StorageCase: RV.URL.StorageOneofCase.RelativePath, RelativePath.Length: > 0 })
            path = url.RelativePath;
        if (string.IsNullOrEmpty(path)) return null;
        var components = path.Split('/', '\\').Where(c => c.Length > 0).ToList();
        if (components.Count == 0) return null;
        return components[^1] == "Theme" && components.Count >= 2 ? components[^2] : components[^1];
    }

    private sealed class CommunicationDevice
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("reconnect")] public bool? Reconnect { get; set; }
        [JsonPropertyName("parser")] public string? Parser { get; set; }
    }

    public static (List<MIDIDevice> Devices, List<string> Warnings) MapCommunicationDevices(byte[] data)
    {
        List<CommunicationDevice>? entries;
        try
        {
            entries = JsonSerializer.Deserialize<List<CommunicationDevice>>(data);
        }
        catch (JsonException)
        {
            entries = null;
        }
        if (entries is null) return ([], ["CommunicationDevices was not readable and was skipped"]);

        var devices = new List<MIDIDevice>();
        var warnings = new List<string>();
        foreach (var entry in entries)
        {
            var name = string.IsNullOrEmpty(entry.Name) ? "MIDI Device" : entry.Name;
            if (DecodeParser(entry.Parser) is not { } xml)
            {
                warnings.Add($"device \"{name}\": its parser payload was not readable and was skipped");
                continue;
            }
            if (!xml.Contains("RVProtocolParserMIDI", StringComparison.Ordinal))
            {
                warnings.Add($"device \"{name}\": {ProtocolName(xml)} devices have no MxU equivalent yet");
                continue;
            }
            var sources = EndpointNames("sources", xml);
            var destinations = EndpointNames("destinations", xml);
            devices.Add(new MIDIDevice
            {
                Id = entry.Id?.ToLowerInvariant() ?? SwiftCompat.NewId(),
                Name = name,
                SourceNames = sources.Count == 0 ? null : sources,
                DestinationNames = destinations.Count == 0 ? null : destinations,
                Direction = (sources.Count > 0, destinations.Count > 0) switch
                {
                    (true, false) => MIDIDeviceItemDirection.Input,
                    (false, true) => MIDIDeviceItemDirection.Output,
                    _ => null,
                },
                AutoReconnect = entry.Reconnect == true ? true : null,
            });
        }
        return (devices, warnings);
    }

    /// <summary>Foundation's <c>String(data:encoding: .utf16)</c>: honours a byte-order mark, else big-endian; fails on bad data.</summary>
    private static string? DecodeParser(string? base64)
    {
        if (base64 is null) return null;
        try
        {
            var bytes = Convert.FromBase64String(base64);
            var bigEndian = true;
            var start = 0;
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) (bigEndian, start) = (false, 2);
            else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) start = 2;
            return new UnicodeEncoding(bigEndian, byteOrderMark: false, throwOnInvalidBytes: true).GetString(bytes, start, bytes.Length - start);
        }
        catch (Exception error) when (error is FormatException or ArgumentException)
        {
            return null;
        }
    }

    [GeneratedRegex("RVProtocolParser([A-Za-z0-9]+)")]
    private static partial Regex ProtocolParser();

    private static string ProtocolName(string xml) =>
        ProtocolParser().Match(xml) is { Success: true } match ? match.Groups[1].Value : "this protocol's";

    private static List<string> EndpointNames(string attribute, string xml)
    {
        var match = Regex.Match(xml, Regex.Escape(attribute) + "=\"([^\"]*)\"");
        if (!match.Success) return [];
        return match.Groups[1].Value.Split(',').Select(s => s.TrimmingWhitespaces()).Where(s => s.Length > 0).ToList();
    }

    public static List<ProTimerPlan> MapTimers(RV.TimersDocument doc, IEnumerable<ProTimerSeed>? existing = null)
    {
        var existingIdsByName = new Dictionary<string, string>();
        foreach (var seed in existing ?? []) existingIdsByName.TryAdd(seed.Name.ToLowerInvariant(), seed.Id);
        return doc.Timers.Select(timer =>
        {
            if (TimerPlanMode(timer.Configuration) is not { } mode) return null;
            var name = timer.Name.Length == 0 ? "Timer" : timer.Name;
            return new ProTimerPlan(timer.Uuid.Lower(), existingIdsByName.GetValueOrDefault(name.ToLowerInvariant()) ?? SwiftCompat.NewId(), name, mode);
        }).OfType<ProTimerPlan>().ToList();
    }

    internal static ProTimerMode? TimerPlanMode(RV.Timer.Types.Configuration? configuration)
    {
        switch (configuration?.TimerTypeCase)
        {
            case RV.Timer.Types.Configuration.TimerTypeOneofCase.Countdown:
                return new ProTimerMode.Countdown(configuration.Countdown.Duration);
            case RV.Timer.Types.Configuration.TimerTypeOneofCase.CountdownToTime:
            {
                var toTime = configuration.CountdownToTime;
                var seconds = (int)toTime.TimeOfDay;
                var hour = seconds / 3600 % 24;
                if (toTime.Period == RV.Timer.Types.Configuration.Types.TimerTypeCountdownToTime.Types.TimePeriod.Pm && hour < 12) hour += 12;
                if (toTime.Period == RV.Timer.Types.Configuration.Types.TimerTypeCountdownToTime.Types.TimePeriod.Am && hour == 12) hour = 0;
                return new ProTimerMode.CountdownToTime(hour, seconds % 3600 / 60);
            }
            case RV.Timer.Types.Configuration.TimerTypeOneofCase.ElapsedTime:
            {
                var elapsed = configuration.ElapsedTime;
                return new ProTimerMode.CountUp(elapsed.HasEndTime ? Math.Max(0, elapsed.EndTime - elapsed.StartTime) : 0);
            }
            default:
                return null;
        }
    }
}
